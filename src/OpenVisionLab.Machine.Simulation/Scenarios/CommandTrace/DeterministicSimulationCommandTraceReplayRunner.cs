using System.Collections.Immutable;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

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
