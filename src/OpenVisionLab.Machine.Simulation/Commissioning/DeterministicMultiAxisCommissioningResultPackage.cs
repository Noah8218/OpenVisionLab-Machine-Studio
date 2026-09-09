using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Scenarios;

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
        AtomicEvidenceFile.Write(fullPath, temporaryPath => File.WriteAllText(temporaryPath, SaveToJson(package)));
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
