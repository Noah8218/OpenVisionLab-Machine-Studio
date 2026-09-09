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
    private readonly SemiconductorProcessBlockPlanBuilder _planBuilder;
    private readonly SemiconductorProcessBlockTimeoutAdjuster _timeoutAdjuster = new();

    public SemiconductorProcessBlockComposer()
    {
        _planBuilder = new SemiconductorProcessBlockPlanBuilder(_store, _station);
    }

    public SemiconductorProcessBlockPreview Preview(
        MachineProjectDocument project,
        SemiconductorProcessBlockKind kind)
        => _planBuilder.Preview(project, kind);

    public SemiconductorProcessBlockPlanPreview Preview(
        MachineProjectDocument project,
        IEnumerable<SemiconductorProcessBlockKind> kinds)
        => _planBuilder.Preview(project, kinds);

    public IReadOnlyList<SemiconductorProcessBlockKind> RecognizeExistingKinds(
        MachineProjectDocument project)
        => _planBuilder.RecognizeExistingKinds(project);

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

        var sequence = _planBuilder.ResolveAutomaticSequence(updated)
            ?? throw new InvalidOperationException("The station template did not provide an automatic Sequence.");
        var expected = _planBuilder.BuildSteps(updated, kind);
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
        var sequence = _planBuilder.ResolveAutomaticSequence(updated)
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
        => _timeoutAdjuster.Preview(project, stepIds, proposedTimeoutMs);

    public SemiconductorManagedTimeoutAdjustmentApplyResult ApplyTimeoutAdjustment(
        MachineProjectDocument project,
        SemiconductorManagedTimeoutAdjustmentPreview preview)
        => _timeoutAdjuster.Apply(project, preview);

    public static bool CanAdjustTimeout(SequenceStepAction action) =>
        SemiconductorProcessBlockTimeoutAdjuster.CanAdjustTimeout(action);

    private static int InsertMissingSteps(
        SequenceDefinition sequence,
        SemiconductorProcessBlockKind kind,
        IReadOnlyList<SequenceStepDefinition> expected)
    {
        bool sourceIsLinear = SequenceDefinitionEditor.IsStrictLinear(sequence);
        if (!SemiconductorProcessBlockPlanBuilder.SupportsManagedSuffix(sequence))
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

    private static void RebuildManagedTransitions(SequenceDefinition sequence)
    {
        SequenceStepDefinition terminal = sequence.Steps[^1];
        SequenceStepDefinition[] load = sequence.Steps
            .Where(step => step.Id.StartsWith(
                SemiconductorProcessBlockPlanBuilder.ProcessBlockPrefix(SemiconductorProcessBlockKind.Load),
                StringComparison.Ordinal))
            .ToArray();
        SequenceStepDefinition[] suffix = sequence.Steps
            .Where(step => step.Id.StartsWith("process-block.", StringComparison.Ordinal)
                && !step.Id.StartsWith(
                    SemiconductorProcessBlockPlanBuilder.ProcessBlockPrefix(SemiconductorProcessBlockKind.Load),
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
            var prefix = SemiconductorProcessBlockPlanBuilder.ProcessBlockPrefix(laterKind);
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
