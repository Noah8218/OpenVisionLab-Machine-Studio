using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
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

    [Fact]
    public void DisposeDisablesCommandsAndNotifiesFinalAdmission()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel();
        var project = new MachineProjectDocument { Name = "Batch dispose test" };
        using var viewModel = CreateViewModel(
            workspace,
            project,
            () => true,
            () => false,
            _ => { });

        var runNotifications = 0;
        var cancelNotifications = 0;
        var acceptNotifications = 0;
        var clearNotifications = 0;
        var navigateNotifications = 0;
        viewModel.RunCommand.CanExecuteChanged += (_, _) => runNotifications++;
        viewModel.CancelCommand.CanExecuteChanged += (_, _) => cancelNotifications++;
        viewModel.AcceptBaselineCommand.CanExecuteChanged += (_, _) => acceptNotifications++;
        viewModel.ClearBaselineCommand.CanExecuteChanged += (_, _) => clearNotifications++;
        viewModel.NavigateToMismatchCommand.CanExecuteChanged += (_, _) => navigateNotifications++;

        Assert.True(viewModel.RunCommand.CanExecute(null));

        viewModel.Dispose();

        Assert.False(viewModel.RunCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.False(viewModel.AcceptBaselineCommand.CanExecute(null));
        Assert.False(viewModel.ClearBaselineCommand.CanExecute(null));
        Assert.False(viewModel.NavigateToMismatchCommand.CanExecute(null));
        Assert.False(viewModel.CanExportEvidence);
        Assert.False(viewModel.CanImportEvidence);
        Assert.Equal(1, runNotifications);
        Assert.Equal(1, cancelNotifications);
        Assert.Equal(1, acceptNotifications);
        Assert.Equal(1, clearNotifications);
        Assert.Equal(1, navigateNotifications);
    }

    [Fact]
    public void DisposeRejectsDirectResetAndRuntimeNotifications()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel();
        var project = new MachineProjectDocument { Name = "Batch direct admission test" };
        var parentNotifications = 0;
        using var viewModel = CreateViewModel(
            workspace,
            project,
            () => true,
            () => false,
            _ => parentNotifications++);
        var propertyNotifications = 0;
        viewModel.PropertyChanged += (_, _) => propertyNotifications++;

        viewModel.Dispose();
        var parentNotificationsAfterDispose = parentNotifications;
        var propertyNotificationsAfterDispose = propertyNotifications;

        viewModel.Reset();
        viewModel.NotifyRuntimeChanged();

        Assert.Equal(parentNotificationsAfterDispose, parentNotifications);
        Assert.Equal(propertyNotificationsAfterDispose, propertyNotifications);
    }

    [Fact]
    public void OtherValidationBlocksBatchAdmission()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel();
        var project = new MachineProjectDocument { Name = "Batch admission test" };
        using var viewModel = CreateViewModel(
            workspace,
            project,
            () => true,
            () => true,
            _ => { });

        Assert.False(viewModel.CanRunScenarioBatch);
        Assert.False(viewModel.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task BatchCompletionUsesTheUiDispatchBoundary()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioTargetId = "axis-1",
            ScenarioDurationCycles = 3,
            BatchRepetitionCount = 2
        };
        var project = new MachineProjectDocument { Name = "Batch dispatch test" };
        var dispatchScope = false;
        var outsideDispatch = new List<string>();
        var propertyChangedOutsideDispatch = false;
        var parentNotificationsOutsideDispatch = false;
        using var viewModel = CreateViewModel(
            workspace,
            project,
            () => true,
            () => false,
            _ =>
            {
                if (!dispatchScope)
                {
                    parentNotificationsOutsideDispatch = true;
                }
            },
            action =>
            {
                var previousScope = dispatchScope;
                dispatchScope = true;
                try
                {
                    action();
                }
                finally
                {
                    dispatchScope = previousScope;
                }

                return Task.CompletedTask;
            },
            status =>
            {
                if (!dispatchScope)
                {
                    outsideDispatch.Add($"status:{status}");
                }
            },
            log =>
            {
                if (!dispatchScope)
                {
                    outsideDispatch.Add($"log:{log}");
                }
            },
            buildRuntime: CreateRuntime);
        viewModel.PropertyChanged += (_, _) =>
        {
            if (!dispatchScope)
            {
                propertyChangedOutsideDispatch = true;
            }
        };

        viewModel.RunCommand.Execute(null);
        var batchTask = viewModel.BatchTask;
        Assert.NotNull(batchTask);
        await batchTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(viewModel.LatestBatchResult?.IsComplete);
        Assert.True(viewModel.LatestBatchResult?.IsSuccess);
        Assert.False(viewModel.IsBatchRunning);
        Assert.Empty(outsideDispatch);
        Assert.False(propertyChangedOutsideDispatch);
        Assert.False(parentNotificationsOutsideDispatch);
    }

    [Fact]
    public async Task BatchCancellationUsesTheUiDispatchBoundary()
    {
        OpenVisionLanguageService.Load();
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioTargetId = "axis-1",
            ScenarioDurationCycles = 100_000,
            BatchRepetitionCount = 1
        };
        var project = new MachineProjectDocument { Name = "Batch cancellation dispatch test" };
        var dispatchScope = false;
        var outsideDispatch = new List<string>();
        var propertyChangedOutsideDispatch = false;
        using var viewModel = CreateViewModel(
            workspace,
            project,
            () => true,
            () => false,
            _ => { },
            action =>
            {
                var previousScope = dispatchScope;
                dispatchScope = true;
                try
                {
                    action();
                }
                finally
                {
                    dispatchScope = previousScope;
                }

                return Task.CompletedTask;
            },
            status =>
            {
                if (!dispatchScope)
                {
                    outsideDispatch.Add($"status:{status}");
                }
            },
            log =>
            {
                if (!dispatchScope)
                {
                    outsideDispatch.Add($"log:{log}");
                }
            },
            buildRuntime: CreateRuntime);
        viewModel.PropertyChanged += (_, _) =>
        {
            if (!dispatchScope)
            {
                propertyChangedOutsideDispatch = true;
            }
        };

        viewModel.RunCommand.Execute(null);
        var batchTask = viewModel.BatchTask;
        Assert.NotNull(batchTask);
        for (var attempt = 0; attempt < 100 && !viewModel.IsBatchRunning; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(viewModel.IsBatchRunning);
        viewModel.CancelCommand.Execute(null);
        await batchTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(viewModel.BatchWasCanceled);
        Assert.False(viewModel.IsBatchRunning);
        Assert.Empty(outsideDispatch);
        Assert.False(propertyChangedOutsideDispatch);
    }

    private static SimulationScenarioBatchViewModel CreateViewModel(
        SimulationWorkspaceViewModel workspace,
        MachineProjectDocument project,
        Func<bool> parentGate,
        Func<bool> otherValidationGate,
        Action<bool> notifyParent,
        Func<Action, Task>? dispatchToUi = null,
        Action<string>? setStatus = null,
        Action<string>? log = null,
        Func<SimulationRuntimeConfiguration>? buildRuntime = null) =>
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
            buildRuntime ?? (() => throw new InvalidOperationException("The batch runner is not part of this test.")),
            () => "{}",
            TimeSpan.FromMilliseconds(5),
            dispatchToUi ?? (action =>
            {
                action();
                return Task.CompletedTask;
            }),
            () => Array.Empty<SimulationScenarioTargetOption>(),
            () => { },
            setStatus ?? (_ => { }),
            log ?? (_ => { }),
            _ => { },
            notifyParent,
            _ => { });

    private static SimulationRuntimeConfiguration CreateRuntime() => new(
        [new AxisConfiguration { Id = "axis-1", Name = "Test axis" }],
        Array.Empty<ChannelDefinition>(),
        Array.Empty<CompiledSequence>());
}
