using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSimulationCommandTraceTests
{
    [Fact]
    public async Task Trace_CapturesDeterministicBoundariesAndRoundTripsWithoutSessionIdentity()
    {
        using var engine = await CreateConfiguredEngineAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new SetVirtualInputCommand("input.start", true))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(
            new SetVirtualInputCommand("input.start", false))).IsAccepted);

        var package = engine.CreateCommandTracePackage();
        var json = DeterministicSimulationCommandTracePackage.SaveToJson(package);
        var path = Path.Combine(
            TestStorage.RootPath,
            "pl-0026-command-trace",
            "trace-roundtrip.json");
        DeterministicSimulationCommandTracePackage.SaveToJson(package, path);
        var restored = DeterministicSimulationCommandTracePackage.LoadFromJson(path);

        Assert.True(package.HasValidTraceHash());
        Assert.NotNull(restored);
        Assert.True(restored!.HasValidTraceHash());
        Assert.Equal(package.TraceHash, restored.TraceHash);
        Assert.Equal(json, DeterministicSimulationCommandTracePackage.SaveToJson(restored));
        Assert.Equal(3, package.Entries.Length);
        Assert.Equal([0L, 0L, 1L], package.Entries.Select(entry => entry.AppliedTick));
        Assert.DoesNotContain("commandId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RuntimeDebugger", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acknowledg", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandCodec_RoundTripsTypedArgumentsWithoutEngineState()
    {
        var command = new MoveAbsoluteCommand("x", 12.5);

        Assert.True(
            DeterministicSimulationCommandTraceCommandCodec.TrySerializeArguments(
                command,
                out var arguments,
                out var replayabilityReason),
            replayabilityReason);
        Assert.True(
            DeterministicSimulationCommandTraceCommandCodec.TryCreateCommand(
                nameof(MoveAbsoluteCommand),
                arguments,
                out var restored,
                out var error),
            error);

        var restoredMove = Assert.IsType<MoveAbsoluteCommand>(restored);
        Assert.Equal(command.AxisId, restoredMove.AxisId);
        Assert.Equal(command.TargetPosition, restoredMove.TargetPosition);
    }

    [Fact]
    public void CommandCodec_ReportsNonReplayableRealTimeCommands()
    {
        Assert.False(
            DeterministicSimulationCommandTraceCommandCodec.TrySerializeArguments(
                new PlayCommand(),
                out _,
                out var replayabilityReason));
        Assert.Contains("real-time", replayabilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_RoutesThroughEngineAndPreservesCommandResultsAndState()
    {
        using var source = await CreateConfiguredEngineAsync();
        Assert.True((await source.EnqueueCommandAsync(
            new SetVirtualInputCommand("input.start", true))).IsAccepted);
        Assert.True((await source.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True((await source.EnqueueCommandAsync(
            new SetVirtualInputCommand("input.start", false))).IsAccepted);
        var package = source.CreateCommandTracePackage();

        using var target = await CreateConfiguredEngineAsync();
        var replay = await new DeterministicSimulationCommandTraceReplayRunner()
            .ReplayAsync(target, package);

        Assert.True(replay.IsSuccess, replay.FailureReason);
        Assert.Equal(package.Entries.Length, replay.AppliedEntries);
        Assert.Equal(package.Entries.Length, replay.CommandResults.Length);
        Assert.Equal(source.CurrentSnapshot.TickIndex, target.CurrentSnapshot.TickIndex);
        Assert.Equal(source.CurrentSnapshot.SimulationTime, target.CurrentSnapshot.SimulationTime);
        Assert.Equal(
            source.CurrentSnapshot.Signals.Single(signal => signal.Id == "input.start").Value,
            target.CurrentSnapshot.Signals.Single(signal => signal.Id == "input.start").Value);
        Assert.Equal(package.TraceHash, target.CreateCommandTracePackage().TraceHash);
    }

    [Fact]
    public async Task Replay_RejectsTamperedAndRealTimeTracesBeforeMutation()
    {
        using var source = await CreateConfiguredEngineAsync();
        Assert.True((await source.EnqueueCommandAsync(new PlayCommand())).IsAccepted);
        var realTimePackage = source.CreateCommandTracePackage();

        using var target = await CreateConfiguredEngineAsync();
        var initialTick = target.CurrentSnapshot.TickIndex;
        var runner = new DeterministicSimulationCommandTraceReplayRunner();
        var realTimeReplay = await runner.ReplayAsync(target, realTimePackage);

        Assert.False(realTimeReplay.IsSuccess);
        Assert.Contains("real-time", realTimeReplay.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(initialTick, target.CurrentSnapshot.TickIndex);

        var tampered = realTimePackage with
        {
            TraceHash = new string('0', 64)
        };
        var tamperedReplay = await runner.ReplayAsync(target, tampered);
        Assert.False(tamperedReplay.IsSuccess);
        Assert.Contains("hash", tamperedReplay.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(initialTick, target.CurrentSnapshot.TickIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeBoundTracePreservesArgumentsAndOutcomeButCannotReplayWithoutItsAdmission(bool stale)
    {
        using var source = new FixedStepSimulationEngine(new SimulationSettings());
        await source.StartAsync();
        try
        {
            Assert.True((await source.EnqueueCommandAsync(new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration([new AxisConfiguration { Id = "x" }], [], []), "source-project"))).IsAccepted);
            Assert.True((await source.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
            Assert.True((await source.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
            source.ClearCommandTrace();
            var snapshot = source.CurrentSnapshot;
            var command = new MoveAxesAbsoluteCommand([new AxisMoveTarget("x", 12.5)])
            {
                ExpectedRuntime = new SimulationRuntimeIdentity(snapshot.ProjectId, snapshot.RuntimeGeneration + (stale ? 1 : 0))
            };
            var result = await source.EnqueueCommandAsync(command);
            Assert.Equal(!stale, result.IsAccepted);
            var package = source.CreateCommandTracePackage();
            var entry = Assert.Single(package.Entries);
            Assert.Equal(result.ErrorCode, entry.ErrorCode);
            Assert.Equal(result.Detail, entry.Detail);
            Assert.False(entry.IsReplayable);
            Assert.Contains("runtime identity", entry.ReplayabilityReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("x", entry.Arguments.GetProperty("targets")[0].GetProperty("axisId").GetString());
            Assert.Equal("12.5", entry.Arguments.GetProperty("targets")[0].GetProperty("targetPosition").GetString());
            Assert.False(entry.TryCreateCommand(out var restoredCommand, out var reason));
            Assert.Null(restoredCommand);
            Assert.Equal(entry.ReplayabilityReason, reason);

            var path = Path.Combine(TestStorage.RootPath, "runtime-command-admission", $"bound-trace-{stale}.json");
            DeterministicSimulationCommandTracePackage.SaveToJson(package, path);
            var restored = DeterministicSimulationCommandTracePackage.LoadFromJson(path);
            Assert.NotNull(restored);
            Assert.True(restored.HasValidTraceHash());
            Assert.False(restored.CanReplay);
            Assert.Equal(package.TraceHash, restored.TraceHash);
            Assert.Equal(DeterministicSimulationCommandTracePackage.SaveToJson(package),
                DeterministicSimulationCommandTracePackage.SaveToJson(restored));

            using var target = await CreateConfiguredEngineAsync();
            try
            {
                var before = target.CurrentSnapshot;
                var traceCount = target.CommandTrace.Length;
                var eventCount = target.EventJournal.TotalEventCount;
                var replay = await new DeterministicSimulationCommandTraceReplayRunner().ReplayAsync(target, restored);
                Assert.False(replay.IsSuccess);
                Assert.Empty(replay.CommandResults);
                Assert.Equal(traceCount, target.CommandTrace.Length);
                Assert.Equal(eventCount, target.EventJournal.TotalEventCount);
                Assert.Same(before, target.CurrentSnapshot);
            }
            finally { await target.StopAsync(); }
        }
        finally { await source.StopAsync(); }
    }

    private static async Task<FixedStepSimulationEngine> CreateConfiguredEngineAsync()
    {
        var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        engine.AddAxis(new AxisServoFixture().Create());
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<AxisConfiguration>(),
                    new[]
                    {
                        new ChannelDefinition
                        {
                            Id = "input.start",
                            Name = "Start input",
                            Kind = ChannelKind.DigitalInput,
                            InitialValue = 0
                        }
                    },
                    Array.Empty<CompiledSequence>(),
                    Array.Empty<VirtualCameraConfiguration>())));
        Assert.True(configured.IsAccepted, configured.Detail);
        engine.ClearCommandTrace();
        return engine;
    }

    private sealed class AxisServoFixture
    {
        public ServoAxisComponent Create() => new(new AxisConfiguration
        {
            Id = "x",
            Name = "X Axis",
            MinimumPosition = 0,
            MaximumPosition = 300,
            HomePosition = 0,
            MaximumVelocity = 200,
            Acceleration = 500,
            Deceleration = 500
        });
    }
}
