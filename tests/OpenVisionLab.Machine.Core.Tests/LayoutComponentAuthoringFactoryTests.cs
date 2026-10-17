using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class LayoutComponentAuthoringFactoryTests
{
    [Fact]
    public void DigitalIoHasAnIndependentDeclaredChannelAndValidEnvelope()
    {
        var project = new MachineProjectDocument { Name = "I/O authoring" };
        var layout = new MachineLayoutDefinition { Id = "main", Name = "Main" };
        project.Layouts.Add(layout);
        var factory = new LayoutComponentAuthoringFactory();
        for (int index = 0; index < 3; index++)
        {
            var component = factory.TryCreate(project, layout, LayoutComponentKind.DigitalIo, null, out var failure);
            Assert.Null(failure);
            layout.Components.Add(Assert.IsType<LayoutComponentDefinition>(component));
        }
        Assert.Equal(3, project.Channels.Select(channel => channel.Id).Distinct().Count());
        Assert.All(layout.Components, component => Assert.Contains(project.Channels, channel => channel.Id == component.BehaviorBindingId));
        Assert.True(new MachineProjectLayoutBehaviorBindingValidator().Validate(project).IsValid);
        project.Channels.Clear();
        Assert.False(new MachineProjectLayoutBehaviorBindingValidator().Validate(project).IsValid);
    }

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
        Assert.Equal(52, component.Size.Width);
        Assert.Equal(67, component.Size.Height);
    }

    [Fact]
    public void TryCreateCameraWithoutUnplacedCameraCreatesAnIndependentVirtualDevice()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        project.Devices.Add(new DeviceDefinition
        {
            Id = "device.camera-1",
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
                    BehaviorBindingId = "device.camera-1"
                }
            ]
        };
        var factory = new LayoutComponentAuthoringFactory();

        var component = factory.TryCreate(project, layout, LayoutComponentKind.Camera, null, out var failure);

        Assert.NotNull(component);
        Assert.Null(failure);
        Assert.Equal("camera-2", component!.Id);
        Assert.Equal("device.camera-2", component.BehaviorBindingId);
        Assert.Equal(2, project.Devices.Count);
        Assert.NotNull(project.Devices[1].Camera);
        Assert.Single(layout.Components);
        Assert.Empty(project.Sequences);
    }

    [Fact]
    public void CameraCreationAvoidsExistingDeviceIdsAndBindingsInOtherLayouts()
    {
        var project = new MachineProjectDocument
        {
            Devices = [new() { Id = "camera-top", Kind = DeviceKind.Camera }, new() { Id = "device.camera-1", Kind = DeviceKind.Cylinder }],
            Layouts = [new() { Id = "first", Components = [new() { Id = "camera-1", Kind = LayoutComponentKind.Camera, BehaviorBindingId = "camera-top" }] }]
        };
        var layout = new MachineLayoutDefinition { Id = "second" };
        project.Layouts.Add(layout);
        var component = new LayoutComponentAuthoringFactory().TryCreate(project, layout, LayoutComponentKind.Camera, null, out var failure);
        Assert.Null(failure);
        Assert.Equal("device.camera-2", component!.BehaviorBindingId);
        Assert.Equal(3, project.Devices.Count);
        Assert.Equal(3, project.Devices.Select(device => device.Id).Distinct().Count());
    }
}
