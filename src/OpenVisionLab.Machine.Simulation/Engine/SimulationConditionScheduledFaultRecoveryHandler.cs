using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationConditionScheduledFaultRecoveryState(
    bool ScheduledFaultActive,
    bool InterruptedAutomaticRun,
    string? ActiveSequenceId,
    SimulationControlOwner ControlOwner,
    bool AutomaticRunActive,
    bool AutomaticRunWaitingForRepeat,
    int AutomaticRunRemainingDelayTicks);

internal sealed record SimulationConditionScheduledFaultRecoveryContext(
    DeterministicFaultRecoverySchedule? Schedule,
    bool RestartSequence,
    string? CommandId,
    SimulationConditionScheduledFaultRecoveryState State,
    IList<ServoAxisComponent> Axes,
    DeterministicSignalHub SignalHub,
    DeterministicMachineLayout? MachineLayout,
    IDictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    SimulationFaultCommandHandler FaultCommandHandler,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationConditionScheduledFaultRecoveryEvent(
    string Category,
    string Code,
    string Message,
    string? CommandId);

internal sealed record SimulationConditionScheduledFaultRecoveryOutcome(
    SimulationConditionScheduledFaultRecoveryState? State = null,
    IReadOnlyList<SimulationConditionScheduledFaultRecoveryEvent>? Events = null);

internal sealed class SimulationConditionScheduledFaultRecoveryHandler
{
    public SimulationConditionScheduledFaultRecoveryOutcome Apply(
        SimulationConditionScheduledFaultRecoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var schedule = context.Schedule;
        if (!context.State.ScheduledFaultActive || schedule is null)
        {
            return new();
        }

        var clear = new ClearSimulationFaultCommand(schedule.FaultKind, schedule.TargetId);
        var faultOutcome = context.FaultCommandHandler.Apply(
            clear,
            new SimulationFaultCommandContext(
                context.Axes,
                context.SignalHub,
                context.MachineLayout,
                context.ActiveFaults,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime));
        var events = faultOutcome.Events?.Select(operationEvent =>
                new SimulationConditionScheduledFaultRecoveryEvent(
                    operationEvent.Category,
                    operationEvent.Code,
                    operationEvent.Message,
                    clear.CommandId))
            .ToList()
            ?? new List<SimulationConditionScheduledFaultRecoveryEvent>();
        if (!faultOutcome.Result.IsAccepted)
        {
            events.Add(new SimulationConditionScheduledFaultRecoveryEvent(
                "Condition",
                "ConditionFaultClearRejected",
                $"Scheduled {schedule.FaultKind} clear for '{schedule.TargetId}' was rejected: " +
                $"{faultOutcome.Result.ErrorCode}: {faultOutcome.Result.Detail}",
                context.CommandId ?? clear.CommandId));
            return new(Events: events);
        }

        var state = context.State with
        {
            ScheduledFaultActive = false,
            InterruptedAutomaticRun = false
        };
        if (!context.RestartSequence || schedule.RestartSequenceId is null)
        {
            return new(state, events);
        }

        var executor = context.SequenceExecutors[schedule.RestartSequenceId];
        if (executor.CaptureSnapshot().Status != SequenceExecutionStatus.Faulted)
        {
            return new(state, events);
        }

        executor.Reset();
        var start = executor.Start();
        if (!start.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Prevalidated recovery sequence '{schedule.RestartSequenceId}' could not restart.");
        }

        state = state with
        {
            ActiveSequenceId = schedule.RestartSequenceId,
            ControlOwner = SimulationControlOwner.EmbeddedSequence
        };
        events.Add(new SimulationConditionScheduledFaultRecoveryEvent(
            "Sequence",
            "SequenceStarted",
            $"{schedule.RestartSequenceId} entered {start.CurrentStepId}; restarted by condition scenario recovery.",
            context.CommandId));
        if (context.State.InterruptedAutomaticRun)
        {
            state = state with
            {
                AutomaticRunActive = true,
                AutomaticRunWaitingForRepeat = false,
                AutomaticRunRemainingDelayTicks = 0
            };
            events.Add(new SimulationConditionScheduledFaultRecoveryEvent(
                "AutomaticRun",
                "AutomaticRunRecovered",
                $"Automatic sequence '{schedule.RestartSequenceId}' resumed after scheduled fault recovery.",
                context.CommandId));
        }

        return new(state, events);
    }
}
