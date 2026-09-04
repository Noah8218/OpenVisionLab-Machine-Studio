using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed class SimulationManualEquipmentCommandHandler
{
    internal SimulationManualControlOutcome Apply(
        SimulationCommand command,
        SimulationManualControlContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            SetCylinderCommand setCylinder => ApplyManualCylinderCommand(command, setCylinder, context),
            SetConveyorCommand setConveyor => ApplyManualConveyorCommand(command, setConveyor, context),
            _ => SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported by the manual equipment handler.")
        };
    }

    private static SimulationManualControlOutcome ApplyManualCylinderCommand(
        SimulationCommand command,
        SetCylinderCommand setCylinder,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual cylinder control is unavailable while owner is {context.ControlOwner}.");
        }

        if (context.MachineLayout is null
            || !context.MachineLayout.TryGetCylinderCommandChannelId(
                setCylinder.CylinderId,
                out string? outputChannelId))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.CylinderNotFound,
                $"Cylinder '{setCylinder.CylinderId}' was not found.");
        }

        if (context.ActiveFaults.Values.Any(fault =>
                fault.Kind == SimulationFaultKind.CylinderTravelBlocked
                && string.Equals(fault.TargetId, setCylinder.CylinderId, StringComparison.Ordinal)))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.CylinderInterlocked,
                $"Cylinder '{setCylinder.CylinderId}' travel is blocked by an active fault.");
        }

        SignalWriteResult write = context.SignalHub.SetDigitalOutput(
            outputChannelId,
            setCylinder.Extend,
            SignalWriteOwner.Manual);
        if (!write.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                write.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Cylinder output '{outputChannelId}' write rejected: {write.ErrorCode}.");
        }

        string action = setCylinder.Extend ? "extend" : "retract";
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Cylinder '{setCylinder.CylinderId}' {action} command accepted.",
            new SimulationManualControlEvent(
                "Cylinder",
                setCylinder.Extend ? "CylinderExtendAccepted" : "CylinderRetractAccepted",
                $"{setCylinder.CylinderId} {action} command wrote {outputChannelId} = " +
                $"{context.FormatSignal(setCylinder.Extend)}."));
    }

    private static SimulationManualControlOutcome ApplyManualConveyorCommand(
        SimulationCommand command,
        SetConveyorCommand setConveyor,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual conveyor control is unavailable while owner is {context.ControlOwner}.");
        }

        if (!Enum.IsDefined(setConveyor.Direction))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ConveyorCommandInvalid,
                "Conveyor direction is invalid.");
        }

        if (context.MachineLayout is null
            || !context.MachineLayout.TryGetConveyorCommandChannelIds(
                setConveyor.ConveyorId,
                out string? runChannelId,
                out string? reverseChannelId))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ConveyorNotFound,
                $"Conveyor '{setConveyor.ConveyorId}' was not found.");
        }

        DigitalOutputPairWriteResult outputWrite = context.SignalHub.SetDigitalOutputPairAtomically(
            reverseChannelId,
            setConveyor.Direction == ConveyorDirection.Reverse,
            runChannelId,
            setConveyor.Running,
            SignalWriteOwner.Manual);
        if (!outputWrite.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                outputWrite.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Conveyor output '{outputWrite.ChannelId}' write rejected: {outputWrite.ErrorCode}.");
        }

        string action = setConveyor.Running ? $"run {setConveyor.Direction}" : "stop";
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Conveyor '{setConveyor.ConveyorId}' {action} command accepted.",
            new SimulationManualControlEvent(
                "Conveyor",
                setConveyor.Running ? "ConveyorRunAccepted" : "ConveyorStopAccepted",
                $"{setConveyor.ConveyorId} {action}; {runChannelId} = " +
                $"{context.FormatSignal(setConveyor.Running)}, {reverseChannelId} = " +
                $"{context.FormatSignal(setConveyor.Direction == ConveyorDirection.Reverse)}."));
    }
}
