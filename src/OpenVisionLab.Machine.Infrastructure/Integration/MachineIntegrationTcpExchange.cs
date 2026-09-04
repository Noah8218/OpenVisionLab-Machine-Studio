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
    private TcpIntegrationServer? _server;
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

    public IPEndPoint? LocalEndpoint => _server?.LocalEndpoint;

    public event Action<TcpIntegrationTransferReceipt>? RequestCompleted;

    public async Task<IPEndPoint> StartListeningAsync(
        IPAddress listenAddress,
        int port,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(listenAddress);
        if (_server is not null)
        {
            throw new InvalidOperationException(
                "The Machine TCP integration listener is already started.");
        }

        var server = new TcpIntegrationServer(
            IntegrationApplicationIds.MachineStudio,
            ExchangeRoot,
            listenAddress,
            port,
            _sharedKey,
            _options);
        server.RequestCompleted += OnRequestCompleted;
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            _server = server;
            return server.LocalEndpoint
                ?? throw new InvalidOperationException(
                    "The Machine TCP integration listener has no local endpoint.");
        }
        catch
        {
            server.RequestCompleted -= OnRequestCompleted;
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopListeningAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var server = _server;
        if (server is null)
        {
            return;
        }

        _server = null;
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
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopListeningAsync().ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_sharedKey);
            _disposed = true;
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

    private void OnRequestCompleted(TcpIntegrationTransferReceipt receipt) =>
        RequestCompleted?.Invoke(receipt);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
