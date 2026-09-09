using System.Windows;

namespace OpenVisionLab.MachineStudio.View.Scene;

/// <summary>
/// Immutable coordinate transform shared by scene rendering and pointer paths.
/// It has no knowledge of the WPF control, resources, or gesture state.
/// </summary>
internal readonly record struct SceneViewportGeometry(
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// Converts authored scene coordinates to viewport pixels and back.
/// Mutable view state remains owned by <see cref="MachineSceneViewport"/>.
/// </summary>
internal readonly record struct SceneViewportProjection(
    double MinimumX,
    double MinimumY,
    double Scale,
    double OffsetX,
    double OffsetY)
{
    private const double Padding = 48;

    internal static SceneViewportProjection Create(
        IReadOnlyList<SceneViewportGeometry> items,
        double viewportWidth,
        double viewportHeight)
    {
        var minimumX = items.Min(item => item.X - (item.Width / 2));
        var maximumX = items.Max(item => item.X + (item.Width / 2));
        var minimumY = items.Min(item => item.Y - (item.Height / 2));
        var maximumY = items.Max(item => item.Y + (item.Height / 2));
        var worldWidth = Math.Max(1, maximumX - minimumX);
        var worldHeight = Math.Max(1, maximumY - minimumY);
        var availableWidth = Math.Max(1, viewportWidth - (Padding * 2));
        var availableHeight = Math.Max(1, viewportHeight - (Padding * 2));
        var scale = Math.Min(availableWidth / worldWidth, availableHeight / worldHeight);
        var offsetX = Padding + ((availableWidth - (worldWidth * scale)) / 2);
        var offsetY = Padding + ((availableHeight - (worldHeight * scale)) / 2);
        return new SceneViewportProjection(minimumX, minimumY, scale, offsetX, offsetY);
    }

    internal static SceneViewportProjection CreateEmpty(double viewportWidth, double viewportHeight) => new(
        0,
        0,
        1,
        viewportWidth / 2,
        viewportHeight / 2);

    internal Point ToScreen(double x, double y) =>
        new(OffsetX + ((x - MinimumX) * Scale), OffsetY + ((y - MinimumY) * Scale));

    internal Point ToWorld(Point point) => new(
        MinimumX + ((point.X - OffsetX) / Scale),
        MinimumY + ((point.Y - OffsetY) / Scale));

    internal SceneViewportProjection ZoomAt(Point anchor, double factor) => new(
        MinimumX,
        MinimumY,
        Scale * factor,
        anchor.X - ((anchor.X - OffsetX) * factor),
        anchor.Y - ((anchor.Y - OffsetY) * factor));

    internal SceneViewportProjection Translate(Vector delta) => new(
        MinimumX,
        MinimumY,
        Scale,
        OffsetX + delta.X,
        OffsetY + delta.Y);
}
