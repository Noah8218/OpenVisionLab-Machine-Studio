using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class LayoutComponentPlacementServiceTests
{
    [Fact]
    public void PlaceSnapsStageAndUpdatesBoundAxisPosition()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var axis = new VirtualAxisDefinition
        {
            Id = "axis-1",
            Name = "X"
        };
        project.Axes.Add(axis);
        var layout = new MachineLayoutDefinition { GridSize = 10, SnapToGrid = true };
        var component = new LayoutComponentDefinition
        {
            Id = "stage-1",
            Name = "Stage 1",
            Kind = LayoutComponentKind.LinearStage,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 84, Height = 48 },
            BehaviorBindingId = axis.Id
        };
        var service = new LayoutComponentPlacementService();

        service.Place(project, layout, component, 45, 185);

        Assert.Equal(50, component.Transform.X);
        Assert.Equal(190, component.Transform.Y);
        Assert.Equal(50, axis.Position.X);
        Assert.Equal(190, axis.Position.Y);
    }

    [Fact]
    public void FindNearestAvailablePositionKeepsIndependentComponentOffAnObstacle()
    {
        var layout = new MachineLayoutDefinition { GridSize = 10, SnapToGrid = true };
        layout.Components.Add(new LayoutComponentDefinition
        {
            Id = "conveyor-1",
            Name = "Conveyor 1",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D { X = 220, Y = 260 },
            Size = new Size2D { Width = 360, Height = 80 }
        });
        var component = new LayoutComponentDefinition
        {
            Id = "cylinder-1",
            Name = "Cylinder 1",
            Kind = LayoutComponentKind.PneumaticCylinder,
            Transform = new Transform2D { X = 220, Y = 260 },
            Size = new Size2D { Width = 96, Height = 36 }
        };
        var service = new LayoutComponentPlacementService();

        var position = service.FindNearestAvailablePosition(layout, component);

        Assert.NotEqual((220d, 260d), position);
        Assert.Equal(0, position.X % 10);
        Assert.Equal(0, position.Y % 10);
    }
}
