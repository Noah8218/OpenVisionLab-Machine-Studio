using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Sequences;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Owns the configured Sequence catalog, executors, and Sequence-debug state
/// used by the fixed-step runtime.
/// </summary>
internal sealed class SimulationSequenceRuntime
{
    private readonly Dictionary<string, CompiledSequence> _compiledSequences =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeterministicSequenceExecutor> _sequenceExecutors =
        new(StringComparer.Ordinal);
    private readonly DeterministicSequenceDebugState _debugState = new();
    private readonly HashSet<string> _activeUnitSequences = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, CompiledSequence> CompiledSequences => _compiledSequences;
    internal IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors => _sequenceExecutors;
    internal DeterministicSequenceDebugState DebugState => _debugState;
    internal bool UnitRunActive => _activeUnitSequences.Any(id => _sequenceExecutors[id].CaptureSnapshot().Status == SequenceExecutionStatus.Running);
    internal bool UnitRunCompleted => _activeUnitSequences.Count > 0 && _activeUnitSequences.All(id => _sequenceExecutors[id].CaptureSnapshot().Status == SequenceExecutionStatus.Completed);

    internal void Configure(
        IReadOnlyDictionary<string, CompiledSequence> compiledSequences,
        IReadOnlyDictionary<string, DeterministicSequenceExecutor> sequenceExecutors)
    {
        ArgumentNullException.ThrowIfNull(compiledSequences);
        ArgumentNullException.ThrowIfNull(sequenceExecutors);

        _debugState.Clear();
        _activeUnitSequences.Clear();
        _compiledSequences.Clear();
        foreach (var pair in compiledSequences)
        {
            _compiledSequences.Add(pair.Key, pair.Value);
        }

        _sequenceExecutors.Clear();
        foreach (var pair in sequenceExecutors)
        {
            _sequenceExecutors.Add(pair.Key, pair.Value);
        }
    }

    internal void ClearConfiguration()
    {
        _activeUnitSequences.Clear();
        _compiledSequences.Clear();
        _sequenceExecutors.Clear();
        _debugState.Clear();
    }

    internal void ResetExecutors()
    {
        _activeUnitSequences.Clear();
        foreach (var executor in _sequenceExecutors.Values)
        {
            executor.Reset();
        }
    }

    internal void BeginUnitRun(string? rootSequenceId)
    {
        if (rootSequenceId is null || !_compiledSequences.TryGetValue(rootSequenceId, out var root) || root.UnitId is null) return;
        _activeUnitSequences.Clear();
        foreach (var sequence in _compiledSequences.Values.Where(sequence => sequence.UnitId is not null).OrderBy(sequence => sequence.UnitId, StringComparer.Ordinal))
        {
            _activeUnitSequences.Add(sequence.Id);
            if (sequence.Id == rootSequenceId) continue;
            _sequenceExecutors[sequence.Id].Reset();
            _sequenceExecutors[sequence.Id].Start();
        }
    }

    internal IReadOnlyList<string> TickSequenceIds(string? rootSequenceId) => _activeUnitSequences.Count > 0
        ? _activeUnitSequences.Where(id => _sequenceExecutors[id].CaptureSnapshot().Status == SequenceExecutionStatus.Running).Order(StringComparer.Ordinal).ToArray()
        : rootSequenceId is not null ? new[] { rootSequenceId } : [];

    internal void AbortUnitRun()
    {
        foreach (var id in _activeUnitSequences)
            if (_sequenceExecutors[id].CaptureSnapshot().Status == SequenceExecutionStatus.Running) _sequenceExecutors[id].Abort();
    }

    internal string? CurrentStepId(string? activeSequenceId) =>
        activeSequenceId is not null
        && _sequenceExecutors.TryGetValue(activeSequenceId, out var executor)
            ? executor.CaptureSnapshot().CurrentStepId
            : null;

    internal bool TryGetCurrentVisionWaitTimeout(
        string? activeSequenceId,
        out string sequenceId,
        out string stepId,
        out TimeSpan timeout)
    {
        sequenceId = string.Empty;
        stepId = string.Empty;
        timeout = TimeSpan.Zero;
        if (activeSequenceId is null
            || !_sequenceExecutors.TryGetValue(activeSequenceId, out var executor))
        {
            return false;
        }

        var snapshot = executor.CaptureSnapshot();
        sequenceId = snapshot.ActiveSequenceId ?? snapshot.SequenceId;
        stepId = snapshot.CurrentStepId ?? string.Empty;
        return _compiledSequences.TryGetValue(sequenceId, out var sequence)
            && !string.IsNullOrWhiteSpace(stepId)
            && sequence.TryGetStep(stepId, out var step)
            && step is WaitVisionResultStep waitVision
            && waitVision.Timeout > TimeSpan.Zero
            && (timeout = waitVision.Timeout) > TimeSpan.Zero;
    }
}
