using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using System.Linq;
using System.Threading.Channels;

namespace OpenVisionLab.Machine.Simulation.FaultScenarios;

public sealed class DeterministicFaultScenarioRunner
{
    public async Task<DeterministicFaultScenarioReplayResult> ReplayAsync(
        FixedStepSimulationEngine engine,
        DeterministicFaultScenarioProfile scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var normalizedScenario = DeterministicFaultScenarioProfile.Normalize(scenario);
        var validationErrors = DeterministicFaultScenarioProfile.Validate(normalizedScenario).ToArray();
        if (validationErrors.Length > 0)
        {
            return DeterministicFaultScenarioReplayResult.Failure(
                normalizedScenario.ScenarioId,
                normalizedScenario.Name,
                normalizedScenario.DurationTicks,
                0,
                0,
                engine.CurrentSnapshot,
                [],
                [engine.CurrentSnapshot],
                [],
                "Scenario validation failed.",
                validationErrors);
        }

        var commandResults = new List<SimulationCommandResult>();
        var snapshotHistory = new List<SimulationSnapshot> { engine.CurrentSnapshot };
        var eventHistory = new List<SimulationEvent>();
        var actionByTick = normalizedScenario.Actions
            .GroupBy(item => item.Tick)
            .ToDictionary(group => group.Key, group => group.ToArray());

        if (engine.CurrentSnapshot.RunMode != SimulationRunMode.Paused)
        {
            var paused = await engine.EnqueueCommandAsync(new PauseCommand(), cancellationToken).ConfigureAwait(false);
            commandResults.Add(paused);
            DrainEvents(engine.EventReader, eventHistory);
            snapshotHistory.Add(engine.CurrentSnapshot);
            if (!paused.IsAccepted)
            {
                return DeterministicFaultScenarioReplayResult.Failure(
                    normalizedScenario.ScenarioId,
                    normalizedScenario.Name,
                    normalizedScenario.DurationTicks,
                    0,
                    normalizedScenario.Actions.Count,
                    engine.CurrentSnapshot,
                    commandResults,
                    snapshotHistory,
                    eventHistory,
                    $"Pause command rejected: {paused.Detail}",
                    null);
            }
        }

        var executedTicks = 0L;
        for (var tick = 0L; tick < normalizedScenario.DurationTicks; tick++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (actionByTick.TryGetValue(tick, out var actions))
            {
                foreach (var action in actions)
                {
                    if (!action.FaultKind.IsSupported())
                    {
                        return DeterministicFaultScenarioReplayResult.Failure(
                            normalizedScenario.ScenarioId,
                            normalizedScenario.Name,
                            normalizedScenario.DurationTicks,
                            executedTicks,
                            normalizedScenario.Actions.Count,
                            engine.CurrentSnapshot,
                            commandResults,
                            snapshotHistory,
                            eventHistory,
                            $"Unsupported fault kind '{action.FaultKind}' at tick {tick}.");
                    }

                    var simulationFaultKind = action.FaultKind.ToSimulationFaultKind();
                    SimulationCommand command = action.Action == DeterministicFaultScenarioActionKind.InjectFault
                        ? new InjectSimulationFaultCommand(simulationFaultKind, action.TargetId, action.ForcedValue)
                        : new ClearSimulationFaultCommand(simulationFaultKind, action.TargetId);
                    var actionResult = await engine.EnqueueCommandAsync(command, cancellationToken)
                        .ConfigureAwait(false);
                    commandResults.Add(actionResult);
                    DrainEvents(engine.EventReader, eventHistory);
                    if (!actionResult.IsAccepted)
                    {
                        return DeterministicFaultScenarioReplayResult.Failure(
                            normalizedScenario.ScenarioId,
                            normalizedScenario.Name,
                            normalizedScenario.DurationTicks,
                            executedTicks,
                            normalizedScenario.Actions.Count,
                            engine.CurrentSnapshot,
                            commandResults,
                            snapshotHistory,
                            eventHistory,
                            $"Action command rejected at tick {tick}: {actionResult.Detail}",
                            null);
                    }
                }
            }

            var stepResult = await engine.EnqueueCommandAsync(new StepCommand(), cancellationToken).ConfigureAwait(false);
            commandResults.Add(stepResult);
            DrainEvents(engine.EventReader, eventHistory);
            if (!stepResult.IsAccepted)
            {
                return DeterministicFaultScenarioReplayResult.Failure(
                    normalizedScenario.ScenarioId,
                    normalizedScenario.Name,
                    normalizedScenario.DurationTicks,
                    executedTicks,
                    normalizedScenario.Actions.Count,
                    engine.CurrentSnapshot,
                    commandResults,
                    snapshotHistory,
                    eventHistory,
                    $"Step command rejected at tick {tick}: {stepResult.Detail}",
                    null);
            }

            snapshotHistory.Add(engine.CurrentSnapshot);
            executedTicks++;
        }

        return DeterministicFaultScenarioReplayResult.Success(
            normalizedScenario.ScenarioId,
            normalizedScenario.Name,
            normalizedScenario.DurationTicks,
            executedTicks,
            normalizedScenario.Actions.Count,
            engine.CurrentSnapshot,
            commandResults,
            snapshotHistory,
            eventHistory);
    }

    private static void DrainEvents(
        ChannelReader<SimulationEvent> reader,
        List<SimulationEvent> events)
    {
        while (reader.TryRead(out var item))
        {
            events.Add(item);
        }
    }

}
