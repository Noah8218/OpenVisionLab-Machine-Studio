using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SequenceAuthoringTargetCatalogSnapshot(
    IReadOnlyList<SequenceAuthoringTarget> AuthoringTargets,
    IReadOnlyList<SequenceExpectedStateTarget> ExpectedStateTargets);

/// <summary>
/// Builds the project-backed target views used by the Sequence editor and
/// compiler. It contains no WPF state or editor selection state.
/// </summary>
internal sealed class SequenceAuthoringTargetCatalog
{
    internal SequenceAuthoringTargetCatalogSnapshot Build(MachineProjectDocument project) =>
        new(BuildAuthoringTargets(project), BuildExpectedStateTargets(project));

    internal IReadOnlyList<SequenceAuthoringTarget> GetTargetsForSequence(
        IReadOnlyList<SequenceAuthoringTarget> targets,
        SequenceDefinition? sequence) =>
        targets
            .Where(target => target.Kind != SequenceAuthoringTargetKind.Subsequence
                || sequence is null
                || !string.Equals(target.Id, sequence.Id, StringComparison.Ordinal))
            .ToArray();

    internal SequenceCompilationTargets BuildCompilationTargets(MachineProjectDocument project)
    {
        var channelKinds = project.Channels
            .GroupBy(channel => channel.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Kind, StringComparer.Ordinal);
        return new SequenceCompilationTargets(
            channelKinds,
            project.Axes.Select(axis => axis.Id),
            project.Devices.Where(device => device.Kind == DeviceKind.Camera).Select(device => device.Id),
            project.Sequences.Select(sequence => sequence.Id));
    }

    private static IReadOnlyList<SequenceAuthoringTarget> BuildAuthoringTargets(
        MachineProjectDocument project)
    {
        var targets = new List<SequenceAuthoringTarget>();
        targets.AddRange(project.Channels
            .Where(channel => channel.Kind is ChannelKind.DigitalInput or ChannelKind.DigitalOutput)
            .Select(channel => new SequenceAuthoringTarget(
                channel.Id,
                TargetDisplayName(channel.Name, channel.Id),
                channel.Kind == ChannelKind.DigitalInput
                    ? SequenceAuthoringTargetKind.DigitalInput
                    : SequenceAuthoringTargetKind.DigitalOutput)));
        targets.AddRange(project.Axes.Select(axis => new SequenceAuthoringTarget(
            axis.Id,
            TargetDisplayName(axis.Name, axis.Id),
            SequenceAuthoringTargetKind.Axis,
            axis.HomePosition.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        targets.AddRange(project.Devices
            .Where(device => device.Kind == DeviceKind.Camera)
            .Select(device => new SequenceAuthoringTarget(
                device.Id,
                TargetDisplayName(device.Name, device.Id),
                SequenceAuthoringTargetKind.Camera)));
        targets.AddRange(project.Sequences.Select(sequence => new SequenceAuthoringTarget(
            sequence.Id,
            SequenceTargetDisplayName(sequence.Name, sequence.Id),
            SequenceAuthoringTargetKind.Subsequence)));
        return targets;
    }

    private static IReadOnlyList<SequenceExpectedStateTarget> BuildExpectedStateTargets(
        MachineProjectDocument project)
    {
        var targets = project.Axes
            .Select(axis => new SequenceExpectedStateTarget(
                axis.Id,
                TargetDisplayName(axis.Name, axis.Id),
                Enum.GetNames<AxisState>()))
            .ToList();
        MachineLayoutDefinition? layout = project.Simulation.ActiveLayoutId is { Length: > 0 } activeLayoutId
            ? project.Layouts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, activeLayoutId, StringComparison.Ordinal))
            : project.Layouts.Count == 1
                ? project.Layouts[0]
                : null;
        if (layout is null)
        {
            return targets;
        }

        foreach (LayoutComponentDefinition component in layout.Components)
        {
            IReadOnlyList<string>? states = component.Kind switch
            {
                LayoutComponentKind.PneumaticCylinder => Enum.GetNames<PneumaticCylinderState>(),
                LayoutComponentKind.Conveyor => ["Stopped", "ForwardRunning", "ReverseRunning"],
                LayoutComponentKind.DigitalSensor => ["Clear", "Detected"],
                LayoutComponentKind.Workpiece => Enum.GetNames<WorkpieceInspectionState>(),
                _ => null
            };
            if (states is not null)
            {
                targets.Add(new SequenceExpectedStateTarget(
                    component.Id,
                    TargetDisplayName(component.Name, component.Id),
                    states));
            }
        }

        return targets;
    }

    private static string TargetDisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : $"{name} · {id}";

    private static string SequenceTargetDisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : name;
}
