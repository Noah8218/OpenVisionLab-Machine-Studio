namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Stable sidecar contract for the first cross-modal projection slice. The
/// profile describes software coordinates only: image pixels and C3D
/// grid-index coordinates both use a top-left origin.
/// </summary>
public static class MachineCoordinateProjectionContract
{
    public const string SchemaVersion = "1.0";
    public const string ProfileArtifactRole = "coordinate-projection-profile";
    public const string ProfileArtifactId = "coordinate-projection-profile";
    public const string ResultEvidenceRole = "coordinate-projection-result";
    public const string ResultEvidenceArtifactId = "coordinate-projection-result";
    public const string MappingKind = "normalized-linear";
    public const string ImageUnit = "px";
    public const string ImageOrigin = "top-left";
    public const string GridUnit = "raw-height";
    public const string GridFrameId = "frame.c3d-grid-index";
    public const string GridOrigin = "top-left";

    public static MachineCoordinateProjectionProfile CreateDefault(
        string projectionId,
        int imageWidth,
        int imageHeight,
        string? twoDTransactionId = null) =>
        new(
            SchemaVersion,
            MachineCoordinateProjectionValidator.RequireText(projectionId, nameof(projectionId)),
            twoDTransactionId,
            new MachineCoordinateProjectionImage(
                imageWidth,
                imageHeight,
                ImageUnit,
                ImageOrigin),
            new MachineCoordinateProjectionGrid(
                GridUnit,
                GridFrameId,
                GridOrigin),
            new MachineCoordinateProjectionMapping(
                MappingKind,
                1.0,
                1.0,
                0.0,
                0.0));

    public static string CreateProjectionId(
        string projectId,
        string cameraId,
        string acquisitionId)
    {
        var identity = string.Join(
            "\u001F",
            MachineCoordinateProjectionValidator.RequireText(projectId, nameof(projectId)),
            MachineCoordinateProjectionValidator.RequireText(cameraId, nameof(cameraId)),
            MachineCoordinateProjectionValidator.RequireText(acquisitionId, nameof(acquisitionId)));
        var bytes = System.Text.Encoding.UTF8.GetBytes(identity);
        return $"projection-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    public static void Validate(MachineCoordinateProjectionProfile profile) =>
        MachineCoordinateProjectionValidator.ValidateProfile(profile);

    public static string SerializeProfile(MachineCoordinateProjectionProfile profile) =>
        MachineCoordinateProjectionPersistence.SerializeProfile(profile);

    public static MachineCoordinateProjectionProfile ReadProfile(string path) =>
        MachineCoordinateProjectionPersistence.ReadProfile(path);

    public static string SerializeResult(MachineCoordinateProjectionResult result) =>
        MachineCoordinateProjectionPersistence.SerializeResult(result);

    public static MachineCoordinateProjectionResult ReadResult(string path) =>
        MachineCoordinateProjectionPersistence.ReadResult(path);

    public static (double X, double Y) MapImageToGrid(
        MachineCoordinateProjectionProfile profile,
        double imageX,
        double imageY,
        int gridWidth,
        int gridHeight)
        => MachineCoordinateProjectionMapper.MapImageToGrid(
            profile,
            imageX,
            imageY,
            gridWidth,
            gridHeight);

    public static (double X, double Y) MapGridToImage(
        MachineCoordinateProjectionProfile profile,
        double gridX,
        double gridY,
        int gridWidth,
        int gridHeight)
        => MachineCoordinateProjectionMapper.MapGridToImage(
            profile,
            gridX,
            gridY,
            gridWidth,
            gridHeight);
}

public sealed record MachineCoordinateProjectionProfile(
    string SchemaVersion,
    string ProjectionId,
    string? TwoDTransactionId,
    MachineCoordinateProjectionImage Image,
    MachineCoordinateProjectionGrid Grid,
    MachineCoordinateProjectionMapping Mapping);

public sealed record MachineCoordinateProjectionImage(
    int Width,
    int Height,
    string Unit,
    string Origin);

public sealed record MachineCoordinateProjectionGrid(
    string Unit,
    string FrameId,
    string Origin);

public sealed record MachineCoordinateProjectionMapping(
    string Kind,
    double ScaleX,
    double ScaleY,
    double OffsetX,
    double OffsetY);

public sealed record MachineCoordinateProjectionResult(
    string SchemaVersion,
    string ProjectionId,
    string TwoDTransactionId,
    string ThreeDTransactionId,
    string Outcome,
    string TwoDRunId,
    string ThreeDRunId,
    int ImageWidth,
    int ImageHeight,
    int GridWidth,
    int GridHeight,
    IReadOnlyList<MachineProjectedCoordinate> TwoDToThreeD,
    IReadOnlyList<MachineProjectedCoordinate> ThreeDToTwoD,
    DateTimeOffset RecordedAtUtc);

public sealed record MachineProjectedCoordinate(
    string Direction,
    string Id,
    string Kind,
    string Label,
    double ImageX,
    double ImageY,
    double GridX,
    double GridY,
    double? SampledHeight,
    string SampleStatus,
    string InspectionStatus);
