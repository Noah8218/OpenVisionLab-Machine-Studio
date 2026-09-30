using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns WPF-neutral routing from scene viewport requests to layout state.
/// The layout owns selection and gesture state; component creation remains an
/// explicit callback to the existing authoring workflow.
/// </summary>
internal sealed class SceneViewportInteractionWorkflow
{
    private readonly MachineLayoutViewModel _layout;
    private readonly Func<LayoutComponentKind, double?, double?, bool> _addLayoutComponent;

    internal SceneViewportInteractionWorkflow(
        MachineLayoutViewModel layout,
        Func<LayoutComponentKind, double?, double?, bool> addLayoutComponent)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _addLayoutComponent = addLayoutComponent ?? throw new ArgumentNullException(nameof(addLayoutComponent));
    }

    internal void HandleSelection(object? parameter)
    {
        if (parameter is SceneSelectionRequest request)
        {
            _layout.ExtendSelection(request.Item, request.Toggle);
        }
    }

    internal void HandleMove(object? parameter)
    {
        if (parameter is not SceneMoveRequest request)
        {
            return;
        }

        switch (request.Action)
        {
            case SceneViewportMoveAction.Begin:
                _layout.BeginSelectionDrag();
                break;
            case SceneViewportMoveAction.Update:
                _layout.UpdateSelectionDrag(request.Delta.X, request.Delta.Y);
                break;
            case SceneViewportMoveAction.Commit:
                _layout.CompleteSelectionDrag();
                break;
            case SceneViewportMoveAction.Cancel:
                _layout.CancelSelectionDrag();
                break;
            case SceneViewportMoveAction.MoveBy:
                _layout.MoveSelectionBy(request.Delta.X, request.Delta.Y);
                break;
        }
    }

    internal void HandleMarqueeSelection(object? parameter)
    {
        if (parameter is SceneMarqueeSelectionRequest request)
        {
            _layout.SelectRegion(request.Items, request.Mode);
        }
    }

    internal void HandleTransform(object? parameter)
    {
        if (parameter is not SceneTransformRequest request)
        {
            return;
        }

        switch (request.Action)
        {
            case SceneViewportMoveAction.Begin:
                _layout.BeginSelectionTransform(request.Handle);
                break;
            case SceneViewportMoveAction.Update:
                _layout.UpdateSelectionTransform(
                    request.WorldPoint.X,
                    request.WorldPoint.Y,
                    request.PreserveAspectRatio);
                break;
            case SceneViewportMoveAction.Commit:
                _layout.CompleteSelectionTransform();
                break;
            case SceneViewportMoveAction.Cancel:
                _layout.CancelSelectionTransform();
                break;
        }
    }

    internal void HandleLibraryComponentDrop(object? parameter)
    {
        if (parameter is SceneLibraryComponentDropRequest request)
        {
            _addLayoutComponent(request.Kind, request.WorldPoint.X, request.WorldPoint.Y);
        }
    }
}
