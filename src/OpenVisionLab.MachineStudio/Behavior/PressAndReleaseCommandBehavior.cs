using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class PressAndReleaseCommandBehavior : Behavior<ButtonBase>
{
    public static readonly DependencyProperty PressCommandProperty =
        DependencyProperty.Register(
            nameof(PressCommand),
            typeof(ICommand),
            typeof(PressAndReleaseCommandBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ReleaseCommandProperty =
        DependencyProperty.Register(
            nameof(ReleaseCommand),
            typeof(ICommand),
            typeof(PressAndReleaseCommandBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PressCommandParameterProperty =
        DependencyProperty.Register(
            nameof(PressCommandParameter),
            typeof(object),
            typeof(PressAndReleaseCommandBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ReleaseCommandParameterProperty =
        DependencyProperty.Register(
            nameof(ReleaseCommandParameter),
            typeof(object),
            typeof(PressAndReleaseCommandBehavior),
            new PropertyMetadata(null));

    public ICommand? PressCommand
    {
        get => (ICommand?)GetValue(PressCommandProperty);
        set => SetValue(PressCommandProperty, value);
    }

    public ICommand? ReleaseCommand
    {
        get => (ICommand?)GetValue(ReleaseCommandProperty);
        set => SetValue(ReleaseCommandProperty, value);
    }

    public object? PressCommandParameter
    {
        get => GetValue(PressCommandParameterProperty);
        set => SetValue(PressCommandParameterProperty, value);
    }

    public object? ReleaseCommandParameter
    {
        get => GetValue(ReleaseCommandParameterProperty);
        set => SetValue(ReleaseCommandParameterProperty, value);
    }

    private bool _isPressed;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
        AssociatedObject.PreviewKeyUp += OnPreviewKeyUp;
        AssociatedObject.LostMouseCapture += OnLostMouseCapture;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
        AssociatedObject.PreviewKeyUp -= OnPreviewKeyUp;
        AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
        base.OnDetaching();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryPress())
        {
            e.Handled = true;
            AssociatedObject.CaptureMouse();
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasPressed = _isPressed;
        TryRelease();
        e.Handled = wasPressed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.IsRepeat && (e.Key is Key.Space or Key.Enter))
        {
            if (TryPress())
            {
                e.Handled = true;
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            var wasPressed = _isPressed;
            TryRelease();
            e.Handled = wasPressed || e.Handled;
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) =>
        TryRelease();

    private bool TryPress()
    {
        if (_isPressed || PressCommand is null || !PressCommand.CanExecute(PressCommandParameter))
        {
            return false;
        }

        PressCommand.Execute(PressCommandParameter);
        _isPressed = true;
        return true;
    }

    private void TryRelease()
    {
        if (!_isPressed || AssociatedObject is not { } button)
        {
            return;
        }

        _isPressed = false;
        if (button.IsMouseCaptured)
        {
            button.ReleaseMouseCapture();
        }

        if (ReleaseCommand?.CanExecute(ReleaseCommandParameter) == true)
        {
            ReleaseCommand.Execute(ReleaseCommandParameter);
        }
    }
}
