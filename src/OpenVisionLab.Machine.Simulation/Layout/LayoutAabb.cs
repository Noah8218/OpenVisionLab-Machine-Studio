namespace OpenVisionLab.Machine.Simulation.Layout;

internal readonly record struct LayoutAabb(
    double MinimumX,
    double MaximumX,
    double MinimumY,
    double MaximumY)
{
    public bool IntersectsInclusive(LayoutAabb other) =>
        MinimumX <= other.MaximumX &&
        MaximumX >= other.MinimumX &&
        MinimumY <= other.MaximumY &&
        MaximumY >= other.MinimumY;
}
