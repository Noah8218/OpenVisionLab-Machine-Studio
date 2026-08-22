namespace OpenVisionLab.Machine.Simulation.Commands;

public enum SimulationCommandErrorCode
{
    None,
    RuntimeConfigurationInvalid,
    AxisNotFound,
    AxisTargetInvalid,
    AxisTargetOutOfRange,
    AxisVelocityInvalid,
    AxisVelocityOutOfRange,
    AxisBusy,
    AxisInterlocked,
    AxisGroupInvalid,
    CylinderNotFound,
    CylinderInterlocked,
    ConveyorNotFound,
    ConveyorCommandInvalid,
    DigitalSensorNotFound,
    DigitalSensorInterlocked,
    CameraNotFound,
    CameraTriggerRejected,
    ControlOwnerNotAllowed,
    SignalNotFound,
    SignalWriteRejected,
    FaultTargetNotFound,
    FaultParameterInvalid,
    FaultAlreadyActive,
    FaultNotActive,
    FaultApplicationRejected,
    ConditionScenarioInvalid,
    ConditionScenarioTargetNotFound,
    ConditionScenarioAlreadyActive,
    ConditionScenarioNotActive,
    SequenceNotFound,
    SequenceStartRejected,
    AutomaticRunNotConfigured,
    AutomaticRunStartRejected,
    InvalidRunMode,
    UnsupportedCommand,
    EngineNotStarted,
    EngineStopped
}

public sealed record SimulationCommandResult(
    string CommandId,
    bool IsAccepted,
    long AppliedTick,
    TimeSpan SimulationTime,
    SimulationCommandErrorCode ErrorCode,
    string? Detail)
{
    internal static SimulationCommandResult Accepted(
        SimulationCommand command,
        long appliedTick,
        TimeSpan simulationTime,
        string? detail = null) =>
        new(
            command.CommandId,
            true,
            appliedTick,
            simulationTime,
            SimulationCommandErrorCode.None,
            detail);

    internal static SimulationCommandResult Rejected(
        SimulationCommand command,
        long appliedTick,
        TimeSpan simulationTime,
        SimulationCommandErrorCode errorCode,
        string? detail = null) =>
        new(command.CommandId, false, appliedTick, simulationTime, errorCode, detail);
}
