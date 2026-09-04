using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ScheduledFaultScenarioWorkflowTests
{
    [Fact]
    public async Task AutomaticRunBranchDispatchesAllStagesInOrder()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.StartAsync(CreateProfile(), hasAutomaticRun: true);

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.StartCommand);
        Assert.Equal(
            new[]
            {
                nameof(ResetCommand),
                nameof(StartConditionScenarioCommand),
                nameof(StartAutomaticRunCommand),
                nameof(PlayCommand)
            },
            commands.Select(command => command.GetType().Name));
    }

    [Fact]
    public async Task RecoveryBranchStartsTheConfiguredSequenceBeforePlay()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.StartAsync(
            CreateProfile(restartSequenceId: "recovery-sequence"),
            hasAutomaticRun: false);

        Assert.True(result.IsAccepted);
        var recovery = Assert.IsType<StartSequenceCommand>(commands[2]);
        Assert.Equal("recovery-sequence", recovery.SequenceId);
        Assert.IsType<PlayCommand>(commands[3]);
    }

    [Fact]
    public async Task AutomaticRunFailureStopsTheStartedConditionScenario()
    {
        var (workflow, commands) = CreateWorkflow(true, true, false);

        var result = await workflow.StartAsync(CreateProfile(), hasAutomaticRun: true);

        Assert.False(result.IsAccepted);
        Assert.Equal(
            ScheduledFaultScenarioFailureStage.StartAutomaticRun,
            result.FailureStage);
        Assert.False(result.FailureResult!.IsAccepted);
        Assert.Equal(nameof(StopConditionScenarioCommand), commands[^1].GetType().Name);
        Assert.Equal(4, commands.Count);
    }

    [Fact]
    public async Task PlayFailureStopsTheStartedConditionScenario()
    {
        var (workflow, commands) = CreateWorkflow(true, true, true, false);

        var result = await workflow.StartAsync(CreateProfile(), hasAutomaticRun: true);

        Assert.False(result.IsAccepted);
        Assert.Equal(ScheduledFaultScenarioFailureStage.Play, result.FailureStage);
        Assert.Equal(
            new[]
            {
                nameof(ResetCommand),
                nameof(StartConditionScenarioCommand),
                nameof(StartAutomaticRunCommand),
                nameof(PlayCommand),
                nameof(StopConditionScenarioCommand)
            },
            commands.Select(command => command.GetType().Name));
    }

    [Fact]
    public async Task ResetFailureDoesNotIssueCleanupOrStartCommands()
    {
        var (workflow, commands) = CreateWorkflow(false);

        var result = await workflow.StartAsync(CreateProfile(), hasAutomaticRun: true);

        Assert.False(result.IsAccepted);
        Assert.Equal(ScheduledFaultScenarioFailureStage.Reset, result.FailureStage);
        Assert.Single(commands);
        Assert.IsType<ResetCommand>(commands[0]);
    }

    private static (ScheduledFaultScenarioWorkflow Workflow, List<SimulationCommand> Commands)
        CreateWorkflow(params bool[] accepted)
    {
        var commands = new List<SimulationCommand>();
        var results = new Queue<bool>(accepted);
        var workflow = new ScheduledFaultScenarioWorkflow(command =>
        {
            commands.Add(command);
            var isAccepted = results.Count == 0 || results.Dequeue();
            return Task.FromResult(
                new SimulationCommandResult(
                    command.CommandId,
                    isAccepted,
                    0,
                    TimeSpan.Zero,
                    isAccepted
                        ? SimulationCommandErrorCode.None
                        : SimulationCommandErrorCode.EngineFaulted,
                    isAccepted ? null : "rejected"));
        });
        return (workflow, commands);
    }

    private static DeterministicConditionScenarioProfile CreateProfile(
        string? restartSequenceId = null) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "scheduled-fault",
            "Scheduled fault",
            "Scheduled fault workflow test",
            "axis-1",
            7,
            100,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-1",
                10,
                3,
                RestartSequenceId: restartSequenceId));
}
