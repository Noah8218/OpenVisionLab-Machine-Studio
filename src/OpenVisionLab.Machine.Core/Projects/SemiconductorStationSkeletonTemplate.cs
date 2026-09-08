using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Core.Projects;

public enum SemiconductorStationSkeletonRole
{
    StationLayout,
    MachineFrame,
    Transport,
    Workpiece,
    ProcessAxisStage,
    EntrySensor,
    ProcessSensor,
    ProcessCylinder,
    RequiredIo,
    AutomaticSequence
}

public enum SemiconductorStationSkeletonStatus
{
    Proposed,
    Existing,
    Unavailable
}

public enum SemiconductorStationSkeletonUnavailableReason
{
    None,
    ActiveLayoutConflict,
    ExistingRoleInvalid,
    DependencyUnavailable,
    AutomaticSequenceConflict
}

public sealed record SemiconductorStationSkeletonEntry(
    SemiconductorStationSkeletonRole Role,
    SemiconductorStationSkeletonStatus Status,
    string? TargetId,
    int ExistingCount = 0,
    int AddedCount = 0,
    SemiconductorStationSkeletonUnavailableReason UnavailableReason =
        SemiconductorStationSkeletonUnavailableReason.None);

public sealed record SemiconductorStationSkeletonPreview(
    IReadOnlyList<SemiconductorStationSkeletonEntry> Entries)
{
    public int ProposedCount => Entries.Count(entry =>
        entry.Status == SemiconductorStationSkeletonStatus.Proposed);
    public int ExistingCount => Entries.Count(entry =>
        entry.Status == SemiconductorStationSkeletonStatus.Existing);
    public int UnavailableCount => Entries.Count(entry =>
        entry.Status == SemiconductorStationSkeletonStatus.Unavailable);
    public bool CanApply => ProposedCount > 0 && UnavailableCount == 0;
}

public sealed record SemiconductorStationSkeletonApplyResult(
    SemiconductorStationSkeletonPreview Preview,
    int AppliedCount,
    bool Changed = false);

/// <summary>
/// Adds one deterministic semiconductor transfer-station starting point without
/// replacing compatible authored roles that already exist in the active layout.
/// </summary>
public sealed class SemiconductorStationSkeletonTemplate
{
    private readonly ProjectDocumentStore _store = new();
    private readonly SemiconductorStationSkeletonPlanner _planner = new();

    public SemiconductorStationSkeletonPreview Preview(MachineProjectDocument project)
        => Preview(project, ResolveSetup(project));

    public SemiconductorStationSkeletonPreview Preview(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup) => _planner.Preview(project, setup);

    public SemiconductorStationSkeletonApplyResult Apply(MachineProjectDocument project)
        => Apply(project, ResolveSetup(project));

    public SemiconductorStationSkeletonApplyResult Apply(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup)
    {
        var plan = _planner.CreatePlan(project, setup);
        if (plan.Preview.UnavailableCount > 0)
        {
            return new SemiconductorStationSkeletonApplyResult(plan.Preview, 0);
        }

        var updated = plan.PreparedProject;
        updated.Schema = MachineProjectDocument.CurrentSchema;
        updated.SemiconductorStationSetup = setup with { };
        var changed = !string.Equals(
            _store.SerializeForEvidence(project),
            _store.SerializeForEvidence(updated),
            StringComparison.Ordinal);
        if (!changed)
        {
            return new SemiconductorStationSkeletonApplyResult(plan.Preview, 0);
        }

        project.Schema = updated.Schema;
        project.SemiconductorStationSetup = updated.SemiconductorStationSetup;
        project.Simulation.ActiveLayoutId = updated.Simulation.ActiveLayoutId;
        project.Simulation.AutomaticRun = updated.Simulation.AutomaticRun;
        project.Layouts = updated.Layouts;
        project.Axes = updated.Axes;
        project.Devices = updated.Devices;
        project.Channels = updated.Channels;
        project.Sequences = updated.Sequences;
        return new SemiconductorStationSkeletonApplyResult(plan.Preview, plan.Preview.ProposedCount, true);
    }

    public SemiconductorStationSetupDefinition ResolveSetup(MachineProjectDocument project)
        => _planner.ResolveSetup(project);

    public static bool IsValidSetup(SemiconductorStationSetupDefinition setup) =>
        SemiconductorStationSkeletonPlanner.IsValidSetup(setup);
}
