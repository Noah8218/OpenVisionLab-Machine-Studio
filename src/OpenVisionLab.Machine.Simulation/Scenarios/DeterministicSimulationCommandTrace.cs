using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// One command boundary without wall-clock command identity. Arguments are
/// serialized only for commands in the paused deterministic replay contract.
/// </summary>
public sealed record DeterministicSimulationCommandTraceEntry(
    int Sequence,
    string CommandType,
    long AppliedTick,
    long SimulationTimeTicks,
    bool IsAccepted,
    SimulationCommandErrorCode ErrorCode,
    string? Detail,
    JsonElement Arguments,
    bool IsReplayable,
    string? ReplayabilityReason)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonElement EmptyArguments =
        JsonSerializer.SerializeToElement(new { }, JsonOptions);

    internal static DeterministicSimulationCommandTraceEntry Capture(
        int sequence,
        SimulationCommand command,
        SimulationCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        bool replayable = TrySerializeArguments(
            command,
            out var arguments,
            out var replayabilityReason);
        return new(
            sequence,
            command.GetType().Name,
            result.AppliedTick,
            result.SimulationTime.Ticks,
            result.IsAccepted,
            result.ErrorCode,
            result.Detail,
            arguments,
            replayable,
            replayabilityReason);
    }

    public bool TryCreateCommand(
        out SimulationCommand? command,
        out string? error)
    {
        command = null;
        error = null;
        if (!IsReplayable)
        {
            error = ReplayabilityReason ??
                $"Command '{CommandType}' is not replayable.";
            return false;
        }

        try
        {
            command = CreateCommand(CommandType, Arguments);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException or
            KeyNotFoundException or JsonException or OverflowException)
        {
            error = $"Command '{CommandType}' arguments are invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TrySerializeArguments(
        SimulationCommand command,
        out JsonElement arguments,
        out string? replayabilityReason)
    {
        replayabilityReason = null;
        switch (command)
        {
            case PauseCommand:
            case StepCommand:
            case ResetCommand:
            case StopConditionScenarioCommand:
                arguments = EmptyArguments;
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
                arguments = Serialize(new { beginRealTime = automatic.BeginRealTime });
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

            default:
                arguments = EmptyArguments;
                replayabilityReason =
                    $"Command '{command.GetType().Name}' is outside the paused deterministic replay contract.";
                return false;
        }
    }

    private static SimulationCommand CreateCommand(
        string commandType,
        JsonElement arguments) =>
        commandType switch
        {
            nameof(PauseCommand) => new PauseCommand(),
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
                RequiredBoolean(arguments, "beginRealTime")),
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

    private static double RequiredDouble(JsonElement arguments, string name)
    {
        var value = RequiredString(arguments, name);
        return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
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

/// <summary>
/// Portable command-boundary trace for the deterministic paused replay path.
/// It contains no project path or RuntimeDebugger session state.
/// </summary>
public sealed record DeterministicSimulationCommandTracePackage(
    int SchemaVersion,
    long FixedStepTicks,
    ImmutableArray<DeterministicSimulationCommandTraceEntry> Entries,
    string TraceHash)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public bool CanReplay =>
        HasValidTraceHash()
        && Entries.All(entry => entry.IsReplayable);

    public static DeterministicSimulationCommandTracePackage Create(
        TimeSpan fixedStep,
        IEnumerable<DeterministicSimulationCommandTraceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStep), "Fixed step must be positive.");
        }

        var materialized = entries.ToImmutableArray();
        return new(
            CurrentSchemaVersion,
            fixedStep.Ticks,
            materialized,
            Hash(CurrentSchemaVersion, fixedStep.Ticks, materialized));
    }

    public bool HasValidTraceHash()
    {
        var entries = Entries.IsDefault
            ? ImmutableArray<DeterministicSimulationCommandTraceEntry>.Empty
            : Entries;
        if (SchemaVersion != CurrentSchemaVersion
            || FixedStepTicks <= 0
            || entries.Where((entry, index) =>
                    entry.Sequence != index + 1
                    || entry.AppliedTick < 0
                    || entry.SimulationTimeTicks < 0
                    || entry.Arguments.ValueKind != JsonValueKind.Object
                    || (!entry.IsReplayable && string.IsNullOrWhiteSpace(entry.ReplayabilityReason)))
                .Any())
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.IsReplayable && !entry.TryCreateCommand(out _, out _))
            {
                return false;
            }
        }

        return string.Equals(
            TraceHash,
            Hash(SchemaVersion, FixedStepTicks, entries),
            StringComparison.Ordinal);
    }

    public static string SaveToJson(DeterministicSimulationCommandTracePackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static void SaveToJson(
        DeterministicSimulationCommandTracePackage package,
        string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidTraceHash())
        {
            throw new InvalidOperationException("Invalid command trace cannot be saved.");
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

    public static DeterministicSimulationCommandTracePackage? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicSimulationCommandTracePackage>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Hash(
        int schemaVersion,
        long fixedStepTicks,
        IEnumerable<DeterministicSimulationCommandTraceEntry> entries)
    {
        var builder = new StringBuilder()
            .Append(schemaVersion).Append('|')
            .Append(fixedStepTicks).Append('\n');
        foreach (var entry in entries)
        {
            builder.Append(entry.Sequence).Append('|')
                .Append(entry.CommandType).Append('|')
                .Append(entry.AppliedTick).Append('|')
                .Append(entry.SimulationTimeTicks).Append('|')
                .Append(entry.IsAccepted).Append('|')
                .Append(entry.ErrorCode).Append('|')
                .Append(entry.Detail).Append('|')
                .Append(entry.IsReplayable).Append('|')
                .Append(entry.ReplayabilityReason).Append('|')
                .Append(JsonSerializer.Serialize(entry.Arguments)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public sealed record DeterministicSimulationCommandTraceMismatch(
    int Sequence,
    string Code,
    string Detail,
    long ExpectedTick,
    long ActualTick,
    SimulationCommandErrorCode ExpectedErrorCode,
    SimulationCommandErrorCode ActualErrorCode);

public sealed record DeterministicSimulationCommandTraceReplayResult(
    bool IsSuccess,
    int AppliedEntries,
    ImmutableArray<SimulationCommandResult> CommandResults,
    DeterministicSimulationCommandTraceMismatch? FirstMismatch,
    string? FailureReason);

/// <summary>
/// Replays a validated command trace through the existing engine queue. The
/// target engine must already contain the same authored/runtime setup and be
/// paused; setup is deliberately not inferred from the trace.
/// </summary>
public sealed class DeterministicSimulationCommandTraceReplayRunner
{
    public async Task<DeterministicSimulationCommandTraceReplayResult> ReplayAsync(
        FixedStepSimulationEngine engine,
        DeterministicSimulationCommandTracePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(package);

        var commandResults = ImmutableArray.CreateBuilder<SimulationCommandResult>();
        if (!package.HasValidTraceHash())
        {
            return Failure(commandResults, "The command trace hash or schema is invalid.");
        }

        if (!package.CanReplay)
        {
            return Failure(
                commandResults,
                "The command trace contains a real-time or unsupported command.");
        }

        if (engine.FixedStep.Ticks != package.FixedStepTicks)
        {
            return Failure(commandResults, "The trace fixed step does not match the target engine.");
        }

        if (engine.CurrentSnapshot.RunMode != SimulationRunMode.Paused)
        {
            return Failure(commandResults, "The target engine must be paused before replay.");
        }

        foreach (var entry in package.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (engine.CurrentSnapshot.TickIndex < entry.AppliedTick)
            {
                var step = await engine.EnqueueCommandAsync(
                    new StepCommand(),
                    cancellationToken).ConfigureAwait(false);
                if (!step.IsAccepted)
                {
                    commandResults.Add(step);
                    return Failure(
                        commandResults,
                        $"Replay could not reach Tick {entry.AppliedTick}: {step.Detail}");
                }
            }

            if (engine.CurrentSnapshot.TickIndex > entry.AppliedTick)
            {
                return Failure(
                    commandResults,
                    CreateMismatch(
                        entry,
                        engine.CurrentSnapshot.TickIndex,
                        SimulationCommandErrorCode.None,
                        "The target engine passed the recorded command boundary."));
            }

            if (!entry.TryCreateCommand(out var command, out var commandError)
                || command is null)
            {
                return Failure(commandResults, commandError ?? "The command could not be reconstructed.");
            }

            var actual = await engine.EnqueueCommandAsync(command, cancellationToken)
                .ConfigureAwait(false);
            commandResults.Add(actual);
            if (!Matches(entry, actual))
            {
                return Failure(
                    commandResults,
                    CreateMismatch(
                        entry,
                        actual.AppliedTick,
                        actual.ErrorCode,
                        "The replay command result differs from the recorded boundary."));
            }
        }

        return new(
            true,
            package.Entries.Length,
            commandResults.ToImmutable(),
            null,
            null);
    }

    private static bool Matches(
        DeterministicSimulationCommandTraceEntry expected,
        SimulationCommandResult actual) =>
        expected.AppliedTick == actual.AppliedTick
        && expected.SimulationTimeTicks == actual.SimulationTime.Ticks
        && expected.IsAccepted == actual.IsAccepted
        && expected.ErrorCode == actual.ErrorCode
        && string.Equals(expected.Detail, actual.Detail, StringComparison.Ordinal);

    private static DeterministicSimulationCommandTraceReplayResult Failure(
        ImmutableArray<SimulationCommandResult>.Builder commandResults,
        string reason) =>
        new(false, commandResults.Count, commandResults.ToImmutable(), null, reason);

    private static DeterministicSimulationCommandTraceReplayResult Failure(
        ImmutableArray<SimulationCommandResult>.Builder commandResults,
        DeterministicSimulationCommandTraceMismatch mismatch) =>
        new(false, commandResults.Count, commandResults.ToImmutable(), mismatch, mismatch.Detail);

    private static DeterministicSimulationCommandTraceMismatch CreateMismatch(
        DeterministicSimulationCommandTraceEntry expected,
        long actualTick,
        SimulationCommandErrorCode actualErrorCode,
        string detail) =>
        new(
            expected.Sequence,
            "CommandResultMismatch",
            detail,
            expected.AppliedTick,
            actualTick,
            expected.ErrorCode,
            actualErrorCode);
}
