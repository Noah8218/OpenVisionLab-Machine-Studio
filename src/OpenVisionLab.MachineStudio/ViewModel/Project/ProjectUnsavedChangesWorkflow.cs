using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum ProjectUnsavedChangesOutcome
{
    NoChanges,
    Saved,
    Discarded,
    SaveRejected,
    Cancelled
}

internal sealed record ProjectUnsavedChangesResult(ProjectUnsavedChangesOutcome Outcome)
{
    internal bool IsAccepted => Outcome is
        ProjectUnsavedChangesOutcome.NoChanges
        or ProjectUnsavedChangesOutcome.Saved
        or ProjectUnsavedChangesOutcome.Discarded;
}

/// <summary>
/// Owns the document dirty-resolution decision sequence without owning file
/// persistence, WPF dialogs, or project state.
/// </summary>
internal sealed class ProjectUnsavedChangesWorkflow
{
    private readonly Func<Task> _commitFocusedEditor;
    private readonly Action _prepareProject;
    private readonly Func<bool> _hasUnsavedChanges;
    private readonly Action _refreshDirtyState;
    private readonly Func<UnsavedProjectDecision> _getDecision;
    private readonly Func<Task<bool>> _saveCurrentProject;

    internal ProjectUnsavedChangesWorkflow(
        Func<Task> commitFocusedEditor,
        Action prepareProject,
        Func<bool> hasUnsavedChanges,
        Action refreshDirtyState,
        Func<UnsavedProjectDecision> getDecision,
        Func<Task<bool>> saveCurrentProject)
    {
        _commitFocusedEditor = commitFocusedEditor ?? throw new ArgumentNullException(nameof(commitFocusedEditor));
        _prepareProject = prepareProject ?? throw new ArgumentNullException(nameof(prepareProject));
        _hasUnsavedChanges = hasUnsavedChanges ?? throw new ArgumentNullException(nameof(hasUnsavedChanges));
        _refreshDirtyState = refreshDirtyState ?? throw new ArgumentNullException(nameof(refreshDirtyState));
        _getDecision = getDecision ?? throw new ArgumentNullException(nameof(getDecision));
        _saveCurrentProject = saveCurrentProject ?? throw new ArgumentNullException(nameof(saveCurrentProject));
    }

    internal async Task<ProjectUnsavedChangesResult> ResolveAsync()
    {
        await _commitFocusedEditor();
        _prepareProject();
        _refreshDirtyState();
        if (!_hasUnsavedChanges())
        {
            return new(ProjectUnsavedChangesOutcome.NoChanges);
        }

        return _getDecision() switch
        {
            UnsavedProjectDecision.Save => await SaveAsync(),
            UnsavedProjectDecision.Discard => new(ProjectUnsavedChangesOutcome.Discarded),
            _ => new(ProjectUnsavedChangesOutcome.Cancelled)
        };
    }

    private async Task<ProjectUnsavedChangesResult> SaveAsync() =>
        await _saveCurrentProject()
            ? new(ProjectUnsavedChangesOutcome.Saved)
            : new(ProjectUnsavedChangesOutcome.SaveRejected);
}
