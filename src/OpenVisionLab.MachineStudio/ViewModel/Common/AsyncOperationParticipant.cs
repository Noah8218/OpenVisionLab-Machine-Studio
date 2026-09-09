namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the shared lifetime mechanics for one cancellable typed operation.
/// Domain participants provide their own idle and observation result values.
/// </summary>
internal sealed class AsyncOperationParticipant<TResult> : IDisposable
{
    private readonly object _gate = new();
    private readonly AsyncOperationLifetime _lifetime = new();
    private readonly Func<TResult> _createIdleResult;
    private readonly Func<Exception, TResult> _createCancelledResult;
    private readonly Func<Exception, TResult> _createTimedOutResult;
    private readonly Func<Exception, TResult> _createFailedResult;
    private Task<TResult>? _currentTask;
    private bool _cancellationRequested;
    private bool _disposed;

    internal AsyncOperationParticipant(
        Func<TResult> createIdleResult,
        Func<Exception, TResult> createCancelledResult,
        Func<Exception, TResult> createTimedOutResult,
        Func<Exception, TResult> createFailedResult)
    {
        _createIdleResult = createIdleResult ?? throw new ArgumentNullException(nameof(createIdleResult));
        _createCancelledResult = createCancelledResult
            ?? throw new ArgumentNullException(nameof(createCancelledResult));
        _createTimedOutResult = createTimedOutResult
            ?? throw new ArgumentNullException(nameof(createTimedOutResult));
        _createFailedResult = createFailedResult
            ?? throw new ArgumentNullException(nameof(createFailedResult));
    }

    internal Task? CurrentTask => _lifetime.CurrentTask;

    internal Task<TResult> TrackAsync(Func<CancellationToken, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentTask is { IsCompleted: false } currentTask)
            {
                return currentTask;
            }

            _currentTask = _lifetime.Start(operation);
            _cancellationRequested = false;
            return _currentTask;
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            if (_currentTask is { IsCompleted: false })
            {
                // Keep the request visible until the next observation. The
                // operation can finish synchronously during Cancel(), before
                // the close workflow gets a chance to capture its task.
                _cancellationRequested = true;
            }
        }

        _lifetime.Cancel();
    }

    internal Task<TResult> ObserveAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task<TResult>? task;
        lock (_gate)
        {
            task = _currentTask;
            var observeCompletedCancellation = _cancellationRequested
                && task?.IsCanceled == true;
            _cancellationRequested = false;

            if (task?.IsCompleted == true)
            {
                _currentTask = null;
            }

            if (task?.IsCompleted == true && !observeCompletedCancellation)
            {
                task = null;
            }
        }

        return task is null
            ? Task.FromResult(_createIdleResult())
            : ObserveTaskAsync(task, timeout);
    }

    private async Task<TResult> ObserveTaskAsync(Task<TResult> task, TimeSpan timeout)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (OperationCanceledException exception)
        {
            return _createCancelledResult(exception);
        }
        catch (TimeoutException exception)
        {
            return _createTimedOutResult(exception);
        }
        catch (Exception exception)
        {
            return _createFailedResult(exception);
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

        _lifetime.Dispose();
    }
}
