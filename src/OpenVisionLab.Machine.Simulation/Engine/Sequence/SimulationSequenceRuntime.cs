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

    internal IReadOnlyDictionary<string, CompiledSequence> CompiledSequences => _compiledSequences;
    internal IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors => _sequenceExecutors;
    internal DeterministicSequenceDebugState DebugState => _debugState;

    internal void Configure(
        IReadOnlyDictionary<string, CompiledSequence> compiledSequences,
        IReadOnlyDictionary<string, DeterministicSequenceExecutor> sequenceExecutors)
    {
        ArgumentNullException.ThrowIfNull(compiledSequences);
        ArgumentNullException.ThrowIfNull(sequenceExecutors);

        _debugState.Clear();
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
        _compiledSequences.Clear();
        _sequenceExecutors.Clear();
        _debugState.Clear();
    }

    internal void ResetExecutors()
    {
        foreach (var executor in _sequenceExecutors.Values)
        {
            executor.Reset();
        }
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
