using System.IO;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the active project document, its file identity, and the saved-evidence
/// baseline used by the shell's dirty-state policy.
/// </summary>
internal sealed class ProjectDocumentSession
{
    private readonly ProjectDocumentStore _projectStore;
    private readonly ProjectDirtyState _dirtyState;
    private MachineProjectDocument _project;
    private string? _currentPath;
    private long _revision = 1;

    internal ProjectDocumentSession(
        ProjectDocumentStore projectStore,
        MachineProjectDocument project,
        string? currentPath)
    {
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _currentPath = NormalizePath(currentPath);
        _dirtyState = new(SerializeForEvidence);
    }

    internal MachineProjectDocument Project => _project;

    internal string SessionId { get; } = Guid.NewGuid().ToString("N");

    internal long Revision => Volatile.Read(ref _revision);

    internal string? CurrentPath => _currentPath;

    internal string DisplayName => _currentPath is null
        ? _project.Name
        : Path.GetFileNameWithoutExtension(_currentPath);

    internal bool HasUnsavedChanges => _dirtyState.HasUnsavedChanges;

    internal void ReplaceProject(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Interlocked.Increment(ref _revision);
    }

    internal void MarkChanged() => Interlocked.Increment(ref _revision);

    internal void SetCurrentPath(string? path) => _currentPath = NormalizePath(path);

    internal string SerializeForEvidence() => _projectStore.SerializeForEvidence(_project);

    internal bool RefreshDirtyState() => _dirtyState.Refresh();

    internal bool AcceptAsSaved() => _dirtyState.AcceptAsSaved();

    internal bool TryApplySave(ProjectDocumentSaveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.SessionId, SessionId, StringComparison.Ordinal)
            || receipt.Revision != Revision
            || !string.Equals(
                receipt.ContentHash,
                ComputeContentHash(SerializeForEvidence()),
                StringComparison.Ordinal))
        {
            return false;
        }

        SetCurrentPath(receipt.SavedPath);
        return true;
    }

    internal static string ComputeContentHash(string evidence) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence ?? string.Empty)));

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
