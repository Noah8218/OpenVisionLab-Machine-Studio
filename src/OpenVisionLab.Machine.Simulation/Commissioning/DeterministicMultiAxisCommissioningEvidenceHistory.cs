using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Simulation.Commissioning;

public sealed record DeterministicCommissioningBaselineComparison(
    bool IsMatch,
    string? MismatchCode,
    DeterministicCommissioningMismatch? FirstMismatch);

public sealed record DeterministicMultiAxisCommissioningBaseline(
    int SchemaVersion,
    string ProjectId,
    string ProjectHash,
    long FixedStepTicks,
    string RecipeId,
    string RecipeHash,
    DeterministicCommissioningRunResult ReferenceRun,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static DeterministicMultiAxisCommissioningBaseline FromResult(
        DeterministicMultiAxisCommissioningResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash() || !package.IsSuccess || package.Runs.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("Only successful commissioning evidence can be accepted.");
        }

        var reference = package.Runs[0];
        return new DeterministicMultiAxisCommissioningBaseline(
            CurrentSchemaVersion,
            package.ProjectId,
            package.ProjectHash,
            package.FixedStepTicks,
            package.RecipeId,
            package.RecipeHash,
            reference,
            HashBaseline(
                package.ProjectId,
                package.ProjectHash,
                package.FixedStepTicks,
                package.RecipeId,
                package.RecipeHash,
                reference));
    }

    public bool HasValidEvidenceHash() =>
        SchemaVersion == CurrentSchemaVersion
        && !string.IsNullOrWhiteSpace(ProjectId)
        && !string.IsNullOrWhiteSpace(RecipeId)
        && FixedStepTicks > 0
        && DeterministicMultiAxisCommissioningResultPackage.HasValidRunHash(ReferenceRun)
        && string.Equals(
            EvidenceHash,
            HashBaseline(
                ProjectId,
                ProjectHash,
                FixedStepTicks,
                RecipeId,
                RecipeHash,
                ReferenceRun),
            StringComparison.Ordinal);

    public DeterministicCommissioningBaselineComparison CompareTo(
        DeterministicMultiAxisCommissioningResultPackage? package)
    {
        if (!HasValidEvidenceHash())
        {
            return new(false, "InvalidBaseline", null);
        }
        if (package is null || !package.HasValidEvidenceHash() || package.Runs.IsDefaultOrEmpty)
        {
            return new(false, "InvalidResult", null);
        }
        if (!string.Equals(ProjectId, package.ProjectId, StringComparison.Ordinal)
            || FixedStepTicks != package.FixedStepTicks
            || !string.Equals(RecipeId, package.RecipeId, StringComparison.Ordinal))
        {
            return new(
                false,
                "ContextMismatch",
                new DeterministicCommissioningMismatch(
                    1,
                    0,
                    "Context",
                    string.Empty,
                    EvidenceHash,
                    package.EvidenceHash));
        }

        var mismatch = DeterministicMultiAxisCommissioningRunner.CompareRuns(
            ReferenceRun,
            package.Runs[0]);
        return mismatch is null
            ? new(true, null, null)
            : new(false, $"{mismatch.EvidenceKind}EvidenceMismatch", mismatch);
    }

    public static void SaveToJson(
        DeterministicMultiAxisCommissioningBaseline baseline,
        string path)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!baseline.HasValidEvidenceHash())
        {
            throw new InvalidOperationException("Invalid commissioning baseline cannot be saved.");
        }
        SaveAtomic(JsonSerializer.Serialize(baseline, JsonOptions), path);
    }

    public static DeterministicMultiAxisCommissioningBaseline? LoadFromJson(string path) =>
        LoadFromJson<DeterministicMultiAxisCommissioningBaseline>(path);

    private static string HashBaseline(
        string projectId,
        string projectHash,
        long fixedStepTicks,
        string recipeId,
        string recipeHash,
        DeterministicCommissioningRunResult reference) =>
        DeterministicMultiAxisCommissioningResultPackage.Hash(
            $"{CurrentSchemaVersion}|{projectId}|{projectHash}|{fixedStepTicks}|{recipeId}|{recipeHash}|{reference.EvidenceHash}");

    internal static JsonSerializerOptions CreateJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static void SaveAtomic(string json, string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
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

    internal static T? LoadFromJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }
}

