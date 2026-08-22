using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

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
        var projectHash = Hash(projectJson ?? string.Empty);
        var commandHash = HashCommands(commandList);
        var conditionHash = HashCondition(normalized, samples, transitionList);
        var faultHash = HashFaults(snapshotList);
        var workpieceHash = HashWorkpieces(snapshotList);
        var signalHash = HashSignals(snapshotList);
        var snapshotHash = HashSnapshots(snapshotList);
        var eventHash = HashEvents(eventList);
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
        var tickEvidence = BuildTickEvidence(
            normalized.TargetId,
            commandList,
            samples,
            transitionList,
            snapshotList,
            eventList);
        var tickEvidenceHash = HashTickEvidence(tickEvidence);
        var evidenceHash = HashEvidence(
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
        DeterministicSimulationRunResultPackage? other)
    {
        if (other is null)
        {
            return new(false, "MissingResult", "The comparison package is missing.");
        }

        if (SchemaVersion != other.SchemaVersion)
        {
            return new(false, "SchemaMismatch", "Result package schemas differ.");
        }

        if (!string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal)
            || !string.Equals(ProjectHash, other.ProjectHash, StringComparison.Ordinal))
        {
            return new(false, "ProjectMismatch", "Project identity or content hash differs.");
        }

        if (FixedStepTicks != other.FixedStepTicks
            || !string.Equals(ScenarioId, other.ScenarioId, StringComparison.Ordinal)
            || !string.Equals(TargetId, other.TargetId, StringComparison.Ordinal)
            || Seed != other.Seed
            || PlannedTicks != other.PlannedTicks)
        {
            return new(false, "ScenarioMismatch", "Scenario identity, seed, duration, or fixed step differs.");
        }

        var tickComparison = CompareTickEvidence(other);
        if (tickComparison is not null)
        {
            return tickComparison;
        }

        if (!string.Equals(CommandHash, other.CommandHash, StringComparison.Ordinal))
        {
            return new(false, "CommandHashMismatch", "Command result history hash differs.");
        }

        if (!string.Equals(ConditionHash, other.ConditionHash, StringComparison.Ordinal))
        {
            return new(false, "ConditionHashMismatch", "Condition history hash differs.");
        }

        if (!string.Equals(FaultHash, other.FaultHash, StringComparison.Ordinal))
        {
            return new(false, "FaultHashMismatch", "Fault history hash differs.");
        }

        if (!string.Equals(WorkpieceHash, other.WorkpieceHash, StringComparison.Ordinal))
        {
            return new(false, "WorkpieceHashMismatch", "Workpiece history hash differs.");
        }

        if (!string.Equals(SignalHash, other.SignalHash, StringComparison.Ordinal))
        {
            return new(false, "SignalHashMismatch", "Signal history hash differs.");
        }

        if (!string.Equals(SnapshotHash, other.SnapshotHash, StringComparison.Ordinal))
        {
            return new(false, "SnapshotHashMismatch", "Snapshot history hash differs.");
        }

        if (!string.Equals(EventHash, other.EventHash, StringComparison.Ordinal))
        {
            return new(false, "EventHashMismatch", "Event history hash differs.");
        }

        if (!string.Equals(AssertionDefinitionHash, other.AssertionDefinitionHash, StringComparison.Ordinal))
        {
            return new(false, "AssertionDefinitionMismatch", "Scenario assertion definitions differ.");
        }

        if (!string.Equals(AssertionOutcomeHash, other.AssertionOutcomeHash, StringComparison.Ordinal))
        {
            return new(false, "AssertionOutcomeMismatch", "Scenario assertion outcomes differ.");
        }

        if (!string.Equals(EvidenceHash, other.EvidenceHash, StringComparison.Ordinal)
            || IsSuccess != other.IsSuccess
            || ExecutedTicks != other.ExecutedTicks)
        {
            return new(false, "ResultMismatch", "Run outcome or combined evidence differs.");
        }

        return new(true, null, null);
    }

    private DeterministicSimulationRunComparison? CompareTickEvidence(
        DeterministicSimulationRunResultPackage other)
    {
        var expected = TickEvidence.IsDefault
            ? ImmutableArray<DeterministicSimulationTickEvidence>.Empty
            : TickEvidence;
        var actual = other.TickEvidence.IsDefault
            ? ImmutableArray<DeterministicSimulationTickEvidence>.Empty
            : other.TickEvidence;
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

        var tickEvidenceHash = HashTickEvidence(TickEvidence);
        var assertionDefinitionHash = DeterministicScenarioAssertionEvaluator.HashDefinitions(
            AssertionOutcomes);
        var assertionOutcomeHash = DeterministicScenarioAssertionEvaluator.HashOutcomes(
            AssertionOutcomes);
        var evidenceHash = HashEvidence(
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
            && string.Equals(ProjectHash, Hash(projectJson ?? string.Empty), StringComparison.Ordinal)
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

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, SaveToJson(package));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ImmutableArray<DeterministicSimulationTickEvidence> BuildTickEvidence(
        string targetId,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<DeterministicConditionSample> samples,
        IEnumerable<DeterministicConditionTransition> transitions,
        IEnumerable<SimulationSnapshot> snapshots,
        IEnumerable<SimulationEvent> events)
    {
        var commandsByTick = commandResults
            .GroupBy(result => result.AppliedTick)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var samplesByTick = samples
            .GroupBy(sample => sample.TickIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var transitionsByTick = transitions
            .GroupBy(transition => transition.TickIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var snapshotsByTick = snapshots
            .GroupBy(snapshot => snapshot.TickIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var eventsByTick = events
            .GroupBy(item => item.TickIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var ticks = commandsByTick.Keys
            .Concat(samplesByTick.Keys)
            .Concat(transitionsByTick.Keys)
            .Concat(snapshotsByTick.Keys)
            .Concat(eventsByTick.Keys)
            .Distinct()
            .Order()
            .ToArray();
        var evidence = ImmutableArray.CreateBuilder<DeterministicSimulationTickEvidence>(ticks.Length);
        foreach (var tick in ticks)
        {
            var commandHash = HashCommands(
                commandsByTick.GetValueOrDefault(tick) ?? Array.Empty<SimulationCommandResult>());
            var conditionHash = HashConditionTick(
                samplesByTick.GetValueOrDefault(tick) ?? Array.Empty<DeterministicConditionSample>(),
                transitionsByTick.GetValueOrDefault(tick) ?? Array.Empty<DeterministicConditionTransition>());
            var tickSnapshots = snapshotsByTick.GetValueOrDefault(tick)
                ?? Array.Empty<SimulationSnapshot>();
            var faultHash = HashFaults(tickSnapshots);
            var workpieceHash = HashWorkpieces(tickSnapshots);
            var signalHash = HashSignals(tickSnapshots);
            var snapshotHash = HashSnapshots(tickSnapshots);
            var eventHash = HashEvents(
                eventsByTick.GetValueOrDefault(tick) ?? Array.Empty<SimulationEvent>());
            var evidenceHash = Hash(string.Join(
                "|",
                tick,
                targetId,
                commandHash,
                conditionHash,
                faultHash,
                workpieceHash,
                signalHash,
                snapshotHash,
                eventHash));
            evidence.Add(new DeterministicSimulationTickEvidence(
                tick,
                targetId,
                commandHash,
                conditionHash,
                faultHash,
                workpieceHash,
                signalHash,
                snapshotHash,
                eventHash,
                evidenceHash));
        }

        return evidence.ToImmutable();
    }

    private static string HashCommands(IEnumerable<SimulationCommandResult> commandResults)
    {
        var builder = new StringBuilder();
        foreach (var result in commandResults)
        {
            builder.Append(result.AppliedTick).Append('|')
                .Append(result.SimulationTime.Ticks).Append('|')
                .Append(result.IsAccepted).Append('|')
                .Append(result.ErrorCode).Append('|')
                .Append(result.Detail).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string HashConditionTick(
        IEnumerable<DeterministicConditionSample> samples,
        IEnumerable<DeterministicConditionTransition> transitions)
    {
        var builder = new StringBuilder();
        foreach (var sample in samples)
        {
            builder.Append(sample.TickIndex).Append('|')
                .Append(sample.TargetId).Append('|')
                .Append(sample.State).Append('|')
                .Append(sample.HealthScore).Append('\n');
        }

        foreach (var transition in transitions)
        {
            builder.Append(transition.TickIndex).Append('|')
                .Append(transition.TargetId).Append('|')
                .Append(transition.From).Append('|')
                .Append(transition.To).Append('|')
                .Append(transition.Reason).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string HashCondition(
        DeterministicConditionScenarioProfile profile,
        IEnumerable<DeterministicConditionSample> samples,
        IEnumerable<DeterministicConditionTransition> transitions)
    {
        var builder = new StringBuilder()
            .Append(profile.SchemaVersion).Append('|')
            .Append(profile.ScenarioId).Append('|')
            .Append(profile.TargetId).Append('|')
            .Append(profile.Seed).Append('|')
            .Append(profile.DurationTicks).Append('|')
            .Append(profile.MinimumStateTicks).Append('|')
            .Append(profile.JitterTicks).Append('|')
            .Append(profile.InitialState).Append('|')
            .Append(profile.FaultRecovery?.FaultKind).Append('|')
            .Append(profile.FaultRecovery?.TargetId).Append('|')
            .Append(profile.FaultRecovery?.ForcedValue).Append('|')
            .Append(profile.FaultRecovery?.InjectTick).Append('|')
            .Append(profile.FaultRecovery?.HoldTicks).Append('|')
            .Append(profile.FaultRecovery?.RestartSequenceId).Append('\n');
        foreach (var sample in samples)
        {
            builder.Append(sample.TickIndex).Append('|')
                .Append(sample.TargetId).Append('|')
                .Append(sample.State).Append('|')
                .Append(sample.HealthScore).Append('\n');
        }

        foreach (var transition in transitions)
        {
            builder.Append(transition.TickIndex).Append('|')
                .Append(transition.TargetId).Append('|')
                .Append(transition.From).Append('|')
                .Append(transition.To).Append('|')
                .Append(transition.Reason).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string HashFaults(IEnumerable<SimulationSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            foreach (var fault in snapshot.Faults)
            {
                builder.Append(snapshot.TickIndex).Append('|')
                    .Append(fault.Kind).Append('|')
                    .Append(fault.TargetId).Append('|')
                    .Append(fault.ForcedValue).Append('|')
                    .Append(fault.ActivatedTick).Append('|')
                    .Append(fault.ActivatedTime.Ticks).Append('\n');
            }
        }

        return Hash(builder.ToString());
    }

    private static string HashSignals(IEnumerable<SimulationSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            builder.Append(snapshot.TickIndex).Append('|')
                .Append(snapshot.SignalRevision).Append('\n');
            foreach (var signal in snapshot.Signals.OrderBy(signal => signal.Id, StringComparer.Ordinal))
            {
                builder.Append(signal.Id).Append('|')
                    .Append(signal.Kind).Append('|')
                    .Append(signal.Value).Append('\n');
            }
        }

        return Hash(builder.ToString());
    }

    private static string HashWorkpieces(IEnumerable<SimulationSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            foreach (var workpiece in snapshot.Workpieces.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                builder.Append(snapshot.TickIndex).Append('|')
                    .Append(JsonSerializer.Serialize(workpiece, SnapshotJsonOptions)).Append('\n');
            }
        }

        return Hash(builder.ToString());
    }

    private static string HashSnapshots(IEnumerable<SimulationSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            builder.Append(JsonSerializer.Serialize(snapshot, SnapshotJsonOptions)).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string HashEvents(IEnumerable<SimulationEvent> events)
    {
        var builder = new StringBuilder();
        foreach (var item in events)
        {
            builder.Append(item.EventIndex).Append('|')
                .Append(item.TickIndex).Append('|')
                .Append(item.SimulationTime.Ticks).Append('|')
                .Append(item.Category).Append('|')
                .Append(item.Code).Append('|')
                .Append(item.Message).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string HashTickEvidence(
        IEnumerable<DeterministicSimulationTickEvidence> tickEvidence)
    {
        var builder = new StringBuilder();
        foreach (var point in tickEvidence)
        {
            builder.Append(point.TickIndex).Append('|')
                .Append(point.TargetId).Append('|')
                .Append(point.EvidenceHash).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string HashEvidence(
        string projectHash,
        long fixedStepTicks,
        string scenarioId,
        string targetId,
        int seed,
        long plannedTicks,
        long executedTicks,
        bool isSuccess,
        string? failureReason,
        string commandHash,
        string conditionHash,
        string faultHash,
        string workpieceHash,
        string signalHash,
        string snapshotHash,
        string eventHash,
        string assertionDefinitionHash,
        string assertionOutcomeHash,
        string tickEvidenceHash) =>
        Hash(string.Join(
            "|",
            CurrentSchemaVersion,
            projectHash,
            fixedStepTicks,
            scenarioId,
            targetId,
            seed,
            plannedTicks,
            executedTicks,
            isSuccess,
            failureReason ?? string.Empty,
            commandHash,
            conditionHash,
            faultHash,
            workpieceHash,
            signalHash,
            snapshotHash,
            eventHash,
            assertionDefinitionHash,
            assertionOutcomeHash,
            tickEvidenceHash));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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
