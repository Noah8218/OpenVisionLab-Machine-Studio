using System.Text.Json;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public sealed record MachineIntegrationTransactionSummary(
    IntegrationHandoffV2 Handoff,
    bool HasAcknowledgement,
    bool HasResult);

/// <summary>
/// Owns the Machine Studio side of the v2 file exchange. A Handoff is staged,
/// copied artifacts are checked against their declared identity, and the
/// complete transaction directory is published with one directory move.
/// Reading and importing never starts a simulation, loads a recipe, or runs an
/// inspection.
/// </summary>
public static class MachineIntegrationExchange
{
    public const long DefaultMinimumFreeSpaceBytes = 1_048_576;

    private const string QuarantineDirectoryName = ".quarantine";
    private const string QuarantineManifestFileName = "quarantine.json";

    public static IntegrationHandoffV2 PublishHandoff(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IReadOnlyDictionary<string, string> artifactSourcePaths) =>
        PublishHandoffAsync(
            exchangeRoot,
            handoff,
            artifactSourcePaths)
            .GetAwaiter()
            .GetResult();

    public static async Task<IntegrationHandoffV2> PublishHandoffAsync(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IReadOnlyDictionary<string, string> artifactSourcePaths,
        IProgress<MachineIntegrationTransferProgress>? progress = null,
        long minimumFreeSpaceBytes = DefaultMinimumFreeSpaceBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(artifactSourcePaths);
        if (minimumFreeSpaceBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumFreeSpaceBytes),
                "The minimum free-space requirement cannot be negative.");
        }

        ThrowIfInvalid(IntegrationContractValidator.Validate(handoff));
        RequireProducer(handoff.Producer, IntegrationApplicationIds.MachineStudio);

        var artifacts = handoff.Context.Artifacts;
        if (!artifacts.Any(artifact => string.Equals(
                artifact.Role,
                IntegrationArtifactRoles.MachineProject,
                StringComparison.Ordinal)))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidArtifact,
                "A Machine Studio Handoff requires a machine-project artifact.");
        }

        foreach (var artifact in artifacts)
        {
            ValidateArtifactPath(artifact);
        }

        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var transactionsRoot = Path.Combine(
            root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        EnsureDirectoryIsNotReparsePoint(root);
        EnsureDirectoryIsNotReparsePoint(transactionsRoot);

        var transactionDirectory = GetTransactionDirectory(root, handoff.TransactionId);
        if (Directory.Exists(transactionDirectory)
            || File.Exists(transactionDirectory))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidState,
                "The transaction identity has already been published.");
        }

        var handoffBytes = IntegrationContractJson.SerializeCanonical(handoff);
        var declaredBytes = GetDeclaredBytes(artifacts);
        var requiredFreeSpace = GetRequiredFreeSpace(
            declaredBytes,
            handoffBytes.LongLength,
            minimumFreeSpaceBytes);
        EnsureFreeSpace(root, requiredFreeSpace);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(transactionsRoot);
        EnsureDirectoryIsNotReparsePoint(transactionsRoot);

        var stagingDirectory = Path.Combine(
            transactionsRoot,
            $".{handoff.TransactionId:D}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingDirectory);
        var completedArtifacts = 0;
        var bytesCopied = 0L;
        ReportProgress(
            progress,
            handoff.TransactionId,
            MachineIntegrationTransferPhase.Preflight,
            null,
            bytesCopied,
            declaredBytes,
            completedArtifacts,
            artifacts.Count);
        try
        {
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!artifactSourcePaths.TryGetValue(
                        artifact.ArtifactId,
                        out var sourcePath)
                    || string.IsNullOrWhiteSpace(sourcePath))
                {
                    throw new IntegrationContractException(
                        IntegrationErrorCode.ArtifactMissing,
                        $"No source file was supplied for artifact '{artifact.ArtifactId}'.");
                }

                var copied = await CopyArtifactAsync(
                        sourcePath,
                        stagingDirectory,
                        artifact,
                        handoff.TransactionId,
                        declaredBytes,
                        bytesCopied,
                        completedArtifacts,
                        artifacts.Count,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                bytesCopied = checked(bytesCopied + copied);
                completedArtifacts++;
                ReportProgress(
                    progress,
                    handoff.TransactionId,
                    MachineIntegrationTransferPhase.Copying,
                    artifact.ArtifactId,
                    bytesCopied,
                    declaredBytes,
                    completedArtifacts,
                    artifacts.Count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteMessage(
                Path.Combine(
                    stagingDirectory,
                    IntegrationTransactionLayout.HandoffFileName),
                handoffBytes);

            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    progress,
                    handoff.TransactionId,
                    MachineIntegrationTransferPhase.Validating,
                    artifact.ArtifactId,
                    bytesCopied,
                    declaredBytes,
                    completedArtifacts,
                    artifacts.Count);
                EnsureNoReparsePoints(stagingDirectory, artifact.RelativePath);
                ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                    artifact,
                    stagingDirectory));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, transactionDirectory);
            TryReportProgress(
                progress,
                handoff.TransactionId,
                MachineIntegrationTransferPhase.Published,
                null,
                bytesCopied,
                declaredBytes,
                completedArtifacts,
                artifacts.Count);
            return handoff;
        }
        catch (Exception exception)
        {
            TryQuarantineStagingDirectory(
                transactionsRoot,
                stagingDirectory,
                handoff,
                exception is OperationCanceledException
                    ? "cancelled"
                    : "publish-failed",
                exception,
                progress,
                bytesCopied);
            throw;
        }
    }

    public static IReadOnlyList<MachineIntegrationTransactionSummary> DiscoverTransactions(
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

    public static IntegrationHandoffV2 ReadHandoff(
        string exchangeRoot,
        Guid transactionId)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var handoff = ReadHandoffEnvelope(root, transactionId);
        var transactionDirectory = GetTransactionDirectory(root, transactionId);
        foreach (var artifact in handoff.Context.Artifacts)
        {
            EnsureNoReparsePoints(transactionDirectory, artifact.RelativePath);
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                artifact,
                transactionDirectory));
        }

        return handoff;
    }

    public static IntegrationHandoffV2 ReadHandoffEnvelope(
        string exchangeRoot,
        Guid transactionId)
    {
        var transactionDirectory = GetTransactionDirectory(
            Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot))),
            transactionId);
        var handoff = IntegrationContractJson.DeserializeHandoffV2(
            ReadMessage(
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

    public static IntegrationAcknowledgementV2 ReadAcknowledgement(
        string exchangeRoot,
        Guid transactionId)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var handoff = ReadHandoff(root, transactionId);
        var transactionDirectory = GetTransactionDirectory(root, transactionId);
        var acknowledgement = IntegrationContractJson.DeserializeAcknowledgementV2(
            ReadMessage(
                transactionDirectory,
                IntegrationTransactionLayout.AcknowledgementFileName));
        ThrowIfInvalid(IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            acknowledgement));
        return acknowledgement;
    }

    public static IntegrationResultV2 ReadResult(
        string exchangeRoot,
        Guid transactionId)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var handoff = ReadHandoff(root, transactionId);
        var transactionDirectory = GetTransactionDirectory(root, transactionId);
        var acknowledgement = IntegrationContractJson.DeserializeAcknowledgementV2(
            ReadMessage(
                transactionDirectory,
                IntegrationTransactionLayout.AcknowledgementFileName));
        var result = IntegrationContractJson.DeserializeResultV2(
            ReadMessage(
                transactionDirectory,
                IntegrationTransactionLayout.ResultFileName));
        ThrowIfInvalid(IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            acknowledgement,
            result));

        if (result.RunRecord is not null)
        {
            EnsureNoReparsePoints(
                transactionDirectory,
                result.RunRecord.RelativePath);
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                result.RunRecord,
                transactionDirectory));
        }
        foreach (var evidence in result.Evidence)
        {
            EnsureNoReparsePoints(transactionDirectory, evidence.RelativePath);
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                evidence,
                transactionDirectory));
        }

        return result;
    }

    public static IReadOnlyList<MachineIntegrationTransactionDiagnostic> DiagnoseTransactions(
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

        EnsureDirectoryIsNotReparsePoint(root);
        EnsureDirectoryIsNotReparsePoint(transactionsRoot);
        var availableFreeBytes = TryGetAvailableFreeBytes(root);
        var diagnostics = new List<MachineIntegrationTransactionDiagnostic>();
        foreach (var directory in Directory.EnumerateDirectories(transactionsRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(
                    name,
                    QuarantineDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                EnsureDirectoryIsNotReparsePoint(directory);
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

    public static MachineIntegrationCleanupReport CleanupStaging(
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

        EnsureDirectoryIsNotReparsePoint(root);
        EnsureDirectoryIsNotReparsePoint(transactionsRoot);
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
            if (GetLastWriteTimeUtc(directory) > cutoff)
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

    public static int PurgeQuarantine(
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
        var quarantineRoot = GetQuarantineDirectory(root);
        if (!Directory.Exists(quarantineRoot))
        {
            return 0;
        }

        EnsureDirectoryIsNotReparsePoint(root);
        EnsureDirectoryIsNotReparsePoint(quarantineRoot);
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - retention;
        var purged = 0;
        foreach (var directory in Directory.EnumerateDirectories(quarantineRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetLastWriteTimeUtc(directory) > cutoff)
            {
                continue;
            }

            EnsureDirectoryIsNotReparsePoint(directory);
            Directory.Delete(directory, recursive: true);
            purged++;
        }

        return purged;
    }

    private static async Task<long> CopyArtifactAsync(
        string sourcePath,
        string transactionDirectory,
        IntegrationArtifactReference artifact,
        Guid transactionId,
        long totalBytes,
        long bytesCopiedBeforeArtifact,
        int completedArtifacts,
        int artifactCount,
        IProgress<MachineIntegrationTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"Source artifact was not found for '{artifact.ArtifactId}'.",
                source);
        }

        var target = GetArtifactPath(transactionDirectory, artifact.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        EnsureNoReparsePoints(transactionDirectory, artifact.RelativePath);

        await using var input = new FileStream(
            source,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                BufferSize = 64 * 1024,
                Mode = FileMode.Open,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.Read
            });
        await using var output = new FileStream(
            target,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                BufferSize = 64 * 1024,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.Read
            });

        var buffer = new byte[64 * 1024];
        var copied = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
            copied = checked(copied + read);
            ReportProgress(
                progress,
                transactionId,
                MachineIntegrationTransferPhase.Copying,
                artifact.ArtifactId,
                checked(bytesCopiedBeforeArtifact + copied),
                totalBytes,
                completedArtifacts,
                artifactCount);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return copied;
    }

    private static void ValidateArtifactPath(IntegrationArtifactReference artifact)
    {
        var relativePath = artifact.RelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.UnsafeArtifactPath,
                "Artifact path cannot be empty.");
        }

        var fileName = relativePath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(fileName)
            || fileName.EndsWith(' ')
            || fileName.EndsWith('.')
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || IsReservedWindowsFileName(fileName))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.UnsafeArtifactPath,
                $"Artifact file name '{fileName}' is not safe for the local file exchange.");
        }
    }

    private static bool IsReservedWindowsFileName(string fileName)
    {
        var stem = fileName.TrimEnd(' ', '.');
        var extensionIndex = stem.IndexOf('.');
        if (extensionIndex >= 0)
        {
            stem = stem[..extensionIndex];
        }

        var upper = stem.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4
                && (upper.StartsWith("COM", StringComparison.Ordinal)
                    || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] is >= '1' and <= '9');
    }

    private static long GetDeclaredBytes(
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

    private static long GetRequiredFreeSpace(
        long declaredBytes,
        long handoffBytes,
        long minimumFreeSpaceBytes)
    {
        try
        {
            return checked(declaredBytes + handoffBytes + minimumFreeSpaceBytes);
        }
        catch (OverflowException exception)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidState,
                "The transaction free-space requirement is too large.",
                exception);
        }
    }

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        var availableBytes = TryGetAvailableFreeBytes(path);
        if (availableBytes.HasValue && availableBytes.Value < requiredBytes)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidState,
                $"Insufficient free space for the transaction: required {requiredBytes} bytes, available {availableBytes.Value} bytes.");
        }
    }

    private static long? TryGetAvailableFreeBytes(string path)
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

    private static void ReportProgress(
        IProgress<MachineIntegrationTransferProgress>? progress,
        Guid transactionId,
        MachineIntegrationTransferPhase phase,
        string? artifactId,
        long bytesCopied,
        long totalBytes,
        int completedArtifacts,
        int artifactCount) =>
        progress?.Report(new(
            transactionId,
            phase,
            artifactId,
            bytesCopied,
            totalBytes,
            completedArtifacts,
            artifactCount));

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
            ReportProgress(
                progress,
                transactionId,
                phase,
                artifactId,
                bytesCopied,
                totalBytes,
                completedArtifacts,
                artifactCount);
        }
        catch
        {
            // A completion observer cannot turn an already-published transaction into a failure.
        }
    }

    private static bool TryQuarantineStagingDirectory(
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
            EnsureDirectoryIsNotReparsePoint(quarantineRoot);
            var quarantineDirectory = Path.Combine(
                quarantineRoot,
                $"{transactionId:D}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            Directory.Move(stagingDirectory, quarantineDirectory);

            var declaredBytes = handoff is null
                ? 0
                : GetDeclaredBytes(handoff.Context.Artifacts);
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

    private static void TryWriteQuarantineManifest(
        string quarantineDirectory,
        QuarantineManifest manifest)
    {
        try
        {
            WriteMessage(
                Path.Combine(quarantineDirectory, QuarantineManifestFileName),
                JsonSerializer.SerializeToUtf8Bytes(manifest));
        }
        catch
        {
            // The quarantined bytes remain the primary evidence if the manifest cannot be written.
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
            EnsureDirectoryIsNotReparsePoint(directory);
            var handoff = ReadHandoffEnvelope(root, transactionId);
            artifactCount = handoff.Context.Artifacts.Count;
            declaredBytes = GetDeclaredBytes(handoff.Context.Artifacts);
            materializedBytes = GetReferencedArtifactBytes(
                directory,
                handoff.Context.Artifacts);
            foreach (var artifact in handoff.Context.Artifacts)
            {
                EnsureNoReparsePoints(directory, artifact.RelativePath);
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
            artifacts is null ? 0 : GetDeclaredBytes(artifacts),
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
            GetLastWriteTimeUtc(directory),
            artifactCount,
            declaredBytes,
            materializedBytes,
            availableFreeBytes,
            detail);

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
                var path = GetArtifactPath(transactionDirectory, artifact.RelativePath);
                EnsureNoReparsePoints(transactionDirectory, artifact.RelativePath);
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

    private static DateTimeOffset GetLastWriteTimeUtc(string path) =>
        new(new DirectoryInfo(path).LastWriteTimeUtc, TimeSpan.Zero);

    private static string GetQuarantineDirectory(string exchangeRoot) =>
        Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            QuarantineDirectoryName);

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.UnsafeArtifactPath,
                "Exchange directories cannot be symbolic links or reparse points.");
        }
    }

    private static string DescribeException(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {exception.Message}";

    private static string GetArtifactPath(
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

    private static void EnsureNoReparsePoints(
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

    private static string GetTransactionDirectory(
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

    private static byte[] ReadMessage(
        string transactionDirectory,
        string fileName) => File.ReadAllBytes(Path.Combine(transactionDirectory, fileName));

    private static void WriteMessage(string path, byte[] bytes)
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

    private static void RequireProducer(
        IntegrationApplicationIdentity producer,
        string expectedApplicationId)
    {
        if (!string.Equals(
                producer.ApplicationId,
                expectedApplicationId,
                StringComparison.Ordinal))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidIdentity,
                $"Expected producer '{expectedApplicationId}'.");
        }
    }

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
