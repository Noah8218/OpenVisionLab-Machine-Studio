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
    private readonly LayoutComponentAuthoringFactory _componentFactory = new();
    private readonly LayoutComponentPlacementService _placementService = new();

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
        var component = _componentFactory.TryCreate(
            project,
            layout,
            kind,
            selectedComponentId,
            out var componentFailure);
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
            _placementService.Place(project, layout, component, dropX, dropY);
        }
        else if (_placementService.UsesIndependentDefaultPlacement(component.Kind))
        {
            var position = _placementService.FindNearestAvailablePosition(layout, component);
            _placementService.Place(project, layout, component, position.X, position.Y);
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
}
