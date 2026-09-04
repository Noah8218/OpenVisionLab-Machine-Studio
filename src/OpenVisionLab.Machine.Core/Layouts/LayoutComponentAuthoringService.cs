using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Core.Layouts;

public enum LayoutComponentAuthoringFailureKind
{
    InvalidCoordinates,
    ActiveLayoutNotFound,
    ActiveLayoutRequired,
    SensorTargetRequired,
    WorkpieceCarrierRequired,
    UnsupportedComponentKind,
    InvalidDefinition
}

public sealed record LayoutComponentAuthoringFailure(
    LayoutComponentAuthoringFailureKind Kind,
    string? ActiveLayoutId = null,
    MachineProjectLayoutValidationError? ValidationError = null);

public sealed record LayoutComponentAddResult(
    MachineLayoutDefinition? Layout,
    LayoutComponentDefinition? Component,
    LayoutComponentAuthoringFailure? Failure)
{
    public bool IsSuccess => Layout is not null && Component is not null && Failure is null;
}

public enum LayoutComponentRemovalFailureKind
{
    NotFound,
    SensorDependency,
    WorkpieceDependency
}

public sealed record LayoutComponentRemovalResult(
    LayoutComponentDefinition? RemovedComponent,
    LayoutComponentDefinition? BlockingComponent,
    LayoutComponentRemovalFailureKind? Failure)
{
    public bool IsSuccess => RemovedComponent is not null && Failure is null;
}

/// <summary>
/// Owns project-level composition policy for authored layout components.
/// It is stateless and has no dependency on WPF or the Machine Studio shell.
/// </summary>
public sealed class LayoutComponentAuthoringService
{
    public LayoutComponentAddResult TryAdd(
        MachineProjectDocument project,
        LayoutComponentKind kind,
        string? selectedComponentId = null,
        double? worldX = null,
        double? worldY = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (worldX.HasValue != worldY.HasValue ||
            worldX is { } x && !double.IsFinite(x) ||
            worldY is { } y && !double.IsFinite(y))
        {
            return new(null, null, new(LayoutComponentAuthoringFailureKind.InvalidCoordinates));
        }

        var previousActiveLayoutId = project.Simulation.ActiveLayoutId;
        var previousLayoutCount = project.Layouts.Count;
        var previousAxisCount = project.Axes.Count;
        var previousDeviceCount = project.Devices.Count;
        var previousChannelCount = project.Channels.Count;
        var layout = GetOrCreateActiveLayout(project, out var layoutFailure);
        if (layout is null)
        {
            return new(null, null, layoutFailure);
        }

        var previousComponentCount = layout.Components.Count;
        var component = CreateComponent(project, layout, kind, selectedComponentId, out var componentFailure);
        if (component is null)
        {
            RollBackAddition(
                project,
                layout,
                previousActiveLayoutId,
                previousLayoutCount,
                previousComponentCount,
                previousAxisCount,
                previousDeviceCount,
                previousChannelCount);
            return new(
                null,
                null,
                componentFailure ?? new(LayoutComponentAuthoringFailureKind.UnsupportedComponentKind));
        }

        if (worldX is { } dropX && worldY is { } dropY)
        {
            PlaceNewComponent(project, layout, component, dropX, dropY);
        }
        else if (UsesIndependentDefaultPlacement(component.Kind))
        {
            var position = FindNearestAvailableGridPosition(layout, component);
            PlaceNewComponent(project, layout, component, position.X, position.Y);
        }

        layout.Components.Add(component);
        var validation = new MachineProjectLayoutValidator().Validate(project);
        if (!validation.IsValid)
        {
            RollBackAddition(
                project,
                layout,
                previousActiveLayoutId,
                previousLayoutCount,
                previousComponentCount,
                previousAxisCount,
                previousDeviceCount,
                previousChannelCount);
            return new(
                null,
                null,
                new(
                    LayoutComponentAuthoringFailureKind.InvalidDefinition,
                    ValidationError: validation.Errors[0]));
        }

        return new(layout, component, null);
    }

    public LayoutComponentRemovalResult TryRemove(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        string componentId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);

