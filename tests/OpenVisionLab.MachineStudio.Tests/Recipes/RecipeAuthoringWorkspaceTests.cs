using System.ComponentModel;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeAuthoringWorkspaceTests
{
    [Fact]
    public void ConstructionAndLoadingDoNotMutateOrExecuteTheProject()
    {
        var project = LoadRecipe();
        var before = Serialize(project);
        using var host = new AuthoringHost(project);

        Assert.Empty(host.Calls);
        Assert.False(host.Workspace.Playback.IsActive);
        Assert.False(host.Workspace.ProcessPlanReview.HasReturnContext);
        host.Load();

        Assert.Empty(host.Calls);
        Assert.Equal(before, Serialize(project));
        Assert.NotEmpty(host.Workspace.Connections.Rows);
        Assert.False(host.Workspace.Connections.DryRun.IsRecipeDryRunRunning);
    }

    [Fact]
    public void StationSetupOwnsApplyCompletionAndRepeatedApplyIsNoOp()
    {
        using var host = new AuthoringHost(new MachineProjectDocument());
        host.Load();
        var setup = host.Workspace.Connections.StationSetups;
        var before = Serialize(host.Project);

        setup.PreviewStationSkeletonCommand.Execute(null);
        Assert.Equal(before, Serialize(host.Project));
        Assert.Empty(host.Calls);
        Assert.True(setup.ApplyStationSkeletonCommand.CanExecute(null));
        setup.ApplyStationSkeletonCommand.Execute(null);

        Assert.NotEmpty(host.Project.Layouts.Single().Components);
        Assert.Equal(new[] { "dirty", "availability", "refresh", "history", "invalidate" }, host.Calls);
        Assert.NotEmpty(host.Workspace.Connections.Rows);
        Assert.False(host.Workspace.Playback.IsActive);

        var applied = Serialize(host.Project);
        host.Calls.Clear();
        setup.PreviewStationSkeletonCommand.Execute(null);
        setup.ApplyStationSkeletonCommand.Execute(null);
        Assert.Equal(applied, Serialize(host.Project));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ProcessPlanApplyReloadsSequenceAndCanNavigateBackFromReview()
    {
        using var host = new AuthoringHost(LoadRecipe());
        host.Load();
        var blocks = host.Workspace.Connections.ProcessBlocks;
        blocks.PreviewProcessBlockCommand.Execute(null);
        blocks.IsLoadBlockSelected = true;
        blocks.IsAlignBlockSelected = true;
        blocks.IsProcessBlockSelected = true;
        blocks.IsInspectBlockSelected = true;
        blocks.IsUnloadBlockSelected = true;
        Assert.True(blocks.ApplyProcessBlockCommand.CanExecute(null));

        blocks.ApplyProcessBlockCommand.Execute(null);

        Assert.Equal(new[] { "dirty", "availability", "refresh", "history", "invalidate" }, host.Calls);
        Assert.Equal(host.Project.Sequences[0].Steps.Count, host.Editor.Steps.Count);
        blocks.PreviewProcessBlockCommand.Execute(null);
        var item = blocks.VisibleProcessBlockItems.First(step => step.CanOpenSequenceStep);
        host.Workspace.Connections.OpenSequenceStepCommand.Execute(item);

        Assert.Equal(item.StepId, host.Editor.SelectedStep?.Id);
        Assert.Equal(2, host.SelectedTab);
        Assert.True(host.Workspace.ProcessPlanReview.HasReturnContext);
        Assert.True(host.Workspace.ProcessPlanReview.ReturnToProcessPlanCommand.CanExecute(null));
        host.Workspace.ProcessPlanReview.ReturnToProcessPlanCommand.Execute(null);
        Assert.Equal(1, host.SelectedTab);
        Assert.Equal(item.StepId, blocks.SelectedProcessBlockItem?.StepId);

        blocks.CancelProcessBlockCommand.Execute(null);
        Assert.False(host.Workspace.ProcessPlanReview.HasReturnContext);
    }

    [Fact]
    public void TimeoutApplyPreservesPlanReviewAndDoesNotResetLayoutHistory()
    {
        using var host = new AuthoringHost(LoadProcessPlan());
        host.Load();
        var blocks = host.Workspace.Connections.ProcessBlocks;
        blocks.PreviewProcessBlockCommand.Execute(null);
        var item = blocks.VisibleProcessBlockItems.First(step => step.CanAdjustTimeout);
        host.Workspace.Connections.OpenSequenceStepCommand.Execute(item);
        var stepIds = blocks.VisibleProcessBlockItems.Where(step => step.CanAdjustTimeout)
            .Select(step => step.StepId).ToHashSet();
        var before = Serialize(host.Project);
        blocks.ProcessBlockTimeoutText = "8123";
        blocks.PreviewProcessBlockTimeoutsCommand.Execute(null);
        Assert.Equal(before, Serialize(host.Project));
        Assert.True(blocks.ApplyProcessBlockTimeoutsCommand.CanExecute(null));

        blocks.ApplyProcessBlockTimeoutsCommand.Execute(null);

        Assert.All(host.Project.Sequences.SelectMany(sequence => sequence.Steps)
            .Where(step => stepIds.Contains(step.Id)), step => Assert.Equal(8123, step.TimeoutMs));
        Assert.Equal(new[] { "dirty", "availability", "invalidate" }, host.Calls);
        Assert.True(blocks.IsProcessBlockPreviewVisible);
        Assert.Equal(item.StepId, host.Workspace.ProcessPlanReview.ReturnStepId);
        Assert.True(host.Workspace.ProcessPlanReview.ReturnToProcessPlanCommand.CanExecute(null));
    }

    [Fact]
    public void CheckpointApplyMutatesOnlyAfterExplicitApplyAndRefreshesTargets()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        foreach (var step in project.Sequences.SelectMany(sequence => sequence.Steps))
        {
            step.ExpectedTargetId = null;
            step.ExpectedState = null;
        }
        using var host = new AuthoringHost(project);
        host.Load();
        var checkpoint = host.Workspace.Connections.CheckpointTemplate;
        var before = Serialize(project);
        checkpoint.PreviewCommand.Execute(null);
        Assert.Equal(before, Serialize(project));
        Assert.True(checkpoint.ApplyCommand.CanExecute(null));

        checkpoint.ApplyCommand.Execute(null);

        Assert.Equal(5, project.Sequences.SelectMany(sequence => sequence.Steps).Count(step =>
            !string.IsNullOrWhiteSpace(step.ExpectedTargetId) && !string.IsNullOrWhiteSpace(step.ExpectedState)));
        Assert.Equal(new[] { "dirty", "availability", "invalidate" }, host.Calls);
        checkpoint.PreviewCommand.Execute(null);
        Assert.False(checkpoint.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReadinessAndDryRunUseIsolatedExecutionAndPlaybackRestoresEditing()
    {
        using var host = new AuthoringHost(LoadTransferCell());
        host.Load();
        var connections = host.Workspace.Connections;
        var before = Serialize(host.Project);
        connections.ValidateSimulationReadinessCommand.Execute(null);
        Assert.True(connections.RunRecipeDryRunCommand.CanExecute(null));
        await RunDryRunAsync(connections.DryRun);
        Assert.NotEmpty(connections.DryRun.Timeline);
        Assert.Equal(before, Serialize(host.Project));
        Assert.Equal(new[] { "validate" }, host.Calls);

        var step = connections.DryRun.Timeline[0];
        connections.PlayRecipeDryRunStepCommand.Execute(step);
        Assert.True(host.Workspace.Playback.IsActive);
        Assert.False(host.Layout.IsEditable);
        Assert.Equal(0, host.SelectedTab);
        Assert.Same(step.BoundarySnapshot, host.Workspace.Playback.PlaybackSnapshots.Latest);
        host.Workspace.Playback.NextStepCommand.Execute(null);
        Assert.Same(connections.DryRun.Timeline[1], host.Workspace.Playback.CurrentStep);
        Assert.Same(host.Workspace.Playback.CurrentStep, connections.SelectedRecipeDryRunStep);

        host.Workspace.ExitPlayback();
        Assert.False(host.Workspace.Playback.IsActive);
        Assert.True(host.Layout.IsEditable);
        Assert.Equal(before, Serialize(host.Project));
    }

    [Fact]
    public void CameraWorkflowUsesTheCurrentProjectAndKeepsRepeatedCreationInert()
    {
        var previous = new MachineProjectDocument { Name = "Previous" };
        using var host = new AuthoringHost(previous);
        host.Load();
        var previousBefore = Serialize(previous);
        host.Project = new MachineProjectDocument { Name = "Replacement" };
        host.Workspace.ResetPresentation();
        host.Load();

        host.Workspace.Connections.CreateVirtualCameraWorkflowCommand.Execute(null);

        var camera = Assert.Single(host.Project.Devices, device => device.Kind == DeviceKind.Camera);
        Assert.Equal(camera.Id, host.SelectedCameraId);
        Assert.Equal(camera.Id, host.Editor.SelectedStep?.TargetId);
        Assert.Equal(2, host.SelectedTab);
        Assert.Equal(new[] { "dirty", "availability", "camera", "invalidate" }, host.Calls);
        Assert.Equal(previousBefore, Serialize(previous));
        Assert.False(host.Workspace.Connections.CreateVirtualCameraWorkflowCommand.CanExecute(null));
        var applied = Serialize(host.Project);
        host.Calls.Clear();
        host.Workspace.Connections.CreateVirtualCameraWorkflowCommand.Execute(null);
        Assert.Equal(applied, Serialize(host.Project));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SequenceNavigationRejectsMissingStepsWithoutSwitchingTabs()
    {
        using var host = new AuthoringHost(LoadTransferCell());
        host.Load();
        var row = host.Workspace.Connections.Rows.First(item => item.HasSequenceUse);
        host.Workspace.Connections.OpenSequenceStepCommand.Execute(row);
        Assert.Equal(row.FirstSequenceStepId, host.Editor.SelectedStep?.Id);
        Assert.Equal(2, host.SelectedTab);

        host.SelectedTab = 1;
        host.Workspace.ProcessPlanReview.OpenProcessBlockSequenceStep("missing-sequence", "missing-step");
        Assert.Equal(1, host.SelectedTab);
        Assert.False(host.Workspace.ProcessPlanReview.HasReturnContext);
        Assert.Equal(OpenVisionLanguageService.T("Connections.OpenStepRejectedStatus"), host.Status);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ReadOnlyAdmissionKeepsAuthoringCommandsDisabled()
    {
        using var host = new AuthoringHost(new MachineProjectDocument());
        host.Load();
        var connections = host.Workspace.Connections;
        connections.IsEditable = false;
        host.Editor.IsEditable = false;

        Assert.False(connections.CreateVirtualCameraWorkflowCommand.CanExecute(null));
        Assert.False(connections.StationSetups.PreviewStationSkeletonCommand.CanExecute(null));
        Assert.False(connections.ProcessBlocks.PreviewProcessBlockCommand.CanExecute(null));
        Assert.False(connections.CheckpointTemplate.PreviewCommand.CanExecute(null));
        Assert.False(connections.ValidateSimulationReadinessCommand.CanExecute(null));
        Assert.False(connections.RunRecipeDryRunCommand.CanExecute(null));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CloseDetachesReviewBeforeDeferredDisposalAndLeavesBorrowedEditorsUsable()
    {
        using var host = new AuthoringHost(LoadProcessPlan());
        host.Load();
        var blocks = host.Workspace.Connections.ProcessBlocks;
        blocks.PreviewProcessBlockCommand.Execute(null);
        var item = blocks.VisibleProcessBlockItems.First(step => step.CanOpenSequenceStep);
        host.Workspace.Connections.OpenSequenceStepCommand.Execute(item);
        Assert.True(host.Workspace.ProcessPlanReview.HasReturnContext);

        host.Workspace.DetachEventHandlers();
        blocks.CancelProcessBlockCommand.Execute(null);
        Assert.True(host.Workspace.ProcessPlanReview.HasReturnContext);
        host.Workspace.Dispose();
        host.Workspace.Dispose();

        Assert.False(host.Workspace.Connections.RunRecipeDryRunCommand.CanExecute(null));
        Assert.False(host.Workspace.Connections.CreateVirtualCameraWorkflowCommand.CanExecute(null));
        host.Editor.Load(host.Project);
        host.Layout.Load(host.Project);
        Assert.NotEmpty(host.Editor.Steps);
        Assert.NotEmpty(host.Layout.Items);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ConnectionStepAdditionUsesEditorChangeEventsAndRejectsReadOnlyExecution()
    {
        using var host = new AuthoringHost(new MachineProjectDocument());
        host.Load();
        var connections = host.Workspace.Connections;
        connections.StationSetups.PreviewStationSkeletonCommand.Execute(null);
        connections.StationSetups.ApplyStationSkeletonCommand.Execute(null);
        host.Load();
        host.Calls.Clear();
        var previousStepCount = host.Project.Sequences[0].Steps.Count;
        var row = connections.Rows.First(item => item.CanAddSequenceStep);
        var changes = 0;
        host.Editor.DefinitionChanged += (_, _) => changes++;
        Assert.True(connections.AddSequenceStepCommand.CanExecute(row));

        connections.AddSequenceStepCommand.Execute(row);

        Assert.Equal(1, changes);
        Assert.Equal(row.SequenceTargetId, host.Editor.SelectedStep?.TargetId);
        Assert.Equal(previousStepCount + 1, host.Project.Sequences[0].Steps.Count);
        Assert.Equal(2, host.SelectedTab);
        var applied = Serialize(host.Project);
        host.Editor.IsEditable = false;
        host.SelectedTab = 1;
        connections.AddSequenceStepCommand.Execute(row);
        Assert.Equal(applied, Serialize(host.Project));
        Assert.Equal(1, changes);
        Assert.Equal(1, host.SelectedTab);
        Assert.Contains(host.Editor.StructuralEditStatus, host.Status!);
    }

    [Fact]
    public async Task ProjectReplacementClearsPlaybackAndReviewWithoutApplyingOrRunning()
    {
        using var host = new AuthoringHost(LoadTransferCell());
        host.Load();
        var connections = host.Workspace.Connections;
        connections.ValidateSimulationReadinessCommand.Execute(null);
        await RunDryRunAsync(connections.DryRun);
        connections.PlayRecipeDryRunStepCommand.Execute(connections.DryRun.Timeline[0]);
        Assert.True(host.Workspace.Playback.IsActive);
        var previous = Serialize(host.Project);
        var previousProject = host.Project;
        host.Project = LoadProcessPlan();
        host.Calls.Clear();

        host.Workspace.ResetPresentation();
        host.Load();

        Assert.False(host.Workspace.Playback.IsActive);
        Assert.Null(host.Workspace.Playback.CurrentStep);
        Assert.False(host.Workspace.ProcessPlanReview.HasReturnContext);
        Assert.True(host.Layout.IsEditable);
        Assert.False(connections.DryRun.IsRecipeDryRunRunning);
        Assert.Empty(connections.DryRun.Timeline);
        Assert.Empty(host.Calls);
        Assert.Equal(previous, Serialize(previousProject));
    }

    private static async Task RunDryRunAsync(RecipeDryRunViewModel dryRun)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(RecipeDryRunViewModel.IsRecipeDryRunRunning) && !dryRun.IsRecipeDryRunRunning)
            {
                completed.TrySetResult(true);
            }
        }
        dryRun.PropertyChanged += OnChanged;
        try
        {
            dryRun.RunRecipeDryRunCommand.Execute(null);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            dryRun.PropertyChanged -= OnChanged;
        }
    }

    private static string Serialize(MachineProjectDocument project) => new ProjectDocumentStore().SerializeForEvidence(project);

    private static MachineProjectDocument LoadRecipe(string name = "10-MetrologySorter.ovmachine") =>
        new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", name)));

    private static MachineProjectDocument LoadTransferCell() =>
        new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));

    private static MachineProjectDocument LoadProcessPlan()
    {
        var project = LoadRecipe();
        new RecipeConnectionProjectApplier().ApplyProcessBlocks(project, Enum.GetValues<SemiconductorProcessBlockKind>());
        return project;
    }

    private sealed class AuthoringHost : IDisposable
    {
        public AuthoringHost(MachineProjectDocument project)
        {
            OpenVisionLanguageService.Load();
            Project = project;
            Workspace = new RecipeAuthoringWorkspace(
                Layout,
                Editor,
                () => Project,
                () => true,
                Validate,
                value => SelectedTab = value,
                () => Calls.Add("dirty"),
                () => Calls.Add("availability"),
                RefreshDefinition,
                () => Calls.Add("history"),
                RefreshCamera,
                () => Calls.Add("invalidate"),
                value => Status = value,
                (_, _) => { });
        }

        public MachineProjectDocument Project { get; set; }
        public MachineLayoutViewModel Layout { get; } = new();
        public SequenceEditorViewModel Editor { get; } = new();
        public RecipeAuthoringWorkspace Workspace { get; }
        public List<string> Calls { get; } = [];
        public int SelectedTab { get; set; } = -1;
        public string? SelectedCameraId { get; private set; }
        public string? Status { get; private set; }

        public void Load()
        {
            Layout.Load(Project);
            Workspace.Connections.Load(Project);
            Editor.Load(Project);
        }

        private void Validate(MachineProjectDocument project)
        {
            Calls.Add("validate");
            Assert.True(new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project).IsSuccess);
        }

        private void RefreshDefinition()
        {
            Calls.Add("refresh");
            Layout.Load(Project);
            Workspace.Connections.Load(Project);
        }

        private void RefreshCamera(string cameraId)
        {
            Calls.Add("camera");
            Workspace.ResetPresentation();
            Load();
            SelectedCameraId = cameraId;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Editor.Dispose();
            Layout.Dispose();
        }
    }
}
