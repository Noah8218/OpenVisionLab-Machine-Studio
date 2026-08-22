using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public sealed record DeterministicSimulationBatchDefinition(
    string BatchId,
    int RepetitionCount,
    string BuildIdentity);

public sealed record DeterministicSimulationBatchRunResult(
    int RunIndex,
    DeterministicSimulationRunResultPackage Result,
    DeterministicSimulationRunComparison ReferenceComparison);

public sealed record DeterministicSimulationBatchMismatch(
    int RunIndex,
    string Code,
    string Detail,
    string EvidenceKind,
    string TargetId,
    long ObservedTickIndex,
    string EvidenceHash);

/// <summary>
/// Portable evidence for one sequential deterministic batch. Each repetition
/// is produced by the existing single-run contract; this type owns no clock or
/// simulation engine.
/// </summary>
public sealed record DeterministicSimulationBatchResultPackage(
    int SchemaVersion,
    string BatchId,
    string BuildIdentity,
    int RequestedRuns,
    int CompletedRuns,
    bool IsComplete,
    bool IsSuccess,
    string ReferenceEvidenceHash,
    ImmutableArray<DeterministicSimulationBatchRunResult> Runs,
    DeterministicSimulationBatchMismatch? FirstMismatch,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DeterministicSimulationBatchComparison CompareTo(
        DeterministicSimulationBatchResultPackage? other)
    {
        if (other is null)
        {
            return new(false, "MissingBatch", "The comparison batch is missing.", null);
        }

        if (!string.Equals(BatchId, other.BatchId, StringComparison.Ordinal)
            || !string.Equals(BuildIdentity, other.BuildIdentity, StringComparison.Ordinal)
            || RequestedRuns != other.RequestedRuns)
        {
            return new(false, "BatchDefinitionMismatch", "Batch identity, build, or run count differs.", null);
        }

        if (CompletedRuns != other.CompletedRuns || IsComplete != other.IsComplete)
        {
            return new(false, "BatchCompletionMismatch", "Batch completion state differs.", null);
        }

        for (var index = 0; index < Math.Min(Runs.Length, other.Runs.Length); index++)
        {
            var runComparison = Runs[index].Result.CompareTo(other.Runs[index].Result);
            if (!runComparison.IsMatch)
            {
                return new(
                    false,
                    runComparison.MismatchCode ?? "RunMismatch",
                    runComparison.Detail ?? "Run evidence differs.",
                    CreateMismatch(Runs[index].RunIndex, other.Runs[index].Result, runComparison));
            }
        }

        if (Runs.Length != other.Runs.Length
            || IsSuccess != other.IsSuccess
            || !string.Equals(EvidenceHash, other.EvidenceHash, StringComparison.Ordinal))
        {
            return new(false, "BatchEvidenceMismatch", "Batch outcome or evidence hash differs.", FirstMismatch);
        }

        return new(true, null, null, null);
    }

    public bool IsEquivalentTo(DeterministicSimulationBatchResultPackage? other) =>
        CompareTo(other).IsMatch;

    public bool HasValidEvidenceHash()
    {
        var runs = Runs.IsDefault
            ? ImmutableArray<DeterministicSimulationBatchRunResult>.Empty
            : Runs;
        if (SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(BatchId)
            || RequestedRuns < 1
            || CompletedRuns != runs.Length
            || !IsComplete
            || runs.Length == 0
            || runs.Where((run, index) => run.RunIndex != index + 1).Any()
            || runs.Any(run => !run.Result.HasValidEvidenceHash()))
        {
            return false;
        }

        var firstFailure = runs.FirstOrDefault(
            run => !run.ReferenceComparison.IsMatch || !run.Result.IsSuccess);
        var expectedMismatch = firstFailure is null
            ? null
            : CreateMismatch(
                firstFailure.RunIndex,
                firstFailure.Result,
                EffectiveComparison(firstFailure));
        if (IsSuccess != (firstFailure is null) || FirstMismatch != expectedMismatch)
        {
            return false;
        }

        var definition = new DeterministicSimulationBatchDefinition(
            BatchId,
            RequestedRuns,
            BuildIdentity);
        return string.Equals(
            EvidenceHash,
            Hash(definition, ReferenceEvidenceHash, runs),
            StringComparison.Ordinal);
    }

    public bool IsForContext(
        string batchId,
        string buildIdentity,
        int repetitionCount,
        string projectId,
        string projectJson,
        TimeSpan fixedStep,
        DeterministicConditionScenarioProfile profile) =>
        HasValidEvidenceHash()
        && string.Equals(BatchId, batchId, StringComparison.Ordinal)
        && string.Equals(BuildIdentity, buildIdentity, StringComparison.Ordinal)
        && RequestedRuns == repetitionCount
        && Runs.All(run => run.Result.IsForContext(projectId, projectJson, fixedStep, profile));

    public static string SaveToJson(DeterministicSimulationBatchResultPackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static void SaveToJson(DeterministicSimulationBatchResultPackage package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException("Invalid or incomplete batch evidence cannot be saved.");
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

    public static DeterministicSimulationBatchResultPackage? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicSimulationBatchResultPackage>(
                File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static DeterministicSimulationBatchMismatch CreateMismatch(
        int runIndex,
        DeterministicSimulationRunResultPackage result,
        DeterministicSimulationRunComparison comparison)
    {
        var evidence = comparison.FirstMismatch;
        return new(
            runIndex,
            comparison.MismatchCode ?? "RunMismatch",
            comparison.Detail ?? result.FailureReason ?? "Run evidence differs.",
            evidence?.EvidenceKind ?? "Run",
            evidence?.TargetId ?? result.TargetId,
            evidence?.TickIndex ?? result.ExecutedTicks,
            result.EvidenceHash);
    }

    private static DeterministicSimulationRunComparison EffectiveComparison(
        DeterministicSimulationBatchRunResult run) =>
        run.ReferenceComparison.IsMatch
            ? new DeterministicSimulationRunComparison(
                false,
                "RunFailed",
                run.Result.FailureReason ?? "The run did not complete successfully.")
            : run.ReferenceComparison;

    internal static string Hash(
        DeterministicSimulationBatchDefinition definition,
        string referenceEvidenceHash,
        IEnumerable<DeterministicSimulationBatchRunResult> runs)
    {
        var builder = new StringBuilder()
            .Append(CurrentSchemaVersion).Append('|')
            .Append(definition.BatchId).Append('|')
            .Append(definition.BuildIdentity).Append('|')
            .Append(definition.RepetitionCount).Append('|')
            .Append(referenceEvidenceHash).Append('\n');
        foreach (var run in runs)
        {
            var mismatch = run.ReferenceComparison.FirstMismatch;
            builder.Append(run.RunIndex).Append('|')
                .Append(run.Result.EvidenceHash).Append('|')
                .Append(run.Result.IsSuccess).Append('|')
                .Append(run.Result.ExecutedTicks).Append('|')
                .Append(run.ReferenceComparison.IsMatch).Append('|')
                .Append(run.ReferenceComparison.MismatchCode).Append('|')
                .Append(run.ReferenceComparison.Detail).Append('|')
                .Append(mismatch?.TickIndex).Append('|')
                .Append(mismatch?.EvidenceKind).Append('|')
                .Append(mismatch?.TargetId).Append('|')
                .Append(mismatch?.ExpectedHash).Append('|')
                .Append(mismatch?.ActualHash).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public sealed record DeterministicSimulationBatchComparison(
    bool IsMatch,
    string? MismatchCode,
    string? Detail,
    DeterministicSimulationBatchMismatch? FirstMismatch);

/// <summary>
/// Runs one deterministic repetition at a time and compares every result with
/// either the accepted baseline or the first repetition.
/// </summary>
public sealed class DeterministicSimulationBatchRunner
{
    public async Task<DeterministicSimulationBatchResultPackage> RunAsync(
        DeterministicSimulationBatchDefinition definition,
        Func<int, CancellationToken, Task<DeterministicSimulationRunResultPackage>> runAsync,
        DeterministicSimulationRunResultPackage? acceptedBaseline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(runAsync);
        if (string.IsNullOrWhiteSpace(definition.BatchId))
        {
            throw new ArgumentException("Batch id is required.", nameof(definition));
        }

        if (definition.RepetitionCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Repetition count must be positive.");
        }

        var normalized = definition with
        {
            BatchId = definition.BatchId.Trim(),
            BuildIdentity = definition.BuildIdentity?.Trim() ?? string.Empty
        };
        var runs = ImmutableArray.CreateBuilder<DeterministicSimulationBatchRunResult>(
            normalized.RepetitionCount);
        DeterministicSimulationRunResultPackage? reference = acceptedBaseline;
        DeterministicSimulationBatchMismatch? firstMismatch = null;

        for (var runIndex = 1; runIndex <= normalized.RepetitionCount; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await runAsync(runIndex, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Batch run {runIndex} returned no result package.");
            reference ??= result;
            var comparison = reference.CompareTo(result);
            runs.Add(new DeterministicSimulationBatchRunResult(runIndex, result, comparison));

            if (firstMismatch is null && (!comparison.IsMatch || !result.IsSuccess))
            {
                var effectiveComparison = comparison.IsMatch
                    ? new DeterministicSimulationRunComparison(
                        false,
                        "RunFailed",
                        result.FailureReason ?? "The run did not complete successfully.")
                    : comparison;
                firstMismatch = DeterministicSimulationBatchResultPackage.CreateMismatch(
                    runIndex,
                    result,
                    effectiveComparison);
            }
        }

        var completedRuns = runs.ToImmutable();
        var referenceHash = reference?.EvidenceHash ?? string.Empty;
        return new DeterministicSimulationBatchResultPackage(
            DeterministicSimulationBatchResultPackage.CurrentSchemaVersion,
            normalized.BatchId,
            normalized.BuildIdentity,
            normalized.RepetitionCount,
            completedRuns.Length,
            IsComplete: true,
            IsSuccess: firstMismatch is null,
            referenceHash,
            completedRuns,
            firstMismatch,
            DeterministicSimulationBatchResultPackage.Hash(normalized, referenceHash, completedRuns));
    }
}
