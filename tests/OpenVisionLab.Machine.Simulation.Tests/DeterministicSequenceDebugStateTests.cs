using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSequenceDebugStateTests
{
    [Fact]
    public void Breakpoints_AreStoredAndProjectedInStableOrder()
    {
        var state = new DeterministicSequenceDebugState();

        state.SetBreakpoint("z-sequence", "step", true);
        state.SetBreakpoint("a-sequence", "step-b", true);
        state.SetBreakpoint("a-sequence", "step-a", true);

        Assert.Equal(
            new[]
            {
                new SequenceBreakpointSnapshot("a-sequence", "step-a"),
                new SequenceBreakpointSnapshot("a-sequence", "step-b"),
                new SequenceBreakpointSnapshot("z-sequence", "step")
            },
            state.CreateSnapshot().Breakpoints);

        state.SetBreakpoint("a-sequence", "step-b", false);

        Assert.False(state.IsBreakpoint("a-sequence", "step-b"));
        Assert.True(state.IsBreakpoint("a-sequence", "step-a"));
    }

    [Fact]
    public void SemanticStepBoundary_RequiresTheRequestedRootTransition()
    {
        var state = new DeterministicSequenceDebugState();
        state.BeginSemanticStep("root", "entry");

        var matching = new SequenceExecutionResult(
            CreateSnapshot("root", "next"),
            Transitioned: true,
            PreviousStepId: "entry",
            CurrentStepId: "next",
            Error: null,
            PreviousSequenceId: "root",
            CurrentSequenceId: "root");
        var childTransition = matching with { PreviousSequenceId = "child" };

        Assert.True(state.IsSemanticStepBoundary(matching, "root"));
        Assert.False(state.IsSemanticStepBoundary(childTransition, "root"));
        Assert.Equal("root", state.GetActiveSemanticStepSequenceId("root"));
        Assert.Null(state.GetActiveSemanticStepSequenceId("other"));
    }

    [Fact]
    public void Clear_RemovesPendingStepPauseAndBreakpoints()
    {
        var state = new DeterministicSequenceDebugState();
        state.SetBreakpoint("sequence", "step", true);
        state.BeginSemanticStep("sequence", "entry");
        state.SetPause(SequenceDebugPauseReason.Breakpoint, "step");

        state.Clear();

        var snapshot = state.CreateSnapshot();
        Assert.False(snapshot.IsSemanticStepActive);
        Assert.Null(snapshot.SemanticStepSequenceId);
        Assert.Equal(SequenceDebugPauseReason.None, snapshot.PauseReason);
        Assert.Null(snapshot.PausedStepId);
        Assert.Empty(snapshot.Breakpoints);
    }

    private static SequenceExecutionSnapshot CreateSnapshot(string sequenceId, string currentStepId) =>
        new(
            sequenceId,
            SequenceExecutionStatus.Running,
            currentStepId,
            1,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1,
            LastError: null,
            TimeSpan.FromSeconds(1),
            ActiveSequenceId: sequenceId,
            CallStack: [sequenceId]);
}
