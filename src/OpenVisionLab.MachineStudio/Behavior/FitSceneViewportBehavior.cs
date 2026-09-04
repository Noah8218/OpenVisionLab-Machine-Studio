using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.View.Scene;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class FitSceneViewportBehavior : Behavior<ButtonBase>
{
    public static readonly DependencyProperty SceneViewportProperty =
        DependencyProperty.Register(
            nameof(SceneViewport),
            typeof(MachineSceneViewport),
            typeof(FitSceneViewportBehavior),
            new PropertyMetadata(null));

    public MachineSceneViewport? SceneViewport
    {
        get => (MachineSceneViewport?)GetValue(SceneViewportProperty);
        set => SetValue(SceneViewportProperty, value);
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
        SceneViewport?.FitToLayout();
}
