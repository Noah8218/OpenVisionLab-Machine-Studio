using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed class SimulationManualInputCommandHandler
{
    internal SimulationManualControlOutcome Apply(
        SimulationCommand command,
        SimulationManualControlContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            SetVirtualInputCommand setInput => ApplyVirtualInput(command, setInput, context),
            SetVirtualInputForceCommand setInputForce => ApplyVirtualInputForce(command, setInputForce, context),
            SetDigitalSensorForceCommand setSensorForce => ApplyDigitalSensorForce(command, setSensorForce, context),
            _ => SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported by the manual input handler.")
        };
    }

    private static SimulationManualControlOutcome ApplyVirtualInput(
        SimulationCommand command,
        SetVirtualInputCommand setInput,
        SimulationManualControlContext context)
    {
        SignalWriteResult write = context.SignalHub.SetDigitalInput(
            setInput.ChannelId,
            setInput.Value,
            SignalWriteOwner.Manual);
        if (!write.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                write.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Input '{setInput.ChannelId}' write failed: {write.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Input '{setInput.ChannelId}' set to {context.FormatSignal(setInput.Value)}.",
            write.StateChanged
                ? new SimulationManualControlEvent(
                    "I/O",
                    "DigitalInputChanged",
                    $"{setInput.ChannelId} = {context.FormatSignal(setInput.Value)}.")
                : null);
    }

    private static SimulationManualControlOutcome ApplyVirtualInputForce(
        SimulationCommand command,
        SetVirtualInputForceCommand setInputForce,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual input forcing is unavailable while owner is {context.ControlOwner}.");
        }

        if (context.ActiveFaults.ContainsKey(
                new SimulationFaultKey(
                    SimulationFaultKind.StuckDigitalInput,
                    setInputForce.ChannelId)))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.SignalWriteRejected,
                $"Input '{setInputForce.ChannelId}' has an active stuck-input fault.");
        }

        DigitalInputOverrideResult inputOverride = context.SignalHub.SetDigitalInputOverride(
            setInputForce.ChannelId,
            setInputForce.ForcedValue);
        if (!inputOverride.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                inputOverride.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Input '{setInputForce.ChannelId}' force failed: {inputOverride.ErrorCode}.");
        }

        string code = setInputForce.ForcedValue switch
        {
            true => "DigitalInputForceOnAccepted",
            false => "DigitalInputForceOffAccepted",
            null => "DigitalInputForceCleared"
        };
        string action = setInputForce.ForcedValue.HasValue
            ? $"forced {context.FormatSignal(setInputForce.ForcedValue.Value)}"
            : "force cleared";
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Input '{setInputForce.ChannelId}' {action}.",
            new SimulationManualControlEvent(
                "I/O",
                code,
                $"{setInputForce.ChannelId} {action}; effective = " +
                $"{context.FormatSignal(inputOverride.CurrentValue ?? false)}."));
    }

    private static SimulationManualControlOutcome ApplyDigitalSensorForce(
        SimulationCommand command,
        SetDigitalSensorForceCommand setSensorForce,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual sensor forcing is unavailable while owner is {context.ControlOwner}.");
        }

        if (context.MachineLayout is null
            || !context.MachineLayout.TryGetDigitalSensorOutputChannelId(
                setSensorForce.SensorId,
                out string? inputChannelId))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.DigitalSensorNotFound,
                $"Digital sensor '{setSensorForce.SensorId}' was not found.");
        }

        if (context.ActiveFaults.ContainsKey(
                new SimulationFaultKey(SimulationFaultKind.StuckDigitalInput, inputChannelId!)))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.DigitalSensorInterlocked,
                $"Digital sensor '{setSensorForce.SensorId}' has an active stuck-input fault.");
        }

        DigitalInputOverrideResult inputOverride = context.SignalHub.SetDigitalInputOverride(
            inputChannelId,
            setSensorForce.ForcedValue);
        if (!inputOverride.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                inputOverride.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Digital sensor input '{inputChannelId}' force failed: {inputOverride.ErrorCode}.");
        }

        string code = setSensorForce.ForcedValue switch
        {
            true => "DigitalSensorForceOnAccepted",
            false => "DigitalSensorForceOffAccepted",
            null => "DigitalSensorForceCleared"
        };
        string action = setSensorForce.ForcedValue.HasValue
            ? $"forced {context.FormatSignal(setSensorForce.ForcedValue.Value)}"
            : "force cleared";
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Digital sensor '{setSensorForce.SensorId}' {action}.",
            new SimulationManualControlEvent(
                "Sensor",
                code,
                $"{setSensorForce.SensorId} {action}; {inputChannelId} effective = " +
                $"{context.FormatSignal(inputOverride.CurrentValue ?? false)}."));
    }
}
