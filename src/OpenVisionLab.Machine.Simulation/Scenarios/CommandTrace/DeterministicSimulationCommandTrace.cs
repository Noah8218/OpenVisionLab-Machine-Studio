using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Commands;

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
    internal static DeterministicSimulationCommandTraceEntry Capture(
        int sequence,
        SimulationCommand command,
        SimulationCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        bool replayable = DeterministicSimulationCommandTraceCommandCodec.TrySerializeArguments(
            command,
            out var arguments,
            out var replayabilityReason);
        if (command.ExpectedRuntime.HasValue)
        {
            // Keep arguments/results for diagnosis. A portable replay cannot infer
            // the live session identity or silently drop this admission condition.
            replayable = false;
            replayabilityReason = "Runtime-bound commands require their original runtime identity and cannot be replayed portably.";
        }
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
        if (!IsReplayable)
        {
            command = null;
            error = ReplayabilityReason ?? $"Command '{CommandType}' is not replayable.";
            return false;
        }

        return DeterministicSimulationCommandTraceCommandCodec.TryCreateCommand(
            CommandType,
            Arguments,
            out command,
            out error);
    }
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

        AtomicEvidenceFile.Write(fullPath, temporaryPath => File.WriteAllText(temporaryPath, SaveToJson(package)));
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
