using OpenVisionLab.Machine.Simulation.Axis;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public class TrapezoidalMotionProfileTests
{
    [Fact]
    public void Trapezoidal_ReachesExactTarget()
    {
        var profile = new TrapezoidalMotionProfile(0, 100, 200, 500, 500);
        var (position, velocity) = profile.Evaluate(profile.TotalTime);
        Assert.Equal(100, position, 6);
        Assert.Equal(0, velocity, 6);
    }

    [Fact]
    public void Triangular_ShortDistance_ReachesExactTarget()
    {
        var profile = new TrapezoidalMotionProfile(0, 10, 200, 500, 500);
        var (position, velocity) = profile.Evaluate(profile.TotalTime);
        Assert.Equal(10, position, 6);
        Assert.Equal(0, velocity, 6);
    }

    [Fact]
    public void NegativeDirection_ReachesExactTarget()
    {
        var profile = new TrapezoidalMotionProfile(100, 0, 200, 500, 500);
        var (position, velocity) = profile.Evaluate(profile.TotalTime);
        Assert.Equal(0, position, 6);
        Assert.Equal(0, velocity, 6);
    }

    [Fact]
    public void ZeroDistance_ZeroTotalTime()
    {
        var profile = new TrapezoidalMotionProfile(50, 50, 200, 500, 500);
        Assert.Equal(0, profile.TotalTime, 6);
    }

    [Fact]
    public void Evaluate_Midpoint_AcceleratesCorrectly()
    {
        var profile = new TrapezoidalMotionProfile(0, 100, 200, 500, 500);
        var (position, velocity) = profile.Evaluate(profile.TotalTime / 2);
        Assert.True(position > 0 && position < 100);
        Assert.True(velocity > 0);
    }
}
