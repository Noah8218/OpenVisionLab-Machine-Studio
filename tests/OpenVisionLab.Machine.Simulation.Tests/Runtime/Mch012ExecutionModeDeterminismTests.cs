using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch012ExecutionModeDeterminismTests
{
    [Fact]
    public async Task PausedSingleStepsAndFastForwardProduceTheSameSemanticSnapshot()
    {
        var stepped = await RunManualMoveAsync(useFastForward: false);
        var fastForwarded = await RunManualMoveAsync(useFastForward: true);

        Assert.Equal(stepped.TickIndex, fastForwarded.TickIndex);
        Assert.Equal(stepped.SimulationTime, fastForwarded.SimulationTime);
        Assert.Equal(stepped.RunMode, fastForwarded.RunMode);
        Assert.Equal(stepped.ControlOwner, fastForwarded.ControlOwner);

        var steppedAxis = Assert.Single(stepped.Axes);
        var fastForwardedAxis = Assert.Single(fastForwarded.Axes);
        Assert.Equal(steppedAxis.Id, fastForwardedAxis.Id);
        Assert.Equal(steppedAxis.State, fastForwardedAxis.State);
        Assert.Equal(steppedAxis.Position, fastForwardedAxis.Position, precision: 12);
        Assert.Equal(steppedAxis.Velocity, fastForwardedAxis.Velocity, precision: 12);
        Assert.Equal(steppedAxis.CommandPosition, fastForwardedAxis.CommandPosition, precision: 12);
        Assert.Equal(steppedAxis.FollowingError, fastForwardedAxis.FollowingError, precision: 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task RealTimeAndPausedProduceTheSameSemanticSnapshotAtTheObservedTickBoundary(int minimumSetupTick)
    {
        const int targetTick = 5;
        var (realtime, motionStartTick) = await RunRealTimeManualMoveAsync(targetTick, minimumSetupTick);
        var stepped = await RunPausedManualMoveAsync(realtime.TickIndex, motionStartTick);

        Assert.Equal(stepped.TickIndex, realtime.TickIndex);
        Assert.Equal(stepped.SimulationTime, realtime.SimulationTime);
        Assert.Equal(SimulationRunMode.Paused, realtime.RunMode);
        Assert.Equal(stepped.RunMode, realtime.RunMode);
        Assert.Equal(stepped.ControlOwner, realtime.ControlOwner);

        var steppedAxis = Assert.Single(stepped.Axes);
        var realtimeAxis = Assert.Single(realtime.Axes);
        Assert.Equal(steppedAxis.Id, realtimeAxis.Id);
        Assert.Equal(steppedAxis.State, realtimeAxis.State);
        Assert.Equal(steppedAxis.Position, realtimeAxis.Position, precision: 12);
        Assert.Equal(steppedAxis.Velocity, realtimeAxis.Velocity, precision: 12);
        Assert.Equal(steppedAxis.CommandPosition, realtimeAxis.CommandPosition, precision: 12);
        Assert.Equal(steppedAxis.FollowingError, realtimeAxis.FollowingError, precision: 12);
    }

    [Fact]
    public async Task PausedSingleStepsAndFastForwardPreserveSequenceCompletionBoundary()
    {
        var stepped = await RunSequenceAsync(useFastForward: false);
        var fastForwarded = await RunSequenceAsync(useFastForward: true);

        Assert.Equal(stepped.TickIndex, fastForwarded.TickIndex);
        Assert.Equal(stepped.SimulationTime, fastForwarded.SimulationTime);
        Assert.Equal(stepped.RunMode, fastForwarded.RunMode);
        Assert.Equal(stepped.ControlOwner, fastForwarded.ControlOwner);

        var steppedSequence = Assert.Single(stepped.Sequences);
        var fastForwardedSequence = Assert.Single(fastForwarded.Sequences);
        Assert.Equal(SequenceExecutionStatus.Completed, steppedSequence.Status);
        Assert.Equal(steppedSequence.Status, fastForwardedSequence.Status);
        Assert.Equal(steppedSequence.CurrentStepId, fastForwardedSequence.CurrentStepId);
        Assert.Equal(
            Assert.Single(stepped.Signals, signal => signal.Id == "do.active").Value,
            Assert.Single(fastForwarded.Signals, signal => signal.Id == "do.active").Value);
    }

    [Fact]
    public async Task RealTimeAndPausedPreserveSequenceCompletionSemantics()
    {
        var realtime = await RunSequenceAsync(useFastForward: false, useRealTime: true);
        var paused = await RunSequenceAsync(useFastForward: false);

        var realtimeSequence = Assert.Single(realtime.Sequences);
        var pausedSequence = Assert.Single(paused.Sequences);
        Assert.Equal(SequenceExecutionStatus.Completed, realtimeSequence.Status);
        Assert.Equal(pausedSequence.Status, realtimeSequence.Status);
        Assert.Equal(pausedSequence.CurrentStepId, realtimeSequence.CurrentStepId);
        Assert.Equal(pausedSequence.CurrentStepIndex, realtimeSequence.CurrentStepIndex);
        Assert.Equal(pausedSequence.ActiveSequenceId, realtimeSequence.ActiveSequenceId);
        Assert.Equal(
            Assert.Single(paused.Signals, signal => signal.Id == "do.active").Value,
            Assert.Single(realtime.Signals, signal => signal.Id == "do.active").Value);
        Assert.Equal(paused.ControlOwner, realtime.ControlOwner);
    }

    private static async Task<SimulationSnapshot> RunManualMoveAsync(bool useFastForward)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(5),
                MaxCatchUpTicks = 3,
                TimeScale = 0.000001
            });
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfiguration()));
        await engine.StartAsync();

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 100))).IsAccepted);

        const int tickBudget = 20;
        if (useFastForward)
        {
            Assert.True((await engine.EnqueueCommandAsync(new FastForwardCommand(tickBudget))).IsAccepted);
            await WaitUntilAsync(() =>
                engine.CurrentSnapshot.TickIndex == tickBudget
                && engine.CurrentSnapshot.RunMode == SimulationRunMode.Paused);
        }
        else
        {
            for (var index = 0; index < tickBudget; index++)
            {
                Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            }
        }

        var snapshot = engine.CurrentSnapshot;
        await engine.StopAsync();
        return snapshot;
    }

    private static async Task<SimulationSnapshot> RunPausedManualMoveAsync(long tickBudget, long motionStartTick)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(5),
                MaxCatchUpTicks = 3,
                TimeScale = 0.000001
            });
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfiguration()));
        await engine.StartAsync();

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        for (var index = 0; index < motionStartTick; index++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }
        Assert.True((await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 100))).IsAccepted);
        for (var index = motionStartTick; index < tickBudget; index++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }

        var snapshot = engine.CurrentSnapshot;
        await engine.StopAsync();
        return snapshot;
    }

    private static async Task<(SimulationSnapshot Snapshot, long MotionStartTick)> RunRealTimeManualMoveAsync(int targetTick, int minimumSetupTick)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(5),
                MaxCatchUpTicks = 1,
                TimeScale = 0.5
            });
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfiguration()));
        await engine.StartAsync();

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        if (minimumSetupTick > 0) await WaitUntilAsync(() => engine.CurrentSnapshot.TickIndex >= minimumSetupTick);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var motionStartTick = engine.CurrentSnapshot.TickIndex;
        Assert.True((await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 100))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PlayCommand())).IsAccepted);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        SimulationSnapshot? observed = null;
        while (await engine.SnapshotReader.WaitToReadAsync(timeout.Token))
        {
            while (engine.SnapshotReader.TryRead(out var candidate))
            {
                if (candidate.TickIndex >= motionStartTick + targetTick)
                {
                    observed = candidate;
                    break;
                }
            }

            if (observed is not null)
            {
                break;
            }
        }

        Assert.NotNull(observed);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var snapshot = engine.CurrentSnapshot;
        await engine.StopAsync();
        return (snapshot, motionStartTick);
    }

    private static async Task<SimulationSnapshot> RunSequenceAsync(
        bool useFastForward,
        bool useRealTime = false)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(5),
                MaxCatchUpTicks = 3,
                TimeScale = useRealTime ? 1.0 : 0.000001
            });
        await engine.StartAsync();

        var compiled = new SequenceCompiler().Compile(
            new SequenceDefinition
            {
                Id = "sequence",
                Name = "Sequence",
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "set-active",
                        Name = "Set active",
                        Action = SequenceStepAction.SetSignal,
                        TargetId = "do.active",
                        Parameter = "true",
                        NextStepId = "complete"
                    },
                    new SequenceStepDefinition
                    {
                        Id = "complete",
                        Name = "Complete",
                        Action = SequenceStepAction.Complete
                    }
                }
            },
            new SequenceCompilationTargets(
                new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
                {
                    ["do.active"] = ChannelKind.DigitalOutput
                },
                Array.Empty<string>()));
        Assert.True(compiled.IsSuccess, string.Join(", ", compiled.Errors.Select(error => error.Message)));

        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<AxisConfiguration>(),
                    new[]
                    {
                        new ChannelDefinition
                        {
                            Id = "do.active",
                            Name = "Active",
                            Kind = ChannelKind.DigitalOutput
                        }
                    },
                    new[] { compiled.Sequence! })));
        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("sequence"))).IsAccepted);

        const int tickBudget = 2;
        if (useRealTime)
        {
            Assert.True((await engine.EnqueueCommandAsync(new PlayCommand())).IsAccepted);
            await WaitUntilAsync(() =>
                engine.CurrentSnapshot.Sequences.Count == 1
                && engine.CurrentSnapshot.Sequences[0].Status == SequenceExecutionStatus.Completed);
        }
        else if (useFastForward)
        {
            Assert.True((await engine.EnqueueCommandAsync(new FastForwardCommand(tickBudget))).IsAccepted);
            await WaitUntilAsync(() =>
                engine.CurrentSnapshot.TickIndex == tickBudget
                && engine.CurrentSnapshot.RunMode == SimulationRunMode.Paused);
        }
        else
        {
            for (var index = 0; index < tickBudget; index++)
            {
                Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            }
        }

        var snapshot = engine.CurrentSnapshot;
        await engine.StopAsync();
        return snapshot;
    }

    private static AxisConfiguration CreateAxisConfiguration() => new()
    {
        Id = "x",
        Name = "X Axis",
        MinimumPosition = 0,
        MaximumPosition = 300,
        HomePosition = 0,
        MaximumVelocity = 200,
        Acceleration = 500,
        Deceleration = 500
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
