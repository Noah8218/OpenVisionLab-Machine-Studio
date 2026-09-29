using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Core.Layouts;

public enum MachineProjectLayoutValidationErrorCode
{
    LayoutIdRequired,
    LayoutNameRequired,
    DuplicateLayoutId,
    InvalidGridSize,
    ComponentsRequired,
    ComponentIdRequired,
    ComponentNameRequired,
    DuplicateComponentId,
    UnsupportedComponentKind,
    InvalidTransform,
    InvalidSize,
    InvalidVerticalEnvelope,
    MissingBehaviorBinding,
    UnsupportedBehaviorBinding,
    AxisBindingNotFound,
    AxisBindingMustBeLinear,
    AxisBindingMustBeRotary,
    SensorDeviceBindingInvalid,
    SensorOutputChannelRequired,
    SensorOutputChannelNotFound,
    SensorOutputChannelMustBeDigitalInput,
    SensorTargetComponentRequired,
    SensorTargetComponentNotFound,
    SensorTargetComponentMustBeInSameLayout,
    SensorDelayInvalid,
    CylinderDeviceBindingInvalid,
    CylinderChannelIdRequired,
    CylinderChannelNotFound,
    CylinderCommandMustBeDigitalOutput,
    CylinderFeedbackMustBeDigitalInput,
    CylinderChannelIdsMustBeDistinct,
    CylinderDurationInvalid,
    CylinderSensorDelayInvalid,
    CylinderStrokeInvalid,
    ConveyorDeviceBindingInvalid,
    ConveyorChannelIdRequired,
    ConveyorChannelNotFound,
    ConveyorCommandMustBeDigitalOutput,
    ConveyorChannelIdsMustBeDistinct,
    ConveyorSpeedInvalid,
    WorkpieceDeviceBindingInvalid,
    CameraDeviceBindingInvalid,
    WorkpieceTypeRequired,
    WorkpieceConveyorComponentRequired,
    WorkpieceConveyorComponentNotFound,
    WorkpieceCarrierMustBeConveyor,
    WorkpieceInspectionStateInvalid,
    AmbiguousBehaviorBinding,
    StationsRequired,
    StationIdRequired,
    StationNameRequired,
    DuplicateStationId,
    StationUnitsRequired,
    UnitIdRequired,
    UnitNameRequired,
    DuplicateUnitId,
    InvalidComponentUnitId,
    ComponentUnitNotFound
}

public sealed record MachineProjectLayoutValidationError(
    MachineProjectLayoutValidationErrorCode Code,
    string? LayoutId,
    string? ComponentId,
    string Message);

