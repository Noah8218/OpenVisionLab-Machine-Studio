namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Persisted content identity for an integration artifact. The caller must
/// compare it with the current bytes before a request is composed.
/// </summary>
public sealed record MachineIntegrationArtifactEvidence(
    string ContentSha256,
    long ContentLength);

/// <summary>
/// Immutable metadata for one Machine-owned deterministic HeightMap source.
/// It is deliberately separate from the virtual-camera image definition even
/// though the simulation uses the same acquisition slot for sequence timing.
/// </summary>
public sealed record MachineIntegrationHeightMapSourceDefinition(
    string SourceRelativePath,
    int Width,
    int Height,
    string PixelFormat,
    string Unit,
    string FrameId,
    MachineIntegrationArtifactEvidence Evidence);
