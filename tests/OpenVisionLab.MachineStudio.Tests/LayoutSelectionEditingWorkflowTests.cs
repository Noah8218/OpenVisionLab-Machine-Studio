using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class LayoutSelectionEditingWorkflowTests
{
    [Fact]
    public void DragUsesSnappedDeltaAndRejectsConcurrentTransform()
    {
        var items = CreateItems();
        var workflow = new LayoutSelectionEditingWorkflow();

        Assert.True(workflow.BeginSelectionDrag(items, isEditable: true));
        Assert.False(workflow.BeginSelectionTransform(items, LayoutTransformHandle.BottomRight, isEditable: true));
        Assert.True(workflow.UpdateSelectionDrag(13, 17, items[0], snapToGrid: true, gridSize: 10));

        Assert.Equal(50, items[0].CurrentX);
        Assert.Equal(40, items[0].CurrentY);
        Assert.Equal(110, items[1].CurrentX);
        Assert.Equal(70, items[1].CurrentY);
        Assert.True(workflow.CompleteSelectionDrag());
        Assert.False(workflow.CompleteSelectionDrag());
    }

    [Fact]
    public void TransformCancelRestoresRotationAndSizeAfterCommittedResize()
    {
        var items = CreateItems();
        var item = items[0];
        var initial = (item.CurrentX, item.CurrentY, item.CurrentWidth, item.CurrentHeight, item.CurrentRotationDegrees);
        var workflow = new LayoutSelectionEditingWorkflow();

        Assert.True(workflow.BeginSelectionTransform([item], LayoutTransformHandle.BottomRight, isEditable: true));
        Assert.True(workflow.UpdateSelectionTransform(
            item.CurrentX + 60,
            item.CurrentY + 50,
            snapToGrid: true,
            gridSize: 10));
        Assert.True(workflow.CompleteSelectionTransform());
        Assert.True(item.CurrentWidth > initial.CurrentWidth);
        Assert.True(item.CurrentHeight > initial.CurrentHeight);

        var committed = (item.CurrentX, item.CurrentY, item.CurrentWidth, item.CurrentHeight, item.CurrentRotationDegrees);
        Assert.True(workflow.BeginSelectionTransform([item], LayoutTransformHandle.Rotation, isEditable: true));
        Assert.True(workflow.UpdateSelectionTransform(
            item.CurrentX + 80,
            item.CurrentY,
            snapToGrid: true,
            gridSize: 10));
        workflow.CancelSelectionTransform();

        Assert.Equal(committed, (item.CurrentX, item.CurrentY, item.CurrentWidth, item.CurrentHeight, item.CurrentRotationDegrees));
    }

    [Fact]
    public void AlignmentNudgeAndLayerOrderOperateOnExplicitSelection()
    {
        var items = CreateItems();
        var workflow = new LayoutSelectionEditingWorkflow();
        var selected = new[] { items[0], items[1] };

        Assert.True(workflow.AlignSelection(selected, items[0], LayoutSelectionAlignment.HorizontalCenter));
        Assert.Equal(items[0].CurrentX, items[1].CurrentX);
        Assert.True(workflow.NudgeSelection(selected, "Right", snapToGrid: true, gridSize: 10));
        Assert.Equal(items[0].CurrentX, items[1].CurrentX);

        Assert.True(workflow.CanChangeSelectionLayerOrder(items, selected, LayoutLayerOrder.BringToFront));
        Assert.True(workflow.ChangeSelectionLayerOrder(items, selected, LayoutLayerOrder.BringToFront));
        Assert.Equal(2, items[0].ZIndex);
        Assert.Equal(3, items[1].ZIndex);
    }

    private static LayoutItem[] CreateItems() =>
        [
            CreateItem("stage-1", LayoutComponentKind.LinearStage, 40, 20, 84, 48, 10),
            CreateItem("sensor-1", LayoutComponentKind.DigitalSensor, 100, 50, 18, 70, 20),
            CreateItem("cylinder-1", LayoutComponentKind.PneumaticCylinder, 160, 80, 50, 30, 30),
            CreateItem("conveyor-1", LayoutComponentKind.Conveyor, 220, 110, 120, 30, 40)
        ];

    private static LayoutItem CreateItem(
        string id,
        LayoutComponentKind kind,
        double x,
        double y,
        double width,
        double height,
        int zIndex) =>
        new(
            new LayoutComponentDefinition
            {
                Id = id,
                Name = id,
                Kind = kind,
                Transform = new Transform2D { X = x, Y = y },
                Size = new Size2D { Width = width, Height = height },
                ZIndex = zIndex
            },
            gridSize: 10,
            snapToGrid: true);
}
