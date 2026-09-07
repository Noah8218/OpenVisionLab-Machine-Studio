using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public class SimulationPhysicalRuntimeTickTests
{
    [Fact]
    public void Advance_ProgressesAxisAndPublishesCameraEventsAtProvidedTickIdentity()
    {
        var axis = new ServoAxisComponent(new AxisConfiguration
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
        Assert.True(axis.MoveAbsolute(100).IsAccepted);

        var camera = new DeterministicVirtualCamera(
            new VirtualCameraConfiguration(
                "camera-1",
                "Camera 1",
                exposureTicks: 1,
                transferTicks: 1,
                PlaceholderInspectionDecision.Pass));
        Assert.True(camera.Trigger("recipe-1").IsAccepted);

        var events = new List<(string Code, long Tick, TimeSpan Time)>();
        var physicalTick = new SimulationPhysicalRuntimeTick(
            (category, code, message, tick, time) => events.Add((code, tick, time)));

        physicalTick.Advance(new SimulationPhysicalRuntimeTickContext(
            TimeSpan.FromMilliseconds(5),
            7,
            TimeSpan.FromMilliseconds(35),
            new[] { axis },
            null,
            null,
            new[] { camera }));

        Assert.Equal(AxisState.Moving, axis.State);
        Assert.True(axis.Position > 0);
        var firstEvent = Assert.Single(events);
        Assert.Equal("CameraExposureCompleted", firstEvent.Code);
        Assert.Equal(7, firstEvent.Tick);
        Assert.Equal(TimeSpan.FromMilliseconds(35), firstEvent.Time);

        physicalTick.Advance(new SimulationPhysicalRuntimeTickContext(
            TimeSpan.FromMilliseconds(5),
            8,
            TimeSpan.FromMilliseconds(40),
            new[] { axis },
            null,
            null,
            new[] { camera }));

        Assert.Equal(
            new[] { "CameraExposureCompleted", "CameraFrameReady", "VisionResultReady" },
            events.Select(item => item.Code));
        Assert.All(events.Skip(1), item =>
        {
            Assert.Equal(8, item.Tick);
            Assert.Equal(TimeSpan.FromMilliseconds(40), item.Time);
        });
    }
}
