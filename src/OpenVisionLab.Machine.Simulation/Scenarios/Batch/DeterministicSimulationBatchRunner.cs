using System.Collections.Immutable;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Runs one deterministic repetition at a time and compares every result with
/// either the accepted baseline or the first repetition.
/// </summary>
public sealed class DeterministicSimulationBatchRunner
{
    public async Task<DeterministicSimulationBatchResultPackage> RunAsync(
        DeterministicSimulationBatchDefinition definition,
        Func<int, CancellationToken, Task<DeterministicSimulationRunResultPackage>> runAsync,
        DeterministicSimulationRunResultPackage? acceptedBaseline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(runAsync);
        if (string.IsNullOrWhiteSpace(definition.BatchId))
        {
            throw new ArgumentException("Batch id is required.", nameof(definition));
        }

        if (definition.RepetitionCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Repetition count must be positive.");
        }

        var normalized = definition with
        {
            BatchId = definition.BatchId.Trim(),
            BuildIdentity = definition.BuildIdentity?.Trim() ?? string.Empty
        };
        var runs = ImmutableArray.CreateBuilder<DeterministicSimulationBatchRunResult>(
            normalized.RepetitionCount);
        DeterministicSimulationRunResultPackage? reference = acceptedBaseline;
        DeterministicSimulationBatchMismatch? firstMismatch = null;

        for (var runIndex = 1; runIndex <= normalized.RepetitionCount; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await runAsync(runIndex, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Batch run {runIndex} returned no result package.");
            reference ??= result;
            var comparison = reference.CompareTo(result);
            runs.Add(new DeterministicSimulationBatchRunResult(runIndex, result, comparison));

            if (firstMismatch is null && (!comparison.IsMatch || !result.IsSuccess))
            {
                var effectiveComparison = comparison.IsMatch
                    ? new DeterministicSimulationRunComparison(
                        false,
                        "RunFailed",
                        result.FailureReason ?? "The run did not complete successfully.")
                    : comparison;
                firstMismatch = DeterministicSimulationBatchResultComparer.CreateMismatch(
                    runIndex,
                    result,
                    effectiveComparison);
            }
        }

        var completedRuns = runs.ToImmutable();
        var referenceHash = reference?.EvidenceHash ?? string.Empty;
        return new DeterministicSimulationBatchResultPackage(
            DeterministicSimulationBatchResultPackage.CurrentSchemaVersion,
            normalized.BatchId,
            normalized.BuildIdentity,
            normalized.RepetitionCount,
            completedRuns.Length,
            IsComplete: true,
            IsSuccess: firstMismatch is null,
            referenceHash,
            completedRuns,
            firstMismatch,
            DeterministicSimulationBatchResultPackage.Hash(normalized, referenceHash, completedRuns));
    }
}
