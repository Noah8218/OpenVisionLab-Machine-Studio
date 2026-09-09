namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MachineIntegrationParticipantOutcome
{
    Idle,
    Completed,
    Cancelled,
    Failed,
    TimedOut
}

internal enum MachineIntegrationOperationKind
{
    None,
    PublishHandoff,
    RefreshResults,
    Tcp
}

internal sealed record MachineIntegrationOperationObservation(
    MachineIntegrationOperationKind Kind,
    MachineIntegrationParticipantOutcome Outcome,
    Exception? Exception = null)
{
    internal bool IsTimedOut => Outcome == MachineIntegrationParticipantOutcome.TimedOut;
}

internal sealed record MachineIntegrationParticipantResult(
    MachineIntegrationParticipantOutcome Outcome,
    MachineIntegrationOperationObservation? FileOperation = null,
    MachineIntegrationOperationObservation? TcpOperation = null,
    Exception? Exception = null)
{
    internal bool IsTimedOut => Outcome == MachineIntegrationParticipantOutcome.TimedOut
        || FileOperation?.IsTimedOut == true
        || TcpOperation?.IsTimedOut == true;
}

/// <summary>
/// Owns the non-cancellable file Publish/Refresh operation lifetime and the
/// close-admission boundary. Result projection and status presentation remain
/// with MachineIntegrationViewModel.
/// </summary>
internal sealed class MachineIntegrationParticipant : IDisposable
{
    private readonly object _gate = new();
    private Task<MachineIntegrationOperationObservation>? _currentTask;
    private bool _closeAdmissionRequested;
    private bool _disposed;

    internal event EventHandler? StateChanged;

    internal bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _currentTask is { IsCompleted: false };
            }
        }
    }

    internal bool CanStart
    {
        get
        {
            lock (_gate)
            {
                return !_disposed
                    && !_closeAdmissionRequested
                    && _currentTask is not { IsCompleted: false };
            }
        }
    }

    internal Task<MachineIntegrationOperationObservation> TrackAsync(
        MachineIntegrationOperationKind kind,
        Func<Task<MachineIntegrationOperationObservation>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        TaskCompletionSource<MachineIntegrationOperationObservation>? completion = null;
        lock (_gate)
        {
            if (_disposed || _closeAdmissionRequested)
            {
                return Task.FromResult(new MachineIntegrationOperationObservation(
                    kind,
                    MachineIntegrationParticipantOutcome.Idle));
            }

            if (_currentTask is { IsCompleted: false } currentTask)
            {
                return currentTask;
            }

            completion = new TaskCompletionSource<MachineIntegrationOperationObservation>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _currentTask = completion.Task;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = RunAsync(kind, operation, completion);
        return completion.Task;
    }

    internal void SetCloseAdmission(bool isRequested)
    {
        EventHandler? stateChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _closeAdmissionRequested = isRequested;
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke(this, EventArgs.Empty);
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
            if (_currentTask?.IsCompleted == true)
            {
                _currentTask = null;
            }

            task = _currentTask;
        }

        return task is null
            ? Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.None,
                MachineIntegrationParticipantOutcome.Idle))
            : ObserveTaskAsync(task, timeout);
    }

    private async Task RunAsync(
        MachineIntegrationOperationKind kind,
        Func<Task<MachineIntegrationOperationObservation>> operation,
        TaskCompletionSource<MachineIntegrationOperationObservation> completion)
    {
        try
        {
            var result = await operation().ConfigureAwait(true);
            completion.TrySetResult(result with { Kind = kind });
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetResult(new(kind, MachineIntegrationParticipantOutcome.Cancelled, exception));
        }
        catch (Exception exception)
        {
            completion.TrySetResult(new(kind, MachineIntegrationParticipantOutcome.Failed, exception));
        }
        finally
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
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
                MachineIntegrationOperationKind.None,
                MachineIntegrationParticipantOutcome.Cancelled,
                exception);
        }
        catch (TimeoutException exception)
        {
            return new(
                MachineIntegrationOperationKind.None,
                MachineIntegrationParticipantOutcome.TimedOut,
                exception);
        }
        catch (Exception exception)
        {
            return new(
                MachineIntegrationOperationKind.None,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
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
            _closeAdmissionRequested = true;
            StateChanged = null;
        }
    }
}
