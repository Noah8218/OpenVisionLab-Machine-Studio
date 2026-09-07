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
    private readonly object _lifecycleGate = new();
    private MachineIntegrationTcpExchange? _listener;
    private bool _isStarting;
    private bool _disposed;

    public bool IsListening
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _listener is not null;
            }
        }
    }

    public async Task<IPEndPoint> StartListeningAsync(
        string exchangeRoot,
        IPAddress listenAddress,
        int listenPort,
        byte[] sharedKey,
        CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            ArgumentNullException.ThrowIfNull(listenAddress);
            ArgumentNullException.ThrowIfNull(sharedKey);
            if (_listener is not null || _isStarting)
            {
                throw new InvalidOperationException(
                    "The Machine Studio TCP listener is already started.");
            }

            _isStarting = true;
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
            lock (_lifecycleGate)
            {
                ThrowIfDisposedLocked();
                _listener = listener;
                listener = null;
                return endpoint;
            }
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _isStarting = false;
            }

            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task StopListeningAsync()
    {
        MachineIntegrationTcpExchange? listener;
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            if (_isStarting)
            {
                throw new InvalidOperationException(
                    "The Machine Studio TCP listener is still starting.");
            }

            listener = _listener;
            _listener = null;
        }

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
        MachineIntegrationTcpExchange? listener;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            listener = _listener;
            _listener = null;
        }

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

    private void ThrowIfDisposed()
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);
}
