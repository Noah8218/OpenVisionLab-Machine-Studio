using System.IO;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the file-backed project save transaction and the deterministic
/// post-save artifact phase without owning WPF presentation or shell state.
/// </summary>
internal sealed class ProjectSaveWorkflow
{
    private readonly ProjectDocumentFileStore _projectStore;
    private readonly ProjectDocumentStore _documentStore;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Action<MachineProjectDocument> _prepareProject;
    private readonly Action<string> _persistScenarioBatchArtifacts;
    private readonly Action<string> _persistMultiAxisResult;
    private readonly Action<string> _persistVisionEvidence;

    internal ProjectSaveWorkflow(
        ProjectDocumentFileStore projectStore,
        ProjectDocumentStore documentStore,
        Func<MachineProjectDocument> getProject,
        Action<MachineProjectDocument> prepareProject,
        Action<string> persistScenarioBatchArtifacts,
        Action<string> persistMultiAxisResult,
        Action<string> persistVisionEvidence)
    {
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
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
        var receipt = await SaveWithReceiptAsync(path, string.Empty, 0);
        return receipt.SavedPath;
    }

    internal async Task<ProjectDocumentSaveReceipt> SaveWithReceiptAsync(
        string path,
        string sessionId,
        long revision)
    {
        var project = _getProject()
            ?? throw new InvalidOperationException("The current project is not available.");
        _prepareProject(project);
        var contentHash = ProjectDocumentSession.ComputeContentHash(
            _documentStore.SerializeForSaveEvidence(project));
        await _projectStore.SaveAsync(project, path);

        var fullPath = Path.GetFullPath(path);
        _persistScenarioBatchArtifacts(fullPath);
        _persistMultiAxisResult(fullPath);
        _persistVisionEvidence(fullPath);
        return new(sessionId, revision, fullPath, contentHash);
    }
}
