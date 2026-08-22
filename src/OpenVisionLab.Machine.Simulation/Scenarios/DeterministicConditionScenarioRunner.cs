using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public sealed class DeterministicConditionScenarioRunner
{
    public async Task<DeterministicConditionScenarioReplayResult> ReplayAsync(
        FixedStepSimulationEngine engine,
        DeterministicConditionScenarioProfile scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var normalizedScenario = DeterministicConditionScenarioProfile.Normalize(scenario);
        var validationErrors = DeterministicConditionScenarioProfile.Validate(normalizedScenario).ToArray();
        var commandResults = new List<SimulationCommandResult>();
        var snapshotHistory = new List<SimulationSnapshot> { engine.CurrentSnapshot };
        var eventHistory = new List<SimulationEvent>();
        var conditionHistory = new List<DeterministicConditionSample>();
        var transitions = new List<DeterministicConditionTransition>();

        if (validationErrors.Length > 0)
        {
            return DeterministicConditionScenarioReplayResult.Failure(
                normalizedScenario,
                0,
                engine.CurrentSnapshot,
                commandResults,
                snapshotHistory,
                eventHistory,
                conditionHistory,
                transitions,
                ComputeEvidenceHash(
                    normalizedScenario,
                    snapshotHistory,
                    eventHistory,
                    conditionHistory,
                    transitions),
                "Scenario validation failed.",
                validationErrors);
        }

        if (engine.CurrentSnapshot.RunMode != SimulationRunMode.Paused)
        {
            var paused = await engine.EnqueueCommandAsync(new PauseCommand(), cancellationToken).ConfigureAwait(false);
            commandResults.Add(paused);
            DrainEvents(engine.EventReader, eventHistory);
            snapshotHistory.Add(engine.CurrentSnapshot);
            if (!paused.IsAccepted)
            {
                return Failure(normalizedScenario, 0, engine, commandResults, snapshotHistory, eventHistory, conditionHistory, transitions,
                    $"Pause command rejected: {paused.Detail}");
            }
        }

        var started = await engine.EnqueueCommandAsync(
            new StartConditionScenarioCommand(normalizedScenario),
            cancellationToken).ConfigureAwait(false);
        commandResults.Add(started);
        DrainEvents(engine.EventReader, eventHistory);
        snapshotHistory.Add(engine.CurrentSnapshot);
        if (!started.IsAccepted)
        {
            return Failure(
                normalizedScenario,
                0,
                engine,
                commandResults,
                snapshotHistory,
                eventHistory,
                conditionHistory,
                transitions,
                $"Start condition scenario command rejected: {started.Detail}");
        }

        var executedTicks = 0L;
        for (var tick = 0L; tick < normalizedScenario.DurationTicks; tick++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepResult = await engine.EnqueueCommandAsync(new StepCommand(), cancellationToken).ConfigureAwait(false);
            commandResults.Add(stepResult);
            DrainEvents(engine.EventReader, eventHistory);
            if (!stepResult.IsAccepted)
            {
                return Failure(normalizedScenario, executedTicks, engine, commandResults, snapshotHistory, eventHistory, conditionHistory, transitions,
                    $"Step command rejected at tick {tick}: {stepResult.Detail}");
            }

            var snapshot = engine.CurrentSnapshot;
            snapshotHistory.Add(snapshot);
            executedTicks++;
            var condition = snapshot.ConditionScenario;
            if (!condition.IsConfigured ||
                condition.ExecutedTicks != executedTicks ||
                !string.Equals(condition.TargetId, normalizedScenario.TargetId, StringComparison.Ordinal))
            {
                return Failure(
                    normalizedScenario,
                    executedTicks,
                    engine,
                    commandResults,
                    snapshotHistory,
                    eventHistory,
                    conditionHistory,
                    transitions,
                    $"Condition snapshot was inconsistent after scenario tick {tick}.");
            }

            conditionHistory.Add(new DeterministicConditionSample(
                tick,
                condition.TargetId!,
                condition.State,
                condition.HealthScore));
            if (condition.LastTransition is not null && condition.LastTransition.TickIndex == tick)
            {
                transitions.Add(condition.LastTransition);
            }
        }

        var hash = ComputeEvidenceHash(
            normalizedScenario,
            snapshotHistory,
            eventHistory,
            conditionHistory,
            transitions);
        return DeterministicConditionScenarioReplayResult.Success(
            normalizedScenario,
            executedTicks,
            engine.CurrentSnapshot,
            commandResults,
            snapshotHistory,
            eventHistory,
            conditionHistory,
            transitions,
            hash);
    }

    private static DeterministicConditionScenarioReplayResult Failure(
        DeterministicConditionScenarioProfile profile,
        long executedTicks,
        FixedStepSimulationEngine engine,
        IEnumerable<SimulationCommandResult> commandResults,
        IEnumerable<SimulationSnapshot> snapshotHistory,
        IEnumerable<SimulationEvent> eventHistory,
        IEnumerable<DeterministicConditionSample> conditionHistory,
        IEnumerable<DeterministicConditionTransition> transitions,
        string reason)
    {
        var snapshots = snapshotHistory.ToArray();
        var samples = conditionHistory.ToArray();
        var transitionList = transitions.ToArray();
        return DeterministicConditionScenarioReplayResult.Failure(
            profile,
            executedTicks,
            engine.CurrentSnapshot,
            commandResults,
            snapshots,
            eventHistory,
            samples,
            transitionList,
            ComputeEvidenceHash(profile, snapshots, eventHistory, samples, transitionList),
            reason);
    }

    private static string ComputeEvidenceHash(
        DeterministicConditionScenarioProfile profile,
        IEnumerable<SimulationSnapshot> snapshots,
        IEnumerable<SimulationEvent> events,
        IEnumerable<DeterministicConditionSample> samples,
        IEnumerable<DeterministicConditionTransition> transitions)
    {
        var builder = new StringBuilder();
        builder.Append(profile.SchemaVersion).Append('|')
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
            builder.Append("S|").Append(sample.TickIndex).Append('|')
                .Append(sample.TargetId).Append('|').Append(sample.State).Append('|')
                .Append(sample.HealthScore).Append('\n');
        }
        foreach (var transition in transitions)
        {
            builder.Append("T|").Append(transition.TickIndex).Append('|')
                .Append(transition.TargetId).Append('|').Append(transition.From).Append('|')
                .Append(transition.To).Append('|').Append(transition.Reason).Append('\n');
        }
        foreach (var snapshot in snapshots)
        {
            builder.Append("P|").Append(snapshot.TickIndex).Append('|')
                .Append(snapshot.SimulationTime.Ticks).Append('|').Append(snapshot.RunMode).Append('|')
                .Append(snapshot.ConditionScenario.IsConfigured).Append('|')
                .Append(snapshot.ConditionScenario.IsActive).Append('|')
                .Append(snapshot.ConditionScenario.ScenarioId).Append('|')
                .Append(snapshot.ConditionScenario.TargetId).Append('|')
                .Append(snapshot.ConditionScenario.Seed).Append('|')
                .Append(snapshot.ConditionScenario.ExecutedTicks).Append('|')
                .Append(snapshot.ConditionScenario.InitialState).Append('|')
                .Append(snapshot.ConditionScenario.State).Append('|')
                .Append(snapshot.ConditionScenario.HealthScore).Append('|')
                .Append(snapshot.ConditionScenario.LastTransition?.TickIndex).Append('|')
                .Append(snapshot.ConditionScenario.LastTransition?.From).Append('|')
                .Append(snapshot.ConditionScenario.LastTransition?.To).Append('\n');
        }
        foreach (var item in events)
        {
            builder.Append("E|").Append(item.EventIndex).Append('|')
                .Append(item.TickIndex).Append('|').Append(item.SimulationTime.Ticks).Append('|')
                .Append(item.Category).Append('|').Append(item.Code).Append('|')
                .Append(item.Message).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void DrainEvents(ChannelReader<SimulationEvent> reader, List<SimulationEvent> events)
    {
        while (reader.TryRead(out var item))
        {
            events.Add(item);
        }
    }
}
