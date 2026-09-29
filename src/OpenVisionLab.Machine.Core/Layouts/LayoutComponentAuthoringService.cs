using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Core.Layouts;

public enum LayoutComponentAuthoringFailureKind
{
    InvalidCoordinates,
    ActiveLayoutNotFound,
    ActiveLayoutRequired,
    SensorTargetRequired,
    WorkpieceCarrierRequired,
    CameraDeviceRequired,
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

public enum LayoutComponentRemovalReferenceKind
{
    Target,
    Workpiece,
    ExpectedTarget
}

public sealed record LayoutComponentRemovalImpact(
    string SequenceId,
    string SequenceName,
    string StepId,
    string StepName,
    string ComponentId,
    LayoutComponentRemovalReferenceKind ReferenceKind);

public sealed record LayoutComponentsRemovalResult(
    IReadOnlyList<LayoutComponentDefinition> RemovedComponents,
    LayoutComponentDefinition? BlockingComponent,
    LayoutComponentRemovalFailureKind? Failure)
{
    public bool IsSuccess => RemovedComponents.Count > 0 && Failure is null;
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
        double? worldY = null,
        string? unitId = null)
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

        if (unitId is not null)
        {
            component.UnitId = unitId;
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
        var result = TryRemove(project, layout, new[] { componentId });
        return new(
            result.RemovedComponents.FirstOrDefault(),
            result.BlockingComponent,
            result.Failure);
    }

    public IReadOnlyList<LayoutComponentRemovalImpact> GetRemovalImpacts(
        MachineProjectDocument project,
        IReadOnlyCollection<string> componentIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(componentIds);

        var ids = componentIds.ToHashSet(StringComparer.Ordinal);
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in project.Layouts.SelectMany(layout => layout.Components).Where(component => ids.Contains(component.Id)))
        {
            references.TryAdd(component.Id, component.Id);
            if (!string.IsNullOrWhiteSpace(component.BehaviorBindingId))
            {
                references.TryAdd(component.BehaviorBindingId, component.Id);
            }
        }

        var impacts = new List<LayoutComponentRemovalImpact>();
        foreach (var sequence in project.Sequences)
        {
            foreach (var step in sequence.Steps)
            {
                AddRemovalImpact(
                    impacts,
                    references,
                    sequence.Id,
                    sequence.Name,
                    step.Id,
                    step.Name,
                    step.TargetId,
                    LayoutComponentRemovalReferenceKind.Target);
                AddRemovalImpact(
                    impacts,
                    references,
                    sequence.Id,
                    sequence.Name,
                    step.Id,
                    step.Name,
                    step.WorkpieceComponentId,
                    LayoutComponentRemovalReferenceKind.Workpiece);
                AddRemovalImpact(
                    impacts,
                    references,
                    sequence.Id,
                    sequence.Name,
                    step.Id,
                    step.Name,
                    step.ExpectedTargetId,
                    LayoutComponentRemovalReferenceKind.ExpectedTarget);
            }
        }

        return impacts;
    }

    public LayoutComponentsRemovalResult TryRemove(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        IReadOnlyCollection<string> componentIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(componentIds);

        var ids = componentIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var components = layout.Components.Where(component => ids.Contains(component.Id)).ToArray();
        if (ids.Count == 0 || components.Length != ids.Count)
        {
            return new(Array.Empty<LayoutComponentDefinition>(), null, LayoutComponentRemovalFailureKind.NotFound);
        }

        var dependentSensorComponent = project.Layouts
            .SelectMany(definition => definition.Components)
            .Where(candidate => !ids.Contains(candidate.Id))
            .Where(candidate => candidate.Kind == LayoutComponentKind.DigitalSensor)
            .Select(candidate => new
            {
                Component = candidate,
                Device = project.Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, candidate.BehaviorBindingId, StringComparison.Ordinal))
            })
            .FirstOrDefault(candidate =>
                candidate.Device?.Sensor is { } sensor && ids.Contains(sensor.TargetComponentId));
        if (dependentSensorComponent is not null)
        {
            return new(
                Array.Empty<LayoutComponentDefinition>(),
                dependentSensorComponent.Component,
                LayoutComponentRemovalFailureKind.SensorDependency);
        }

        var dependentWorkpiece = project.Layouts
            .SelectMany(definition => definition.Components)
            .Where(candidate => !ids.Contains(candidate.Id))
            .Where(candidate => candidate.Kind == LayoutComponentKind.Workpiece)
            .Select(candidate => new
            {
                Component = candidate,
                Device = project.Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, candidate.BehaviorBindingId, StringComparison.Ordinal))
            })
            .FirstOrDefault(candidate =>
                candidate.Device?.Workpiece is { } workpiece && ids.Contains(workpiece.ConveyorComponentId));
        if (dependentWorkpiece is not null)
        {
            return new(
                Array.Empty<LayoutComponentDefinition>(),
                dependentWorkpiece.Component,
                LayoutComponentRemovalFailureKind.WorkpieceDependency);
        }

        layout.Components.RemoveAll(component => ids.Contains(component.Id));
        return new(components, null, null);
    }

    private static void AddRemovalImpact(
        ICollection<LayoutComponentRemovalImpact> impacts,
        IReadOnlyDictionary<string, string> componentIds,
        string sequenceId,
        string sequenceName,
        string stepId,
        string stepName,
        string? referencedComponentId,
        LayoutComponentRemovalReferenceKind referenceKind)
    {
        if (referencedComponentId is not null && componentIds.TryGetValue(referencedComponentId, out var componentId))
        {
            impacts.Add(new(sequenceId, sequenceName, stepId, stepName, componentId, referenceKind));
        }
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
