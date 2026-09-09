using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationConditionScenarioProgressHandlerTests
{
    [Fact]
    public void Apply_AdvancesStateMachineWithoutTransitionBeforeBoundary()
    {
        var profile = CreateProfile(durationTicks: 3, minimumStateTicks: 2);
        var outcome = new SimulationConditionScenarioProgressHandler().Apply(
            CreateContext(profile, scenarioActive: true, executedTicks: 0));

        Assert.True(outcome.State.IsActive);
        Assert.Equal(1, outcome.State.ExecutedTicks);
        Assert.Null(outcome.State.LastTransition);
        Assert.Empty(outcome.Events!);
    }

    [Fact]
    public void Apply_EmitsTransitionAndCompletionAtFinalTick()
    {
        var profile = CreateProfile(durationTicks: 2, minimumStateTicks: 1);
        var handler = new SimulationConditionScenarioProgressHandler();
        var stateMachine = new DeterministicConditionStateMachine(profile);

        var first = handler.Apply(
            CreateContext(
                profile,
                scenarioActive: true,
                executedTicks: 0,
                stateMachine: stateMachine));
        var second = handler.Apply(
            CreateContext(
                profile,
                scenarioActive: true,
                executedTicks: first.State.ExecutedTicks,
                stateMachine: stateMachine));

        Assert.False(second.State.IsActive);
        Assert.Equal(2, second.State.ExecutedTicks);
        Assert.Equal(DeterministicConditionState.Degraded, second.State.LastTransition!.To);
        Assert.Equal(
            new[] { "ConditionStateChanged", "ConditionScenarioCompleted" },
            second.Events!.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Apply_PreservesInactiveStateAfterPriorInjectionRejection()
    {
        var profile = CreateProfile(durationTicks: 3, minimumStateTicks: 1);
        var outcome = new SimulationConditionScenarioProgressHandler().Apply(
            CreateContext(profile, scenarioActive: false, executedTicks: 0));

        Assert.False(outcome.State.IsActive);
        Assert.Equal(1, outcome.State.ExecutedTicks);
        Assert.Empty(outcome.Events!);
    }

    private static SimulationConditionScenarioProgressContext CreateContext(
        DeterministicConditionScenarioProfile profile,
        bool scenarioActive,
        long executedTicks,
        DeterministicConditionStateMachine? stateMachine = null) =>
        new(
            profile,
            stateMachine ?? new DeterministicConditionStateMachine(profile),
            scenarioActive,
            executedTicks);

    private static DeterministicConditionScenarioProfile CreateProfile(
        long durationTicks,
        int minimumStateTicks) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "progress",
            "Progress",
            "Progress test",
            "equipment-1",
            7,
            durationTicks,
            minimumStateTicks,
            JitterTicks: 0);
}
