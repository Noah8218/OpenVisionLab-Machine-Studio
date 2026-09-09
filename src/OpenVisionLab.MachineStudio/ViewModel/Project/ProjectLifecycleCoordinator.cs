using System.IO;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum ProjectLifecycleTransitionKind
{
    ProjectOpened,
    NewProjectCreated,
    BundledSampleOpened
}

internal sealed record ProjectLifecycleTransition(
    ProjectLifecycleTransitionKind Kind,
    MachineProjectDocument Project,
    string? Path);

internal sealed record ProjectSaveLifecycleResult(
    ProjectDocumentSaveReceipt Receipt,
    bool Applied);

/// <summary>
/// Owns the active project document session and every project transition
/// transaction. Runtime application and shell presentation are supplied as
/// callbacks so this owner does not depend on WPF or the simulation engine.
/// </summary>
internal sealed class ProjectLifecycleCoordinator
{
    private readonly ProjectDocumentStore _projectStore = new();
    private readonly ProjectDocumentFileStore _projectFileStore;
    private readonly ProjectDocumentSession _projectSession;
    private readonly ProjectDocumentOperationGate _operationGate = new();
    private readonly ProjectSaveParticipant _projectSaveParticipant = new();
    private readonly ProjectOpenWorkflow _projectOpenWorkflow;
    private readonly ProjectSaveWorkflow _projectSaveWorkflow;
    private readonly ProjectUnsavedChangesWorkflow _projectUnsavedChangesWorkflow;
    private readonly SemiconductorRecipeCopyWorkflow _semiconductorRecipeCopyWorkflow;
    private readonly Func<MachineProjectDocument, Task<bool>> _applyProject;
    private readonly Action<ProjectLifecycleTransition> _onTransitionCompleted;
    private readonly Action<ProjectSaveLifecycleResult> _onSaveCompleted;
    private readonly Func<Task> _commitFocusedEditor;
    private readonly Func<string, string?> _selectProjectSaveAsPath;
    private readonly Func<string, string?> _selectRecipeCopyDestination;
    private readonly string? _startupSamplePath;

    internal ProjectLifecycleCoordinator(
        MachineProjectDocument initialProject,
        string? initialProjectPath,
        string? startupSamplePath,
        Func<MachineProjectDocument, Task<bool>> applyProject,
        Action<ProjectLifecycleTransition> onTransitionCompleted,
        Action<ProjectSaveLifecycleResult> onSaveCompleted,
        Func<Task> commitFocusedEditor,
        Action<MachineProjectDocument> prepareProjectForSave,
        Action<string> persistScenarioBatchArtifacts,
        Action<string> persistMultiAxisResult,
        Action<string> persistVisionEvidence,
        Func<UnsavedProjectDecision> getUnsavedDecision,
        Action<Exception> handleProjectOpenFailure,
        Action<Exception> handleProjectSaveFailure,
        Func<string, string?> selectProjectSaveAsPath,
        Func<string, string?> selectRecipeCopyDestination,
        Func<string> getRecipeOverwriteRejectedMessage)
    {
        _applyProject = applyProject ?? throw new ArgumentNullException(nameof(applyProject));
        _onTransitionCompleted = onTransitionCompleted
            ?? throw new ArgumentNullException(nameof(onTransitionCompleted));
        _onSaveCompleted = onSaveCompleted
            ?? throw new ArgumentNullException(nameof(onSaveCompleted));
        _commitFocusedEditor = commitFocusedEditor
            ?? throw new ArgumentNullException(nameof(commitFocusedEditor));
        _selectProjectSaveAsPath = selectProjectSaveAsPath
            ?? throw new ArgumentNullException(nameof(selectProjectSaveAsPath));
        _selectRecipeCopyDestination = selectRecipeCopyDestination
            ?? throw new ArgumentNullException(nameof(selectRecipeCopyDestination));
        _startupSamplePath = NormalizePath(startupSamplePath);
        _projectFileStore = new(_projectStore);
        _projectSession = new(
            _projectStore,
            initialProject ?? throw new ArgumentNullException(nameof(initialProject)),
            initialProjectPath);

        _projectSaveWorkflow = new(
            _projectFileStore,
            _projectStore,
            () => CurrentProject,
            prepareProjectForSave,
            persistScenarioBatchArtifacts,
            persistMultiAxisResult,
            persistVisionEvidence);
        _projectUnsavedChangesWorkflow = new(
            _commitFocusedEditor,
            () => prepareProjectForSave(CurrentProject),
            () => HasUnsavedChanges,
            () => RefreshDirtyState(),
            getUnsavedDecision ?? throw new ArgumentNullException(nameof(getUnsavedDecision)),
            () => TrySaveCurrentProjectWithParticipantAsync(saveAs: false));
        _projectOpenWorkflow = new(
            _projectFileStore,
            ResolveUnsavedChangesCoreAsync,
            ApplyOpenedProjectAsync,
            handleProjectOpenFailure ?? throw new ArgumentNullException(nameof(handleProjectOpenFailure)));
        _semiconductorRecipeCopyWorkflow = new(
            _projectFileStore,
            getRecipeOverwriteRejectedMessage
                ?? throw new ArgumentNullException(nameof(getRecipeOverwriteRejectedMessage)));
        HandleProjectSaveFailure = handleProjectSaveFailure
            ?? throw new ArgumentNullException(nameof(handleProjectSaveFailure));
    }

