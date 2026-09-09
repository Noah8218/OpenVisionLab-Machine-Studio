using System.Runtime.ExceptionServices;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Persistence.Projects;

/// <summary>
/// Owns file-system persistence for a machine project document.
/// JSON serialization and schema normalization stay with the Core document store;
/// this adapter adds paths, atomic replacement, backup recovery, and cancellation.
/// </summary>
public sealed class ProjectDocumentFileStore
{
    private readonly ProjectDocumentStore _documentStore;

    public ProjectDocumentFileStore(ProjectDocumentStore? documentStore = null)
    {
        _documentStore = documentStore ?? new ProjectDocumentStore();
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

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<MachineProjectDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            return await LoadFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryException) when (ShouldTryBackup(primaryException))
        {
            try
            {
                return await LoadFileAsync(fullPath + ".bak", cancellationToken).ConfigureAwait(false);
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

    public MachineProjectDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            return LoadFile(fullPath);
        }
        catch (Exception primaryException) when (ShouldTryBackup(primaryException))
        {
            try
            {
                return LoadFile(fullPath + ".bak");
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

    private async Task<MachineProjectDocument> LoadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return _documentStore.Load(json);
    }

    private MachineProjectDocument LoadFile(string path) =>
        _documentStore.Load(File.ReadAllText(path));

    private static bool ShouldTryBackup(Exception exception) => exception switch
    {
        ProjectDocumentLoadException
        {
            ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema
        } => false,
        _ => IsExpectedLoadFailure(exception)
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
