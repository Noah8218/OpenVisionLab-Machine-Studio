namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum CameraAcquisitionParticipantOutcome
{
    Idle,
    Completed,
    Cancelled,
    Failed,
    TimedOut
}

internal sealed record CameraAcquisitionParticipantResult(
    CameraAcquisitionParticipantOutcome Outcome,
    ManualCameraTriggerResult? Trigger = null,
    Exception? Exception = null)
{
    internal bool IsTimedOut => Outcome == CameraAcquisitionParticipantOutcome.TimedOut;
}

/// <summary>
/// Owns the manual-camera preparation token and the current acquisition task.
/// It exposes observation for session close without taking ownership of camera
/// inspection policy or presentation.
/// </summary>
internal sealed class ManualCameraAcquisitionParticipant : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualCameraPreparationLifetime _preparationLifetime = new();
    private Task<ManualCameraTriggerResult>? _currentTask;
    private bool _disposed;

    internal CancellationToken Token => _preparationLifetime.Token;

    internal void Invalidate() => _preparationLifetime.Invalidate();

    internal Task<ManualCameraTriggerResult> TrackAsync(
        Func<CancellationToken, Task<ManualCameraTriggerResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentTask is { IsCompleted: false } currentTask)
            {
                return currentTask;
            }

            _currentTask = RunAsync(operation, _preparationLifetime.Token);
            return _currentTask;
        }
    }

    internal Task<CameraAcquisitionParticipantResult> ObserveAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task<ManualCameraTriggerResult>? task;
        lock (_gate)
        {
            if (_currentTask?.IsCompleted == true)
            {
                _currentTask = null;
            }

            task = _currentTask;
        }

        return task is null
            ? Task.FromResult(new CameraAcquisitionParticipantResult(
                CameraAcquisitionParticipantOutcome.Idle))
            : ObserveTaskAsync(task, timeout);
    }

    private static async Task<ManualCameraTriggerResult> RunAsync(
        Func<CancellationToken, Task<ManualCameraTriggerResult>> operation,
        CancellationToken cancellationToken) =>
        await operation(cancellationToken);

    private static async Task<CameraAcquisitionParticipantResult> ObserveTaskAsync(
        Task<ManualCameraTriggerResult> task,
        TimeSpan timeout)
    {
        try
        {
            var trigger = await task.WaitAsync(timeout);
            return new(CameraAcquisitionParticipantOutcome.Completed, trigger);
        }
        catch (OperationCanceledException exception)
        {
            return new(CameraAcquisitionParticipantOutcome.Cancelled, Exception: exception);
        }
        catch (TimeoutException exception)
        {
            return new(CameraAcquisitionParticipantOutcome.TimedOut, Exception: exception);
        }
        catch (Exception exception)
        {
            return new(CameraAcquisitionParticipantOutcome.Failed, Exception: exception);
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

        _preparationLifetime.Dispose();
    }
}
