using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
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
        var path = @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\pl-0026-command-trace\trace-roundtrip.json";
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
