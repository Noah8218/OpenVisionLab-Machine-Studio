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
                : errors[0].Message
        };
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
