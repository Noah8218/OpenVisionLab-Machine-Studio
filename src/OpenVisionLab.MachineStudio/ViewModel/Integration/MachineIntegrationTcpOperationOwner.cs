namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns one TCP operation's admission, cancellation, observation and disposal
/// lifetime. It is deliberately WPF-neutral: the TCP control ViewModel supplies
/// only busy and status callbacks for presentation updates.
/// </summary>
internal sealed class MachineIntegrationTcpOperationOwner : IDisposable
{
    private readonly Action<bool> _setBusy;
    private readonly Action<string> _setStatus;
    private readonly Func<string> _createCancelledStatus;
    private readonly object _gate = new();
    private CancellationTokenSource? _operationCancellation;
    private Task<MachineIntegrationOperationObservation>? _operationTask;
    private int _operationActive;
    private bool _closeAdmissionRequested;
    private bool _disposed;

    internal MachineIntegrationTcpOperationOwner(
        Action<bool> setBusy,
        Action<string> setStatus,
        Func<string> createCancelledStatus)
    {
        _setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _createCancelledStatus = createCancelledStatus
            ?? throw new ArgumentNullException(nameof(createCancelledStatus));
    }

    internal bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _operationTask is { IsCompleted: false };
            }
        }
    }

    internal bool IsCloseAdmissionRequested
    {
        get
        {
            lock (_gate)
            {
                return _closeAdmissionRequested;
            }
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    internal Task<MachineIntegrationOperationObservation> TrackAsync(
        string busyStatus,
        Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            return CreateIdleTask();
        }

        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<MachineIntegrationOperationObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || _closeAdmissionRequested)
            {
                Interlocked.Exchange(ref _operationActive, 0);
                cancellation.Dispose();
                completion.SetResult(CreateIdleObservation());
                return completion.Task;
            }

            _operationCancellation = cancellation;
            _operationTask = completion.Task;
            _setBusy(true);
            _setStatus(busyStatus);
        }

        _ = ExecuteAsync(operation, cancellation, completion);
        return completion.Task;
    }

    internal void SetCloseAdmission(bool isRequested)
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _closeAdmissionRequested = isRequested;
            if (isRequested)
            {
                cancellation = _operationCancellation;
            }
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal Task<MachineIntegrationOperationObservation> ObserveAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task<MachineIntegrationOperationObservation>? task;
        lock (_gate)
        {
            if (_operationTask?.IsCompleted == true)
            {
                _operationTask = null;
            }

            task = _operationTask;
        }

        return task is null ? Task.FromResult(CreateIdleObservation()) : ObserveTaskAsync(task, timeout);
    }

    internal bool TryPublishIfActive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            action();
            return true;
        }
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation,
        TaskCompletionSource<MachineIntegrationOperationObservation> completion)
    {
        var result = new MachineIntegrationOperationObservation(
            MachineIntegrationOperationKind.Tcp,
            MachineIntegrationParticipantOutcome.Failed);
        try
        {
            await operation(cancellation.Token).ConfigureAwait(true);
            result = new(
                MachineIntegrationOperationKind.Tcp,
                MachineIntegrationParticipantOutcome.Completed);
        }
        catch (OperationCanceledException exception)
        {
            TryPublishIfActive(() => _setStatus(_createCancelledStatus()));
            result = new(
                MachineIntegrationOperationKind.Tcp,
                MachineIntegrationParticipantOutcome.Cancelled,
                exception);
        }
        catch (Exception exception)
        {
            TryPublishIfActive(() => _setStatus(exception.Message));
            result = new(
                MachineIntegrationOperationKind.Tcp,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operationCancellation, cancellation))
                {
                    _operationCancellation = null;
                }

                if (ReferenceEquals(_operationTask, completion.Task))
                {
                    _operationTask = null;
                }
            }

            TryPublishIfActive(() => _setBusy(false));
            Interlocked.Exchange(ref _operationActive, 0);
            cancellation.Dispose();
            completion.TrySetResult(result);
        }
    }

    private static async Task<MachineIntegrationOperationObservation> ObserveTaskAsync(
        Task<MachineIntegrationOperationObservation> task,
        TimeSpan timeout)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (OperationCanceledException exception)
        {
            return new(
                MachineIntegrationOperationKind.Tcp,
                MachineIntegrationParticipantOutcome.Cancelled,
                exception);
        }
        catch (TimeoutException exception)
        {
            return new(
                MachineIntegrationOperationKind.Tcp,
                MachineIntegrationParticipantOutcome.TimedOut,
                exception);
        }
        catch (Exception exception)
        {
            return new(
                MachineIntegrationOperationKind.Tcp,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
        }
    }

    private static MachineIntegrationOperationObservation CreateIdleObservation() => new(
        MachineIntegrationOperationKind.Tcp,
        MachineIntegrationParticipantOutcome.Idle);

    private static Task<MachineIntegrationOperationObservation> CreateIdleTask() =>
        Task.FromResult(CreateIdleObservation());

    internal bool TryDispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            cancellation = _operationCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    public void Dispose() => TryDispose();
}
