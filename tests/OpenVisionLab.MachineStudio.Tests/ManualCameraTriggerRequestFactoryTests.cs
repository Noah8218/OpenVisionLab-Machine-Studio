using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualCameraTriggerRequestFactoryTests
{
    [Fact]
    public void TryCreateBuildsInspectionContextFromOneImmutableSnapshot()
    {
        var snapshot = CreateSnapshot();
        var factory = new ManualCameraTriggerRequestFactory();
        var request = factory.TryCreate(new ManualCameraTriggerRequestInput(
            IsAllowed: true,
            ProjectId: "project-1",
            ProjectName: "Project",
            ProjectPath: "C:\\Project\\machine.ovmachine",
            ProjectJson: "{\"id\":\"project-1\"}",
            BuildIdentity: "0.1.0-test+factory",
            SimulationFixedStep: TimeSpan.FromMilliseconds(5),
            Snapshot: snapshot,
            CurrentCamera: snapshot.Cameras[0],
            SelectedRecipe: "recipe-a",
            CameraDefinition: new VirtualCameraDefinition
            {
                PlaceholderDecision = PlaceholderInspectionDecision.Fail
            },
            SourceDefinition: new VirtualSingleImageSourceDefinition
            {
                SourceRelativePath = "images/part.png",
                Width = 640,
                Height = 480,
                PixelFormat = "Mono8"
            },
            SimulationSeed: 42));

        Assert.NotNull(request);
        var result = request!;
        Assert.Equal("project-1", result.ProjectId);
        Assert.Equal("camera-1", result.InspectionRequest.CameraId);
        Assert.Equal("recipe-a", result.InspectionRequest.RecipeId);
        Assert.Equal(PlaceholderInspectionDecision.Fail, result.InspectionRequest.PlaceholderDecision);
        Assert.Equal("images/part.png", result.InspectionRequest.SourceRelativePath);
        Assert.Equal(640, result.InspectionRequest.Width);
        Assert.Equal(480, result.InspectionRequest.Height);
        Assert.Equal(42, result.InspectionRequest.Seed);
    }

    [Fact]
    public void TryCreateRejectsIncompleteContextBeforeConstructingRequest()
    {
        var snapshot = CreateSnapshot();
        var factory = new ManualCameraTriggerRequestFactory();
        var request = factory.TryCreate(new ManualCameraTriggerRequestInput(
            IsAllowed: false,
            ProjectId: "project-1",
            ProjectName: "Project",
            ProjectPath: null,
            ProjectJson: "{}",
            BuildIdentity: "test",
            SimulationFixedStep: TimeSpan.FromMilliseconds(5),
            Snapshot: snapshot,
            CurrentCamera: null,
            SelectedRecipe: null,
            CameraDefinition: null,
            SourceDefinition: null,
            SimulationSeed: 1));

        Assert.Null(request);
    }

    private static SimulationSnapshot CreateSnapshot() => new(
        TimeSpan.FromMilliseconds(25),
        5,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [],
        [],
        [new VirtualCameraSnapshot(
            "camera-1",
            "Camera",
            VirtualCameraState.Idle,
            3,
            null,
            null,
            0,
            0,
            null)]);
}
