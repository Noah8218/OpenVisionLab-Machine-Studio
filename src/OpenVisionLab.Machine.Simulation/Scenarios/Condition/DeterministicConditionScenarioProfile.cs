using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Faults;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public enum DeterministicConditionState
{
    Normal,
    Degraded,
    Fault,
    Recovering
}

public sealed record DeterministicConditionScenarioProfile(
    int SchemaVersion,
    string ScenarioId,
    string Name,
    string Description,
    string TargetId,
    int Seed,
    long DurationTicks,
    int MinimumStateTicks = 4,
    int JitterTicks = 4,
    DeterministicConditionState InitialState = DeterministicConditionState.Normal,
    DeterministicAxisFaultRecoverySchedule? AxisFaultRecovery = null,
    DeterministicFaultRecoverySchedule? FaultRecovery = null,
    ImmutableArray<DeterministicScenarioAssertion> Assertions = default)
{
    private const int DefaultSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static readonly int CurrentSchemaVersion = 2;

    public static readonly DeterministicConditionScenarioProfile Empty = new(
        CurrentSchemaVersion,
        "empty-condition",
        "Empty condition scenario",
        "No condition transitions.",
        "fleet",
        1001,
        0);

    public static DeterministicConditionScenarioProfile? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicConditionScenarioProfile>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string SaveToJson(DeterministicConditionScenarioProfile profile) =>
        JsonSerializer.Serialize(Normalize(profile), JsonOptions);

    public static void SaveToJson(DeterministicConditionScenarioProfile profile, string path)
    {
        var fullPath = Path.GetFullPath(path);
        AtomicEvidenceFile.Write(fullPath, temporaryPath => File.WriteAllText(temporaryPath, SaveToJson(profile)));
    }

    public static DeterministicConditionScenarioProfile Normalize(
        DeterministicConditionScenarioProfile? profile)
    {
        if (profile is null)
        {
            return Empty;
        }

        return profile with
        {
            SchemaVersion = profile.SchemaVersion == 0
                ? DefaultSchemaVersion
                : profile.SchemaVersion,
            ScenarioId = string.IsNullOrWhiteSpace(profile.ScenarioId)
                ? "custom-condition"
                : profile.ScenarioId.Trim(),
            Name = string.IsNullOrWhiteSpace(profile.Name)
                ? "Custom condition scenario"
                : profile.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(profile.Description)
                ? "Custom deterministic condition transitions."
                : profile.Description.Trim(),
            TargetId = string.IsNullOrWhiteSpace(profile.TargetId)
                ? "fleet"
                : profile.TargetId.Trim(),
            DurationTicks = Math.Max(0, profile.DurationTicks),
            MinimumStateTicks = Math.Clamp(profile.MinimumStateTicks, 1, 1_000_000),
            JitterTicks = Math.Clamp(profile.JitterTicks, 0, 1_000),
            AxisFaultRecovery = null,
            FaultRecovery = NormalizeFaultRecovery(
                profile.FaultRecovery ?? FromLegacyAxisFault(profile.AxisFaultRecovery)),
            Assertions = profile.Assertions.IsDefault
                ? ImmutableArray<DeterministicScenarioAssertion>.Empty
                : profile.Assertions.Select(DeterministicScenarioAssertion.Normalize).ToImmutableArray()
        };
    }

    public static IReadOnlyList<string> Validate(DeterministicConditionScenarioProfile? profile)
    {
        var normalized = Normalize(profile);
        var errors = new List<string>();
        if (normalized.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"Unsupported schema '{normalized.SchemaVersion}'. Supported schema is {CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(normalized.ScenarioId))
        {
            errors.Add("ScenarioId is required.");
        }

        if (string.IsNullOrWhiteSpace(normalized.TargetId))
        {
            errors.Add("TargetId is required.");
        }

        if (normalized.DurationTicks < 0)
        {
            errors.Add("DurationTicks must be non-negative.");
        }

        if (normalized.MinimumStateTicks < 1)
        {
            errors.Add("MinimumStateTicks must be at least 1.");
        }

        if (normalized.JitterTicks < 0)
        {
            errors.Add("JitterTicks must be non-negative.");
        }

        if (!Enum.IsDefined(normalized.InitialState))
        {
            errors.Add($"InitialState '{normalized.InitialState}' is not supported.");
        }

        foreach (var assertion in normalized.Assertions)
        {
            errors.AddRange(DeterministicScenarioAssertion.Validate(assertion));
        }
        foreach (var duplicateId in normalized.Assertions
                     .GroupBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"AssertionId '{duplicateId}' must be unique.");
        }

        var recovery = normalized.FaultRecovery;
        if (recovery is not null)
        {
            if (recovery.FaultKind is not (
                    SimulationFaultKind.StuckDigitalInput or
                    SimulationFaultKind.CylinderTravelBlocked or
                    SimulationFaultKind.AxisMotionBlocked))
            {
                errors.Add($"FaultRecovery.FaultKind '{recovery.FaultKind}' is not supported.");
            }
            if (string.IsNullOrWhiteSpace(recovery.TargetId))
            {
                errors.Add("FaultRecovery.TargetId is required.");
            }
            if (recovery.InjectTick < 0)
            {
                errors.Add("FaultRecovery.InjectTick must be non-negative.");
            }
            if (recovery.HoldTicks < 1)
            {
                errors.Add("FaultRecovery.HoldTicks must be at least 1.");
            }
            if (recovery.InjectTick > long.MaxValue - recovery.HoldTicks
                || recovery.InjectTick + recovery.HoldTicks >= normalized.DurationTicks)
            {
                errors.Add("FaultRecovery must clear before the scenario duration ends.");
            }
            if (recovery.FaultKind == SimulationFaultKind.StuckDigitalInput
                && !recovery.ForcedValue.HasValue)
            {
                errors.Add("FaultRecovery.ForcedValue is required for StuckDigitalInput.");
            }
            if (recovery.FaultKind != SimulationFaultKind.StuckDigitalInput
                && recovery.ForcedValue.HasValue)
            {
                errors.Add($"FaultRecovery.ForcedValue is not valid for {recovery.FaultKind}.");
            }
        }

        return errors;
    }

    private static DeterministicFaultRecoverySchedule? NormalizeFaultRecovery(
        DeterministicFaultRecoverySchedule? recovery) =>
        recovery is null
            ? null
            : recovery with
            {
                TargetId = recovery.TargetId?.Trim() ?? string.Empty,
                RestartSequenceId = string.IsNullOrWhiteSpace(recovery.RestartSequenceId)
                    ? null
                    : recovery.RestartSequenceId.Trim()
            };

    private static DeterministicFaultRecoverySchedule? FromLegacyAxisFault(
        DeterministicAxisFaultRecoverySchedule? recovery) =>
        recovery is null
            ? null
            : new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.AxisMotionBlocked,
                recovery.AxisId,
                recovery.InjectTick,
                recovery.HoldTicks,
                RestartSequenceId: recovery.RestartSequenceId);
}

public sealed record DeterministicAxisFaultRecoverySchedule(
    string AxisId,
    long InjectTick,
    int HoldTicks,
    string? RestartSequenceId = null);

public sealed record DeterministicFaultRecoverySchedule(
    SimulationFaultKind FaultKind,
    string TargetId,
    long InjectTick,
    int HoldTicks,
    bool? ForcedValue = null,
    string? RestartSequenceId = null);
