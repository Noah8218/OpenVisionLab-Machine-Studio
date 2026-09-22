using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Persistence.Projects;

/// <summary>
/// Owns file-system persistence for a machine project document.
/// JSON serialization and schema normalization stay with the Core document store;
/// this adapter adds paths, atomic replacement, backup recovery, and cancellation.
/// </summary>
public sealed class ProjectDocumentFileStore
{
    internal const string TemporaryCleanupPathDataKey =
        "OpenVisionLab.Machine.Persistence.ProjectDocumentFileStore.TemporaryCleanupPath";
    internal const string TemporaryCleanupFailureDataKey =
        "OpenVisionLab.Machine.Persistence.ProjectDocumentFileStore.TemporaryCleanupFailure";

    private readonly ProjectDocumentStore _documentStore;
    private readonly Action<string>? _afterTemporaryFileWritten;
    private readonly Action<string>? _beforeCommit;
    private readonly Action<string>? _deleteTemporaryFile;

    public ProjectDocumentFileStore(ProjectDocumentStore? documentStore = null)
        : this(
            documentStore,
            afterTemporaryFileWritten: null,
            beforeCommit: null,
            deleteTemporaryFile: null)
    {
    }

    internal ProjectDocumentFileStore(
        ProjectDocumentStore? documentStore,
        Action<string>? afterTemporaryFileWritten,
        Action<string>? beforeCommit,
        Action<string>? deleteTemporaryFile = null)
    {
        _documentStore = documentStore ?? new ProjectDocumentStore();
        _afterTemporaryFileWritten = afterTemporaryFileWritten;
        _beforeCommit = beforeCommit;
        _deleteTemporaryFile = deleteTemporaryFile;
    }

    public async Task SaveAsync(
        MachineProjectDocument document,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The project path must include a directory.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var modifiedAt = DateTimeOffset.UtcNow;
        var json = _documentStore.SerializeForSave(document, modifiedAt);
        Exception? saveException = null;

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            _afterTemporaryFileWritten?.Invoke(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            // Commit begins after this observation; File.Replace/File.Move is synchronous,
            // and caller cancellation cannot undo a replacement that has started.
            _beforeCommit?.Invoke(fullPath);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, fullPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            document.Schema = MachineProjectDocument.CurrentSchema;
            document.ModifiedAt = modifiedAt;
        }
        catch (Exception exception)
        {
            saveException = exception;
            throw;
        }
        finally
        {
            try
            {
                (_deleteTemporaryFile ?? File.Delete)(temporaryPath);
            }
            catch (Exception cleanupException)
            {
                AttachTemporaryCleanupDiagnostic(
                    saveException ?? cleanupException,
                    temporaryPath,
                    cleanupException);
                if (saveException is null)
                {
                    throw;
                }
            }
        }
    }

    public async Task<MachineProjectDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
        => (await LoadWithProvenanceAsync(path, cancellationToken).ConfigureAwait(false)).Document;

    public async Task<ProjectDocumentLoadResult> LoadWithProvenanceAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            var document = await LoadFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new(
                document,
                fullPath,
                fullPath,
                ProjectDocumentLoadSource.Primary);
        }
        catch (Exception primaryException) when (ShouldTryBackup(primaryException))
        {
            try
            {
                var backupPath = fullPath + ".bak";
                var document = await LoadFileAsync(backupPath, cancellationToken).ConfigureAwait(false);
                return new(
                    document,
                    fullPath,
                    backupPath,
                    ProjectDocumentLoadSource.Backup,
                    GetRecoveryReason(primaryException));
            }
            catch (Exception backupException) when (IsExpectedLoadFailure(backupException))
            {
                if (backupException is ProjectDocumentLoadException
                    {
                        ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema
                    })
                {
                    ExceptionDispatchInfo.Capture(backupException).Throw();
                }

                ExceptionDispatchInfo.Capture(primaryException).Throw();
                throw;
            }
        }
    }

    public MachineProjectDocument Load(string path) => LoadWithProvenance(path).Document;

    public ProjectDocumentLoadResult LoadWithProvenance(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            var document = LoadFile(fullPath);
            return new(
                document,
                fullPath,
                fullPath,
                ProjectDocumentLoadSource.Primary);
        }
        catch (Exception primaryException) when (ShouldTryBackup(primaryException))
        {
            try
            {
                var backupPath = fullPath + ".bak";
                var document = LoadFile(backupPath);
                return new(
                    document,
                    fullPath,
                    backupPath,
                    ProjectDocumentLoadSource.Backup,
                    GetRecoveryReason(primaryException));
            }
            catch (Exception backupException) when (IsExpectedLoadFailure(backupException))
            {
                if (backupException is ProjectDocumentLoadException
                    {
                        ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema
                    })
                {
                    ExceptionDispatchInfo.Capture(backupException).Throw();
                }

                ExceptionDispatchInfo.Capture(primaryException).Throw();
                throw;
            }
        }
    }

    public async Task<string> RestoreBackupAsync(
        ProjectDocumentRecoveryPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var fullPath = Path.GetFullPath(preview.ProjectPath);
        var backupPath = Path.GetFullPath(preview.SourcePath);
        var expectedBackupPath = fullPath + ".bak";
        if (!string.Equals(backupPath, expectedBackupPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The recovery source must be the project's .bak file.", nameof(preview));
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The project path must include a directory.", nameof(preview));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.recovery.tmp");
        var bytes = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, preview.SourceHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The recovery source changed after the preview was created.");
        }

        _ = _documentStore.Load(Encoding.UTF8.GetString(bytes));
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<MachineProjectDocument> LoadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return _documentStore.Load(json);
    }

    private MachineProjectDocument LoadFile(string path) =>
        _documentStore.Load(File.ReadAllText(path));

    private static void AttachTemporaryCleanupDiagnostic(
        Exception target,
        string temporaryPath,
        Exception cleanupException)
    {
        target.Data[TemporaryCleanupPathDataKey] = temporaryPath;
        target.Data[TemporaryCleanupFailureDataKey] = cleanupException.ToString();
    }

    private static bool ShouldTryBackup(Exception exception) => exception switch
    {
        ProjectDocumentLoadException
        {
            ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema
        } => false,
        _ => IsExpectedLoadFailure(exception)
    };

    private static ProjectDocumentRecoveryReason GetRecoveryReason(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => ProjectDocumentRecoveryReason.PrimaryMissing,
        UnauthorizedAccessException or IOException => ProjectDocumentRecoveryReason.PrimaryUnreadable,
        _ => ProjectDocumentRecoveryReason.PrimaryInvalid
    };

    private static bool IsExpectedLoadFailure(Exception exception) => exception switch
    {
        ProjectDocumentLoadException => true,
        JsonException => true,
        IOException => true,
        UnauthorizedAccessException => true,
        ArgumentOutOfRangeException => true,
        _ => false
    };
}
