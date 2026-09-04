using System.Collections.Immutable;
using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioBatchPresentationTests
{
    [Fact]
    public void BatchStatePresentationPreservesExistingStatusContracts()
    {
        OpenVisionLanguageService.Load();
        var presentation = new SimulationScenarioBatchPresentation();
        var result = CreateBatchResult(
            isSuccess: true,
            completedRuns: 3,
            evidenceHash: "1234567890ABCDEF");
        var baseline = CreateRunResult("FEDCBA0987654321");

        var running = new SimulationScenarioBatchPresentationState(
            IsBatchRunning: true,
            BatchWasCanceled: false,
            BatchCompletedRuns: 2,
            BatchRepetitionCount: 3,
            LatestBatchResult: result,
            AcceptedBatchBaseline: baseline,
            ArtifactState: SimulationScenarioBatchArtifactState.Saved);

        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchRunning"),
                2,
                3),
            presentation.GetBatchStatusText(running));
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchResultPassed"),
                3,
                "1234567890AB"),
            presentation.GetBatchResultText(running));
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchBaselineAccepted"),
                "FEDCBA098765"),
            presentation.GetBatchBaselineText(running));
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.BatchArtifactSaved"),
            presentation.GetBatchArtifactStatusText(running));

        var canceled = running with
        {
            IsBatchRunning = false,
            BatchWasCanceled = true
        };
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.BatchCanceled"),
            presentation.GetBatchStatusText(canceled));
    }

    [Fact]
    public void BatchMismatchAndAssertionPresentationPreserveTargetAndTickDetails()
    {
        OpenVisionLanguageService.Load();
        var presentation = new SimulationScenarioBatchPresentation();
        var mismatch = new DeterministicSimulationBatchMismatch(
            2,
            "ScenarioMismatch",
            "Scenario evidence differs.",
            "Condition",
            "equipment-1",
            17,
            "ABCDEF1234567890");
        var result = CreateBatchResult(
            isSuccess: false,
            completedRuns: 2,
            firstMismatch: mismatch);
        var state = new SimulationScenarioBatchPresentationState(
            IsBatchRunning: false,
            BatchWasCanceled: false,
            BatchCompletedRuns: 2,
            BatchRepetitionCount: 2,
            LatestBatchResult: result,
            AcceptedBatchBaseline: null,
            ArtifactState: SimulationScenarioBatchArtifactState.None);

        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.BatchMismatch"),
            presentation.GetBatchStatusText(state));
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchResultMismatch"),
                2,
                "Condition",
                "equipment-1",
                17),
            presentation.GetBatchResultText(state));

        var outcomes = new[]
        {
            new DeterministicScenarioAssertionOutcome(
                "cycles",
                DeterministicScenarioAssertionKind.AutomaticCycleCompleted,
                null,
                "2",
                "2",
                2,
                true,
                12,
                "cycles pass"),
            new DeterministicScenarioAssertionOutcome(
                "faults",
                DeterministicScenarioAssertionKind.NoActiveFaults,
                null,
                "0",
                "1",
                1,
                false,
                13,
                "fault remains"),
            new DeterministicScenarioAssertionOutcome(
                "equipment",
                DeterministicScenarioAssertionKind.FinalEquipmentState,
                "equipment-1",
                "Ready",
                "Idle",
                1,
                false,
                14,
                "state differs")
        };
        var presentationResult = CreateRunResult("1111222233334444") with
        {
            AssertionOutcomes = outcomes.ToImmutableArray()
        };

        var displayed = presentation.GetAssertionOutcomes(
            presentationResult,
            [new SimulationScenarioTargetOption("equipment-1", "Transfer stage")]);

        Assert.Equal(3, displayed.Count);
        Assert.Equal(
            OpenVisionLanguageService.T("Simulation.AssertionPassed"),
            displayed[0].StatusText);
        Assert.Contains("2", displayed[0].Summary, StringComparison.Ordinal);
        Assert.Contains("1", displayed[1].Summary, StringComparison.Ordinal);
        Assert.Contains("Transfer stage", displayed[2].Summary, StringComparison.Ordinal);
        Assert.Contains("Ready", displayed[2].Summary, StringComparison.Ordinal);
        Assert.True(SimulationScenarioBatchPresentation.HasAssertionOutcomes(presentationResult));
        Assert.False(SimulationScenarioBatchPresentation.HasAssertionOutcomes(null));
    }

    private static DeterministicSimulationBatchResultPackage CreateBatchResult(
        bool isSuccess,
        int completedRuns,
        string evidenceHash = "ABCDEF1234567890",
        DeterministicSimulationBatchMismatch? firstMismatch = null) => new(
        DeterministicSimulationBatchResultPackage.CurrentSchemaVersion,
        "project-1:scenario-1",
        "test-build",
        completedRuns,
        completedRuns,
        true,
        isSuccess,
        "reference-hash",
        ImmutableArray<DeterministicSimulationBatchRunResult>.Empty,
        firstMismatch,
        evidenceHash);

    private static DeterministicSimulationRunResultPackage CreateRunResult(string evidenceHash) => new(
        DeterministicSimulationRunResultPackage.CurrentSchemaVersion,
        "project-1",
        "Project",
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\scenario-batch-presentation\\project.ovmachine",
        "project-hash",
        TimeSpan.FromMilliseconds(5).Ticks,
        "scenario-1",
        "Scenario",
        "equipment-1",
        42,
        10,
        10,
        true,
        "command-hash",
        "condition-hash",
        "fault-hash",
        "workpiece-hash",
        "signal-hash",
        "snapshot-hash",
        "event-hash",
        "assertion-definition-hash",
        "assertion-outcome-hash",
        ImmutableArray<DeterministicScenarioAssertionOutcome>.Empty,
        "tick-evidence-hash",
        ImmutableArray<DeterministicSimulationTickEvidence>.Empty,
        evidenceHash,
        null);
}
