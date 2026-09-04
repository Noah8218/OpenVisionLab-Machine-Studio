using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationConditionScheduledFaultInjectionContext(
    DeterministicFaultRecoverySchedule? Schedule,
    long ScenarioTick,
    IList<ServoAxisComponent> Axes,
    DeterministicSignalHub SignalHub,
    DeterministicMachineLayout? MachineLayout,
    IDictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults,
    SimulationFaultCommandHandler FaultCommandHandler,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationConditionScheduledFaultInjectionEvent(
    string Category,
    string Code,
    string Message,
    string? CommandId);

internal sealed record SimulationConditionScheduledFaultInjectionOutcome(
    bool? ScheduledFaultActive = null,
    bool? ConditionScenarioActive = null,
    IReadOnlyList<SimulationConditionScheduledFaultInjectionEvent>? Events = null);

internal sealed class SimulationConditionScheduledFaultInjectionHandler
{
    public SimulationConditionScheduledFaultInjectionOutcome Apply(
        SimulationConditionScheduledFaultInjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var schedule = context.Schedule;
        if (schedule is null || context.ScenarioTick != schedule.InjectTick)
        {
            return new();
        }

        var injection = new InjectSimulationFaultCommand(
            schedule.FaultKind,
            schedule.TargetId,
            schedule.ForcedValue);
        var faultOutcome = context.FaultCommandHandler.Apply(
            injection,
            new SimulationFaultCommandContext(
                context.Axes,
                context.SignalHub,
                context.MachineLayout,
                context.ActiveFaults,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime));
        var events = faultOutcome.Events?.Select(operationEvent =>
                new SimulationConditionScheduledFaultInjectionEvent(
                    operationEvent.Category,
                    operationEvent.Code,
                    operationEvent.Message,
                    injection.CommandId))
            .ToList()
            ?? new List<SimulationConditionScheduledFaultInjectionEvent>();
        if (!faultOutcome.Result.IsAccepted)
        {
            events.Add(new SimulationConditionScheduledFaultInjectionEvent(
                "Condition",
                "ConditionFaultScheduleRejected",
                $"Scheduled {schedule.FaultKind} injection for '{schedule.TargetId}' was rejected: " +
                $"{faultOutcome.Result.ErrorCode}: {faultOutcome.Result.Detail}",
                injection.CommandId));
            return new(ConditionScenarioActive: false, Events: events);
        }

        return new(ScheduledFaultActive: true, Events: events);
    }
}
