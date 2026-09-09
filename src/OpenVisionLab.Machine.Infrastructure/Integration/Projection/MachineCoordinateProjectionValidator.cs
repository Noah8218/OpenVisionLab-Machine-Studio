namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Validates the persisted coordinate projection contracts without performing
/// mapping or file I/O.
/// </summary>
internal static class MachineCoordinateProjectionValidator
{
    internal static void ValidateProfile(MachineCoordinateProjectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.SchemaVersion, MachineCoordinateProjectionContract.SchemaVersion, StringComparison.Ordinal))
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
            || !string.Equals(profile.Image.Unit, MachineCoordinateProjectionContract.ImageUnit, StringComparison.Ordinal)
            || !string.Equals(profile.Image.Origin, MachineCoordinateProjectionContract.ImageOrigin, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Coordinate projection image dimensions, unit, and origin are invalid.");
        }

        ArgumentNullException.ThrowIfNull(profile.Grid);
        if (!string.Equals(profile.Grid.Unit, MachineCoordinateProjectionContract.GridUnit, StringComparison.Ordinal)
            || !string.Equals(profile.Grid.FrameId, MachineCoordinateProjectionContract.GridFrameId, StringComparison.Ordinal)
            || !string.Equals(profile.Grid.Origin, MachineCoordinateProjectionContract.GridOrigin, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Coordinate projection grid unit, frame, and origin are invalid.");
        }

        ArgumentNullException.ThrowIfNull(profile.Mapping);
        if (!string.Equals(profile.Mapping.Kind, MachineCoordinateProjectionContract.MappingKind, StringComparison.Ordinal)
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

    internal static void ValidateResult(MachineCoordinateProjectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.SchemaVersion, MachineCoordinateProjectionContract.SchemaVersion, StringComparison.Ordinal)
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

    internal static void ValidateGridDimensions(int gridWidth, int gridHeight)
    {
        if (gridWidth <= 1 || gridHeight <= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gridWidth),
                "Projection requires a grid wider and taller than one cell.");
        }
    }

    internal static void ValidateCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Coordinate must be finite.");
        }
    }

    internal static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static void ValidatePoints(IEnumerable<MachineProjectedCoordinate> points)
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
}
