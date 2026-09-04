using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// One explicit transport envelope for deterministic simulation evidence.
/// The nested result packages remain authoritative for runtime evidence and
/// hashes; this envelope only makes their transfer boundary explicit.
/// </summary>
public sealed record DeterministicSimulationEvidenceExchangePackage(
    int SchemaVersion,
    string ArtifactType,
    string ProjectId,
    string ProjectName,
    string ProjectHash,
    long FixedStepTicks,
    string ScenarioId,
    string ScenarioName,
    string TargetId,
    int Seed,
    long PlannedTicks,
    string BuildIdentity,
    int RepetitionCount,
    JsonElement BatchResult,
    JsonElement? AcceptedBaseline,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentArtifactType = "deterministic-simulation-evidence";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static DeterministicSimulationEvidenceExchangePackage Create(
        DeterministicSimulationBatchResultPackage batchResult,
        DeterministicSimulationRunResultPackage? acceptedBaseline = null)
    {
        ArgumentNullException.ThrowIfNull(batchResult);
        if (!batchResult.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Only complete, integrity-checked batch evidence can be exported.");
        }

        if (acceptedBaseline is not null && !acceptedBaseline.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Only integrity-checked baseline evidence can be exported.");
        }

        var firstRun = batchResult.Runs.FirstOrDefault()?.Result
            ?? throw new InvalidOperationException("A batch must contain at least one run.");
        EnsureCommonContext(firstRun, batchResult.Runs.Select(run => run.Result), acceptedBaseline);

        var portableBatch = batchResult with
        {
            Runs = batchResult.Runs
                .Select(run => run with
                {
                    Result = run.Result with { ProjectPath = string.Empty }
                })
                .ToImmutableArray()
        };
        var portableBaseline = acceptedBaseline is null
            ? (JsonElement?)null
            : SerializeToElement(acceptedBaseline with { ProjectPath = string.Empty });
        var batchElement = SerializeToElement(portableBatch);
        var evidenceHash = Hash(
            firstRun.ProjectId,
            firstRun.ProjectName,
            firstRun.ProjectHash,
            firstRun.FixedStepTicks,
            firstRun.ScenarioId,
            firstRun.ScenarioName,
            firstRun.TargetId,
            firstRun.Seed,
            firstRun.PlannedTicks,
            batchResult.BuildIdentity,
            batchResult.RequestedRuns,
            portableBatch.EvidenceHash,
            acceptedBaseline?.EvidenceHash);

        return new(
            CurrentSchemaVersion,
            CurrentArtifactType,
            firstRun.ProjectId,
            firstRun.ProjectName,
            firstRun.ProjectHash,
            firstRun.FixedStepTicks,
            firstRun.ScenarioId,
            firstRun.ScenarioName,
            firstRun.TargetId,
            firstRun.Seed,
            firstRun.PlannedTicks,
            batchResult.BuildIdentity,
            batchResult.RequestedRuns,
            batchElement,
            portableBaseline,
            evidenceHash);
    }

    public bool HasValidEvidenceHash()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || !string.Equals(ArtifactType, CurrentArtifactType, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ProjectId)
            || string.IsNullOrWhiteSpace(ProjectHash)
            || FixedStepTicks <= 0
            || string.IsNullOrWhiteSpace(ScenarioId)
            || string.IsNullOrWhiteSpace(TargetId)
            || PlannedTicks <= 0
            || RepetitionCount < 1
            || BatchResult.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return false;
        }

        if (!TryReadPackages(out var batchResult, out var acceptedBaseline)
            || !batchResult.HasValidEvidenceHash()
            || batchResult.RequestedRuns != RepetitionCount
            || batchResult.Runs.Any(run => !string.IsNullOrEmpty(run.Result.ProjectPath))
            || (acceptedBaseline is not null && !acceptedBaseline.HasValidEvidenceHash())
            || (acceptedBaseline is not null && !string.IsNullOrEmpty(acceptedBaseline.ProjectPath)))
        {
            return false;
        }

        var firstRun = batchResult.Runs.FirstOrDefault()?.Result;
        if (firstRun is null
            || !HasCommonMetadata(firstRun, batchResult.Runs.Select(run => run.Result), acceptedBaseline))
        {
            return false;
        }

        var expected = Hash(
            firstRun.ProjectId,
            firstRun.ProjectName,
            firstRun.ProjectHash,
            firstRun.FixedStepTicks,
            firstRun.ScenarioId,
            firstRun.ScenarioName,
            firstRun.TargetId,
            firstRun.Seed,
            firstRun.PlannedTicks,
            batchResult.BuildIdentity,
            batchResult.RequestedRuns,
            batchResult.EvidenceHash,
            acceptedBaseline?.EvidenceHash);
        return string.Equals(ProjectId, firstRun.ProjectId, StringComparison.Ordinal)
            && string.Equals(ProjectName, firstRun.ProjectName, StringComparison.Ordinal)
            && string.Equals(ProjectHash, firstRun.ProjectHash, StringComparison.Ordinal)
            && FixedStepTicks == firstRun.FixedStepTicks
            && string.Equals(ScenarioId, firstRun.ScenarioId, StringComparison.Ordinal)
            && string.Equals(ScenarioName, firstRun.ScenarioName, StringComparison.Ordinal)
            && string.Equals(TargetId, firstRun.TargetId, StringComparison.Ordinal)
            && Seed == firstRun.Seed
            && PlannedTicks == firstRun.PlannedTicks
            && string.Equals(BuildIdentity, batchResult.BuildIdentity, StringComparison.Ordinal)
            && string.Equals(EvidenceHash, expected, StringComparison.Ordinal);
    }

    public bool IsForContext(
        string projectId,
        string projectJson,
        TimeSpan fixedStep,
        DeterministicConditionScenarioProfile profile,
        string buildIdentity)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = DeterministicConditionScenarioProfile.Normalize(profile);
        if (!HasValidEvidenceHash()
            || string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(buildIdentity))
        {
            return false;
        }

        if (!TryReadPackages(out var batchResult, out var acceptedBaseline))
        {
            return false;
        }

        var matchesBatch = batchResult.IsForContext(
            $"{projectId}:{normalized.ScenarioId}",
            buildIdentity,
            RepetitionCount,
            projectId,
            projectJson,
            fixedStep,
            normalized);
        var matchesBaseline = acceptedBaseline is null
            || acceptedBaseline.IsForContext(projectId, projectJson, fixedStep, normalized);
        return matchesBatch && matchesBaseline;
    }

    public bool TryGetPackages(
        string projectPath,
        out DeterministicSimulationBatchResultPackage batchResult,
        out DeterministicSimulationRunResultPackage? acceptedBaseline)
    {
        batchResult = null!;
        acceptedBaseline = null;
        if (string.IsNullOrWhiteSpace(projectPath)
            || !HasValidEvidenceHash()
            || !TryReadPackages(out var portableBatch, out var portableBaseline))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(projectPath);
        batchResult = portableBatch with
        {
            Runs = portableBatch.Runs
                .Select(run => run with
                {
                    Result = run.Result with { ProjectPath = fullPath }
                })
                .ToImmutableArray()
        };
        acceptedBaseline = portableBaseline is null
            ? null
            : portableBaseline with { ProjectPath = fullPath };
        return true;
    }

    public static string SaveToJson(DeterministicSimulationEvidenceExchangePackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static void SaveToJson(
        DeterministicSimulationEvidenceExchangePackage package,
        string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Invalid simulation evidence exchange cannot be saved.");
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

    public static DeterministicSimulationEvidenceExchangePackage? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicSimulationEvidenceExchangePackage>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private bool TryReadPackages(
        out DeterministicSimulationBatchResultPackage batchResult,
        out DeterministicSimulationRunResultPackage? acceptedBaseline)
    {
        batchResult = null!;
        acceptedBaseline = null;
        try
        {
            batchResult = JsonSerializer.Deserialize<DeterministicSimulationBatchResultPackage>(
                BatchResult.GetRawText(),
                JsonOptions)!;
            acceptedBaseline = AcceptedBaseline is { } baseline
                ? JsonSerializer.Deserialize<DeterministicSimulationRunResultPackage>(
                    baseline.GetRawText(),
                    JsonOptions)
                : null;
            return batchResult is not null;
        }
        catch (JsonException)
        {
            batchResult = null!;
            acceptedBaseline = null;
            return false;
        }
    }

    private static void EnsureCommonContext(
        DeterministicSimulationRunResultPackage firstRun,
        IEnumerable<DeterministicSimulationRunResultPackage> runs,
        DeterministicSimulationRunResultPackage? acceptedBaseline)
    {
        if (!HasCommonMetadata(firstRun, runs, acceptedBaseline))
        {
            throw new InvalidOperationException(
                "Evidence packages must describe the same project and scenario context.");
        }
    }

    private static bool HasCommonMetadata(
        DeterministicSimulationRunResultPackage firstRun,
        IEnumerable<DeterministicSimulationRunResultPackage> runs,
        DeterministicSimulationRunResultPackage? acceptedBaseline)
    {
        bool Matches(DeterministicSimulationRunResultPackage candidate) =>
            string.Equals(candidate.ProjectId, firstRun.ProjectId, StringComparison.Ordinal)
            && string.Equals(candidate.ProjectName, firstRun.ProjectName, StringComparison.Ordinal)
            && string.Equals(candidate.ProjectHash, firstRun.ProjectHash, StringComparison.Ordinal)
            && candidate.FixedStepTicks == firstRun.FixedStepTicks
            && string.Equals(candidate.ScenarioId, firstRun.ScenarioId, StringComparison.Ordinal)
            && string.Equals(candidate.ScenarioName, firstRun.ScenarioName, StringComparison.Ordinal)
            && string.Equals(candidate.TargetId, firstRun.TargetId, StringComparison.Ordinal)
            && candidate.Seed == firstRun.Seed
            && candidate.PlannedTicks == firstRun.PlannedTicks;

        return runs.All(Matches)
            && (acceptedBaseline is null || Matches(acceptedBaseline));
    }

    private static JsonElement SerializeToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private static string Hash(
        string projectId,
        string projectName,
        string projectHash,
        long fixedStepTicks,
        string scenarioId,
        string scenarioName,
        string targetId,
        int seed,
        long plannedTicks,
        string buildIdentity,
        int repetitionCount,
        string batchEvidenceHash,
        string? baselineEvidenceHash)
    {
        var builder = new StringBuilder()
            .Append(CurrentSchemaVersion).Append('|')
            .Append(CurrentArtifactType).Append('|')
            .Append(projectId).Append('|')
            .Append(projectName).Append('|')
            .Append(projectHash).Append('|')
            .Append(fixedStepTicks).Append('|')
            .Append(scenarioId).Append('|')
            .Append(scenarioName).Append('|')
            .Append(targetId).Append('|')
            .Append(seed).Append('|')
            .Append(plannedTicks).Append('|')
            .Append(buildIdentity).Append('|')
            .Append(repetitionCount).Append('|')
            .Append(batchEvidenceHash).Append('|')
            .Append(baselineEvidenceHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
