using System.Text.Json;
using System.IO;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the file-backed project-open ordering without owning shell presentation
/// or project application state.
/// </summary>
internal sealed class ProjectOpenWorkflow
{
    private readonly ProjectDocumentFileStore _projectStore;
    private readonly Func<Task<bool>> _resolveUnsavedChanges;
    private readonly Func<ProjectDocumentLoadResult, Task<bool>> _applyOpenedProject;
    private readonly Action<Exception> _handleLoadFailure;

    internal ProjectOpenWorkflow(
        ProjectDocumentFileStore projectStore,
        Func<Task<bool>> resolveUnsavedChanges,
        Func<ProjectDocumentLoadResult, Task<bool>> applyOpenedProject,
        Action<Exception> handleLoadFailure)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(resolveUnsavedChanges);
        ArgumentNullException.ThrowIfNull(applyOpenedProject);
        ArgumentNullException.ThrowIfNull(handleLoadFailure);

        _projectStore = projectStore;
        _resolveUnsavedChanges = resolveUnsavedChanges;
        _applyOpenedProject = applyOpenedProject;
        _handleLoadFailure = handleLoadFailure;
    }

    internal async Task<bool> OpenAsync(string path, bool replaceCurrent = false)
    {
        var loadResult = await TryLoadAsync(path);
        if (loadResult is null)
        {
            return false;
        }

        if (replaceCurrent)
        {
            if (!await _resolveUnsavedChanges())
            {
                return false;
            }

            loadResult = await TryLoadAsync(path);
            if (loadResult is null)
            {
                return false;
            }
        }

        return await _applyOpenedProject(loadResult);
    }

    private async Task<ProjectDocumentLoadResult?> TryLoadAsync(string path)
    {
        try
        {
            return await _projectStore.LoadWithProvenanceAsync(path);
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or ProjectDocumentLoadException)
        {
            _handleLoadFailure(exception);
            return null;
        }
    }
}
