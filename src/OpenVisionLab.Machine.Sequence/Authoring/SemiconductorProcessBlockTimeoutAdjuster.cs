using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

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
/// Owns managed Process Block timeout preview and application policy.
/// </summary>
internal sealed class SemiconductorProcessBlockTimeoutAdjuster
{
    private readonly ProjectDocumentStore _store = new();

    internal SemiconductorManagedTimeoutAdjustmentPreview Preview(
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

    internal SemiconductorManagedTimeoutAdjustmentApplyResult Apply(
        MachineProjectDocument project,
        SemiconductorManagedTimeoutAdjustmentPreview preview)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preview);
        var current = Preview(project, preview.RequestedStepIds, preview.ProposedTimeoutMs);
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

    internal static bool CanAdjustTimeout(SequenceStepAction action) => action is
        SequenceStepAction.Wait or
        SequenceStepAction.WaitAxisDone or
        SequenceStepAction.WaitSignal;

    private static SequenceDefinition? ResolveAutomaticSequence(MachineProjectDocument project) =>
        project.Simulation.AutomaticRun is not { } automatic
            ? null
            : project.Sequences.FirstOrDefault(sequence => string.Equals(
                sequence.Id,
                automatic.SequenceId,
                StringComparison.Ordinal));

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
