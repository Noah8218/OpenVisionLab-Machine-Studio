using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioBatchViewModelTests
{
    [Fact]
    public void ParentGateAndResetAreOwnedByScenarioBatchViewModel()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel();
        var project = new MachineProjectDocument { Name = "Batch owner test" };
        var parentAllowsRun = true;
        var otherValidationRunning = false;
        var parentNotifications = 0;
        using var viewModel = CreateViewModel(
            workspace,
            project,
            () => parentAllowsRun,
            () => otherValidationRunning,
            _ => parentNotifications++);

        Assert.True(viewModel.CanRunScenarioBatch);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        Assert.True(viewModel.IsScenarioConfigurationEnabled);

        parentAllowsRun = false;
        viewModel.InvalidateCommands();
        Assert.False(viewModel.CanRunScenarioBatch);
        Assert.False(viewModel.RunCommand.CanExecute(null));

        otherValidationRunning = true;
        Assert.False(viewModel.IsScenarioConfigurationEnabled);
        Assert.False(viewModel.CanImportEvidence);
        viewModel.Reset();

        Assert.False(viewModel.IsBatchRunning);
        Assert.Equal(0, viewModel.BatchCompletedRuns);
        Assert.Null(viewModel.LatestBatchResult);
        Assert.Null(viewModel.AcceptedBatchBaseline);
        Assert.True(parentNotifications > 0);
    }

    private static SimulationScenarioBatchViewModel CreateViewModel(
        SimulationWorkspaceViewModel workspace,
        MachineProjectDocument project,
        Func<bool> parentGate,
        Func<bool> otherValidationGate,
        Action<bool> notifyParent) =>
        new(
            workspace,
            parentGate,
            parentGate,
            parentGate,
            otherValidationGate,
            () => project,
            () => null,
            () => Task.FromResult(true),
            () => false,
            () => Task.FromResult(true),
            _ => { },
            () => throw new InvalidOperationException("The batch runner is not part of this test."),
            () => "{}",
            TimeSpan.FromMilliseconds(5),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            () => Array.Empty<SimulationScenarioTargetOption>(),
            () => { },
            _ => { },
            _ => { },
            _ => { },
            notifyParent,
            _ => { });
}
