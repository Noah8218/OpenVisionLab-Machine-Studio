using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationAutomaticRunCycleHandlerTests
{
    [Fact]
    public void AdvanceRepeat_DecrementsDelayBeforeRestartingReadySequence()
    {
        var executor = CreateReadyExecutor("sequence");
        var handler = new SimulationAutomaticRunCycleHandler();
        var outcome = handler.AdvanceRepeat(CreateContext(
            new AutomaticRunConfiguration("sequence", null, true, true, 0),
            executor,
            new SimulationAutomaticRunCycleState(
                "sequence",
                AutomaticRunActive: true,
                AutomaticRunWaitingForRepeat: true,
                AutomaticRunCompletedCycleCount: 1,
                AutomaticRunRemainingDelayTicks: 1)));

        Assert.Null(outcome.FaultDetail);
        Assert.Equal(SequenceExecutionStatus.Running, executor.CaptureSnapshot().Status);
        Assert.False(outcome.State!.AutomaticRunWaitingForRepeat);
        Assert.Equal(0, outcome.State.AutomaticRunRemainingDelayTicks);
        Assert.Equal("AutomaticRunCycleRestarted", Assert.Single(outcome.Events!).Code);
    }

    [Fact]
    public void AdvanceRepeat_UpdatesRemainingDelayWithoutRestartingSequence()
    {
        var executor = CreateReadyExecutor("sequence");
        var handler = new SimulationAutomaticRunCycleHandler();
        var outcome = handler.AdvanceRepeat(CreateContext(
            new AutomaticRunConfiguration("sequence", null, true, true, 0),
            executor,
            new SimulationAutomaticRunCycleState(
                "sequence",
                AutomaticRunActive: true,
                AutomaticRunWaitingForRepeat: true,
                AutomaticRunCompletedCycleCount: 1,
                AutomaticRunRemainingDelayTicks: 3)));

        Assert.Null(outcome.FaultDetail);
        Assert.Equal(SequenceExecutionStatus.Ready, executor.CaptureSnapshot().Status);
        Assert.Equal(2, outcome.State!.AutomaticRunRemainingDelayTicks);
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationAutomaticRunCycleEvent>());
    }

    [Fact]
    public void Complete_NonRepeatingRunReturnsCompletionStateAndOrderedEvents()
    {
        var executor = CreateReadyExecutor("sequence");
        var handler = new SimulationAutomaticRunCycleHandler();
        var outcome = handler.Complete(CreateContext(
            new AutomaticRunConfiguration("sequence", null, true, false, 0),
            executor,
            new SimulationAutomaticRunCycleState(
                "sequence",
                AutomaticRunActive: true,
                AutomaticRunWaitingForRepeat: false,
                AutomaticRunCompletedCycleCount: 0,
                AutomaticRunRemainingDelayTicks: 0)));

        Assert.Equal(1, outcome.State!.AutomaticRunCompletedCycleCount);
        Assert.False(outcome.State.AutomaticRunActive);
        Assert.False(outcome.State.AutomaticRunWaitingForRepeat);
        Assert.Equal(
            new[] { "AutomaticRunCycleCompleted", "AutomaticRunCompleted" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    private static SimulationAutomaticRunCycleContext CreateContext(
        AutomaticRunConfiguration configuration,
        DeterministicSequenceExecutor executor,
        SimulationAutomaticRunCycleState state) =>
        new(
            configuration,
            state,
            new Dictionary<string, DeterministicSequenceExecutor>
            {
                [configuration.SequenceId] = executor
            },
            RepeatDelayTicks: 2);

    private static DeterministicSequenceExecutor CreateReadyExecutor(string id)
    {
        var compilation = new SequenceCompiler().Compile(
            new SequenceDefinition
            {
                Id = id,
                Name = id,
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "complete",
                        Name = "Complete",
                        Action = SequenceStepAction.Complete
                    }
                }
            });
        return new DeterministicSequenceExecutor(compilation.Sequence!);
    }
}
