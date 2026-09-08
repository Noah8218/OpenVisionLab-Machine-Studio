using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectLifecycleCoordinatorTests
{
    [Fact]
    public async Task SaveAndOpenKeepSessionStateInsideCoordinator()
    {
        var directory = CreateTestDirectory();
        try
        {
            var project = new MachineProjectDocument { Name = "Initial" };
            var transitions = new List<ProjectLifecycleTransition>();
            var saves = new List<ProjectSaveLifecycleResult>();
            var appliedProjects = new List<MachineProjectDocument>();
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                project,
                applyProject: appliedProject =>
                {
                    appliedProjects.Add(appliedProject);
                    coordinator!.ReplaceProject(appliedProject);
                    return Task.FromResult(true);
                },
                transitions: transitions,
                saves: saves);

            project.Name = "Changed";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            var savePath = Path.Combine(directory, "saved.ovmachine");
            await coordinator.SaveProjectAsync(savePath);
            coordinator.AcceptAsSaved();

            Assert.Single(saves);
            Assert.True(saves[0].Applied);
            Assert.Equal(Path.GetFullPath(savePath), coordinator.CurrentPath);
            Assert.False(coordinator.HasUnsavedChanges);

            var openedProject = new MachineProjectDocument { Name = "Opened" };
            var openPath = Path.Combine(directory, "opened.ovmachine");
            await new ProjectDocumentStore().SaveAsync(openedProject, openPath);

            Assert.True(await coordinator.OpenProjectAsync(openPath));
            Assert.Equal(openedProject.Name, appliedProjects[0].Name);
            Assert.Equal(Path.GetFullPath(openPath), coordinator.CurrentPath);
            Assert.Equal(ProjectLifecycleTransitionKind.ProjectOpened, transitions[^1].Kind);
            Assert.Equal(Path.GetFullPath(openPath), transitions[^1].Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewProjectResolvesDirtyStateThroughTheCoreSavePath()
    {
        var directory = CreateTestDirectory();
        try
        {
            var initialPath = Path.Combine(directory, "current.ovmachine");
            var project = new MachineProjectDocument { Name = "Dirty" };
            var transitions = new List<ProjectLifecycleTransition>();
            var saves = new List<ProjectSaveLifecycleResult>();
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                project,
                initialPath,
                applyProject: appliedProject =>
                {
                    coordinator!.ReplaceProject(appliedProject);
                    return Task.FromResult(true);
                },
                transitions: transitions,
                saves: saves,
                getUnsavedDecision: () => UnsavedProjectDecision.Save);

            project.Name = "Edited";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.True(await coordinator.CreateNewProjectAsync());
            Assert.Equal("Untitled", coordinator.CurrentProject.Name);
            Assert.Null(coordinator.CurrentPath);
            Assert.Equal(ProjectLifecycleTransitionKind.NewProjectCreated, transitions[^1].Kind);
            Assert.Single(saves);
            Assert.True(saves[0].Applied);
            Assert.True(File.Exists(initialPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacementOpenCancelLeavesTheCurrentSessionUntouched()
    {
        var directory = CreateTestDirectory();
        try
        {
            var initialPath = Path.Combine(directory, "current.ovmachine");
            var project = new MachineProjectDocument { Name = "Current" };
            var transitions = new List<ProjectLifecycleTransition>();
            var saves = new List<ProjectSaveLifecycleResult>();
            var applyCount = 0;
            var coordinator = CreateCoordinator(
                project,
                initialPath,
                applyProject: _ =>
                {
                    applyCount++;
                    return Task.FromResult(true);
                },
                transitions: transitions,
                saves: saves,
                getUnsavedDecision: () => UnsavedProjectDecision.Cancel);

            project.Name = "Edited";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            var replacement = new MachineProjectDocument { Name = "Replacement" };
            var replacementPath = Path.Combine(directory, "replacement.ovmachine");
            await new ProjectDocumentStore().SaveAsync(replacement, replacementPath);

            Assert.False(await coordinator.OpenProjectReplacingCurrentAsync(replacementPath));
            Assert.Equal("Edited", coordinator.CurrentProject.Name);
            Assert.Equal(Path.GetFullPath(initialPath), coordinator.CurrentPath);
            Assert.Equal(0, applyCount);
            Assert.Empty(transitions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveParticipantObservesAQueuedSaveWithoutChangingItsResult()
    {
        var directory = CreateTestDirectory();
        try
        {
            var editorStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseEditor = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = CreateCoordinator(
                new MachineProjectDocument { Name = "Observable save" },
                commitFocusedEditor: async () =>
                {
                    editorStarted.SetResult(true);
                    await releaseEditor.Task;
                });
            var savePath = Path.Combine(directory, "observable.ovmachine");

            var saveTask = coordinator.SaveProjectAsync(savePath);
            await editorStarted.Task;
            var observationTask = coordinator.ObserveSaveAsync();

            Assert.False(observationTask.IsCompleted);
            releaseEditor.SetResult(true);
            await saveTask;

            var result = await observationTask;
            Assert.Equal(ProjectSaveParticipantOutcome.Completed, result.Outcome);
            Assert.Equal(savePath, result.Save?.Receipt.SavedPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandledSaveFailureKeepsThePublicBoolAndParticipantFailure()
    {
        var directory = CreateTestDirectory();
        try
        {
            var editorStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseEditor = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new IOException("editor commit failed");
            var coordinator = CreateCoordinator(
                new MachineProjectDocument { Name = "Failed observable save" },
                Path.Combine(directory, "current.ovmachine"),
                commitFocusedEditor: async () =>
                {
                    editorStarted.SetResult(true);
                    await releaseEditor.Task;
                    throw expected;
                });

            var saveTask = coordinator.TrySaveCurrentProjectAsync();
            await editorStarted.Task;
            var observationTask = coordinator.ObserveSaveAsync();

            releaseEditor.SetResult(true);
            Assert.False(await saveTask);

            var result = await observationTask;
            Assert.Equal(ProjectSaveParticipantOutcome.Failed, result.Outcome);
            Assert.Same(expected, result.Exception);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectLifecycleCoordinator CreateCoordinator(
        MachineProjectDocument project,
        string? currentPath = null,
        Func<MachineProjectDocument, Task<bool>>? applyProject = null,
        List<ProjectLifecycleTransition>? transitions = null,
        List<ProjectSaveLifecycleResult>? saves = null,
        Func<UnsavedProjectDecision>? getUnsavedDecision = null,
        Func<Task>? commitFocusedEditor = null) =>
        new(
            project,
            currentPath,
            startupSamplePath: null,
            applyProject ?? (_ => Task.FromResult(true)),
            transition => transitions?.Add(transition),
            result => saves?.Add(result),
            commitFocusedEditor ?? (() => Task.CompletedTask),
            prepareProjectForSave: _ => { },
            persistScenarioBatchArtifacts: _ => { },
            persistMultiAxisResult: _ => { },
            persistVisionEvidence: _ => { },
            getUnsavedDecision ?? (() => UnsavedProjectDecision.Cancel),
            handleProjectOpenFailure: _ => { },
            handleProjectSaveFailure: _ => { },
            selectProjectSaveAsPath: _ => null,
            selectRecipeCopyDestination: _ => null,
            getRecipeOverwriteRejectedMessage: () => "overwrite rejected");

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\project-lifecycle-coordinator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
