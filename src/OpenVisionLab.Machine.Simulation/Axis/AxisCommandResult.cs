namespace OpenVisionLab.Machine.Simulation.Axis;

public enum AxisCommandErrorCode
{
    None,
    InvalidTarget,
    TargetOutOfRange,
    InvalidVelocity,
    VelocityOutOfRange,
    AxisBusy,
    AxisInterlocked
}

public sealed record AxisCommandResult(
    bool IsAccepted,
    AxisCommandErrorCode ErrorCode,
    double RequestedTarget)
{
    public static AxisCommandResult Accepted(double requestedTarget) =>
        new(true, AxisCommandErrorCode.None, requestedTarget);

    public static AxisCommandResult Rejected(
        AxisCommandErrorCode errorCode,
        double requestedTarget) =>
        new(false, errorCode, requestedTarget);
}
