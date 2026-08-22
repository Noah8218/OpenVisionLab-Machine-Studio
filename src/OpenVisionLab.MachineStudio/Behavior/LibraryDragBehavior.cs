using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class LibraryDragBehavior : Behavior<FrameworkElement>
{
    public static readonly DependencyProperty DragDataProperty = DependencyProperty.Register(
        nameof(DragData),
        typeof(object),
        typeof(LibraryDragBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty DragEffectsProperty = DependencyProperty.Register(
        nameof(DragEffects),
        typeof(DragDropEffects),
        typeof(LibraryDragBehavior),
        new PropertyMetadata(DragDropEffects.Copy));

    public object? DragData
    {
        get => GetValue(DragDataProperty);
        set => SetValue(DragDataProperty, value);
    }

    public DragDropEffects DragEffects
    {
        get => (DragDropEffects)GetValue(DragEffectsProperty);
        set => SetValue(DragEffectsProperty, value);
    }

    private Point? _dragStart;
    private bool _isDragging;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        AssociatedObject.PreviewMouseMove += OnMouseMove;
        AssociatedObject.PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
        AssociatedObject.PreviewMouseMove -= OnMouseMove;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnMouseLeftButtonUp;
        base.OnDetaching();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragStart = e.GetPosition(AssociatedObject);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragStart = null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging ||
            e.LeftButton != MouseButtonState.Pressed ||
            _dragStart is not { } dragStart ||
            GetEffectiveDragData() is not { } dragData)
        {
            return;
        }

        var current = e.GetPosition(AssociatedObject);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isDragging = true;
        _dragStart = null;
        e.Handled = true;
        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new DataObject(dragData.GetType(), dragData),
            DragEffects);
    }

    private object? GetEffectiveDragData() =>
        DragData ?? AssociatedObject.DataContext;
}
