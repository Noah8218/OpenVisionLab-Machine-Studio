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
    WorkpieceTypeRequired,
    WorkpieceConveyorComponentRequired,
    WorkpieceConveyorComponentNotFound,
    WorkpieceCarrierMustBeConveyor,
    WorkpieceInspectionStateInvalid,
    AmbiguousBehaviorBinding
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

                ValidateComponentDefinition(layout, component, componentIds, errors);
            }
        }

        errors.AddRange(new MachineProjectLayoutBehaviorBindingValidator().Validate(project).Errors);
        return new MachineProjectLayoutValidationResult(errors);
    }

    private static void ValidateComponentDefinition(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ISet<string> componentIds,
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
    }

    private static MachineProjectLayoutValidationError Error(
        MachineProjectLayoutValidationErrorCode code,
        string? layoutId,
        string? componentId,
        string message) =>
        new(code, layoutId, componentId, message);
}
