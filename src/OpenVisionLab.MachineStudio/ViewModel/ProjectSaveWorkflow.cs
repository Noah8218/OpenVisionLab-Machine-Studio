using System.IO;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the file-backed project save transaction and the deterministic
/// post-save artifact phase without owning WPF presentation or shell state.
/// </summary>
internal sealed class ProjectSaveWorkflow
{
    private readonly ProjectDocumentStore _projectStore;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Action<MachineProjectDocument> _prepareProject;
    private readonly Action<string> _persistScenarioBatchArtifacts;
    private readonly Action<string> _persistMultiAxisResult;
    private readonly Action<string> _persistVisionEvidence;

    internal ProjectSaveWorkflow(
        ProjectDocumentStore projectStore,
        Func<MachineProjectDocument> getProject,
        Action<MachineProjectDocument> prepareProject,
        Action<string> persistScenarioBatchArtifacts,
        Action<string> persistMultiAxisResult,
        Action<string> persistVisionEvidence)
    {
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _prepareProject = prepareProject ?? throw new ArgumentNullException(nameof(prepareProject));
        _persistScenarioBatchArtifacts = persistScenarioBatchArtifacts
            ?? throw new ArgumentNullException(nameof(persistScenarioBatchArtifacts));
        _persistMultiAxisResult = persistMultiAxisResult
            ?? throw new ArgumentNullException(nameof(persistMultiAxisResult));
        _persistVisionEvidence = persistVisionEvidence
            ?? throw new ArgumentNullException(nameof(persistVisionEvidence));
    }

    internal async Task<string> SaveAsync(string path)
    {
        var project = _getProject()
            ?? throw new InvalidOperationException("The current project is not available.");
        _prepareProject(project);
        await _projectStore.SaveAsync(project, path);

        var fullPath = Path.GetFullPath(path);
        _persistScenarioBatchArtifacts(fullPath);
        _persistMultiAxisResult(fullPath);
        _persistVisionEvidence(fullPath);
        return fullPath;
    }
}
