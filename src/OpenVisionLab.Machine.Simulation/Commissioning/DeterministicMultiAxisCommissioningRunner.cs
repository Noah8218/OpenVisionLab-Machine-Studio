using System.Collections.Immutable;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Commissioning;

public sealed class DeterministicMultiAxisCommissioningRunner
{
    private const int MaximumTicks = 1_000_000;

    public async Task<DeterministicMultiAxisCommissioningResultPackage> RunAsync(
        SimulationRuntimeConfiguration runtime,
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        MultiAxisCommissioningRecipeDefinition recipe,
        TimeSpan fixedStep,
        Func<int, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.ValidationRepetitions is < 2 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recipe),
                "Validation repetitions must be between 2 and 100.");
        }
        if (recipe.Targets.Count < 2)
        {
            throw new ArgumentException("At least two commissioning targets are required.", nameof(recipe));
        }

        var runs = ImmutableArray.CreateBuilder<DeterministicCommissioningRunResult>(
            recipe.ValidationRepetitions);
        DeterministicCommissioningMismatch? firstMismatch = null;
        DeterministicCommissioningRunResult? reference = null;
        for (var runIndex = 1; runIndex <= recipe.ValidationRepetitions; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = await RunOnceAsync(runtime, recipe, fixedStep, runIndex, cancellationToken)
                .ConfigureAwait(false);
            if (reference is null)
            {
                reference = run;
            }
            else
            {
                var mismatch = CompareRuns(reference, run);
                firstMismatch ??= mismatch;
                run = run with { IsMatch = mismatch is null };
            }
            runs.Add(run);
            if (progress is not null)
            {
                await progress(runIndex).ConfigureAwait(false);
            }
        }

        return DeterministicMultiAxisCommissioningResultPackage.Create(
            projectId,
            projectName,
            projectPath,
            projectJson,
            fixedStep,
            recipe,
            runs,
            firstMismatch);
    }

    private static async Task<DeterministicCommissioningRunResult> RunOnceAsync(
        SimulationRuntimeConfiguration runtime,
        MultiAxisCommissioningRecipeDefinition recipe,
        TimeSpan fixedStep,
        int runIndex,
        CancellationToken cancellationToken)
    {
        // Commissioning owns an explicit paused single-step stream; authored
        // real-time pacing must not open a wall-clock tick window between commands.
        var singleStepRuntime = new SimulationRuntimeConfiguration(
            runtime.Axes,
            runtime.Channels,
            runtime.Sequences,
            runtime.Cameras,
            runtime.AutomaticRun,
            runtime.Layout,
            runtime.PickPlaceWorkpiece);
        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = fixedStep,
            TimeScale = 0.000001
        });
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<SimulationSnapshot>();
        try
        {
            await RequireAcceptedAsync(
                engine,
                new ConfigureRuntimeCommand(singleStepRuntime),
                cancellationToken).ConfigureAwait(false);
            await RequireAcceptedAsync(engine, new ResetCommand(), cancellationToken).ConfigureAwait(false);
            snapshots.Add(engine.CurrentSnapshot);
            await RequireAcceptedAsync(
                engine,
                new StartManualControlCommand(),
                cancellationToken).ConfigureAwait(false);
            await RequireAcceptedAsync(engine, new PauseCommand(), cancellationToken).ConfigureAwait(false);
            snapshots.Add(engine.CurrentSnapshot);
            await RequireAcceptedAsync(
                engine,
                new MoveAxesAbsoluteCommand(recipe.Targets.Select(target =>
                    new AxisMoveTarget(target.AxisId, target.TargetPosition))),
                cancellationToken).ConfigureAwait(false);
            snapshots.Add(engine.CurrentSnapshot);

            while (TargetAxes(engine.CurrentSnapshot, recipe).Any(axis => axis.State == AxisState.Moving))
            {
                if (engine.CurrentSnapshot.TickIndex >= MaximumTicks)
                {
                    throw new InvalidOperationException(
                        $"Commissioning recipe exceeded {MaximumTicks} fixed ticks.");
                }
                var before = engine.CurrentSnapshot.TickIndex;
                await RequireAcceptedAsync(engine, new StepCommand(), cancellationToken).ConfigureAwait(false);
                snapshots.Add(await WaitForSnapshotAsync(
                    engine.SnapshotReader,
                    snapshot => snapshot.TickIndex > before,
                    cancellationToken).ConfigureAwait(false));
            }

            var final = engine.CurrentSnapshot;
            if (TargetAxes(final, recipe).Any(axis =>
                    axis.State != AxisState.Idle
                    || Math.Abs(axis.Position - recipe.Targets.Single(target =>
                        string.Equals(target.AxisId, axis.Id, StringComparison.Ordinal)).TargetPosition) > 1e-9))
            {
                throw new InvalidOperationException("Commissioning axes did not reach every authored target.");
            }
            await RequireAcceptedAsync(engine, new PauseCommand(), cancellationToken).ConfigureAwait(false);
            snapshots.Add(engine.CurrentSnapshot);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var events = new List<SimulationEvent>();
        await foreach (var item in engine.EventReader.ReadAllAsync(CancellationToken.None))
        {
            events.Add(item);
        }
        var snapshotHash = DeterministicMultiAxisCommissioningResultPackage.HashSnapshots(snapshots);
        var eventHash = DeterministicMultiAxisCommissioningResultPackage.HashEvents(events);
        var tickEvidence = DeterministicMultiAxisCommissioningResultPackage.BuildTickEvidence(
            snapshots,
            events,
            recipe.Targets.Select(target => target.AxisId));
        var tickEvidenceHash = DeterministicMultiAxisCommissioningResultPackage.HashTickEvidence(
            tickEvidence);
        var executedTicks = snapshots.Max(snapshot => snapshot.TickIndex);
        return new DeterministicCommissioningRunResult(
            runIndex,
            executedTicks,
            snapshotHash,
            eventHash,
            tickEvidenceHash,
            tickEvidence,
            DeterministicMultiAxisCommissioningResultPackage.HashRun(
                executedTicks,
                snapshotHash,
                eventHash,
                tickEvidenceHash),
            true);
    }

    private static IReadOnlyList<AxisSnapshot> TargetAxes(
        SimulationSnapshot snapshot,
        MultiAxisCommissioningRecipeDefinition recipe) =>
        recipe.Targets.Select(target => snapshot.Axes.Single(axis =>
            string.Equals(axis.Id, target.AxisId, StringComparison.Ordinal))).ToArray();

    private static async Task RequireAcceptedAsync(
        FixedStepSimulationEngine engine,
        SimulationCommand command,
        CancellationToken cancellationToken)
    {
        var result = await engine.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.IsAccepted)
        {
            throw new InvalidOperationException(
                $"{command.GetType().Name} was rejected: {result.ErrorCode}: {result.Detail}");
        }
    }

    private static async Task<SimulationSnapshot> WaitForSnapshotAsync(
        ChannelReader<SimulationSnapshot> reader,
        Func<SimulationSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        await foreach (var snapshot in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (predicate(snapshot))
            {
                return snapshot;
            }
        }
        throw new InvalidOperationException("The simulation snapshot stream ended unexpectedly.");
    }

    internal static DeterministicCommissioningMismatch? CompareRuns(
        DeterministicCommissioningRunResult expected,
        DeterministicCommissioningRunResult actual)
    {
        var expectedByTick = expected.TickEvidence.ToDictionary(point => point.TickIndex);
        var actualByTick = actual.TickEvidence.ToDictionary(point => point.TickIndex);
        foreach (var tick in expectedByTick.Keys.Concat(actualByTick.Keys).Distinct().Order())
        {
            expectedByTick.TryGetValue(tick, out var expectedPoint);
            actualByTick.TryGetValue(tick, out var actualPoint);
            if (expectedPoint is null || actualPoint is null)
            {
                var expectedEndpoint = expectedPoint ?? expected.TickEvidence.LastOrDefault();
                var actualEndpoint = actualPoint ?? actual.TickEvidence.LastOrDefault();
                var changedTargetId = FirstChangedTargetId(expectedEndpoint, actualEndpoint);
                return new DeterministicCommissioningMismatch(
                    actual.RunIndex,
                    tick,
                    string.IsNullOrWhiteSpace(changedTargetId) ? "Tick" : "Snapshot",
                    changedTargetId,
                    expectedPoint?.EvidenceHash ?? string.Empty,
                    actualPoint?.EvidenceHash ?? string.Empty);
            }
            var expectedTargets = expectedPoint.TargetEvidence.ToDictionary(
                point => point.TargetId,
                StringComparer.Ordinal);
            var actualTargets = actualPoint.TargetEvidence.ToDictionary(
                point => point.TargetId,
                StringComparer.Ordinal);
            foreach (var targetId in expectedTargets.Keys.Concat(actualTargets.Keys)
                         .Distinct(StringComparer.Ordinal))
            {
                expectedTargets.TryGetValue(targetId, out var expectedTarget);
                actualTargets.TryGetValue(targetId, out var actualTarget);
                if (expectedTarget is null
                    || actualTarget is null
                    || !string.Equals(
                        expectedTarget.SnapshotHash,
                        actualTarget.SnapshotHash,
                        StringComparison.Ordinal))
                {
                    return new DeterministicCommissioningMismatch(
                        actual.RunIndex,
                        tick,
                        "Snapshot",
                        targetId,
                        expectedTarget?.SnapshotHash ?? string.Empty,
                        actualTarget?.SnapshotHash ?? string.Empty);
                }
            }
            if (!string.Equals(expectedPoint.SnapshotHash, actualPoint.SnapshotHash, StringComparison.Ordinal))
            {
                return new DeterministicCommissioningMismatch(
                    actual.RunIndex,
                    tick,
                    "Snapshot",
                    string.Empty,
                    expectedPoint.SnapshotHash,
                    actualPoint.SnapshotHash);
            }
            if (!string.Equals(expectedPoint.EventHash, actualPoint.EventHash, StringComparison.Ordinal))
            {
                return new DeterministicCommissioningMismatch(
                    actual.RunIndex,
                    tick,
                    "Event",
                    FirstChangedTargetId(expected.TickEvidence, actual.TickEvidence),
                    expectedPoint.EventHash,
                    actualPoint.EventHash);
            }
        }

        return expected.ExecutedTicks == actual.ExecutedTicks
            && string.Equals(expected.EvidenceHash, actual.EvidenceHash, StringComparison.Ordinal)
            ? null
            : new DeterministicCommissioningMismatch(
                actual.RunIndex,
                Math.Max(expected.ExecutedTicks, actual.ExecutedTicks),
                "Result",
                string.Empty,
                expected.EvidenceHash,
                actual.EvidenceHash);
    }

    private static string FirstChangedTargetId(
        DeterministicCommissioningTickEvidence? expected,
        DeterministicCommissioningTickEvidence? actual)
    {
        if (expected is null || actual is null)
        {
            return string.Empty;
        }
        var expectedTargets = expected.TargetEvidence.ToDictionary(
            point => point.TargetId,
            StringComparer.Ordinal);
        var actualTargets = actual.TargetEvidence.ToDictionary(
            point => point.TargetId,
            StringComparer.Ordinal);
        return expectedTargets.Keys.Concat(actualTargets.Keys)
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(targetId =>
                !expectedTargets.TryGetValue(targetId, out var expectedTarget)
                || !actualTargets.TryGetValue(targetId, out var actualTarget)
                || !string.Equals(
                    expectedTarget.SnapshotHash,
                    actualTarget.SnapshotHash,
                    StringComparison.Ordinal))
            ?? string.Empty;
    }

    private static string FirstChangedTargetId(
        IEnumerable<DeterministicCommissioningTickEvidence> expected,
        IEnumerable<DeterministicCommissioningTickEvidence> actual)
    {
        var expectedByTick = expected.ToDictionary(point => point.TickIndex);
        var actualByTick = actual.ToDictionary(point => point.TickIndex);
        foreach (var tick in expectedByTick.Keys.Concat(actualByTick.Keys).Distinct().Order())
        {
            expectedByTick.TryGetValue(tick, out var expectedPoint);
            actualByTick.TryGetValue(tick, out var actualPoint);
            var targetId = FirstChangedTargetId(
                expectedPoint ?? expected.LastOrDefault(),
                actualPoint ?? actual.LastOrDefault());
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                return targetId;
            }
        }
        return string.Empty;
    }
}
