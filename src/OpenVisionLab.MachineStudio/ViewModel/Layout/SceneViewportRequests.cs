using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum SceneViewportMoveAction
{
    Begin,
    Update,
    Commit,
    Cancel
}

internal sealed record SceneSelectionRequest(LayoutItem Item, bool Toggle);

internal sealed record SceneMoveRequest(SceneViewportMoveAction Action, (double X, double Y) Delta);

internal sealed record SceneMarqueeSelectionRequest(
    IReadOnlyList<LayoutItem> Items,
    LayoutSelectionMode Mode);

internal sealed record SceneTransformRequest(
    SceneViewportMoveAction Action,
    LayoutTransformHandle Handle,
    (double X, double Y) WorldPoint,
    bool PreserveAspectRatio);

internal sealed record SceneLibraryComponentDropRequest(
    LayoutComponentKind Kind,
    (double X, double Y) WorldPoint);
