namespace OpenVisionLab.Machine.Simulation.Scenarios;

internal sealed class DeterministicSimulationBatchResultComparer
{
    internal DeterministicSimulationBatchComparison Compare(
        DeterministicSimulationBatchResultPackage expected,
        DeterministicSimulationBatchResultPackage? actual)
    {
        if (actual is null)
        {
            return new(false, "MissingBatch", "The comparison batch is missing.", null);
        }

        if (!string.Equals(expected.BatchId, actual.BatchId, StringComparison.Ordinal)
            || !string.Equals(expected.BuildIdentity, actual.BuildIdentity, StringComparison.Ordinal)
            || expected.RequestedRuns != actual.RequestedRuns)
        {
            return new(false, "BatchDefinitionMismatch", "Batch identity, build, or run count differs.", null);
        }

        if (expected.CompletedRuns != actual.CompletedRuns || expected.IsComplete != actual.IsComplete)
        {
            return new(false, "BatchCompletionMismatch", "Batch completion state differs.", null);
        }

        for (var index = 0; index < Math.Min(expected.Runs.Length, actual.Runs.Length); index++)
        {
            var runComparison = expected.Runs[index].Result.CompareTo(actual.Runs[index].Result);
            if (!runComparison.IsMatch)
            {
                return new(
                    false,
                    runComparison.MismatchCode ?? "RunMismatch",
                    runComparison.Detail ?? "Run evidence differs.",
                    CreateMismatch(expected.Runs[index].RunIndex, actual.Runs[index].Result, runComparison));
            }
        }

        if (expected.Runs.Length != actual.Runs.Length
            || expected.IsSuccess != actual.IsSuccess
            || !string.Equals(expected.EvidenceHash, actual.EvidenceHash, StringComparison.Ordinal))
        {
            return new(false, "BatchEvidenceMismatch", "Batch outcome or evidence hash differs.", expected.FirstMismatch);
        }

        return new(true, null, null, null);
    }

    internal static DeterministicSimulationBatchMismatch CreateMismatch(
        int runIndex,
        DeterministicSimulationRunResultPackage result,
        DeterministicSimulationRunComparison comparison)
    {
        var evidence = comparison.FirstMismatch;
        return new(
            runIndex,
            comparison.MismatchCode ?? "RunMismatch",
            comparison.Detail ?? result.FailureReason ?? "Run evidence differs.",
            evidence?.EvidenceKind ?? "Run",
            evidence?.TargetId ?? result.TargetId,
            evidence?.TickIndex ?? result.ExecutedTicks,
            result.EvidenceHash);
    }

    internal static DeterministicSimulationRunComparison EffectiveComparison(
        DeterministicSimulationBatchRunResult run) =>
        run.ReferenceComparison.IsMatch
            ? new DeterministicSimulationRunComparison(
                false,
                "RunFailed",
                run.Result.FailureReason ?? "The run did not complete successfully.")
            : run.ReferenceComparison;
}
