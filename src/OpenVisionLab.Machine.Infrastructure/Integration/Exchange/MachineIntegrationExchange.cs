using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public sealed record MachineIntegrationTransactionSummary(
    IntegrationHandoffV2 Handoff,
    bool HasAcknowledgement,
    bool HasResult);

/// <summary>
/// Public compatibility facade for the Machine Studio side of the v2 file exchange.
/// Each operation delegates to the concrete owner for publication, inspection, or
/// retention. The facade contains no transaction policy or file-system workflow.
/// </summary>
public static class MachineIntegrationExchange
{
    public const long DefaultMinimumFreeSpaceBytes = 1_048_576;

    public static IntegrationHandoffV2 PublishHandoff(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IReadOnlyDictionary<string, string> artifactSourcePaths) =>
        MachineIntegrationTransactionPublisher.PublishHandoff(
            exchangeRoot,
            handoff,
            artifactSourcePaths);

    public static Task<IntegrationHandoffV2> PublishHandoffAsync(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IReadOnlyDictionary<string, string> artifactSourcePaths,
        IProgress<MachineIntegrationTransferProgress>? progress = null,
        long minimumFreeSpaceBytes = DefaultMinimumFreeSpaceBytes,
        CancellationToken cancellationToken = default) =>
        MachineIntegrationTransactionPublisher.PublishHandoffAsync(
            exchangeRoot,
            handoff,
            artifactSourcePaths,
            progress,
            minimumFreeSpaceBytes,
            cancellationToken);

    public static IReadOnlyList<MachineIntegrationTransactionSummary> DiscoverTransactions(
        string exchangeRoot) =>
        MachineIntegrationTransactionInspector.DiscoverTransactions(exchangeRoot);

    public static IntegrationHandoffV2 ReadHandoff(
        string exchangeRoot,
        Guid transactionId) =>
        MachineIntegrationTransactionInspector.ReadHandoff(exchangeRoot, transactionId);

    public static IntegrationHandoffV2 ReadHandoffEnvelope(
        string exchangeRoot,
        Guid transactionId) =>
        MachineIntegrationTransactionInspector.ReadHandoffEnvelope(exchangeRoot, transactionId);

    public static IntegrationAcknowledgementV2 ReadAcknowledgement(
        string exchangeRoot,
        Guid transactionId) =>
        MachineIntegrationTransactionInspector.ReadAcknowledgement(exchangeRoot, transactionId);

    public static IntegrationResultV2 ReadResult(
        string exchangeRoot,
        Guid transactionId) =>
        MachineIntegrationTransactionInspector.ReadResult(exchangeRoot, transactionId);

    public static MachineIntegrationValidatedResult ReadValidatedResult(
        string exchangeRoot,
        Guid transactionId) =>
        MachineIntegrationTransactionInspector.ReadValidatedResult(exchangeRoot, transactionId);

    public static IReadOnlyList<MachineIntegrationTransactionDiagnostic> DiagnoseTransactions(
        string exchangeRoot) =>
        MachineIntegrationTransactionInspector.DiagnoseTransactions(exchangeRoot);

    public static MachineIntegrationCleanupReport CleanupStaging(
        string exchangeRoot,
        TimeSpan staleAfter,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default) =>
        MachineIntegrationTransactionRetention.CleanupStaging(
            exchangeRoot,
            staleAfter,
            nowUtc,
            cancellationToken);

    public static int PurgeQuarantine(
        string exchangeRoot,
        TimeSpan retention,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default) =>
        MachineIntegrationTransactionRetention.PurgeQuarantine(
            exchangeRoot,
            retention,
            nowUtc,
            cancellationToken);
}
