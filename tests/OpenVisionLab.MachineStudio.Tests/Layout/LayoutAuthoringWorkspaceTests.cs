using System.Windows;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
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
    public void SceneDropSelectionAndGestureCommandsShareTheAuthoringHistory()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        workspace.SceneLibraryComponentDropRequestedCommand.Execute(new SceneLibraryComponentDropRequest(
            LayoutComponentKind.LinearStage, new Point(45, 185)));
        var firstId = Assert.Single(fixture.Layout.Items).Id;
        Assert.True(workspace.TryAddComponent(LayoutComponentKind.LinearStage, 200, 200));
        var first = fixture.Layout.Items.Single(item => item.Id == firstId);
        var second = fixture.Layout.SelectedItem!;

        workspace.SceneMarqueeSelectionRequestedCommand.Execute(new SceneMarqueeSelectionRequest(
            [first], LayoutSelectionMode.Replace));
        workspace.SceneSelectionRequestedCommand.Execute(new SceneSelectionRequest(second, Toggle: true));

        Assert.Equal(2, fixture.Layout.SelectionCount);
        Assert.Same(second, fixture.Layout.SelectedItem);
        Assert.False(workspace.DeleteLayoutComponentCommand.CanExecute(null));
        Assert.True(workspace.AlignLayoutSelectionCommand.CanExecute("Left"));

        workspace.SceneMoveRequestedCommand.Execute(new SceneMoveRequest(SceneViewportMoveAction.Begin, default));
        workspace.SceneMoveRequestedCommand.Execute(new SceneMoveRequest(SceneViewportMoveAction.Update, new Vector(20, 10)));
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
            SceneViewportMoveAction.Update, LayoutTransformHandle.BottomRight, new Point(350, 350), false));
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
    public void RejectedMutationAndUnexpectedRequestsLeaveTheProjectUnchanged()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        var before = new ProjectDocumentStore().SerializeForEvidence(fixture.Project);

        Assert.False(workspace.TryAddComponent(LayoutComponentKind.DigitalSensor));
        Assert.Equal("Add a Workpiece or Stage before adding a Digital Sensor", fixture.StatusMessages[^1]);
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
                () => IsEditable,
                () => IsApplyingProject,
                () => CanExecuteSessionCommand,
                () => MarkCount++,
                () => { },
                RefreshDefinition,
                () => CommandInvalidationCount++,
                StatusMessages.Add,
                (category, message) => Logs.Add((category, message)),
                () => DefinitionChangedCount++);
            Workspace.Reset();
        }

        public MachineProjectDocument Project { get; } = new() { Name = "Layout workspace" };
        public MachineLayoutViewModel Layout { get; } = new();
        public LayoutAuthoringWorkspace Workspace { get; }
        public bool IsEditable { get; set; } = true;
        public bool IsApplyingProject { get; set; }
        public bool CanExecuteSessionCommand { get; set; } = true;
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
