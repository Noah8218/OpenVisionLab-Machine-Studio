using System.Text.Json;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Owns discovery, diagnostics, stale staging cleanup, and quarantine
/// retention for Machine Integration transactions.
/// </summary>
internal static class MachineIntegrationTransactionMaintenance
{
    private const string QuarantineDirectoryName = ".quarantine";
    private const string QuarantineManifestFileName = "quarantine.json";

    internal static IReadOnlyList<MachineIntegrationTransactionSummary> DiscoverTransactions(
        string exchangeRoot)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var transactionsRoot = Path.Combine(
            root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        if (!Directory.Exists(transactionsRoot))
        {
            return [];
        }

        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(root);
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(transactionsRoot);
        var transactions = new List<MachineIntegrationTransactionSummary>();
        foreach (var directory in Directory.EnumerateDirectories(transactionsRoot))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out var transactionId))
            {
                continue;
            }

            var handoffPath = Path.Combine(
                directory,
                IntegrationTransactionLayout.HandoffFileName);
            if (!File.Exists(handoffPath))
            {
                continue;
            }

            var handoff = ReadHandoffEnvelope(root, transactionId);
            transactions.Add(new(
                handoff,
                File.Exists(Path.Combine(
                    directory,
                    IntegrationTransactionLayout.AcknowledgementFileName)),
                File.Exists(Path.Combine(
                    directory,
                    IntegrationTransactionLayout.ResultFileName))));
        }

        return transactions
            .OrderByDescending(transaction => transaction.Handoff.CreatedAtUtc)
            .ToArray();
    }

    internal static IReadOnlyList<MachineIntegrationTransactionDiagnostic> DiagnoseTransactions(
        string exchangeRoot)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var transactionsRoot = Path.Combine(
            root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        if (!Directory.Exists(transactionsRoot))
        {
            return [];
        }

        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(root);
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(transactionsRoot);
        var availableFreeBytes = MachineIntegrationTransactionFileSystem.TryGetAvailableFreeBytes(root);
        var diagnostics = new List<MachineIntegrationTransactionDiagnostic>();
        foreach (var directory in Directory.EnumerateDirectories(transactionsRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(
                    name,
                    QuarantineDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(directory);
                foreach (var quarantineDirectory in Directory.EnumerateDirectories(directory))
                {
                    diagnostics.Add(DiagnoseQuarantineDirectory(
                        quarantineDirectory,
                        availableFreeBytes));
                }

                continue;
            }

            if (Guid.TryParse(name, out var transactionId))
            {
                diagnostics.Add(DiagnosePublishedDirectory(
                    root,
                    directory,
                    transactionId,
                    availableFreeBytes));
                continue;
            }

            if (TryParseStagingDirectoryName(name, out transactionId))
            {
                diagnostics.Add(DiagnoseStagingDirectory(
                    directory,
                    transactionId,
                    availableFreeBytes));
                continue;
            }

            diagnostics.Add(CreateDiagnostic(
                null,
                MachineIntegrationTransactionState.Invalid,
                directory,
                availableFreeBytes,
                detail: "Unknown transaction directory name."));
        }

        return diagnostics
            .OrderByDescending(diagnostic => diagnostic.LastWriteTimeUtc)
            .ToArray();
    }

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

        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
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
            if (!TryParseStagingDirectoryName(
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

            var stagedHandoff = TryReadStagedHandoff(directory);
            if (TryQuarantineStagingDirectory(
                    transactionsRoot,
                    directory,
                    stagedHandoff,
                    "stale-staging",
                    null,
                    null,
                    GetReferencedArtifactBytes(
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
            DiagnoseTransactions(root));
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

        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
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
            ?? (TryParseStagingDirectoryName(
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
            var manifest = new QuarantineManifest(
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

    private static MachineIntegrationTransactionDiagnostic DiagnosePublishedDirectory(
        string root,
        string directory,
        Guid transactionId,
        long? availableFreeBytes)
    {
        var state = MachineIntegrationTransactionState.Published;
        var artifactCount = 0;
        var declaredBytes = 0L;
        var materializedBytes = 0L;
        string? detail = null;
        try
        {
            MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(directory);
            var handoff = ReadHandoffEnvelope(root, transactionId);
            artifactCount = handoff.Context.Artifacts.Count;
            declaredBytes = MachineIntegrationTransactionFileSystem.GetDeclaredArtifactBytes(handoff.Context.Artifacts);
            materializedBytes = GetReferencedArtifactBytes(
                directory,
                handoff.Context.Artifacts);
            foreach (var artifact in handoff.Context.Artifacts)
            {
                MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(directory, artifact.RelativePath);
                ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                    artifact,
                    directory));
            }
        }
        catch (Exception exception)
        {
            state = MachineIntegrationTransactionState.Invalid;
            detail = DescribeException(exception);
        }

        return CreateDiagnostic(
            transactionId,
            state,
            directory,
            availableFreeBytes,
            artifactCount,
            declaredBytes,
            materializedBytes,
            detail);
    }

    private static MachineIntegrationTransactionDiagnostic DiagnoseStagingDirectory(
        string directory,
        Guid transactionId,
        long? availableFreeBytes)
    {
        var handoff = TryReadStagedHandoff(directory);
        var artifacts = handoff?.Context.Artifacts;
        var detail = handoff is null
            ? "Staging directory has no readable Handoff."
            : "Transaction has not been atomically published.";
        return CreateDiagnostic(
            transactionId,
            MachineIntegrationTransactionState.Staging,
            directory,
            availableFreeBytes,
            artifacts?.Count ?? 0,
            artifacts is null ? 0 : MachineIntegrationTransactionFileSystem.GetDeclaredArtifactBytes(artifacts),
            GetReferencedArtifactBytes(directory, artifacts),
            detail);
    }

    private static MachineIntegrationTransactionDiagnostic DiagnoseQuarantineDirectory(
        string directory,
        long? availableFreeBytes)
    {
        var manifest = TryReadQuarantineManifest(directory);
        var transactionId = manifest?.TransactionId
            ?? (TryParseQuarantineDirectoryName(
                    Path.GetFileName(directory),
                    out var parsedId)
                ? parsedId
                : null);
        var detail = manifest is null
            ? "Quarantine manifest is unavailable."
            : string.IsNullOrWhiteSpace(manifest.Message)
                ? manifest.Reason
                : $"{manifest.Reason}: {manifest.Message}";
        return CreateDiagnostic(
            transactionId,
            MachineIntegrationTransactionState.Quarantined,
            directory,
            availableFreeBytes,
            manifest?.ArtifactCount ?? 0,
            manifest?.DeclaredBytes ?? 0,
            manifest?.MaterializedBytes ?? GetTotalFileBytes(directory),
            detail);
    }

    private static MachineIntegrationTransactionDiagnostic CreateDiagnostic(
        Guid? transactionId,
        MachineIntegrationTransactionState state,
        string directory,
        long? availableFreeBytes,
        int artifactCount = 0,
        long declaredBytes = 0,
        long materializedBytes = 0,
        string? detail = null) =>
        new(
            transactionId,
            state,
            directory,
            MachineIntegrationTransactionFileSystem.GetLastWriteTimeUtc(directory),
            artifactCount,
            declaredBytes,
            materializedBytes,
            availableFreeBytes,
            detail);

    private static IntegrationHandoffV2 ReadHandoffEnvelope(
        string exchangeRoot,
        Guid transactionId)
    {
        var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(
            exchangeRoot,
            transactionId);
        var handoff = IntegrationContractJson.DeserializeHandoffV2(
            MachineIntegrationTransactionFileSystem.ReadMessage(
                transactionDirectory,
                IntegrationTransactionLayout.HandoffFileName));
        if (handoff.TransactionId != transactionId)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.CorrelationMismatch,
                "Handoff transaction identity does not match its directory.");
        }

        return handoff;
    }

    private static IntegrationHandoffV2? TryReadStagedHandoff(string directory)
    {
        try
        {
            var path = Path.Combine(
                directory,
                IntegrationTransactionLayout.HandoffFileName);
            return File.Exists(path)
                ? IntegrationContractJson.DeserializeHandoffV2(File.ReadAllBytes(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static QuarantineManifest? TryReadQuarantineManifest(string directory)
    {
        try
        {
            var path = Path.Combine(directory, QuarantineManifestFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<QuarantineManifest>(File.ReadAllBytes(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static long GetReferencedArtifactBytes(
        string transactionDirectory,
        IReadOnlyList<IntegrationArtifactReference>? artifacts)
    {
        if (artifacts is null)
        {
            return 0;
        }

        var total = 0L;
        foreach (var artifact in artifacts)
        {
            try
            {
                var path = MachineIntegrationTransactionFileSystem.GetArtifactPath(
                    transactionDirectory,
                    artifact.RelativePath);
                MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(
                    transactionDirectory,
                    artifact.RelativePath);
                if (File.Exists(path))
                {
                    total = checked(total + new FileInfo(path).Length);
                }
            }
            catch
            {
                // Diagnostics report the bytes that can be safely observed.
            }
        }

        return total;
    }

    private static long GetTotalFileBytes(string directory)
    {
        var total = 0L;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || string.Equals(
                        info.Name,
                        QuarantineManifestFileName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                total = checked(total + info.Length);
            }
        }
        catch
        {
            return total;
        }

        return total;
    }

    private static bool TryParseStagingDirectoryName(
        string? name,
        out Guid transactionId)
    {
        transactionId = Guid.Empty;
        if (string.IsNullOrEmpty(name)
            || name[0] != '.'
            || !name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)
            || name.Length < 1 + 36 + 1 + 1 + ".staging".Length)
        {
            return false;
        }

        if (!Guid.TryParseExact(name.Substring(1, 36), "D", out transactionId)
            || name[37] != '.')
        {
            transactionId = Guid.Empty;
            return false;
        }

        return true;
    }

    private static bool TryParseQuarantineDirectoryName(
        string? name,
        out Guid transactionId)
    {
        transactionId = Guid.Empty;
        return !string.IsNullOrEmpty(name)
            && name.Length >= 36
            && Guid.TryParseExact(name[..36], "D", out transactionId);
    }

    private static void TryWriteQuarantineManifest(
        string quarantineDirectory,
        QuarantineManifest manifest)
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

    private static string DescribeException(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {exception.Message}";

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty path is required.", parameterName)
            : value.Trim();

    private static void ThrowIfInvalid(IntegrationValidationResult validation)
    {
        if (validation.IsValid)
        {
            return;
        }

        var issue = validation.Issues[0];
        throw new IntegrationContractException(
            issue.Code,
            $"{issue.Field}: {issue.Message}");
    }

    private sealed record QuarantineManifest(
        Guid TransactionId,
        string Reason,
        string? ExceptionType,
        string? Message,
        DateTimeOffset QuarantinedAtUtc,
        int ArtifactCount,
        long DeclaredBytes,
        long MaterializedBytes);
}
