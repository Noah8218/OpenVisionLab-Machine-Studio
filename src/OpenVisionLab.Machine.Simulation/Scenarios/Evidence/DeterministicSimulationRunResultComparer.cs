using System.Collections.Immutable;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

internal sealed class DeterministicSimulationRunResultComparer
{
    internal DeterministicSimulationRunComparison Compare(
        DeterministicSimulationRunResultPackage expected,
        DeterministicSimulationRunResultPackage? actual)
    {
        if (actual is null)
        {
            return new(false, "MissingResult", "The comparison package is missing.");
        }

        if (expected.SchemaVersion != actual.SchemaVersion)
        {
            return new(false, "SchemaMismatch", "Result package schemas differ.");
        }

        if (!string.Equals(expected.ProjectId, actual.ProjectId, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectHash, actual.ProjectHash, StringComparison.Ordinal))
        {
            return new(false, "ProjectMismatch", "Project identity or content hash differs.");
        }

        if (expected.FixedStepTicks != actual.FixedStepTicks
            || !string.Equals(expected.ScenarioId, actual.ScenarioId, StringComparison.Ordinal)
            || !string.Equals(expected.TargetId, actual.TargetId, StringComparison.Ordinal)
            || expected.Seed != actual.Seed
            || expected.PlannedTicks != actual.PlannedTicks)
        {
            return new(false, "ScenarioMismatch", "Scenario identity, seed, duration, or fixed step differs.");
        }

        var tickComparison = CompareTickEvidence(expected, actual);
        if (tickComparison is not null)
        {
            return tickComparison;
        }

        if (!string.Equals(expected.CommandHash, actual.CommandHash, StringComparison.Ordinal))
        {
            return new(false, "CommandHashMismatch", "Command result history hash differs.");
        }

        if (!string.Equals(expected.ConditionHash, actual.ConditionHash, StringComparison.Ordinal))
        {
            return new(false, "ConditionHashMismatch", "Condition history hash differs.");
        }

        if (!string.Equals(expected.FaultHash, actual.FaultHash, StringComparison.Ordinal))
        {
            return new(false, "FaultHashMismatch", "Fault history hash differs.");
        }

        if (!string.Equals(expected.WorkpieceHash, actual.WorkpieceHash, StringComparison.Ordinal))
        {
            return new(false, "WorkpieceHashMismatch", "Workpiece history hash differs.");
        }

        if (!string.Equals(expected.SignalHash, actual.SignalHash, StringComparison.Ordinal))
        {
            return new(false, "SignalHashMismatch", "Signal history hash differs.");
        }

        if (!string.Equals(expected.SnapshotHash, actual.SnapshotHash, StringComparison.Ordinal))
        {
            return new(false, "SnapshotHashMismatch", "Snapshot history hash differs.");
        }

        if (!string.Equals(expected.EventHash, actual.EventHash, StringComparison.Ordinal))
        {
            return new(false, "EventHashMismatch", "Event history hash differs.");
        }

        if (!string.Equals(expected.AssertionDefinitionHash, actual.AssertionDefinitionHash, StringComparison.Ordinal))
        {
            return new(false, "AssertionDefinitionMismatch", "Scenario assertion definitions differ.");
        }

        if (!string.Equals(expected.AssertionOutcomeHash, actual.AssertionOutcomeHash, StringComparison.Ordinal))
        {
            return new(false, "AssertionOutcomeMismatch", "Scenario assertion outcomes differ.");
        }

        if (!string.Equals(expected.EvidenceHash, actual.EvidenceHash, StringComparison.Ordinal)
            || expected.IsSuccess != actual.IsSuccess
            || expected.ExecutedTicks != actual.ExecutedTicks)
        {
            return new(false, "ResultMismatch", "Run outcome or combined evidence differs.");
        }

        return new(true, null, null);
    }

    private static DeterministicSimulationRunComparison? CompareTickEvidence(
        DeterministicSimulationRunResultPackage expectedPackage,
        DeterministicSimulationRunResultPackage actualPackage)
    {
        var expected = expectedPackage.TickEvidence.IsDefault
            ? ImmutableArray<DeterministicSimulationTickEvidence>.Empty
            : expectedPackage.TickEvidence;
        var actual = actualPackage.TickEvidence.IsDefault
            ? ImmutableArray<DeterministicSimulationTickEvidence>.Empty
            : actualPackage.TickEvidence;
        var expectedIndex = 0;
        var actualIndex = 0;
        while (expectedIndex < expected.Length || actualIndex < actual.Length)
        {
            var expectedPoint = expectedIndex < expected.Length ? expected[expectedIndex] : null;
            var actualPoint = actualIndex < actual.Length ? actual[actualIndex] : null;
            if (actualPoint is null
                || (expectedPoint is not null && expectedPoint.TickIndex < actualPoint.TickIndex))
            {
                return EvidenceMismatch(
                    "Tick",
                    expectedPoint!.TickIndex,
                    expectedPoint.TargetId,
                    expectedPoint.EvidenceHash,
                    string.Empty);
            }

            if (expectedPoint is null || actualPoint.TickIndex < expectedPoint.TickIndex)
            {
                return EvidenceMismatch(
                    "Tick",
                    actualPoint.TickIndex,
                    actualPoint.TargetId,
                    string.Empty,
                    actualPoint.EvidenceHash);
            }

            var mismatch = CompareTickPoint(expectedPoint, actualPoint);
            if (mismatch is not null)
            {
                return mismatch;
            }

            expectedIndex++;
            actualIndex++;
        }

        return null;
    }

    private static DeterministicSimulationRunComparison? CompareTickPoint(
        DeterministicSimulationTickEvidence expected,
        DeterministicSimulationTickEvidence actual)
    {
        var fields = new[]
        {
            (Kind: "Command", Expected: expected.CommandHash, Actual: actual.CommandHash),
            (Kind: "Condition", Expected: expected.ConditionHash, Actual: actual.ConditionHash),
            (Kind: "Fault", Expected: expected.FaultHash, Actual: actual.FaultHash),
            (Kind: "Workpiece", Expected: expected.WorkpieceHash, Actual: actual.WorkpieceHash),
            (Kind: "Signal", Expected: expected.SignalHash, Actual: actual.SignalHash),
            (Kind: "Snapshot", Expected: expected.SnapshotHash, Actual: actual.SnapshotHash),
            (Kind: "Event", Expected: expected.EventHash, Actual: actual.EventHash)
        };
        foreach (var field in fields)
        {
            if (!string.Equals(field.Expected, field.Actual, StringComparison.Ordinal))
            {
                return EvidenceMismatch(
                    field.Kind,
                    expected.TickIndex,
                    expected.TargetId,
                    field.Expected,
                    field.Actual);
            }
        }

        return null;
    }

    private static DeterministicSimulationRunComparison EvidenceMismatch(
        string evidenceKind,
        long tickIndex,
        string targetId,
        string expectedHash,
        string actualHash) =>
        new(
            false,
            $"{evidenceKind}EvidenceMismatch",
            $"{evidenceKind} evidence first differs at Tick {tickIndex} for '{targetId}'.",
            new DeterministicSimulationEvidenceMismatch(
                tickIndex,
                evidenceKind,
                targetId,
                expectedHash,
                actualHash));
}
