using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.View.Scene;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class ZoomSceneViewportBehavior : Behavior<ButtonBase>
{
    public static readonly DependencyProperty SceneViewportProperty =
        DependencyProperty.Register(
            nameof(SceneViewport),
            typeof(MachineSceneViewport),
            typeof(ZoomSceneViewportBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty WheelDeltaProperty =
        DependencyProperty.Register(
            nameof(WheelDelta),
            typeof(int),
            typeof(ZoomSceneViewportBehavior),
            new PropertyMetadata(120));

    public MachineSceneViewport? SceneViewport
    {
        get => (MachineSceneViewport?)GetValue(SceneViewportProperty);
        set => SetValue(SceneViewportProperty, value);
    }

    public int WheelDelta
    {
        get => (int)GetValue(WheelDeltaProperty);
        set => SetValue(WheelDeltaProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Click += OnClick;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Click -= OnClick;
        base.OnDetaching();
    }

    private void OnClick(object sender, RoutedEventArgs e) =>
        SceneViewport?.ZoomAtCenter(WheelDelta);
}
