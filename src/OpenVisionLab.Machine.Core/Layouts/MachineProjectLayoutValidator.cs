using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
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
        var allComponents = new List<(MachineLayoutDefinition Layout, LayoutComponentDefinition Component)>();

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

                allComponents.Add((layout, component));
                ValidateComponentDefinition(layout, component, componentIds, errors);
            }
        }

        ValidateBehaviorBindings(project, allComponents, errors);
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

    private static void ValidateBehaviorBindings(
        MachineProjectDocument project,
        IEnumerable<(MachineLayoutDefinition Layout, LayoutComponentDefinition Component)> components,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        var axesById = (project.Axes ?? new List<Axes.VirtualAxisDefinition>())
            .ToLookup(axis => axis.Id ?? string.Empty, StringComparer.Ordinal);
        var devicesById = (project.Devices ?? new List<DeviceDefinition>())
            .ToLookup(device => device.Id ?? string.Empty, StringComparer.Ordinal);
        var channelsById = (project.Channels ?? new List<ChannelDefinition>())
            .ToLookup(channel => channel.Id ?? string.Empty, StringComparer.Ordinal);
        var componentsById = components.ToLookup(
            item => item.Component.Id ?? string.Empty,
            StringComparer.Ordinal);

        foreach (var (layout, component) in components)
        {
            if (!Enum.IsDefined(component.Kind))
            {
                continue;
            }

            switch (component.Kind)
            {
                case LayoutComponentKind.MachineFrame:
                    if (!string.IsNullOrWhiteSpace(component.BehaviorBindingId))
                    {
                        errors.Add(Error(
                            MachineProjectLayoutValidationErrorCode.UnsupportedBehaviorBinding,
                            layout.Id,
                            component.Id,
                            "MachineFrame does not support a behavior binding."));
                    }
                    break;

                case LayoutComponentKind.LinearStage:
                    ValidateStageBinding(
                        layout,
                        component,
                        axesById,
                        Axes.AxisKind.Linear,
                        MachineProjectLayoutValidationErrorCode.AxisBindingMustBeLinear,
                        errors);
                    break;

                case LayoutComponentKind.RotaryStage:
                    ValidateStageBinding(
                        layout,
                        component,
                        axesById,
                        Axes.AxisKind.Rotary,
                        MachineProjectLayoutValidationErrorCode.AxisBindingMustBeRotary,
                        errors);
                    break;

                case LayoutComponentKind.DigitalSensor:
                    ValidateDigitalSensorBinding(
                        layout,
                        component,
                        devicesById,
                        channelsById,
                        componentsById,
                        errors);
                    break;

                case LayoutComponentKind.PneumaticCylinder:
                    ValidatePneumaticCylinderBinding(
                        layout,
                        component,
                        devicesById,
                        channelsById,
                        errors);
                    break;

                case LayoutComponentKind.Conveyor:
                    ValidateConveyorBinding(
                        layout,
                        component,
                        devicesById,
                        channelsById,
                        errors);
                    break;

                case LayoutComponentKind.Workpiece:
                    ValidateWorkpieceBinding(
                        layout,
                        component,
                        devicesById,
                        componentsById,
                        errors);
                    break;
            }
        }
    }

    private static void ValidateStageBinding(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ILookup<string, Axes.VirtualAxisDefinition> axesById,
        Axes.AxisKind expectedAxisKind,
        MachineProjectLayoutValidationErrorCode kindMismatchCode,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        string stageKind = component.Kind.ToString();
        if (string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding,
                layout.Id,
                component.Id,
                $"{stageKind} requires an axis behavior binding."));
            return;
        }

        var matchingAxes = axesById[component.BehaviorBindingId].Take(2).ToArray();
        if (matchingAxes.Length == 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AxisBindingNotFound,
                layout.Id,
                component.Id,
                $"{stageKind} axis '{component.BehaviorBindingId}' was not found."));
        }
        else if (matchingAxes.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"{stageKind} axis binding '{component.BehaviorBindingId}' is ambiguous."));
        }
        else if (matchingAxes[0].Kind != expectedAxisKind)
        {
            errors.Add(Error(
                kindMismatchCode,
                layout.Id,
                component.Id,
                $"{stageKind} binding '{component.BehaviorBindingId}' must identify a {expectedAxisKind} axis."));
        }
    }

    private static void ValidateDigitalSensorBinding(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ILookup<string, DeviceDefinition> devicesById,
        ILookup<string, ChannelDefinition> channelsById,
        ILookup<string, (MachineLayoutDefinition Layout, LayoutComponentDefinition Component)> componentsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding,
                layout.Id,
                component.Id,
                "DigitalSensor requires a sensor-device behavior binding."));
            return;
        }

        var matchingDevices = devicesById[component.BehaviorBindingId].Take(2).ToArray();
        if (matchingDevices.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"DigitalSensor device binding '{component.BehaviorBindingId}' is ambiguous."));
            return;
        }

        var device = matchingDevices.SingleOrDefault();
        if (device is not { Kind: DeviceKind.Sensor, Sensor: not null })
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.SensorDeviceBindingInvalid,
                layout.Id,
                component.Id,
                $"DigitalSensor binding '{component.BehaviorBindingId}' must identify a Sensor device with sensor settings."));
            return;
        }

        ValidateSensorDefinition(
            layout,
            component,
            device.Sensor,
            channelsById,
            componentsById,
            errors);
    }

    private static void ValidateSensorDefinition(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        DigitalSensorDefinition sensor,
        ILookup<string, ChannelDefinition> channelsById,
        ILookup<string, (MachineLayoutDefinition Layout, LayoutComponentDefinition Component)> componentsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(sensor.OutputChannelId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.SensorOutputChannelRequired,
                layout.Id,
                component.Id,
                "Digital sensor output channel id is required."));
        }
        else
        {
            var matchingChannels = channelsById[sensor.OutputChannelId].Take(2).ToArray();
            if (matchingChannels.Length == 0)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.SensorOutputChannelNotFound,
                    layout.Id,
                    component.Id,
                    $"Digital sensor output channel '{sensor.OutputChannelId}' was not found."));
            }
            else if (matchingChannels.Length > 1)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                    layout.Id,
                    component.Id,
                    $"Digital sensor output channel '{sensor.OutputChannelId}' is ambiguous."));
            }
            else if (matchingChannels[0].Kind != ChannelKind.DigitalInput)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.SensorOutputChannelMustBeDigitalInput,
                    layout.Id,
                    component.Id,
                    $"Digital sensor output channel '{sensor.OutputChannelId}' must be a DigitalInput."));
            }
        }

        if (string.IsNullOrWhiteSpace(sensor.TargetComponentId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.SensorTargetComponentRequired,
                layout.Id,
                component.Id,
                "Digital sensor target component id is required."));
        }
        else
        {
            var matchingComponents = componentsById[sensor.TargetComponentId].Take(2).ToArray();
            if (matchingComponents.Length == 0)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.SensorTargetComponentNotFound,
                    layout.Id,
                    component.Id,
                    $"Digital sensor target component '{sensor.TargetComponentId}' was not found."));
            }
            else if (matchingComponents.Length > 1)
            {
                errors.Add(Error(
                    MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                    layout.Id,
                    component.Id,
                    $"Digital sensor target component '{sensor.TargetComponentId}' is ambiguous."));
            }
        }

        if (sensor.OnDelayMilliseconds < 0 || sensor.OffDelayMilliseconds < 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.SensorDelayInvalid,
                layout.Id,
                component.Id,
                "Digital sensor on/off delays cannot be negative."));
        }
    }

    private static void ValidatePneumaticCylinderBinding(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ILookup<string, DeviceDefinition> devicesById,
        ILookup<string, ChannelDefinition> channelsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding,
                layout.Id,
                component.Id,
                "PneumaticCylinder requires a cylinder-device behavior binding."));
            return;
        }

        var matchingDevices = devicesById[component.BehaviorBindingId].Take(2).ToArray();
        if (matchingDevices.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"PneumaticCylinder device binding '{component.BehaviorBindingId}' is ambiguous."));
            return;
        }

        var device = matchingDevices.SingleOrDefault();
        if (device is not { Kind: DeviceKind.Cylinder, Cylinder: not null })
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderDeviceBindingInvalid,
                layout.Id,
                component.Id,
                $"PneumaticCylinder binding '{component.BehaviorBindingId}' must identify a Cylinder device with cylinder settings."));
            return;
        }

        var cylinder = device.Cylinder;
        ValidateCylinderChannel(
            layout,
            component,
            cylinder.ExtendCommandChannelId,
            ChannelKind.DigitalOutput,
            "extend command",
            channelsById,
            errors);
        ValidateCylinderChannel(
            layout,
            component,
            cylinder.ExtendedSensorChannelId,
            ChannelKind.DigitalInput,
            "extended sensor",
            channelsById,
            errors);
        ValidateCylinderChannel(
            layout,
            component,
            cylinder.RetractedSensorChannelId,
            ChannelKind.DigitalInput,
            "retracted sensor",
            channelsById,
            errors);

        var channelIds = new[]
        {
            cylinder.ExtendCommandChannelId,
            cylinder.ExtendedSensorChannelId,
            cylinder.RetractedSensorChannelId
        };
        if (channelIds.All(id => !string.IsNullOrWhiteSpace(id))
            && channelIds.Distinct(StringComparer.Ordinal).Count() != channelIds.Length)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderChannelIdsMustBeDistinct,
                layout.Id,
                component.Id,
                "Cylinder command, extended sensor, and retracted sensor channels must be distinct."));
        }

        if (cylinder.ExtendDurationMilliseconds <= 0 || cylinder.RetractDurationMilliseconds <= 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderDurationInvalid,
                layout.Id,
                component.Id,
                "Cylinder extend and retract durations must be positive."));
        }

        if (cylinder.ExtendedSensorDelayMilliseconds < 0
            || cylinder.RetractedSensorDelayMilliseconds < 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderSensorDelayInvalid,
                layout.Id,
                component.Id,
                "Cylinder end-position sensor delays cannot be negative."));
        }

        if (!double.IsFinite(cylinder.Stroke) || cylinder.Stroke <= 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderStrokeInvalid,
                layout.Id,
                component.Id,
                "Cylinder stroke must be finite and positive."));
        }
    }

    private static void ValidateCylinderChannel(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        string channelId,
        ChannelKind expectedKind,
        string role,
        ILookup<string, ChannelDefinition> channelsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderChannelIdRequired,
                layout.Id,
                component.Id,
                $"Cylinder {role} channel id is required."));
            return;
        }

        var matchingChannels = channelsById[channelId].Take(2).ToArray();
        if (matchingChannels.Length == 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.CylinderChannelNotFound,
                layout.Id,
                component.Id,
                $"Cylinder {role} channel '{channelId}' was not found."));
        }
        else if (matchingChannels.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"Cylinder {role} channel '{channelId}' is ambiguous."));
        }
        else if (matchingChannels[0].Kind != expectedKind)
        {
            errors.Add(Error(
                expectedKind == ChannelKind.DigitalOutput
                    ? MachineProjectLayoutValidationErrorCode.CylinderCommandMustBeDigitalOutput
                    : MachineProjectLayoutValidationErrorCode.CylinderFeedbackMustBeDigitalInput,
                layout.Id,
                component.Id,
                $"Cylinder {role} channel '{channelId}' must be a {expectedKind}."));
        }
    }

    private static void ValidateConveyorBinding(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ILookup<string, DeviceDefinition> devicesById,
        ILookup<string, ChannelDefinition> channelsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding,
                layout.Id,
                component.Id,
                "Conveyor requires a conveyor-device behavior binding."));
            return;
        }

        var matchingDevices = devicesById[component.BehaviorBindingId].Take(2).ToArray();
        if (matchingDevices.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"Conveyor device binding '{component.BehaviorBindingId}' is ambiguous."));
            return;
        }

        var device = matchingDevices.SingleOrDefault();
        if (device is not { Kind: DeviceKind.Conveyor, Conveyor: not null })
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ConveyorDeviceBindingInvalid,
                layout.Id,
                component.Id,
                $"Conveyor binding '{component.BehaviorBindingId}' must identify a Conveyor device with conveyor settings."));
            return;
        }

        var conveyor = device.Conveyor;
        ValidateConveyorChannel(
            layout,
            component,
            conveyor.RunCommandChannelId,
            "run command",
            channelsById,
            errors);
        ValidateConveyorChannel(
            layout,
            component,
            conveyor.ReverseCommandChannelId,
            "reverse command",
            channelsById,
            errors);

        if (!string.IsNullOrWhiteSpace(conveyor.RunCommandChannelId)
            && !string.IsNullOrWhiteSpace(conveyor.ReverseCommandChannelId)
            && string.Equals(
                conveyor.RunCommandChannelId,
                conveyor.ReverseCommandChannelId,
                StringComparison.Ordinal))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ConveyorChannelIdsMustBeDistinct,
                layout.Id,
                component.Id,
                "Conveyor run and reverse command channels must be distinct."));
        }

        if (!double.IsFinite(conveyor.SpeedUnitsPerSecond) || conveyor.SpeedUnitsPerSecond <= 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ConveyorSpeedInvalid,
                layout.Id,
                component.Id,
                "Conveyor speed must be finite and positive."));
        }
    }

    private static void ValidateConveyorChannel(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        string channelId,
        string role,
        ILookup<string, ChannelDefinition> channelsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ConveyorChannelIdRequired,
                layout.Id,
                component.Id,
                $"Conveyor {role} channel id is required."));
            return;
        }

        var matchingChannels = channelsById[channelId].Take(2).ToArray();
        if (matchingChannels.Length == 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ConveyorChannelNotFound,
                layout.Id,
                component.Id,
                $"Conveyor {role} channel '{channelId}' was not found."));
        }
        else if (matchingChannels.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"Conveyor {role} channel '{channelId}' is ambiguous."));
        }
        else if (matchingChannels[0].Kind != ChannelKind.DigitalOutput)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.ConveyorCommandMustBeDigitalOutput,
                layout.Id,
                component.Id,
                $"Conveyor {role} channel '{channelId}' must be a DigitalOutput."));
        }
    }

    private static void ValidateWorkpieceBinding(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        ILookup<string, DeviceDefinition> devicesById,
        ILookup<string, (MachineLayoutDefinition Layout, LayoutComponentDefinition Component)> componentsById,
        ICollection<MachineProjectLayoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding,
                layout.Id,
                component.Id,
                "Workpiece requires a workpiece-device behavior binding."));
            return;
        }

        var matchingDevices = devicesById[component.BehaviorBindingId].Take(2).ToArray();
        if (matchingDevices.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"Workpiece device binding '{component.BehaviorBindingId}' is ambiguous."));
            return;
        }

        var device = matchingDevices.SingleOrDefault();
        if (device is not { Kind: DeviceKind.Workpiece, Workpiece: not null })
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.WorkpieceDeviceBindingInvalid,
                layout.Id,
                component.Id,
                $"Workpiece binding '{component.BehaviorBindingId}' must identify a Workpiece device with workpiece settings."));
            return;
        }

        var workpiece = device.Workpiece;
        if (string.IsNullOrWhiteSpace(workpiece.Type))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.WorkpieceTypeRequired,
                layout.Id,
                component.Id,
                "Workpiece type is required."));
        }

        if (!Enum.IsDefined(workpiece.InspectionState))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.WorkpieceInspectionStateInvalid,
                layout.Id,
                component.Id,
                $"Workpiece inspection state '{workpiece.InspectionState}' is unsupported."));
        }

        if (string.IsNullOrWhiteSpace(workpiece.ConveyorComponentId))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentRequired,
                layout.Id,
                component.Id,
                "Workpiece conveyor component id is required."));
            return;
        }

        var matchingComponents = componentsById[workpiece.ConveyorComponentId].Take(2).ToArray();
        if (matchingComponents.Length == 0)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentNotFound,
                layout.Id,
                component.Id,
                $"Workpiece conveyor component '{workpiece.ConveyorComponentId}' was not found."));
        }
        else if (matchingComponents.Length > 1)
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding,
                layout.Id,
                component.Id,
                $"Workpiece conveyor component '{workpiece.ConveyorComponentId}' is ambiguous."));
        }
        else if (matchingComponents[0].Component.Kind != LayoutComponentKind.Conveyor
                 || !string.Equals(matchingComponents[0].Layout.Id, layout.Id, StringComparison.Ordinal))
        {
            errors.Add(Error(
                MachineProjectLayoutValidationErrorCode.WorkpieceCarrierMustBeConveyor,
                layout.Id,
                component.Id,
                $"Workpiece carrier '{workpiece.ConveyorComponentId}' must identify a Conveyor in the same layout."));
        }
    }

    private static MachineProjectLayoutValidationError Error(
        MachineProjectLayoutValidationErrorCode code,
        string? layoutId,
        string? componentId,
        string message) =>
        new(code, layoutId, componentId, message);
}
