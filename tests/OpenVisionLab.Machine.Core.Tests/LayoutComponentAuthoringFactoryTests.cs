using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class LayoutComponentAuthoringFactoryTests
{
    [Fact]
    public void TryCreateLinearStageCreatesItsUnboundAxisArtifact()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var layout = new MachineLayoutDefinition();
        var factory = new LayoutComponentAuthoringFactory();

        var component = factory.TryCreate(
            project,
            layout,
            LayoutComponentKind.LinearStage,
            selectedComponentId: null,
            out var failure);

        Assert.NotNull(component);
        Assert.Null(failure);
        Assert.Equal(LayoutComponentKind.LinearStage, component!.Kind);
        Assert.Equal("axis-1", component.BehaviorBindingId);
        Assert.Single(project.Axes);
        Assert.Equal("axis-1", project.Axes[0].Id);
    }

    [Fact]
    public void TryCreateSensorWithoutTargetReturnsTypedFailureWithoutArtifacts()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var layout = new MachineLayoutDefinition();
        var factory = new LayoutComponentAuthoringFactory();

        var component = factory.TryCreate(
            project,
            layout,
            LayoutComponentKind.DigitalSensor,
            selectedComponentId: null,
            out var failure);

        Assert.Null(component);
        Assert.Equal(LayoutComponentAuthoringFailureKind.SensorTargetRequired, failure?.Kind);
        Assert.Empty(project.Devices);
        Assert.Empty(project.Channels);
    }

    [Fact]
    public void TryCreateCameraUsesOneExistingUnplacedCameraAndItsMountPosition()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        project.Devices.Add(new DeviceDefinition
        {
            Id = "camera-top",
            Name = "Top camera",
            Kind = DeviceKind.Camera,
            MountPosition = new(470, 230, 120)
        });
        project.Devices.Add(new DeviceDefinition
        {
            Id = "camera-side",
            Name = "Side camera",
            Kind = DeviceKind.Camera
        });
        var layout = new MachineLayoutDefinition();
        var factory = new LayoutComponentAuthoringFactory();

        var component = factory.TryCreate(project, layout, LayoutComponentKind.Camera, null, out var failure);

        Assert.NotNull(component);
        Assert.Null(failure);
        Assert.Equal(LayoutComponentKind.Camera, component!.Kind);
        Assert.Equal("camera-top", component.BehaviorBindingId);
        Assert.Equal("Top camera", component.Name);
        Assert.Equal(470, component.Transform.X);
        Assert.Equal(230, component.Transform.Y);
        Assert.Equal(28, component.Size.Width);
        Assert.Equal(20, component.Size.Height);
    }

    [Fact]
    public void TryCreateCameraWithoutUnplacedCameraReturnsTypedFailureWithoutArtifacts()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        project.Devices.Add(new DeviceDefinition
        {
            Id = "camera-top",
            Name = "Top camera",
            Kind = DeviceKind.Camera
        });
        var layout = new MachineLayoutDefinition
        {
            Components =
            [
                new LayoutComponentDefinition
                {
                    Id = "camera-1",
                    Name = "Top camera",
                    Kind = LayoutComponentKind.Camera,
                    BehaviorBindingId = "camera-top"
                }
            ]
        };
        var factory = new LayoutComponentAuthoringFactory();

        var component = factory.TryCreate(project, layout, LayoutComponentKind.Camera, null, out var failure);

        Assert.Null(component);
        Assert.Equal(LayoutComponentAuthoringFailureKind.CameraDeviceRequired, failure?.Kind);
        Assert.Single(project.Devices);
        Assert.Single(layout.Components);
    }
}
