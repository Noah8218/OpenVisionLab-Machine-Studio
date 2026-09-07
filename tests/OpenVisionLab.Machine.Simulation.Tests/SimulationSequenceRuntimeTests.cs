using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationSequenceRuntimeTests
{
    [Fact]
    public void ConfigureOwnsCatalogAndResetClearsDebugAndExecutorState()
    {
        var compilation = new SequenceCompiler().Compile(
            new SequenceDefinition
            {
                Id = "sequence",
                Name = "Sequence",
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
        CompiledSequence sequence = compilation.Sequence!;
        var executor = new DeterministicSequenceExecutor(sequence);
        var runtime = new SimulationSequenceRuntime();
        runtime.DebugState.SetBreakpoint("sequence", "complete", true);

        runtime.Configure(
            new Dictionary<string, CompiledSequence> { [sequence.Id] = sequence },
            new Dictionary<string, DeterministicSequenceExecutor> { [sequence.Id] = executor });
        Assert.Same(sequence, runtime.CompiledSequences[sequence.Id]);
        Assert.Same(executor, runtime.SequenceExecutors[sequence.Id]);
        Assert.Empty(runtime.DebugState.CreateSnapshot().Breakpoints);

        Assert.True(executor.Start().IsSuccess);
        Assert.Equal("complete", runtime.CurrentStepId(sequence.Id));
        runtime.ResetExecutors();
        Assert.Equal(SequenceExecutionStatus.Ready, executor.CaptureSnapshot().Status);

        runtime.ClearConfiguration();
        Assert.Empty(runtime.CompiledSequences);
        Assert.Empty(runtime.SequenceExecutors);
        Assert.Empty(runtime.DebugState.CreateSnapshot().Breakpoints);
    }
}
