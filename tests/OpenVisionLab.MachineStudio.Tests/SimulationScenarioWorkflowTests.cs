using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioWorkflowTests
{
    [Fact]
    public async Task StartDispatchesOrdinaryConditionScenario()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.StartAsync(CreateProfile(), hasAutomaticRun: false);

        Assert.True(result.IsAccepted);
        Assert.False(result.OwnsRun);
        Assert.IsType<StartConditionScenarioCommand>(commands.Single());
    }

    [Fact]
    public async Task StartReturnsTheRejectedCommandResult()
    {
        var (workflow, commands) = CreateWorkflow(false);

        var result = await workflow.StartAsync(CreateProfile(), hasAutomaticRun: false);

        Assert.False(result.IsAccepted);
        Assert.False(result.OwnsRun);
        Assert.Equal(SimulationCommandErrorCode.EngineFaulted, result.FailureResult!.ErrorCode);
        Assert.IsType<StartConditionScenarioCommand>(commands.Single());
    }

    [Fact]
    public async Task ReplayDispatchesResetThenOrdinaryStart()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.ReplayAsync(CreateProfile(), hasAutomaticRun: false);

        Assert.True(result.IsAccepted);
        Assert.Equal(
            new[]
            {
                nameof(ResetCommand),
                nameof(StartConditionScenarioCommand)
            },
            commands.Select(command => command.GetType().Name));
    }

    [Fact]
    public async Task ReplayStopsAtResetFailure()
    {
        var (workflow, commands) = CreateWorkflow(false);

        var result = await workflow.ReplayAsync(CreateProfile(), hasAutomaticRun: false);

        Assert.False(result.IsAccepted);
        Assert.Equal(SimulationScenarioFailureStage.ReplayReset, result.FailureStage);
        Assert.Single(commands);
    }

    [Fact]
    public async Task StopOwnedRunPausesAfterStoppingTheScenario()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.StopAsync(ownsRun: true);

        Assert.True(result.IsAccepted);
        Assert.False(result.OwnsRunAfterOperation);
        Assert.Equal(
            new[]
            {
                nameof(StopConditionScenarioCommand),
                nameof(PauseCommand)
            },
            commands.Select(command => command.GetType().Name));
    }

    [Fact]
    public async Task StopUnownedRunDoesNotPauseTheExistingRuntime()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.StopAsync(ownsRun: false);

        Assert.True(result.IsAccepted);
        Assert.False(result.OwnsRunAfterOperation);
        Assert.Single(commands);
    }

    [Fact]
    public async Task PauseFailureRetainsRunOwnershipForTheCaller()
    {
        var (workflow, commands) = CreateWorkflow(true, false);

        var result = await workflow.StopAsync(ownsRun: true);

        Assert.False(result.IsAccepted);
        Assert.True(result.OwnsRunAfterOperation);
        Assert.Equal(nameof(PauseCommand), commands[^1].GetType().Name);
        Assert.False(result.PauseResult!.IsAccepted);
    }

    [Fact]
    public async Task ScheduledStartIsDelegatedToTheExistingTransaction()
    {
        var (workflow, commands) = CreateWorkflow();

        var result = await workflow.StartAsync(
            CreateProfile(restartSequenceId: "recovery-sequence"),
            hasAutomaticRun: false);

        Assert.True(result.IsAccepted);
        Assert.True(result.OwnsRun);
        Assert.Equal(
            new[]
            {
                nameof(ResetCommand),
                nameof(StartConditionScenarioCommand),
                nameof(StartSequenceCommand),
                nameof(PlayCommand)
            },
            commands.Select(command => command.GetType().Name));
    }

    private static (SimulationScenarioWorkflow Workflow, List<SimulationCommand> Commands)
        CreateWorkflow(params bool[] accepted)
    {
        var commands = new List<SimulationCommand>();
        var results = new Queue<bool>(accepted);
        var workflow = new SimulationScenarioWorkflow(command =>
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
            "scenario-workflow",
            "Scenario workflow",
            "Ordinary scenario workflow test",
            "axis-1",
            7,
            100,
            FaultRecovery: restartSequenceId is null
                ? null
                : new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.AxisMotionBlocked,
                    "axis-1",
                    10,
                    3,
                    RestartSequenceId: restartSequenceId));
}
