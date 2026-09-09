using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record ScenarioAssertionOutcomePresentation(
    bool IsPassed,
    string StatusText,
    string Summary);

internal sealed record SimulationScenarioBatchPresentationState(
    bool IsBatchRunning,
    bool BatchWasCanceled,
    int BatchCompletedRuns,
    int BatchRepetitionCount,
    DeterministicSimulationBatchResultPackage? LatestBatchResult,
    DeterministicSimulationRunResultPackage? AcceptedBatchBaseline,
    SimulationScenarioBatchArtifactState ArtifactState);

/// <summary>
/// Maps deterministic scenario-batch state to the existing binding-facing
/// display values. It does not own commands, persistence, or runtime state.
/// </summary>
internal sealed class SimulationScenarioBatchPresentation
{
    internal string GetBatchStatusText(SimulationScenarioBatchPresentationState state) =>
        state.IsBatchRunning
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchRunning"),
                state.BatchCompletedRuns,
                state.BatchRepetitionCount)
            : state.BatchWasCanceled
                ? OpenVisionLanguageService.T("Simulation.BatchCanceled")
                : state.LatestBatchResult is null
                    ? OpenVisionLanguageService.T("Simulation.BatchIdle")
                    : state.LatestBatchResult.IsSuccess
                        ? string.Format(
                            CultureInfo.CurrentCulture,
                            OpenVisionLanguageService.T("Simulation.BatchPassed"),
                            state.LatestBatchResult.CompletedRuns)
                        : OpenVisionLanguageService.T("Simulation.BatchMismatch");

    internal string GetBatchResultText(SimulationScenarioBatchPresentationState state)
    {
        if (state.LatestBatchResult is null)
        {
            return OpenVisionLanguageService.T("Simulation.BatchNoResult");
        }

        if (state.LatestBatchResult.IsSuccess)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchResultPassed"),
                state.LatestBatchResult.CompletedRuns,
                ShortHash(state.LatestBatchResult.EvidenceHash));
        }

        var mismatch = state.LatestBatchResult.FirstMismatch;
        return mismatch is null
            ? OpenVisionLanguageService.T("Simulation.BatchMismatch")
            : string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchResultMismatch"),
                mismatch.RunIndex,
                mismatch.EvidenceKind,
                mismatch.TargetId,
                mismatch.ObservedTickIndex);
    }

    internal string GetBatchBaselineText(SimulationScenarioBatchPresentationState state) =>
        state.AcceptedBatchBaseline is null
            ? OpenVisionLanguageService.T("Simulation.BatchBaselineNone")
            : string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.BatchBaselineAccepted"),
                ShortHash(state.AcceptedBatchBaseline.EvidenceHash));

    internal string GetBatchArtifactStatusText(SimulationScenarioBatchPresentationState state) =>
        state.ArtifactState switch
        {
            SimulationScenarioBatchArtifactState.MemoryOnly =>
                OpenVisionLanguageService.T("Simulation.BatchArtifactMemoryOnly"),
            SimulationScenarioBatchArtifactState.Saved =>
                OpenVisionLanguageService.T("Simulation.BatchArtifactSaved"),
            SimulationScenarioBatchArtifactState.Restored =>
                OpenVisionLanguageService.T("Simulation.BatchArtifactRestored"),
            SimulationScenarioBatchArtifactState.Imported =>
                OpenVisionLanguageService.T("Simulation.BatchArtifactImported"),
            SimulationScenarioBatchArtifactState.StaleRejected =>
                OpenVisionLanguageService.T("Simulation.BatchArtifactStale"),
            SimulationScenarioBatchArtifactState.SaveFailed =>
                OpenVisionLanguageService.T("Simulation.BatchArtifactSaveFailed"),
            _ => OpenVisionLanguageService.T("Simulation.BatchArtifactNone")
        };

    internal IReadOnlyList<ScenarioAssertionOutcomePresentation> GetAssertionOutcomes(
        DeterministicSimulationRunResultPackage? result,
        IReadOnlyList<SimulationScenarioTargetOption> scenarioTargets)
    {
        ArgumentNullException.ThrowIfNull(scenarioTargets);
        if (result is null || result.AssertionOutcomes.IsDefaultOrEmpty)
        {
            return [];
        }

        return result.AssertionOutcomes
            .Select(outcome => CreateAssertionOutcomePresentation(outcome, scenarioTargets))
            .ToArray();
    }

    internal static bool HasAssertionOutcomes(DeterministicSimulationRunResultPackage? result) =>
        result?.AssertionOutcomes.IsDefaultOrEmpty == false;

    private static ScenarioAssertionOutcomePresentation CreateAssertionOutcomePresentation(
        DeterministicScenarioAssertionOutcome outcome,
        IReadOnlyList<SimulationScenarioTargetOption> scenarioTargets)
    {
        string status = OpenVisionLanguageService.T(
            outcome.IsPassed
                ? "Simulation.AssertionPassed"
                : "Simulation.AssertionFailed");
        string summary = outcome.Kind switch
        {
            DeterministicScenarioAssertionKind.AutomaticCycleCompleted => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.AssertionOutcomeCycle"),
                outcome.ActualValue,
                outcome.ExpectedValue,
                outcome.ObservedTickIndex),
            DeterministicScenarioAssertionKind.NoActiveFaults => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.AssertionOutcomeFaults"),
                outcome.ActualValue,
                outcome.ObservedTickIndex),
            DeterministicScenarioAssertionKind.FinalEquipmentState => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.AssertionOutcomeEquipment"),
                scenarioTargets.FirstOrDefault(target =>
                    string.Equals(target.Id, outcome.TargetId, StringComparison.Ordinal))?.Name
                    ?? outcome.TargetId,
                outcome.ActualValue,
                outcome.ExpectedValue,
                outcome.ObservedTickIndex),
            _ => outcome.Detail
        };
        return new ScenarioAssertionOutcomePresentation(outcome.IsPassed, status, summary);
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12];
}
