using OpenVisionLab.Machine.Core.Layouts;
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
}
