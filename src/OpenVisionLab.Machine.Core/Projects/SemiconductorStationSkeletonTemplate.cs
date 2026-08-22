using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Core.Projects;

public enum SemiconductorStationSkeletonRole
{
    StationLayout,
    MachineFrame,
    Transport,
    Workpiece,
    ProcessAxisStage,
    EntrySensor,
    ProcessSensor,
    ProcessCylinder,
    RequiredIo,
    AutomaticSequence
}

public enum SemiconductorStationSkeletonStatus
{
    Proposed,
    Existing,
    Unavailable
}

public enum SemiconductorStationSkeletonUnavailableReason
{
    None,
    ActiveLayoutConflict,
    ExistingRoleInvalid,
    DependencyUnavailable,
    AutomaticSequenceConflict
}

public sealed record SemiconductorStationSkeletonEntry(
    SemiconductorStationSkeletonRole Role,
    SemiconductorStationSkeletonStatus Status,
    string? TargetId,
    int ExistingCount = 0,
    int AddedCount = 0,
    SemiconductorStationSkeletonUnavailableReason UnavailableReason =
        SemiconductorStationSkeletonUnavailableReason.None);

public sealed record SemiconductorStationSkeletonPreview(
    IReadOnlyList<SemiconductorStationSkeletonEntry> Entries)
{
    public int ProposedCount => Entries.Count(entry =>
        entry.Status == SemiconductorStationSkeletonStatus.Proposed);
    public int ExistingCount => Entries.Count(entry =>
        entry.Status == SemiconductorStationSkeletonStatus.Existing);
    public int UnavailableCount => Entries.Count(entry =>
        entry.Status == SemiconductorStationSkeletonStatus.Unavailable);
    public bool CanApply => ProposedCount > 0 && UnavailableCount == 0;
}

public sealed record SemiconductorStationSkeletonApplyResult(
    SemiconductorStationSkeletonPreview Preview,
    int AppliedCount,
    bool Changed = false);

/// <summary>
/// Adds one deterministic semiconductor transfer-station starting point without
/// replacing compatible authored roles that already exist in the active layout.
/// </summary>
public sealed class SemiconductorStationSkeletonTemplate
{
    private readonly ProjectDocumentStore _store = new();

    public SemiconductorStationSkeletonPreview Preview(MachineProjectDocument project)
        => Preview(project, ResolveSetup(project));

