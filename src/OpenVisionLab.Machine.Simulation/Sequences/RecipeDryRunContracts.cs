using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Sequences;

public enum RecipeDryRunOutcome
{
    Completed,
    CompletedWithIssue,
    CompletedWithMismatch,
    LimitReached,
    Faulted,
    Rejected
}

public sealed record RecipeDryRunIssue(
    string StepId,
    long Tick,
    string Code,
    string Detail);

public sealed record RecipeDryRunStepCheckpoint(
    string TargetId,
    string ExpectedState,
    string ActualState,
    bool IsPassed,
    string Detail);

public sealed record RecipeDryRunCheckpointMismatch(
    string StepId,
    long Tick,
    string TargetId,
    string ExpectedState,
    string ActualState,
    string Detail);

public sealed record RecipeDryRunStepTrace(
    string StepId,
    string Name,
    SequenceStepAction Action,
    long StartedTick,
    long EndedTick,
    bool HasIssue,
    SimulationSnapshot BoundarySnapshot,
    RecipeDryRunStepCheckpoint? Checkpoint)
{
    public bool HasCheckpoint => Checkpoint is not null;
    public bool HasCheckpointMismatch => Checkpoint is { IsPassed: false };
}

public sealed record RecipeDryRunResult(
    RecipeDryRunOutcome Outcome,
    string SequenceId,
    string SequenceName,
    long ExecutedTicks,
    int MaximumTicks,
    IReadOnlyList<RecipeDryRunStepTrace> Timeline,
    RecipeDryRunIssue? FirstIssue,
    RecipeDryRunCheckpointMismatch? FirstCheckpointMismatch,
    SimulationSnapshot? FinalSnapshot,
    string Detail)
{
    public bool IsCompleted => Outcome is RecipeDryRunOutcome.Completed
        or RecipeDryRunOutcome.CompletedWithIssue
        or RecipeDryRunOutcome.CompletedWithMismatch;
}
