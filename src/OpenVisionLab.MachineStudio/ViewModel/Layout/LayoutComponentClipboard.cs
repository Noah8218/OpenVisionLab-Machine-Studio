using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record LayoutPasteResult(
    bool IsSuccess,
    IReadOnlyList<string> ComponentIds,
    MachineProjectLayoutValidationError? Error = null);

internal sealed class LayoutComponentClipboard
{
    private readonly ProjectDocumentStore _projectStore = new();
    private string? _projectJson;
    private string? _layoutId;
    private IReadOnlyList<string> _componentIds = Array.Empty<string>();
    private int _pasteCount;

    public bool HasContent => _projectJson is not null && _layoutId is not null && _componentIds.Count > 0;

    public int Copy(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        IEnumerable<string> componentIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(componentIds);
        _componentIds = componentIds.ToArray();
        if (_componentIds.Count == 0)
        {
            Clear();
            return 0;
        }

        _projectJson = _projectStore.Serialize(project);
        _layoutId = layout.Id;
        _pasteCount = 0;
        return _componentIds.Count;
    }

    public LayoutPasteResult Paste(
        MachineProjectDocument targetProject,
        MachineLayoutDefinition targetLayout)
    {
        ArgumentNullException.ThrowIfNull(targetProject);
        ArgumentNullException.ThrowIfNull(targetLayout);
        if (!HasContent)
        {
            return new LayoutPasteResult(false, Array.Empty<string>());
        }

        var sourceProject = _projectStore.Load(_projectJson!);
        var sourceLayout = sourceProject.Layouts.FirstOrDefault(layout => string.Equals(
            layout.Id,
            _layoutId,
            StringComparison.Ordinal));
        var sourceComponents = _componentIds
            .Select(id => sourceLayout?.Components.FirstOrDefault(component => string.Equals(
                component.Id,
                id,
                StringComparison.Ordinal)))
            .Where(component => component is not null)
            .Cast<LayoutComponentDefinition>()
            .ToArray();
        if (sourceComponents.Length == 0)
        {
            return new LayoutPasteResult(false, Array.Empty<string>());
        }

        var usedComponentIds = targetProject.Layouts
            .SelectMany(layout => layout.Components)
            .Select(component => component.Id)
            .ToHashSet(StringComparer.Ordinal);
        var componentIdMap = sourceComponents.ToDictionary(
            source => source.Id,
            source => NextComponentId(source.Kind, usedComponentIds),
            StringComparer.Ordinal);
        var axisIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var deviceIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var channelIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var pasteOffset = targetLayout.GridSize * (_pasteCount + 1);
        var componentCount = targetLayout.Components.Count;
        var axisCount = targetProject.Axes.Count;
        var deviceCount = targetProject.Devices.Count;
        var channelCount = targetProject.Channels.Count;

        try
        {
            foreach (var component in sourceComponents)
            {
                var sourceId = component.Id;
                component.Id = componentIdMap[sourceId];
                component.Name = $"{component.Name} Copy";
                component.Transform.X += pasteOffset;
                component.Transform.Y += pasteOffset;
                component.BehaviorBindingId = CloneBehaviorBinding(
                    sourceProject,
                    targetProject,
                    component,
                    component.BehaviorBindingId,
                    componentIdMap,
                    axisIdMap,
                    deviceIdMap,
                    channelIdMap);
                targetLayout.Components.Add(component);
            }

            var validation = new MachineProjectLayoutValidator().Validate(targetProject);
            if (!validation.IsValid)
            {
                RollbackPaste(targetProject, targetLayout, componentCount, axisCount, deviceCount, channelCount);
                return new LayoutPasteResult(false, Array.Empty<string>(), validation.Errors[0]);
            }
        }
        catch
        {
            RollbackPaste(targetProject, targetLayout, componentCount, axisCount, deviceCount, channelCount);
            throw;
        }

        _pasteCount++;
        return new LayoutPasteResult(true, sourceComponents.Select(component => component.Id).ToArray());
    }

    private static void RollbackPaste(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        int componentCount,
        int axisCount,
        int deviceCount,
        int channelCount)
    {
        layout.Components.RemoveRange(componentCount, layout.Components.Count - componentCount);
        project.Axes.RemoveRange(axisCount, project.Axes.Count - axisCount);
        project.Devices.RemoveRange(deviceCount, project.Devices.Count - deviceCount);
        project.Channels.RemoveRange(channelCount, project.Channels.Count - channelCount);
    }

    public void Clear()
    {
        _projectJson = null;
        _layoutId = null;
        _componentIds = Array.Empty<string>();
        _pasteCount = 0;
    }

