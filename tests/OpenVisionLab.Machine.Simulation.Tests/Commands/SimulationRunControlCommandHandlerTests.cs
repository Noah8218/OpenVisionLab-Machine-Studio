using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationRunControlCommandHandlerTests
{
    [Fact]
    public void Apply_PlayReturnsRunAndControlStateDelta()
    {
        var handler = new SimulationRunControlCommandHandler();
        var context = CreateContext();

        var outcome = handler.Apply(new PlayCommand(), context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SimulationRunMode.RealTime, outcome.RunMode);
        Assert.Equal(SimulationControlOwner.Manual, outcome.ControlOwner);
        Assert.Equal(0, outcome.PendingSteps);
        Assert.False(context.SequenceDebugState.IsSemanticStepActive);
    }

    [Fact]
    public void Apply_StepIncrementsPendingStepsOnlyFromAllowedModes()
    {
        var handler = new SimulationRunControlCommandHandler();
        var context = CreateContext(pendingSteps: 2);

        var outcome = handler.Apply(new StepCommand(), context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SimulationRunMode.SingleStep, outcome.RunMode);
        Assert.Equal(3, outcome.PendingSteps);
        Assert.Equal(SequenceDebugPauseReason.FixedTick, context.SequenceDebugState.CreateSnapshot().PauseReason);
    }

    [Fact]
    public void Apply_StepSequenceRejectsAnUnconfiguredSequence()
    {
        var handler = new SimulationRunControlCommandHandler();
        var context = CreateContext();

        var outcome = handler.Apply(new StepSequenceCommand("missing"), context);

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.SequenceNotFound, outcome.Result.ErrorCode);
        Assert.Null(outcome.RunMode);
        Assert.Equal(SequenceDebugPauseReason.None, context.SequenceDebugState.CreateSnapshot().PauseReason);
    }

    private static SimulationRunControlContext CreateContext(
        SimulationRunMode runMode = SimulationRunMode.Paused,
        int pendingSteps = 0,
        string? activeSequenceId = null,
        string? currentSequenceStepId = null,
        IReadOnlyDictionary<string, CompiledSequence>? compiledSequences = null,
        IReadOnlyDictionary<string, DeterministicSequenceExecutor>? sequenceExecutors = null) =>
        new(
            runMode,
            pendingSteps,
            activeSequenceId,
            currentSequenceStepId,
            compiledSequences ?? new Dictionary<string, CompiledSequence>(),
            sequenceExecutors ?? new Dictionary<string, DeterministicSequenceExecutor>(),
            new DeterministicSequenceDebugState(),
            11,
            TimeSpan.FromMilliseconds(55));
}
