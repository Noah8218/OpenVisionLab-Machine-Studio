using System.Text.Json;
using System.IO;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the file-backed project-open ordering without owning shell presentation
/// or project application state.
/// </summary>
internal sealed class ProjectOpenWorkflow
{
    private readonly ProjectDocumentStore _projectStore;
    private readonly Func<Task<bool>> _resolveUnsavedChanges;
    private readonly Func<MachineProjectDocument, string, Task<bool>> _applyOpenedProject;
    private readonly Action<Exception> _handleLoadFailure;

    internal ProjectOpenWorkflow(
        ProjectDocumentStore projectStore,
        Func<Task<bool>> resolveUnsavedChanges,
        Func<MachineProjectDocument, string, Task<bool>> applyOpenedProject,
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
        var project = await TryLoadAsync(path);
        if (project is null)
        {
            return false;
        }

        if (replaceCurrent)
        {
            if (!await _resolveUnsavedChanges())
            {
                return false;
            }

            project = await TryLoadAsync(path);
            if (project is null)
            {
                return false;
            }
        }

        return await _applyOpenedProject(project, path);
    }

    private async Task<MachineProjectDocument?> TryLoadAsync(string path)
    {
        try
        {
            return await _projectStore.LoadAsync(path);
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
