namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns one cancellable asynchronous operation without owning its UI or domain state.
/// </summary>
internal sealed class AsyncOperationLifetime : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _currentTask;
    private bool _disposed;

    internal Task? CurrentTask
    {
        get
        {
            lock (_gate)
            {
                return _currentTask;
            }
        }
    }

    internal Task Start(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentTask is { IsCompleted: false })
            {
                return _currentTask;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _currentTask = RunAsync(cancellation, operation);
            return _currentTask;
        }
    }

    internal void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(
        CancellationTokenSource cancellation,
        Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(cancellation.Token);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Cancel();
    }
}
