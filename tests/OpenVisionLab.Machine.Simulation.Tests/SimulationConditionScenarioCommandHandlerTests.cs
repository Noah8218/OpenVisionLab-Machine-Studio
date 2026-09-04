using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationConditionScenarioCommandHandlerTests
{
    [Fact]
    public void Apply_ValidStartReturnsNormalizedProfileAndInitializedState()
    {
        var handler = new SimulationConditionScenarioCommandHandler();
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            " scenario ",
            " Scenario ",
            " Description ",
            " x ",
            7,
            3);

        var outcome = handler.Apply(
            new StartConditionScenarioCommand(profile),
            CreateContext());

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal("scenario", outcome.State!.Profile.ScenarioId);
        Assert.Equal("x", outcome.State.Profile.TargetId);
        Assert.True(outcome.State.IsActive);
        Assert.Equal(
            DeterministicConditionState.Normal,
            outcome.State.StateMachine.State);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("ConditionScenarioStarted", operationEvent.Code);
        Assert.Contains("scenario", operationEvent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MissingConditionTargetRejectsWithoutReturningState()
    {
        var handler = new SimulationConditionScenarioCommandHandler();
        var profile = CreateProfile(targetId: "missing");

        var outcome = handler.Apply(
            new StartConditionScenarioCommand(profile),
            CreateContext());

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(
            SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
            outcome.Result.ErrorCode);
        Assert.Null(outcome.State);
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationConditionScenarioCommandEvent>());
    }

    [Fact]
    public void Apply_ZeroDurationReturnsStartAndCompletionEventsWithoutActiveState()
    {
        var handler = new SimulationConditionScenarioCommandHandler();
        var profile = CreateProfile(durationTicks: 0);

        var outcome = handler.Apply(
            new StartConditionScenarioCommand(profile),
            CreateContext());

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.False(outcome.State!.IsActive);
        Assert.Equal(
            new[] { "ConditionScenarioStarted", "ConditionScenarioCompleted" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    private static SimulationConditionScenarioCommandContext CreateContext(
        bool scenarioActive = false) =>
        new(
            scenarioActive,
            CreateSnapshot(),
            new Dictionary<string, DeterministicSequenceExecutor>(),
            new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>(),
            19,
            TimeSpan.FromMilliseconds(95));

    private static SimulationSnapshot CreateSnapshot() =>
        new(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Definition,
            1,
            new[] { new AxisSnapshot("x", "X", AxisState.Idle, 0, 0) },
            0,
            new[] { new DigitalSignalSnapshot("di.start", "Start", ChannelKind.DigitalInput, false) },
            Array.Empty<SequenceExecutionSnapshot>());

    private static DeterministicConditionScenarioProfile CreateProfile(
        string targetId = "x",
        long durationTicks = 3) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "scenario",
            "Scenario",
            "Description",
            targetId,
            7,
            durationTicks);
}
