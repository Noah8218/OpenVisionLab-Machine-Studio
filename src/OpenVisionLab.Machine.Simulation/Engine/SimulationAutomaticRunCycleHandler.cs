using OpenVisionLab.Machine.Sequence.Runtime;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationAutomaticRunCycleState(
    string? ActiveSequenceId,
    bool AutomaticRunActive,
    bool AutomaticRunWaitingForRepeat,
    long AutomaticRunCompletedCycleCount,
    int AutomaticRunRemainingDelayTicks);

internal sealed record SimulationAutomaticRunCycleContext(
    AutomaticRunConfiguration? Configuration,
    SimulationAutomaticRunCycleState State,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    int RepeatDelayTicks);

internal sealed record SimulationAutomaticRunCycleEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationAutomaticRunCycleOutcome(
    SimulationAutomaticRunCycleState? State = null,
    string? FaultDetail = null,
    IReadOnlyList<SimulationAutomaticRunCycleEvent>? Events = null);

internal sealed class SimulationAutomaticRunCycleHandler
{
    public SimulationAutomaticRunCycleOutcome AdvanceRepeat(
        SimulationAutomaticRunCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = context.State;
        if (!state.AutomaticRunActive || !state.AutomaticRunWaitingForRepeat)
        {
            return new();
        }

        var remainingDelayTicks = state.AutomaticRunRemainingDelayTicks > 0
            ? state.AutomaticRunRemainingDelayTicks - 1
            : 0;
        var delayedState = state with
        {
            AutomaticRunRemainingDelayTicks = remainingDelayTicks
        };
        if (remainingDelayTicks > 0)
        {
            return new(delayedState);
        }

        var configuration = context.Configuration;
        if (configuration is null
            || !context.SequenceExecutors.TryGetValue(configuration.SequenceId, out var executor))
        {
            return new(
                delayedState,
                "The configured automatic sequence is unavailable during repeat.");
        }

        executor.Reset();
        var start = executor.Start();
        if (!start.IsSuccess)
        {
            return new(
                delayedState,
                start.Error?.Message ?? "The automatic sequence could not restart.");
        }

        return new(
            delayedState with
            {
                ActiveSequenceId = configuration.SequenceId,
                AutomaticRunWaitingForRepeat = false,
                AutomaticRunRemainingDelayTicks = 0
            },
            Events: new[]
            {
                new SimulationAutomaticRunCycleEvent(
                    "AutomaticRun",
                    "AutomaticRunCycleRestarted",
                    $"Automatic cycle {state.AutomaticRunCompletedCycleCount + 1} entered {start.CurrentStepId}.")
            });
    }

    public SimulationAutomaticRunCycleOutcome Complete(
        SimulationAutomaticRunCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = context.State;
        var configuration = context.Configuration;
        if (!state.AutomaticRunActive
            || configuration is null
            || !string.Equals(state.ActiveSequenceId, configuration.SequenceId, StringComparison.Ordinal))
        {
            return new();
        }

        var completedCycleCount = state.AutomaticRunCompletedCycleCount + 1;
        var nextState = state with
        {
            AutomaticRunCompletedCycleCount = completedCycleCount
        };
        var events = new List<SimulationAutomaticRunCycleEvent>
        {
            new(
                "AutomaticRun",
                "AutomaticRunCycleCompleted",
                $"Automatic cycle {completedCycleCount} completed.")
        };
        if (configuration.Repeat)
        {
            return new(
                nextState with
                {
                    AutomaticRunWaitingForRepeat = true,
                    AutomaticRunRemainingDelayTicks = context.RepeatDelayTicks
                },
                Events: events);
        }

        events.Add(new SimulationAutomaticRunCycleEvent(
            "AutomaticRun",
            "AutomaticRunCompleted",
            $"Automatic run completed after {completedCycleCount} cycle(s)."));
        return new(
            nextState with
            {
                AutomaticRunActive = false,
                AutomaticRunWaitingForRepeat = false,
                AutomaticRunRemainingDelayTicks = 0
            },
            Events: events);
    }
}
