namespace OpenVisionLab.Machine.Infrastructure.Integration;

public enum MachineIntegrationTransferPhase
{
    Preflight,
    Copying,
    Validating,
    Published,
    Quarantined
}

public sealed record MachineIntegrationTransferProgress(
    Guid TransactionId,
    MachineIntegrationTransferPhase Phase,
    string? ArtifactId,
    long BytesCopied,
    long TotalBytes,
    int CompletedArtifacts,
    int ArtifactCount)
{
    public double FractionCompleted => TotalBytes > 0
        ? Math.Clamp((double)BytesCopied / TotalBytes, 0, 1)
        : ArtifactCount == 0
            ? 1
            : Math.Clamp((double)CompletedArtifacts / ArtifactCount, 0, 1);
}

public enum MachineIntegrationTransactionState
{
    Published,
    Staging,
    Quarantined,
    Invalid
}

public sealed record MachineIntegrationTransactionDiagnostic(
    Guid? TransactionId,
    MachineIntegrationTransactionState State,
    string DirectoryPath,
    DateTimeOffset LastWriteTimeUtc,
    int ArtifactCount,
    long DeclaredBytes,
    long MaterializedBytes,
    long? AvailableFreeBytes,
    string? Detail);

public sealed record MachineIntegrationCleanupReport(
    int ScannedStagingDirectories,
    int QuarantinedStagingDirectories,
    IReadOnlyList<MachineIntegrationTransactionDiagnostic> Diagnostics);
