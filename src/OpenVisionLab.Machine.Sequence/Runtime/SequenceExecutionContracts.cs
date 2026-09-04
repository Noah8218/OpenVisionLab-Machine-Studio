namespace OpenVisionLab.Machine.Sequence.Runtime;

public enum SequenceExecutionStatus
{
    Ready,
    Running,
    Completed,
    Faulted,
    Aborted
}

public enum SequenceExecutionErrorCode
{
    InvalidState,
    InvalidElapsedTime,
    InvalidProgram,
    SignalReadFailed,
    SignalWriteFailed,
    AxisMoveFailed,
    AxisStateReadFailed,
    AxisFaulted,
    StepTimedOut,
    SequenceWatchdogTimedOut,
    SubsequenceDepthExceeded,
    CameraTriggerFailed,
    VisionResultNotTriggered,
    VisionResultReadFailed,
    VisionResultFaulted,
    VisionResultTimedOut
}

public sealed record SequenceExecutionError(
    SequenceExecutionErrorCode Code,
    string SequenceId,
    string? StepId,
    string Message,
    SequenceContextError? ContextError = null);

public sealed record SequenceExecutionSnapshot(
    string SequenceId,
    SequenceExecutionStatus Status,
    string? CurrentStepId,
    int CurrentStepIndex,
    TimeSpan ElapsedInStep,
    TimeSpan TotalElapsed,
    long TickCount,
    SequenceExecutionError? LastError,
    TimeSpan WatchdogTimeout,
    string? ActiveSequenceId = null,
    IReadOnlyList<string>? CallStack = null);

public sealed record SequenceExecutionResult(
    SequenceExecutionSnapshot Snapshot,
    bool Transitioned,
    string? PreviousStepId,
    string? CurrentStepId,
    SequenceExecutionError? Error,
    string? PreviousSequenceId = null,
    string? CurrentSequenceId = null)
{
    public bool IsSuccess => Error is null;
}
