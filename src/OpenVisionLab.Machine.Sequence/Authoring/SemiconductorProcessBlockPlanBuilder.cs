using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

/// <summary>
/// Builds read-only Process Block plans and recognizes authored managed blocks.
/// It never mutates the caller project; mutation and commit stay in
/// <see cref="SemiconductorProcessBlockComposer"/>.
/// </summary>
internal sealed class SemiconductorProcessBlockPlanBuilder
{
    private readonly ProjectDocumentStore _store;
    private readonly SemiconductorStationSkeletonTemplate _station;

    internal SemiconductorProcessBlockPlanBuilder(
        ProjectDocumentStore store,
        SemiconductorStationSkeletonTemplate station)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _station = station ?? throw new ArgumentNullException(nameof(station));
    }

    public SemiconductorProcessBlockPreview Preview(
        MachineProjectDocument project,
        SemiconductorProcessBlockKind kind)
    {
        ArgumentNullException.ThrowIfNull(project);
        var stationPreview = _station.Preview(project);
        if (stationPreview.UnavailableCount > 0)
        {
            return new SemiconductorProcessBlockPreview(kind, stationPreview, []);
        }

        var resolved = Clone(project);
        _station.Apply(resolved);
        var expected = BuildSteps(resolved, kind);
        var sourceSequence = ResolveAutomaticSequence(project);
        var sourceSupportsComposition = sourceSequence is null || SupportsManagedSuffix(sourceSequence);
        var entries = expected.Select(step =>
        {
            var existing = sourceSequence?.Steps.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, step.Id, StringComparison.Ordinal));
            var status = ResolveStatus(existing, step, sourceSupportsComposition, isSelected: true);
            return new SemiconductorProcessBlockStepEntry(
                step.Id,
                step.Name,
                step.Action,
                step.TargetId,
                step.Parameter ?? string.Empty,
                step.TimeoutMs,
                status);
        }).ToArray();
        return new SemiconductorProcessBlockPreview(kind, stationPreview, entries);
    }

    public SemiconductorProcessBlockPlanPreview Preview(
        MachineProjectDocument project,
        IEnumerable<SemiconductorProcessBlockKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(kinds);
        var selected = Normalize(kinds);
        var selectedSet = selected.ToHashSet();
        var existingKinds = RecognizeExistingKinds(project);
        var stationPreview = _station.Preview(project);
        if (stationPreview.UnavailableCount > 0)
        {
            return new SemiconductorProcessBlockPlanPreview(
                selected,
                existingKinds,
                stationPreview,
                []);
        }

        var resolved = Clone(project);
        _station.Apply(resolved);
        var sourceSequence = ResolveAutomaticSequence(project);
        var sourceSupportsComposition = sourceSequence is null || SupportsManagedSuffix(sourceSequence);
        var entries = new List<SemiconductorProcessBlockStepEntry>();
        foreach (var kind in Enum.GetValues<SemiconductorProcessBlockKind>())
        {
            foreach (var step in BuildSteps(resolved, kind))
            {
                var existing = sourceSequence?.Steps.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, step.Id, StringComparison.Ordinal));
                if (!selectedSet.Contains(kind) && existing is null)
                {
                    continue;
                }

                var status = ResolveStatus(existing, step, sourceSupportsComposition, selectedSet.Contains(kind));
                entries.Add(new SemiconductorProcessBlockStepEntry(
                    step.Id,
                    step.Name,
                    step.Action,
                    step.TargetId,
                    step.Parameter ?? string.Empty,
                    step.TimeoutMs,
                    status));
            }
        }

        return new SemiconductorProcessBlockPlanPreview(
            selected,
            existingKinds,
            stationPreview,
            entries);
    }

    public IReadOnlyList<SemiconductorProcessBlockKind> RecognizeExistingKinds(
        MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sequence = ResolveAutomaticSequence(project);
        return sequence is null
            ? []
            : Enum.GetValues<SemiconductorProcessBlockKind>()
                .Where(kind =>
                    (kind == SemiconductorProcessBlockKind.Load && HasAuthoredOhtLoad(project, sequence))
                    || (kind == SemiconductorProcessBlockKind.Align && HasAuthoredPrealignerAlignment(project, sequence))
                    || (kind == SemiconductorProcessBlockKind.Inspect && HasAuthoredInspectionHandoff(project, sequence))
                    || sequence.Steps.Any(step => step.Id.StartsWith(
                        ProcessBlockPrefix(kind),
                        StringComparison.Ordinal)))
                .ToArray();
    }

    private static SemiconductorProcessBlockKind[] Normalize(
        IEnumerable<SemiconductorProcessBlockKind> kinds)
    {
        var selected = kinds.ToHashSet();
        return Enum.GetValues<SemiconductorProcessBlockKind>()
            .Where(selected.Contains)
            .ToArray();
    }

    internal static string ProcessBlockPrefix(SemiconductorProcessBlockKind kind) =>
        $"process-block.{kind.ToString().ToLowerInvariant()}.";

    internal IReadOnlyList<SequenceStepDefinition> BuildSteps(
        MachineProjectDocument project,
        SemiconductorProcessBlockKind kind)
    {
        if (kind == SemiconductorProcessBlockKind.Load
            && ResolveAutomaticSequence(project) is { } sequence
            && HasAuthoredOhtLoad(project, sequence))
        {
            return [];
        }

        if (kind == SemiconductorProcessBlockKind.Inspect
            && ResolveAutomaticSequence(project) is { } inspectionSequence
            && HasAuthoredInspectionHandoff(project, inspectionSequence))
        {
            return [];
        }

        if (kind == SemiconductorProcessBlockKind.Align
            && ResolveAutomaticSequence(project) is { } alignmentSequence
            && HasAuthoredPrealignerAlignment(project, alignmentSequence))
        {
            return [];
        }

        var layout = project.Layouts.First(layout => string.Equals(
            layout.Id,
            project.Simulation.ActiveLayoutId,
            StringComparison.Ordinal));
        var conveyor = project.Devices.First(device => device is { Kind: DeviceKind.Conveyor, Conveyor: not null });
        var loadLock = project.Devices.FirstOrDefault(device =>
            device is { Kind: DeviceKind.LoadLock, LoadLock: not null });
        var loadLockOuterDoor = loadLock is null
            ? null
            : layout.Components.FirstOrDefault(component => string.Equals(
                component.Id,
                loadLock.LoadLock!.OuterDoorComponentId,
                StringComparison.Ordinal));
        var cylinder = loadLockOuterDoor is null
            ? project.Devices.First(device => device is { Kind: DeviceKind.Cylinder, Cylinder: not null })
            : project.Devices.Single(device =>
                device is { Kind: DeviceKind.Cylinder, Cylinder: not null }
                && string.Equals(device.Id, loadLockOuterDoor.BehaviorBindingId, StringComparison.Ordinal));
        var axis = project.Axes.First(axis => axis.Kind == AxisKind.Linear);
        var sensors = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.DigitalSensor)
            .OrderBy(component => component.Transform.X)
            .Select(component => project.Devices.First(device =>
                string.Equals(device.Id, component.BehaviorBindingId, StringComparison.Ordinal)))
            .ToArray();
        var entrySensor = sensors.FirstOrDefault(sensor => string.Equals(
                sensor.Id,
                "device.sensor-entry",
                StringComparison.Ordinal))
            ?? sensors[0];
        var processSensor = sensors.FirstOrDefault(sensor => string.Equals(
                sensor.Id,
                "device.sensor-process",
                StringComparison.Ordinal))
            ?? sensors[1];
        var run = conveyor.Conveyor!.RunCommandChannelId;
        var entry = entrySensor.Sensor!.OutputChannelId;
        var process = processSensor.Sensor!.OutputChannelId;
        var extend = cylinder.Cylinder!.ExtendCommandChannelId;
        var extended = cylinder.Cylinder.ExtendedSensorChannelId;
        var retracted = cylinder.Cylinder.RetractedSensorChannelId;
        var moveTarget = ResolveMoveTarget(axis);

        return kind switch
        {
            SemiconductorProcessBlockKind.Load =>
            [
                Step("process-block.load.start", "Load · Start transport", SequenceStepAction.SetSignal, run, "true"),
                Step("process-block.load.wait-entry", "Load · Wait entry sensor", SequenceStepAction.WaitSignal, entry, "true", 5000),
                Step("process-block.load.stop", "Load · Stop transport", SequenceStepAction.SetSignal, run, "false")
            ],
            SemiconductorProcessBlockKind.Align =>
            [
                Step("process-block.align.move", "Align · Move process axis", SequenceStepAction.MoveAxis, axis.Id, moveTarget),
                Step("process-block.align.wait", "Align · Wait process axis", SequenceStepAction.WaitAxisDone, axis.Id, string.Empty, 5000)
            ],
            SemiconductorProcessBlockKind.Process =>
            [
                Step("process-block.process.extend", "Process · Extend cylinder", SequenceStepAction.SetSignal, extend, "true"),
                Step("process-block.process.wait-extended", "Process · Wait cylinder extended", SequenceStepAction.WaitSignal, extended, "true", 3000),
                Step("process-block.process.retract", "Process · Retract cylinder", SequenceStepAction.SetSignal, extend, "false"),
                Step("process-block.process.wait-retracted", "Process · Wait cylinder retracted", SequenceStepAction.WaitSignal, retracted, "true", 3000)
            ],
            SemiconductorProcessBlockKind.Inspect =>
            [
                Step("process-block.inspect.confirm-position", "Inspect · Confirm process position", SequenceStepAction.WaitSignal, process, "true", 3000)
            ],
            SemiconductorProcessBlockKind.Unload =>
            [
                Step("process-block.unload.start", "Unload · Start transport", SequenceStepAction.SetSignal, run, "true"),
                Step("process-block.unload.wait-clear", "Unload · Wait process position clear", SequenceStepAction.WaitSignal, process, "false", 5000),
                Step("process-block.unload.stop", "Unload · Stop transport", SequenceStepAction.SetSignal, run, "false")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    internal static bool SupportsManagedSuffix(SequenceDefinition sequence)
    {
        if (SequenceDefinitionEditor.IsStrictLinear(sequence))
        {
            return true;
        }

        if (sequence.Steps.Count == 0
            || sequence.Steps[^1].Action is not (SequenceStepAction.Complete or SequenceStepAction.None)
            || sequence.Steps.Take(sequence.Steps.Count - 1).Any(step =>
                step.Action is SequenceStepAction.Complete or SequenceStepAction.None))
        {
            return false;
        }

        string terminalId = sequence.Steps[^1].Id;
        return sequence.Steps.Take(sequence.Steps.Count - 1).Any(step =>
            string.Equals(step.NextStepId, terminalId, StringComparison.Ordinal));
    }

    private static SemiconductorProcessBlockStepStatus ResolveStatus(
        SequenceStepDefinition? existing,
        SequenceStepDefinition expected,
        bool sourceSupportsComposition,
        bool isSelected)
    {
        if (!sourceSupportsComposition || (existing is not null && !HasCompatibleManagedRole(existing, expected)))
        {
            return SemiconductorProcessBlockStepStatus.Unavailable;
        }

        if (!isSelected)
        {
            return SemiconductorProcessBlockStepStatus.ProposedRemoval;
        }

        if (existing is null)
        {
            return SemiconductorProcessBlockStepStatus.Proposed;
        }

        return HasTemplateSettings(existing, expected)
            ? SemiconductorProcessBlockStepStatus.Existing
            : SemiconductorProcessBlockStepStatus.Customized;
    }

    private static bool HasTemplateSettings(SequenceStepDefinition existing, SequenceStepDefinition expected) =>
        string.Equals(existing.TargetId, expected.TargetId, StringComparison.Ordinal)
        && string.Equals(existing.Parameter ?? string.Empty, expected.Parameter ?? string.Empty, StringComparison.Ordinal)
        && existing.TimeoutMs == expected.TimeoutMs;

    private static bool HasCompatibleManagedRole(
        SequenceStepDefinition existing,
        SequenceStepDefinition expected) =>
        existing.Action == expected.Action
        && string.Equals(existing.TargetId, expected.TargetId, StringComparison.Ordinal)
        && (existing.Action == SequenceStepAction.MoveAxis
            || string.Equals(existing.Parameter ?? string.Empty, expected.Parameter ?? string.Empty, StringComparison.Ordinal));

    internal SequenceDefinition? ResolveAutomaticSequence(MachineProjectDocument project) =>
        project.Simulation.AutomaticRun is not { } automatic
            ? null
            : project.Sequences.FirstOrDefault(sequence => string.Equals(
                sequence.Id,
                automatic.SequenceId,
                StringComparison.Ordinal));

    private static bool HasAuthoredOhtLoad(
        MachineProjectDocument project,
        SequenceDefinition sequence)
    {
        OhtHandoffDefinition? handoff = project.Devices.FirstOrDefault(device =>
            device is { Kind: DeviceKind.Oht, OhtHandoff: not null })?.OhtHandoff;
        return handoff is not null
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, handoff.HandoffReadyFeedbackChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, handoff.CarrierTransferredFeedbackChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAuthoredInspectionHandoff(
        MachineProjectDocument project,
        SequenceDefinition sequence)
    {
        InspectionHandoffDefinition? handoff = project.Devices.FirstOrDefault(device =>
            device is { Kind: DeviceKind.Inspection, InspectionHandoff: not null })?.InspectionHandoff;
        return handoff is not null
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, handoff.InspectionReadyFeedbackChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.TriggerCamera
                && string.Equals(step.TargetId, handoff.CameraId, StringComparison.Ordinal))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitVisionResult
                && string.Equals(step.TargetId, handoff.CameraId, StringComparison.Ordinal))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.SetSignal
                && string.Equals(step.TargetId, handoff.ResultAcceptedCommandChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, handoff.InspectionCompleteFeedbackChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAuthoredPrealignerAlignment(
        MachineProjectDocument project,
        SequenceDefinition sequence)
    {
        PrealignerDefinition? prealigner = project.Devices.FirstOrDefault(device =>
            device is { Kind: DeviceKind.Prealigner, Prealigner: not null })?.Prealigner;
        return prealigner is not null
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, prealigner.AlignmentReadyFeedbackChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.SetSignal
                && string.Equals(step.TargetId, prealigner.AlignmentAcceptedCommandChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase))
            && sequence.Steps.Any(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, prealigner.AlignmentCompleteFeedbackChannelId, StringComparison.Ordinal)
                && string.Equals(step.Parameter, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static SequenceStepDefinition Step(
        string id,
        string name,
        SequenceStepAction action,
        string targetId,
        string parameter,
        int timeoutMs = 0) => new()
    {
        Id = id,
        Name = name,
        Action = action,
        TargetId = targetId,
        Parameter = parameter,
        TimeoutMs = timeoutMs
    };

    private static string ResolveMoveTarget(VirtualAxisDefinition axis)
    {
        var target = axis.SoftLimitMax is { } maximum && maximum > axis.HomePosition
            ? axis.HomePosition + ((maximum - axis.HomePosition) / 2d)
            : axis.HomePosition;
        return target.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private MachineProjectDocument Clone(MachineProjectDocument project) =>
        _store.Load(_store.Serialize(project));
}
