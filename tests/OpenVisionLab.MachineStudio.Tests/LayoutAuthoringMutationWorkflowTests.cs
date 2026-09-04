using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class LayoutAuthoringMutationWorkflowTests
{
    [Fact]
    public void AddMapsCoreResultAndCommitsSelectionAwareHistory()
    {
        using var fixture = CreateFixture();

        Assert.True(fixture.Workflow.TryAdd(LayoutComponentKind.LinearStage, 45, 185));

        var component = Assert.Single(fixture.LayoutDefinition.Components);
        Assert.Equal(LayoutComponentKind.LinearStage, component.Kind);
        Assert.Equal(50, component.Transform.X);
        Assert.Equal(190, component.Transform.Y);
        Assert.Equal(component.Id, fixture.Layout.SelectedItem?.Id);
        Assert.True(fixture.History.UndoCommand.CanExecute(null));
        Assert.Equal(1, fixture.MarkCount);
        Assert.Equal(1, fixture.RunToolAvailabilityRefreshCount);
        Assert.Equal(1, fixture.DefinitionRefreshCount);
        Assert.Equal($"Added {component.Name}", fixture.StatusMessages[^1]);
        Assert.Contains(fixture.Logs, log => log.Category == "Layout" && log.Message.Contains(component.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void AddFailureMapsDependencyWithoutChangingProjectOrHistory()
    {
        using var fixture = CreateFixture();

        Assert.False(fixture.Workflow.TryAdd(LayoutComponentKind.DigitalSensor));

        Assert.Empty(fixture.LayoutDefinition.Components);
        Assert.False(fixture.History.UndoCommand.CanExecute(null));
        Assert.Equal(0, fixture.MarkCount);
        Assert.Equal(0, fixture.RunToolAvailabilityRefreshCount);
        Assert.Equal(0, fixture.DefinitionRefreshCount);
        Assert.Equal("Add a Workpiece or Stage before adding a Digital Sensor", fixture.StatusMessages[^1]);
        Assert.Contains(fixture.Logs, log => log.Message.Contains("Digital Sensor requires", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveMapsCoreResultAndCommitsTheMutationAfterRefresh()
    {
        using var fixture = CreateFixture();

        Assert.True(fixture.Workflow.TryAdd(LayoutComponentKind.LinearStage));
        var addedName = Assert.Single(fixture.LayoutDefinition.Components).Name;
        fixture.StatusMessages.Clear();
        fixture.Logs.Clear();

        Assert.True(fixture.Workflow.TryRemoveSelected());

        Assert.Empty(fixture.LayoutDefinition.Components);
        Assert.False(fixture.Layout.SelectedItem is not null);
        Assert.True(fixture.History.UndoCommand.CanExecute(null));
        Assert.Equal(2, fixture.MarkCount);
        Assert.Equal(2, fixture.RunToolAvailabilityRefreshCount);
        Assert.Equal(2, fixture.DefinitionRefreshCount);
        Assert.Equal($"Removed {addedName}", Assert.Single(fixture.StatusMessages));
        Assert.Contains(fixture.Logs, log => log.Message.Contains("without cascading", StringComparison.Ordinal));
    }

    [Fact]
    public void AddIsNoOpWhenShellIsNotEditableOrApplyingProject()
    {
        using var notEditable = CreateFixture(isEditable: false);
        using var applyingProject = CreateFixture(isApplyingProject: true);

        Assert.False(notEditable.Workflow.TryAdd(LayoutComponentKind.LinearStage));
        Assert.False(applyingProject.Workflow.TryAdd(LayoutComponentKind.LinearStage));
        Assert.Empty(notEditable.LayoutDefinition.Components);
        Assert.Empty(applyingProject.LayoutDefinition.Components);
        Assert.Empty(notEditable.StatusMessages);
        Assert.Empty(applyingProject.StatusMessages);
    }

    private static Fixture CreateFixture(
        bool isEditable = true,
        bool isApplyingProject = false)
    {
        var project = new MachineProjectDocument { Name = "Layout authoring" };
        var layoutDefinition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            GridSize = 10,
            SnapToGrid = true
        };
        project.Layouts.Add(layoutDefinition);
        project.Simulation.ActiveLayoutId = layoutDefinition.Id;

        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var statusMessages = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var fixture = new Fixture(
            project,
            layoutDefinition,
            layout,
            statusMessages,
            logs,
            isEditable,
            isApplyingProject);

        fixture.History = new LayoutAuthoringHistoryViewModel(
            layout,
            () => project,
            () => isEditable,
            () => isApplyingProject,
            () => fixture.MarkCount++,
            () => fixture.RunToolAvailabilityRefreshCount++,
            fixture.RefreshDefinition,
            () => fixture.HostCommandInvalidationCount++,
            statusMessages.Add,
            (category, message) => logs.Add((category, message)),
            () => fixture.LayoutDefinitionChangedCount++);
        fixture.History.Reset();
        fixture.Workflow = new LayoutAuthoringMutationWorkflow(
            layout,
            new LayoutComponentAuthoringService(),
            fixture.History,
            () => project,
            () => isEditable,
            () => isApplyingProject,
            () => fixture.MarkCount++,
            () => fixture.RunToolAvailabilityRefreshCount++,
            fixture.RefreshDefinition,
            statusMessages.Add,
            (category, message) => logs.Add((category, message)));
        return fixture;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            MachineProjectDocument project,
            MachineLayoutDefinition layoutDefinition,
            MachineLayoutViewModel layout,
            List<string> statusMessages,
            List<(string Category, string Message)> logs,
            bool isEditable,
            bool isApplyingProject)
        {
            Project = project;
            LayoutDefinition = layoutDefinition;
            Layout = layout;
            StatusMessages = statusMessages;
            Logs = logs;
            IsEditable = isEditable;
            IsApplyingProject = isApplyingProject;
        }

        public MachineProjectDocument Project { get; }
        public MachineLayoutDefinition LayoutDefinition { get; }
        public MachineLayoutViewModel Layout { get; }
        public List<string> StatusMessages { get; }
        public List<(string Category, string Message)> Logs { get; }
        public bool IsEditable { get; }
        public bool IsApplyingProject { get; }
        public int MarkCount { get; set; }
        public int RunToolAvailabilityRefreshCount { get; set; }
        public int DefinitionRefreshCount { get; set; }
        public int HostCommandInvalidationCount { get; set; }
        public int LayoutDefinitionChangedCount { get; set; }
        public LayoutAuthoringHistoryViewModel History { get; set; } = null!;
        public LayoutAuthoringMutationWorkflow Workflow { get; set; } = null!;

        public void RefreshDefinition(string? selectedId)
        {
            DefinitionRefreshCount++;
            Layout.Load(Project);
            if (selectedId is not null)
            {
                Layout.Select(selectedId);
            }
        }

        public void Dispose() => History.Dispose();
    }
}
