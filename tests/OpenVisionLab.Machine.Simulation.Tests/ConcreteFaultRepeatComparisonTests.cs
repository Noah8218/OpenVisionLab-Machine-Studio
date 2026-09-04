using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class ConcreteFaultRepeatComparisonTests
{
    private const string ArtifactRoot =
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260813-cross-fault-repeat";

    [Fact]
    public async Task DiCylinderAndAxis_RepeatsMatchAndChangedHoldLocatesFirstFaultTick()
    {
        var stuckInput = AutomaticTransferCellScheduledFaultRecoveryTests.PersistedStuckInput;
        await AssertScenarioAsync(
            "stuck-di",
            "cylinder-1",
            stuckInput.HoldTicks,
            async holdTicks =>
            {
                var run = await AutomaticTransferCellScheduledFaultRecoveryTests.RunAsync(
                    stuckInput with
                    {
                        HoldTicks = holdTicks,
                        IsPersistedConfiguration = holdTicks == stuckInput.HoldTicks
                    });
                return (run.Package, run.FaultClearedTick);
            });

        var blockedCylinder =
            AutomaticTransferCellScheduledFaultRecoveryTests.BlockedCylinderTravel;
        await AssertScenarioAsync(
            "blocked-cylinder",
            "cylinder-1",
            blockedCylinder.HoldTicks,
            async holdTicks =>
            {
                var run = await AutomaticTransferCellScheduledFaultRecoveryTests.RunAsync(
                    blockedCylinder with { HoldTicks = holdTicks });
                return (run.Package, run.FaultClearedTick);
            });

        await AssertScenarioAsync(
            "blocked-axis",
            "x",
            baselineHoldTicks: 3,
            async holdTicks =>
            {
                var run = await PickAndPlaceFaultRecoveryEvidenceTests.RunAsync(holdTicks);
                return (run.Package, run.FaultClearedTick);
            });
    }

    private static async Task AssertScenarioAsync(
        string scenarioName,
        string expectedTargetId,
        int baselineHoldTicks,
        Func<int, Task<(DeterministicSimulationRunResultPackage Package, long FaultClearedTick)>> runAsync)
    {
        var accepted = await runAsync(baselineHoldTicks);
        var runner = new DeterministicSimulationBatchRunner();
        var repeated = await runner.RunAsync(
            new DeterministicSimulationBatchDefinition(
                $"{scenarioName}-repeat",
                RepetitionCount: 2,
                BuildIdentity: "concrete-fault-repeat-v1"),
            async (_, _) => (await runAsync(baselineHoldTicks)).Package,
            accepted.Package);

        Assert.True(repeated.IsComplete);
        Assert.True(repeated.IsSuccess, repeated.FirstMismatch?.ToString());
        Assert.Equal(2, repeated.CompletedRuns);
        Assert.Null(repeated.FirstMismatch);
        Assert.All(repeated.Runs, run => Assert.True(run.ReferenceComparison.IsMatch));
        Assert.True(repeated.HasValidEvidenceHash());

        var changed = await runner.RunAsync(
            new DeterministicSimulationBatchDefinition(
                $"{scenarioName}-changed-hold",
                RepetitionCount: 1,
                BuildIdentity: "concrete-fault-repeat-v1"),
            async (_, _) => (await runAsync(baselineHoldTicks + 1)).Package,
            accepted.Package);

        Assert.True(changed.IsComplete);
        Assert.False(changed.IsSuccess);
        var mismatch = Assert.IsType<DeterministicSimulationBatchMismatch>(changed.FirstMismatch);
        Assert.Equal(1, mismatch.RunIndex);
        Assert.Equal("FaultEvidenceMismatch", mismatch.Code);
        Assert.Equal("Fault", mismatch.EvidenceKind);
        Assert.Equal(expectedTargetId, mismatch.TargetId);
        Assert.Equal(accepted.FaultClearedTick, mismatch.ObservedTickIndex);
        Assert.True(changed.HasValidEvidenceHash());

        string scenarioArtifactRoot = Path.Combine(ArtifactRoot, scenarioName);
        string baselinePath = Path.Combine(scenarioArtifactRoot, "accepted-baseline.json");
        string repeatedPath = Path.Combine(scenarioArtifactRoot, "repeated-batch.json");
        string mismatchPath = Path.Combine(scenarioArtifactRoot, "changed-hold-mismatch.json");
        DeterministicSimulationRunResultPackage.SaveToJson(accepted.Package, baselinePath);
        DeterministicSimulationBatchResultPackage.SaveToJson(repeated, repeatedPath);
        DeterministicSimulationBatchResultPackage.SaveToJson(changed, mismatchPath);

        Assert.True(accepted.Package.IsEquivalentTo(
            DeterministicSimulationRunResultPackage.LoadFromJson(baselinePath)));
        Assert.True(repeated.IsEquivalentTo(
            DeterministicSimulationBatchResultPackage.LoadFromJson(repeatedPath)));
        Assert.True(changed.IsEquivalentTo(
            DeterministicSimulationBatchResultPackage.LoadFromJson(mismatchPath)));
    }
}