    private static string NextComponentId(
        LayoutComponentKind kind,
        ISet<string> usedIds)
    {
        var prefix = kind switch
        {
            LayoutComponentKind.MachineFrame => "frame",
            LayoutComponentKind.LinearStage => "stage",
            LayoutComponentKind.RotaryStage => "rotary-stage",
            LayoutComponentKind.DigitalSensor => "sensor",
            LayoutComponentKind.PneumaticCylinder => "cylinder",
            LayoutComponentKind.Conveyor => "conveyor",
            LayoutComponentKind.Workpiece => "workpiece",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var id = $"{prefix}-{NextOrdinal(prefix, usedIds)}";
        usedIds.Add(id);
        return id;
    }

    private static string? CloneBehaviorBinding(
        MachineProjectDocument sourceProject,
        MachineProjectDocument targetProject,
        LayoutComponentDefinition component,
        string? sourceBindingId,
        IReadOnlyDictionary<string, string> componentIdMap,
        IDictionary<string, string> axisIdMap,
        IDictionary<string, string> deviceIdMap,
        IDictionary<string, string> channelIdMap)
    {
        if (string.IsNullOrWhiteSpace(sourceBindingId))
        {
            return null;
        }

        if (component.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            if (axisIdMap.TryGetValue(sourceBindingId, out var existingAxisId))
            {
                return existingAxisId;
            }

            var axis = sourceProject.Axes.Single(item => item.Id == sourceBindingId);
            var axisId = $"axis-{NextOrdinal("axis", targetProject.Axes.Select(item => item.Id).Concat(axisIdMap.Values))}";
            axisIdMap[sourceBindingId] = axisId;
            axis.Id = axisId;
            axis.Name = $"{axis.Name} Copy";
            axis.Position = new Coordinate3D(component.Transform.X, component.Transform.Y, axis.Position.Z);
            targetProject.Axes.Add(axis);
            return axisId;
        }

        if (deviceIdMap.TryGetValue(sourceBindingId, out var existingDeviceId))
        {
            return existingDeviceId;
        }

        var device = sourceProject.Devices.Single(item => item.Id == sourceBindingId);
        var deviceId = NextUniqueId(
            $"device.{component.Id}",
            targetProject.Devices.Select(item => item.Id).Concat(deviceIdMap.Values));
        deviceIdMap[sourceBindingId] = deviceId;
        device.Id = deviceId;
        device.Name = $"{device.Name} Copy";
        device.MountPosition = new Coordinate3D(component.Transform.X, component.Transform.Y, device.MountPosition.Z);
        device.ChannelIds = device.ChannelIds
            .Select(channelId => CloneChannel(sourceProject, targetProject, channelId, component.Id, channelIdMap))
            .ToList();

        if (device.Sensor is { } sensor)
        {
            sensor.OutputChannelId = channelIdMap[sensor.OutputChannelId];
            if (componentIdMap.TryGetValue(sensor.TargetComponentId, out var targetId))
            {
                sensor.TargetComponentId = targetId;
            }
        }
        else if (device.Cylinder is { } cylinder)
        {
            cylinder.ExtendCommandChannelId = channelIdMap[cylinder.ExtendCommandChannelId];
            cylinder.ExtendedSensorChannelId = channelIdMap[cylinder.ExtendedSensorChannelId];
            cylinder.RetractedSensorChannelId = channelIdMap[cylinder.RetractedSensorChannelId];
        }
        else if (device.Conveyor is { } conveyor)
        {
            conveyor.RunCommandChannelId = channelIdMap[conveyor.RunCommandChannelId];
            conveyor.ReverseCommandChannelId = channelIdMap[conveyor.ReverseCommandChannelId];
        }
        else if (device.Workpiece is { } workpiece &&
                 componentIdMap.TryGetValue(workpiece.ConveyorComponentId, out var conveyorId))
        {
            workpiece.ConveyorComponentId = conveyorId;
        }

        targetProject.Devices.Add(device);
        return deviceId;
    }

    private static string CloneChannel(
        MachineProjectDocument sourceProject,
        MachineProjectDocument targetProject,
        string sourceChannelId,
        string componentId,
        IDictionary<string, string> channelIdMap)
    {
        if (channelIdMap.TryGetValue(sourceChannelId, out var existingChannelId))
        {
            return existingChannelId;
        }

        var channel = sourceProject.Channels.Single(item => item.Id == sourceChannelId);
        var suffix = sourceChannelId[(sourceChannelId.LastIndexOf('.') + 1)..];
        var prefix = channel.Kind == ChannelKind.DigitalInput ? "di" : "do";
        var baseId = $"{prefix}.{componentId}.{suffix}";
        var channelId = baseId;
        var ordinal = 2;
        while (targetProject.Channels.Any(item => item.Id == channelId) ||
               channelIdMap.Values.Contains(channelId, StringComparer.Ordinal))
        {
            channelId = $"{baseId}-{ordinal++}";
        }

        channelIdMap[sourceChannelId] = channelId;
        channel.Id = channelId;
        channel.Name = $"{channel.Name} Copy";
        targetProject.Channels.Add(channel);
        return channelId;
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

    private static string NextUniqueId(string baseId, IEnumerable<string> ids)
    {
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(baseId))
        {
            return baseId;
        }

        var ordinal = 2;
        while (existing.Contains($"{baseId}-{ordinal}"))
        {
            ordinal++;
        }
        return $"{baseId}-{ordinal}";
    }
}
