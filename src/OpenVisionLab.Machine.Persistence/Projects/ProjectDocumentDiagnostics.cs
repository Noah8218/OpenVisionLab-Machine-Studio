using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Persistence.Projects;

/// <summary>
/// Inspects the active project file and its backup without changing either file.
/// </summary>
public sealed class ProjectDocumentDiagnostics
{
    private readonly ProjectDocumentStore _documentStore;

    public ProjectDocumentDiagnostics(ProjectDocumentStore? documentStore = null)
    {
        _documentStore = documentStore ?? new ProjectDocumentStore();
    }

    public ProjectDocumentDiagnosticReport Inspect(
        string? projectPath,
        MachineProjectDocument currentProject,
        bool hasUnsavedChanges)
    {
        ArgumentNullException.ThrowIfNull(currentProject);

        var items = new List<ProjectDocumentDiagnosticItem>();
        if (hasUnsavedChanges)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.CurrentDocumentHasUnsavedChanges,
                ProjectDocumentDiagnosticSeverity.Warning,
                string.Empty));
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            items.Insert(0, new(
                ProjectDocumentDiagnosticCode.NoSavedPath,
                ProjectDocumentDiagnosticSeverity.Information,
                string.Empty));
            return new(null, items, null);
        }

        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
        {
            items.Insert(0, new(
                ProjectDocumentDiagnosticCode.PrimaryFileMissing,
                ProjectDocumentDiagnosticSeverity.Error,
                fullPath,
                RelatedPath: fullPath));
            return AddBackupResult(fullPath, items);
        }

        try
        {
            var primary = _documentStore.Load(File.ReadAllText(fullPath, Encoding.UTF8));
            items.Insert(0, new(
                ProjectDocumentDiagnosticCode.PrimaryFileHealthy,
                ProjectDocumentDiagnosticSeverity.Information,
                primary.Schema,
                RelatedPath: fullPath));

            if (!hasUnsavedChanges && !SameCanonicalContent(currentProject, primary))
            {
                items.Add(new(
                    ProjectDocumentDiagnosticCode.PrimaryFileDiffersFromCurrent,
                    ProjectDocumentDiagnosticSeverity.Warning,
                    fullPath,
                    RelatedPath: fullPath));
            }

            AddInvalidBackupWarningIfPresent(fullPath, items);
            return new(fullPath, items, null);
        }
        catch (ProjectDocumentLoadException exception)
        {
            items.Insert(0, new(
                exception.ErrorCode == ProjectDocumentLoadErrorCode.UnsupportedSchema
                    ? ProjectDocumentDiagnosticCode.PrimaryFileUnsupportedSchema
                    : ProjectDocumentDiagnosticCode.PrimaryFileInvalid,
                ProjectDocumentDiagnosticSeverity.Error,
                exception.ProjectSchema ?? string.Empty,
                RelatedPath: fullPath));
            return AddBackupResult(fullPath, items);
        }
        catch (JsonException)
        {
            items.Insert(0, new(
                ProjectDocumentDiagnosticCode.PrimaryFileInvalid,
                ProjectDocumentDiagnosticSeverity.Error,
                fullPath,
                RelatedPath: fullPath));
            return AddBackupResult(fullPath, items);
        }
        catch (UnauthorizedAccessException exception)
        {
            items.Insert(0, new(
                ProjectDocumentDiagnosticCode.PrimaryFileUnreadable,
                ProjectDocumentDiagnosticSeverity.Error,
                exception.GetType().Name,
                RelatedPath: fullPath));
            return AddBackupResult(fullPath, items);
        }
        catch (IOException exception)
        {
            items.Insert(0, new(
                ProjectDocumentDiagnosticCode.PrimaryFileUnreadable,
                ProjectDocumentDiagnosticSeverity.Error,
                exception.GetType().Name,
                RelatedPath: fullPath));
            return AddBackupResult(fullPath, items);
        }
    }

    public ProjectDocumentRecoveryPreview? PreviewRecovery(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fullPath = Path.GetFullPath(projectPath);
        var sourcePath = fullPath + ".bak";
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(sourcePath);
        var document = _documentStore.Load(Encoding.UTF8.GetString(bytes));
        return new(
            fullPath,
            sourcePath,
            ComputeHash(bytes),
            document.Id,
            document.Name,
            document.Schema);
    }

    private ProjectDocumentDiagnosticReport AddBackupResult(
        string fullPath,
        List<ProjectDocumentDiagnosticItem> items)
    {
        var sourcePath = fullPath + ".bak";
        if (!File.Exists(sourcePath))
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.RecoveryUnavailable,
                ProjectDocumentDiagnosticSeverity.Information,
                sourcePath,
                RelatedPath: sourcePath));
            return new(fullPath, items, null);
        }

        try
        {
            var preview = PreviewRecovery(fullPath);
            if (preview is null)
            {
                items.Add(new(
                    ProjectDocumentDiagnosticCode.RecoveryUnavailable,
                    ProjectDocumentDiagnosticSeverity.Information,
                    sourcePath,
                    RelatedPath: sourcePath));
                return new(fullPath, items, null);
            }

            items.Add(new(
                ProjectDocumentDiagnosticCode.RecoveryAvailable,
                ProjectDocumentDiagnosticSeverity.Warning,
                preview.ProjectSchema,
                IsRepairable: true,
                RelatedPath: sourcePath));
            return new(fullPath, items, sourcePath);
        }
        catch (ProjectDocumentLoadException exception)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileInvalid,
                ProjectDocumentDiagnosticSeverity.Error,
                exception.ProjectSchema ?? string.Empty,
                RelatedPath: sourcePath));
            return new(fullPath, items, null);
        }
        catch (JsonException)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileInvalid,
                ProjectDocumentDiagnosticSeverity.Error,
                sourcePath,
                RelatedPath: sourcePath));
            return new(fullPath, items, null);
        }
        catch (UnauthorizedAccessException exception)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileUnreadable,
                ProjectDocumentDiagnosticSeverity.Error,
                exception.GetType().Name,
                RelatedPath: sourcePath));
            return new(fullPath, items, null);
        }
        catch (IOException exception)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileUnreadable,
                ProjectDocumentDiagnosticSeverity.Error,
                exception.GetType().Name,
                RelatedPath: sourcePath));
            return new(fullPath, items, null);
        }
    }

    private void AddInvalidBackupWarningIfPresent(
        string fullPath,
        List<ProjectDocumentDiagnosticItem> items)
    {
        var sourcePath = fullPath + ".bak";
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            _ = PreviewRecovery(fullPath);
        }
        catch (ProjectDocumentLoadException exception)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileInvalid,
                ProjectDocumentDiagnosticSeverity.Warning,
                exception.ProjectSchema ?? string.Empty,
                RelatedPath: sourcePath));
        }
        catch (JsonException)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileInvalid,
                ProjectDocumentDiagnosticSeverity.Warning,
                sourcePath,
                RelatedPath: sourcePath));
        }
        catch (UnauthorizedAccessException exception)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileUnreadable,
                ProjectDocumentDiagnosticSeverity.Warning,
                exception.GetType().Name,
                RelatedPath: sourcePath));
        }
        catch (IOException exception)
        {
            items.Add(new(
                ProjectDocumentDiagnosticCode.BackupFileUnreadable,
                ProjectDocumentDiagnosticSeverity.Warning,
                exception.GetType().Name,
                RelatedPath: sourcePath));
        }
    }

    private bool SameCanonicalContent(
        MachineProjectDocument left,
        MachineProjectDocument right) =>
        string.Equals(
            ComputeHash(Encoding.UTF8.GetBytes(_documentStore.SerializeForSaveEvidence(left))),
            ComputeHash(Encoding.UTF8.GetBytes(_documentStore.SerializeForSaveEvidence(right))),
            StringComparison.Ordinal);

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
