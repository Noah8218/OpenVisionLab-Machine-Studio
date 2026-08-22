using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Commissioning;

public sealed record DeterministicCommissioningTargetTickEvidence(
    string TargetId,
    string SnapshotHash,
    string EvidenceHash);

public sealed record DeterministicCommissioningTickEvidence(
    long TickIndex,
    string SnapshotHash,
    string EventHash,
    ImmutableArray<DeterministicCommissioningTargetTickEvidence> TargetEvidence,
    string EvidenceHash);

public sealed record DeterministicCommissioningRunResult(
    int RunIndex,
    long ExecutedTicks,
    string SnapshotHash,
    string EventHash,
    string TickEvidenceHash,
    ImmutableArray<DeterministicCommissioningTickEvidence> TickEvidence,
    string EvidenceHash,
    bool IsMatch);

public sealed record DeterministicCommissioningMismatch(
    int RunIndex,
    long TickIndex,
    string EvidenceKind,
    string TargetId,
    string ExpectedHash,
    string ActualHash);

public sealed record DeterministicMultiAxisCommissioningResultPackage(
    int SchemaVersion,
    string ProjectId,
    string ProjectName,
    string ProjectPath,
    string ProjectHash,
    long FixedStepTicks,
    string RecipeId,
    string RecipeName,
    string RecipeHash,
    int RepetitionCount,
    int CompletedRuns,
    bool IsSuccess,
    ImmutableArray<DeterministicCommissioningRunResult> Runs,
    DeterministicCommissioningMismatch? FirstMismatch,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static DeterministicMultiAxisCommissioningResultPackage Create(
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        TimeSpan fixedStep,
        MultiAxisCommissioningRecipeDefinition recipe,
        IEnumerable<DeterministicCommissioningRunResult> runs,
        DeterministicCommissioningMismatch? firstMismatch)
    {
        var runList = runs.ToImmutableArray();
        var projectHash = Hash(projectJson);
        var recipeHash = HashRecipe(recipe);
        var isSuccess = runList.Length == recipe.ValidationRepetitions
            && firstMismatch is null
            && runList.All(run => run.IsMatch);
        return new DeterministicMultiAxisCommissioningResultPackage(
            CurrentSchemaVersion,
            projectId,
            projectName,
            Path.GetFullPath(projectPath),
            projectHash,
            fixedStep.Ticks,
            recipe.Id,
            recipe.Name,
            recipeHash,
            recipe.ValidationRepetitions,
            runList.Length,
            isSuccess,
            runList,
            firstMismatch,
            HashPackage(
                projectHash,
                fixedStep.Ticks,
                recipeHash,
                recipe.ValidationRepetitions,
                runList,
                firstMismatch,
                isSuccess));
    }

    public bool IsForContext(
        string projectId,
        string projectJson,
        TimeSpan fixedStep,
        MultiAxisCommissioningRecipeDefinition recipe) =>
        HasValidEvidenceHash()
        && string.Equals(ProjectId, projectId, StringComparison.Ordinal)
        && string.Equals(ProjectHash, Hash(projectJson), StringComparison.Ordinal)
        && FixedStepTicks == fixedStep.Ticks
        && string.Equals(RecipeHash, HashRecipe(recipe), StringComparison.Ordinal)
        && RepetitionCount == recipe.ValidationRepetitions;

    public bool HasValidEvidenceHash()
    {
        var runs = Runs.IsDefault
            ? ImmutableArray<DeterministicCommissioningRunResult>.Empty
            : Runs;
        if (SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(ProjectId)
            || string.IsNullOrWhiteSpace(RecipeId)
            || FixedStepTicks <= 0
            || RepetitionCount < 2
            || CompletedRuns != runs.Length
            || runs.Any(run => !HasValidRunHash(run)))
        {
            return false;
        }

        var expectedSuccess = runs.Length == RepetitionCount
            && FirstMismatch is null
            && runs.All(run => run.IsMatch);
        return IsSuccess == expectedSuccess
            && string.Equals(
                EvidenceHash,
                HashPackage(
                    ProjectHash,
                    FixedStepTicks,
                    RecipeHash,
                    RepetitionCount,
                    runs,
                    FirstMismatch,
                    IsSuccess),
                StringComparison.Ordinal);
    }

    public static string SaveToJson(DeterministicMultiAxisCommissioningResultPackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static void SaveToJson(
        DeterministicMultiAxisCommissioningResultPackage package,
        string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException("Invalid commissioning evidence cannot be saved.");
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
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

    public static DeterministicMultiAxisCommissioningResultPackage? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicMultiAxisCommissioningResultPackage>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    internal static string HashRecipe(MultiAxisCommissioningRecipeDefinition recipe)
    {
        var builder = new StringBuilder()
            .Append(recipe.Id).Append('|')
            .Append(recipe.Name).Append('|')
            .Append(recipe.ValidationRepetitions).Append('\n');
        foreach (var target in recipe.Targets)
        {
            builder.Append(target.AxisId).Append('|')
                .Append(target.TargetPosition.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return Hash(builder.ToString());
    }

    internal static string HashSnapshots(IEnumerable<SimulationSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            builder.Append(JsonSerializer.Serialize(snapshot, JsonOptions)).Append('\n');
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

    internal static ImmutableArray<DeterministicCommissioningTickEvidence> BuildTickEvidence(
        IEnumerable<SimulationSnapshot> snapshots,
        IEnumerable<SimulationEvent> events,
        IEnumerable<string> targetIds)
    {
        var targets = targetIds.Distinct(StringComparer.Ordinal).ToArray();
        var snapshotsByTick = snapshots.GroupBy(item => item.TickIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var eventsByTick = events.GroupBy(item => item.TickIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        return snapshotsByTick.Keys.Concat(eventsByTick.Keys).Distinct().Order()
            .Select(tick =>
            {
                var snapshotHash = HashSnapshots(
                    snapshotsByTick.GetValueOrDefault(tick) ?? Array.Empty<SimulationSnapshot>());
                var eventHash = HashEvents(
                    eventsByTick.GetValueOrDefault(tick) ?? Array.Empty<SimulationEvent>());
                var targetEvidence = targets.Select(targetId =>
                {
                    var targetSnapshotHash = HashTargetSnapshots(
                        snapshotsByTick.GetValueOrDefault(tick) ?? Array.Empty<SimulationSnapshot>(),
                        targetId);
                    return new DeterministicCommissioningTargetTickEvidence(
                        targetId,
                        targetSnapshotHash,
                        Hash($"{targetId}|{targetSnapshotHash}"));
                }).ToImmutableArray();
                var targetEvidenceHash = HashTargetEvidence(targetEvidence);
                return new DeterministicCommissioningTickEvidence(
                    tick,
                    snapshotHash,
                    eventHash,
                    targetEvidence,
                    Hash($"{tick}|{snapshotHash}|{eventHash}|{targetEvidenceHash}"));
            })
            .ToImmutableArray();
    }

    internal static string HashTargetSnapshots(
        IEnumerable<SimulationSnapshot> snapshots,
        string targetId)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            var axis = snapshot.Axes.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, targetId, StringComparison.Ordinal));
            builder.Append(JsonSerializer.Serialize(axis, JsonOptions)).Append('\n');
        }
        return Hash(builder.ToString());
    }

    internal static string HashTargetEvidence(
        IEnumerable<DeterministicCommissioningTargetTickEvidence> evidence) =>
        Hash(string.Join('\n', evidence.Select(point =>
            $"{point.TargetId}|{point.EvidenceHash}")));

    internal static string HashTickEvidence(
        IEnumerable<DeterministicCommissioningTickEvidence> evidence) =>
        Hash(string.Join('\n', evidence.Select(point =>
            $"{point.TickIndex}|{point.EvidenceHash}")));

    internal static string HashRun(
        long executedTicks,
        string snapshotHash,
        string eventHash,
        string tickEvidenceHash) =>
        Hash($"{executedTicks}|{snapshotHash}|{eventHash}|{tickEvidenceHash}");

    internal static bool HasValidRunHash(DeterministicCommissioningRunResult run)
    {
        var points = run.TickEvidence.IsDefault
            ? ImmutableArray<DeterministicCommissioningTickEvidence>.Empty
            : run.TickEvidence;
        return run.RunIndex > 0
            && run.ExecutedTicks >= 0
            && points.All(point => !point.TargetEvidence.IsDefault
                && point.TargetEvidence.All(target => string.Equals(
                    target.EvidenceHash,
                    Hash($"{target.TargetId}|{target.SnapshotHash}"),
                    StringComparison.Ordinal)))
            && points.All(point => string.Equals(
                point.EvidenceHash,
                Hash($"{point.TickIndex}|{point.SnapshotHash}|{point.EventHash}|{HashTargetEvidence(point.TargetEvidence)}"),
                StringComparison.Ordinal))
            && string.Equals(run.TickEvidenceHash, HashTickEvidence(points), StringComparison.Ordinal)
            && string.Equals(
                run.EvidenceHash,
                HashRun(run.ExecutedTicks, run.SnapshotHash, run.EventHash, run.TickEvidenceHash),
                StringComparison.Ordinal);
    }

    private static string HashPackage(
        string projectHash,
        long fixedStepTicks,
        string recipeHash,
        int repetitions,
        IEnumerable<DeterministicCommissioningRunResult> runs,
        DeterministicCommissioningMismatch? mismatch,
        bool isSuccess)
    {
        var builder = new StringBuilder()
            .Append(CurrentSchemaVersion).Append('|')
            .Append(projectHash).Append('|')
            .Append(fixedStepTicks).Append('|')
            .Append(recipeHash).Append('|')
            .Append(repetitions).Append('|')
            .Append(isSuccess).Append('\n');
        foreach (var run in runs)
        {
            builder.Append(run.RunIndex).Append('|')
                .Append(run.EvidenceHash).Append('|')
                .Append(run.IsMatch).Append('\n');
        }
        if (mismatch is not null)
        {
            builder.Append(mismatch.RunIndex).Append('|')
                .Append(mismatch.TickIndex).Append('|')
                .Append(mismatch.EvidenceKind).Append('|')
                .Append(mismatch.TargetId).Append('|')
                .Append(mismatch.ExpectedHash).Append('|')
                .Append(mismatch.ActualHash);
        }
        return Hash(builder.ToString());
    }
}

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
                new ConfigureRuntimeCommand(runtime),
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
