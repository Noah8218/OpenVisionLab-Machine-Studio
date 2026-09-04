using System.Net;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns Machine Studio TCP listener and client transport lifetimes. It does
/// not own ViewModel state, localization, or setup persistence.
/// </summary>
internal sealed class MachineIntegrationTcpWorkflow : IAsyncDisposable
{
    private MachineIntegrationTcpExchange? _listener;
    private bool _disposed;

    public bool IsListening => _listener is not null;

    public async Task<IPEndPoint> StartListeningAsync(
        string exchangeRoot,
        IPAddress listenAddress,
        int listenPort,
        byte[] sharedKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(listenAddress);
        ArgumentNullException.ThrowIfNull(sharedKey);
        if (_listener is not null)
        {
            throw new InvalidOperationException(
                "The Machine Studio TCP listener is already started.");
        }

        MachineIntegrationTcpExchange? listener = null;
        try
        {
            listener = new MachineIntegrationTcpExchange(exchangeRoot, sharedKey);
            var endpoint = await listener.StartListeningAsync(
                    listenAddress,
                    listenPort,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MachineIntegrationTcpWorkflow));
            }

            _listener = listener;
            listener = null;
            return endpoint;
        }
        finally
        {
            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task StopListeningAsync()
    {
        ThrowIfDisposed();
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null)
        {
            return;
        }

        await listener.DisposeAsync().ConfigureAwait(false);
    }

    public Task<TcpIntegrationTransferReceipt> PingAsync(
        string exchangeRoot,
        byte[] sharedKey,
        TcpIntegrationEndpoint peer,
        CancellationToken cancellationToken = default) =>
        ExecuteClientAsync(
            exchangeRoot,
            sharedKey,
            (exchange, token) => exchange.PingAsync(peer, token),
            cancellationToken);

    public Task<TcpIntegrationTransferReceipt> PushTransactionAsync(
        string exchangeRoot,
        byte[] sharedKey,
        TcpIntegrationEndpoint peer,
        Guid transactionId,
        CancellationToken cancellationToken = default) =>
        ExecuteClientAsync(
            exchangeRoot,
            sharedKey,
            (exchange, token) => exchange.PushTransactionAsync(peer, transactionId, token),
            cancellationToken);

    public Task<TcpIntegrationTransferReceipt> PullTransactionAsync(
        string exchangeRoot,
        byte[] sharedKey,
        TcpIntegrationEndpoint peer,
        Guid transactionId,
        CancellationToken cancellationToken = default) =>
        ExecuteClientAsync(
            exchangeRoot,
            sharedKey,
            (exchange, token) => exchange.PullTransactionAsync(peer, transactionId, token),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<TcpIntegrationTransferReceipt> ExecuteClientAsync(
        string exchangeRoot,
        byte[] sharedKey,
        Func<MachineIntegrationTcpExchange, CancellationToken, Task<TcpIntegrationTransferReceipt>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sharedKey);
        ArgumentNullException.ThrowIfNull(operation);
        await using var exchange = new MachineIntegrationTcpExchange(exchangeRoot, sharedKey);
        return await operation(exchange, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
