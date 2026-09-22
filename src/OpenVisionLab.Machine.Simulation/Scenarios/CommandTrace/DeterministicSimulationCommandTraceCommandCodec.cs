using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Owns the deterministic trace command argument codec. It translates the
/// existing command types to portable arguments and reconstructs them without
/// depending on an engine, trace package, or UI owner.
/// </summary>
internal static class DeterministicSimulationCommandTraceCommandCodec
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonElement EmptyArguments =
        JsonSerializer.SerializeToElement(new { }, JsonOptions);

    internal static bool TrySerializeArguments(
        SimulationCommand command,
        out JsonElement arguments,
        out string? replayabilityReason)
    {
        ArgumentNullException.ThrowIfNull(command);

        replayabilityReason = null;
        switch (command)
        {
            case PauseCommand:
            case StepCommand:
            case ResetCommand:
            case StopConditionScenarioCommand:
                arguments = EmptyArguments;
                return true;

            case FastForwardCommand fastForward:
                arguments = Serialize(new { tickBudget = fastForward.TickBudget });
                return true;

            case PlayCommand:
            case StartManualControlCommand:
                arguments = EmptyArguments;
                replayabilityReason =
                    "Real-time control entry depends on wall-clock scheduling and is not replayable.";
                return false;

            case MoveAbsoluteCommand moveAbsolute:
                arguments = Serialize(new
                {
                    axisId = moveAbsolute.AxisId,
                    targetPosition = FormatDouble(moveAbsolute.TargetPosition)
                });
                return true;

            case MoveAxesAbsoluteCommand moveAxes:
                arguments = Serialize(new
                {
                    targets = moveAxes.Targets.Select(target => new
                    {
                        axisId = target.AxisId,
                        targetPosition = FormatDouble(target.TargetPosition)
                    })
                });
                return true;

            case MoveRelativeCommand moveRelative:
                arguments = Serialize(new
                {
                    axisId = moveRelative.AxisId,
                    distance = FormatDouble(moveRelative.Distance)
                });
                return true;

            case MoveVelocityCommand moveVelocity:
                arguments = Serialize(new
                {
                    axisId = moveVelocity.AxisId,
                    velocity = FormatDouble(moveVelocity.Velocity)
                });
                return true;

            case HomeAxisCommand home:
                arguments = Serialize(new { axisId = home.AxisId });
                return true;

            case JogAxisCommand jog:
                arguments = Serialize(new
                {
                    axisId = jog.AxisId,
                    direction = jog.Direction
                });
                return true;

            case StopAxisCommand stopAxis:
                arguments = Serialize(new { axisId = stopAxis.AxisId });
                return true;

            case StopAxesCommand stopAxes:
                arguments = Serialize(new { axisIds = stopAxes.AxisIds });
                return true;

            case SetVirtualInputCommand input:
                arguments = Serialize(new
                {
                    channelId = input.ChannelId,
                    value = input.Value
                });
                return true;

            case SetVirtualInputForceCommand inputForce:
                arguments = Serialize(new
                {
                    channelId = inputForce.ChannelId,
                    forcedValue = inputForce.ForcedValue
                });
                return true;

            case SetDigitalSensorForceCommand sensorForce:
                arguments = Serialize(new
                {
                    sensorId = sensorForce.SensorId,
                    forcedValue = sensorForce.ForcedValue
                });
                return true;

            case InjectSimulationFaultCommand inject:
                arguments = Serialize(new
                {
                    kind = inject.Kind,
                    targetId = inject.TargetId,
                    forcedValue = inject.ForcedValue
                });
                return true;

            case ClearSimulationFaultCommand clear:
                arguments = Serialize(new
                {
                    kind = clear.Kind,
                    targetId = clear.TargetId
                });
                return true;

            case SetCylinderCommand cylinder:
                arguments = Serialize(new
                {
                    cylinderId = cylinder.CylinderId,
                    extend = cylinder.Extend
                });
                return true;

            case SetConveyorCommand conveyor:
                arguments = Serialize(new
                {
                    conveyorId = conveyor.ConveyorId,
                    running = conveyor.Running,
                    direction = conveyor.Direction
                });
                return true;

            case StartSequenceCommand startSequence:
                arguments = Serialize(new { sequenceId = startSequence.SequenceId });
                return true;

            case AbortSequenceCommand abortSequence:
                arguments = Serialize(new { sequenceId = abortSequence.SequenceId });
                return true;

            case RetrySequenceCommand retrySequence:
                arguments = Serialize(new { sequenceId = retrySequence.SequenceId });
                return true;

            case StepSequenceCommand stepSequence:
                arguments = Serialize(new { sequenceId = stepSequence.SequenceId });
                return true;

            case SetSequenceBreakpointCommand breakpoint:
                arguments = Serialize(new
                {
                    sequenceId = breakpoint.SequenceId,
                    stepId = breakpoint.StepId,
                    isEnabled = breakpoint.IsEnabled
                });
                return true;

            case StartAutomaticRunCommand automatic:
                arguments = Serialize(new
                {
                    beginRealTime = automatic.BeginRealTime,
                    waitForExternalResult = automatic.WaitForExternalResult
                });
                if (automatic.WaitForExternalResult)
                {
                    replayabilityReason =
                        "Automatic external inspection depends on preflighted frame input and a live consumer; it is not replayable.";
                    return false;
                }

                if (automatic.BeginRealTime)
                {
                    replayabilityReason =
                        "Automatic real-time start depends on wall-clock scheduling and is not replayable.";
                    return false;
                }

                return true;

            case StartConditionScenarioCommand condition:
                arguments = Serialize(new
                {
                    profileJson = DeterministicConditionScenarioProfile.SaveToJson(condition.Profile)
                });
                return true;

            case ConfigureAxesCommand:
                arguments = EmptyArguments;
                replayabilityReason =
                    "Axis configuration is runtime setup and must be supplied before replay.";
                return false;

            case ConfigureRuntimeCommand:
                arguments = EmptyArguments;
                replayabilityReason =
                    "Runtime configuration is setup data and must be supplied before replay.";
                return false;

            case TriggerVirtualCameraCommand:
                arguments = EmptyArguments;
                replayabilityReason =
                    "Camera frame evidence is an external acquisition input and is not replayable by this trace.";
                return false;

            case ApplyExternalInspectionResultCommand:
                arguments = EmptyArguments;
                replayabilityReason =
                    "An external inspection Result is validated file input and is not replayable by this trace.";
                return false;

            case ArmAutomaticExternalInspectionCommand:
                arguments = EmptyArguments;
                replayabilityReason =
                    "Automatic external inspection source metadata is preflighted file input and is not replayable by this trace.";
                return false;

            default:
                arguments = EmptyArguments;
                replayabilityReason =
                    $"Command '{command.GetType().Name}' is outside the paused deterministic replay contract.";
                return false;
        }
    }

    internal static bool TryCreateCommand(
        string commandType,
        JsonElement arguments,
        out SimulationCommand? command,
        out string? error)
    {
        command = null;
        error = null;
        try
        {
            command = CreateCommand(commandType, arguments);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException or
            KeyNotFoundException or JsonException or OverflowException)
        {
            error = $"Command '{commandType}' arguments are invalid: {exception.Message}";
            return false;
        }
    }

    private static SimulationCommand CreateCommand(
        string commandType,
        JsonElement arguments) =>
        commandType switch
        {
            nameof(PauseCommand) => new PauseCommand(),
            nameof(FastForwardCommand) => new FastForwardCommand(
                RequiredPositiveInt(arguments, "tickBudget")),
            nameof(StepCommand) => new StepCommand(),
            nameof(ResetCommand) => new ResetCommand(),
            nameof(StartManualControlCommand) => new StartManualControlCommand(),
            nameof(StopConditionScenarioCommand) => new StopConditionScenarioCommand(),
            nameof(MoveAbsoluteCommand) => new MoveAbsoluteCommand(
                RequiredString(arguments, "axisId"),
                RequiredDouble(arguments, "targetPosition")),
            nameof(MoveAxesAbsoluteCommand) => new MoveAxesAbsoluteCommand(
                RequiredArray(arguments, "targets")
                    .Select(item => new AxisMoveTarget(
                        RequiredString(item, "axisId"),
                        RequiredDouble(item, "targetPosition")))),
            nameof(MoveRelativeCommand) => new MoveRelativeCommand(
                RequiredString(arguments, "axisId"),
                RequiredDouble(arguments, "distance")),
            nameof(MoveVelocityCommand) => new MoveVelocityCommand(
                RequiredString(arguments, "axisId"),
                RequiredDouble(arguments, "velocity")),
            nameof(HomeAxisCommand) => new HomeAxisCommand(
                RequiredString(arguments, "axisId")),
            nameof(JogAxisCommand) => new JogAxisCommand(
                RequiredString(arguments, "axisId"),
                RequiredEnum<AxisJogDirection>(arguments, "direction")),
            nameof(StopAxisCommand) => new StopAxisCommand(
                RequiredString(arguments, "axisId")),
            nameof(StopAxesCommand) => new StopAxesCommand(
                RequiredArray(arguments, "axisIds").Select(item =>
                    item.GetString() ?? throw new JsonException("Axis id is required."))),
            nameof(SetVirtualInputCommand) => new SetVirtualInputCommand(
                RequiredString(arguments, "channelId"),
                RequiredBoolean(arguments, "value")),
            nameof(SetVirtualInputForceCommand) => new SetVirtualInputForceCommand(
                RequiredString(arguments, "channelId"),
                NullableBoolean(arguments, "forcedValue")),
            nameof(SetDigitalSensorForceCommand) => new SetDigitalSensorForceCommand(
                RequiredString(arguments, "sensorId"),
                NullableBoolean(arguments, "forcedValue")),
            nameof(InjectSimulationFaultCommand) => new InjectSimulationFaultCommand(
                RequiredEnum<SimulationFaultKind>(arguments, "kind"),
                RequiredString(arguments, "targetId"),
                NullableBoolean(arguments, "forcedValue")),
            nameof(ClearSimulationFaultCommand) => new ClearSimulationFaultCommand(
                RequiredEnum<SimulationFaultKind>(arguments, "kind"),
                RequiredString(arguments, "targetId")),
            nameof(SetCylinderCommand) => new SetCylinderCommand(
                RequiredString(arguments, "cylinderId"),
                RequiredBoolean(arguments, "extend")),
            nameof(SetConveyorCommand) => new SetConveyorCommand(
                RequiredString(arguments, "conveyorId"),
                RequiredBoolean(arguments, "running"),
                RequiredEnum<ConveyorDirection>(arguments, "direction")),
            nameof(StartSequenceCommand) => new StartSequenceCommand(
                RequiredString(arguments, "sequenceId")),
            nameof(AbortSequenceCommand) => new AbortSequenceCommand(
                RequiredString(arguments, "sequenceId")),
            nameof(RetrySequenceCommand) => new RetrySequenceCommand(
                RequiredString(arguments, "sequenceId")),
            nameof(StepSequenceCommand) => new StepSequenceCommand(
                RequiredString(arguments, "sequenceId")),
            nameof(SetSequenceBreakpointCommand) => new SetSequenceBreakpointCommand(
                RequiredString(arguments, "sequenceId"),
                RequiredString(arguments, "stepId"),
                RequiredBoolean(arguments, "isEnabled")),
            nameof(StartAutomaticRunCommand) => new StartAutomaticRunCommand(
                RequiredBoolean(arguments, "beginRealTime"),
                OptionalBoolean(arguments, "waitForExternalResult")),
            nameof(StartConditionScenarioCommand) => new StartConditionScenarioCommand(
                DeserializeProfile(RequiredString(arguments, "profileJson"))),
            _ => throw new InvalidOperationException(
                $"Command '{commandType}' is not supported by the replay factory.")
        };

    private static DeterministicConditionScenarioProfile DeserializeProfile(string json) =>
        DeterministicConditionScenarioProfile.Normalize(
            JsonSerializer.Deserialize<DeterministicConditionScenarioProfile>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                }) ?? throw new JsonException("Condition scenario profile is required."));

    private static JsonElement Serialize(object value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private static JsonElement RequiredProperty(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value))
        {
            throw new JsonException($"Argument '{name}' is required.");
        }

        return value;
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        var value = RequiredProperty(arguments, name);
        return value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"Argument '{name}' must be a non-empty string.");
    }

    private static bool RequiredBoolean(JsonElement arguments, string name) =>
        RequiredProperty(arguments, name).ValueKind == JsonValueKind.True
            ? true
            : RequiredProperty(arguments, name).ValueKind == JsonValueKind.False
                ? false
                : throw new JsonException($"Argument '{name}' must be a Boolean.");

    private static bool? NullableBoolean(JsonElement arguments, string name)
    {
        var value = RequiredProperty(arguments, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"Argument '{name}' must be a nullable Boolean.")
        };
    }

    private static bool OptionalBoolean(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"Argument '{name}' must be a Boolean.")
        };
    }

    private static double RequiredDouble(JsonElement arguments, string name)
    {
        var value = RequiredString(arguments, name);
        return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static int RequiredPositiveInt(JsonElement arguments, string name)
    {
        var value = RequiredProperty(arguments, name);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed)
            || parsed <= 0)
        {
            throw new JsonException($"Argument '{name}' must be a positive integer.");
        }

        return parsed;
    }

    private static TEnum RequiredEnum<TEnum>(JsonElement arguments, string name)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(RequiredString(arguments, name), ignoreCase: true);

    private static JsonElement.ArrayEnumerator RequiredArray(
        JsonElement arguments,
        string name)
    {
        var value = RequiredProperty(arguments, name);
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw new JsonException($"Argument '{name}' must be an array.");
    }

    private static string FormatDouble(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
