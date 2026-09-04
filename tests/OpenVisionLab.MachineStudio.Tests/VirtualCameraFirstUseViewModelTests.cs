using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class VirtualCameraFirstUseViewModelTests
{
    [Fact]
    public void CreateCompleteCameraSetupIsAtomicDirtyAndDoesNotRun()
    {
        using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Untitled" });
        var runtimeBefore = viewModel.SceneSnapshots.Latest;
        var ownerBefore = viewModel.ControlOwnerText;

        Assert.True(viewModel.RecipeConnections.CanCreateVirtualCameraWorkflow);
        Assert.True(viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null));

        viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.Execute(null);

        var project = Assert.IsType<MachineProjectDocument>(
            viewModel.ProjectTree.Roots.Single().Model);
        var camera = Assert.Single(project.Devices, device => device.Kind == DeviceKind.Camera);
        var sequence = Assert.Single(project.Sequences);
        var trigger = Assert.Single(sequence.Steps, step => step.Action == SequenceStepAction.TriggerCamera);
        var wait = Assert.Single(sequence.Steps, step => step.Action == SequenceStepAction.WaitVisionResult);

        Assert.Equal("default", trigger.Parameter);
        Assert.Equal(camera.Id, trigger.TargetId);
        Assert.Equal(camera.Id, wait.TargetId);
        Assert.Equal(trigger.NextStepId, wait.Id);
        Assert.NotNull(wait.FailureStepId);
        Assert.Equal(4, sequence.Steps.Count);
        Assert.Equal(camera.Id, viewModel.SelectedCameraId);
        Assert.Equal("default", viewModel.SelectedCameraRecipe);
        Assert.Equal(2, viewModel.SelectedDocumentTabIndex);
        Assert.Equal(sequence.Id, viewModel.SequenceEditor.SelectedSequence?.Id);
        Assert.Equal(trigger.Id, viewModel.SequenceEditor.SelectedStep?.Id);
        Assert.Equal(
            sequence.Steps.Select(step => (sequence.Id, step.Id)),
            viewModel.RuntimeDebugger.Breakpoints.Select(item => (item.SequenceId, item.StepId)));
        Assert.All(viewModel.RuntimeDebugger.Breakpoints, item => Assert.False(item.IsEnabled));
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.EndsWith(" *", viewModel.Title, StringComparison.Ordinal);
        Assert.True(viewModel.IsDesignMode);
        Assert.False(viewModel.IsRunning);
        Assert.Equal(ownerBefore, viewModel.ControlOwnerText);
        Assert.Equal(runtimeBefore?.TickIndex, viewModel.SceneSnapshots.Latest?.TickIndex);
        Assert.Equal(runtimeBefore?.SimulationTime, viewModel.SceneSnapshots.Latest?.SimulationTime);
        Assert.Empty(viewModel.SceneSnapshots.Latest?.Cameras ?? []);
        Assert.Null(project.Simulation.AutomaticRun);
        Assert.False(viewModel.RecipeConnections.CanCreateVirtualCameraWorkflow);
        Assert.False(viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null));

        var evidenceBeforeSecondInvocation = new ProjectDocumentStore().SerializeForEvidence(project);
        viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.Execute(null);

        Assert.Equal(evidenceBeforeSecondInvocation, new ProjectDocumentStore().SerializeForEvidence(project));
        Assert.Single(project.Devices, device => device.Kind == DeviceKind.Camera);
        Assert.Single(project.Sequences);
    }

    [Fact]
    public async Task SaveAndReopenRestoresCameraRecipeAndBranchesWithoutAcquisition()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ovl-camera-first-use-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "camera-first-use.ovmachine");

        try
        {
            var store = new ProjectDocumentStore();
            string authoredCameraId;
            string authoredSequenceId;
            using (var authoringViewModel = new MainViewModel(
                       new MachineProjectDocument { Name = "Untitled" }))
            {
                authoringViewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.Execute(null);
                var authored = Assert.IsType<MachineProjectDocument>(
                    authoringViewModel.ProjectTree.Roots.Single().Model);
                authoredCameraId = Assert.Single(
                    authored.Devices,
                    device => device.Kind == DeviceKind.Camera).Id;
                authoredSequenceId = Assert.Single(authored.Sequences).Id;
                await store.SaveAsync(authored, projectPath);
            }

            var reopenedDocument = await store.LoadAsync(projectPath);
            using var viewModel = new MainViewModel(reopenedDocument, projectPath);
            await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Cameras.Count == 1);

            var reopened = Assert.IsType<MachineProjectDocument>(
                viewModel.ProjectTree.Roots.Single().Model);
            var camera = Assert.Single(reopened.Devices, device => device.Kind == DeviceKind.Camera);
            var sequence = Assert.Single(reopened.Sequences);
            var trigger = Assert.Single(sequence.Steps, step => step.Action == SequenceStepAction.TriggerCamera);
            var wait = Assert.Single(sequence.Steps, step => step.Action == SequenceStepAction.WaitVisionResult);
            var pass = Assert.Single(sequence.Steps, step => step.Id == wait.NextStepId);
            var fail = Assert.Single(sequence.Steps, step => step.Id == wait.FailureStepId);

            Assert.Equal(authoredCameraId, camera.Id);
            Assert.Equal(authoredSequenceId, sequence.Id);
            Assert.Equal("default", trigger.Parameter);
            Assert.Equal(SequenceStepAction.Complete, pass.Action);
            Assert.Equal(SequenceStepAction.Complete, fail.Action);
            Assert.Equal(fail.Id, trigger.ErrorStepId);
            Assert.Equal(fail.Id, wait.ErrorStepId);
            Assert.Equal(camera.Id, viewModel.SelectedCameraId);
            Assert.Equal("default", viewModel.SelectedCameraRecipe);
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.HasUnsavedChanges);

            var runtimeCamera = Assert.Single(viewModel.SceneSnapshots.Latest?.Cameras ?? []);
            Assert.Equal(camera.Id, runtimeCamera.Id);
            Assert.Equal(VirtualCameraState.Idle, runtimeCamera.State);
            Assert.Equal(0L, runtimeCamera.AcquisitionOrdinal);
            Assert.Null(runtimeCamera.CurrentAcquisitionId);
            Assert.Null(runtimeCamera.CurrentRecipeId);
            Assert.Null(runtimeCamera.Result);
            Assert.Null(runtimeCamera.FrameEvidence);
            Assert.Equal(0L, viewModel.SceneSnapshots.Latest?.TickIndex);
            Assert.Equal(TimeSpan.Zero, viewModel.SceneSnapshots.Latest?.SimulationTime);
            Assert.False(viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateCameraSetupClearsStaleLayoutUndoThatCouldReplaceDevices()
    {
        using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Partial" });
        Assert.True(viewModel.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
        Assert.True(viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.Execute(null);

        var project = Assert.IsType<MachineProjectDocument>(
            viewModel.ProjectTree.Roots.Single().Model);
        Assert.False(viewModel.UndoLayoutEditCommand.CanExecute(null));
        Assert.Single(project.Devices, device => device.Kind == DeviceKind.Camera);
        Assert.Single(project.Layouts.SelectMany(layout => layout.Components));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The reopened camera runtime did not become observable.");
    }
}
