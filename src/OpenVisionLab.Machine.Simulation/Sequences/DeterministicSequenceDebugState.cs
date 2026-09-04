using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Sequences;

internal sealed class DeterministicSequenceDebugState
{
    private readonly HashSet<(string SequenceId, string StepId)> _breakpoints = [];
    private string? _semanticStepSequenceId;
    private string? _semanticStepInitialStepId;
    private SequenceDebugPauseReason _pauseReason;
    private string? _pausedStepId;

    public bool IsSemanticStepActive => _semanticStepSequenceId is not null;

    public bool IsBreakpoint(string sequenceId, string stepId) => _breakpoints.Contains((sequenceId, stepId));

    public void SetBreakpoint(string sequenceId, string stepId, bool isEnabled)
    {
        if (isEnabled)
        {
            _breakpoints.Add((sequenceId, stepId));
        }
        else
        {
            _breakpoints.Remove((sequenceId, stepId));
        }
    }

    public void BeginSemanticStep(string sequenceId, string initialStepId)
    {
        _semanticStepSequenceId = sequenceId;
        _semanticStepInitialStepId = initialStepId;
    }

    public bool IsSemanticStepBoundary(SequenceExecutionResult execution, string rootSequenceId) =>
        string.Equals(_semanticStepSequenceId, rootSequenceId, StringComparison.Ordinal)
        && string.Equals(_semanticStepInitialStepId, execution.PreviousStepId, StringComparison.Ordinal)
        && string.Equals(execution.PreviousSequenceId, rootSequenceId, StringComparison.Ordinal);

    public string? GetActiveSemanticStepSequenceId(string? activeSequenceId) =>
        _semanticStepSequenceId is not null
        && string.Equals(_semanticStepSequenceId, activeSequenceId, StringComparison.Ordinal)
            ? _semanticStepSequenceId
            : null;

    public void ClearPendingSemanticStep()
    {
        _semanticStepSequenceId = null;
        _semanticStepInitialStepId = null;
    }

    public void SetPause(SequenceDebugPauseReason reason, string? stepId)
    {
        _pauseReason = reason;
        _pausedStepId = stepId;
    }

    public void Clear()
    {
        _breakpoints.Clear();
        ClearPendingSemanticStep();
        SetPause(SequenceDebugPauseReason.None, null);
    }

    public SequenceDebugSnapshot CreateSnapshot() => new(
        IsSemanticStepActive,
        _semanticStepSequenceId,
        _pauseReason,
        _pausedStepId,
        _breakpoints
            .OrderBy(item => item.SequenceId, StringComparer.Ordinal)
            .ThenBy(item => item.StepId, StringComparer.Ordinal)
            .Select(item => new SequenceBreakpointSnapshot(item.SequenceId, item.StepId)));
}
