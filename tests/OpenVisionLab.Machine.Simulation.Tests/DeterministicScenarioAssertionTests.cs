using System.Collections.Immutable;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicScenarioAssertionTests
{
    private const string ArtifactRoot =
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260813-scenario-assertions";

    [Fact]
    public async Task ConcreteRuns_EvaluateSnapshotAndEventAssertionsIntoSchema5Evidence()
    {
        var automaticRun = await AutomaticTransferCellScheduledFaultRecoveryTests.RunAsync(
            AutomaticTransferCellScheduledFaultRecoveryTests.BlockedCylinderTravel,
            ImmutableArray.Create(
                new DeterministicScenarioAssertion(
                    "automatic-cycle-completed",
                    DeterministicScenarioAssertionKind.AutomaticCycleCompleted,
                    MinimumCount: 1),
                new DeterministicScenarioAssertion(
                    "final-faults-cleared",
                    DeterministicScenarioAssertionKind.NoActiveFaults)));

        Assert.Equal(5, automaticRun.Package.SchemaVersion);
        Assert.True(automaticRun.Package.IsSuccess, automaticRun.Package.FailureReason);
        Assert.Collection(
            automaticRun.Package.AssertionOutcomes,
            cycle =>
            {
                Assert.Equal("automatic-cycle-completed", cycle.AssertionId);
                Assert.Equal(DeterministicScenarioAssertionKind.AutomaticCycleCompleted, cycle.Kind);
                Assert.True(cycle.IsPassed);
                Assert.Equal(">=1", cycle.ExpectedValue);
                Assert.NotEqual("0", cycle.ActualValue);
                Assert.Equal(automaticRun.FirstCompletedCycleTick, cycle.ObservedTickIndex);
            },
            faults =>
            {
                Assert.Equal("final-faults-cleared", faults.AssertionId);
                Assert.Equal(DeterministicScenarioAssertionKind.NoActiveFaults, faults.Kind);
                Assert.True(faults.IsPassed);
                Assert.Equal("0", faults.ExpectedValue);
                Assert.Equal("0", faults.ActualValue);
            });

        var axisRun = await PickAndPlaceFaultRecoveryEvidenceTests.RunAsync(
            assertions: ImmutableArray.Create(
                new DeterministicScenarioAssertion(
                    "x-finished-idle",
                    DeterministicScenarioAssertionKind.FinalEquipmentState,
                    TargetId: "x",
                    ExpectedState: "Idle")));
        DeterministicScenarioAssertionOutcome axisState = Assert.Single(
            axisRun.Package.AssertionOutcomes);
        Assert.True(axisRun.Package.IsSuccess, axisRun.Package.FailureReason);
        Assert.True(axisState.IsPassed);
        Assert.Equal("x", axisState.TargetId);
        Assert.Equal("Idle", axisState.ExpectedValue);
        Assert.Equal("Idle", axisState.ActualValue);
        Assert.Equal(axisRun.Package.ExecutedTicks, axisState.ObservedTickIndex);

        SaveAndAssertRoundTrip(
            automaticRun.Package,
            Path.Combine(ArtifactRoot, "automatic-cycle-assertions.json"));
        SaveAndAssertRoundTrip(
            axisRun.Package,
            Path.Combine(ArtifactRoot, "axis-final-state-assertion.json"));
    }

    [Fact]
    public async Task FailedAssertion_IsHashedAndChangesScenarioContext()
    {
        var passed = await PickAndPlaceFaultRecoveryEvidenceTests.RunAsync(
            assertions: ImmutableArray.Create(
                new DeterministicScenarioAssertion(
                    "x-final-state",
                    DeterministicScenarioAssertionKind.FinalEquipmentState,
                    TargetId: "x",
                    ExpectedState: "Idle")));
        var failed = await PickAndPlaceFaultRecoveryEvidenceTests.RunAsync(
            assertions: ImmutableArray.Create(
                new DeterministicScenarioAssertion(
                    "x-final-state",
                    DeterministicScenarioAssertionKind.FinalEquipmentState,
                    TargetId: "x",
                    ExpectedState: "Moving")));

        DeterministicScenarioAssertionOutcome outcome = Assert.Single(
            failed.Package.AssertionOutcomes);
        Assert.False(failed.Package.IsSuccess);
        Assert.False(outcome.IsPassed);
        Assert.Equal("Moving", outcome.ExpectedValue);
        Assert.Equal("Idle", outcome.ActualValue);
        Assert.Contains("x-final-state", failed.Package.FailureReason, StringComparison.Ordinal);
        Assert.True(failed.Package.HasValidEvidenceHash());
        Assert.Equal(
            "AssertionDefinitionMismatch",
            passed.Package.CompareTo(failed.Package).MismatchCode);
        Assert.False((failed.Package with
        {
            AssertionOutcomes = failed.Package.AssertionOutcomes.SetItem(
                0,
                outcome with { ActualValue = "tampered" })
        }).HasValidEvidenceHash());

        SaveAndAssertRoundTrip(
            failed.Package,
            Path.Combine(ArtifactRoot, "failed-equipment-state-assertion.json"));
    }

    [Fact]
    public void ProfileAssertions_NormalizeRoundTripAndRejectDuplicateIds()
    {
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "assertion-profile",
            "Assertion profile",
            "Profile persistence fixture.",
            "x",
            42,
            20,
            Assertions: ImmutableArray.Create(
                new DeterministicScenarioAssertion(
                    " cycle-complete ",
                    DeterministicScenarioAssertionKind.AutomaticCycleCompleted,
                    TargetId: "ignored",
                    ExpectedState: "ignored",
                    MinimumCount: 2),
                new DeterministicScenarioAssertion(
                    "x-idle",
                    DeterministicScenarioAssertionKind.FinalEquipmentState,
                    TargetId: " x ",
                    ExpectedState: " Idle ",
                    MinimumCount: 99)));
        DeterministicConditionScenarioProfile normalized =
            DeterministicConditionScenarioProfile.Normalize(profile);

        Assert.Collection(
            normalized.Assertions,
            cycle =>
            {
                Assert.Equal("cycle-complete", cycle.AssertionId);
                Assert.Null(cycle.TargetId);
                Assert.Null(cycle.ExpectedState);
                Assert.Equal(2, cycle.MinimumCount);
            },
            equipment =>
            {
                Assert.Equal("x", equipment.TargetId);
                Assert.Equal("Idle", equipment.ExpectedState);
                Assert.Equal(1, equipment.MinimumCount);
            });
        Assert.Empty(DeterministicConditionScenarioProfile.Validate(normalized));

        string path = Path.Combine(ArtifactRoot, "assertion-profile.json");
        DeterministicConditionScenarioProfile.SaveToJson(profile, path);
        var loaded = Assert.IsType<DeterministicConditionScenarioProfile>(
            DeterministicConditionScenarioProfile.LoadFromJson(path));
        Assert.Equal(
            DeterministicConditionScenarioProfile.SaveToJson(normalized),
            DeterministicConditionScenarioProfile.SaveToJson(loaded));

        var duplicate = normalized with
        {
            Assertions = normalized.Assertions.Add(
                new DeterministicScenarioAssertion(
                    "cycle-complete",
                    DeterministicScenarioAssertionKind.NoActiveFaults))
        };
        Assert.Contains(
            DeterministicConditionScenarioProfile.Validate(duplicate),
            error => error.Contains("must be unique", StringComparison.Ordinal));
    }

    private static void SaveAndAssertRoundTrip(
        DeterministicSimulationRunResultPackage package,
        string path)
    {
        Assert.True(package.HasValidEvidenceHash());
        DeterministicSimulationRunResultPackage.SaveToJson(package, path);
        Assert.True(package.IsEquivalentTo(
            DeterministicSimulationRunResultPackage.LoadFromJson(path)));
    }
}
