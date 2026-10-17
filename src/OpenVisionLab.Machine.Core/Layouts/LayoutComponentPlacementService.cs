using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Core.Layouts;

/// <summary>
/// Owns world-coordinate placement policy for authored layout components.
/// Component creation and project validation remain outside this owner.
/// </summary>
public sealed class LayoutComponentPlacementService
{
    public static Coordinate3D CreateUnitBaseOrigin(int ordinal) => new(ordinal * 225, ordinal * -225, 0);

    public bool ApplyUnitDefault(LayoutComponentDefinition component, Coordinate3D origin)
    {
        (double X, double Y)? local = component.Kind switch
        {
            LayoutComponentKind.LinearStage => (-50d, 50d),
            LayoutComponentKind.PneumaticCylinder => (34d, 81d),
            LayoutComponentKind.DigitalSensor => (-102.5, -43d),
            LayoutComponentKind.Camera => (56d, -31.5),
            LayoutComponentKind.Conveyor => (2.5, 2.5),
            LayoutComponentKind.FeedPosition => (-122.5, 2.5),
            LayoutComponentKind.ExitPosition => (127.5, 2.5),
            LayoutComponentKind.DigitalIo => (207.5, 12d),
            _ => null
        };
        if (local is { } position)
        {
            component.Transform.X = origin.X + position.X;
            component.Transform.Y = origin.Y + position.Y;
            return true;
        }
        return false;
    }

    public void Place(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        double worldX,
        double worldY,
        bool snapToGrid = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(component);

        var x = snapToGrid ? SnapLayoutCoordinate(layout, worldX) : worldX;
        var y = snapToGrid ? SnapLayoutCoordinate(layout, worldY) : worldY;
        component.Transform.X = x;
        component.Transform.Y = y;

        if (component.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            var axis = project.Axes.FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                component.BehaviorBindingId,
                StringComparison.Ordinal));
            if (axis is not null)
            {
                axis.Position = new Coordinate3D(x, y, axis.Position.Z);
            }

            return;
        }

        var device = project.Devices.FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            component.BehaviorBindingId,
            StringComparison.Ordinal));
        if (device is not null)
        {
            device.MountPosition = new Coordinate3D(x, y, device.MountPosition.Z);
        }
    }

    public bool UsesIndependentDefaultPlacement(LayoutComponentKind kind) =>
        kind is not LayoutComponentKind.MachineFrame and not LayoutComponentKind.Workpiece;

    public (double X, double Y) FindNearestAvailablePosition(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(component);

        var defaultX = SnapLayoutCoordinate(layout, component.Transform.X);
        var defaultY = SnapLayoutCoordinate(layout, component.Transform.Y);
        if (!layout.SnapToGrid || !double.IsFinite(layout.GridSize) || layout.GridSize <= 0)
        {
            return (defaultX, defaultY);
        }

        var obstacles = layout.Components
            .Where(existing => existing.Kind != LayoutComponentKind.MachineFrame)
            .ToArray();
        if (obstacles.Length == 0 || !OverlapsAny(component, defaultX, defaultY, obstacles))
        {
            return (defaultX, defaultY);
        }

        var maximumRadius = (int)Math.Ceiling(obstacles.Max(existing =>
            (Math.Abs(existing.Transform.Y - defaultY) +
             GetVerticalHalfExtent(component) +
             GetVerticalHalfExtent(existing)) / layout.GridSize)) + 1;

        for (var radius = 1; radius <= maximumRadius; radius++)
        {
            var offsets = Enumerable.Range(-radius, (radius * 2) + 1)
                .SelectMany(x => Enumerable.Range(-radius, (radius * 2) + 1)
                    .Where(y => Math.Max(Math.Abs(x), Math.Abs(y)) == radius)
                    .Select(y => (X: x, Y: y)))
                .OrderBy(offset => (offset.X * offset.X) + (offset.Y * offset.Y))
                .ThenBy(offset => offset.Y)
                .ThenBy(offset => offset.X);
            foreach (var offset in offsets)
            {
                var x = defaultX + (offset.X * layout.GridSize);
                var y = defaultY + (offset.Y * layout.GridSize);
                if (!OverlapsAny(component, x, y, obstacles))
                {
                    return (x, y);
                }
            }
        }

        return (defaultX, defaultY);
    }

    private static double SnapLayoutCoordinate(MachineLayoutDefinition layout, double value) =>
        layout.SnapToGrid && double.IsFinite(layout.GridSize) && layout.GridSize > 0
            ? Math.Round(value / layout.GridSize, MidpointRounding.AwayFromZero) * layout.GridSize
            : value;

    private static bool OverlapsAny(
        LayoutComponentDefinition component,
        double x,
        double y,
        IReadOnlyList<LayoutComponentDefinition> obstacles) =>
        obstacles.Any(existing =>
            Math.Abs(existing.Transform.X - x) <
                GetHorizontalHalfExtent(component) + GetHorizontalHalfExtent(existing) &&
            Math.Abs(existing.Transform.Y - y) <
                GetVerticalHalfExtent(component) + GetVerticalHalfExtent(existing));

    private static double GetHorizontalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Cos(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Sin(radians)) * component.Size.Height / 2d);
    }

    private static double GetVerticalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Sin(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Cos(radians)) * component.Size.Height / 2d);
    }
}
