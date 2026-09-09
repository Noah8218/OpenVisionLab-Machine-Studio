using System.Windows;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SceneViewportInteractionWorkflowTests
{
    [Fact]
    public void RoutesSelectionAndMarqueeRequestsToTheLayoutOwner()
    {
        using var layout = CreateLayout();
        var first = layout.Items[0];
        var second = layout.Items[1];
        var workflow = new SceneViewportInteractionWorkflow(layout, (_, _, _) => true);

        workflow.HandleSelection(new SceneSelectionRequest(first, Toggle: false));
        workflow.HandleSelection(new SceneSelectionRequest(second, Toggle: true));

        Assert.Equal(2, layout.SelectionCount);
        Assert.Same(second, layout.SelectedItem);

        workflow.HandleMarqueeSelection(new SceneMarqueeSelectionRequest(
            [first],
            LayoutSelectionMode.Replace));

        Assert.Equal(1, layout.SelectionCount);
        Assert.Same(first, layout.SelectedItem);
    }

    [Fact]
    public void RoutesMoveAndTransformLifecyclesWithoutOwningGestureState()
    {
        using var layout = CreateLayout();
        var item = layout.Items[0];
        layout.Select(item.Id);
        var initial = (item.CurrentX, item.CurrentY, item.CurrentWidth, item.CurrentHeight);
        var workflow = new SceneViewportInteractionWorkflow(layout, (_, _, _) => true);

        workflow.HandleMove(new SceneMoveRequest(SceneViewportMoveAction.Begin, default));
        workflow.HandleMove(new SceneMoveRequest(SceneViewportMoveAction.Update, new Vector(13, 17)));
        workflow.HandleMove(new SceneMoveRequest(SceneViewportMoveAction.Commit, default));

        Assert.Equal(initial.CurrentX + 10, item.CurrentX);
        Assert.Equal(initial.CurrentY + 20, item.CurrentY);

        workflow.HandleTransform(new SceneTransformRequest(
            SceneViewportMoveAction.Begin,
            LayoutTransformHandle.BottomRight,
            default,
            PreserveAspectRatio: false));
        workflow.HandleTransform(new SceneTransformRequest(
            SceneViewportMoveAction.Update,
            LayoutTransformHandle.BottomRight,
            new Point(item.CurrentX + 40, item.CurrentY + 30),
            PreserveAspectRatio: false));
        workflow.HandleTransform(new SceneTransformRequest(
            SceneViewportMoveAction.Cancel,
            LayoutTransformHandle.BottomRight,
            default,
            PreserveAspectRatio: false));

        Assert.Equal(initial.CurrentX + 10, item.CurrentX);
        Assert.Equal(initial.CurrentY + 20, item.CurrentY);
        Assert.Equal(initial.CurrentWidth, item.CurrentWidth);
        Assert.Equal(initial.CurrentHeight, item.CurrentHeight);
    }

    [Fact]
    public void RoutesLibraryDropToTheExistingAuthoringWorkflow()
    {
        using var layout = CreateLayout();
        LayoutComponentKind? addedKind = null;
        double? addedX = null;
        double? addedY = null;
        var workflow = new SceneViewportInteractionWorkflow(
            layout,
            (kind, x, y) =>
            {
                addedKind = kind;
                addedX = x;
                addedY = y;
                return true;
            });

        workflow.HandleLibraryComponentDrop(new SceneLibraryComponentDropRequest(
            LayoutComponentKind.Conveyor,
            new Point(45, 185)));

        Assert.Equal(LayoutComponentKind.Conveyor, addedKind);
        Assert.Equal(45, addedX);
        Assert.Equal(185, addedY);
    }

    [Fact]
    public void IgnoresUnexpectedRequestPayloads()
    {
        using var layout = CreateLayout();
        var callbackCount = 0;
        var workflow = new SceneViewportInteractionWorkflow(
            layout,
            (_, _, _) =>
            {
                callbackCount++;
                return true;
            });

        workflow.HandleSelection(new object());
        workflow.HandleMove(new object());
        workflow.HandleMarqueeSelection(new object());
        workflow.HandleTransform(new object());
        workflow.HandleLibraryComponentDrop(new object());

        Assert.Equal(0, layout.SelectionCount);
        Assert.Equal(0, callbackCount);
    }

    private static MachineLayoutViewModel CreateLayout()
    {
        var project = new MachineProjectDocument { Name = "Scene interaction" };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            GridSize = 10,
            SnapToGrid = true
        };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "stage-1",
            Name = "Stage 1",
            Kind = LayoutComponentKind.LinearStage,
            Transform = new Transform2D { X = 40, Y = 20 },
            Size = new Size2D { Width = 80, Height = 40 },
            ZIndex = 0
        });
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "sensor-1",
            Name = "Sensor 1",
            Kind = LayoutComponentKind.DigitalSensor,
            Transform = new Transform2D { X = 100, Y = 50 },
            Size = new Size2D { Width = 20, Height = 20 },
            ZIndex = 1
        });
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }
}
