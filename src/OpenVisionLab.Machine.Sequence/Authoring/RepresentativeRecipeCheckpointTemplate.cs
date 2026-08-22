using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

public enum RepresentativeCheckpointRole
{
    CylinderExtended,
    SensorDetected,
    AxisIdle,
    CylinderRetracted,
    ConveyorStopped
}

public enum RepresentativeCheckpointTemplateStatus
{
    Proposed,
    AlreadyConfigured,
    Unavailable
}

public enum RepresentativeCheckpointUnavailableReason
{
    None,
    EquipmentOrBoundaryNotFound,
    StepAlreadyHasCheckpoint
}

public sealed record RepresentativeCheckpointTemplateEntry(
    RepresentativeCheckpointRole Role,
    RepresentativeCheckpointTemplateStatus Status,
    RepresentativeCheckpointUnavailableReason UnavailableReason,
    string? StepId,
    string? StepName,
    string? ExpectedTargetId,
    string? ExpectedState);

public sealed record RepresentativeRecipeCheckpointTemplatePreview(
    string SequenceId,
    IReadOnlyList<RepresentativeCheckpointTemplateEntry> Entries)
{
    public int ProposedCount => Entries.Count(entry =>
        entry.Status == RepresentativeCheckpointTemplateStatus.Proposed);
    public int ExistingCount => Entries.Count(entry =>
        entry.Status == RepresentativeCheckpointTemplateStatus.AlreadyConfigured);
    public int UnavailableCount => Entries.Count(entry =>
        entry.Status == RepresentativeCheckpointTemplateStatus.Unavailable);
}

public sealed class RepresentativeRecipeCheckpointTemplate
{
    public RepresentativeRecipeCheckpointTemplatePreview Preview(
        MachineProjectDocument project,
        string sequenceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sequence = project.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sequenceId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Sequence '{sequenceId}' was not found.", nameof(sequenceId));
        var layout = ResolveActiveLayout(project);
        var reservedStepIds = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<RepresentativeCheckpointTemplateEntry>(5);

        Add(entries, reservedStepIds, RepresentativeCheckpointRole.CylinderExtended,
            FindCylinder(project, layout, sequence, extended: true));
        Add(entries, reservedStepIds, RepresentativeCheckpointRole.SensorDetected,
            FindSensor(project, layout, sequence));
        Add(entries, reservedStepIds, RepresentativeCheckpointRole.AxisIdle,
            FindAxis(project, sequence));
        Add(entries, reservedStepIds, RepresentativeCheckpointRole.CylinderRetracted,
            FindCylinder(project, layout, sequence, extended: false));
        Add(entries, reservedStepIds, RepresentativeCheckpointRole.ConveyorStopped,
            FindConveyor(project, layout, sequence, reservedStepIds));

        return new RepresentativeRecipeCheckpointTemplatePreview(sequence.Id, entries);
    }