    public SemiconductorStationSkeletonPreview Preview(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);
        if (!IsValidSetup(setup))
        {
            throw new ArgumentException("The semiconductor station setup is invalid.", nameof(setup));
        }
        return Build(Clone(project), setup);
    }

    public SemiconductorStationSkeletonApplyResult Apply(MachineProjectDocument project)
        => Apply(project, ResolveSetup(project));

    public SemiconductorStationSkeletonApplyResult Apply(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);
        if (!IsValidSetup(setup))
        {
            throw new ArgumentException("The semiconductor station setup is invalid.", nameof(setup));
        }

        var updated = Clone(project);
        var preview = Build(updated, setup);
        if (preview.UnavailableCount > 0)
        {
            return new SemiconductorStationSkeletonApplyResult(preview, 0);
        }

        updated.Schema = MachineProjectDocument.CurrentSchema;
        updated.SemiconductorStationSetup = setup with { };
        var changed = !string.Equals(
            _store.SerializeForEvidence(project),
            _store.SerializeForEvidence(updated),
            StringComparison.Ordinal);
        if (!changed)
        {
            return new SemiconductorStationSkeletonApplyResult(preview, 0);
        }

        project.Schema = updated.Schema;
        project.SemiconductorStationSetup = updated.SemiconductorStationSetup;
        project.Simulation.ActiveLayoutId = updated.Simulation.ActiveLayoutId;
        project.Simulation.AutomaticRun = updated.Simulation.AutomaticRun;
        project.Layouts = updated.Layouts;
        project.Axes = updated.Axes;
        project.Devices = updated.Devices;
        project.Channels = updated.Channels;
        project.Sequences = updated.Sequences;
        return new SemiconductorStationSkeletonApplyResult(preview, preview.ProposedCount, true);
    }

    public SemiconductorStationSetupDefinition ResolveSetup(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.SemiconductorStationSetup is { } persisted && IsValidSetup(persisted))
        {
            return persisted with { };
        }

        var setup = new SemiconductorStationSetupDefinition();
        var layout = project.Layouts.FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                project.Simulation.ActiveLayoutId,
                StringComparison.Ordinal))
            ?? (project.Layouts.Count == 1 ? project.Layouts[0] : null);
        if (!string.IsNullOrWhiteSpace(layout?.Name))
        {
            setup.StationName = layout.Name;
        }

        var workpiece = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Workpiece && device.Workpiece is not null);
        if (!string.IsNullOrWhiteSpace(workpiece?.Workpiece?.Type))
        {
            setup.WaferType = workpiece.Workpiece.Type;
        }

        var axis = project.Axes.FirstOrDefault(candidate => candidate.Kind == AxisKind.Linear);
        if (axis?.SoftLimitMin is { } minimum && axis.SoftLimitMax is { } maximum
            && double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum)
        {
            setup.AxisTravel = maximum - minimum;
        }

        var transport = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Conveyor && device.Conveyor is not null);
        if (transport?.Conveyor?.SpeedUnitsPerSecond is > 0 and var speed && double.IsFinite(speed))
        {
            setup.TransportSpeed = speed;
        }

        var sensorPositions = layout?.Components
            .Where(component => component.Kind == LayoutComponentKind.DigitalSensor)
            .Select(component => component.Transform.X)
            .Where(double.IsFinite)
            .Order()
            .Take(2)
            .ToArray() ?? [];
        if (sensorPositions.Length == 2 && sensorPositions[0] < sensorPositions[1])
        {
            setup.EntrySensorPosition = sensorPositions[0];
            setup.ProcessSensorPosition = sensorPositions[1];
        }

        var cylinder = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Cylinder && device.Cylinder is not null);
        if (cylinder?.Cylinder?.ExtendDurationMilliseconds is > 0 and var duration)
        {
            setup.CylinderTravelTimeMilliseconds = duration;
        }
        return setup;
    }

    public static bool IsValidSetup(SemiconductorStationSetupDefinition setup) =>
        !string.IsNullOrWhiteSpace(setup.StationName)
        && !string.IsNullOrWhiteSpace(setup.WaferType)
        && double.IsFinite(setup.AxisTravel)
        && setup.AxisTravel > 0
        && double.IsFinite(setup.TransportSpeed)
        && setup.TransportSpeed > 0
        && double.IsFinite(setup.EntrySensorPosition)
        && double.IsFinite(setup.ProcessSensorPosition)
        && setup.EntrySensorPosition < setup.ProcessSensorPosition
        && setup.CylinderTravelTimeMilliseconds > 0;

    private MachineProjectDocument Clone(MachineProjectDocument project) =>
        _store.Load(_store.Serialize(project));

    private static SemiconductorStationSkeletonPreview Build(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup)
    {
        var entries = new List<SemiconductorStationSkeletonEntry>(10);
        var layout = ResolveLayout(project, setup, entries);
        if (layout is null)
        {
            AddUnavailableDependencies(entries);
            return CreatePreview(entries);
        }

        ResolveFrame(project, layout, setup, entries);
        var transport = ResolveTransport(project, layout, setup, entries);
        var workpiece = transport is null
            ? Unavailable<WorkpieceRole>(entries, SemiconductorStationSkeletonRole.Workpiece)
            : ResolveWorkpiece(project, layout, transport.Value.Component, setup, entries);
        var axisStage = ResolveAxisStage(project, layout, setup, entries);
        var sensors = workpiece is null
            ? Unavailable<SensorRoles>(entries,
                SemiconductorStationSkeletonRole.EntrySensor,
                SemiconductorStationSkeletonRole.ProcessSensor)
            : ResolveSensors(project, layout, workpiece.Value.Component, setup, entries);
        var cylinder = ResolveCylinder(project, layout, setup, entries);

        var initialChannelCount = entries
            .Where(entry => entry.Role is SemiconductorStationSkeletonRole.Transport
                or SemiconductorStationSkeletonRole.Workpiece
                or SemiconductorStationSkeletonRole.EntrySensor
                or SemiconductorStationSkeletonRole.ProcessSensor
                or SemiconductorStationSkeletonRole.ProcessCylinder)
            .Sum(entry => entry.AddedCount);

        var cycleActive = project.Simulation.AutomaticRun is null
            ? ResolveOutput(project, "do.station-cycle-active", "Station Cycle Active")
            : new OutputRole(new ChannelDefinition(), 0);
        var cycleDone = project.Simulation.AutomaticRun is null
            ? ResolveOutput(project, "do.station-cycle-done", "Station Cycle Done")
            : new OutputRole(new ChannelDefinition(), 0);
        var addedChannelCount = initialChannelCount + cycleActive.AddedCount + cycleDone.AddedCount;
        entries.Add(new SemiconductorStationSkeletonEntry(
            SemiconductorStationSkeletonRole.RequiredIo,
            addedChannelCount > 0
                ? SemiconductorStationSkeletonStatus.Proposed
                : SemiconductorStationSkeletonStatus.Existing,
            null,
            project.Channels.Count - addedChannelCount,
            addedChannelCount));

        ResolveAutomaticSequence(
            project,
            entries,
            transport,
            workpiece,
            axisStage,
            sensors,
            cylinder,
            cycleActive.Channel,
            cycleDone.Channel);
        return CreatePreview(entries);
    }

    private static MachineLayoutDefinition? ResolveLayout(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        if (!string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId))
        {
            var active = project.Layouts.FirstOrDefault(layout => string.Equals(
                layout.Id,
                project.Simulation.ActiveLayoutId,
                StringComparison.Ordinal));
            entries.Add(active is null
                ? UnavailableEntry(
                    SemiconductorStationSkeletonRole.StationLayout,
                    project.Simulation.ActiveLayoutId,
                    SemiconductorStationSkeletonUnavailableReason.ActiveLayoutConflict)
                : ExistingEntry(SemiconductorStationSkeletonRole.StationLayout, active.Id));
            if (active is not null)
            {
                active.Name = setup.StationName;
            }
            return active;
        }

        if (project.Layouts.Count > 1)
        {
            entries.Add(UnavailableEntry(
                SemiconductorStationSkeletonRole.StationLayout,
                null,
                SemiconductorStationSkeletonUnavailableReason.ActiveLayoutConflict));
            return null;
        }

        if (project.Layouts.Count == 1)
        {
            var existing = project.Layouts[0];
            existing.Name = setup.StationName;
            project.Simulation.ActiveLayoutId = existing.Id;
            entries.Add(ProposedEntry(SemiconductorStationSkeletonRole.StationLayout, existing.Id));
            return existing;
        }

        var layoutId = UniqueId("station-layout", project.Layouts.Select(item => item.Id));
        var created = new MachineLayoutDefinition
        {
            Id = layoutId,
            Name = setup.StationName,
            GridSize = 10,
            SnapToGrid = true
        };
        project.Layouts.Add(created);
        project.Simulation.ActiveLayoutId = created.Id;
        entries.Add(ProposedEntry(SemiconductorStationSkeletonRole.StationLayout, created.Id));
        return created;
    }

    private static void ResolveFrame(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        var existing = layout.Components.FirstOrDefault(component =>
            component.Kind == LayoutComponentKind.MachineFrame);
        if (existing is not null)
        {
            existing.Name = $"{setup.StationName} Frame";
            entries.Add(ExistingEntry(SemiconductorStationSkeletonRole.MachineFrame, existing.Id));
            return;
        }

        var component = new LayoutComponentDefinition
        {
            Id = UniqueComponentId(project, "frame"),
            Name = $"{setup.StationName} Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 360, Y = 250 },
            Size = new Size2D { Width = 680, Height = 360 },
            ZIndex = -100
        };
        layout.Components.Add(component);
        entries.Add(ProposedEntry(SemiconductorStationSkeletonRole.MachineFrame, component.Id));
    }

    private static ConveyorRole? ResolveTransport(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        var candidates = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.Conveyor)
            .ToArray();
        foreach (var candidateComponent in candidates)
        {
            if (TryResolveConveyor(project, candidateComponent, out var role))
            {
                role.Device.Conveyor!.SpeedUnitsPerSecond = setup.TransportSpeed;
                entries.Add(ExistingEntry(SemiconductorStationSkeletonRole.Transport, candidateComponent.Id));
                return role;
            }
        }

        if (candidates.Length > 0)
        {
            entries.Add(UnavailableEntry(
                SemiconductorStationSkeletonRole.Transport,
                candidates[0].Id,
                SemiconductorStationSkeletonUnavailableReason.ExistingRoleInvalid));
            return null;
        }

        var componentId = UniqueComponentId(project, "transport");
        var deviceId = UniqueId("device.transport", project.Devices.Select(device => device.Id));
        var run = AddChannel(project, "do.transport.run", "Transport Run", ChannelKind.DigitalOutput, 0);
        var reverse = AddChannel(project, "do.transport.reverse", "Transport Reverse", ChannelKind.DigitalOutput, 0);
        var device = new DeviceDefinition
        {
            Id = deviceId,
            Name = "Wafer Transport",
            Kind = DeviceKind.Conveyor,
            MountPosition = new Coordinate3D(340, 380, 0),
            ChannelIds = { run.Id, reverse.Id },
            Conveyor = new ConveyorDefinition
            {
                RunCommandChannelId = run.Id,
                ReverseCommandChannelId = reverse.Id,
                SpeedUnitsPerSecond = setup.TransportSpeed
            },
            Properties = { ["connectionRole"] = "material transport" }
        };
        var component = new LayoutComponentDefinition
        {
            Id = componentId,
            Name = "Wafer Transport",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D { X = 340, Y = 380 },
            Size = new Size2D { Width = 560, Height = 70 },
            ZIndex = 10,
            BehaviorBindingId = device.Id
        };
        project.Devices.Add(device);
        layout.Components.Add(component);
        entries.Add(ProposedEntry(
            SemiconductorStationSkeletonRole.Transport,
            component.Id,
            addedCount: 2));
        return new ConveyorRole(component, device, run, reverse);
    }

    private static WorkpieceRole? ResolveWorkpiece(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentDefinition transport,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        var candidates = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.Workpiece)
            .ToArray();
        foreach (var candidateComponent in candidates)
        {
            var candidateDevice = ResolveDevice(project, candidateComponent, DeviceKind.Workpiece);
            if (candidateDevice?.Workpiece is { } definition && string.Equals(
                    definition.ConveyorComponentId,
                    transport.Id,
                    StringComparison.Ordinal))
            {
                candidateDevice.Name = setup.WaferType;
                definition.Type = setup.WaferType;
                candidateComponent.Name = setup.WaferType;
                entries.Add(ExistingEntry(SemiconductorStationSkeletonRole.Workpiece, candidateComponent.Id));
                return new WorkpieceRole(candidateComponent, candidateDevice);
            }
        }

        if (candidates.Any(component => ResolveDevice(project, component, DeviceKind.Workpiece) is null))
        {
            entries.Add(UnavailableEntry(
                SemiconductorStationSkeletonRole.Workpiece,
                candidates[0].Id,
                SemiconductorStationSkeletonUnavailableReason.ExistingRoleInvalid));
            return null;
        }

        var componentId = UniqueComponentId(project, "wafer");
        var deviceId = UniqueId("device.wafer", project.Devices.Select(device => device.Id));
        var x = transport.Transform.X - ((transport.Size.Width - 34) / 2d) + 20;
        var y = transport.Transform.Y;
        var device = new DeviceDefinition
        {
            Id = deviceId,
            Name = setup.WaferType,
            Kind = DeviceKind.Workpiece,
            MountPosition = new Coordinate3D(x, y, 0),
            Workpiece = new WorkpieceDefinition
            {
                Type = setup.WaferType,
                ConveyorComponentId = transport.Id,
                InspectionState = WorkpieceInspectionState.Pending
            },
            Properties = { ["connectionRole"] = "process material" }
        };
        var component = new LayoutComponentDefinition
        {
            Id = componentId,
            Name = setup.WaferType,
            Kind = LayoutComponentKind.Workpiece,
            Transform = new Transform2D { X = x, Y = y },
            Size = new Size2D { Width = 34, Height = 34 },
            ZIndex = 25,
            BehaviorBindingId = device.Id
        };
        project.Devices.Add(device);
        layout.Components.Add(component);
        entries.Add(ProposedEntry(SemiconductorStationSkeletonRole.Workpiece, component.Id));
        return new WorkpieceRole(component, device);
    }

    private static AxisStageRole? ResolveAxisStage(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        var candidates = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.LinearStage)
            .ToArray();
        foreach (var candidateComponent in candidates)
        {
            var candidateAxis = project.Axes.FirstOrDefault(candidate =>
                candidate.Kind == AxisKind.Linear
                && string.Equals(candidate.Id, candidateComponent.BehaviorBindingId, StringComparison.Ordinal));
            if (candidateAxis is not null)
            {
                candidateAxis.SoftLimitMin = 0;
                candidateAxis.SoftLimitMax = setup.AxisTravel;
                entries.Add(ExistingEntry(
                    SemiconductorStationSkeletonRole.ProcessAxisStage,
                    $"{candidateAxis.Id} / {candidateComponent.Id}"));
                return new AxisStageRole(candidateAxis, candidateComponent);
            }
        }

        if (candidates.Length > 0)
        {
            entries.Add(UnavailableEntry(
                SemiconductorStationSkeletonRole.ProcessAxisStage,
                candidates[0].Id,
                SemiconductorStationSkeletonUnavailableReason.ExistingRoleInvalid));
            return null;
        }

        var axis = new VirtualAxisDefinition
        {
            Id = UniqueId("axis.process", project.Axes.Select(item => item.Id)),
            Name = "Wafer Transfer Axis",
            Kind = AxisKind.Linear,
            Unit = "mm",
            HomePosition = 0,
            SoftLimitMin = 0,
            SoftLimitMax = setup.AxisTravel,
            MaxVelocity = 180,
            MaxAcceleration = 700,
            MaxDeceleration = 650,
            FollowingErrorLimit = VirtualAxisDefinition.DefaultFollowingErrorLimit,
            Position = new Coordinate3D(100, 230, 0)
        };
        var component = new LayoutComponentDefinition
        {
            Id = UniqueComponentId(project, "process-stage"),
            Name = "Wafer Transfer Stage",
            Kind = LayoutComponentKind.LinearStage,
            Transform = new Transform2D { X = 100, Y = 230 },
            Size = new Size2D { Width = 90, Height = 50 },
            ZIndex = 25,
            BehaviorBindingId = axis.Id
        };
        project.Axes.Add(axis);
        layout.Components.Add(component);
        entries.Add(ProposedEntry(
            SemiconductorStationSkeletonRole.ProcessAxisStage,
            $"{axis.Id} / {component.Id}"));
        return new AxisStageRole(axis, component);
    }

    private static SensorRoles? ResolveSensors(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentDefinition workpiece,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        var components = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.DigitalSensor)
            .ToArray();
        var malformed = components.Any(component => !TryResolveSensor(project, component, out _));
        var existing = components
            .Select(component => TryResolveSensor(project, component, out var role) ? role : null)
            .Where(role => role is not null && string.Equals(
                role.Device.Sensor!.TargetComponentId,
                workpiece.Id,
                StringComparison.Ordinal))
            .Cast<SensorRole>()
            .OrderBy(role => role.Component.Transform.X)
            .ToList();

        if (malformed && existing.Count < 2)
        {
            if (existing.Count == 1)
            {
                entries.Add(ExistingEntry(SemiconductorStationSkeletonRole.EntrySensor, existing[0].Component.Id));
                entries.Add(UnavailableEntry(
                    SemiconductorStationSkeletonRole.ProcessSensor,
                    null,
                    SemiconductorStationSkeletonUnavailableReason.ExistingRoleInvalid));
            }
            else
            {
                Unavailable<SensorRoles>(entries,
                    SemiconductorStationSkeletonRole.EntrySensor,
                    SemiconductorStationSkeletonRole.ProcessSensor);
            }
            return null;
        }

        while (existing.Count < 2)
        {
            var role = existing.Count == 0
                ? SemiconductorStationSkeletonRole.EntrySensor
                : SemiconductorStationSkeletonRole.ProcessSensor;
            var created = CreateSensor(project, layout, workpiece, setup, role);
            existing.Add(created);
            entries.Add(ProposedEntry(role, created.Component.Id, addedCount: 1));
        }

        if (!entries.Any(entry => entry.Role == SemiconductorStationSkeletonRole.EntrySensor))
        {
            entries.Add(ExistingEntry(
                SemiconductorStationSkeletonRole.EntrySensor,
                existing[0].Component.Id));
        }
        if (!entries.Any(entry => entry.Role == SemiconductorStationSkeletonRole.ProcessSensor))
        {
            entries.Add(ExistingEntry(
                SemiconductorStationSkeletonRole.ProcessSensor,
                existing[1].Component.Id));
        }

        ApplySensorPosition(existing[0], setup.EntrySensorPosition);
        ApplySensorPosition(existing[1], setup.ProcessSensorPosition);

        return new SensorRoles(existing[0], existing[1]);
    }

    private static SensorRole CreateSensor(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentDefinition workpiece,
        SemiconductorStationSetupDefinition setup,
        SemiconductorStationSkeletonRole role)
    {
        var isEntry = role == SemiconductorStationSkeletonRole.EntrySensor;
        var baseId = isEntry ? "sensor-entry" : "sensor-process";
        var componentId = UniqueComponentId(project, baseId);
        var deviceId = UniqueId($"device.{baseId}", project.Devices.Select(device => device.Id));
        var channel = AddChannel(
            project,
            $"di.{baseId}",
            isEntry ? "Wafer Entry Sensor" : "Process Position Sensor",
            ChannelKind.DigitalInput,
            0);
        var x = isEntry ? setup.EntrySensorPosition : setup.ProcessSensorPosition;
        var device = new DeviceDefinition
        {
            Id = deviceId,
            Name = channel.Name,
            Kind = DeviceKind.Sensor,
            MountPosition = new Coordinate3D(x, workpiece.Transform.Y, 0),
            ChannelIds = { channel.Id },
            Sensor = new DigitalSensorDefinition
            {
                OutputChannelId = channel.Id,
                TargetComponentId = workpiece.Id,
                OnDelayMilliseconds = 10,
                OffDelayMilliseconds = 10
            },
            Properties = { ["connectionRole"] = isEntry ? "entry detection" : "process-position detection" }
        };
        var component = new LayoutComponentDefinition
        {
            Id = componentId,
            Name = device.Name,
            Kind = LayoutComponentKind.DigitalSensor,
            Transform = new Transform2D { X = x, Y = workpiece.Transform.Y },
            Size = new Size2D { Width = 20, Height = 76 },
            ZIndex = 20,
            BehaviorBindingId = device.Id
        };
        project.Devices.Add(device);
        layout.Components.Add(component);
        return new SensorRole(component, device, channel);
    }

    private static void ApplySensorPosition(SensorRole sensor, double x)
    {
        sensor.Component.Transform.X = x;
        sensor.Device.MountPosition = sensor.Device.MountPosition with { X = x };
    }

    private static CylinderRole? ResolveCylinder(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        SemiconductorStationSetupDefinition setup,
        ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        var candidates = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder)
            .ToArray();
        foreach (var candidateComponent in candidates)
        {
            if (TryResolveCylinder(project, candidateComponent, out var role))
            {
                role.Device.Cylinder!.ExtendDurationMilliseconds = setup.CylinderTravelTimeMilliseconds;
                role.Device.Cylinder.RetractDurationMilliseconds = setup.CylinderTravelTimeMilliseconds;
                entries.Add(ExistingEntry(SemiconductorStationSkeletonRole.ProcessCylinder, candidateComponent.Id));
                return role;
            }
        }

        if (candidates.Length > 0)
        {
            entries.Add(UnavailableEntry(
                SemiconductorStationSkeletonRole.ProcessCylinder,
                candidates[0].Id,
                SemiconductorStationSkeletonUnavailableReason.ExistingRoleInvalid));
            return null;
        }

        var componentId = UniqueComponentId(project, "process-cylinder");
        var deviceId = UniqueId("device.process-cylinder", project.Devices.Select(device => device.Id));
        var extend = AddChannel(project, "do.cylinder.extend", "Process Cylinder Extend", ChannelKind.DigitalOutput, 0);
        var extended = AddChannel(project, "di.cylinder.extended", "Process Cylinder Extended", ChannelKind.DigitalInput, 0);
        var retracted = AddChannel(project, "di.cylinder.retracted", "Process Cylinder Retracted", ChannelKind.DigitalInput, 1);
        var device = new DeviceDefinition
        {
            Id = deviceId,
            Name = "Process Cylinder",
            Kind = DeviceKind.Cylinder,
            MountPosition = new Coordinate3D(430, 230, 0),
            ChannelIds = { extend.Id, extended.Id, retracted.Id },
            Cylinder = new PneumaticCylinderDefinition
            {
                ExtendCommandChannelId = extend.Id,
                ExtendedSensorChannelId = extended.Id,
                RetractedSensorChannelId = retracted.Id,
                ExtendDurationMilliseconds = setup.CylinderTravelTimeMilliseconds,
                RetractDurationMilliseconds = setup.CylinderTravelTimeMilliseconds,
                ExtendedSensorDelayMilliseconds = 10,
                RetractedSensorDelayMilliseconds = 10,
                Stroke = 60
            },
            Properties = { ["connectionRole"] = "station interlock" }
        };
        var component = new LayoutComponentDefinition
        {
            Id = componentId,
            Name = "Process Cylinder",
            Kind = LayoutComponentKind.PneumaticCylinder,
            Transform = new Transform2D { X = 430, Y = 230 },
            Size = new Size2D { Width = 100, Height = 38 },
            ZIndex = 30,
            BehaviorBindingId = device.Id
        };
        project.Devices.Add(device);
        layout.Components.Add(component);
        entries.Add(ProposedEntry(
            SemiconductorStationSkeletonRole.ProcessCylinder,
            component.Id,
            addedCount: 3));
        return new CylinderRole(component, device, extend, extended, retracted);
    }

    private static void ResolveAutomaticSequence(
        MachineProjectDocument project,
        ICollection<SemiconductorStationSkeletonEntry> entries,
        ConveyorRole? transport,
        WorkpieceRole? workpiece,
        AxisStageRole? axisStage,
        SensorRoles? sensors,
        CylinderRole? cylinder,
        ChannelDefinition cycleActive,
        ChannelDefinition cycleDone)
    {
        if (project.Simulation.AutomaticRun is { } automatic)
        {
            var existingSequence = project.Sequences.FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                automatic.SequenceId,
                StringComparison.Ordinal));
            entries.Add(existingSequence is null
                ? UnavailableEntry(
                    SemiconductorStationSkeletonRole.AutomaticSequence,
                    automatic.SequenceId,
                    SemiconductorStationSkeletonUnavailableReason.AutomaticSequenceConflict)
                : ExistingEntry(SemiconductorStationSkeletonRole.AutomaticSequence, existingSequence.Id));
            return;
        }

        if (transport is null || workpiece is null || axisStage is null || sensors is null || cylinder is null)
        {
            entries.Add(UnavailableEntry(
                SemiconductorStationSkeletonRole.AutomaticSequence,
                null,
                SemiconductorStationSkeletonUnavailableReason.DependencyUnavailable));
            return;
        }

        var sequenceId = UniqueId("automatic-cycle", project.Sequences.Select(sequence => sequence.Id));
        var moveTarget = ResolveMoveTarget(axisStage.Value.Axis);
        var sequence = new SequenceDefinition
        {
            Id = sequenceId,
            Name = "Semiconductor station automatic cycle",
            Steps =
            {
                Step("cycle-active", "Cycle Active", SequenceStepAction.SetSignal, cycleActive.Id, "true", "extend-cylinder"),
                Step("extend-cylinder", "Extend Process Cylinder", SequenceStepAction.SetSignal, cylinder.Value.Extend.Id, "true", "wait-cylinder-extended"),
                Step("wait-cylinder-extended", "Wait Process Cylinder Extended", SequenceStepAction.WaitSignal, cylinder.Value.Extended.Id, "true", "start-transport", 3000),
                Step("start-transport", "Start Wafer Transport", SequenceStepAction.SetSignal, transport.Value.Run.Id, "true", "wait-process-position"),
                Step("wait-process-position", "Wait Process Position Sensor", SequenceStepAction.WaitSignal, sensors.Value.Process.Channel.Id, "true", "stop-transport", 5000),
                Step("stop-transport", "Stop Wafer Transport", SequenceStepAction.SetSignal, transport.Value.Run.Id, "false", "move-process-axis"),
                Step("move-process-axis", "Move Wafer Transfer Axis", SequenceStepAction.MoveAxis, axisStage.Value.Axis.Id, moveTarget, "wait-process-axis"),
                Step("wait-process-axis", "Wait Wafer Transfer Axis", SequenceStepAction.WaitAxisDone, axisStage.Value.Axis.Id, string.Empty, "retract-cylinder", 5000),
                Step("retract-cylinder", "Retract Process Cylinder", SequenceStepAction.SetSignal, cylinder.Value.Extend.Id, "false", "wait-cylinder-retracted"),
                Step("wait-cylinder-retracted", "Wait Process Cylinder Retracted", SequenceStepAction.WaitSignal, cylinder.Value.Retracted.Id, "true", "cycle-done", 3000),
                Step("cycle-done", "Cycle Done", SequenceStepAction.SetSignal, cycleDone.Id, "true", "complete"),
                Step("complete", "Complete", SequenceStepAction.Complete, string.Empty, string.Empty, null)
            }
        };
        project.Sequences.Add(sequence);
        project.Simulation.AutomaticRun = new AutomaticRunDefinition
        {
            SequenceId = sequence.Id,
            Repeat = false,
            RepeatDelayMilliseconds = 0
        };
        entries.Add(ProposedEntry(SemiconductorStationSkeletonRole.AutomaticSequence, sequence.Id));
    }

    private static SequenceStepDefinition Step(
        string id,
        string name,
        SequenceStepAction action,
        string targetId,
        string parameter,
        string? nextStepId,
        int timeoutMs = 0) => new()
    {
        Id = id,
        Name = name,
        Action = action,
        TargetId = targetId,
        Parameter = parameter,
        NextStepId = nextStepId,
        TimeoutMs = timeoutMs
    };

    private static string ResolveMoveTarget(VirtualAxisDefinition axis)
    {
        var home = axis.HomePosition;
        var maximum = axis.SoftLimitMax;
        if (maximum is { } max && double.IsFinite(max) && max > home)
        {
            return (home + ((max - home) / 2d)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        var minimum = axis.SoftLimitMin;
        if (minimum is { } min && double.IsFinite(min) && min < home)
        {
            return (home - ((home - min) / 2d)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        return (home + 100).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OutputRole ResolveOutput(MachineProjectDocument project, string baseId, string name)
    {
        var existing = project.Channels.FirstOrDefault(channel =>
            channel.Kind == ChannelKind.DigitalOutput
            && string.Equals(channel.Id, baseId, StringComparison.Ordinal));
        return existing is not null
            ? new OutputRole(existing, 0)
            : new OutputRole(AddChannel(project, baseId, name, ChannelKind.DigitalOutput, 0), 1);
    }

    private static bool TryResolveConveyor(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        out ConveyorRole role)
    {
        var device = ResolveDevice(project, component, DeviceKind.Conveyor);
        var definition = device?.Conveyor;
        var run = definition is null ? null : ResolveChannel(project, definition.RunCommandChannelId, ChannelKind.DigitalOutput);
        var reverse = definition is null ? null : ResolveChannel(project, definition.ReverseCommandChannelId, ChannelKind.DigitalOutput);
        if (device is null || definition is null || run is null || reverse is null || run.Id == reverse.Id)
        {
            role = default;
            return false;
        }
        role = new ConveyorRole(component, device, run, reverse);
        return true;
    }

    private static bool TryResolveSensor(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        out SensorRole? role)
    {
        var device = ResolveDevice(project, component, DeviceKind.Sensor);
        var channel = device?.Sensor is { } sensor
            ? ResolveChannel(project, sensor.OutputChannelId, ChannelKind.DigitalInput)
            : null;
        if (device?.Sensor is null || channel is null)
        {
            role = null;
            return false;
        }
        role = new SensorRole(component, device, channel);
        return true;
    }

    private static bool TryResolveCylinder(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        out CylinderRole role)
    {
        var device = ResolveDevice(project, component, DeviceKind.Cylinder);
        var definition = device?.Cylinder;
        var extend = definition is null ? null : ResolveChannel(project, definition.ExtendCommandChannelId, ChannelKind.DigitalOutput);
        var extended = definition is null ? null : ResolveChannel(project, definition.ExtendedSensorChannelId, ChannelKind.DigitalInput);
        var retracted = definition is null ? null : ResolveChannel(project, definition.RetractedSensorChannelId, ChannelKind.DigitalInput);
        if (device is null || definition is null || extend is null || extended is null || retracted is null)
        {
            role = default;
            return false;
        }
        role = new CylinderRole(component, device, extend, extended, retracted);
        return true;
    }

    private static DeviceDefinition? ResolveDevice(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        DeviceKind kind) => project.Devices.FirstOrDefault(device =>
            device.Kind == kind
            && string.Equals(device.Id, component.BehaviorBindingId, StringComparison.Ordinal));

    private static ChannelDefinition? ResolveChannel(
        MachineProjectDocument project,
        string? id,
        ChannelKind kind) => project.Channels.FirstOrDefault(channel =>
            channel.Kind == kind && string.Equals(channel.Id, id, StringComparison.Ordinal));

    private static ChannelDefinition AddChannel(
        MachineProjectDocument project,
        string baseId,
        string name,
        ChannelKind kind,
        double initialValue)
    {
        var channel = new ChannelDefinition
        {
            Id = UniqueId(baseId, project.Channels.Select(item => item.Id)),
            Name = name,
            Kind = kind,
            InitialValue = initialValue
        };
        project.Channels.Add(channel);
        return channel;
    }

    private static string UniqueComponentId(MachineProjectDocument project, string baseId) => UniqueId(
        baseId,
        project.Layouts.SelectMany(layout => layout.Components).Select(component => component.Id));

    private static SemiconductorStationSkeletonPreview CreatePreview(
        IEnumerable<SemiconductorStationSkeletonEntry> entries) => new(
            entries.OrderBy(entry => entry.Role).ToArray());

    private static string UniqueId(string baseId, IEnumerable<string> existingIds)
    {
        var ids = existingIds.ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(baseId))
        {
            return baseId;
        }
        var suffix = 2;
        while (ids.Contains($"{baseId}-{suffix}"))
        {
            suffix++;
        }
        return $"{baseId}-{suffix}";
    }

    private static SemiconductorStationSkeletonEntry ProposedEntry(
        SemiconductorStationSkeletonRole role,
        string? targetId,
        int addedCount = 0) => new(
            role,
            SemiconductorStationSkeletonStatus.Proposed,
            targetId,
            AddedCount: addedCount);

    private static SemiconductorStationSkeletonEntry ExistingEntry(
        SemiconductorStationSkeletonRole role,
        string? targetId) => new(
            role,
            SemiconductorStationSkeletonStatus.Existing,
            targetId,
            ExistingCount: 1);

    private static SemiconductorStationSkeletonEntry UnavailableEntry(
        SemiconductorStationSkeletonRole role,
        string? targetId,
        SemiconductorStationSkeletonUnavailableReason reason) => new(
            role,
            SemiconductorStationSkeletonStatus.Unavailable,
            targetId,
            UnavailableReason: reason);

    private static T? Unavailable<T>(
        ICollection<SemiconductorStationSkeletonEntry> entries,
        params SemiconductorStationSkeletonRole[] roles)
        where T : struct
    {
        foreach (var role in roles)
        {
            entries.Add(UnavailableEntry(
                role,
                null,
                SemiconductorStationSkeletonUnavailableReason.DependencyUnavailable));
        }
        return null;
    }

    private static void AddUnavailableDependencies(ICollection<SemiconductorStationSkeletonEntry> entries)
    {
        Unavailable<ConveyorRole>(entries,
            SemiconductorStationSkeletonRole.MachineFrame,
            SemiconductorStationSkeletonRole.Transport,
            SemiconductorStationSkeletonRole.Workpiece,
            SemiconductorStationSkeletonRole.ProcessAxisStage,
            SemiconductorStationSkeletonRole.EntrySensor,
            SemiconductorStationSkeletonRole.ProcessSensor,
            SemiconductorStationSkeletonRole.ProcessCylinder,
            SemiconductorStationSkeletonRole.RequiredIo,
            SemiconductorStationSkeletonRole.AutomaticSequence);
    }

    private readonly record struct ConveyorRole(
        LayoutComponentDefinition Component,
        DeviceDefinition Device,
        ChannelDefinition Run,
        ChannelDefinition Reverse);
    private readonly record struct WorkpieceRole(LayoutComponentDefinition Component, DeviceDefinition Device);
    private readonly record struct AxisStageRole(VirtualAxisDefinition Axis, LayoutComponentDefinition Component);
    private sealed record SensorRole(LayoutComponentDefinition Component, DeviceDefinition Device, ChannelDefinition Channel);
    private readonly record struct SensorRoles(SensorRole Entry, SensorRole Process);
    private readonly record struct CylinderRole(
        LayoutComponentDefinition Component,
        DeviceDefinition Device,
        ChannelDefinition Extend,
        ChannelDefinition Extended,
        ChannelDefinition Retracted);
    private readonly record struct OutputRole(ChannelDefinition Channel, int AddedCount);
}
