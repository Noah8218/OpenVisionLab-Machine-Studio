using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

public enum SemiconductorProcessBlockKind
{
    Load,
    Align,
    Process,
    Inspect,
    Unload
}

public enum SemiconductorProcessBlockStepStatus
{
    Proposed,
    Existing,
    Customized,
    ProposedRemoval,
    Unavailable
}

public sealed record SemiconductorProcessBlockStepEntry(
    string StepId,
    string Name,
    SequenceStepAction Action,
    string TargetId,
    string Parameter,
    int TimeoutMs,
    SemiconductorProcessBlockStepStatus Status);

public sealed record SemiconductorProcessBlockPreview(
    SemiconductorProcessBlockKind Kind,
    SemiconductorStationSkeletonPreview Station,
    IReadOnlyList<SemiconductorProcessBlockStepEntry> Steps)
{
    public int ProposedConnectionCount => Station.ProposedCount;
    public int ExistingConnectionCount => Station.ExistingCount;
    public int ProposedStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Proposed);
    public int ExistingStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Existing);
    public int CustomizedStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Customized);
    public int UnavailableCount => Station.UnavailableCount
        + Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Unavailable);
    public bool CanApply => UnavailableCount == 0 && (ProposedConnectionCount > 0 || ProposedStepCount > 0);
}

public sealed record SemiconductorProcessBlockApplyResult(
    SemiconductorProcessBlockPreview Preview,
    int AddedConnectionCount,
    int AddedStepCount,
    bool Changed);

public sealed record SemiconductorProcessBlockPlanPreview(
    IReadOnlyList<SemiconductorProcessBlockKind> Kinds,
    IReadOnlyList<SemiconductorProcessBlockKind> ExistingKinds,
    SemiconductorStationSkeletonPreview Station,
    IReadOnlyList<SemiconductorProcessBlockStepEntry> Steps)
{
    public int ProposedConnectionCount => Kinds.Count > 0 ? Station.ProposedCount : 0;
    public int ProposedStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Proposed);
    public int ExistingStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Existing);
    public int CustomizedStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Customized);
    public int RemovedStepCount => Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.ProposedRemoval);
    public int UnavailableCount => Station.UnavailableCount
        + Steps.Count(step => step.Status == SemiconductorProcessBlockStepStatus.Unavailable);
    public bool CanApply => UnavailableCount == 0
        && (ProposedConnectionCount > 0 || ProposedStepCount > 0 || RemovedStepCount > 0);
}

public sealed record SemiconductorProcessBlockPlanApplyResult(
    SemiconductorProcessBlockPlanPreview Preview,
    int AddedConnectionCount,
    int AddedStepCount,
    int RemovedStepCount,
    bool Changed);

public sealed record SemiconductorManagedTimeoutAdjustmentEntry(
    string StepId,
    string Name,
    SequenceStepAction Action,
    string TargetId,
    int CurrentTimeoutMs,
    int ProposedTimeoutMs);

public sealed record SemiconductorManagedTimeoutAdjustmentPreview(
    string? SequenceId,
    int ProposedTimeoutMs,
    IReadOnlyList<string> RequestedStepIds,
    IReadOnlyList<SemiconductorManagedTimeoutAdjustmentEntry> Entries,
    IReadOnlyList<string> InvalidStepIds)
{
    public int ChangedCount => Entries.Count(entry => entry.CurrentTimeoutMs != entry.ProposedTimeoutMs);
    public bool CanApply => ProposedTimeoutMs >= 0
        && RequestedStepIds.Count > 0
        && InvalidStepIds.Count == 0
        && Entries.Count == RequestedStepIds.Count
        && ChangedCount > 0;
}

public sealed record SemiconductorManagedTimeoutAdjustmentApplyResult(
    SemiconductorManagedTimeoutAdjustmentPreview Preview,
    int AppliedStepCount,
    bool Changed);

