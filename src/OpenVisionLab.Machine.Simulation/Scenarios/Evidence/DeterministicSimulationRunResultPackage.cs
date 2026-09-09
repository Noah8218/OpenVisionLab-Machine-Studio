using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Portable evidence for one deterministic simulation run. The package is
/// derived from one immutable snapshot/event history; it does not own a clock
/// or execute a second runtime.
/// </summary>
public sealed record DeterministicSimulationRunResultPackage(
    int SchemaVersion,
    string ProjectId,
    string ProjectName,
    string ProjectPath,
    string ProjectHash,
    long FixedStepTicks,
    string ScenarioId,
    string ScenarioName,
    string TargetId,
    int Seed,
    long PlannedTicks,
    long ExecutedTicks,
    bool IsSuccess,
    string CommandHash,
    string ConditionHash,
    string FaultHash,
    string WorkpieceHash,
    string SignalHash,
    string SnapshotHash,
    string EventHash,
    string AssertionDefinitionHash,
    string AssertionOutcomeHash,
    ImmutableArray<DeterministicScenarioAssertionOutcome> AssertionOutcomes,
    string TickEvidenceHash,
    ImmutableArray<DeterministicSimulationTickEvidence> TickEvidence,
    string EvidenceHash,
    string? FailureReason)
{
    public const int CurrentSchemaVersion = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly DeterministicSimulationRunResultComparer ResultComparer = new();

    public static DeterministicSimulationRunResultPackage FromReplay(
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        TimeSpan fixedStep,
        DeterministicConditionScenarioProfile profile,
        DeterministicConditionScenarioReplayResult replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var normalized = DeterministicConditionScenarioProfile.Normalize(profile);
        return Create(
            projectId,
            projectName,
            projectPath,
            projectJson,
            fixedStep,
            normalized,
            replay.IsSuccess,
            replay.ExecutedTicks,
            replay.CommandResults,
            replay.ConditionHistory,
            replay.Transitions,
            replay.SnapshotHistory,
            replay.EventHistory,
            replay.FailureReason);
    }

    public static DeterministicSimulationRunResultPackage Create(
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        TimeSpan fixedStep,
        DeterministicConditionScenarioProfile profile,
        bool isSuccess,
        long executedTicks,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<DeterministicConditionSample> conditionHistory,
        IEnumerable<DeterministicConditionTransition> transitions,
        IEnumerable<SimulationSnapshot> snapshots,
        IEnumerable<SimulationEvent> events,
        string? failureReason = null)
    {
        ArgumentNullException.ThrowIfNull(commandResults);
        ArgumentNullException.ThrowIfNull(conditionHistory);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(events);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStep), "Fixed step must be positive.");
        }

        var normalized = DeterministicConditionScenarioProfile.Normalize(profile);
        var commandList = commandResults.ToImmutableArray();
        var samples = conditionHistory.ToImmutableArray();
        var transitionList = transitions.ToImmutableArray();
        var snapshotList = snapshots.ToImmutableArray();
        var eventList = events.ToImmutableArray();
        var projectHash = DeterministicSimulationRunEvidenceHasher.Hash(projectJson ?? string.Empty);
        var commandHash = DeterministicSimulationRunEvidenceHasher.HashCommands(commandList);
        var conditionHash = DeterministicSimulationRunEvidenceHasher.HashCondition(
            normalized,
            samples,
            transitionList);
        var faultHash = DeterministicSimulationRunEvidenceHasher.HashFaults(snapshotList);
        var workpieceHash = DeterministicSimulationRunEvidenceHasher.HashWorkpieces(snapshotList);
        var signalHash = DeterministicSimulationRunEvidenceHasher.HashSignals(snapshotList);
        var snapshotHash = DeterministicSimulationRunEvidenceHasher.HashSnapshots(snapshotList);
        var eventHash = DeterministicSimulationRunEvidenceHasher.HashEvents(eventList);
        var assertionOutcomes = DeterministicScenarioAssertionEvaluator.Evaluate(
            normalized.Assertions,
            snapshotList,
            eventList);
        var assertionDefinitionHash = DeterministicScenarioAssertionEvaluator.HashDefinitions(
            normalized.Assertions);
        var assertionOutcomeHash = DeterministicScenarioAssertionEvaluator.HashOutcomes(
            assertionOutcomes);
        bool assertionsPassed = assertionOutcomes.All(outcome => outcome.IsPassed);
        bool effectiveSuccess = isSuccess && assertionsPassed;
        string? effectiveFailureReason = isSuccess && !assertionsPassed
            ? $"Scenario assertions failed: {string.Join(", ", assertionOutcomes.Where(outcome => !outcome.IsPassed).Select(outcome => outcome.AssertionId))}."
            : failureReason;
        var tickEvidence = DeterministicSimulationRunEvidenceHasher.BuildTickEvidence(
            normalized.TargetId,
            commandList,
            samples,
            transitionList,
            snapshotList,
            eventList);
        var tickEvidenceHash = DeterministicSimulationRunEvidenceHasher.HashTickEvidence(tickEvidence);
        var evidenceHash = DeterministicSimulationRunEvidenceHasher.HashEvidence(
            CurrentSchemaVersion,
            projectHash,
            fixedStep.Ticks,
            normalized.ScenarioId,
            normalized.TargetId,
            normalized.Seed,
            normalized.DurationTicks,
            executedTicks,
            effectiveSuccess,
            effectiveFailureReason,
            commandHash,
            conditionHash,
            faultHash,
            workpieceHash,
            signalHash,
            snapshotHash,
            eventHash,
            assertionDefinitionHash,
            assertionOutcomeHash,
            tickEvidenceHash);

        return new DeterministicSimulationRunResultPackage(
            CurrentSchemaVersion,
            projectId.Trim(),
            projectName?.Trim() ?? string.Empty,
            Path.GetFullPath(projectPath),
            projectHash,
            fixedStep.Ticks,
            normalized.ScenarioId,
            normalized.Name,
            normalized.TargetId,
            normalized.Seed,
            normalized.DurationTicks,
            executedTicks,
            effectiveSuccess,
            commandHash,
            conditionHash,
            faultHash,
            workpieceHash,
            signalHash,
            snapshotHash,
            eventHash,
            assertionDefinitionHash,
            assertionOutcomeHash,
            assertionOutcomes,
            tickEvidenceHash,
            tickEvidence,
            evidenceHash,
            effectiveFailureReason);
    }

    public DeterministicSimulationRunComparison CompareTo(
        DeterministicSimulationRunResultPackage? other) =>
        ResultComparer.Compare(this, other);

    public bool IsEquivalentTo(DeterministicSimulationRunResultPackage? other) =>
        CompareTo(other).IsMatch;

    public bool HasValidEvidenceHash()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(ProjectId)
            || string.IsNullOrWhiteSpace(ScenarioId)
            || string.IsNullOrWhiteSpace(TargetId)
            || FixedStepTicks <= 0
            || PlannedTicks <= 0
            || ExecutedTicks < 0
            || AssertionOutcomes.IsDefault
            || TickEvidence.IsDefault)
        {
            return false;
        }

        var tickEvidenceHash = DeterministicSimulationRunEvidenceHasher.HashTickEvidence(TickEvidence);
        var assertionDefinitionHash = DeterministicScenarioAssertionEvaluator.HashDefinitions(
            AssertionOutcomes);
        var assertionOutcomeHash = DeterministicScenarioAssertionEvaluator.HashOutcomes(
            AssertionOutcomes);
        var evidenceHash = DeterministicSimulationRunEvidenceHasher.HashEvidence(
            CurrentSchemaVersion,
            ProjectHash,
            FixedStepTicks,
            ScenarioId,
            TargetId,
            Seed,
            PlannedTicks,
            ExecutedTicks,
            IsSuccess,
            FailureReason,
            CommandHash,
            ConditionHash,
            FaultHash,
            WorkpieceHash,
            SignalHash,
            SnapshotHash,
            EventHash,
            assertionDefinitionHash,
            assertionOutcomeHash,
            tickEvidenceHash);
        return !(IsSuccess && AssertionOutcomes.Any(outcome => !outcome.IsPassed))
            && string.Equals(AssertionDefinitionHash, assertionDefinitionHash, StringComparison.Ordinal)
            && string.Equals(AssertionOutcomeHash, assertionOutcomeHash, StringComparison.Ordinal)
            && string.Equals(TickEvidenceHash, tickEvidenceHash, StringComparison.Ordinal)
            && string.Equals(EvidenceHash, evidenceHash, StringComparison.Ordinal);
    }

    public bool IsForContext(
        string projectId,
        string projectJson,
        TimeSpan fixedStep,
        DeterministicConditionScenarioProfile profile)
    {
        var normalized = DeterministicConditionScenarioProfile.Normalize(profile);
        return HasValidEvidenceHash()
            && string.Equals(ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(
                ProjectHash,
                DeterministicSimulationRunEvidenceHasher.Hash(projectJson ?? string.Empty),
                StringComparison.Ordinal)
            && FixedStepTicks == fixedStep.Ticks
            && string.Equals(ScenarioId, normalized.ScenarioId, StringComparison.Ordinal)
            && string.Equals(TargetId, normalized.TargetId, StringComparison.Ordinal)
            && Seed == normalized.Seed
            && PlannedTicks == normalized.DurationTicks
            && string.Equals(
                AssertionDefinitionHash,
                DeterministicScenarioAssertionEvaluator.HashDefinitions(normalized.Assertions),
                StringComparison.Ordinal);
    }

    public static string SaveToJson(DeterministicSimulationRunResultPackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static DeterministicSimulationRunResultPackage? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicSimulationRunResultPackage>(
                File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void SaveToJson(DeterministicSimulationRunResultPackage package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException("Invalid run evidence cannot be saved as a baseline.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicEvidenceFile.Write(fullPath, temporaryPath => File.WriteAllText(temporaryPath, SaveToJson(package)));
    }

}

public sealed record DeterministicSimulationTickEvidence(
    long TickIndex,
    string TargetId,
    string CommandHash,
    string ConditionHash,
    string FaultHash,
    string WorkpieceHash,
    string SignalHash,
    string SnapshotHash,
    string EventHash,
    string EvidenceHash);

public sealed record DeterministicSimulationEvidenceMismatch(
    long TickIndex,
    string EvidenceKind,
    string TargetId,
    string ExpectedHash,
    string ActualHash);

public sealed record DeterministicSimulationRunComparison(
    bool IsMatch,
    string? MismatchCode,
    string? Detail,
    DeterministicSimulationEvidenceMismatch? FirstMismatch = null);