public sealed record DeterministicCommissioningResultHistoryEntry(
    int Sequence,
    DateTimeOffset CapturedAtUtc,
    bool IsSuccess,
    int CompletedRuns,
    string PackageEvidenceHash,
    DeterministicMultiAxisCommissioningBaseline? Reference,
    DeterministicCommissioningMismatch? FirstMismatch,
    string EvidenceHash)
{
    [JsonIgnore]
    public string ShortEvidenceHash => PackageEvidenceHash.Length <= 12
        ? PackageEvidenceHash
        : PackageEvidenceHash[..12];

    internal static DeterministicCommissioningResultHistoryEntry Create(
        int sequence,
        DateTimeOffset capturedAtUtc,
        DeterministicMultiAxisCommissioningResultPackage package)
    {
        var reference = package.IsSuccess
            ? DeterministicMultiAxisCommissioningBaseline.FromResult(package)
            : null;
        return new DeterministicCommissioningResultHistoryEntry(
            sequence,
            capturedAtUtc.ToUniversalTime(),
            package.IsSuccess,
            package.CompletedRuns,
            package.EvidenceHash,
            reference,
            package.FirstMismatch,
            HashEntry(
                sequence,
                capturedAtUtc.ToUniversalTime(),
                package.IsSuccess,
                package.CompletedRuns,
                package.EvidenceHash,
                reference,
                package.FirstMismatch));
    }

    internal bool HasValidEvidenceHash() =>
        Sequence > 0
        && CompletedRuns > 0
        && !string.IsNullOrWhiteSpace(PackageEvidenceHash)
        && (Reference is null || Reference.HasValidEvidenceHash())
        && IsSuccess == (Reference is not null)
        && string.Equals(
            EvidenceHash,
            HashEntry(
                Sequence,
                CapturedAtUtc,
                IsSuccess,
                CompletedRuns,
                PackageEvidenceHash,
                Reference,
                FirstMismatch),
            StringComparison.Ordinal);

    private static string HashEntry(
        int sequence,
        DateTimeOffset capturedAtUtc,
        bool isSuccess,
        int completedRuns,
        string packageEvidenceHash,
        DeterministicMultiAxisCommissioningBaseline? reference,
        DeterministicCommissioningMismatch? mismatch) =>
        DeterministicMultiAxisCommissioningResultPackage.Hash(string.Join(
            "|",
            sequence,
            capturedAtUtc.UtcTicks,
            isSuccess,
            completedRuns,
            packageEvidenceHash,
            reference?.EvidenceHash,
            mismatch?.RunIndex,
            mismatch?.TickIndex,
            mismatch?.EvidenceKind,
            mismatch?.TargetId,
            mismatch?.ExpectedHash,
            mismatch?.ActualHash));
}

public sealed record DeterministicMultiAxisCommissioningResultHistory(
    int SchemaVersion,
    string ProjectId,
    ImmutableArray<DeterministicCommissioningResultHistoryEntry> Entries,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 20;

    private static readonly JsonSerializerOptions JsonOptions =
        DeterministicMultiAxisCommissioningBaseline.CreateJsonOptions();

    public static DeterministicMultiAxisCommissioningResultHistory Empty(string projectId) =>
        Create(projectId, ImmutableArray<DeterministicCommissioningResultHistoryEntry>.Empty);

    public DeterministicMultiAxisCommissioningResultHistory Append(
        DeterministicMultiAxisCommissioningResultPackage package,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!HasValidEvidenceHash()
            || !package.HasValidEvidenceHash()
            || !string.Equals(ProjectId, package.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Commissioning result history context is invalid.");
        }

        var sequence = Entries.IsDefaultOrEmpty ? 1 : Entries[^1].Sequence + 1;
        return Create(
            ProjectId,
            Entries.Append(DeterministicCommissioningResultHistoryEntry.Create(
                    sequence,
                    capturedAtUtc,
                    package))
                .TakeLast(MaximumEntries)
                .ToImmutableArray());
    }

    public bool HasValidEvidenceHash()
    {
        var entries = Entries.IsDefault
            ? ImmutableArray<DeterministicCommissioningResultHistoryEntry>.Empty
            : Entries;
        return SchemaVersion == CurrentSchemaVersion
            && !string.IsNullOrWhiteSpace(ProjectId)
            && entries.Length <= MaximumEntries
            && entries.All(entry => entry.HasValidEvidenceHash())
            && entries.Select(entry => entry.Sequence).Distinct().Count() == entries.Length
            && entries.SequenceEqual(entries.OrderBy(entry => entry.Sequence))
            && string.Equals(EvidenceHash, HashHistory(ProjectId, entries), StringComparison.Ordinal);
    }

    public static void SaveToJson(
        DeterministicMultiAxisCommissioningResultHistory history,
        string path)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (!history.HasValidEvidenceHash())
        {
            throw new InvalidOperationException("Invalid commissioning result history cannot be saved.");
        }
        DeterministicMultiAxisCommissioningBaseline.SaveAtomic(
            JsonSerializer.Serialize(history, JsonOptions),
            path);
    }

    public static DeterministicMultiAxisCommissioningResultHistory? LoadFromJson(string path) =>
        DeterministicMultiAxisCommissioningBaseline
            .LoadFromJson<DeterministicMultiAxisCommissioningResultHistory>(path);

    private static DeterministicMultiAxisCommissioningResultHistory Create(
        string projectId,
        ImmutableArray<DeterministicCommissioningResultHistoryEntry> entries) =>
        new(
            CurrentSchemaVersion,
            projectId,
            entries,
            HashHistory(projectId, entries));

    private static string HashHistory(
        string projectId,
        IEnumerable<DeterministicCommissioningResultHistoryEntry> entries) =>
        DeterministicMultiAxisCommissioningResultPackage.Hash(
            $"{CurrentSchemaVersion}|{projectId}\n{string.Join('\n', entries.Select(entry => $"{entry.Sequence}|{entry.EvidenceHash}"))}");
}
