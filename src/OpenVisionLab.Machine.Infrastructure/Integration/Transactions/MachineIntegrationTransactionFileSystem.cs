using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Owns the shared filesystem rules for a Machine Integration transaction.
/// Every caller uses the same transaction paths, message writes, and
/// reparse-point checks.
/// </summary>
internal static class MachineIntegrationTransactionFileSystem
{
    internal static string GetTransactionDirectory(
        string exchangeRoot,
        Guid transactionId)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Transaction identity cannot be empty.",
                nameof(transactionId));
        }

        return Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            transactionId.ToString("D"));
    }

    internal static string GetQuarantineDirectory(string exchangeRoot) =>
        Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            ".quarantine");

    internal static string GetArtifactPath(
        string transactionDirectory,
        string relativePath)
    {
        var root = Path.GetFullPath(transactionDirectory);
        var path = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.UnsafeArtifactPath,
                "Artifact path escapes the transaction directory.");
        }

        return path;
    }

    internal static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.UnsafeArtifactPath,
                "Exchange directories cannot be symbolic links or reparse points.");
        }
    }

    internal static void EnsureNoReparsePoints(
        string transactionDirectory,
        string relativePath)
    {
        var current = Path.GetFullPath(transactionDirectory);
        EnsureDirectoryIsNotReparsePoint(current);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo entry = index == segments.Length - 1
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IntegrationContractException(
                    IntegrationErrorCode.UnsafeArtifactPath,
                    "Artifact paths cannot traverse symbolic links or reparse points.");
            }
        }
    }

    internal static long? TryGetAvailableFreeBytes(string path)
    {
        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(volumeRoot)
                ? null
                : new DriveInfo(volumeRoot).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static long GetDeclaredArtifactBytes(
        IReadOnlyList<IntegrationArtifactReference> artifacts)
    {
        try
        {
            return artifacts.Aggregate(
                0L,
                (total, artifact) => checked(total + artifact.ByteLength));
        }
        catch (OverflowException exception)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidArtifact,
                "The declared artifact byte total is too large.",
                exception);
        }
    }

    internal static DateTimeOffset GetLastWriteTimeUtc(string path) =>
        new(new DirectoryInfo(path).LastWriteTimeUtc, TimeSpan.Zero);

    internal static byte[] ReadMessage(
        string transactionDirectory,
        string fileName) => File.ReadAllBytes(Path.Combine(transactionDirectory, fileName));

    internal static void WriteMessage(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.SequentialScan);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
