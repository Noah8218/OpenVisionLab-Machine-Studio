using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.Wpf.MessageDialogs;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class LayoutAuthoringWorkspaceTests
{
    [Fact]
    public void ConstructionAndResetDoNotEditTheProjectOrDispatchAnAction()
    {
        using var fixture = new Fixture();
        var before = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);

        fixture.Workspace.Reset();

        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
        Assert.Equal(0, fixture.MarkCount);
        Assert.Equal(0, fixture.RefreshCount);
        Assert.Equal(0, fixture.CommandInvalidationCount);
        Assert.Empty(fixture.StatusMessages);
        Assert.Empty(fixture.Logs);
        Assert.False(fixture.Workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.False(fixture.Workspace.RedoLayoutEditCommand.CanExecute(null));
        Assert.False(fixture.Workspace.PasteLayoutSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void AddUndoRedoAndDeleteUseOneSelectionAwareHistory()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;

        workspace.AddLayoutComponentCommand.Execute(LayoutComponentKind.LinearStage);

        var component = Assert.Single(fixture.Layout.Definition!.Components);
        var componentId = component.Id;
        var axisId = Assert.Single(fixture.Project.Axes).Id;
        Assert.Equal(componentId, fixture.Layout.SelectedItem?.Id);
        Assert.Equal(1, fixture.MarkCount);
        Assert.Equal(1, fixture.RefreshCount);
        Assert.Equal($"Added {component.Name}", fixture.StatusMessages[^1]);
        Assert.True(workspace.UndoLayoutEditCommand.CanExecute(null));

        workspace.UndoLayoutEditCommand.Execute(null);

        Assert.Empty(fixture.Layout.Definition!.Components);
        Assert.Empty(fixture.Project.Axes);
        Assert.Null(fixture.Layout.SelectedItem);
        Assert.True(workspace.RedoLayoutEditCommand.CanExecute(null));

        workspace.RedoLayoutEditCommand.Execute(null);

        Assert.Equal(componentId, Assert.Single(fixture.Layout.Definition!.Components).Id);
        Assert.Equal(axisId, Assert.Single(fixture.Project.Axes).Id);
        Assert.Equal(componentId, fixture.Layout.SelectedItem?.Id);
        Assert.True(workspace.DeleteLayoutComponentCommand.CanExecute(null));

        workspace.DeleteLayoutComponentCommand.Execute(null);

        Assert.Empty(fixture.Layout.Definition!.Components);
        Assert.Equal(axisId, Assert.Single(fixture.Project.Axes).Id);
        workspace.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(componentId, Assert.Single(fixture.Layout.Definition!.Components).Id);
        Assert.Equal(componentId, fixture.Layout.SelectedItem?.Id);
    }

    [Fact]
    public void RejectedPlacementDraftBlocksLayoutMutationsAndHistoryUntilRecovered()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        Assert.True(workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        var before = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);
        var selectedId = fixture.Layout.SelectedItem?.Id;
        fixture.ResolveDraft = false;

        Assert.False(workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        Assert.False(workspace.TryMoveSelectionBy(10, 0));
        workspace.AddLayoutComponentCommand.Execute(LayoutComponentKind.LinearStage);
        workspace.DeleteLayoutComponentCommand.Execute(null);
        workspace.UndoLayoutEditCommand.Execute(null);

        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
        Assert.Equal(selectedId, fixture.Layout.SelectedItem?.Id);
        fixture.ResolveDraft = true;
        workspace.UndoLayoutEditCommand.Execute(null);
        Assert.Empty(fixture.Layout.Definition!.Components);
    }

    [Fact]
    public void MultiSelectionRemovalShowsImpactsAndCancelPreservesOneUndoBoundary()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        var first = fixture.Layout.SelectedItem!.Component!;
        Assert.True(fixture.Workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        var second = fixture.Layout.SelectedItem!.Component!;
        fixture.Project.Sequences.Add(new SequenceDefinition
        {
            Id = "cycle",
            Name = "Cycle",
            Steps = [new SequenceStepDefinition
            {
                Id = "move",
                Name = "Move stage",
                Action = SequenceStepAction.MoveAxis,
                TargetId = first.BehaviorBindingId!
            }]
        });
        fixture.Layout.SelectMany([first.Id, second.Id], second.Id);
        fixture.Workspace.Reset();
        var before = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);
        var marks = fixture.MarkCount;
        fixture.ConfirmRemoval = false;

        Assert.True(fixture.Workspace.DeleteLayoutComponentCommand.CanExecute(null));
        fixture.Workspace.DeleteLayoutComponentCommand.Execute(null);

        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
        Assert.Equal(marks, fixture.MarkCount);
        Assert.Equal(2, fixture.Layout.SelectionCount);
        Assert.False(fixture.Workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.Equal([first.Id, second.Id], fixture.LastConfirmedIds);
        Assert.Contains(fixture.LastImpacts, impact =>
            impact.ComponentId == first.Id && impact.ReferenceKind == LayoutComponentRemovalReferenceKind.Target);

        var options = MainMessageDialogHost.CreateLayoutRemovalDialogOptions([first, second], fixture.LastImpacts);
        Assert.Equal(WpfMessageDialogResult.No, options.DefaultResult);
        Assert.Contains("cycle", options.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(first.Id, options.Message, StringComparison.Ordinal);

        fixture.ConfirmRemoval = true;
        fixture.Workspace.DeleteLayoutComponentCommand.Execute(null);

        Assert.Empty(fixture.Layout.Definition!.Components);
        Assert.Equal(marks + 1, fixture.MarkCount);
        Assert.Equal(2, fixture.ConfirmationCount);
        Assert.Equal(first.BehaviorBindingId, fixture.Project.Sequences[0].Steps[0].TargetId);
        fixture.Workspace.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
        Assert.Equal(2, fixture.Layout.SelectionCount);
    }

    [Fact]
    public void SceneDropSelectionAndGestureCommandsShareTheAuthoringHistory()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        workspace.SceneLibraryComponentDropRequestedCommand.Execute(new SceneLibraryComponentDropRequest(
            LayoutComponentKind.LinearStage, (45, 185)));
        var firstId = Assert.Single(fixture.Layout.Items).Id;
        Assert.True(workspace.TryAddComponent(LayoutComponentKind.LinearStage, 200, 200));
        var first = fixture.Layout.Items.Single(item => item.Id == firstId);
        var second = fixture.Layout.SelectedItem!;

        workspace.SceneMarqueeSelectionRequestedCommand.Execute(new SceneMarqueeSelectionRequest(
            [first], LayoutSelectionMode.Replace));
        workspace.SceneSelectionRequestedCommand.Execute(new SceneSelectionRequest(second, Toggle: true));

        Assert.Equal(2, fixture.Layout.SelectionCount);
        Assert.Same(second, fixture.Layout.SelectedItem);
        Assert.True(workspace.DeleteLayoutComponentCommand.CanExecute(null));
        Assert.True(workspace.AlignLayoutSelectionCommand.CanExecute("Left"));

        workspace.SceneMoveRequestedCommand.Execute(new SceneMoveRequest(SceneViewportMoveAction.Begin, default));
        workspace.SceneMoveRequestedCommand.Execute(new SceneMoveRequest(SceneViewportMoveAction.Update, (20, 10)));
        workspace.SceneMoveRequestedCommand.Execute(new SceneMoveRequest(SceneViewportMoveAction.Commit, default));

        Assert.Equal(70, first.CurrentX);
        Assert.Equal(200, first.CurrentY);
        Assert.Equal(220, second.CurrentX);
        workspace.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(50, fixture.Layout.Items.Single(item => item.Id == firstId).CurrentX);
        Assert.Equal(2, fixture.Layout.SelectionCount);
        Assert.Equal(second.Id, fixture.Layout.SelectedItem?.Id);

        var selected = fixture.Layout.SelectedItem!;
        var originalSize = (selected.CurrentWidth, selected.CurrentHeight);
        workspace.SceneTransformRequestedCommand.Execute(new SceneTransformRequest(
            SceneViewportMoveAction.Begin, LayoutTransformHandle.BottomRight, default, false));
        workspace.SceneTransformRequestedCommand.Execute(new SceneTransformRequest(
            SceneViewportMoveAction.Update, LayoutTransformHandle.BottomRight, (350, 350), false));
        workspace.SceneTransformRequestedCommand.Execute(new SceneTransformRequest(
            SceneViewportMoveAction.Cancel, LayoutTransformHandle.BottomRight, default, false));
        Assert.Equal(originalSize, (selected.CurrentWidth, selected.CurrentHeight));
    }

    [Fact]
    public void SelectionCommandsAndClipboardUseTheSameMutationBoundary()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        Assert.True(workspace.TryAddComponent(LayoutComponentKind.LinearStage, 50, 50));
        var originalId = fixture.Layout.SelectedItem!.Id;
        workspace.DuplicateLayoutSelectionCommand.Execute(null);
        var copyId = fixture.Layout.SelectedItem!.Id;
        Assert.NotEqual(originalId, copyId);
        Assert.Equal(2, fixture.Project.Axes.Count);

        fixture.Layout.SelectMany([originalId, copyId], originalId);
        workspace.AlignLayoutSelectionCommand.Execute("Left");
        Assert.All(fixture.Layout.Items, item => Assert.Equal(50, item.CurrentX));
        workspace.NudgeLayoutComponentCommand.Execute("Right");
        Assert.All(fixture.Layout.Items, item => Assert.Equal(60, item.CurrentX));

        fixture.Layout.Select(originalId);
        Assert.True(workspace.ChangeLayoutLayerOrderCommand.CanExecute("BringToFront"));
        workspace.ChangeLayoutLayerOrderCommand.Execute("BringToFront");
        Assert.True(fixture.Layout.Items.Single(item => item.Id == originalId).Component!.ZIndex >
                    fixture.Layout.Items.Single(item => item.Id == copyId).Component!.ZIndex);

        var beforeReset = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);
        var markCount = fixture.MarkCount;
        workspace.Reset();
        Assert.Equal(beforeReset, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
        Assert.Equal(markCount, fixture.MarkCount);
        Assert.Equal(originalId, fixture.Layout.SelectedItem?.Id);
        Assert.False(workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.False(workspace.PasteLayoutSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void CameraPlacementCanBeCopiedAndPastedWithAnIndependentDeviceBinding()
    {
        using var fixture = new Fixture();
        fixture.Project.Devices.Add(new DeviceDefinition
        {
            Id = "device.camera-1",
            Name = "Top camera",
            Kind = DeviceKind.Camera,
            Camera = new VirtualCameraDefinition()
        });

        Assert.True(fixture.Workspace.TryAddComponent(LayoutComponentKind.Camera));
        var original = Assert.IsType<LayoutComponentDefinition>(fixture.Layout.SelectedItem!.Component);
        fixture.Workspace.CopyLayoutSelectionCommand.Execute(null);
        Assert.True(fixture.Workspace.PasteLayoutSelectionCommand.CanExecute(null));
        fixture.Workspace.PasteLayoutSelectionCommand.Execute(null);

        var components = fixture.Layout.Items
            .Where(item => item.Kind == LayoutItemKind.Camera)
            .Select(item => item.Component!)
            .ToArray();
        Assert.Equal(2, components.Length);
        Assert.NotEqual(original.Id, components[1].Id);
        Assert.NotEqual(original.BehaviorBindingId, components[1].BehaviorBindingId);
        Assert.Equal(2, fixture.Project.Devices.Count(device => device.Kind == DeviceKind.Camera));
    }

    [Fact]
    public void ResetNotifiesHistoryCommandStateAfterClearingHistory()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        Assert.True(workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        var invalidations = 0;
        workspace.UndoLayoutEditCommand.CanExecuteChanged += (_, _) => invalidations++;

        Assert.True(workspace.UndoLayoutEditCommand.CanExecute(null));
        workspace.Reset();

        Assert.False(workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.True(invalidations > 0);
    }

    [Fact]
    public void RejectedMutationAndUnexpectedRequestsLeaveTheProjectUnchanged()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        var before = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);

        Assert.False(workspace.TryAddComponent(LayoutComponentKind.DigitalSensor));
        Assert.Equal(OpenVisionLanguageService.T("Layout.Add.SensorTargetRequired"), fixture.StatusMessages[^1]);
        Assert.False(workspace.TryAddComponent(LayoutComponentKind.LinearStage, double.NaN, 10));
        Assert.False(workspace.TryAddComponent(LayoutComponentKind.LinearStage, 10, null));
        workspace.AddLayoutComponentCommand.Execute("LinearStage");
        workspace.SceneSelectionRequestedCommand.Execute(new object());
        workspace.SceneMoveRequestedCommand.Execute(new object());
        workspace.SceneMarqueeSelectionRequestedCommand.Execute(new object());
        workspace.SceneTransformRequestedCommand.Execute(new object());
        workspace.SceneLibraryComponentDropRequestedCommand.Execute(new object());

        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
        Assert.Equal(0, fixture.MarkCount);
        Assert.Equal(0, fixture.RefreshCount);
        Assert.False(workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.False(workspace.DeleteLayoutComponentCommand.CanExecute(null));
        Assert.False(workspace.ChangeLayoutLayerOrderCommand.CanExecute("invalid"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EditingAdmissionAppliesToMutationAndHistoryCommands(bool editable, bool applyingProject)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        fixture.IsEditable = editable;
        fixture.IsApplyingProject = applyingProject;

        Assert.False(fixture.Workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        Assert.False(fixture.Workspace.AddLayoutComponentCommand.CanExecute(null));
        Assert.False(fixture.Workspace.DeleteLayoutComponentCommand.CanExecute(null));
        Assert.False(fixture.Workspace.NudgeLayoutComponentCommand.CanExecute("Right"));
        Assert.False(fixture.Workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.False(fixture.Workspace.CopyLayoutSelectionCommand.CanExecute(null));
        Assert.Single(fixture.Layout.Items);
    }

    [Fact]
    public void SessionAdmissionPreservesExistingSceneAndHistoryGateDifferences()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        Assert.True(workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        var invalidations = 0;
        workspace.AddLayoutComponentCommand.CanExecuteChanged += (_, _) => invalidations++;
        fixture.CanExecuteSessionCommand = false;
        workspace.InvalidateCommands();

        Assert.False(workspace.AddLayoutComponentCommand.CanExecute(null));
        Assert.False(workspace.DeleteLayoutComponentCommand.CanExecute(null));
        Assert.False(workspace.NudgeLayoutComponentCommand.CanExecute("Right"));
        Assert.False(workspace.SceneSelectionRequestedCommand.CanExecute(null));
        Assert.False(workspace.SceneMarqueeSelectionRequestedCommand.CanExecute(null));
        Assert.False(workspace.SceneMoveRequestedCommand.CanExecute(null));
        Assert.False(workspace.SceneTransformRequestedCommand.CanExecute(null));
        Assert.False(workspace.SceneLibraryComponentDropRequestedCommand.CanExecute(null));
        Assert.True(workspace.UndoLayoutEditCommand.CanExecute(null));
        Assert.True(workspace.CopyLayoutSelectionCommand.CanExecute(null));
        Assert.Equal(1, invalidations);

        fixture.CanExecuteSessionCommand = true;
        workspace.InvalidateCommands();
        Assert.True(workspace.AddLayoutComponentCommand.CanExecute(null));
        Assert.True(workspace.SceneSelectionRequestedCommand.CanExecute(null));
        Assert.Equal(2, invalidations);
    }

    [Fact]
    public void DisposalUnsubscribesHistoryWithoutDisposingTheSharedLayout()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Workspace.TryAddComponent(LayoutComponentKind.LinearStage, 50, 50));
        var marks = fixture.MarkCount;
        var invalidations = fixture.CommandInvalidationCount;
        fixture.Workspace.Dispose();
        fixture.Workspace.Dispose();

        Assert.True(fixture.Layout.NudgeSelection("Right"));

        Assert.Equal(60, fixture.Layout.SelectedItem!.CurrentX);
        Assert.Equal(marks, fixture.MarkCount);
        Assert.Equal(invalidations, fixture.CommandInvalidationCount);
        Assert.Equal(0, fixture.DefinitionChangedCount);
    }

    [Fact]
    public void DisposalDisablesHistoryCommandsAndIgnoresDirectExecution()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Workspace.TryAddComponent(LayoutComponentKind.LinearStage));
        var before = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);
        var undoCommand = fixture.Workspace.UndoLayoutEditCommand;
        var copyCommand = fixture.Workspace.CopyLayoutSelectionCommand;
        var pasteCommand = fixture.Workspace.PasteLayoutSelectionCommand;
        var invalidations = 0;
        undoCommand.CanExecuteChanged += (_, _) => invalidations++;

        fixture.Workspace.Dispose();

        Assert.False(undoCommand.CanExecute(null));
        Assert.False(copyCommand.CanExecute(null));
        Assert.False(pasteCommand.CanExecute(null));
        Assert.True(invalidations > 0);

        undoCommand.Execute(null);
        copyCommand.Execute(null);
        pasteCommand.Execute(null);

        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(fixture.Project));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            var definition = new MachineLayoutDefinition
            {
                Id = "main-cell",
                Name = "Main Cell",
                GridSize = 10,
                SnapToGrid = true
            };
            Project.Layouts.Add(definition);
            Project.Simulation.ActiveLayoutId = definition.Id;
            Layout.Load(Project);
            Workspace = new LayoutAuthoringWorkspace(
                Layout,
                () => Project,
                () => null,
                () => IsEditable,
                () => IsApplyingProject,
                () => CanExecuteSessionCommand,
                () => MarkCount++,
                () => { },
                RefreshDefinition,
                () => CommandInvalidationCount++,
                StatusMessages.Add,
                (category, message) => Logs.Add((category, message)),
                () => DefinitionChangedCount++,
                (components, impacts) =>
                {
                    ConfirmationCount++;
                    LastConfirmedIds = components.Select(component => component.Id).ToArray();
                    LastImpacts = impacts.ToArray();
                    return ConfirmRemoval;
                },
                () => ResolveDraft);
            Workspace.Reset();
        }

        public MachineProjectDocument Project { get; } = new() { Name = "Layout workspace" };
        public MachineLayoutViewModel Layout { get; } = new();
        public LayoutAuthoringWorkspace Workspace { get; }
        public bool IsEditable { get; set; } = true;
        public bool IsApplyingProject { get; set; }
        public bool CanExecuteSessionCommand { get; set; } = true;
        public bool ConfirmRemoval { get; set; } = true;
        public bool ResolveDraft { get; set; } = true;
        public int ConfirmationCount { get; private set; }
        public IReadOnlyList<string> LastConfirmedIds { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<LayoutComponentRemovalImpact> LastImpacts { get; private set; } = Array.Empty<LayoutComponentRemovalImpact>();
        public int MarkCount { get; private set; }
        public int RefreshCount { get; private set; }
        public int CommandInvalidationCount { get; private set; }
        public int DefinitionChangedCount { get; private set; }
        public List<string> StatusMessages { get; } = [];
        public List<(string Category, string Message)> Logs { get; } = [];

        private void RefreshDefinition(string? selectedId)
        {
            RefreshCount++;
            Layout.Load(Project);
            if (selectedId is not null)
            {
                Layout.Select(selectedId);
            }
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Layout.Dispose();
        }
    }
}
