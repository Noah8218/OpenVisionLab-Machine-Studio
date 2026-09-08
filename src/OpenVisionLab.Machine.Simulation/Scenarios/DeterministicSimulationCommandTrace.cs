using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

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
