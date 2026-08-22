using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Simulation.FaultScenarios;

public enum DeterministicFaultScenarioActionKind
{
    InjectFault,
    ClearFault
}

public sealed record DeterministicFaultScenarioAction(
    long Tick,
    DeterministicFaultScenarioActionKind Action,
    DeterministicFaultScenarioFaultKind FaultKind,
    string TargetId,
    bool? ForcedValue = null);

public sealed record DeterministicFaultScenarioProfile(
    int SchemaVersion,
    string ScenarioId,
    string Name,
    string Description,
    long DurationTicks,
    IReadOnlyList<DeterministicFaultScenarioAction> Actions)
{
    private const int DefaultSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static readonly int CurrentSchemaVersion = 1;

    public static readonly DeterministicFaultScenarioProfile Empty =
        new(
            SchemaVersion: CurrentSchemaVersion,
            ScenarioId: "empty",
            Name: "Empty fault scenario",
            Description: "No scripted faults. Useful for repeatability-only smoke runs.",
            DurationTicks: 0,
            Actions: Array.Empty<DeterministicFaultScenarioAction>());

    public static DeterministicFaultScenarioProfile? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DeterministicFaultScenarioProfile>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string SaveToJson(DeterministicFaultScenarioProfile scenario) =>
        JsonSerializer.Serialize(Normalize(scenario), JsonOptions);

    public static void SaveToJson(DeterministicFaultScenarioProfile scenario, string path) =>
        File.WriteAllText(path, SaveToJson(scenario));

    public static DeterministicFaultScenarioProfile Normalize(DeterministicFaultScenarioProfile? scenario)
    {
        if (scenario is null)
        {
            return Empty;
        }

        var actions = scenario.Actions
            .Select(action => action with
            {
                TargetId = string.IsNullOrWhiteSpace(action.TargetId) ? string.Empty : action.TargetId.Trim()
            })
            .ToArray()
            .OrderBy(action => action.Tick)
            .ThenBy(action => action.Action)
            .ThenBy(action => action.FaultKind)
            .ThenBy(action => action.TargetId, StringComparer.Ordinal)
            .ToArray();

        var duration = scenario.DurationTicks < 0 ? 0 : scenario.DurationTicks;
        if (duration == 0)
        {
            var maxTick = actions.Select(action => action.Tick).DefaultIfEmpty(-1).Max();
            if (maxTick >= 0)
            {
                if (maxTick > long.MaxValue - 1)
                {
                    duration = long.MaxValue;
                }
                else
                {
                    duration = maxTick + 1;
                }
            }
        }

        return scenario with
        {
            SchemaVersion = scenario.SchemaVersion == 0 ? DefaultSchemaVersion : scenario.SchemaVersion,
            ScenarioId = string.IsNullOrWhiteSpace(scenario.ScenarioId)
                ? "custom"
                : scenario.ScenarioId.Trim(),
            Name = string.IsNullOrWhiteSpace(scenario.Name)
                ? "Custom fault scenario"
                : scenario.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(scenario.Description)
                ? "Custom fault scenario profile."
                : scenario.Description.Trim(),
            DurationTicks = duration,
            Actions = actions
        };
    }

    public static IReadOnlyList<string> Validate(DeterministicFaultScenarioProfile? scenario)
    {
        var normalized = Normalize(scenario);
        var errors = new List<string>();

        if (normalized.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add(
                $"Unsupported schema '{normalized.SchemaVersion}'. Supported schema is {CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(normalized.ScenarioId))
        {
            errors.Add("ScenarioId is required.");
        }

        if (normalized.Actions.Count == 0 && normalized.DurationTicks > 0)
        {
            errors.Add("DurationTicks was set but no actions were provided.");
        }

        var actionMap = new Dictionary<(long Tick, DeterministicFaultScenarioFaultKind FaultKind, string TargetId), DeterministicFaultScenarioActionKind>();
        for (var index = 0; index < normalized.Actions.Count; index++)
        {
            var action = normalized.Actions[index];
            if (!action.FaultKind.IsSupported())
            {
                errors.Add(
                    $"Unsupported fault kind '{action.FaultKind}' at index {index}.");
                continue;
            }

            if (action.Tick < 0)
            {
                errors.Add($"Action at index {index} has negative tick {action.Tick}.");
            }
            else if (action.Tick >= normalized.DurationTicks && normalized.DurationTicks > 0)
            {
                errors.Add(
                    $"Action at index {index} targets tick {action.Tick} outside scenario duration {normalized.DurationTicks}.");
            }

            if (string.IsNullOrWhiteSpace(action.TargetId))
            {
                errors.Add($"Action at index {index} has an empty target id.");
            }

            if (action.Action == DeterministicFaultScenarioActionKind.InjectFault
                && action.FaultKind == DeterministicFaultScenarioFaultKind.StuckDigitalInput
                && !action.ForcedValue.HasValue)
            {
                errors.Add(
                    $"InjectFault action at index {index} requires ForcedValue for {action.TargetId}.");
            }

            if (action.Action == DeterministicFaultScenarioActionKind.InjectFault
                && action.FaultKind == DeterministicFaultScenarioFaultKind.CylinderTravelBlocked
                && action.ForcedValue.HasValue)
            {
                errors.Add(
                    $"InjectFault action at index {index} should not set ForcedValue for {action.TargetId}.");
            }

            if (action.Action == DeterministicFaultScenarioActionKind.ClearFault && action.ForcedValue.HasValue)
            {
                errors.Add(
                    $"ClearFault action at index {index} should not set ForcedValue for {action.TargetId}.");
            }

            var key = (action.Tick, action.FaultKind, action.TargetId);
            if (actionMap.TryGetValue(key, out var existingAction))
            {
                errors.Add(
                    $"Conflicting actions at tick {action.Tick} for {action.FaultKind} on {action.TargetId}: " +
                    $"{existingAction} and {action.Action} cannot both be scheduled.");
            }
            else
            {
                actionMap[key] = action.Action;
            }

        }

        return errors;
    }
}
