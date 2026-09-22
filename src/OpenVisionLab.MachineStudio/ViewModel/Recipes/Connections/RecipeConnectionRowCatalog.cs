using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Builds the read-only Recipe Connection row projection from authored project data.
/// </summary>
internal sealed class RecipeConnectionRowCatalog
{
    internal IReadOnlyList<RecipeConnectionValidationIssue> BuildValidationIssues(
        MachineProjectDocument project,
        MachineProjectLayoutValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(validation);

        return validation.Errors
            .Select(error => CreateValidationIssue(project, error))
            .ToArray();
    }

    internal IReadOnlyList<RecipeConnectionRowViewModel> BuildRows(
        MachineProjectDocument project,
        MachineProjectLayoutValidationResult validation,
        bool canEditSequenceStructure)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(validation);

        var layout = ResolveActiveLayout(project);
        if (layout is null)
        {
            return Array.Empty<RecipeConnectionRowViewModel>();
        }

        var componentNames = layout.Components.ToDictionary(
            component => component.Id,
            component => DisplayName(component.Name, component.Id),
            StringComparer.Ordinal);
        return layout.Components
            .OrderBy(component => component.ZIndex)
            .ThenBy(component => component.Id, StringComparer.Ordinal)
            .Select(component => CreateRow(
                project,
                component,
                componentNames,
                validation,
                canEditSequenceStructure))
            .ToArray();
    }

    private static RecipeConnectionRowViewModel CreateRow(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        IReadOnlyDictionary<string, string> componentNames,
        MachineProjectLayoutValidationResult validation,
        bool canEditSequenceStructure)
    {
        var device = project.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
        var targetIds = new HashSet<string>(StringComparer.Ordinal) { component.Id };
        if (!string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            targetIds.Add(component.BehaviorBindingId);
        }

        var behaviorText = OpenVisionLanguageService.T("Connections.None");
        var connectionText = OpenVisionLanguageService.T("Connections.NotApplicable");
        string? sequenceTargetId = null;
        var isConnected = false;

        switch (component.Kind)
        {
            case LayoutComponentKind.LinearStage:
            case LayoutComponentKind.RotaryStage:
            {
                var axis = project.Axes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
                behaviorText = axis is null
                    ? OpenVisionLanguageService.T("Connections.MissingAxis")
                    : DisplayName(axis.Name, axis.Id);
                connectionText = Format("Connections.StageLinkFormat", component.Id, axis?.Id ?? "—");
                if (axis is not null)
                {
                    targetIds.Add(axis.Id);
                    sequenceTargetId = axis.Id;
                    isConnected = true;
                }
                break;
            }
            case LayoutComponentKind.DigitalSensor:
            {
                var sensor = device?.Sensor;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.SensorLinkFormat",
                    sensor?.OutputChannelId ?? "—",
                    ResolveName(componentNames, sensor?.TargetComponentId));
                AddTarget(targetIds, sensor?.OutputChannelId);
                AddTarget(targetIds, sensor?.TargetComponentId);
                sequenceTargetId = sensor?.OutputChannelId;
                isConnected = sensor is not null;
                break;
            }
            case LayoutComponentKind.PneumaticCylinder:
            {
                var cylinder = device?.Cylinder;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.CylinderLinkFormat",
                    cylinder?.ExtendCommandChannelId ?? "—",
                    cylinder?.ExtendedSensorChannelId ?? "—",
                    cylinder?.RetractedSensorChannelId ?? "—");
                AddTarget(targetIds, cylinder?.ExtendCommandChannelId);
                AddTarget(targetIds, cylinder?.ExtendedSensorChannelId);
                AddTarget(targetIds, cylinder?.RetractedSensorChannelId);
                sequenceTargetId = cylinder?.ExtendCommandChannelId;
                isConnected = cylinder is not null;
                break;
            }
            case LayoutComponentKind.Conveyor:
            {
                var conveyor = device?.Conveyor;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.ConveyorLinkFormat",
                    conveyor?.RunCommandChannelId ?? "—",
                    conveyor?.ReverseCommandChannelId ?? "—");
                AddTarget(targetIds, conveyor?.RunCommandChannelId);
                AddTarget(targetIds, conveyor?.ReverseCommandChannelId);
                sequenceTargetId = conveyor?.RunCommandChannelId;
                isConnected = conveyor is not null;
                break;
            }
            case LayoutComponentKind.Workpiece:
            {
                var workpiece = device?.Workpiece;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.WorkpieceLinkFormat",
                    ResolveName(componentNames, workpiece?.ConveyorComponentId));
                AddTarget(targetIds, workpiece?.ConveyorComponentId);
                isConnected = workpiece is not null;
                break;
            }
            case LayoutComponentKind.MachineFrame:
                behaviorText = OpenVisionLanguageService.T("Connections.StaticComponent");
                break;
        }

        var sequenceUses = project.Sequences
            .SelectMany(sequence => sequence.Steps.Select(step => (Sequence: sequence, Step: step)))
            .Where(item => targetIds.Contains(item.Step.TargetId))
            .ToArray();
        var sequenceText = sequenceUses.Length == 0
            ? OpenVisionLanguageService.T("Connections.NoSequenceUse")
            : string.Join(", ", sequenceUses.Take(3).Select(item => item.Step.Name)) +
              (sequenceUses.Length > 3 ? $" (+{sequenceUses.Length - 3})" : string.Empty);
        var errors = validation.Errors
            .Where(error => string.Equals(error.ComponentId, component.Id, StringComparison.Ordinal))
            .ToArray();
        var validationIssues = errors
            .Select(error => CreateValidationIssue(project, error))
            .ToArray();

        return new RecipeConnectionRowViewModel
        {
            ComponentId = component.Id,
            Name = component.Name,
            Kind = component.Kind,
            KindText = OpenVisionLanguageService.T(
                $"Properties.Value.{component.Kind}",
                component.Kind.ToString(),
                component.Kind.ToString()),
            BehaviorText = behaviorText,
            ConnectionText = connectionText,
            SequenceText = sequenceText,
            SequenceUseCount = sequenceUses.Length,
            FirstSequenceId = sequenceUses.FirstOrDefault().Sequence?.Id,
            FirstSequenceStepId = sequenceUses.FirstOrDefault().Step?.Id,
            FirstSequenceAction = sequenceUses.FirstOrDefault().Step?.Action,
            SequenceTargetId = sequenceTargetId,
            CanAddSequenceStep = sequenceUses.Length == 0
                && sequenceTargetId is not null
                && canEditSequenceStructure,
            RelatedTargetIds = targetIds,
            IsConnected = isConnected && errors.Length == 0,
            IsValid = errors.Length == 0,
            ValidationText = errors.Length == 0
                ? OpenVisionLanguageService.T("Connections.Valid")
                : errors[0].Message,
            ValidationIssues = validationIssues
        };
    }

    private static RecipeConnectionValidationIssue CreateValidationIssue(
        MachineProjectDocument project,
        MachineProjectLayoutValidationError error)
    {
        var component = project.Layouts
            .Where(layout => error.LayoutId is null
                || string.Equals(layout.Id, error.LayoutId, StringComparison.Ordinal))
            .SelectMany(layout => layout.Components ?? [])
            .FirstOrDefault(candidate => error.ComponentId is not null
                && string.Equals(candidate.Id, error.ComponentId, StringComparison.Ordinal));
        var issue = new RecipeConnectionValidationIssue(
            error.Code,
            error.LayoutId,
            error.ComponentId,
            ResolveTargetId(project, component, error),
            ResolveValidationProperty(error),
            error.Message);
        var sequenceUse = ResolveSequenceUse(project, component, issue.TargetId);
        return sequenceUse is null
            ? issue
            : issue with
            {
                SequenceId = sequenceUse.Value.SequenceId,
                StepId = sequenceUse.Value.StepId
            };
    }

    private static (string SequenceId, string StepId)? ResolveSequenceUse(
        MachineProjectDocument project,
        LayoutComponentDefinition? component,
        string? targetId)
    {
        if (component is null)
        {
            return null;
        }

        var targetIds = new HashSet<string>(StringComparer.Ordinal) { component.Id };
        AddTarget(targetIds, component.BehaviorBindingId);
        AddTarget(targetIds, targetId);
        AddComponentSequenceTargets(project, component, targetIds);
        var sequenceUse = project.Sequences
            .SelectMany(sequence => sequence.Steps.Select(step => (Sequence: sequence, Step: step)))
            .FirstOrDefault(item => targetIds.Contains(item.Step.TargetId));
        return sequenceUse.Sequence is null
            ? null
            : (sequenceUse.Sequence.Id, sequenceUse.Step.Id);
    }

    private static void AddComponentSequenceTargets(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        ISet<string> targetIds)
    {
        var device = project.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
        switch (component.Kind)
        {
            case LayoutComponentKind.LinearStage:
            case LayoutComponentKind.RotaryStage:
                AddTarget(targetIds, component.BehaviorBindingId);
                break;
            case LayoutComponentKind.DigitalSensor:
                AddTarget(targetIds, device?.Sensor?.OutputChannelId);
                AddTarget(targetIds, device?.Sensor?.TargetComponentId);
                break;
            case LayoutComponentKind.PneumaticCylinder:
                AddTarget(targetIds, device?.Cylinder?.ExtendCommandChannelId);
                AddTarget(targetIds, device?.Cylinder?.ExtendedSensorChannelId);
                AddTarget(targetIds, device?.Cylinder?.RetractedSensorChannelId);
                break;
            case LayoutComponentKind.Conveyor:
                AddTarget(targetIds, device?.Conveyor?.RunCommandChannelId);
                AddTarget(targetIds, device?.Conveyor?.ReverseCommandChannelId);
                break;
            case LayoutComponentKind.Workpiece:
                AddTarget(targetIds, device?.Workpiece?.ConveyorComponentId);
                break;
        }
    }

    private static string? ResolveTargetId(
        MachineProjectDocument project,
        LayoutComponentDefinition? component,
        MachineProjectLayoutValidationError error)
    {
        if (component is null)
        {
            return null;
        }

        var device = project.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
        return error.Code switch
        {
            MachineProjectLayoutValidationErrorCode.SensorOutputChannelRequired
                or MachineProjectLayoutValidationErrorCode.SensorOutputChannelNotFound
                or MachineProjectLayoutValidationErrorCode.SensorOutputChannelMustBeDigitalInput
                => device?.Sensor?.OutputChannelId ?? component.BehaviorBindingId,
            MachineProjectLayoutValidationErrorCode.SensorTargetComponentRequired
                or MachineProjectLayoutValidationErrorCode.SensorTargetComponentNotFound
                or MachineProjectLayoutValidationErrorCode.SensorTargetComponentMustBeInSameLayout
                => device?.Sensor?.TargetComponentId ?? component.BehaviorBindingId,
            MachineProjectLayoutValidationErrorCode.CylinderChannelIdRequired
                or MachineProjectLayoutValidationErrorCode.CylinderChannelNotFound
                or MachineProjectLayoutValidationErrorCode.CylinderCommandMustBeDigitalOutput
                or MachineProjectLayoutValidationErrorCode.CylinderFeedbackMustBeDigitalInput
                => ResolveCylinderTarget(device?.Cylinder, error.Message) ?? component.BehaviorBindingId,
            MachineProjectLayoutValidationErrorCode.ConveyorChannelIdRequired
                or MachineProjectLayoutValidationErrorCode.ConveyorChannelNotFound
                or MachineProjectLayoutValidationErrorCode.ConveyorCommandMustBeDigitalOutput
                => ResolveConveyorTarget(device?.Conveyor, error.Message) ?? component.BehaviorBindingId,
            MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentRequired
                or MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentNotFound
                or MachineProjectLayoutValidationErrorCode.WorkpieceCarrierMustBeConveyor
                => device?.Workpiece?.ConveyorComponentId ?? component.BehaviorBindingId,
            _ => string.IsNullOrWhiteSpace(component.BehaviorBindingId)
                ? component.Id
                : component.BehaviorBindingId
        };
    }

    private static string ResolveValidationProperty(MachineProjectLayoutValidationError error)
    {
        if (error.Code is MachineProjectLayoutValidationErrorCode.CylinderChannelIdRequired
            or MachineProjectLayoutValidationErrorCode.CylinderChannelNotFound
            or MachineProjectLayoutValidationErrorCode.CylinderCommandMustBeDigitalOutput
            or MachineProjectLayoutValidationErrorCode.CylinderFeedbackMustBeDigitalInput)
        {
            return ResolveCylinderProperty(error.Message);
        }

        if (error.Code is MachineProjectLayoutValidationErrorCode.ConveyorChannelIdRequired
            or MachineProjectLayoutValidationErrorCode.ConveyorChannelNotFound
            or MachineProjectLayoutValidationErrorCode.ConveyorCommandMustBeDigitalOutput)
        {
            return error.Message.Contains("reverse", StringComparison.OrdinalIgnoreCase)
                ? "Device.Conveyor.ReverseCommandChannelId"
                : "Device.Conveyor.RunCommandChannelId";
        }

        return ResolveValidationProperty(error.Code);
    }

    private static string ResolveValidationProperty(MachineProjectLayoutValidationErrorCode code) => code switch
    {
        MachineProjectLayoutValidationErrorCode.LayoutIdRequired
            or MachineProjectLayoutValidationErrorCode.DuplicateLayoutId => "Layout.Id",
        MachineProjectLayoutValidationErrorCode.LayoutNameRequired => "Layout.Name",
        MachineProjectLayoutValidationErrorCode.InvalidGridSize => "Layout.GridSize",
        MachineProjectLayoutValidationErrorCode.ComponentsRequired => "Layout.Components",
        MachineProjectLayoutValidationErrorCode.ComponentIdRequired
            or MachineProjectLayoutValidationErrorCode.DuplicateComponentId => "Component.Id",
        MachineProjectLayoutValidationErrorCode.ComponentNameRequired => "Component.Name",
        MachineProjectLayoutValidationErrorCode.UnsupportedComponentKind => "Component.Kind",
        MachineProjectLayoutValidationErrorCode.InvalidTransform => "Component.Transform",
        MachineProjectLayoutValidationErrorCode.InvalidSize => "Component.Size",
        MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding
            or MachineProjectLayoutValidationErrorCode.UnsupportedBehaviorBinding
            or MachineProjectLayoutValidationErrorCode.AmbiguousBehaviorBinding
            or MachineProjectLayoutValidationErrorCode.AxisBindingNotFound
            or MachineProjectLayoutValidationErrorCode.AxisBindingMustBeLinear
            or MachineProjectLayoutValidationErrorCode.AxisBindingMustBeRotary
            or MachineProjectLayoutValidationErrorCode.SensorDeviceBindingInvalid
            or MachineProjectLayoutValidationErrorCode.CylinderDeviceBindingInvalid
            or MachineProjectLayoutValidationErrorCode.ConveyorDeviceBindingInvalid
            or MachineProjectLayoutValidationErrorCode.WorkpieceDeviceBindingInvalid => "Component.BehaviorBindingId",
        MachineProjectLayoutValidationErrorCode.SensorOutputChannelRequired
            or MachineProjectLayoutValidationErrorCode.SensorOutputChannelNotFound
            or MachineProjectLayoutValidationErrorCode.SensorOutputChannelMustBeDigitalInput => "Device.Sensor.OutputChannelId",
        MachineProjectLayoutValidationErrorCode.SensorTargetComponentRequired
            or MachineProjectLayoutValidationErrorCode.SensorTargetComponentNotFound
            or MachineProjectLayoutValidationErrorCode.SensorTargetComponentMustBeInSameLayout => "Device.Sensor.TargetComponentId",
        MachineProjectLayoutValidationErrorCode.SensorDelayInvalid => "Device.Sensor.OnDelayMilliseconds",
        MachineProjectLayoutValidationErrorCode.CylinderChannelIdRequired
            or MachineProjectLayoutValidationErrorCode.CylinderChannelNotFound
            or MachineProjectLayoutValidationErrorCode.CylinderCommandMustBeDigitalOutput => "Device.Cylinder.ExtendCommandChannelId",
        MachineProjectLayoutValidationErrorCode.CylinderFeedbackMustBeDigitalInput => "Device.Cylinder.ExtendedSensorChannelId",
        MachineProjectLayoutValidationErrorCode.CylinderChannelIdsMustBeDistinct => "Device.Cylinder.*ChannelId",
        MachineProjectLayoutValidationErrorCode.CylinderDurationInvalid => "Device.Cylinder.*DurationMilliseconds",
        MachineProjectLayoutValidationErrorCode.CylinderSensorDelayInvalid => "Device.Cylinder.*SensorDelayMilliseconds",
        MachineProjectLayoutValidationErrorCode.CylinderStrokeInvalid => "Device.Cylinder.Stroke",
        MachineProjectLayoutValidationErrorCode.ConveyorChannelIdRequired
            or MachineProjectLayoutValidationErrorCode.ConveyorChannelNotFound
            or MachineProjectLayoutValidationErrorCode.ConveyorCommandMustBeDigitalOutput => "Device.Conveyor.RunCommandChannelId",
        MachineProjectLayoutValidationErrorCode.ConveyorChannelIdsMustBeDistinct => "Device.Conveyor.*CommandChannelId",
        MachineProjectLayoutValidationErrorCode.ConveyorSpeedInvalid => "Device.Conveyor.SpeedUnitsPerSecond",
        MachineProjectLayoutValidationErrorCode.WorkpieceTypeRequired => "Device.Workpiece.Type",
        MachineProjectLayoutValidationErrorCode.WorkpieceInspectionStateInvalid => "Device.Workpiece.InspectionState",
        MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentRequired
            or MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentNotFound
            or MachineProjectLayoutValidationErrorCode.WorkpieceCarrierMustBeConveyor => "Device.Workpiece.ConveyorComponentId",
        _ => "Component"
    };

    private static string ResolveCylinderProperty(string message)
    {
        if (message.Contains("retracted", StringComparison.OrdinalIgnoreCase))
        {
            return "Device.Cylinder.RetractedSensorChannelId";
        }

        if (message.Contains("extended", StringComparison.OrdinalIgnoreCase))
        {
            return "Device.Cylinder.ExtendedSensorChannelId";
        }

        return "Device.Cylinder.ExtendCommandChannelId";
    }

    private static string? ResolveCylinderTarget(PneumaticCylinderDefinition? cylinder, string message)
    {
        if (cylinder is null)
        {
            return null;
        }

        if (message.Contains("retracted", StringComparison.OrdinalIgnoreCase))
        {
            return cylinder.RetractedSensorChannelId;
        }

        if (message.Contains("extended", StringComparison.OrdinalIgnoreCase))
        {
            return cylinder.ExtendedSensorChannelId;
        }

        return cylinder.ExtendCommandChannelId;
    }

    private static string? ResolveConveyorTarget(ConveyorDefinition? conveyor, string message)
    {
        if (conveyor is null)
        {
            return null;
        }

        return message.Contains("reverse", StringComparison.OrdinalIgnoreCase)
            ? conveyor.ReverseCommandChannelId
            : conveyor.RunCommandChannelId;
    }

    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project)
    {
        if (!string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId))
        {
            return project.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, project.Simulation.ActiveLayoutId, StringComparison.Ordinal));
        }

        return project.Layouts.Count == 1 ? project.Layouts[0] : null;
    }

    private static string ResolveName(IReadOnlyDictionary<string, string> names, string? id) =>
        !string.IsNullOrWhiteSpace(id) && names.TryGetValue(id, out var name) ? name : id ?? "—";

    private static void AddTarget(ISet<string> targets, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            targets.Add(id);
        }
    }

    private static string DisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}";

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
}
