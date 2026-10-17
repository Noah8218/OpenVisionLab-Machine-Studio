using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class InspectorScrollContextBehavior : Behavior<ScrollViewer>
{
    public static readonly DependencyProperty ContextProperty = DependencyProperty.Register(
        nameof(Context), typeof(string), typeof(InspectorScrollContextBehavior), new PropertyMetadata(null, OnContextChanged));

    public string? Context
    {
        get => (string?)GetValue(ContextProperty);
        set => SetValue(ContextProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.IsVisibleChanged += OnVisibilityChanged;
        AssociatedObject.ScrollToTop();
    }

    protected override void OnDetaching()
    {
        AssociatedObject.IsVisibleChanged -= OnVisibilityChanged;
        base.OnDetaching();
    }

    private static void OnContextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((InspectorScrollContextBehavior)sender).AssociatedObject?.ScrollToTop();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is true) AssociatedObject.ScrollToTop();
    }
}
