using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationFaultCommandContext(
    IList<ServoAxisComponent> Axes,
    DeterministicSignalHub SignalHub,
    DeterministicMachineLayout? MachineLayout,
    IDictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationFaultCommandEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationFaultCommandOutcome(
    SimulationCommandResult Result,
    IReadOnlyList<SimulationFaultCommandEvent>? Events = null);

internal sealed class SimulationFaultCommandHandler
{
    public SimulationFaultCommandOutcome Apply(
        SimulationCommand command,
        SimulationFaultCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            InjectSimulationFaultCommand inject => ApplyInjectFault(command, inject, context),
            ClearSimulationFaultCommand clear => ApplyClearFault(command, clear, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationFaultCommandOutcome ApplyInjectFault(
        SimulationCommand command,
        InjectSimulationFaultCommand injectFault,
        SimulationFaultCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(injectFault.TargetId))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.FaultParameterInvalid,
                "A fault target id is required.");
        }

        var key = new SimulationFaultKey(injectFault.Kind, injectFault.TargetId);
        if (context.ActiveFaults.ContainsKey(key))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.FaultAlreadyActive,
                $"Fault '{injectFault.Kind}' is already active for '{injectFault.TargetId}'.");
        }

        switch (injectFault.Kind)
        {
            case SimulationFaultKind.StuckDigitalInput:
                if (!injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "StuckDigitalInput requires a forced Boolean value.");
                }

                if (context.SignalHub.CaptureSnapshot().TryGetSignal(
                        injectFault.TargetId,
                        out DigitalSignalSnapshot? inputSignal)
                    && inputSignal?.OverrideValue.HasValue == true)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultApplicationRejected,
                        $"Digital-input target '{injectFault.TargetId}' already has a manual force.");
                }

                DigitalInputOverrideResult inputOverride = context.SignalHub.SetDigitalInputOverride(
                    injectFault.TargetId,
                    injectFault.ForcedValue.Value);
                if (!inputOverride.IsAccepted)
                {
                    return Reject(
                        command,
                        context,
                        inputOverride.ErrorCode is SignalHubErrorCode.ChannelNotFound
                            or SignalHubErrorCode.ChannelKindMismatch
                            ? SimulationCommandErrorCode.FaultTargetNotFound
                            : SimulationCommandErrorCode.FaultApplicationRejected,
                        $"Digital-input fault target '{injectFault.TargetId}' is unavailable: " +
                        $"{inputOverride.ErrorCode}.");
                }
                break;

            case SimulationFaultKind.CylinderTravelBlocked:
                if (injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "CylinderTravelBlocked does not accept a forced Boolean value.");
                }

                if (context.MachineLayout is null
                    || !context.MachineLayout.ContainsCylinder(injectFault.TargetId))
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultTargetNotFound,
                        $"Cylinder fault target '{injectFault.TargetId}' was not found.");
                }
                break;

            case SimulationFaultKind.AxisMotionBlocked:
                if (injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "AxisMotionBlocked does not accept a forced Boolean value.");
                }

                var blockedAxis = context.Axes.FirstOrDefault(axis =>
                    string.Equals(axis.Id, injectFault.TargetId, StringComparison.Ordinal));
                if (blockedAxis is null)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultTargetNotFound,
                        $"Axis fault target '{injectFault.TargetId}' was not found.");
                }

                blockedAxis.SetMotionBlocked(true);
                break;

            case SimulationFaultKind.AxisFollowingError:
                if (injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "AxisFollowingError does not accept a forced Boolean value.");
                }

                var followingErrorAxis = context.Axes.FirstOrDefault(axis =>
                    string.Equals(axis.Id, injectFault.TargetId, StringComparison.Ordinal));
                if (followingErrorAxis is null)
                {
                    return Reject(
                        command,
                        context,
                        SimulationCommandErrorCode.FaultTargetNotFound,
                        $"Axis fault target '{injectFault.TargetId}' was not found.");
                }

                followingErrorAxis.SetFollowingErrorInjected(true);
                break;

            default:
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.FaultParameterInvalid,
                    $"Fault kind '{injectFault.Kind}' is unsupported.");
        }

        var snapshot = new SimulationFaultSnapshot(
            injectFault.Kind,
            injectFault.TargetId,
            injectFault.ForcedValue,
            context.CommandBoundaryTick,
            context.CommandBoundaryTime);
        context.ActiveFaults.Add(key, snapshot);
        return Accept(
            command,
            context,
            $"Fault '{injectFault.Kind}' injected for '{injectFault.TargetId}'.",
            new SimulationFaultCommandEvent("Fault", "FaultInjected", FormatFault(snapshot)));
    }

    private static SimulationFaultCommandOutcome ApplyClearFault(
        SimulationCommand command,
        ClearSimulationFaultCommand clearFault,
        SimulationFaultCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(clearFault.TargetId))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.FaultParameterInvalid,
                "A fault target id is required.");
        }

        var key = new SimulationFaultKey(clearFault.Kind, clearFault.TargetId);
        if (!context.ActiveFaults.TryGetValue(key, out SimulationFaultSnapshot? activeFault))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.FaultNotActive,
                $"Fault '{clearFault.Kind}' is not active for '{clearFault.TargetId}'.");
        }

        var events = new List<SimulationFaultCommandEvent>();
        if (clearFault.Kind == SimulationFaultKind.StuckDigitalInput)
        {
            DigitalInputOverrideResult inputOverride = context.SignalHub.SetDigitalInputOverride(
                clearFault.TargetId,
                null);
            if (!inputOverride.IsAccepted)
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.FaultApplicationRejected,
                    $"Digital-input override could not be cleared: {inputOverride.ErrorCode}.");
            }
        }
        else if (clearFault.Kind == SimulationFaultKind.AxisMotionBlocked)
        {
            var blockedAxis = context.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, clearFault.TargetId, StringComparison.Ordinal));
            if (blockedAxis is null)
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.FaultApplicationRejected,
                    $"Axis fault target '{clearFault.TargetId}' could not be recovered.");
            }

            blockedAxis.SetMotionBlocked(false);
        }
        else if (clearFault.Kind == SimulationFaultKind.AxisFollowingError)
        {
            var followingErrorAxis = context.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, clearFault.TargetId, StringComparison.Ordinal));
            if (followingErrorAxis is null)
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.FaultApplicationRejected,
                    $"Axis fault target '{clearFault.TargetId}' could not be recovered.");
            }

            var alarmWasActive = followingErrorAxis.DriveAlarmActive;
            followingErrorAxis.SetFollowingErrorInjected(false);
            if (alarmWasActive)
            {
                events.Add(new SimulationFaultCommandEvent(
                    "Motion",
                    "AxisDriveAlarmCleared",
                    $"{followingErrorAxis.Id} drive alarm cleared; axis is stopped."));
            }
        }

        context.ActiveFaults.Remove(key);
        events.Add(new SimulationFaultCommandEvent(
            "Fault",
            "FaultCleared",
            $"Cleared {FormatFault(activeFault)}"));
        return Accept(
            command,
            context,
            $"Fault '{clearFault.Kind}' cleared for '{clearFault.TargetId}'.",
            events);
    }

    private static SimulationFaultCommandOutcome Accept(
        SimulationCommand command,
        SimulationFaultCommandContext context,
        string detail,
        SimulationFaultCommandEvent? operationEvent = null) =>
        Accept(
            command,
            context,
            detail,
            operationEvent is null ? null : new[] { operationEvent });

    private static SimulationFaultCommandOutcome Accept(
        SimulationCommand command,
        SimulationFaultCommandContext context,
        string detail,
        IReadOnlyList<SimulationFaultCommandEvent>? events) =>
        new(
            SimulationCommandResult.Accepted(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                detail),
            events);

    private static SimulationFaultCommandOutcome Reject(
        SimulationCommand command,
        SimulationFaultCommandContext context,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        new(
            SimulationCommandResult.Rejected(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                errorCode,
                detail));

    private static string FormatFault(SimulationFaultSnapshot fault) =>
        fault.ForcedValue.HasValue
            ? $"{fault.Kind} on '{fault.TargetId}' forced to {FormatSignal(fault.ForcedValue.Value)}."
            : $"{fault.Kind} on '{fault.TargetId}'.";

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";
}
