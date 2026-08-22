using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.View.Scene;

public partial class SceneDocumentView : UserControl
{
    public SceneDocumentView()
    {
        InitializeComponent();
    }

    private void OnFitLayoutClick(object sender, RoutedEventArgs e) => SceneViewport.FitToLayout();

    private void OnSceneSelectionRequested(
        object sender,
        MachineSceneSelectionRequestedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Layout.ExtendSelection(
                args.Item,
                toggle: args.Modifiers.HasFlag(ModifierKeys.Control));
        }
    }

    private void OnSceneMoveRequested(
        object sender,
        MachineSceneMoveRequestedEventArgs args)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        switch (args.Action)
        {
            case MachineSceneMoveAction.Begin:
                viewModel.Layout.BeginSelectionDrag();
                break;
            case MachineSceneMoveAction.Update:
                viewModel.Layout.UpdateSelectionDrag(args.Delta.X, args.Delta.Y);
                break;
            case MachineSceneMoveAction.Commit:
                viewModel.Layout.CompleteSelectionDrag();
                break;
            case MachineSceneMoveAction.Cancel:
                viewModel.Layout.CancelSelectionDrag();
                break;
        }
    }

    private void OnSceneMarqueeSelectionRequested(
        object sender,
        MachineSceneMarqueeSelectionRequestedEventArgs args)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var mode = args.Modifiers.HasFlag(ModifierKeys.Control)
            ? LayoutSelectionMode.Toggle
            : args.Modifiers.HasFlag(ModifierKeys.Shift)
                ? LayoutSelectionMode.Add
                : LayoutSelectionMode.Replace;
        viewModel.Layout.SelectRegion(args.Items, mode);
    }

    private void OnSceneTransformRequested(
        object sender,
        MachineSceneTransformRequestedEventArgs args)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        switch (args.Action)
        {
            case MachineSceneMoveAction.Begin:
                viewModel.Layout.BeginSelectionTransform(args.Handle);
                break;
            case MachineSceneMoveAction.Update:
                viewModel.Layout.UpdateSelectionTransform(
                    args.WorldPoint.X,
                    args.WorldPoint.Y,
                    args.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case MachineSceneMoveAction.Commit:
                viewModel.Layout.CompleteSelectionTransform();
                break;
            case MachineSceneMoveAction.Cancel:
                viewModel.Layout.CancelSelectionTransform();
                break;
        }
    }

    private void OnLibraryComponentDropRequested(
        object sender,
        MachineSceneLibraryComponentDropRequestedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.TryAddLayoutComponent(args.Kind, args.WorldPoint.X, args.WorldPoint.Y);
        }
    }
}
