using System.Collections.Immutable;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public sealed record DeterministicConditionScenarioReplayResult(
    bool IsSuccess,
    string ScenarioId,
    string ScenarioName,
    string TargetId,
    long PlannedTicks,
    long ExecutedTicks,
    SimulationSnapshot FinalSnapshot,
    IReadOnlyList<SimulationCommandResult> CommandResults,
    IReadOnlyList<SimulationSnapshot> SnapshotHistory,
    IReadOnlyList<SimulationEvent> EventHistory,
    IReadOnlyList<DeterministicConditionSample> ConditionHistory,
    IReadOnlyList<DeterministicConditionTransition> Transitions,
    string EvidenceHash,
    string? FailureReason,
    IReadOnlyList<string> ValidationErrors)
{
    public static DeterministicConditionScenarioReplayResult Success(
        DeterministicConditionScenarioProfile profile,
        long executedTicks,
        SimulationSnapshot finalSnapshot,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<SimulationSnapshot> snapshotHistory,
        IEnumerable<SimulationEvent> eventHistory,
        IEnumerable<DeterministicConditionSample> conditionHistory,
        IEnumerable<DeterministicConditionTransition> transitions,
        string evidenceHash) =>
        Create(
            true,
            profile,
            executedTicks,
            finalSnapshot,
            commandResults,
            snapshotHistory,
            eventHistory,
            conditionHistory,
            transitions,
            evidenceHash,
            null,
            Array.Empty<string>());

    public static DeterministicConditionScenarioReplayResult Failure(
        DeterministicConditionScenarioProfile profile,
        long executedTicks,
        SimulationSnapshot finalSnapshot,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<SimulationSnapshot> snapshotHistory,
        IEnumerable<SimulationEvent> eventHistory,
        IEnumerable<DeterministicConditionSample> conditionHistory,
        IEnumerable<DeterministicConditionTransition> transitions,
        string evidenceHash,
        string failureReason,
        IReadOnlyList<string>? validationErrors = null) =>
        Create(
            false,
            profile,
            executedTicks,
            finalSnapshot,
            commandResults,
            snapshotHistory,
            eventHistory,
            conditionHistory,
            transitions,
            evidenceHash,
            failureReason,
            validationErrors ?? Array.Empty<string>());

    private static DeterministicConditionScenarioReplayResult Create(
        bool isSuccess,
        DeterministicConditionScenarioProfile profile,
        long executedTicks,
        SimulationSnapshot finalSnapshot,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<SimulationSnapshot> snapshotHistory,
        IEnumerable<SimulationEvent> eventHistory,
        IEnumerable<DeterministicConditionSample> conditionHistory,
        IEnumerable<DeterministicConditionTransition> transitions,
        string evidenceHash,
        string? failureReason,
        IReadOnlyList<string> validationErrors) =>
        new(
            isSuccess,
            profile.ScenarioId,
            profile.Name,
            profile.TargetId,
            profile.DurationTicks,
            executedTicks,
            finalSnapshot,
            commandResults?.ToImmutableList() ?? throw new ArgumentNullException(nameof(commandResults)),
            snapshotHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(snapshotHistory)),
            eventHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(eventHistory)),
            conditionHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(conditionHistory)),
            transitions?.ToImmutableList() ?? throw new ArgumentNullException(nameof(transitions)),
            evidenceHash,
            failureReason,
            validationErrors.ToImmutableList());
}
