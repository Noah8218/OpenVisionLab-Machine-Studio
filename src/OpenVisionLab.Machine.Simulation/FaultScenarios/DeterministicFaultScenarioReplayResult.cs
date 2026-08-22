using System.Collections.Immutable;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.FaultScenarios;

public sealed record DeterministicFaultScenarioReplayResult(
    bool IsSuccess,
    string ScenarioId,
    string ScenarioName,
    long PlannedTicks,
    long ExecutedTicks,
    long PlannedActions,
    SimulationSnapshot FinalSnapshot,
    IReadOnlyList<SimulationCommandResult> CommandResults,
    IReadOnlyList<SimulationSnapshot> SnapshotHistory,
    IReadOnlyList<SimulationEvent> EventHistory,
    string? FailureReason,
    IReadOnlyList<string> ValidationErrors)
{
    public static DeterministicFaultScenarioReplayResult Success(
        string scenarioId,
        string scenarioName,
        long plannedTicks,
        long executedTicks,
        long plannedActions,
        SimulationSnapshot finalSnapshot,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<SimulationSnapshot> snapshotHistory,
        IEnumerable<SimulationEvent> eventHistory) =>
        new(
            true,
            scenarioId,
            scenarioName,
            plannedTicks,
            executedTicks,
            plannedActions,
            finalSnapshot,
            commandResults?.ToImmutableList() ?? throw new ArgumentNullException(nameof(commandResults)),
            snapshotHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(snapshotHistory)),
            eventHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(eventHistory)),
            null,
            Array.Empty<string>());

    public static DeterministicFaultScenarioReplayResult Failure(
        string scenarioId,
        string scenarioName,
        long plannedTicks,
        long executedTicks,
        long plannedActions,
        SimulationSnapshot finalSnapshot,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<SimulationSnapshot> snapshotHistory,
        IEnumerable<SimulationEvent> eventHistory,
        string failureReason,
        IReadOnlyList<string>? validationErrors = null) =>
        new(
            false,
            scenarioId,
            scenarioName,
            plannedTicks,
            executedTicks,
            plannedActions,
            finalSnapshot,
            commandResults?.ToImmutableList() ?? throw new ArgumentNullException(nameof(commandResults)),
            snapshotHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(snapshotHistory)),
            eventHistory?.ToImmutableList() ?? throw new ArgumentNullException(nameof(eventHistory)),
            failureReason,
            validationErrors ?? Array.Empty<string>());
}

