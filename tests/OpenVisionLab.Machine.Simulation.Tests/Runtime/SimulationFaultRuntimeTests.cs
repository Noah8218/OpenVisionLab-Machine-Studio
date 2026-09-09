using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationFaultRuntimeTests
{
    [Fact]
    public void AddRemoveAndClearOwnActiveFaultRecords()
    {
        var runtime = new SimulationFaultRuntime();
        var fault = new SimulationFaultSnapshot(
            SimulationFaultKind.AxisMotionBlocked,
            "axis.x",
            null,
            7,
            TimeSpan.FromMilliseconds(35));
        var key = new SimulationFaultKey(fault.Kind, fault.TargetId);

        runtime.Add(fault);

        Assert.Single(runtime);
        Assert.True(runtime.ContainsKey(key));
        Assert.Equal(fault, runtime[key]);
        Assert.True(runtime.Remove(key));
        Assert.Empty(runtime);

        runtime.Add(fault);
        runtime.Clear();

        Assert.Empty(runtime);
    }
}
