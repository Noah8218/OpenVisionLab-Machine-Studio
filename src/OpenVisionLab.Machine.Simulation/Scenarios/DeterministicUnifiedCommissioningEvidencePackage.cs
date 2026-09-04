using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// One explicit commissioning handoff over the existing simulation, command,
/// and optional Vision evidence contracts. The nested packages remain the
/// authorities for their own evidence and hashes.
/// </summary>
public sealed record DeterministicUnifiedCommissioningEvidencePackage(
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
    DeterministicSimulationEvidenceExchangePackage SimulationEvidence,
    DeterministicSimulationCommandTracePackage CommandTrace,
    DeterministicVisionExecutionEvidencePackage? VisionEvidence,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentArtifactType = "deterministic-unified-commissioning-evidence";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// The command trace is the only nested artifact that can be replayed.
    /// </summary>
    public bool CanReplayCommandTrace => CommandTrace?.CanReplay == true;

    /// <summary>
    /// Vision evidence records an acquisition and inspection; it is not a
    /// replay command source.
    /// </summary>
    public bool ContainsNonReplayableVisionEvidence => VisionEvidence is not null;

    public static DeterministicUnifiedCommissioningEvidencePackage Create(
        DeterministicSimulationEvidenceExchangePackage simulationEvidence,
        DeterministicSimulationCommandTracePackage commandTrace,
        DeterministicVisionExecutionEvidencePackage? visionEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(simulationEvidence);
        ArgumentNullException.ThrowIfNull(commandTrace);
        if (!simulationEvidence.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Only complete, integrity-checked simulation evidence can be bundled.");
        }

        if (!commandTrace.HasValidTraceHash())
        {
            throw new InvalidOperationException(
                "Only integrity-checked command trace can be bundled.");
        }

        if (commandTrace.FixedStepTicks != simulationEvidence.FixedStepTicks)
        {
            throw new InvalidOperationException(
                "Simulation evidence and command trace must use the same fixed step.");
        }

        var portableVisionEvidence = visionEvidence is null
            ? null
            : NormalizeVisionEvidence(visionEvidence, simulationEvidence);
        var evidenceHash = Hash(
            CurrentSchemaVersion,
            CurrentArtifactType,
            simulationEvidence.ProjectId,
            simulationEvidence.ProjectName,
            simulationEvidence.ProjectHash,
            simulationEvidence.FixedStepTicks,
            simulationEvidence.ScenarioId,
            simulationEvidence.ScenarioName,
            simulationEvidence.TargetId,
            simulationEvidence.Seed,
            simulationEvidence.PlannedTicks,
            simulationEvidence.BuildIdentity,
            simulationEvidence.EvidenceHash,
            commandTrace.TraceHash,
            portableVisionEvidence?.EvidenceHash);

        return new(
            CurrentSchemaVersion,
            CurrentArtifactType,
            simulationEvidence.ProjectId,
            simulationEvidence.ProjectName,
            simulationEvidence.ProjectHash,
            simulationEvidence.FixedStepTicks,
            simulationEvidence.ScenarioId,
            simulationEvidence.ScenarioName,
            simulationEvidence.TargetId,
            simulationEvidence.Seed,
            simulationEvidence.PlannedTicks,
            simulationEvidence.BuildIdentity,
            simulationEvidence,
            commandTrace,
            portableVisionEvidence,
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
            || string.IsNullOrWhiteSpace(BuildIdentity)
            || SimulationEvidence is null
            || CommandTrace is null)
        {
            return false;
        }

        if (!SimulationEvidence.HasValidEvidenceHash()
            || !CommandTrace.HasValidTraceHash()
            || !HasSimulationContext()
            || CommandTrace.FixedStepTicks != FixedStepTicks)
        {
            return false;
        }

        if (VisionEvidence is not null
            && (!VisionEvidence.HasValidEvidenceHash()
                || !string.IsNullOrEmpty(VisionEvidence.ProjectPath)
                || !HasVisionContext()))
        {
            return false;
        }

        var expected = Hash(
            SchemaVersion,
            ArtifactType,
            ProjectId,
            ProjectName,
            ProjectHash,
            FixedStepTicks,
            ScenarioId,
            ScenarioName,
            TargetId,
            Seed,
            PlannedTicks,
            BuildIdentity,
            SimulationEvidence.EvidenceHash,
            CommandTrace.TraceHash,
            VisionEvidence?.EvidenceHash);
        return string.Equals(EvidenceHash, expected, StringComparison.Ordinal);
    }

    public bool IsForContext(
        string projectId,
        string projectJson,
        TimeSpan fixedStep,
        DeterministicConditionScenarioProfile profile,
        string buildIdentity)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!HasValidEvidenceHash()
            || string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(buildIdentity)
            || fixedStep <= TimeSpan.Zero)
        {
            return false;
        }

        var normalizedProfile = DeterministicConditionScenarioProfile.Normalize(profile);
        return string.Equals(ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(BuildIdentity, buildIdentity, StringComparison.Ordinal)
            && FixedStepTicks == fixedStep.Ticks
            && SimulationEvidence.IsForContext(
                projectId,
                projectJson,
                fixedStep,
                normalizedProfile,
                buildIdentity)
            && (VisionEvidence is null
                || VisionEvidence.IsForContext(
                    projectId,
                    projectJson,
                    buildIdentity,
                    VisionEvidence.CameraId,
                    VisionEvidence.RecipeId));
    }

    /// <summary>
    /// Restores project-linked paths only after the caller explicitly supplies
    /// the destination project path. No command is queued and no authored
    /// project state is changed.
    /// </summary>
    public bool TryGetArtifacts(
        string projectPath,
        out DeterministicSimulationBatchResultPackage batchResult,
        out DeterministicSimulationRunResultPackage? acceptedBaseline,
        out DeterministicSimulationCommandTracePackage commandTrace,
        out DeterministicVisionExecutionEvidencePackage? visionEvidence)
    {
        batchResult = null!;
        acceptedBaseline = null;
        commandTrace = null!;
        visionEvidence = null;
        if (string.IsNullOrWhiteSpace(projectPath)
            || !HasValidEvidenceHash()
            || !SimulationEvidence.TryGetPackages(
                projectPath,
                out batchResult,
                out acceptedBaseline))
        {
            return false;
        }

        commandTrace = CommandTrace;
        var fullPath = Path.GetFullPath(projectPath);
        visionEvidence = VisionEvidence is null
            ? null
            : VisionEvidence with { ProjectPath = fullPath };
        return true;
    }

    public static string SaveToJson(
        DeterministicUnifiedCommissioningEvidencePackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static void SaveToJson(
        DeterministicUnifiedCommissioningEvidencePackage package,
        string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Invalid unified commissioning evidence cannot be saved.");
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

    public static DeterministicUnifiedCommissioningEvidencePackage? LoadFromJson(
        string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicUnifiedCommissioningEvidencePackage>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private bool HasSimulationContext() =>
        string.Equals(ProjectId, SimulationEvidence.ProjectId, StringComparison.Ordinal)
        && string.Equals(ProjectName, SimulationEvidence.ProjectName, StringComparison.Ordinal)
        && string.Equals(ProjectHash, SimulationEvidence.ProjectHash, StringComparison.Ordinal)
        && FixedStepTicks == SimulationEvidence.FixedStepTicks
        && string.Equals(ScenarioId, SimulationEvidence.ScenarioId, StringComparison.Ordinal)
        && string.Equals(ScenarioName, SimulationEvidence.ScenarioName, StringComparison.Ordinal)
        && string.Equals(TargetId, SimulationEvidence.TargetId, StringComparison.Ordinal)
        && Seed == SimulationEvidence.Seed
        && PlannedTicks == SimulationEvidence.PlannedTicks
        && string.Equals(BuildIdentity, SimulationEvidence.BuildIdentity, StringComparison.Ordinal);

    private bool HasVisionContext() =>
        string.Equals(ProjectId, VisionEvidence!.ProjectId, StringComparison.Ordinal)
        && string.Equals(ProjectName, VisionEvidence.ProjectName, StringComparison.Ordinal)
        && string.Equals(ProjectHash, VisionEvidence.ProjectHash, StringComparison.Ordinal)
        && FixedStepTicks == VisionEvidence.FixedStepTicks
        && string.Equals(BuildIdentity, VisionEvidence.BuildIdentity, StringComparison.Ordinal);

    private static DeterministicVisionExecutionEvidencePackage NormalizeVisionEvidence(
        DeterministicVisionExecutionEvidencePackage visionEvidence,
        DeterministicSimulationEvidenceExchangePackage simulationEvidence)
    {
        if (!visionEvidence.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Only integrity-checked Vision evidence can be bundled.");
        }

        if (!string.Equals(visionEvidence.ProjectId, simulationEvidence.ProjectId, StringComparison.Ordinal)
            || !string.Equals(visionEvidence.ProjectName, simulationEvidence.ProjectName, StringComparison.Ordinal)
            || !string.Equals(visionEvidence.ProjectHash, simulationEvidence.ProjectHash, StringComparison.Ordinal)
            || visionEvidence.FixedStepTicks != simulationEvidence.FixedStepTicks
            || !string.Equals(visionEvidence.BuildIdentity, simulationEvidence.BuildIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Simulation and Vision evidence must describe the same project and build context.");
        }

        return visionEvidence with { ProjectPath = string.Empty };
    }

    private static string Hash(
        int schemaVersion,
        string artifactType,
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
        string simulationEvidenceHash,
        string commandTraceHash,
        string? visionEvidenceHash)
    {
        var builder = new StringBuilder()
            .Append(schemaVersion).Append('|')
            .Append(artifactType).Append('|')
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
            .Append(simulationEvidenceHash).Append('|')
            .Append(commandTraceHash).Append('|')
            .Append(visionEvidenceHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
