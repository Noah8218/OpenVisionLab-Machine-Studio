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

    [Fact]
    public void MoveSelectionByAppliesExactSharedOffsetAndRejectsInvalidOrReadOnlyRequests()
    {
        var items = CreateItems();
        var workflow = new LayoutSelectionEditingWorkflow();

        Assert.True(workflow.MoveSelectionBy(items.Take(2), 12.5, -7.25, isEditable: true));
        Assert.Equal(52.5, items[0].CurrentX);
        Assert.Equal(12.75, items[0].CurrentY);
        Assert.Equal(112.5, items[1].CurrentX);
        Assert.Equal(42.75, items[1].CurrentY);

        Assert.False(workflow.MoveSelectionBy(items, 0, 0, isEditable: true));
        Assert.False(workflow.MoveSelectionBy(items, double.NaN, 1, isEditable: true));
        Assert.False(workflow.MoveSelectionBy(items, 1, 1, isEditable: false));
    }

    [Fact]
    public void DragRejectsInvalidCoordinatesAndGridWithoutMutationThenRecoversOrCancels()
    {
        var items = CreateItems();
        var workflow = new LayoutSelectionEditingWorkflow();
        var initial = items.Select(item => (item.CurrentX, item.CurrentY)).ToArray();
        var notifications = 0;
        foreach (var item in items) item.DefinitionChanged += (_, _) => notifications++;

        Assert.True(workflow.BeginSelectionDrag(items, isEditable: true));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.False(workflow.UpdateSelectionDrag(invalid, 1, items[0], snapToGrid: false, gridSize: 10));
            Assert.False(workflow.UpdateSelectionDrag(1, invalid, items[0], snapToGrid: false, gridSize: 10));
            Assert.False(workflow.UpdateSelectionDrag(1, 1, items[0], snapToGrid: true, gridSize: invalid));
        }
        Assert.False(workflow.UpdateSelectionDrag(1, 1, items[0], snapToGrid: true, gridSize: 0));
        Assert.False(workflow.UpdateSelectionDrag(1, 1, items[0], snapToGrid: true, gridSize: -10));
        Assert.Equal(initial, items.Select(item => (item.CurrentX, item.CurrentY)).ToArray());
        Assert.Equal(0, notifications);

        Assert.True(workflow.UpdateSelectionDrag(12.5, -7.25, items[0], snapToGrid: false, gridSize: 0));
        Assert.Equal(52.5, items[0].CurrentX);
        Assert.Equal(12.75, items[0].CurrentY);
        workflow.CancelSelectionDrag();
        Assert.Equal(initial, items.Select(item => (item.CurrentX, item.CurrentY)).ToArray());
        Assert.False(workflow.CompleteSelectionDrag());
        Assert.True(workflow.BeginSelectionDrag(items, isEditable: true));
        Assert.True(workflow.UpdateSelectionDrag(13, 17, items[0], snapToGrid: true, gridSize: 10));
        Assert.True(workflow.CompleteSelectionDrag());
        Assert.Equal(50, items[0].Component!.Transform.X);
        Assert.Equal(40, items[0].Component!.Transform.Y);
    }

    [Fact]
    public void DragAndMoveRejectGroupOverflowBeforeWritingAnyComponentThenRecover()
    {
        var items = CreateItems();
        items[1].SetCurrentX(double.MaxValue, snapToGrid: false);
        var workflow = new LayoutSelectionEditingWorkflow();
        var initial = items.Select(item => (item.CurrentX, item.CurrentY)).ToArray();
        var notifications = 0;
        foreach (var item in items) item.DefinitionChanged += (_, _) => notifications++;

        Assert.True(workflow.BeginSelectionDrag(items, isEditable: true));
        Assert.False(workflow.UpdateSelectionDrag(double.MaxValue, 2, items[0], snapToGrid: false, gridSize: 10));
        Assert.Equal(initial, items.Select(item => (item.CurrentX, item.CurrentY)).ToArray());
        Assert.Equal(0, notifications);
        workflow.CancelSelectionDrag();
        Assert.False(workflow.MoveSelectionBy(items, double.MaxValue, 2, isEditable: true));
        Assert.False(workflow.NudgeSelection(items, "Right", snapToGrid: true, gridSize: double.PositiveInfinity));
        Assert.False(workflow.NudgeSelection(items, "Right", snapToGrid: true, gridSize: -10));
        Assert.Equal(initial, items.Select(item => (item.CurrentX, item.CurrentY)).ToArray());
        Assert.Equal(0, notifications);

        Assert.True(workflow.MoveSelectionBy(items, 12.5, -7.25, isEditable: true));
        Assert.Equal(52.5, items[0].Component!.Transform.X);
        Assert.Equal(12.75, items[0].Component!.Transform.Y);
        Assert.All(items, item => Assert.True(double.IsFinite(item.CurrentX) && double.IsFinite(item.CurrentY)));
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
