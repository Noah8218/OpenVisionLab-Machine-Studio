using System.Text.Json;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Owns mutating staging and quarantine retention operations for Machine
/// Integration transactions.
/// </summary>
internal static class MachineIntegrationTransactionRetention
{
    private const string QuarantineDirectoryName = ".quarantine";
    private const string QuarantineManifestFileName = "quarantine.json";

    internal static MachineIntegrationCleanupReport CleanupStaging(
        string exchangeRoot,
        TimeSpan staleAfter,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (staleAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfter),
                "The staging retention period cannot be negative.");
        }

        var root = Path.GetFullPath(
            MachineIntegrationTransactionInspector.RequireText(exchangeRoot, nameof(exchangeRoot)));
        var transactionsRoot = Path.Combine(
            root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        if (!Directory.Exists(transactionsRoot))
        {
            return new(0, 0, []);
        }

        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(root);
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(transactionsRoot);
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - staleAfter;
        var scanned = 0;
        var quarantined = 0;
        foreach (var directory in Directory.EnumerateDirectories(transactionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MachineIntegrationTransactionInspector.TryParseStagingDirectoryName(
                    Path.GetFileName(directory),
                    out var transactionId))
            {
                continue;
            }

            scanned++;
            if (MachineIntegrationTransactionFileSystem.GetLastWriteTimeUtc(directory) > cutoff)
            {
                continue;
            }

            var stagedHandoff = MachineIntegrationTransactionInspector.TryReadStagedHandoff(directory);
            if (TryQuarantineStagingDirectory(
                    transactionsRoot,
                    directory,
                    stagedHandoff,
                    "stale-staging",
                    null,
                    null,
                    MachineIntegrationTransactionInspector.GetReferencedArtifactBytes(
                        directory,
                        stagedHandoff?.Context.Artifacts),
                    transactionId))
            {
                quarantined++;
            }
        }

        return new(
            scanned,
            quarantined,
            MachineIntegrationTransactionInspector.DiagnoseTransactions(root));
    }

    internal static int PurgeQuarantine(
        string exchangeRoot,
        TimeSpan retention,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (retention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "The quarantine retention period cannot be negative.");
        }

        var root = Path.GetFullPath(
            MachineIntegrationTransactionInspector.RequireText(exchangeRoot, nameof(exchangeRoot)));
        var quarantineRoot = MachineIntegrationTransactionFileSystem.GetQuarantineDirectory(root);
        if (!Directory.Exists(quarantineRoot))
        {
            return 0;
        }

        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(root);
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(quarantineRoot);
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - retention;
        var purged = 0;
        foreach (var directory in Directory.EnumerateDirectories(quarantineRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MachineIntegrationTransactionFileSystem.GetLastWriteTimeUtc(directory) > cutoff)
            {
                continue;
            }

            MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(directory);
            Directory.Delete(directory, recursive: true);
            purged++;
        }

        return purged;
    }

    internal static bool TryQuarantineStagingDirectory(
        string transactionsRoot,
        string stagingDirectory,
        IntegrationHandoffV2? handoff,
        string reason,
        Exception? exception,
        IProgress<MachineIntegrationTransferProgress>? progress,
        long materializedBytes,
        Guid? transactionIdOverride = null)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            return false;
        }

        var transactionId = handoff?.TransactionId
            ?? transactionIdOverride
            ?? (MachineIntegrationTransactionInspector.TryParseStagingDirectoryName(
                    Path.GetFileName(stagingDirectory),
                    out var parsedId)
                ? parsedId
                : Guid.NewGuid());
        var quarantineRoot = Path.Combine(
            transactionsRoot,
            QuarantineDirectoryName);
        try
        {
            Directory.CreateDirectory(quarantineRoot);
            MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(quarantineRoot);
            var quarantineDirectory = Path.Combine(
                quarantineRoot,
                $"{transactionId:D}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            Directory.Move(stagingDirectory, quarantineDirectory);

            var declaredBytes = handoff is null
                ? 0
                : MachineIntegrationTransactionFileSystem.GetDeclaredArtifactBytes(handoff.Context.Artifacts);
            var manifest = new MachineIntegrationQuarantineManifest(
                transactionId,
                reason,
                exception?.GetType().FullName,
                exception?.Message,
                DateTimeOffset.UtcNow,
                handoff?.Context.Artifacts.Count ?? 0,
                declaredBytes,
                materializedBytes);
            TryWriteQuarantineManifest(quarantineDirectory, manifest);
            TryReportProgress(
                progress,
                transactionId,
                MachineIntegrationTransferPhase.Quarantined,
                null,
                materializedBytes,
                declaredBytes,
                0,
                manifest.ArtifactCount);
            return true;
        }
        catch
        {
            // Keep the original publish or cleanup failure and leave staging in place for diagnosis.
            return false;
        }
    }

    private static void TryWriteQuarantineManifest(
        string quarantineDirectory,
        MachineIntegrationQuarantineManifest manifest)
    {
        try
        {
            MachineIntegrationTransactionFileSystem.WriteMessage(
                Path.Combine(quarantineDirectory, QuarantineManifestFileName),
                JsonSerializer.SerializeToUtf8Bytes(manifest));
        }
        catch
        {
            // The quarantined bytes remain the primary evidence if the manifest cannot be written.
        }
    }

    private static void TryReportProgress(
        IProgress<MachineIntegrationTransferProgress>? progress,
        Guid transactionId,
        MachineIntegrationTransferPhase phase,
        string? artifactId,
        long bytesCopied,
        long totalBytes,
        int completedArtifacts,
        int artifactCount)
    {
        try
        {
            progress?.Report(new(
                transactionId,
                phase,
                artifactId,
                bytesCopied,
                totalBytes,
                completedArtifacts,
                artifactCount));
        }
        catch
        {
            // A completion observer cannot turn an already-published transaction into a failure.
        }
    }
}
