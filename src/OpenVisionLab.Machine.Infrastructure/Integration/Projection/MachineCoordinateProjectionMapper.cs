namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Applies the normalized-linear image/grid mapping policy to validated
/// coordinate projection profiles.
/// </summary>
internal static class MachineCoordinateProjectionMapper
{
    internal static (double X, double Y) MapImageToGrid(
        MachineCoordinateProjectionProfile profile,
        double imageX,
        double imageY,
        int gridWidth,
        int gridHeight)
    {
        MachineCoordinateProjectionValidator.ValidateProfile(profile);
        MachineCoordinateProjectionValidator.ValidateCoordinate(imageX, nameof(imageX));
        MachineCoordinateProjectionValidator.ValidateCoordinate(imageY, nameof(imageY));
        MachineCoordinateProjectionValidator.ValidateGridDimensions(gridWidth, gridHeight);
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

    internal static (double X, double Y) MapGridToImage(
        MachineCoordinateProjectionProfile profile,
        double gridX,
        double gridY,
        int gridWidth,
        int gridHeight)
    {
        MachineCoordinateProjectionValidator.ValidateProfile(profile);
        MachineCoordinateProjectionValidator.ValidateCoordinate(gridX, nameof(gridX));
        MachineCoordinateProjectionValidator.ValidateCoordinate(gridY, nameof(gridY));
        MachineCoordinateProjectionValidator.ValidateGridDimensions(gridWidth, gridHeight);
        return (
            (gridX - profile.Mapping.OffsetX)
                / ((gridWidth - 1) * profile.Mapping.ScaleX)
                * (profile.Image.Width - 1),
            (gridY - profile.Mapping.OffsetY)
                / ((gridHeight - 1) * profile.Mapping.ScaleY)
                * (profile.Image.Height - 1));
    }
}