    private Action<Exception> HandleProjectSaveFailure { get; }

    internal MachineProjectDocument CurrentProject => _projectSession.Project;

    internal string DisplayName => _projectSession.DisplayName;

    internal string? CurrentPath => _projectSession.CurrentPath;

    internal string SessionId => _projectSession.SessionId;

    internal long Revision => _projectSession.Revision;

    internal bool HasUnsavedChanges => _projectSession.HasUnsavedChanges;

    internal bool HasStartupSample => _startupSamplePath is not null;

    internal string SerializeForEvidence() => _projectSession.SerializeForEvidence();

    internal void ReplaceProject(MachineProjectDocument project) => _projectSession.ReplaceProject(project);

    internal void MarkChanged() => _projectSession.MarkChanged();

    internal bool RefreshDirtyState() => _projectSession.RefreshDirtyState();

    internal bool AcceptAsSaved() => _projectSession.AcceptAsSaved();

    internal bool TryApplySave(ProjectDocumentSaveReceipt receipt) => _projectSession.TryApplySave(receipt);

    internal Task<bool> OpenProjectAsync(string path) =>
        _operationGate.RunAsync(() => _projectOpenWorkflow.OpenAsync(path));

    internal Task<bool> OpenProjectReplacingCurrentAsync(string path) =>
        _operationGate.RunAsync(() => _projectOpenWorkflow.OpenAsync(path, replaceCurrent: true));

    internal Task<bool> CreateNewProjectAsync() =>
        _operationGate.RunAsync(CreateNewProjectCoreAsync);

    internal Task OpenBundledSampleAsync() =>
        _operationGate.RunAsync(OpenBundledSampleCoreAsync);

    internal Task SaveProjectAsync(string path) =>
        _projectSaveParticipant.TrackSaveAsync(
            () => _operationGate.RunAsync(() => SaveProjectCoreAsync(path)));

    internal async Task<bool> TrySaveCurrentProjectAsync(bool saveAs = false)
    {
        var result = await _projectSaveParticipant.TrackAttemptAsync(
            () => _operationGate.RunAsync(() => TrySaveCurrentProjectCoreAsync(saveAs)));
        return result.IsSuccessful;
    }

    internal Task<ProjectSaveParticipantResult> ObserveSaveAsync() =>
        _projectSaveParticipant.ObserveAsync();

    internal Task<ProjectSaveParticipantResult> ObserveSaveAsync(TimeSpan timeout) =>
        _projectSaveParticipant.ObserveAsync(timeout);

