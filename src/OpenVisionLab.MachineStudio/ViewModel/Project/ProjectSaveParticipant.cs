namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum ProjectSaveAttemptOutcome
{
    Saved,
    Cancelled,
    Failed
}

internal sealed record ProjectSaveAttemptResult(
    ProjectSaveAttemptOutcome Outcome,
    ProjectSaveLifecycleResult? Save = null,
    Exception? Exception = null)
{
    internal bool IsSuccessful => Outcome == ProjectSaveAttemptOutcome.Saved;
}

internal enum ProjectSaveParticipantOutcome
{
    Idle,
    Completed,
    Failed,
    TimedOut
}

internal sealed record ProjectSaveParticipantResult(
    ProjectSaveParticipantOutcome Outcome,
    ProjectSaveLifecycleResult? Save = null,
    Exception? Exception = null)
{
    internal bool IsCompleted => Outcome == ProjectSaveParticipantOutcome.Completed;
    internal bool IsFailed => Outcome is
        ProjectSaveParticipantOutcome.Failed
        or ProjectSaveParticipantOutcome.TimedOut;
    internal bool IsTimedOut => Outcome == ProjectSaveParticipantOutcome.TimedOut;
}

/// <summary>
/// Tracks project-save work that is already admitted by the project lifecycle
/// coordinator. It never serializes or retries saves; the operation gate keeps
/// ordering, while this owner only exposes a typed observation boundary to
/// session close and diagnostics.
/// </summary>
internal sealed class ProjectSaveParticipant
{
    private readonly object _gate = new();
    private readonly HashSet<Task<ProjectSaveParticipantResult>> _inFlight = new();

    internal Task<ProjectSaveParticipantResult> ObserveAsync()
        => ObserveAsync(timeout: null);

    internal Task<ProjectSaveParticipantResult> ObserveAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return ObserveAsync((TimeSpan?)timeout);
    }

    private Task<ProjectSaveParticipantResult> ObserveAsync(TimeSpan? timeout)
    {
        Task<ProjectSaveParticipantResult>[] operations;
        lock (_gate)
        {
            operations = _inFlight.ToArray();
        }

        return operations.Length == 0
            ? Task.FromResult(new ProjectSaveParticipantResult(ProjectSaveParticipantOutcome.Idle))
            : ObserveOperationsAsync(operations, timeout);
    }

    internal Task<ProjectSaveLifecycleResult> TrackSaveAsync(
        Func<Task<ProjectSaveLifecycleResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return TrackAsync(
            operation,
            static save => new(ProjectSaveParticipantOutcome.Completed, save));
    }

    internal Task<ProjectSaveAttemptResult> TrackAttemptAsync(
        Func<Task<ProjectSaveAttemptResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return TrackAsync(
            operation,
            static attempt => attempt.Outcome == ProjectSaveAttemptOutcome.Failed
                ? new(
                    ProjectSaveParticipantOutcome.Failed,
                    attempt.Save,
                    attempt.Exception)
                : new(
                    ProjectSaveParticipantOutcome.Completed,
                    attempt.Save,
                    attempt.Exception));
    }

    private async Task<T> TrackAsync<T>(
        Func<Task<T>> operation,
        Func<T, ProjectSaveParticipantResult> createCompletedResult)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(createCompletedResult);
        var completion = new TaskCompletionSource<ProjectSaveParticipantResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _inFlight.Add(completion.Task);
        }

        var participantResult = new ProjectSaveParticipantResult(
            ProjectSaveParticipantOutcome.Failed);
        try
        {
            var result = await operation();
            participantResult = createCompletedResult(result);
            completion.SetResult(participantResult);
            return result;
        }
        catch (Exception exception)
        {
            participantResult = new(
                ProjectSaveParticipantOutcome.Failed,
                Exception: exception);
            completion.SetResult(participantResult);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(completion.Task);
            }
        }
    }

    private static async Task<ProjectSaveParticipantResult> ObserveOperationsAsync(
        Task<ProjectSaveParticipantResult>[] operations,
        TimeSpan? timeout)
    {
        var observation = Task.WhenAll(operations);
        try
        {
            var results = timeout is { } value
                ? await observation.WaitAsync(value)
                : await observation;
            return results.FirstOrDefault(result => result.IsFailed)
                ?? results[^1];
        }
        catch (TimeoutException exception)
        {
            return new(
                ProjectSaveParticipantOutcome.TimedOut,
                Exception: exception);
        }
    }
}
