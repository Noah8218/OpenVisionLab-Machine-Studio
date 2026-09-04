using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.Models.Simulation;

public enum SimulationScenarioMode
{
    Normal,
    Fault,
    Recovery,
    Congested
}

/// <summary>
/// Authored condition-scenario settings whose tuning fields map directly to
/// <see cref="DeterministicConditionScenarioProfile"/>.
/// </summary>
public sealed record SimulationScenarioProfile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProfileId { get; init; } = "normal";
    public string Name { get; init; } = "Normal";
    public string Description { get; init; } =
        "Stable deterministic condition phases with no transition jitter.";
    public SimulationScenarioMode Mode { get; init; } = SimulationScenarioMode.Normal;
    public int Seed { get; init; } = 1001;
    public DeterministicConditionState InitialState { get; init; } =
        DeterministicConditionState.Normal;
    public int MinimumStateTicks { get; init; } = 20;
    public int JitterTicks { get; init; }

    public static SimulationScenarioProfile[] BuiltIns { get; } =
    [
        new()
        {
            ProfileId = "normal",
            Name = "Normal",
            Description = "Stable deterministic condition phases with no transition jitter.",
            Mode = SimulationScenarioMode.Normal,
            Seed = 1001,
            InitialState = DeterministicConditionState.Normal,
            MinimumStateTicks = 20,
            JitterTicks = 0
        },
        new()
        {
            ProfileId = "fault-injection",
            Name = "Fault Injection",
            Description = "Starts degraded and advances through deterministic condition phases with short jitter.",
            Mode = SimulationScenarioMode.Fault,
            Seed = 2002,
            InitialState = DeterministicConditionState.Degraded,
            MinimumStateTicks = 8,
            JitterTicks = 3
        },
        new()
        {
            ProfileId = "recovery",
            Name = "Recovery",
            Description = "Starts faulted and exercises deterministic recovery and return-to-normal phases.",
            Mode = SimulationScenarioMode.Recovery,
            Seed = 3003,
            InitialState = DeterministicConditionState.Fault,
            MinimumStateTicks = 8,
            JitterTicks = 2
        },
        new()
        {
            ProfileId = "congested",
            Name = "Congested Line",
            Description = "Starts degraded with longer deterministic phases and bounded transition jitter.",
            Mode = SimulationScenarioMode.Congested,
            Seed = 4004,
            InitialState = DeterministicConditionState.Degraded,
            MinimumStateTicks = 12,
            JitterTicks = 4
        }
    ];

    public static SimulationScenarioProfile GetBuiltInById(string profileId) =>
        BuiltIns.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        ?? BuiltIns[0];

    public static SimulationScenarioProfile? LoadFromJson(string path) =>
        TryLoadFromJson(path, out SimulationScenarioProfile? profile, out _)
            ? profile
            : null;

    public static bool TryLoadFromJson(
        string? path,
        out SimulationScenarioProfile? profile,
        out string? error)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "A scenario profile path is required.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"Scenario profile '{path}' was not found.";
            return false;
        }

        try
        {
            SimulationScenarioProfile? authored = JsonSerializer.Deserialize<SimulationScenarioProfile>(
                File.ReadAllText(path),
                JsonOptions);
            if (authored is null)
            {
                error = "The scenario profile document is empty.";
                return false;
            }

            string? validationError = Validate(authored);
            if (validationError is not null)
            {
                error = validationError;
                return false;
            }

            profile = Normalize(authored);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Scenario profile could not be loaded: {exception.Message}";
            return false;
        }
    }

    public static SimulationScenarioProfile Normalize(SimulationScenarioProfile? profile)
    {
        if (profile is null)
        {
            return BuiltIns[0];
        }

        return profile with
        {
            ProfileId = string.IsNullOrWhiteSpace(profile.ProfileId) ? "custom" : profile.ProfileId.Trim(),
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Custom Scenario" : profile.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(profile.Description)
                ? "Custom deterministic condition scenario."
                : profile.Description.Trim()
        };
    }

    private static string? Validate(SimulationScenarioProfile profile)
    {
        if (profile.SchemaVersion != CurrentSchemaVersion)
        {
            return $"Unsupported scenario profile schema '{profile.SchemaVersion}'. Supported schema is {CurrentSchemaVersion}.";
        }
        if (!Enum.IsDefined(profile.Mode))
        {
            return $"Scenario mode '{profile.Mode}' is not supported.";
        }
        if (!Enum.IsDefined(profile.InitialState))
        {
            return $"Initial state '{profile.InitialState}' is not supported.";
        }
        if (profile.MinimumStateTicks is < 1 or > 1_000_000)
        {
            return "MinimumStateTicks must be between 1 and 1000000.";
        }
        if (profile.JitterTicks is < 0 or > 1_000)
        {
            return "JitterTicks must be between 0 and 1000.";
        }

        return null;
    }
}
