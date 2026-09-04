using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class MachineSceneViewportCommandBehavior : Behavior<MachineSceneViewport>
{
    public static readonly DependencyProperty SelectionRequestedCommandProperty = RegisterCommand(
        nameof(SelectionRequestedCommand));

    public static readonly DependencyProperty MoveRequestedCommandProperty = RegisterCommand(
        nameof(MoveRequestedCommand));

    public static readonly DependencyProperty MarqueeSelectionRequestedCommandProperty = RegisterCommand(
        nameof(MarqueeSelectionRequestedCommand));

    public static readonly DependencyProperty TransformRequestedCommandProperty = RegisterCommand(
        nameof(TransformRequestedCommand));

    public static readonly DependencyProperty LibraryComponentDropRequestedCommandProperty = RegisterCommand(
        nameof(LibraryComponentDropRequestedCommand));

    public ICommand? SelectionRequestedCommand
    {
        get => (ICommand?)GetValue(SelectionRequestedCommandProperty);
        set => SetValue(SelectionRequestedCommandProperty, value);
    }

    public ICommand? MoveRequestedCommand
    {
        get => (ICommand?)GetValue(MoveRequestedCommandProperty);
        set => SetValue(MoveRequestedCommandProperty, value);
    }

    public ICommand? MarqueeSelectionRequestedCommand
    {
        get => (ICommand?)GetValue(MarqueeSelectionRequestedCommandProperty);
        set => SetValue(MarqueeSelectionRequestedCommandProperty, value);
    }

    public ICommand? TransformRequestedCommand
    {
        get => (ICommand?)GetValue(TransformRequestedCommandProperty);
        set => SetValue(TransformRequestedCommandProperty, value);
    }

    public ICommand? LibraryComponentDropRequestedCommand
    {
        get => (ICommand?)GetValue(LibraryComponentDropRequestedCommandProperty);
        set => SetValue(LibraryComponentDropRequestedCommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SelectionRequested += OnSelectionRequested;
        AssociatedObject.MoveRequested += OnMoveRequested;
        AssociatedObject.MarqueeSelectionRequested += OnMarqueeSelectionRequested;
        AssociatedObject.TransformRequested += OnTransformRequested;
        AssociatedObject.LibraryComponentDropRequested += OnLibraryComponentDropRequested;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.SelectionRequested -= OnSelectionRequested;
        AssociatedObject.MoveRequested -= OnMoveRequested;
        AssociatedObject.MarqueeSelectionRequested -= OnMarqueeSelectionRequested;
        AssociatedObject.TransformRequested -= OnTransformRequested;
        AssociatedObject.LibraryComponentDropRequested -= OnLibraryComponentDropRequested;
        base.OnDetaching();
    }

    private void OnSelectionRequested(
        object? sender,
        MachineSceneSelectionRequestedEventArgs args) =>
        Execute(
            SelectionRequestedCommand,
            new SceneSelectionRequest(
                args.Item,
                args.Modifiers.HasFlag(ModifierKeys.Control)));

    private void OnMoveRequested(
        object? sender,
        MachineSceneMoveRequestedEventArgs args)
    {
        if (MapAction(args.Action) is not { } action)
        {
            return;
        }

        Execute(
            MoveRequestedCommand,
            new SceneMoveRequest(action, args.Delta));
    }

    private void OnMarqueeSelectionRequested(
        object? sender,
        MachineSceneMarqueeSelectionRequestedEventArgs args)
    {
        var mode = args.Modifiers.HasFlag(ModifierKeys.Control)
            ? LayoutSelectionMode.Toggle
            : args.Modifiers.HasFlag(ModifierKeys.Shift)
                ? LayoutSelectionMode.Add
                : LayoutSelectionMode.Replace;
        Execute(
            MarqueeSelectionRequestedCommand,
            new SceneMarqueeSelectionRequest(args.Items, mode));
    }

    private void OnTransformRequested(
        object? sender,
        MachineSceneTransformRequestedEventArgs args)
    {
        if (MapAction(args.Action) is not { } action)
        {
            return;
        }

        Execute(
            TransformRequestedCommand,
            new SceneTransformRequest(
                action,
                args.Handle,
                args.WorldPoint,
                args.Modifiers.HasFlag(ModifierKeys.Shift)));
    }

    private void OnLibraryComponentDropRequested(
        object? sender,
        MachineSceneLibraryComponentDropRequestedEventArgs args) =>
        Execute(
            LibraryComponentDropRequestedCommand,
            new SceneLibraryComponentDropRequest(args.Kind, args.WorldPoint));

    private static DependencyProperty RegisterCommand(string name) =>
        DependencyProperty.Register(
            name,
            typeof(ICommand),
            typeof(MachineSceneViewportCommandBehavior),
            new PropertyMetadata(null));

    private static SceneViewportMoveAction? MapAction(MachineSceneMoveAction action) =>
        action switch
        {
            MachineSceneMoveAction.Begin => SceneViewportMoveAction.Begin,
            MachineSceneMoveAction.Update => SceneViewportMoveAction.Update,
            MachineSceneMoveAction.Commit => SceneViewportMoveAction.Commit,
            MachineSceneMoveAction.Cancel => SceneViewportMoveAction.Cancel,
            _ => null
        };

    private static void Execute(ICommand? command, object parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }
}
