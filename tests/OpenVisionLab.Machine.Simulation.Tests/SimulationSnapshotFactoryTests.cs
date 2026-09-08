using System.Text.Json;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public class SimulationSnapshotFactoryTests
{
    [Fact]
    public void Create_CapturesRuntimeSourcesAndPreservesDeterministicCameraOrdering()
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
        var signalHub = DeterministicSignalHub.Create(new[]
        {
            new ChannelDefinition
            {
                Id = "di.ready",
                Name = "Ready",
                Kind = ChannelKind.DigitalInput
            }
        }).Hub!;
        var cameras = new[]
        {
            new DeterministicVirtualCamera(new VirtualCameraConfiguration(
                "camera-z",
                "Camera Z",
                exposureTicks: 1,
                transferTicks: 1,
                PlaceholderInspectionDecision.Pass)),
            new DeterministicVirtualCamera(new VirtualCameraConfiguration(
                "camera-a",
                "Camera A",
                exposureTicks: 1,
                transferTicks: 1,
                PlaceholderInspectionDecision.Fail))
        };

        SimulationSnapshot snapshot = SimulationSnapshotFactory.Create(
            new SimulationSnapshotFactoryContext(
                TimeSpan.FromMilliseconds(25),
                5,
                SimulationRunMode.Paused,
                SimulationControlOwner.Definition,
                1,
                new[] { axis },
                signalHub,
                new Dictionary<string, DeterministicSequenceExecutor>(StringComparer.Ordinal),
                cameras,
                AutomaticRunSnapshot.NotConfigured,
                null,
                Array.Empty<SimulationFaultSnapshot>(),
                DeterministicConditionScenarioSnapshot.NotConfigured,
                null,
                SequenceDebugSnapshot.Empty,
                ProjectId: "project-a",
                RuntimeGeneration: 12));

        Assert.Equal(TimeSpan.FromMilliseconds(25), snapshot.SimulationTime);
        Assert.Equal(5, snapshot.TickIndex);
        Assert.Single(snapshot.Axes);
        Assert.Equal("di.ready", Assert.Single(snapshot.Signals).Id);
        Assert.Equal(new[] { "camera-a", "camera-z" }, snapshot.Cameras.Select(camera => camera.Id));
        Assert.Empty(snapshot.LayoutComponents);
        Assert.Empty(snapshot.Workpieces);
        Assert.Equal(SequenceDebugSnapshot.Empty, snapshot.SequenceDebug);
        Assert.Equal("project-a", snapshot.ProjectId);
        Assert.Equal(12, snapshot.RuntimeGeneration);

        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("project-a", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeGeneration", json, StringComparison.Ordinal);
    }
}
