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
    double Height,
    double RotationDegrees = 0,
    double BaseElevation = 0,
    double VerticalHeight = 0)
{
    internal Point[] GetFootprintCorners()
    {
        var halfWidth = Width / 2d;
        var halfHeight = Height / 2d;
        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var centerX = X;
        var centerY = Y;
        return
        [
            Rotate(-halfWidth, -halfHeight),
            Rotate(halfWidth, -halfHeight),
            Rotate(halfWidth, halfHeight),
            Rotate(-halfWidth, halfHeight)
        ];

        Point Rotate(double localX, double localY) => new(
            centerX + (localX * cosine) - (localY * sine),
            centerY + (localX * sine) + (localY * cosine));
    }
}

/// <summary>
/// Converts authored plan coordinates to viewport pixels and back. Oblique
/// projection is schematic; all persisted dimensions remain layout units.
/// </summary>
internal readonly record struct SceneViewportProjection(
    double MinimumX,
    double MinimumY,
    double Scale,
    double OffsetX,
    double OffsetY,
    bool IsObliqueView = false)
{
    private const double Padding = 48;
    private const double ObliqueAxisX = 0.78;
    private const double ObliqueAxisY = 0.34;
    private const double ObliqueElevationScale = 0.88;

    internal static SceneViewportProjection Create(
        IReadOnlyList<SceneViewportGeometry> items,
        double viewportWidth,
        double viewportHeight,
        bool isObliqueView = false)
    {
        if (!isObliqueView)
        {
            var minimumX = items.Min(item => item.X - (item.Width / 2));
            var maximumX = items.Max(item => item.X + (item.Width / 2));
            var minimumY = items.Min(item => item.Y - (item.Height / 2));
            var maximumY = items.Max(item => item.Y + (item.Height / 2));
            return Fit(minimumX, minimumY, maximumX, maximumY, viewportWidth, viewportHeight);
        }

        var projected = items.SelectMany(item => item.GetFootprintCorners().SelectMany(point => new[]
        {
            ProjectOblique(point.X, point.Y, item.BaseElevation),
            ProjectOblique(point.X, point.Y, item.BaseElevation + item.VerticalHeight)
        })).ToArray();
        return Fit(
            projected.Min(point => point.X),
            projected.Min(point => point.Y),
            projected.Max(point => point.X),
            projected.Max(point => point.Y),
            viewportWidth,
            viewportHeight,
            isObliqueView: true);
    }

    internal static SceneViewportProjection CreateEmpty(
        double viewportWidth,
        double viewportHeight,
        bool isObliqueView = false) => new(
        0,
        0,
        1,
        viewportWidth / 2,
        viewportHeight / 2,
        isObliqueView);

    internal Point ToScreen(double x, double y, double elevation = 0)
    {
        var point = IsObliqueView ? ProjectOblique(x, y, elevation) : new Point(x, y);
        return new Point(
            OffsetX + ((point.X - MinimumX) * Scale),
            OffsetY + ((point.Y - MinimumY) * Scale));
    }

    internal Point ToWorld(Point point, double referenceElevation = 0)
    {
        var projectedX = MinimumX + ((point.X - OffsetX) / Scale);
        var projectedY = MinimumY + ((point.Y - OffsetY) / Scale);
        if (!IsObliqueView)
        {
            return new Point(projectedX, projectedY);
        }

        var planeCenter = (projectedY + (ObliqueElevationScale * referenceElevation)) / (2d * ObliqueAxisY);
        var axisDifference = projectedX / ObliqueAxisX;
        return new Point(planeCenter + (axisDifference / 2d), planeCenter - (axisDifference / 2d));
    }

    internal Vector ToWorldDelta(Vector screenDelta)
    {
        var projectedX = screenDelta.X / Scale;
        var projectedY = screenDelta.Y / Scale;
        if (!IsObliqueView)
        {
            return new Vector(projectedX, projectedY);
        }

        var planeCenter = projectedY / (2d * ObliqueAxisY);
        var axisDifference = projectedX / ObliqueAxisX;
        return new Vector(planeCenter + (axisDifference / 2d), planeCenter - (axisDifference / 2d));
    }

    internal SceneViewportProjection ZoomAt(Point anchor, double factor) => new(
        MinimumX,
        MinimumY,
        Scale * factor,
        anchor.X - ((anchor.X - OffsetX) * factor),
        anchor.Y - ((anchor.Y - OffsetY) * factor),
        IsObliqueView);

    internal SceneViewportProjection Translate(Vector delta) => new(
        MinimumX,
        MinimumY,
        Scale,
        OffsetX + delta.X,
        OffsetY + delta.Y,
        IsObliqueView);

    private static SceneViewportProjection Fit(
        double minimumX,
        double minimumY,
        double maximumX,
        double maximumY,
        double viewportWidth,
        double viewportHeight,
        bool isObliqueView = false)
    {
        var worldWidth = Math.Max(1, maximumX - minimumX);
        var worldHeight = Math.Max(1, maximumY - minimumY);
        var availableWidth = Math.Max(1, viewportWidth - (Padding * 2));
        var availableHeight = Math.Max(1, viewportHeight - (Padding * 2));
        var scale = Math.Min(availableWidth / worldWidth, availableHeight / worldHeight);
        var offsetX = Padding + ((availableWidth - (worldWidth * scale)) / 2);
        var offsetY = Padding + ((availableHeight - (worldHeight * scale)) / 2);
        return new SceneViewportProjection(minimumX, minimumY, scale, offsetX, offsetY, isObliqueView);
    }

    private static Point ProjectOblique(double x, double y, double elevation) => new(
        ObliqueAxisX * (x - y),
        (ObliqueAxisY * (x + y)) - (ObliqueElevationScale * elevation));
}
