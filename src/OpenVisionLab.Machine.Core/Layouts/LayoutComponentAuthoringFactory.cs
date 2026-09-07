using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Core.Layouts;

/// <summary>
/// Creates one authored layout component and any bound project artifacts it requires.
/// It does not choose placement or validate the complete project graph.
/// </summary>
public sealed class LayoutComponentAuthoringFactory
{
    public LayoutComponentDefinition? TryCreate(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentKind kind,
        string? selectedComponentId,
        out LayoutComponentAuthoringFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);

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
