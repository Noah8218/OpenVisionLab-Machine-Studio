using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
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
            await new ProjectDocumentFileStore().SaveAsync(openedProject, openPath);

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
    public async Task NewProjectUsesTrimmedProvidedNameAfterSavingCurrent()
    {
        var directory = CreateTestDirectory();
        try
        {
            var initialPath = Path.Combine(directory, "current.ovmachine");
            var project = new MachineProjectDocument { Name = "Current" };
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

            project.Name = "Edited current";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.True(await coordinator.CreateNewProjectAsync("  New equipment  "));

            Assert.Equal("New equipment", coordinator.CurrentProject.Name);
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NewProjectRejectsEmptyNameBeforeResolvingDirtyState(string name)
    {
        var directory = CreateTestDirectory();
        try
        {
            var currentPath = Path.Combine(directory, "current.ovmachine");
            var project = new MachineProjectDocument { Name = "Current recipe" };
            var promptCount = 0;
            var applyCount = 0;
            var transitions = new List<ProjectLifecycleTransition>();
            var saves = new List<ProjectSaveLifecycleResult>();
            var coordinator = CreateCoordinator(
                project,
                currentPath,
                applyProject: _ =>
                {
                    applyCount++;
                    return Task.FromResult(true);
                },
                transitions: transitions,
                saves: saves,
                getUnsavedDecision: () =>
                {
                    promptCount++;
                    return UnsavedProjectDecision.Save;
                });

            project.Name = "Edited current recipe";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.False(await coordinator.CreateNewProjectAsync(name));

            Assert.Same(project, coordinator.CurrentProject);
            Assert.Equal("Edited current recipe", coordinator.CurrentProject.Name);
            Assert.Equal(Path.GetFullPath(currentPath), coordinator.CurrentPath);
            Assert.True(coordinator.HasUnsavedChanges);
            Assert.Equal(0, promptCount);
            Assert.Equal(0, applyCount);
            Assert.Empty(transitions);
            Assert.Empty(saves);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewProjectRejectsNameLongerThanRecipeInputLimit()
    {
        var coordinator = CreateCoordinator(new MachineProjectDocument { Name = "Current recipe" });

        Assert.False(await coordinator.CreateNewProjectAsync(new string('x', 61)));

        Assert.Equal("Current recipe", coordinator.CurrentProject.Name);
        Assert.Empty(coordinator.CurrentProject.Layouts);
    }

    [Fact]
    public async Task NewProjectCancelKeepsCurrentDirtySessionUntouched()
    {
        var directory = CreateTestDirectory();
        try
        {
            var currentPath = Path.Combine(directory, "current.ovmachine");
            var project = new MachineProjectDocument { Name = "Current recipe" };
            var transitions = new List<ProjectLifecycleTransition>();
            var saves = new List<ProjectSaveLifecycleResult>();
            var applyCount = 0;
            var coordinator = CreateCoordinator(
                project,
                currentPath,
                applyProject: _ =>
                {
                    applyCount++;
                    return Task.FromResult(true);
                },
                transitions: transitions,
                saves: saves,
                getUnsavedDecision: () => UnsavedProjectDecision.Cancel);

            project.Name = "Edited current recipe";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.False(await coordinator.CreateNewProjectAsync());

            Assert.Same(project, coordinator.CurrentProject);
            Assert.Equal("Edited current recipe", coordinator.CurrentProject.Name);
            Assert.Equal(Path.GetFullPath(currentPath), coordinator.CurrentPath);
            Assert.True(coordinator.HasUnsavedChanges);
            Assert.Equal(0, applyCount);
            Assert.Empty(transitions);
            Assert.Empty(saves);
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
            await new ProjectDocumentFileStore().SaveAsync(replacement, replacementPath);

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
    public async Task ReplacementOpenRetriesAfterRuntimeApplyRejectionWithoutLosingCurrentSession()
    {
        var directory = CreateTestDirectory();
        try
        {
            var currentPath = Path.Combine(directory, "current.ovmachine");
            var incomingPath = Path.Combine(directory, "incoming.ovmachine");
            var currentProject = new MachineProjectDocument { Name = "Current recipe" };
            var incomingProject = new MachineProjectDocument { Name = "Incoming recipe" };
            await new ProjectDocumentFileStore().SaveAsync(incomingProject, incomingPath);

            var appliedProjects = new List<MachineProjectDocument>();
            var transitions = new List<ProjectLifecycleTransition>();
            var applyAttempts = 0;
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                currentProject,
                currentPath,
                applyProject: project =>
                {
                    appliedProjects.Add(project);
                    applyAttempts++;
                    if (applyAttempts == 1)
                    {
                        return Task.FromResult(false);
                    }

                    coordinator!.ReplaceProject(project);
                    coordinator.AcceptAsSaved();
                    return Task.FromResult(true);
                },
                transitions: transitions,
                getUnsavedDecision: () => UnsavedProjectDecision.Discard);

            currentProject.Name = "Edited current recipe";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.False(await coordinator.OpenProjectReplacingCurrentAsync(incomingPath));

            Assert.Same(currentProject, coordinator.CurrentProject);
            Assert.Equal("Edited current recipe", coordinator.CurrentProject.Name);
            Assert.Equal(Path.GetFullPath(currentPath), coordinator.CurrentPath);
            Assert.True(coordinator.HasUnsavedChanges);
            Assert.Single(appliedProjects);
            Assert.Empty(transitions);

            Assert.True(await coordinator.OpenProjectReplacingCurrentAsync(incomingPath));

            Assert.Equal(2, applyAttempts);
            Assert.Equal("Incoming recipe", coordinator.CurrentProject.Name);
            Assert.Equal(Path.GetFullPath(incomingPath), coordinator.CurrentPath);
            Assert.False(coordinator.HasUnsavedChanges);
            var transition = Assert.Single(transitions);
            Assert.Equal(ProjectLifecycleTransitionKind.ProjectOpened, transition.Kind);
            Assert.Equal(Path.GetFullPath(incomingPath), transition.Path);
            Assert.Same(coordinator.CurrentProject, transition.Project);
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

    [Fact]
    public async Task FailedNewRecipeSaveAsKeepsDirtyStateAndRetriesToAReopenableFile()
    {
        var directory = CreateTestDirectory();
        try
        {
            var failedPath = Path.Combine(directory, "blocked.ovmachine");
            Directory.CreateDirectory(failedPath);
            var savedPath = Path.Combine(directory, "recipe.ovmachine");
            var paths = new Queue<string?>(new[] { failedPath, savedPath });
            var failures = new List<Exception>();
            var saves = new List<ProjectSaveLifecycleResult>();
            var project = new MachineProjectDocument { Name = "Untitled" };
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                project,
                saves: saves,
                onSaveCompleted: result =>
                {
                    saves.Add(result);
                    if (result.Applied)
                    {
                        coordinator!.AcceptAsSaved();
                    }
                },
                handleProjectSaveFailure: failures.Add,
                selectProjectSaveAsPath: _ => paths.Dequeue());

            project.Name = "Authored recipe";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.False(await coordinator.TrySaveCurrentProjectAsync(saveAs: true));
            Assert.True(Assert.Single(failures) is IOException or UnauthorizedAccessException);
            Assert.Null(coordinator.CurrentPath);
            Assert.True(coordinator.HasUnsavedChanges);
            Assert.Empty(saves);
            Assert.Empty(Directory.EnumerateFiles(directory, ".blocked.ovmachine.*.tmp"));
            Assert.True(Directory.Exists(failedPath));

            Assert.True(await coordinator.TrySaveCurrentProjectAsync(saveAs: true));
            Assert.Equal(Path.GetFullPath(savedPath), coordinator.CurrentPath);
            Assert.False(coordinator.HasUnsavedChanges);
            Assert.True(Assert.Single(saves).Applied);
            Assert.Equal("Authored recipe", new ProjectDocumentFileStore().Load(savedPath).Name);
            Assert.Empty(paths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledRecipeSaveAsKeepsSourceAndDirtyDraftUnchanged()
    {
        var directory = CreateTestDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "current.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            var project = new MachineProjectDocument { Name = "Current recipe" };
            await fileStore.SaveAsync(project, sourcePath);
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var failures = new List<Exception>();
            var saves = new List<ProjectSaveLifecycleResult>();
            var coordinator = CreateCoordinator(
                project,
                currentPath: sourcePath,
                saves: saves,
                handleProjectSaveFailure: failures.Add,
                selectProjectSaveAsPath: _ => null);

            project.Name = "Unsaved draft";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.False(await coordinator.TrySaveCurrentProjectAsync(saveAs: true));

            Assert.Equal(Path.GetFullPath(sourcePath), coordinator.CurrentPath);
            Assert.True(coordinator.HasUnsavedChanges);
            Assert.Empty(saves);
            Assert.Empty(failures);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledSaveAsCanBeRetriedToSaveTheDraftAsACopy()
    {
        var directory = CreateTestDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "current.ovmachine");
            var copyPath = Path.Combine(directory, "copy.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            var project = new MachineProjectDocument { Name = "Current recipe" };
            await fileStore.SaveAsync(project, sourcePath);
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var paths = new Queue<string?>(new[] { null, copyPath });
            var failures = new List<Exception>();
            var saves = new List<ProjectSaveLifecycleResult>();
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                project,
                currentPath: sourcePath,
                saves: saves,
                onSaveCompleted: result =>
                {
                    saves.Add(result);
                    if (result.Applied)
                    {
                        coordinator!.AcceptAsSaved();
                    }
                },
                handleProjectSaveFailure: failures.Add,
                selectProjectSaveAsPath: _ => paths.Dequeue());

            project.Name = "Authored draft";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());

            Assert.False(await coordinator.TrySaveCurrentProjectAsync(saveAs: true));
            Assert.Equal(Path.GetFullPath(sourcePath), coordinator.CurrentPath);
            Assert.True(coordinator.HasUnsavedChanges);
            Assert.Empty(saves);
            Assert.Empty(failures);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));

            Assert.True(await coordinator.TrySaveCurrentProjectAsync(saveAs: true));
            Assert.Equal(Path.GetFullPath(copyPath), coordinator.CurrentPath);
            Assert.False(coordinator.HasUnsavedChanges);
            Assert.True(Assert.Single(saves).Applied);
            Assert.Equal("Authored draft", fileStore.Load(copyPath).Name);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Empty(paths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AppliesAValidatedBackupThroughTheLifecycleAndReopensIt()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "lifecycle-recovery.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
            await File.WriteAllTextAsync(path, "corrupted primary");

            var current = fileStore.Load(path + ".bak");
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                current,
                path,
                applyProject: project =>
                {
                    coordinator!.ReplaceProject(project);
                    return Task.FromResult(true);
                });
            var preview = new ProjectDocumentDiagnostics().PreviewRecovery(path);

            Assert.NotNull(preview);
            Assert.True(await coordinator.ApplyProjectRecoveryAsync(preview!));
            Assert.Equal("Backup", coordinator.CurrentProject.Name);
            Assert.Equal(Path.GetFullPath(path), coordinator.CurrentPath);
            Assert.Equal("Backup", fileStore.Load(path).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mch003_RecoveredOpenKeepsSourceProvenanceAndSaveAsCreatesCopy()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "provenance.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup project" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current project" }, path);
            await File.WriteAllTextAsync(path, "corrupted primary");
            var primaryBefore = await File.ReadAllBytesAsync(path);
            var backupBefore = await File.ReadAllBytesAsync(path + ".bak");
            var transitions = new List<ProjectLifecycleTransition>();
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                fileStore.Load(path + ".bak"),
                path,
                applyProject: project =>
                {
                    coordinator!.ReplaceProject(project);
                    return Task.FromResult(true);
                },
                transitions: transitions);

            Assert.True(await coordinator.OpenProjectAsync(path));
            var transition = Assert.Single(transitions);
            Assert.NotNull(transition.LoadResult);
            Assert.Equal(ProjectDocumentLoadSource.Backup, transition.LoadResult.Source);
            Assert.Equal(ProjectDocumentRecoveryReason.PrimaryInvalid, transition.LoadResult.RecoveryReason);
            Assert.Equal(Path.GetFullPath(path + ".bak"), transition.LoadResult.SourcePath);
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
            Assert.Equal(backupBefore, await File.ReadAllBytesAsync(path + ".bak"));

            var recoveryCopyPath = Path.Combine(directory, "recovered-copy.ovmachine");
            await coordinator.SaveProjectAsync(recoveryCopyPath);
            Assert.Equal("Backup project", fileStore.Load(recoveryCopyPath).Name);
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
            Assert.Equal(backupBefore, await File.ReadAllBytesAsync(path + ".bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryDoesNotWriteWhenTheActiveDocumentIsDirty()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "dirty-recovery.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
            await File.WriteAllTextAsync(path, "corrupted primary");

            var current = fileStore.Load(path + ".bak");
            ProjectLifecycleCoordinator? coordinator = null;
            coordinator = CreateCoordinator(
                current,
                path,
                applyProject: project =>
                {
                    coordinator!.ReplaceProject(project);
                    return Task.FromResult(true);
                });
            current.Name = "Unsaved";
            coordinator.MarkChanged();
            Assert.True(coordinator.RefreshDirtyState());
            var preview = new ProjectDocumentDiagnostics().PreviewRecovery(path);
            var primaryBefore = await File.ReadAllBytesAsync(path);

            Assert.False(await coordinator.ApplyProjectRecoveryAsync(preview!));
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
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
        Func<Task>? commitFocusedEditor = null,
        Action<ProjectSaveLifecycleResult>? onSaveCompleted = null,
        Action<Exception>? handleProjectSaveFailure = null,
        Func<string, string?>? selectProjectSaveAsPath = null) =>
        new(
            project,
            currentPath,
            startupSamplePath: null,
            applyProject ?? (_ => Task.FromResult(true)),
            transition => transitions?.Add(transition),
            onSaveCompleted ?? (result => saves?.Add(result)),
            commitFocusedEditor ?? (() => Task.CompletedTask),
            prepareProjectForSave: _ => { },
            persistScenarioBatchArtifacts: _ => { },
            persistMultiAxisResult: _ => { },
            persistVisionEvidence: _ => { },
            getUnsavedDecision ?? (() => UnsavedProjectDecision.Cancel),
            handleProjectOpenFailure: _ => { },
            handleProjectSaveFailure: handleProjectSaveFailure ?? (_ => { }),
            selectProjectSaveAsPath: selectProjectSaveAsPath ?? (_ => null),
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
