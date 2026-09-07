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
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(root);
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(transactionsRoot);
        var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(root, handoff.TransactionId);
        if (Directory.Exists(transactionDirectory)
            || File.Exists(transactionDirectory))
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidState,
                "The transaction identity has already been published.");
        }

        var handoffBytes = IntegrationContractJson.SerializeCanonical(handoff);
        var declaredBytes = MachineIntegrationTransactionFileSystem.GetDeclaredArtifactBytes(artifacts);
        var requiredFreeSpace = GetRequiredFreeSpace(
            declaredBytes,
            handoffBytes.LongLength,
            minimumFreeSpaceBytes);
        EnsureFreeSpace(root, requiredFreeSpace);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(transactionsRoot);
        MachineIntegrationTransactionFileSystem.EnsureDirectoryIsNotReparsePoint(transactionsRoot);

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
            MachineIntegrationTransactionFileSystem.WriteMessage(
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
                MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(stagingDirectory, artifact.RelativePath);
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
            MachineIntegrationTransactionMaintenance.TryQuarantineStagingDirectory(
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
        string exchangeRoot) =>
        MachineIntegrationTransactionMaintenance.DiscoverTransactions(exchangeRoot);

    public static IntegrationHandoffV2 ReadHandoff(
        string exchangeRoot,
        Guid transactionId)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var handoff = ReadHandoffEnvelope(root, transactionId);
        var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(root, transactionId);
        foreach (var artifact in handoff.Context.Artifacts)
        {
            MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(transactionDirectory, artifact.RelativePath);
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
        var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(
            Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot))),
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

    public static IntegrationAcknowledgementV2 ReadAcknowledgement(
        string exchangeRoot,
        Guid transactionId)
    {
        var root = Path.GetFullPath(RequireText(exchangeRoot, nameof(exchangeRoot)));
        var handoff = ReadHandoff(root, transactionId);
        var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(root, transactionId);
        var acknowledgement = IntegrationContractJson.DeserializeAcknowledgementV2(
            MachineIntegrationTransactionFileSystem.ReadMessage(
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
        var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(root, transactionId);
        var acknowledgement = IntegrationContractJson.DeserializeAcknowledgementV2(
            MachineIntegrationTransactionFileSystem.ReadMessage(
                transactionDirectory,
                IntegrationTransactionLayout.AcknowledgementFileName));
        var result = IntegrationContractJson.DeserializeResultV2(
            MachineIntegrationTransactionFileSystem.ReadMessage(
                transactionDirectory,
                IntegrationTransactionLayout.ResultFileName));
        ThrowIfInvalid(IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            acknowledgement,
            result));

        if (result.RunRecord is not null)
        {
            MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(
                transactionDirectory,
                result.RunRecord.RelativePath);
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                result.RunRecord,
                transactionDirectory));
        }
        foreach (var evidence in result.Evidence)
        {
            MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(transactionDirectory, evidence.RelativePath);
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                evidence,
                transactionDirectory));
        }

        return result;
    }

    public static IReadOnlyList<MachineIntegrationTransactionDiagnostic> DiagnoseTransactions(
        string exchangeRoot) =>
        MachineIntegrationTransactionMaintenance.DiagnoseTransactions(exchangeRoot);

    public static MachineIntegrationCleanupReport CleanupStaging(
        string exchangeRoot,
        TimeSpan staleAfter,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default) =>
        MachineIntegrationTransactionMaintenance.CleanupStaging(
            exchangeRoot,
            staleAfter,
            nowUtc,
            cancellationToken);

    public static int PurgeQuarantine(
        string exchangeRoot,
        TimeSpan retention,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default) =>
        MachineIntegrationTransactionMaintenance.PurgeQuarantine(
            exchangeRoot,
            retention,
            nowUtc,
            cancellationToken);

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

        var target = MachineIntegrationTransactionFileSystem.GetArtifactPath(transactionDirectory, artifact.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MachineIntegrationTransactionFileSystem.EnsureNoReparsePoints(transactionDirectory, artifact.RelativePath);

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
        var availableBytes = MachineIntegrationTransactionFileSystem.TryGetAvailableFreeBytes(path);
        if (availableBytes.HasValue && availableBytes.Value < requiredBytes)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidState,
                $"Insufficient free space for the transaction: required {requiredBytes} bytes, available {availableBytes.Value} bytes.");
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

}
