using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationManualControlContext(
    SimulationRunMode RunMode,
    SimulationControlOwner ControlOwner,
    bool AutomaticRunActive,
    IReadOnlyList<ServoAxisComponent> Axes,
    IReadOnlyList<DeterministicVirtualCamera> Cameras,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    DeterministicSignalHub SignalHub,
    DeterministicMachineLayout? MachineLayout,
    IReadOnlyDictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime,
    Func<bool, string> FormatSignal);

internal sealed record SimulationManualControlEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationManualControlOutcome(
    SimulationCommandResult Result,
    SimulationRunMode? RunMode = null,
    SimulationControlOwner? ControlOwner = null,
    int? PendingSteps = null,
    IReadOnlyList<SimulationManualControlEvent>? Events = null);

internal sealed class SimulationManualControlCommandHandler
{
    private readonly SimulationManualAxisCommandHandler _axisCommandHandler = new();
    private readonly SimulationManualCameraCommandHandler _cameraCommandHandler = new();
    private readonly SimulationManualEquipmentCommandHandler _equipmentCommandHandler = new();
    private readonly SimulationManualInputCommandHandler _inputCommandHandler = new();

    public SimulationManualControlOutcome Apply(
        SimulationCommand command,
        SimulationManualControlContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            StartManualControlCommand => ApplyStartManualControl(command, context),
            MoveAbsoluteCommand
                or MoveAxesAbsoluteCommand
                or MoveRelativeCommand
                or MoveVelocityCommand
                or HomeAxisCommand
                or JogAxisCommand
                or StopAxisCommand
                or StopAxesCommand => _axisCommandHandler.Apply(command, context),
            TriggerVirtualCameraCommand => _cameraCommandHandler.Apply(command, context),
            SetCylinderCommand
                or SetConveyorCommand => _equipmentCommandHandler.Apply(command, context),
            SetVirtualInputCommand
                or SetVirtualInputForceCommand
                or SetDigitalSensorForceCommand => _inputCommandHandler.Apply(command, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationManualControlOutcome ApplyStartManualControl(
        SimulationCommand command,
        SimulationManualControlContext context)
    {
        if (context.RunMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.InvalidRunMode,
                "Manual control can start only while the simulation is paused.");
        }

        if (context.AutomaticRunActive || context.SequenceExecutors.Values.Any(executor =>
                executor.CaptureSnapshot().Status == SequenceExecutionStatus.Running))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                "Reset the active automatic or sequence run before starting manual control.");
        }

        return Accept(
            command,
            context,
            "Manual commissioning control started.",
            new SimulationManualControlEvent(
                "Motion",
                "ManualControlStarted",
                "Manual commissioning control entered RealTime mode."),
            runMode: SimulationRunMode.RealTime,
            controlOwner: SimulationControlOwner.Manual,
            pendingSteps: 0);
    }

    internal static SimulationManualControlOutcome Accept(
        SimulationCommand command,
        SimulationManualControlContext context,
        string detail,
        SimulationManualControlEvent? operationEvent = null,
        SimulationRunMode? runMode = null,
        SimulationControlOwner? controlOwner = null,
        int? pendingSteps = null) =>
        new(
            SimulationCommandResult.Accepted(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                detail),
            runMode,
            controlOwner,
            pendingSteps,
            operationEvent is null ? null : new[] { operationEvent });

    internal static SimulationManualControlOutcome Reject(
        SimulationCommand command,
        SimulationManualControlContext context,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        new(
            SimulationCommandResult.Rejected(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                errorCode,
                detail));
}