/// <summary>
/// Adds one small, deterministic semiconductor process block to the existing
/// automatic Sequence while reusing the station skeleton for missing links.
/// </summary>
public sealed class SemiconductorProcessBlockComposer
{
    private static readonly SemiconductorProcessBlockKind[] SuffixOrder =
    [
        SemiconductorProcessBlockKind.Align,
        SemiconductorProcessBlockKind.Process,
        SemiconductorProcessBlockKind.Inspect,
        SemiconductorProcessBlockKind.Unload
    ];

    private readonly ProjectDocumentStore _store = new();
    private readonly SemiconductorStationSkeletonTemplate _station = new();

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

    public SemiconductorProcessBlockApplyResult Apply(
        MachineProjectDocument project,
        SemiconductorProcessBlockKind kind)
    {
        ArgumentNullException.ThrowIfNull(project);
        var preview = Preview(project, kind);
        if (!preview.CanApply)
        {
            return new SemiconductorProcessBlockApplyResult(preview, 0, 0, false);
        }

        var updated = Clone(project);
        var stationResult = _station.Apply(updated);
        if (stationResult.Preview.UnavailableCount > 0)
        {
            return new SemiconductorProcessBlockApplyResult(preview, 0, 0, false);
        }

        var sequence = ResolveAutomaticSequence(updated)
            ?? throw new InvalidOperationException("The station template did not provide an automatic Sequence.");
        var expected = BuildSteps(updated, kind);
        var addedSteps = InsertMissingSteps(sequence, kind, expected);
        var changed = stationResult.Changed || addedSteps > 0;
        if (changed)
        {
            Copy(updated, project);
        }
        return new SemiconductorProcessBlockApplyResult(
            preview,
            stationResult.AppliedCount,
            addedSteps,
            changed);
    }

    public SemiconductorProcessBlockPlanApplyResult Apply(
        MachineProjectDocument project,
        IEnumerable<SemiconductorProcessBlockKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(kinds);
        var preview = Preview(project, kinds);
        if (!preview.CanApply)
        {
            return new SemiconductorProcessBlockPlanApplyResult(preview, 0, 0, 0, false);
        }

        var updated = Clone(project);
        var addedConnections = 0;
        var addedSteps = 0;
        var removedSteps = 0;
        var sequence = ResolveAutomaticSequence(updated)
            ?? throw new InvalidOperationException("The managed process plan has no automatic Sequence.");
        bool sourceIsLinear = SequenceDefinitionEditor.IsStrictLinear(sequence);
        foreach (var entry in preview.Steps.Where(step =>
                     step.Status == SemiconductorProcessBlockStepStatus.ProposedRemoval))
        {
            bool removed = sourceIsLinear
                ? new SequenceDefinitionEditor().Delete(sequence, entry.StepId).IsAccepted
                : RemoveManagedStep(sequence, entry.StepId);
            if (!removed)
            {
                return new SemiconductorProcessBlockPlanApplyResult(preview, 0, 0, 0, false);
            }
            removedSteps++;
        }

        var changed = removedSteps > 0;
        foreach (var kind in preview.Kinds)
        {
            var result = Apply(updated, kind);
            addedConnections += result.AddedConnectionCount;
            addedSteps += result.AddedStepCount;
            changed |= result.Changed;
        }
        if (changed)
        {
            Copy(updated, project);
        }
        return new SemiconductorProcessBlockPlanApplyResult(
            preview,
            addedConnections,
            addedSteps,
            removedSteps,
            changed);
    }

    public SemiconductorManagedTimeoutAdjustmentPreview PreviewTimeoutAdjustment(
        MachineProjectDocument project,
        IEnumerable<string> stepIds,
        int proposedTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(stepIds);
        var requested = stepIds
            .Where(stepId => !string.IsNullOrWhiteSpace(stepId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sequence = ResolveAutomaticSequence(project);
        var entries = new List<SemiconductorManagedTimeoutAdjustmentEntry>(requested.Length);
        var invalid = new List<string>();
        foreach (var stepId in requested)
        {
            var step = sequence?.Steps.FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                stepId,
                StringComparison.Ordinal));
            if (step is null
                || !step.Id.StartsWith("process-block.", StringComparison.Ordinal)
                || !CanAdjustTimeout(step.Action))
            {
                invalid.Add(stepId);
                continue;
            }

            entries.Add(new SemiconductorManagedTimeoutAdjustmentEntry(
                step.Id,
                step.Name,
                step.Action,
                step.TargetId,
                step.TimeoutMs,
                proposedTimeoutMs));
        }

        return new SemiconductorManagedTimeoutAdjustmentPreview(
            sequence?.Id,
            proposedTimeoutMs,
            requested,
            entries,
            invalid);
    }

