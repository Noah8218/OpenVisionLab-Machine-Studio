using System.Text.Json;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static MachineCoordinateProjectionProfile CreateDefault(
        string projectionId,
        int imageWidth,
        int imageHeight,
        string? twoDTransactionId = null) =>
        new(
            SchemaVersion,
            RequireText(projectionId, nameof(projectionId)),
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
            RequireText(projectId, nameof(projectId)),
            RequireText(cameraId, nameof(cameraId)),
            RequireText(acquisitionId, nameof(acquisitionId)));
        var bytes = System.Text.Encoding.UTF8.GetBytes(identity);
        return $"projection-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    public static void Validate(MachineCoordinateProjectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported coordinate projection schema: '{profile.SchemaVersion}'.");
        }

        RequireText(profile.ProjectionId, nameof(profile.ProjectionId));
        if (!string.IsNullOrWhiteSpace(profile.TwoDTransactionId)
            && (!Guid.TryParse(profile.TwoDTransactionId, out var twoDTransactionId)
                || twoDTransactionId == Guid.Empty))
        {
            throw new InvalidDataException(
                "A coordinate projection TwoD transaction identity must be a non-empty GUID when supplied.");
        }

        ArgumentNullException.ThrowIfNull(profile.Image);
        if (profile.Image.Width <= 1 || profile.Image.Height <= 1
            || !string.Equals(profile.Image.Unit, ImageUnit, StringComparison.Ordinal)
            || !string.Equals(profile.Image.Origin, ImageOrigin, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Coordinate projection image dimensions, unit, and origin are invalid.");
        }

        ArgumentNullException.ThrowIfNull(profile.Grid);
        if (!string.Equals(profile.Grid.Unit, GridUnit, StringComparison.Ordinal)
            || !string.Equals(profile.Grid.FrameId, GridFrameId, StringComparison.Ordinal)
            || !string.Equals(profile.Grid.Origin, GridOrigin, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Coordinate projection grid unit, frame, and origin are invalid.");
        }

        ArgumentNullException.ThrowIfNull(profile.Mapping);
        if (!string.Equals(profile.Mapping.Kind, MappingKind, StringComparison.Ordinal)
            || !double.IsFinite(profile.Mapping.ScaleX)
            || !double.IsFinite(profile.Mapping.ScaleY)
            || profile.Mapping.ScaleX == 0.0
            || profile.Mapping.ScaleY == 0.0
            || !double.IsFinite(profile.Mapping.OffsetX)
            || !double.IsFinite(profile.Mapping.OffsetY))
        {
            throw new InvalidDataException(
                "Coordinate projection mapping must be normalized-linear with finite non-zero scale.");
        }
    }

    public static string SerializeProfile(MachineCoordinateProjectionProfile profile)
    {
        Validate(profile);
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    public static MachineCoordinateProjectionProfile ReadProfile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var profile = JsonSerializer.Deserialize<MachineCoordinateProjectionProfile>(
                           File.ReadAllText(Path.GetFullPath(path)),
                           JsonOptions)
                       ?? throw new InvalidDataException("Coordinate projection profile is empty.");
        Validate(profile);
        return profile;
    }

    public static string SerializeResult(MachineCoordinateProjectionResult result)
    {
        ValidateResult(result);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public static MachineCoordinateProjectionResult ReadResult(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = JsonSerializer.Deserialize<MachineCoordinateProjectionResult>(
                         File.ReadAllText(Path.GetFullPath(path)),
                         JsonOptions)
                     ?? throw new InvalidDataException("Coordinate projection result is empty.");
        ValidateResult(result);
        return result;
    }

    public static (double X, double Y) MapImageToGrid(
        MachineCoordinateProjectionProfile profile,
        double imageX,
        double imageY,
        int gridWidth,
        int gridHeight)
    {
        Validate(profile);
        ValidateCoordinate(imageX, nameof(imageX));
        ValidateCoordinate(imageY, nameof(imageY));
        ValidateGridDimensions(gridWidth, gridHeight);
        return (
            profile.Mapping.OffsetX
                + imageX / (profile.Image.Width - 1)
                * (gridWidth - 1)
                * profile.Mapping.ScaleX,
            profile.Mapping.OffsetY
                + imageY / (profile.Image.Height - 1)
                * (gridHeight - 1)
                * profile.Mapping.ScaleY);
    }

    public static (double X, double Y) MapGridToImage(
        MachineCoordinateProjectionProfile profile,
        double gridX,
        double gridY,
        int gridWidth,
        int gridHeight)
    {
        Validate(profile);
        ValidateCoordinate(gridX, nameof(gridX));
        ValidateCoordinate(gridY, nameof(gridY));
        ValidateGridDimensions(gridWidth, gridHeight);
        return (
            (gridX - profile.Mapping.OffsetX)
                / ((gridWidth - 1) * profile.Mapping.ScaleX)
                * (profile.Image.Width - 1),
            (gridY - profile.Mapping.OffsetY)
                / ((gridHeight - 1) * profile.Mapping.ScaleY)
                * (profile.Image.Height - 1));
    }

    private static void ValidateResult(MachineCoordinateProjectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.SchemaVersion, SchemaVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(result.ProjectionId)
            || !Guid.TryParse(result.TwoDTransactionId, out var twoDTransactionId)
            || twoDTransactionId == Guid.Empty
            || !Guid.TryParse(result.ThreeDTransactionId, out var threeDTransactionId)
            || threeDTransactionId == Guid.Empty
            || result.ImageWidth <= 1
            || result.ImageHeight <= 1
            || result.GridWidth <= 1
            || result.GridHeight <= 1
            || result.TwoDToThreeD is null
            || result.ThreeDToTwoD is null)
        {
            throw new InvalidDataException("Coordinate projection result identity or dimensions are invalid.");
        }

        ValidatePoints(result.TwoDToThreeD);
        ValidatePoints(result.ThreeDToTwoD);
    }

    private static void ValidatePoints(
        IEnumerable<MachineProjectedCoordinate> points)
    {
        foreach (var point in points)
        {
            if (point is null
                || string.IsNullOrWhiteSpace(point.Direction)
                || string.IsNullOrWhiteSpace(point.Id)
                || !double.IsFinite(point.ImageX)
                || !double.IsFinite(point.ImageY)
                || !double.IsFinite(point.GridX)
                || !double.IsFinite(point.GridY)
                || point.SampledHeight is { } height && !double.IsFinite(height))
            {
                throw new InvalidDataException("Coordinate projection contains an invalid point.");
            }
        }
    }

    private static void ValidateGridDimensions(int gridWidth, int gridHeight)
    {
        if (gridWidth <= 1 || gridHeight <= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gridWidth),
                "Projection requires a grid wider and taller than one cell.");
        }
    }

    private static void ValidateCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Coordinate must be finite.");
        }
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
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
