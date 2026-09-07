using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationAutomaticRunRuntimeTests
{
    [Fact]
    public void ConfigureProjectsCommandAndCycleState()
    {
        var configuration = new AutomaticRunConfiguration(
            "sequence",
            "di.start",
            true,
            Repeat: true,
            RepeatDelayMilliseconds: 15);
        var runtime = new SimulationAutomaticRunRuntime();
        runtime.Configure(configuration, repeatDelayTicks: 3);

        var commandState = runtime.CreateCommandState(
            SimulationRunMode.Paused,
            SimulationControlOwner.Definition,
            pendingSteps: 0,
            activeSequenceId: null);
        Assert.Same(configuration, runtime.Configuration);
        Assert.False(commandState.AutomaticRunActive);
        Assert.Equal(3, runtime.CreateCycleContext(
            "sequence",
            new Dictionary<string, OpenVisionLab.Machine.Sequence.Runtime.DeterministicSequenceExecutor>())
            .RepeatDelayTicks);

        runtime.ApplyCommandState(commandState with
        {
            AutomaticRunActive = true,
            AutomaticRunCompletedCycleCount = 2,
            AutomaticRunRemainingDelayTicks = 1
        });

        Assert.True(runtime.IsActive);
        Assert.Equal(2, runtime.CompletedCycleCount);
        Assert.Equal(1, runtime.RemainingDelayTicks);
    }

    [Fact]
    public void CycleFaultAndResetPreserveConfigurationButClearExecutionState()
    {
        var configuration = new AutomaticRunConfiguration("sequence", null, true, true, 0);
        var runtime = new SimulationAutomaticRunRuntime();
        runtime.Configure(configuration, repeatDelayTicks: 2);
        runtime.ApplyCycleState(
            new SimulationAutomaticRunCycleState(
                "sequence",
                AutomaticRunActive: true,
                AutomaticRunWaitingForRepeat: true,
                AutomaticRunCompletedCycleCount: 4,
                AutomaticRunRemainingDelayTicks: 2));

        runtime.MarkFaulted();
        Assert.False(runtime.IsActive);
        Assert.False(runtime.WaitingForRepeat);
        Assert.Equal(4, runtime.CompletedCycleCount);
        Assert.Equal(0, runtime.RemainingDelayTicks);

        runtime.Reset();
        Assert.Same(configuration, runtime.Configuration);
        Assert.Equal(0, runtime.CompletedCycleCount);
        Assert.Equal(0, runtime.RemainingDelayTicks);
    }
}
