using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class ListBoxScrollIntoViewBehavior : Behavior<ListBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.SelectionChanged -= OnSelectionChanged;
        base.OnDetaching();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || sender is not ListBox listBox)
        {
            return;
        }

        var item = e.AddedItems[0];
        if (AssociatedObject.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(() =>
            {
                listBox.UpdateLayout();
                listBox.ScrollIntoView(item);
            }, DispatcherPriority.Loaded);
        }
    }
}