    public SemiconductorManagedTimeoutAdjustmentApplyResult ApplyTimeoutAdjustment(
        MachineProjectDocument project,
        SemiconductorManagedTimeoutAdjustmentPreview preview)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preview);
        var current = PreviewTimeoutAdjustment(project, preview.RequestedStepIds, preview.ProposedTimeoutMs);
        if (!current.CanApply
            || !string.Equals(current.SequenceId, preview.SequenceId, StringComparison.Ordinal)
            || !current.Entries.SequenceEqual(preview.Entries))
        {
            return new SemiconductorManagedTimeoutAdjustmentApplyResult(current, 0, false);
        }

        var updated = Clone(project);
        var sequence = ResolveAutomaticSequence(updated)
            ?? throw new InvalidOperationException("The managed process plan has no automatic Sequence.");
        foreach (var entry in current.Entries)
        {
            sequence.Steps.Single(step => string.Equals(
                step.Id,
                entry.StepId,
                StringComparison.Ordinal)).TimeoutMs = entry.ProposedTimeoutMs;
        }
        Copy(updated, project);
        return new SemiconductorManagedTimeoutAdjustmentApplyResult(
            current,
            current.ChangedCount,
            true);
    }

    public static bool CanAdjustTimeout(SequenceStepAction action) => action is
        SequenceStepAction.Wait or
        SequenceStepAction.WaitAxisDone or
        SequenceStepAction.WaitSignal;

    private static SemiconductorProcessBlockKind[] Normalize(
        IEnumerable<SemiconductorProcessBlockKind> kinds)
    {
        var selected = kinds.ToHashSet();
        return Enum.GetValues<SemiconductorProcessBlockKind>()
            .Where(selected.Contains)
            .ToArray();
    }

    private static string ProcessBlockPrefix(SemiconductorProcessBlockKind kind) =>
        $"process-block.{kind.ToString().ToLowerInvariant()}.";

    private static IReadOnlyList<SequenceStepDefinition> BuildSteps(
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

    private static int InsertMissingSteps(
        SequenceDefinition sequence,
        SemiconductorProcessBlockKind kind,
        IReadOnlyList<SequenceStepDefinition> expected)
    {
        bool sourceIsLinear = SequenceDefinitionEditor.IsStrictLinear(sequence);
        if (!SupportsManagedSuffix(sequence))
        {
            return 0;
        }

        var added = 0;
        for (var expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
        {
            var step = expected[expectedIndex];
            if (sequence.Steps.Any(candidate => string.Equals(candidate.Id, step.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            var previous = expected.Take(expectedIndex).Reverse().FirstOrDefault(candidate =>
                sequence.Steps.Any(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal)));
            var next = expected.Skip(expectedIndex + 1).FirstOrDefault(candidate =>
                sequence.Steps.Any(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal)));
            int insertIndex;
            if (previous is not null)
            {
                insertIndex = sequence.Steps.FindIndex(item => string.Equals(item.Id, previous.Id, StringComparison.Ordinal)) + 1;
            }
            else if (next is not null)
            {
                insertIndex = sequence.Steps.FindIndex(item => string.Equals(item.Id, next.Id, StringComparison.Ordinal));
            }
            else if (kind == SemiconductorProcessBlockKind.Load)
            {
                insertIndex = added;
            }
            else
            {
                insertIndex = FindSuffixInsertionIndex(sequence, kind);
            }
            sequence.Steps.Insert(insertIndex, step);
            added++;
        }

        if (sourceIsLinear)
        {
            RebuildLinearTransitions(sequence.Steps);
        }
        else
        {
            RebuildManagedTransitions(sequence);
        }
        return added;
    }

    private static bool SupportsManagedSuffix(SequenceDefinition sequence)
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

    private static void RebuildManagedTransitions(SequenceDefinition sequence)
    {
        SequenceStepDefinition terminal = sequence.Steps[^1];
        SequenceStepDefinition[] load = sequence.Steps
            .Where(step => step.Id.StartsWith(
                ProcessBlockPrefix(SemiconductorProcessBlockKind.Load),
                StringComparison.Ordinal))
            .ToArray();
        SequenceStepDefinition[] suffix = sequence.Steps
            .Where(step => step.Id.StartsWith("process-block.", StringComparison.Ordinal)
                && !step.Id.StartsWith(
                    ProcessBlockPrefix(SemiconductorProcessBlockKind.Load),
                    StringComparison.Ordinal))
            .ToArray();

        SequenceStepDefinition? authoredStart = sequence.Steps.FirstOrDefault(step =>
            !step.Id.StartsWith("process-block.", StringComparison.Ordinal));
        for (var index = 0; index < load.Length; index++)
        {
            load[index].NextStepId = index + 1 < load.Length
                ? load[index + 1].Id
                : authoredStart?.Id ?? terminal.Id;
        }

        if (suffix.Length == 0)
        {
            return;
        }

        foreach (SequenceStepDefinition step in sequence.Steps.Except(load).Except(suffix))
        {
            if (string.Equals(step.NextStepId, terminal.Id, StringComparison.Ordinal))
            {
                step.NextStepId = suffix[0].Id;
            }
        }

        for (var index = 0; index < suffix.Length; index++)
        {
            suffix[index].NextStepId = index + 1 < suffix.Length
                ? suffix[index + 1].Id
                : terminal.Id;
        }
    }

    private static bool RemoveManagedStep(SequenceDefinition sequence, string stepId)
    {
        SequenceStepDefinition? removed = sequence.Steps.FirstOrDefault(step =>
            string.Equals(step.Id, stepId, StringComparison.Ordinal)
            && step.Id.StartsWith("process-block.", StringComparison.Ordinal));
        if (removed is null || string.IsNullOrWhiteSpace(removed.NextStepId))
        {
            return false;
        }

        foreach (SequenceStepDefinition step in sequence.Steps)
        {
            if (string.Equals(step.NextStepId, removed.Id, StringComparison.Ordinal))
            {
                step.NextStepId = removed.NextStepId;
            }
            if (string.Equals(step.FailureStepId, removed.Id, StringComparison.Ordinal))
            {
                step.FailureStepId = removed.NextStepId;
            }
            if (string.Equals(step.ErrorStepId, removed.Id, StringComparison.Ordinal))
            {
                step.ErrorStepId = removed.NextStepId;
            }
        }

        return sequence.Steps.Remove(removed);
    }

    private static int FindSuffixInsertionIndex(
        SequenceDefinition sequence,
        SemiconductorProcessBlockKind kind)
    {
        var currentOrder = Array.IndexOf(SuffixOrder, kind);
        foreach (var laterKind in SuffixOrder.Skip(currentOrder + 1))
        {
            var prefix = ProcessBlockPrefix(laterKind);
            var index = sequence.Steps.FindIndex(step => step.Id.StartsWith(prefix, StringComparison.Ordinal));
            if (index >= 0)
            {
                return index;
            }
        }
        return sequence.Steps.Count - 1;
    }

    private static void RebuildLinearTransitions(IReadOnlyList<SequenceStepDefinition> steps)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            steps[index].NextStepId = index + 1 < steps.Count ? steps[index + 1].Id : null;
        }
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

    private static SequenceDefinition? ResolveAutomaticSequence(MachineProjectDocument project) =>
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

    private static void Copy(MachineProjectDocument source, MachineProjectDocument target)
    {
        target.Schema = source.Schema;
        target.Simulation = source.Simulation;
        target.Layouts = source.Layouts;
        target.Axes = source.Axes;
        target.MultiAxisCommissioningRecipe = source.MultiAxisCommissioningRecipe;
        target.SemiconductorStationSetup = source.SemiconductorStationSetup;
        target.Devices = source.Devices;
        target.Channels = source.Channels;
        target.Sequences = source.Sequences;
    }
}
