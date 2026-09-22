using System.Net;
using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Composes the shared authenticated TCP transport with Machine Studio's
/// existing local transaction store. TCP receipt only materializes immutable
/// files; Machine acknowledgement, consumer execution, and result projection
/// remain explicit actions owned by their existing adapters.
/// </summary>
public sealed class MachineIntegrationTcpExchange : IAsyncDisposable
{
    private readonly byte[] _sharedKey;
    private readonly TcpIntegrationOptions _options;
    private readonly object _lifecycleGate = new();
    private TcpIntegrationServer? _server;
    private bool _isStarting;
    private bool _zeroSharedKeyWhenStartCompletes;
    private bool _disposed;

    public MachineIntegrationTcpExchange(
        string exchangeRoot,
        ReadOnlySpan<byte> sharedKey,
        TcpIntegrationOptions? options = null)
    {
        ExchangeRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(exchangeRoot)
                ? throw new ArgumentException(
                    "A local Machine integration exchange root is required.",
                    nameof(exchangeRoot))
                : exchangeRoot.Trim());
        if (sharedKey.Length < 32)
        {
            throw new ArgumentException(
                "The TCP integration shared key must contain at least 32 bytes.",
                nameof(sharedKey));
        }

        _sharedKey = sharedKey.ToArray();
        _options = options ?? new TcpIntegrationOptions();
    }

    public string ExchangeRoot { get; }

    public IPEndPoint? LocalEndpoint
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _server?.LocalEndpoint;
            }
        }
    }

    public event Action<TcpIntegrationTransferReceipt>? RequestCompleted;

    public async Task<IPEndPoint> StartListeningAsync(
        IPAddress listenAddress,
        int port,
        CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            ArgumentNullException.ThrowIfNull(listenAddress);
            if (_server is not null || _isStarting)
            {
                throw new InvalidOperationException(
                    "The Machine TCP integration listener is already started.");
            }

            _isStarting = true;
        }

        TcpIntegrationServer? server = null;
        try
        {
            server = new TcpIntegrationServer(
                IntegrationApplicationIds.MachineStudio,
                ExchangeRoot,
                listenAddress,
                port,
                _sharedKey,
                _options);
            server.RequestCompleted += OnRequestCompleted;
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            var endpoint = server.LocalEndpoint
                ?? throw new InvalidOperationException(
                    "The Machine TCP integration listener has no local endpoint.");
            lock (_lifecycleGate)
            {
                ThrowIfDisposedLocked();
                _server = server;
                server = null;
                return endpoint;
            }
        }
        catch
        {
            if (server is not null)
            {
                server.RequestCompleted -= OnRequestCompleted;
                await server.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            bool zeroSharedKey;
            lock (_lifecycleGate)
            {
                _isStarting = false;
                zeroSharedKey = _zeroSharedKeyWhenStartCompletes;
                _zeroSharedKeyWhenStartCompletes = false;
            }

            if (zeroSharedKey)
            {
                CryptographicOperations.ZeroMemory(_sharedKey);
            }
        }
    }

    public async Task StopListeningAsync(CancellationToken cancellationToken = default)
    {
        TcpIntegrationServer? server;
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            if (_isStarting)
            {
                throw new InvalidOperationException(
                    "The Machine TCP integration listener is still starting.");
            }

            server = _server;
            _server = null;
        }

        if (server is null)
        {
            return;
        }

        server.RequestCompleted -= OnRequestCompleted;
        try
        {
            await server.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<TcpIntegrationTransferReceipt> PingAsync(
        TcpIntegrationEndpoint peer,
        CancellationToken cancellationToken = default) =>
        ExecuteClientAsync(
            peer,
            (client, token) => client.PingAsync(token),
            cancellationToken);

    public Task<TcpIntegrationTransferReceipt> PushTransactionAsync(
        TcpIntegrationEndpoint peer,
        Guid transactionId,
        CancellationToken cancellationToken = default) =>
        ExecuteClientAsync(
            peer,
            (client, token) => client.PushTransactionAsync(
                ExchangeRoot,
                transactionId,
                token),
            cancellationToken);

    public Task<TcpIntegrationTransferReceipt> PullTransactionAsync(
        TcpIntegrationEndpoint peer,
        Guid transactionId,
        CancellationToken cancellationToken = default) =>
        ExecuteClientAsync(
            peer,
            (client, token) => client.PullTransactionAsync(
                ExchangeRoot,
                transactionId,
                token),
            cancellationToken);

    public Task<TcpIntegrationCancellationReceipt> CancelTransactionAsync(
        TcpIntegrationEndpoint peer,
        IntegrationCancelRequestV2 request,
        CancellationToken cancellationToken = default) =>
        ExecuteCancellationClientAsync(
            peer,
            request,
            cancellationToken);

    public IReadOnlyList<MachineIntegrationTransactionSummary> DiscoverTransactions()
    {
        ThrowIfDisposed();
        return MachineIntegrationExchange.DiscoverTransactions(ExchangeRoot);
    }

    public IntegrationHandoffV2 ReadHandoff(Guid transactionId)
    {
        ThrowIfDisposed();
        return MachineIntegrationExchange.ReadHandoff(ExchangeRoot, transactionId);
    }

    public IntegrationHandoffV2 ReadHandoffEnvelope(Guid transactionId)
    {
        ThrowIfDisposed();
        return MachineIntegrationExchange.ReadHandoffEnvelope(ExchangeRoot, transactionId);
    }

    public IntegrationAcknowledgementV2 ReadAcknowledgement(Guid transactionId)
    {
        ThrowIfDisposed();
        return MachineIntegrationExchange.ReadAcknowledgement(ExchangeRoot, transactionId);
    }

    public IntegrationResultV2 ReadResult(Guid transactionId)
    {
        ThrowIfDisposed();
        return MachineIntegrationExchange.ReadResult(ExchangeRoot, transactionId);
    }

    public async ValueTask DisposeAsync()
    {
        TcpIntegrationServer? server;
        bool zeroSharedKey;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            server = _server;
            _server = null;
            zeroSharedKey = !_isStarting;
            _zeroSharedKeyWhenStartCompletes = !zeroSharedKey;
        }

        try
        {
            if (server is not null)
            {
                server.RequestCompleted -= OnRequestCompleted;
                try
                {
                    await server.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (zeroSharedKey)
            {
                CryptographicOperations.ZeroMemory(_sharedKey);
            }
        }
    }

    private async Task<TcpIntegrationTransferReceipt> ExecuteClientAsync(
        TcpIntegrationEndpoint peer,
        Func<
            TcpIntegrationClient,
            CancellationToken,
            Task<TcpIntegrationTransferReceipt>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(operation);
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            peer,
            _sharedKey,
            _options);
        return await operation(client, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TcpIntegrationCancellationReceipt> ExecuteCancellationClientAsync(
        TcpIntegrationEndpoint peer,
        IntegrationCancelRequestV2 request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            peer,
            _sharedKey,
            _options);
        return await client.CancelTransactionAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private void OnRequestCompleted(TcpIntegrationTransferReceipt receipt)
    {
        lock (_lifecycleGate)
        {
            if (!_disposed)
            {
                RequestCompleted?.Invoke(receipt);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);
}
