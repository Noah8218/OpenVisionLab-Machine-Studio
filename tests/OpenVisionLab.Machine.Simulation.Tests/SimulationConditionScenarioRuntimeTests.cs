using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationConditionScenarioRuntimeTests
{
    [Fact]
    public void StartAdvanceResetAndClearOwnScenarioState()
    {
        var profile = CreateProfile(durationTicks: 2, minimumStateTicks: 1);
        var runtime = new SimulationConditionScenarioRuntime();
        runtime.ApplyStartState(
            new SimulationConditionScenarioStartState(
                profile,
                new DeterministicConditionStateMachine(profile),
                IsActive: true));

        Assert.True(runtime.IsActive);
        Assert.Empty(runtime.Advance());

        var completionEvents = runtime.Advance();
        Assert.False(runtime.IsActive);
        Assert.Equal(2, runtime.ExecutedTicks);
        Assert.Equal(
            new[] { "ConditionStateChanged", "ConditionScenarioCompleted" },
            completionEvents.Select(item => item.Code).ToArray());
        Assert.Equal(2, runtime.CreateSnapshot().ExecutedTicks);

        runtime.Reset();
        var resetSnapshot = runtime.CreateSnapshot();
        Assert.True(resetSnapshot.IsConfigured);
        Assert.False(resetSnapshot.IsActive);
        Assert.Equal(0, resetSnapshot.ExecutedTicks);
        Assert.Equal(profile.InitialState, resetSnapshot.State);

        runtime.Clear();
        Assert.False(runtime.CreateSnapshot().IsConfigured);
        Assert.Null(runtime.Profile);
    }

    [Fact]
    public void InactiveProgressStillConsumesTheCurrentTickAfterSchedulingRejects()
    {
        var profile = CreateProfile(durationTicks: 3, minimumStateTicks: 1);
        var runtime = new SimulationConditionScenarioRuntime();
        runtime.ApplyStartState(
            new SimulationConditionScenarioStartState(
                profile,
                new DeterministicConditionStateMachine(profile),
                IsActive: true));
        runtime.SetActive(false);

        Assert.Empty(runtime.Advance());
        Assert.Equal(1, runtime.ExecutedTicks);
        Assert.False(runtime.IsActive);
    }

    [Fact]
    public void ScheduledFaultStateAndAutomaticRunInterruptionStayWithScenarioRuntime()
    {
        var profile = CreateProfile(
            durationTicks: 4,
            minimumStateTicks: 1,
            faultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                InjectTick: 1,
                HoldTicks: 2,
                RestartSequenceId: "sequence"));
        var runtime = new SimulationConditionScenarioRuntime();
        runtime.ApplyStartState(
            new SimulationConditionScenarioStartState(
                profile,
                new DeterministicConditionStateMachine(profile),
                IsActive: true));

        runtime.ApplyScheduledFaultInjectionOutcome(
            new SimulationConditionScheduledFaultInjectionOutcome(
                ScheduledFaultActive: true));
        runtime.CaptureAutomaticRunInterruption("sequence");

        Assert.True(runtime.ScheduledFaultActive);
        Assert.True(runtime.ScheduledFaultInterruptedAutomaticRun);

        runtime.ApplyScheduledFaultRecoveryState(
            scheduledFaultActive: false,
            interruptedAutomaticRun: false);

        Assert.False(runtime.ScheduledFaultActive);
        Assert.False(runtime.ScheduledFaultInterruptedAutomaticRun);
    }

    [Fact]
    public void StartingResettingAndClearingScenarioAlsoClearsScheduledFaultState()
    {
        var profile = CreateProfile(durationTicks: 2, minimumStateTicks: 1);
        var runtime = new SimulationConditionScenarioRuntime();
        runtime.ApplyStartState(
            new SimulationConditionScenarioStartState(
                profile,
                new DeterministicConditionStateMachine(profile),
                IsActive: true));
        runtime.ApplyScheduledFaultRecoveryState(
            scheduledFaultActive: true,
            interruptedAutomaticRun: true);

        runtime.Reset();
        Assert.False(runtime.ScheduledFaultActive);
        Assert.False(runtime.ScheduledFaultInterruptedAutomaticRun);

        runtime.ApplyScheduledFaultRecoveryState(
            scheduledFaultActive: true,
            interruptedAutomaticRun: true);
        runtime.Clear();
        Assert.False(runtime.ScheduledFaultActive);
        Assert.False(runtime.ScheduledFaultInterruptedAutomaticRun);
    }

    private static DeterministicConditionScenarioProfile CreateProfile(
        long durationTicks,
        int minimumStateTicks,
        DeterministicFaultRecoverySchedule? faultRecovery = null) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "runtime",
            "Runtime",
            "Runtime test",
            "equipment-1",
            7,
            durationTicks,
            minimumStateTicks,
            JitterTicks: 0,
            FaultRecovery: faultRecovery);
}
