namespace OpenVisionLab.Machine.Sequence.Runtime;

public enum SequenceContextErrorCode
{
    TargetNotFound,
    InvalidTargetKind,
    Rejected,
    Unavailable,
    Faulted
}

public sealed record SequenceContextError(SequenceContextErrorCode Code, string Message);

public readonly record struct SequenceContextOperationResult(bool IsSuccess, SequenceContextError? Error)
{
    public static SequenceContextOperationResult Success() => new(true, null);

    public static SequenceContextOperationResult Failure(SequenceContextErrorCode code, string message) =>
        new(false, new SequenceContextError(code, message));
}

public readonly record struct SequenceSignalReadResult(bool IsSuccess, bool Value, SequenceContextError? Error)
{
    public static SequenceSignalReadResult Success(bool value) => new(true, value, null);

    public static SequenceSignalReadResult Failure(SequenceContextErrorCode code, string message) =>
        new(false, false, new SequenceContextError(code, message));
}

public enum SequenceAxisMotionState
{
    Moving,
    Completed,
    Faulted
}

public readonly record struct SequenceAxisMotionReadResult(
    bool IsSuccess,
    SequenceAxisMotionState State,
    SequenceContextError? Error)
{
    public static SequenceAxisMotionReadResult Success(SequenceAxisMotionState state) => new(true, state, null);

    public static SequenceAxisMotionReadResult Failure(SequenceContextErrorCode code, string message) =>
        new(false, SequenceAxisMotionState.Faulted, new SequenceContextError(code, message));
}

public enum SequenceVisionResultState
{
    NotTriggered,
    Pending,
    Passed,
    Failed,
    Faulted
}

public readonly record struct SequenceVisionResultReadResult(
    bool IsSuccess,
    SequenceVisionResultState State,
    SequenceContextError? Error)
{
    public static SequenceVisionResultReadResult Success(SequenceVisionResultState state) =>
        new(true, state, null);

    public static SequenceVisionResultReadResult Failure(SequenceContextErrorCode code, string message) =>
        new(false, SequenceVisionResultState.Faulted, new SequenceContextError(code, message));
}

public readonly record struct SequenceCameraTriggerResult(
    bool IsSuccess,
    string? AcquisitionId,
    SequenceContextError? Error)
{
    public static SequenceCameraTriggerResult Success(string acquisitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionId);
        return new SequenceCameraTriggerResult(true, acquisitionId, null);
    }

    public static SequenceCameraTriggerResult Failure(SequenceContextErrorCode code, string message) =>
        new(false, null, new SequenceContextError(code, message));
}

public interface ISequenceRuntimeContext
{
    SequenceSignalReadResult ReadSignal(string signalId);

    SequenceContextOperationResult SetSignal(string signalId, bool value);

    SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition);

    SequenceAxisMotionReadResult ReadAxisMotionState(string axisId);

    SequenceCameraTriggerResult TriggerCamera(string cameraId, string recipeId) =>
        SequenceCameraTriggerResult.Failure(
            SequenceContextErrorCode.Unavailable,
            "Camera triggering is not available in this runtime context.");

    SequenceVisionResultReadResult ReadVisionResult(string cameraId, string acquisitionId) =>
        SequenceVisionResultReadResult.Failure(
            SequenceContextErrorCode.Unavailable,
            "Vision results are not available in this runtime context.");
}
