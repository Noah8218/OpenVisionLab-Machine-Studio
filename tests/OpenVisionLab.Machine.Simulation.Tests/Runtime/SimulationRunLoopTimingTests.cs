using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationRunLoopTimingTests
{
    [Fact]
    public void CalculateRealTimeTicks_ConsumesWholeStepsAndRetainsRemainder()
    {
        var timing = new SimulationRunLoopTiming(
            TimeSpan.FromMilliseconds(5),
            maxCatchUpTicks: 10);
        timing.Reset(TimeSpan.Zero);

        Assert.Equal(2, timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(12), timeScale: 1));
        Assert.Equal(TimeSpan.FromMilliseconds(2), timing.Accumulator);
        Assert.Equal(1, timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(15), timeScale: 1));
        Assert.Equal(TimeSpan.Zero, timing.Accumulator);
    }

    [Fact]
    public void CalculateRealTimeTicks_CapsCatchUpWithoutDroppingRemainder()
    {
        var timing = new SimulationRunLoopTiming(
            TimeSpan.FromMilliseconds(5),
            maxCatchUpTicks: 2);
        timing.Reset(TimeSpan.Zero);

        Assert.Equal(2, timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(100), timeScale: 1));
        Assert.Equal(TimeSpan.FromMilliseconds(90), timing.Accumulator);
        Assert.Equal(TimeSpan.FromMilliseconds(1), timing.CalculateRealTimeDelay(timeScale: 1));
    }

    [Fact]
    public void ResetAndAlignToWallTimePreserveTheirDistinctAccumulatorSemantics()
    {
        var timing = new SimulationRunLoopTiming(
            TimeSpan.FromMilliseconds(10),
            maxCatchUpTicks: 10);
        timing.Reset(TimeSpan.Zero);
        Assert.Equal(0, timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(3), timeScale: 1));

        timing.AlignToWallTime(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(107), timeScale: 1));
        Assert.Equal(TimeSpan.FromMilliseconds(0), timing.Accumulator);

        timing.Reset(TimeSpan.FromMilliseconds(200));
        Assert.Equal(TimeSpan.Zero, timing.Accumulator);
        Assert.Equal(TimeSpan.FromMilliseconds(5), timing.CalculateRealTimeDelay(timeScale: 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTimeScale_IsRejected(double timeScale)
    {
        var timing = new SimulationRunLoopTiming(
            TimeSpan.FromMilliseconds(5),
            maxCatchUpTicks: 10);
        timing.Reset(TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            timing.CalculateRealTimeTicks(TimeSpan.FromMilliseconds(5), timeScale));
        Assert.Throws<ArgumentOutOfRangeException>(() => timing.CalculateRealTimeDelay(timeScale));
    }
}
