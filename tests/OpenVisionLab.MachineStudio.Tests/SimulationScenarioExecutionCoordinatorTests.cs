using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioExecutionCoordinatorTests
{
    [Fact]
    public async Task StartPresentsAcceptedOrdinaryScenarioWithoutMainViewModel()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioTargetId = "axis-1"
        };
        var (workflow, commands) = CreateWorkflow();
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var designModeValues = new List<bool>();
        var runningValues = new List<bool>();
        var coordinator = CreateCoordinator(
            workflow,
            workspace,
            statuses,
            logs,
            designModeValues,
            runningValues);

        await coordinator.StartAsync();

        Assert.False(coordinator.OwnsRun);
        Assert.Equal(new[] { false, false }, designModeValues);
        Assert.Empty(runningValues);
        Assert.IsType<StartConditionScenarioCommand>(commands.Single());
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.ScenarioStarted"),
            statuses.Single());
        Assert.Contains("Test scenario started · CMD-", logs.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedStartPresentsFailureAndDoesNotClaimRunOwnership()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioTargetId = "axis-1"
        };
        var (workflow, commands) = CreateWorkflow(false);
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var designModeValues = new List<bool>();
        var runningValues = new List<bool>();
        var coordinator = CreateCoordinator(
            workflow,
            workspace,
            statuses,
            logs,
            designModeValues,
            runningValues);

        await coordinator.StartAsync();

        Assert.False(coordinator.OwnsRun);
        Assert.IsType<StartConditionScenarioCommand>(Assert.Single(commands));
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.ScenarioStartRejected"),
            statuses.Single());
        Assert.Contains("Start rejected", logs.Single().Message, StringComparison.Ordinal);
        Assert.Empty(runningValues);
    }

    [Fact]
    public async Task ScheduledFaultStartThenStopUpdatesOwnershipAndRunningState()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioTargetId = "axis-1",
            IsScheduledFaultEnabled = true,
            ScheduledFaultTargetId = "axis-1",
            ScheduledFaultInjectTick = 10,
            ScheduledFaultHoldTicks = 3,
            RecoverySequenceId = "recovery-sequence",
            ScenarioDurationCycles = 100
        };
        var (workflow, commands) = CreateWorkflow();
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var designModeValues = new List<bool>();
        var runningValues = new List<bool>();
        var coordinator = CreateCoordinator(
            workflow,
            workspace,
            statuses,
            logs,
            designModeValues,
            runningValues);

        await coordinator.StartAsync();
        await coordinator.StopAsync();

        Assert.False(coordinator.OwnsRun);
        Assert.Equal(new[] { true, false }, runningValues);
        Assert.Equal(
            new[]
            {
                nameof(ResetCommand),
                nameof(StartConditionScenarioCommand),
                nameof(StartSequenceCommand),
                nameof(PlayCommand),
                nameof(StopConditionScenarioCommand),
                nameof(PauseCommand)
            },
            commands.Select(command => command.GetType().Name));
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.ScenarioStopped"),
            statuses[^1]);
    }

    [Fact]
    public async Task ReplayPresentsAcceptedResultThroughTheSameBoundary()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioTargetId = "axis-1"
        };
        var (workflow, commands) = CreateWorkflow();
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var designModeValues = new List<bool>();
        var runningValues = new List<bool>();
        var coordinator = CreateCoordinator(
            workflow,
            workspace,
            statuses,
            logs,
            designModeValues,
            runningValues);

        await coordinator.ReplayAsync();

        Assert.False(coordinator.OwnsRun);
        Assert.Equal(
            new[] { nameof(ResetCommand), nameof(StartConditionScenarioCommand) },
            commands.Select(command => command.GetType().Name));
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.ScenarioReplayed"),
            statuses.Single());
        Assert.Contains("Test scenario replayed · CMD-", logs.Single().Message, StringComparison.Ordinal);
    }

    private static SimulationScenarioExecutionCoordinator CreateCoordinator(
        SimulationScenarioWorkflow workflow,
        SimulationWorkspaceViewModel workspace,
        List<string> statuses,
        List<(string Category, string Message)> logs,
        List<bool> designModeValues,
        List<bool> runningValues) => new(
        workflow,
        workspace,
        () => new MachineProjectDocument(),
        () => Task.FromResult(true),
        designModeValues.Add,
        runningValues.Add,
        statuses.Add,
        (category, message) => logs.Add((category, message)));

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
}