public sealed class MachineProjectLayoutValidationResult
{
    public MachineProjectLayoutValidationResult(
        IEnumerable<MachineProjectLayoutValidationError> errors)
    {
        Errors = errors.ToArray();
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<MachineProjectLayoutValidationError> Errors { get; }
}

/// <summary>
/// Validates authored layout geometry and its explicit links to runtime-neutral
/// project definitions. It never mutates the project or creates inferred links.
/// </summary>
public sealed class MachineProjectLayoutValidator
{
    public MachineProjectLayoutValidationResult Validate(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var errors = new List<MachineProjectLayoutValidationError>();
        var layouts = project.Layouts ?? new List<MachineLayoutDefinition>();
        var unitIds = ValidateStations(project.Stations, errors);
        var layoutIds = new HashSet<string>(StringComparer.Ordinal);
        var componentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var layout in layouts)
        {
            if (layout is null)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.LayoutIdRequired,
                    null,
                    null,
                    "Layout entries cannot be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.LayoutIdRequired,
                    layout.Id,
                    null,
                    "Every layout requires a stable id."));
            }
            else if (!layoutIds.Add(layout.Id))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.DuplicateLayoutId,
                    layout.Id,
                    null,
                    $"Layout id '{layout.Id}' is duplicated."));
            }

            if (string.IsNullOrWhiteSpace(layout.Name))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.LayoutNameRequired,
                    layout.Id,
                    null,
                    "Every layout requires a name."));
            }

            if (!double.IsFinite(layout.GridSize) || layout.GridSize <= 0)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.InvalidGridSize,
                    layout.Id,
                    null,
                    "Layout grid size must be finite and positive."));
            }

            if (layout.Components is null)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.ComponentsRequired,
                    layout.Id,
                    null,
                    "Layout components cannot be null."));
                continue;
            }

            foreach (var component in layout.Components)
            {
                if (component is null)
                {
                    errors.Add(Error(
                        MachineProjectLayoutValidationErrorCode.ComponentIdRequired,
                        layout.Id,
                        null,
                        "Layout component entries cannot be null."));
                    continue;
                }

                ValidateComponentDefinition(layout, component, componentIds, unitIds, errors);
            }
        }

        errors.AddRange(new MachineProjectLayoutBehaviorBindingValidator().Validate(project).Errors);
        return new MachineProjectLayoutValidationResult(errors);
    }

    private static void ValidateComponentDefinition(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ISet<string> componentIds,
        ISet<string> unitIds,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(component.Id))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ComponentIdRequired,
                layout.Id,
                component.Id,
                "Every layout component requires a stable id."));
        }
        else if (!componentIds.Add(component.Id))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.DuplicateComponentId,
                layout.Id,
                component.Id,
                $"Layout component id '{component.Id}' is duplicated."));
        }

        if (string.IsNullOrWhiteSpace(component.Name))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ComponentNameRequired,
                layout.Id,
                component.Id,
                "Every layout component requires a name."));
        }

        if (component.UnitId is not null)
        {
            if (string.IsNullOrWhiteSpace(component.UnitId))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.InvalidComponentUnitId,
                    layout.Id,
                    component.Id,
                    "A component unit id must be null or name an existing unit."));
            }
            else if (!unitIds.Contains(component.UnitId))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.ComponentUnitNotFound,
                    layout.Id,
                    component.Id,
                    $"Component unit '{component.UnitId}' was not found."));
            }
        }

        if (!Enum.IsDefined(component.Kind))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.UnsupportedComponentKind,
                layout.Id,
                component.Id,
                $"Layout component kind '{component.Kind}' is unsupported."));
        }

        if (component.Transform is null ||
            !double.IsFinite(component.Transform.X) ||
            !double.IsFinite(component.Transform.Y) ||
            !double.IsFinite(component.Transform.RotationDegrees))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.InvalidTransform,
                layout.Id,
                component.Id,
                "Component transform values must be finite."));
        }

        if (component.Size is null ||
            !double.IsFinite(component.Size.Width) ||
            !double.IsFinite(component.Size.Height) ||
            component.Size.Width <= 0 ||
            component.Size.Height <= 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.InvalidSize,
                layout.Id,
                component.Id,
                "Component width and height must be finite and positive."));
        }

        if (component.VerticalEnvelope is { } verticalEnvelope &&
            (!double.IsFinite(verticalEnvelope.BaseElevation) ||
             !double.IsFinite(verticalEnvelope.Height) ||
             verticalEnvelope.Height <= 0))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.InvalidVerticalEnvelope,
                layout.Id,
                component.Id,
                "Component vertical envelope must have a finite base elevation and positive finite height."));
        }
    }

    private static HashSet<string> ValidateStations(
        IReadOnlyCollection<MachineStationDefinition>? stations,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        var stationIds = new HashSet<string>(StringComparer.Ordinal);
        var unitIds = new HashSet<string>(StringComparer.Ordinal);
        if (stations is null)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.StationsRequired,
                null,
                null,
                "Station definitions cannot be null."));
            return unitIds;
        }

        foreach (MachineStationDefinition? station in stations)
        {
            if (station is null)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.StationIdRequired,
                    null,
                    null,
                    "Station entries cannot be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(station.Id))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.StationIdRequired,
                    null,
                    null,
                    "Every station requires a stable id."));
            }
            else if (!stationIds.Add(station.Id))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.DuplicateStationId,
                    null,
                    null,
                    $"Station id '{station.Id}' is duplicated."));
            }

            if (string.IsNullOrWhiteSpace(station.Name))
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.StationNameRequired,
                    null,
                    null,
                    $"Station '{station.Id}' requires a name."));
            }

            if (station.Units is null)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.StationUnitsRequired,
                    null,
                    null,
                    $"Station '{station.Id}' unit list cannot be null."));
                continue;
            }

            foreach (MachineUnitDefinition? unit in station.Units)
            {
                if (unit is null)
                {
                    errors.Add(Error(
                        MachineProjectLayoutValidationErrorCode.UnitIdRequired,
                        null,
                        null,
                        $"Station '{station.Id}' contains a null unit."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(unit.Id))
                {
                    errors.Add(Error(
                        MachineProjectLayoutValidationErrorCode.UnitIdRequired,
                        null,
                        null,
                        $"Every unit in station '{station.Id}' requires a stable id."));
                }
                else if (!unitIds.Add(unit.Id))
                {
                    errors.Add(Error(
                        MachineProjectLayoutValidationErrorCode.DuplicateUnitId,
                        null,
                        null,
                        $"Unit id '{unit.Id}' is duplicated."));
                }

                if (string.IsNullOrWhiteSpace(unit.Name))
                {
                    errors.Add(Error(
                        MachineProjectLayoutValidationErrorCode.UnitNameRequired,
                        null,
                        null,
                        $"Unit '{unit.Id}' requires a name."));
                }
            }
        }

        return unitIds;
    }

    private static MachineProjectLayoutValidationError Error(
        MachineProjectLayoutValidationErrorCode code,
        string? layoutId,
        string? componentId,
        string message) =>
        new(code, layoutId, componentId, message);
}
