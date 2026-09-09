namespace OpenVisionLab.Machine.Sequence.Compilation;

/// <summary>
/// Owns cross-sequence composition checks after individual sequences are compiled.
/// </summary>
public static class SequenceCompositionValidator
{
    public static IReadOnlyList<SequenceCompositionError> Validate(
        IEnumerable<CompiledSequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);

        var byId = sequences
            .Where(sequence => sequence is not null && !string.IsNullOrWhiteSpace(sequence.Id))
            .GroupBy(sequence => sequence.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var errors = new List<SequenceCompositionError>();
        foreach (var sequence in byId.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var call in sequence.Steps.OfType<CallSubsequenceStep>())
            {
                if (!byId.ContainsKey(call.SequenceId))
                {
                    errors.Add(new SequenceCompositionError(
                        SequenceCompilationErrorCode.UnknownSubsequence,
                        sequence.Id,
                        call.Id,
                        $"Subsequence '{call.SequenceId}' is not configured."));
                }
            }
        }

        var visitState = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sequence in byId.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Visit(sequence.Id);
        }

        return errors;

        void Visit(string sequenceId)
        {
            if (visitState.TryGetValue(sequenceId, out var state))
            {
                if (state == 2)
                {
                    return;
                }

                var cycleStart = path.IndexOf(sequenceId);
                if (cycleStart < 0)
                {
                    return;
                }

                var cycle = path.Skip(cycleStart).Append(sequenceId).ToArray();
                var cycleKey = string.Join("\u001f", cycle.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal));
                if (reportedCycles.Add(cycleKey))
                {
                    errors.Add(new SequenceCompositionError(
                        SequenceCompilationErrorCode.SubsequenceCycle,
                        cycle[0],
                        null,
                        $"Subsequence call cycle detected: {string.Join(" -> ", cycle)}."));
                }

                return;
            }

            visitState[sequenceId] = 1;
            path.Add(sequenceId);
            if (byId.TryGetValue(sequenceId, out var sequence))
            {
                foreach (var call in sequence.Steps.OfType<CallSubsequenceStep>())
                {
                    if (byId.ContainsKey(call.SequenceId))
                    {
                        Visit(call.SequenceId);
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            visitState[sequenceId] = 2;
        }
    }
}
