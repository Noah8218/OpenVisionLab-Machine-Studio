using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class LayoutComponentAuthoringServiceTests
{
    [Fact]
    public void TryAddLinearStageCreatesBoundAxisAndSnapsDropPosition()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var service = new LayoutComponentAuthoringService();

        var result = service.TryAdd(
            project,
            LayoutComponentKind.LinearStage,
            worldX: 45,
            worldY: 185);

        Assert.True(result.IsSuccess);
        var layout = Assert.Single(project.Layouts);
        Assert.NotNull(result.Component);
        var component = result.Component!;
        var axis = Assert.Single(project.Axes);
        Assert.Same(layout, result.Layout);
        Assert.Equal("main-cell", project.Simulation.ActiveLayoutId);
        Assert.Equal(LayoutComponentKind.LinearStage, component.Kind);
        Assert.Equal(50, component.Transform.X);
        Assert.Equal(190, component.Transform.Y);
        Assert.Equal(axis.Id, component.BehaviorBindingId);
        Assert.Equal(component.Transform.X, axis.Position.X);
        Assert.Equal(component.Transform.Y, axis.Position.Y);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void TryAddSensorWithoutTargetRollsBackImplicitLayout()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var service = new LayoutComponentAuthoringService();

        var result = service.TryAdd(project, LayoutComponentKind.DigitalSensor);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            LayoutComponentAuthoringFailureKind.SensorTargetRequired,
            result.Failure?.Kind);
        Assert.Empty(project.Layouts);
        Assert.Null(project.Simulation.ActiveLayoutId);
        Assert.Empty(project.Axes);
        Assert.Empty(project.Devices);
        Assert.Empty(project.Channels);
    }

    [Fact]
    public void TryRemoveConveyorWithWorkpieceReturnsDependencyWithoutMutation()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var layout = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell"
        };
        var conveyor = new LayoutComponentDefinition
        {
            Id = "conveyor-1",
            Name = "Conveyor 1",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 360, Height = 80 },
            BehaviorBindingId = "device.conveyor-1"
        };
        var workpiece = new LayoutComponentDefinition
        {
            Id = "workpiece-1",
            Name = "Workpiece 1",
            Kind = LayoutComponentKind.Workpiece,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 42, Height = 42 },
            BehaviorBindingId = "device.workpiece-1"
        };
        layout.Components.AddRange([conveyor, workpiece]);
        project.Layouts.Add(layout);
        project.Simulation.ActiveLayoutId = layout.Id;
        project.Devices.Add(new DeviceDefinition
        {
            Id = "device.workpiece-1",
            Name = "Workpiece 1",
            Kind = DeviceKind.Workpiece,
            Workpiece = new WorkpieceDefinition
            {
                ConveyorComponentId = conveyor.Id
            }
        });
        var service = new LayoutComponentAuthoringService();

        var result = service.TryRemove(project, layout, conveyor.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            LayoutComponentRemovalFailureKind.WorkpieceDependency,
            result.Failure);
        Assert.Same(workpiece, result.BlockingComponent);
        Assert.Contains(conveyor, layout.Components);
        Assert.Equal(2, layout.Components.Count);
    }
}