    internal Task<bool> CreateSemiconductorRecipeCopyAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath) =>
        _operationGate.RunAsync(() => CreateSemiconductorRecipeCopyCoreAsync(recipe, destinationPath));

    internal Task<bool> TryResolveUnsavedChangesAsync() =>
        _operationGate.RunAsync(ResolveUnsavedChangesCoreAsync);

    private Task<bool> ResolveUnsavedChangesCoreAsync() =>
        ResolveUnsavedChangesResultAsync();

    private async Task<bool> ResolveUnsavedChangesResultAsync() =>
        (await _projectUnsavedChangesWorkflow.ResolveAsync()).IsAccepted;

    private async Task<bool> ApplyOpenedProjectAsync(
        MachineProjectDocument project,
        string path)
    {
        if (!await _applyProject(project))
        {
            return false;
        }

        _projectSession.SetCurrentPath(path);
        _onTransitionCompleted(new(
            ProjectLifecycleTransitionKind.ProjectOpened,
            project,
            _projectSession.CurrentPath));
        return true;
    }

    private async Task<bool> CreateNewProjectCoreAsync()
    {
        if (!await ResolveUnsavedChangesCoreAsync()
            || !await _applyProject(new MachineProjectDocument { Name = "Untitled" }))
        {
            return false;
        }

        _projectSession.SetCurrentPath(null);
        _onTransitionCompleted(new(
            ProjectLifecycleTransitionKind.NewProjectCreated,
            CurrentProject,
            null));
        return true;
    }

    private async Task OpenBundledSampleCoreAsync()
    {
        if (_startupSamplePath is null)
        {
            return;
        }

        var project = await _projectFileStore.LoadAsync(_startupSamplePath);
        if (!await _applyProject(project))
        {
            return;
        }

        _projectSession.SetCurrentPath(null);
        _onTransitionCompleted(new(
            ProjectLifecycleTransitionKind.BundledSampleOpened,
            project,
            null));
    }

    private async Task<ProjectSaveLifecycleResult> SaveProjectCoreAsync(string path)
    {
        await _commitFocusedEditor();
        var receipt = await _projectSaveWorkflow.SaveWithReceiptAsync(
            path,
            SessionId,
            Revision);
        var result = new ProjectSaveLifecycleResult(receipt, TryApplySave(receipt));
        _onSaveCompleted(result);
        return result;
    }

    private async Task<bool> TrySaveCurrentProjectWithParticipantAsync(bool saveAs)
    {
        var result = await _projectSaveParticipant.TrackAttemptAsync(
            () => TrySaveCurrentProjectCoreAsync(saveAs));
        return result.IsSuccessful;
    }

    private async Task<ProjectSaveAttemptResult> TrySaveCurrentProjectCoreAsync(bool saveAs)
    {
        try
        {
            if (!saveAs && CurrentPath is not null)
            {
                return new(
                    ProjectSaveAttemptOutcome.Saved,
                    await SaveProjectCoreAsync(CurrentPath));
            }

            return await TrySaveProjectAsCoreAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HandleProjectSaveFailure(exception);
            return new(ProjectSaveAttemptOutcome.Failed, Exception: exception);
        }
    }

    private async Task<ProjectSaveAttemptResult> TrySaveProjectAsCoreAsync()
    {
        await _commitFocusedEditor();
        var path = _selectProjectSaveAsPath(CurrentProject.Name);
        if (path is null)
        {
            return new(ProjectSaveAttemptOutcome.Cancelled);
        }

        return new(
            ProjectSaveAttemptOutcome.Saved,
            await SaveProjectCoreAsync(path));
    }

    private async Task<bool> CreateSemiconductorRecipeCopyCoreAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            destinationPath = _selectRecipeCopyDestination(recipe.FileName);
        }

        if (destinationPath is null || !await ResolveUnsavedChangesCoreAsync())
        {
            return false;
        }

        var copyPath = await _semiconductorRecipeCopyWorkflow.CopyAsync(
            recipe.SourcePath,
            destinationPath);
        return await _projectOpenWorkflow.OpenAsync(copyPath);
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