        var component = layout.Components.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, componentId, StringComparison.Ordinal));
        if (component is null)
        {
            return new(null, null, LayoutComponentRemovalFailureKind.NotFound);
        }

        var dependentSensorComponent = project.Layouts
            .SelectMany(definition => definition.Components)
            .Where(candidate => candidate.Kind == LayoutComponentKind.DigitalSensor)
            .Select(candidate => new
            {
                Component = candidate,
                Device = project.Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, candidate.BehaviorBindingId, StringComparison.Ordinal))
            })
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Device?.Sensor?.TargetComponentId,
                    component.Id,
                    StringComparison.Ordinal));
        if (dependentSensorComponent is not null)
        {
            return new(
                null,
                dependentSensorComponent.Component,
                LayoutComponentRemovalFailureKind.SensorDependency);
        }

        var dependentWorkpiece = project.Layouts
            .SelectMany(definition => definition.Components)
            .Where(candidate => candidate.Kind == LayoutComponentKind.Workpiece)
            .Select(candidate => new
            {
                Component = candidate,
                Device = project.Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, candidate.BehaviorBindingId, StringComparison.Ordinal))
            })
            .FirstOrDefault(candidate => string.Equals(
                candidate.Device?.Workpiece?.ConveyorComponentId,
                component.Id,
                StringComparison.Ordinal));
        if (dependentWorkpiece is not null)
        {
            return new(
                null,
                dependentWorkpiece.Component,
                LayoutComponentRemovalFailureKind.WorkpieceDependency);
        }

        return layout.Components.Remove(component)
            ? new(component, null, null)
            : new(null, null, LayoutComponentRemovalFailureKind.NotFound);
    }

    private static LayoutComponentDefinition? CreateComponent(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentKind kind,
        string? selectedComponentId,
        out LayoutComponentAuthoringFailure? failure)
    {
        failure = null;
        return kind switch
        {
            LayoutComponentKind.MachineFrame => CreateMachineFrame(project),
            LayoutComponentKind.LinearStage => CreateAxisStage(project, layout, AxisKind.Linear),
            LayoutComponentKind.RotaryStage => CreateAxisStage(project, layout, AxisKind.Rotary),
            LayoutComponentKind.DigitalSensor => CreateDigitalSensor(
                project,
                layout,
                selectedComponentId,
                out failure),
            LayoutComponentKind.PneumaticCylinder => CreatePneumaticCylinder(project),
            LayoutComponentKind.Conveyor => CreateConveyor(project),
            LayoutComponentKind.Workpiece => CreateWorkpiece(project, layout, out failure),
            _ => null
        } ?? SetUnsupportedFailure(ref failure);
    }

    private static LayoutComponentDefinition? SetUnsupportedFailure(
        ref LayoutComponentAuthoringFailure? failure)
    {
        failure ??= new(LayoutComponentAuthoringFailureKind.UnsupportedComponentKind);
        return null;
    }

    private static void PlaceNewComponent(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        double worldX,
        double worldY)
    {
        var x = SnapLayoutCoordinate(layout, worldX);
        var y = SnapLayoutCoordinate(layout, worldY);
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

    private static double SnapLayoutCoordinate(MachineLayoutDefinition layout, double value) =>
        layout.SnapToGrid && double.IsFinite(layout.GridSize) && layout.GridSize > 0
            ? Math.Round(value / layout.GridSize, MidpointRounding.AwayFromZero) * layout.GridSize
            : value;

    private static bool UsesIndependentDefaultPlacement(LayoutComponentKind kind) =>
        kind is not LayoutComponentKind.MachineFrame and not LayoutComponentKind.Workpiece;

    private static (double X, double Y) FindNearestAvailableGridPosition(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component)
    {
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

    private static void RollBackAddition(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        string? previousActiveLayoutId,
        int previousLayoutCount,
        int previousComponentCount,
        int previousAxisCount,
        int previousDeviceCount,
        int previousChannelCount)
    {
        RemoveAddedItems(layout.Components, previousComponentCount);
        RemoveAddedItems(project.Axes, previousAxisCount);
        RemoveAddedItems(project.Devices, previousDeviceCount);
        RemoveAddedItems(project.Channels, previousChannelCount);
        RemoveAddedItems(project.Layouts, previousLayoutCount);
        project.Simulation.ActiveLayoutId = previousActiveLayoutId;
    }

    private static void RemoveAddedItems<T>(List<T> items, int originalCount)
    {
        if (items.Count > originalCount)
        {
            items.RemoveRange(originalCount, items.Count - originalCount);
        }
    }

    private static MachineLayoutDefinition? GetOrCreateActiveLayout(
        MachineProjectDocument project,
        out LayoutComponentAuthoringFailure? failure)
    {
        failure = null;
        var activeLayoutId = project.Simulation.ActiveLayoutId;
        if (!string.IsNullOrWhiteSpace(activeLayoutId))
        {
            var active = project.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, activeLayoutId, StringComparison.Ordinal));
            if (active is not null)
            {
                return active;
            }

            failure = new(LayoutComponentAuthoringFailureKind.ActiveLayoutNotFound, activeLayoutId);
            return null;
        }

        if (project.Layouts.Count == 1)
        {
            var existing = project.Layouts[0];
            project.Simulation.ActiveLayoutId = existing.Id;
            return existing;
        }

        if (project.Layouts.Count > 1)
        {
            failure = new(LayoutComponentAuthoringFailureKind.ActiveLayoutRequired);
            return null;
        }

        var layout = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            GridSize = 10,
            SnapToGrid = true
        };
        project.Layouts.Add(layout);
        project.Simulation.ActiveLayoutId = layout.Id;
        return layout;
    }

    private static LayoutComponentDefinition CreateMachineFrame(MachineProjectDocument project)
    {
        var index = NextOrdinal("frame", AllLayoutComponentIds(project));
        return new LayoutComponentDefinition
        {
            Id = $"frame-{index}",
            Name = $"Machine Frame {index}",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 150 + ((index - 1) * 20), Y = 200 },
            Size = new Size2D { Width = 520, Height = 300 },
            ZIndex = -100
        };
    }

    private static LayoutComponentDefinition CreateAxisStage(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        AxisKind axisKind)
    {
        var boundAxisIds = layout.Components
            .Where(item => item.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
            .Select(item => item.BehaviorBindingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var axis = project.Axes.FirstOrDefault(item =>
            item.Kind == axisKind && !boundAxisIds.Contains(item.Id));
        if (axis is null)
        {
            var axisIndex = NextOrdinal("axis", project.Axes.Select(item => item.Id));
            var rotary = axisKind == AxisKind.Rotary;
            axis = new VirtualAxisDefinition
            {
                Id = $"axis-{axisIndex}",
                Name = rotary ? $"Rotation Axis {axisIndex}" : $"Transfer Axis {axisIndex}",
                Kind = axisKind,
                Unit = rotary ? "deg" : "mm",
                HomePosition = 0,
                SoftLimitMin = rotary ? -360 : 0,
                SoftLimitMax = rotary ? 360 : 300,
                MaxVelocity = rotary ? 240 : 180,
                MaxAcceleration = rotary ? 900 : 600,
                MaxDeceleration = rotary ? 900 : 600,
                FollowingErrorLimit = VirtualAxisDefinition.DefaultFollowingErrorLimit,
                Position = new Coordinate3D(40, 180 + ((axisIndex - 1) * 90), 0)
            };
            project.Axes.Add(axis);
        }

        var isRotary = axisKind == AxisKind.Rotary;
        var stagePrefix = isRotary ? "rotary-stage" : "stage";
        var stageIndex = NextOrdinal(stagePrefix, AllLayoutComponentIds(project));
        return new LayoutComponentDefinition
        {
            Id = $"{stagePrefix}-{stageIndex}",
            Name = isRotary ? $"Rotary Stage {stageIndex}" : $"Linear Stage {stageIndex}",
            Kind = isRotary ? LayoutComponentKind.RotaryStage : LayoutComponentKind.LinearStage,
            Transform = new Transform2D
            {
                X = 40,
                Y = 180 + ((stageIndex - 1) * 90)
            },
            Size = isRotary
                ? new Size2D { Width = 72, Height = 72 }
                : new Size2D { Width = 84, Height = 48 },
            ZIndex = 20,
            BehaviorBindingId = axis.Id
        };
    }

    private static LayoutComponentDefinition? CreateDigitalSensor(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        string? selectedComponentId,
        out LayoutComponentAuthoringFailure? failure)
    {
        failure = null;
        var target = layout.Components.FirstOrDefault(item =>
                string.Equals(item.Id, selectedComponentId, StringComparison.Ordinal)) is
            { Kind: LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage or LayoutComponentKind.Workpiece } selected
            ? selected
            : layout.Components.FirstOrDefault(item => item.Kind == LayoutComponentKind.Workpiece)
              ?? layout.Components.FirstOrDefault(item => item.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage);
        if (target is null)
        {
            failure = new(LayoutComponentAuthoringFailureKind.SensorTargetRequired);
            return null;
        }

        var sensorIndex = NextSensorOrdinal(project);
        var componentId = $"sensor-{sensorIndex}";
        var channelId = $"di.{componentId}";
        var deviceId = $"device.{componentId}";

        project.Channels.Add(new ChannelDefinition
        {
            Id = channelId,
            Name = $"Stage Sensor {sensorIndex}",
            Kind = ChannelKind.DigitalInput,
            InitialValue = 0
        });
        project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Stage Sensor {sensorIndex}",
            Kind = DeviceKind.Sensor,
            MountPosition = new Coordinate3D(target.Transform.X + 180, target.Transform.Y, 0),
            ChannelIds = { channelId },
            Sensor = new DigitalSensorDefinition
            {
                OutputChannelId = channelId,
                TargetComponentId = target.Id,
                OnDelayMilliseconds = 0,
                OffDelayMilliseconds = 0
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Digital Sensor {sensorIndex}",
            Kind = LayoutComponentKind.DigitalSensor,
            Transform = new Transform2D { X = target.Transform.X + 180, Y = target.Transform.Y },
            Size = new Size2D { Width = 18, Height = 84 },
            ZIndex = 30,
            BehaviorBindingId = deviceId
        };
    }

    private static LayoutComponentDefinition CreateConveyor(MachineProjectDocument project)
    {
        var conveyorIndex = NextConveyorOrdinal(project);
        var componentId = $"conveyor-{conveyorIndex}";
        var deviceId = $"device.{componentId}";
        var runChannelId = $"do.{componentId}.run";
        var reverseChannelId = $"do.{componentId}.reverse";
        var y = 260 + ((conveyorIndex - 1) * 110);

        project.Channels.Add(new ChannelDefinition
        {
            Id = runChannelId,
            Name = $"Conveyor {conveyorIndex} Run",
            Kind = ChannelKind.DigitalOutput,
            InitialValue = 0
        });
        project.Channels.Add(new ChannelDefinition
        {
            Id = reverseChannelId,
            Name = $"Conveyor {conveyorIndex} Reverse",
            Kind = ChannelKind.DigitalOutput,
            InitialValue = 0
        });
        project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Conveyor {conveyorIndex}",
            Kind = DeviceKind.Conveyor,
            MountPosition = new Coordinate3D(220, y, 0),
            ChannelIds = { runChannelId, reverseChannelId },
            Conveyor = new ConveyorDefinition
            {
                RunCommandChannelId = runChannelId,
                ReverseCommandChannelId = reverseChannelId,
                SpeedUnitsPerSecond = 120
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Conveyor {conveyorIndex}",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D { X = 220, Y = y },
            Size = new Size2D { Width = 360, Height = 80 },
            ZIndex = 10,
            BehaviorBindingId = deviceId
        };
    }

    private static LayoutComponentDefinition? CreateWorkpiece(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        out LayoutComponentAuthoringFailure? failure)
    {
        failure = null;
        var conveyor = layout.Components.FirstOrDefault(item => item.Kind == LayoutComponentKind.Conveyor);
        if (conveyor is null)
        {
            failure = new(LayoutComponentAuthoringFailureKind.WorkpieceCarrierRequired);
            return null;
        }

        var workpieceIndex = NextWorkpieceOrdinal(project);
        var componentId = $"workpiece-{workpieceIndex}";
        var deviceId = $"device.{componentId}";
        var radians = conveyor.Transform.RotationDegrees * Math.PI / 180d;
        var initialOffset = -((conveyor.Size.Width - 42) / 2d) + 20;
        var x = conveyor.Transform.X + (initialOffset * Math.Cos(radians));
        var y = conveyor.Transform.Y + (initialOffset * Math.Sin(radians));

        project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Workpiece {workpieceIndex}",
            Kind = DeviceKind.Workpiece,
            MountPosition = new Coordinate3D(x, y, 0),
            Workpiece = new WorkpieceDefinition
            {
                Type = "Generic Part",
                ConveyorComponentId = conveyor.Id,
                InspectionState = WorkpieceInspectionState.Pending
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Workpiece {workpieceIndex}",
            Kind = LayoutComponentKind.Workpiece,
            Transform = new Transform2D
            {
                X = x,
                Y = y,
                RotationDegrees = conveyor.Transform.RotationDegrees
            },
            Size = new Size2D { Width = 42, Height = 42 },
            ZIndex = 20,
            BehaviorBindingId = deviceId
        };
    }

    private static LayoutComponentDefinition CreatePneumaticCylinder(MachineProjectDocument project)
    {
        var cylinderIndex = NextCylinderOrdinal(project);
        var componentId = $"cylinder-{cylinderIndex}";
        var deviceId = $"device.{componentId}";
        var commandChannelId = $"do.{componentId}.extend";
        var extendedChannelId = $"di.{componentId}.extended";
        var retractedChannelId = $"di.{componentId}.retracted";
        var y = 110 + ((cylinderIndex - 1) * 70);

        project.Channels.Add(new ChannelDefinition
        {
            Id = commandChannelId,
            Name = $"Cylinder {cylinderIndex} Extend Command",
            Kind = ChannelKind.DigitalOutput,
            InitialValue = 0
        });
        project.Channels.Add(new ChannelDefinition
        {
            Id = extendedChannelId,
            Name = $"Cylinder {cylinderIndex} Extended",
            Kind = ChannelKind.DigitalInput,
            InitialValue = 0
        });
        project.Channels.Add(new ChannelDefinition
        {
            Id = retractedChannelId,
            Name = $"Cylinder {cylinderIndex} Retracted",
            Kind = ChannelKind.DigitalInput,
            InitialValue = 1
        });
        project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Pneumatic Cylinder {cylinderIndex}",
            Kind = DeviceKind.Cylinder,
            MountPosition = new Coordinate3D(360, y, 0),
            ChannelIds = { commandChannelId, extendedChannelId, retractedChannelId },
            Cylinder = new PneumaticCylinderDefinition
            {
                ExtendCommandChannelId = commandChannelId,
                ExtendedSensorChannelId = extendedChannelId,
                RetractedSensorChannelId = retractedChannelId,
                ExtendDurationMilliseconds = 300,
                RetractDurationMilliseconds = 250,
                ExtendedSensorDelayMilliseconds = 10,
                RetractedSensorDelayMilliseconds = 10,
                Stroke = 80
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Pneumatic Cylinder {cylinderIndex}",
            Kind = LayoutComponentKind.PneumaticCylinder,
            Transform = new Transform2D { X = 360, Y = y },
            Size = new Size2D { Width = 96, Height = 36 },
            ZIndex = 25,
            BehaviorBindingId = deviceId
        };
    }

    private static int NextOrdinal(string prefix, IEnumerable<string> ids)
    {
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (existing.Contains($"{prefix}-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private static IEnumerable<string> AllLayoutComponentIds(MachineProjectDocument project) =>
        project.Layouts.SelectMany(layout => layout.Components).Select(component => component.Id);

    private static int NextSensorOrdinal(MachineProjectDocument project)
    {
        var componentIds = AllLayoutComponentIds(project).ToHashSet(StringComparer.Ordinal);
        var deviceIds = project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var channelIds = project.Channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"sensor-{ordinal}")
               || deviceIds.Contains($"device.sensor-{ordinal}")
               || channelIds.Contains($"di.sensor-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private static int NextCylinderOrdinal(MachineProjectDocument project)
    {
        var componentIds = AllLayoutComponentIds(project).ToHashSet(StringComparer.Ordinal);
        var deviceIds = project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var channelIds = project.Channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"cylinder-{ordinal}")
               || deviceIds.Contains($"device.cylinder-{ordinal}")
               || channelIds.Contains($"do.cylinder-{ordinal}.extend")
               || channelIds.Contains($"di.cylinder-{ordinal}.extended")
               || channelIds.Contains($"di.cylinder-{ordinal}.retracted"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private static int NextConveyorOrdinal(MachineProjectDocument project)
    {
        var componentIds = AllLayoutComponentIds(project).ToHashSet(StringComparer.Ordinal);
        var deviceIds = project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var channelIds = project.Channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"conveyor-{ordinal}")
               || deviceIds.Contains($"device.conveyor-{ordinal}")
               || channelIds.Contains($"do.conveyor-{ordinal}.run")
               || channelIds.Contains($"do.conveyor-{ordinal}.reverse"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private static int NextWorkpieceOrdinal(MachineProjectDocument project)
    {
        var componentIds = AllLayoutComponentIds(project).ToHashSet(StringComparer.Ordinal);
        var deviceIds = project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"workpiece-{ordinal}")
               || deviceIds.Contains($"device.workpiece-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }
}
