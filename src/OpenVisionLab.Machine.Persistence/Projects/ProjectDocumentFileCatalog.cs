using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Persistence.Projects;

public sealed record ProjectDocumentFileEntry(
    string FullPath,
    string FileName,
    MachineProjectDocument Document);

/// <summary>
/// Enumerates and loads project documents from a directory. Presentation
/// models should consume these entries instead of performing file I/O.
/// </summary>
public sealed class ProjectDocumentFileCatalog
{
    private readonly ProjectDocumentFileStore _fileStore;

    public ProjectDocumentFileCatalog(ProjectDocumentFileStore? fileStore = null)
    {
        _fileStore = fileStore ?? new ProjectDocumentFileStore();
    }

    public IEnumerable<ProjectDocumentFileEntry> Enumerate(
        string directory,
        string searchPattern = "*.ovmachine")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);

        foreach (var fullPath in Directory
                     .EnumerateFiles(directory, searchPattern)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            yield return new(
                fullPath,
                Path.GetFileName(fullPath),
                _fileStore.Load(fullPath));
        }
    }
}
