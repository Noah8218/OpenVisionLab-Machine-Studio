using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationTimeScaleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task SpeedChangePreservesPausedClockAndOneFixedTick(double speed)
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var before = engine.CurrentSnapshot;
        Assert.True((await engine.EnqueueCommandAsync(new SetSimulationTimeScaleCommand(speed))).IsAccepted);
        var changed = engine.CurrentSnapshot;
        Assert.Equal(speed, changed.TimeScale);
        Assert.Equal(SimulationRunMode.Paused, changed.RunMode);
        Assert.Equal(before.TickIndex, changed.TickIndex);
        Assert.Equal(before.SimulationTime, changed.SimulationTime);
        Assert.Equal(before.RuntimeGeneration, changed.RuntimeGeneration);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Equal(before.TickIndex + 1, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(before.SimulationTime + TimeSpan.FromMilliseconds(5), engine.CurrentSnapshot.SimulationTime);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(speed, engine.CurrentSnapshot.TimeScale);
        Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidSpeedDoesNotChangeStateAndValidRetryRecovers(double invalid)
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings());
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new SetSimulationTimeScaleCommand(2))).IsAccepted);
        var rejected = await engine.EnqueueCommandAsync(new SetSimulationTimeScaleCommand(invalid));
        Assert.False(rejected.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.TimeScaleInvalid, rejected.ErrorCode);
        Assert.Equal(2, engine.CurrentSnapshot.TimeScale);
        Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
        Assert.True((await engine.EnqueueCommandAsync(new SetSimulationTimeScaleCommand(4))).IsAccepted);
        Assert.Equal(4, engine.CurrentSnapshot.TimeScale);
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void RealTimePacingUsesSelectedMultiplierAndTraceRetainsIt(double speed)
    {
        var timing = new SimulationRunLoopTiming(TimeSpan.FromMilliseconds(5), 100);
        timing.Reset(TimeSpan.Zero);
        Assert.Equal((int)(2 * speed), timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(10), speed));
        Assert.Equal(TimeSpan.FromMilliseconds(Math.Max(1, 5 / speed)), timing.CalculateRealTimeDelay(speed));
        var command = new SetSimulationTimeScaleCommand(speed);
        Assert.True(DeterministicSimulationCommandTraceCommandCodec.TrySerializeArguments(command, out var args, out var reason), reason);
        Assert.True(DeterministicSimulationCommandTraceCommandCodec.TryCreateCommand(nameof(SetSimulationTimeScaleCommand), args, out var restored, out var error), error);
        Assert.Equal(speed, Assert.IsType<SetSimulationTimeScaleCommand>(restored).TimeScale);
    }
}