    public int Apply(
        MachineProjectDocument project,
        RepresentativeRecipeCheckpointTemplatePreview preview)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preview);
        var sequence = project.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, preview.SequenceId, StringComparison.Ordinal));
        if (sequence is null)
        {
            return 0;
        }

        var applied = 0;
        foreach (var entry in preview.Entries.Where(candidate =>
                     candidate.Status == RepresentativeCheckpointTemplateStatus.Proposed))
        {
            var step = sequence.Steps.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entry.StepId, StringComparison.Ordinal));
            if (step is null
                || HasCheckpoint(step)
                || string.IsNullOrWhiteSpace(entry.ExpectedTargetId)
                || string.IsNullOrWhiteSpace(entry.ExpectedState))
            {
                continue;
            }

            step.ExpectedTargetId = entry.ExpectedTargetId;
            step.ExpectedState = entry.ExpectedState;
            applied++;
        }

        return applied;
    }

    private static void Add(
        ICollection<RepresentativeCheckpointTemplateEntry> entries,
        ISet<string> reservedStepIds,
        RepresentativeCheckpointRole role,
        Candidate? candidate)
    {
        if (candidate is null)
        {
            entries.Add(new RepresentativeCheckpointTemplateEntry(
                role,
                RepresentativeCheckpointTemplateStatus.Unavailable,
                RepresentativeCheckpointUnavailableReason.EquipmentOrBoundaryNotFound,
                null,
                null,
                null,
                null));
            return;
        }

        var step = candidate.Step;
        if (string.Equals(step.ExpectedTargetId, candidate.TargetId, StringComparison.Ordinal)
            && string.Equals(step.ExpectedState, candidate.State, StringComparison.OrdinalIgnoreCase))
        {
            reservedStepIds.Add(step.Id);
            entries.Add(ToEntry(candidate, RepresentativeCheckpointTemplateStatus.AlreadyConfigured));
            return;
        }

        if (HasCheckpoint(step) || !reservedStepIds.Add(step.Id))
        {
            entries.Add(ToEntry(
                candidate,
                RepresentativeCheckpointTemplateStatus.Unavailable,
                RepresentativeCheckpointUnavailableReason.StepAlreadyHasCheckpoint));
            return;
        }

        entries.Add(ToEntry(candidate, RepresentativeCheckpointTemplateStatus.Proposed));
    }

    private static RepresentativeCheckpointTemplateEntry ToEntry(
        Candidate candidate,
        RepresentativeCheckpointTemplateStatus status,
        RepresentativeCheckpointUnavailableReason reason = RepresentativeCheckpointUnavailableReason.None) =>
        new(
            candidate.Role,
            status,
            reason,
            candidate.Step.Id,
            candidate.Step.Name,
            candidate.TargetId,
            candidate.State);

    private static Candidate? FindCylinder(
        MachineProjectDocument project,
        MachineLayoutDefinition? layout,
        SequenceDefinition sequence,
        bool extended)
    {
        if (layout is null)
        {
            return null;
        }

        var role = extended
            ? RepresentativeCheckpointRole.CylinderExtended
            : RepresentativeCheckpointRole.CylinderRetracted;
        var state = extended ? "Extended" : "Retracted";
        foreach (var component in Components(layout, LayoutComponentKind.PneumaticCylinder))
        {
            var device = project.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
            var feedbackId = extended
                ? device?.Cylinder?.ExtendedSensorChannelId
                : device?.Cylinder?.RetractedSensorChannelId;
            var existing = FindExisting(sequence, component.Id, state);
            var boundary = existing ?? sequence.Steps.FirstOrDefault(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, feedbackId, StringComparison.Ordinal)
                && IsBoolean(step.Parameter, expected: true));
            if (boundary is not null)
            {
                return new Candidate(role, boundary, component.Id, state);
            }
        }

        return null;
    }

    private static Candidate? FindSensor(
        MachineProjectDocument project,
        MachineLayoutDefinition? layout,
        SequenceDefinition sequence)
    {
        if (layout is null)
        {
            return null;
        }

        foreach (var component in Components(layout, LayoutComponentKind.DigitalSensor))
        {
            var device = project.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
            var existing = FindExisting(sequence, component.Id, "Detected");
            var boundary = existing ?? sequence.Steps.FirstOrDefault(step =>
                step.Action == SequenceStepAction.WaitSignal
                && string.Equals(step.TargetId, device?.Sensor?.OutputChannelId, StringComparison.Ordinal)
                && IsBoolean(step.Parameter, expected: true));
            if (boundary is not null)
            {
                return new Candidate(
                    RepresentativeCheckpointRole.SensorDetected,
                    boundary,
                    component.Id,
                    "Detected");
            }
        }

        return null;
    }

    private static Candidate? FindAxis(
        MachineProjectDocument project,
        SequenceDefinition sequence)
    {
        foreach (var axis in project.Axes)
        {
            var existing = FindExisting(sequence, axis.Id, "Idle");
            var boundary = existing ?? sequence.Steps.FirstOrDefault(step =>
                step.Action == SequenceStepAction.WaitAxisDone
                && string.Equals(step.TargetId, axis.Id, StringComparison.Ordinal));
            if (boundary is not null)
            {
                return new Candidate(
                    RepresentativeCheckpointRole.AxisIdle,
                    boundary,
                    axis.Id,
                    "Idle");
            }
        }

        return null;
    }

    private static Candidate? FindConveyor(
        MachineProjectDocument project,
        MachineLayoutDefinition? layout,
        SequenceDefinition sequence,
        ISet<string> reservedStepIds)
    {
        if (layout is null)
        {
            return null;
        }

        foreach (var component in Components(layout, LayoutComponentKind.Conveyor))
        {
            var existing = FindExisting(sequence, component.Id, "Stopped");
            if (existing is not null)
            {
                return new Candidate(
                    RepresentativeCheckpointRole.ConveyorStopped,
                    existing,
                    component.Id,
                    "Stopped");
            }

            var device = project.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
            var stopIndex = sequence.Steps.FindLastIndex(step =>
                step.Action == SequenceStepAction.SetSignal
                && string.Equals(step.TargetId, device?.Conveyor?.RunCommandChannelId, StringComparison.Ordinal)
                && IsBoolean(step.Parameter, expected: false));
            if (stopIndex < 0)
            {
                continue;
            }

            var boundary = sequence.Steps
                .Skip(stopIndex + 1)
                .Reverse()
                .FirstOrDefault(step =>
                    step.Action != SequenceStepAction.Complete
                    && !reservedStepIds.Contains(step.Id))
                ?? sequence.Steps.Skip(stopIndex + 1).LastOrDefault(step =>
                    !reservedStepIds.Contains(step.Id));
            if (boundary is not null)
            {
                return new Candidate(
                    RepresentativeCheckpointRole.ConveyorStopped,
                    boundary,
                    component.Id,
                    "Stopped");
            }
        }

        return null;
    }

    private static IEnumerable<LayoutComponentDefinition> Components(
        MachineLayoutDefinition layout,
        LayoutComponentKind kind) =>
        layout.Components
            .Where(component => component.Kind == kind)
            .OrderBy(component => component.ZIndex)
            .ThenBy(component => component.Id, StringComparer.Ordinal);

    private static SequenceStepDefinition? FindExisting(
        SequenceDefinition sequence,
        string targetId,
        string state) =>
        sequence.Steps.FirstOrDefault(step =>
            string.Equals(step.ExpectedTargetId, targetId, StringComparison.Ordinal)
            && string.Equals(step.ExpectedState, state, StringComparison.OrdinalIgnoreCase));

    private static bool HasCheckpoint(SequenceStepDefinition step) =>
        !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
        || !string.IsNullOrWhiteSpace(step.ExpectedState);

    private static bool IsBoolean(string? value, bool expected) =>
        bool.TryParse(value?.Trim(), out var parsed)
            ? parsed == expected
            : string.Equals(value?.Trim(), expected ? "1" : "0", StringComparison.Ordinal);

    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project) =>
        project.Simulation.ActiveLayoutId is { Length: > 0 } activeLayoutId
            ? project.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, activeLayoutId, StringComparison.Ordinal))
            : project.Layouts.Count == 1
                ? project.Layouts[0]
                : null;

    private sealed record Candidate(
        RepresentativeCheckpointRole Role,
        SequenceStepDefinition Step,
        string TargetId,
        string State);
}
