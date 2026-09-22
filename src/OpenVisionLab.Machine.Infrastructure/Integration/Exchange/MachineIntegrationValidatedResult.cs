using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// One immutable, fully validated view of a Result transaction. The document
/// hash is calculated from the exact Result bytes that produced Result.
/// </summary>
public sealed record MachineIntegrationValidatedResult(
    IntegrationHandoffV2 Handoff,
    IntegrationAcknowledgementV2 Acknowledgement,
    IntegrationResultV2 Result,
    string ResultDocumentSha256);
