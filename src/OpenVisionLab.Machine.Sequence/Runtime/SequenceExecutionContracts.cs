namespace OpenVisionLab.Machine.Sequence.Runtime;

public enum SequenceExecutionStatus
{
    Ready,
    Running,
    Completed,
    Faulted
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
    SequenceExecutionError? LastError);

public sealed record SequenceExecutionResult(
    SequenceExecutionSnapshot Snapshot,
    bool Transitioned,
    string? PreviousStepId,
    string? CurrentStepId,
    SequenceExecutionError? Error)
{
    public bool IsSuccess => Error is null;
}
