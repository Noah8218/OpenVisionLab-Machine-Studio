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
/// Projects spatial equipment geometry through an orthographic orbit camera.
/// All persisted dimensions remain layout units; camera gestures change only the view.
/// </summary>
internal readonly record struct SceneViewportProjection(
    double MinimumX,
    double MinimumY,
    double Scale,
    double OffsetX,
    double OffsetY,
    bool IsObliqueView = false,
    double YawDegrees = SceneViewportProjection.DefaultYawDegrees,
    double PitchDegrees = SceneViewportProjection.DefaultPitchDegrees,
    bool IsEquipmentPresentation = false)
{
    internal const double DefaultYawDegrees = 45;
    internal const double DefaultPitchDegrees = 28.65222299882812;
    private const double Padding = 48;
    private const double ObliqueAxisX = 0.78;
    private const double ObliqueAxisY = 0.34;
    private const double ObliqueElevationScale = 0.88;
    private double Yaw => YawDegrees * Math.PI / 180;
    private double Pitch => PitchDegrees * Math.PI / 180;
    private double GroundScale => Math.Sqrt(2) * ObliqueAxisY * Math.Sin(Pitch) / Math.Sin(DefaultPitchDegrees * Math.PI / 180);
    private double ElevationScale => ObliqueElevationScale * Math.Cos(Pitch) / Math.Cos(DefaultPitchDegrees * Math.PI / 180);
    internal bool CanMapToWorld => !IsObliqueView || Math.Abs(GroundScale) > 0.000001;

    internal static SceneViewportProjection Create(
        IReadOnlyList<SceneViewportGeometry> items,
        double viewportWidth,
        double viewportHeight,
        bool isObliqueView = false,
        double yawDegrees = DefaultYawDegrees,
        double pitchDegrees = DefaultPitchDegrees)
    {
        if (!double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees) || pitchDegrees is < -80 or > 85)
            throw new ArgumentOutOfRangeException(nameof(pitchDegrees), "Orbit angles must be finite and pitch must be between -80 and 85 degrees.");
        if (items.Count == 0) return CreateEmpty(viewportWidth, viewportHeight, isObliqueView, yawDegrees, pitchDegrees);
        if (!isObliqueView)
        {
            var minimumX = items.Min(item => item.X - (item.Width / 2));
            var maximumX = items.Max(item => item.X + (item.Width / 2));
            var minimumY = items.Min(item => item.Y - (item.Height / 2));
            var maximumY = items.Max(item => item.Y + (item.Height / 2));
            return Fit(minimumX, minimumY, maximumX, maximumY, viewportWidth, viewportHeight);
        }

        var orientation = CreateEmpty(viewportWidth, viewportHeight, true, yawDegrees, pitchDegrees);
        var projected = items.SelectMany(item => item.GetFootprintCorners().SelectMany(point => new[]
        {
            orientation.ProjectOblique(point.X, point.Y, item.BaseElevation),
            orientation.ProjectOblique(point.X, point.Y, item.BaseElevation + item.VerticalHeight)
        })).ToArray();
        return Fit(
            projected.Min(point => point.X),
            projected.Min(point => point.Y),
            projected.Max(point => point.X),
            projected.Max(point => point.Y),
            viewportWidth,
            viewportHeight,
            isObliqueView: true) with { YawDegrees = yawDegrees, PitchDegrees = pitchDegrees };
    }

    internal static SceneViewportProjection CreateEquipment(IReadOnlyList<SceneViewportGeometry> items, double viewportWidth, double viewportHeight, bool isObliqueView)
    {
        var orientation = CreateEmpty(viewportWidth, viewportHeight, isObliqueView) with { IsEquipmentPresentation = true };
        var bounds = items.SelectMany(item => new SceneViewportGeometry(item.X, item.Y, item.Width + 100, item.Height + 100,
            item.RotationDegrees, item.BaseElevation, item.VerticalHeight).GetFootprintCorners().SelectMany(point => new[]
            {
                isObliqueView ? orientation.ProjectOblique(point.X, point.Y, item.BaseElevation) : point,
                isObliqueView ? orientation.ProjectOblique(point.X, point.Y, item.BaseElevation + item.VerticalHeight) : point
            })).ToArray();
        var minX = bounds.Length == 0 ? 0 : bounds.Min(point => point.X);
        var maxX = bounds.Length == 0 ? 800 : bounds.Max(point => point.X);
        var minY = bounds.Length == 0 ? -200 : bounds.Min(point => point.Y);
        var maxY = bounds.Length == 0 ? 200 : bounds.Max(point => point.Y);
        var viewBoxScale = Math.Min(viewportWidth / 1000, viewportHeight / 600);
        var scale = Math.Min(900 / Math.Max(1, maxX - minX), 500 / Math.Max(1, maxY - minY)) * viewBoxScale;
        return orientation with { MinimumX = minX, MinimumY = minY, Scale = scale,
            OffsetX = viewportWidth / 2 - (maxX - minX) * scale / 2,
            OffsetY = viewportHeight / 2 - (maxY - minY) * scale / 2 };
    }

    internal static SceneViewportProjection CreateEmpty(
        double viewportWidth,
        double viewportHeight,
        bool isObliqueView = false,
        double yawDegrees = DefaultYawDegrees,
        double pitchDegrees = DefaultPitchDegrees) => new(
        0,
        0,
        1,
        viewportWidth / 2,
        viewportHeight / 2,
        isObliqueView, yawDegrees, pitchDegrees);

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

        if (IsEquipmentPresentation)
        {
            var depth = (projectedY + .85 * referenceElevation) / .38;
            return new Point(projectedX + .45 * depth, depth);
        }
        if (!CanMapToWorld) throw new InvalidOperationException("The camera is parallel to the editing plane.");
        var right = projectedX / (Math.Sqrt(2) * ObliqueAxisX);
        var forward = (projectedY + ElevationScale * referenceElevation) / GroundScale;
        return new Point(Math.Cos(Yaw) * right + Math.Sin(Yaw) * forward, -Math.Sin(Yaw) * right + Math.Cos(Yaw) * forward);
    }

    internal Vector ToWorldDelta(Vector screenDelta)
    {
        var projectedX = screenDelta.X / Scale;
        var projectedY = screenDelta.Y / Scale;
        if (!IsObliqueView)
        {
            return new Vector(projectedX, projectedY);
        }

        if (IsEquipmentPresentation)
        {
            var depth = projectedY / .38;
            return new Vector(projectedX + .45 * depth, depth);
        }
        if (!CanMapToWorld) throw new InvalidOperationException("The camera is parallel to the editing plane.");
        var right = projectedX / (Math.Sqrt(2) * ObliqueAxisX);
        var forward = projectedY / GroundScale;
        return new Vector(Math.Cos(Yaw) * right + Math.Sin(Yaw) * forward, -Math.Sin(Yaw) * right + Math.Cos(Yaw) * forward);
    }

    internal SceneViewportProjection ZoomAt(Point anchor, double factor) => this with
    {
        Scale = Scale * factor,
        OffsetX = anchor.X - ((anchor.X - OffsetX) * factor),
        OffsetY = anchor.Y - ((anchor.Y - OffsetY) * factor)
    };

    internal SceneViewportProjection Translate(Vector delta) => this with { OffsetX = OffsetX + delta.X, OffsetY = OffsetY + delta.Y };

    internal SceneViewportProjection OrbitAt(Point pivot, double elevation, Vector delta)
    {
        if (IsEquipmentPresentation || !IsObliqueView || !double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return this;
        var screenPivot = ToScreen(pivot.X, pivot.Y, elevation);
        var rotated = this with
        {
            YawDegrees = ((YawDegrees + delta.X * 0.35) % 360 + 360) % 360,
            PitchDegrees = Math.Clamp(PitchDegrees - delta.Y * 0.35, -80, 85)
        };
        return rotated.Translate(screenPivot - rotated.ToScreen(pivot.X, pivot.Y, elevation));
    }

    internal double Depth(double x, double y, double elevation) =>
        (Math.Sin(Yaw) * x + Math.Cos(Yaw) * y) * Math.Cos(Pitch) + elevation * Math.Sin(Pitch);

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

    private Point ProjectOblique(double x, double y, double elevation) => IsEquipmentPresentation
        ? new Point(x - .45 * y, .38 * y - .85 * elevation) : new(
        Math.Sqrt(2) * ObliqueAxisX * (Math.Cos(Yaw) * x - Math.Sin(Yaw) * y),
        GroundScale * (Math.Sin(Yaw) * x + Math.Cos(Yaw) * y) - ElevationScale * elevation);
}
