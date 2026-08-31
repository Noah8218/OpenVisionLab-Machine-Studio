using System.Net;
using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Adds the shared authenticated TCP transport to Machine Studio's existing
/// transaction directory. Transport only copies immutable transaction files;
/// acknowledgement, inspection, and result refresh remain explicit actions.
/// </summary>
public sealed class MachineIntegrationTcpExchange : IAsyncDisposable
{
    private readonly byte[] sharedKey;
    private readonly TcpIntegrationOptions options;
    private TcpIntegrationServer? server;
    private bool disposed;

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

        this.sharedKey = sharedKey.ToArray();
        this.options = options ?? new TcpIntegrationOptions();
    }

    public string ExchangeRoot { get; }

    public IPEndPoint? LocalEndpoint => server?.LocalEndpoint;

    public event Action<TcpIntegrationTransferReceipt>? RequestCompleted;

    public async Task<IPEndPoint> StartListeningAsync(
        IPAddress listenAddress,
        int port,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(listenAddress);
        if (server is not null)
        {
            throw new InvalidOperationException(
                "The Machine TCP integration listener is already started.");
        }

        var candidate = new TcpIntegrationServer(
            IntegrationApplicationIds.MachineStudio,
            ExchangeRoot,
            listenAddress,
            port,
            sharedKey,
            options);
        candidate.RequestCompleted += OnRequestCompleted;
        try
        {
            await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
            server = candidate;
            return candidate.LocalEndpoint
                ?? throw new InvalidOperationException(
                    "The Machine TCP integration listener has no local endpoint.");
        }
        catch
        {
            candidate.RequestCompleted -= OnRequestCompleted;
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopListeningAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var active = server;
        if (active is null)
        {
            return;
        }

        server = null;
        active.RequestCompleted -= OnRequestCompleted;
        try
        {
            await active.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await active.DisposeAsync().ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await StopListeningAsync().ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedKey);
            disposed = true;
        }
    }

    private async Task<TcpIntegrationTransferReceipt> ExecuteClientAsync(
        TcpIntegrationEndpoint peer,
        Func<TcpIntegrationClient, CancellationToken, Task<TcpIntegrationTransferReceipt>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(operation);
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            peer,
            sharedKey,
            options);
        return await operation(client, cancellationToken).ConfigureAwait(false);
    }

    private void OnRequestCompleted(TcpIntegrationTransferReceipt receipt) =>
        RequestCompleted?.Invoke(receipt);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
