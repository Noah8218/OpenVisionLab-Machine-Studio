using System.Collections.Immutable;

namespace OpenVisionLab.Machine.Simulation.Snapshots;

public enum SequenceDebugPauseReason
{
    None,
    User,
    FixedTick,
    SemanticStep,
    Breakpoint,
    SequenceCompleted,
    SequenceFaulted,
    SequenceAborted
}

public sealed record SequenceBreakpointSnapshot(string SequenceId, string StepId);

public sealed class SequenceDebugSnapshot
{
    public static SequenceDebugSnapshot Empty { get; } = new(
        false,
        null,
        SequenceDebugPauseReason.None,
        null,
        []);

    public SequenceDebugSnapshot(
        bool isSemanticStepActive,
        string? semanticStepSequenceId,
        SequenceDebugPauseReason pauseReason,
        string? pausedStepId,
        IEnumerable<SequenceBreakpointSnapshot> breakpoints)
    {
        IsSemanticStepActive = isSemanticStepActive;
        SemanticStepSequenceId = semanticStepSequenceId;
        PauseReason = pauseReason;
        PausedStepId = pausedStepId;
        Breakpoints = breakpoints.ToImmutableArray();
    }

    public bool IsSemanticStepActive { get; }

    public string? SemanticStepSequenceId { get; }

    public SequenceDebugPauseReason PauseReason { get; }

    public string? PausedStepId { get; }

    public IReadOnlyList<SequenceBreakpointSnapshot> Breakpoints { get; }
}
