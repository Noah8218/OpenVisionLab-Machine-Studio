using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

internal static class DeterministicSimulationRunEvidenceHasher
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static ImmutableArray<DeterministicSimulationTickEvidence> BuildTickEvidence(
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

    internal static string HashCommands(IEnumerable<SimulationCommandResult> commandResults)
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

    internal static string HashConditionTick(
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

    internal static string HashCondition(
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

    internal static string HashFaults(IEnumerable<SimulationSnapshot> snapshots)
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

    internal static string HashSignals(IEnumerable<SimulationSnapshot> snapshots)
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

    internal static string HashWorkpieces(IEnumerable<SimulationSnapshot> snapshots)
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

    internal static string HashSnapshots(IEnumerable<SimulationSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            builder.Append(JsonSerializer.Serialize(snapshot, SnapshotJsonOptions)).Append('\n');
        }

        return Hash(builder.ToString());
    }

    internal static string HashEvents(IEnumerable<SimulationEvent> events)
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

    internal static string HashTickEvidence(
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

    internal static string HashEvidence(
        int schemaVersion,
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
            schemaVersion,
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

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
