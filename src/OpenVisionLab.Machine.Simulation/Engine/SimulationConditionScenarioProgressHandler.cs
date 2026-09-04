using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationConditionScenarioProgressContext(
    DeterministicConditionScenarioProfile Profile,
    DeterministicConditionStateMachine StateMachine,
    bool ScenarioActive,
    long ExecutedTicks);

internal sealed record SimulationConditionScenarioProgressState(
    bool IsActive,
    long ExecutedTicks,
    DeterministicConditionTransition? LastTransition);

internal sealed record SimulationConditionScenarioProgressEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationConditionScenarioProgressOutcome(
    SimulationConditionScenarioProgressState State,
    IReadOnlyList<SimulationConditionScenarioProgressEvent>? Events = null);

internal sealed class SimulationConditionScenarioProgressHandler
{
    public SimulationConditionScenarioProgressOutcome Apply(
        SimulationConditionScenarioProgressContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scenarioTick = context.ExecutedTicks;
        context.StateMachine.Advance(scenarioTick, out var transition);
        var executedTicks = scenarioTick + 1;
        var events = new List<SimulationConditionScenarioProgressEvent>();
        if (transition is not null)
        {
            events.Add(new SimulationConditionScenarioProgressEvent(
                "Condition",
                "ConditionStateChanged",
                $"{transition.TargetId}: {transition.From} -> " +
                $"{transition.To} at scenario tick {transition.TickIndex}."));
        }

        bool completed = executedTicks >= context.Profile.DurationTicks;
        if (completed)
        {
            events.Add(new SimulationConditionScenarioProgressEvent(
                "Condition",
                "ConditionScenarioCompleted",
                $"Condition scenario '{context.Profile.ScenarioId}' completed after " +
                $"{executedTicks} ticks."));
        }

        return new(
            new SimulationConditionScenarioProgressState(
                context.ScenarioActive && !completed,
                executedTicks,
                transition),
            events);
    }
}
