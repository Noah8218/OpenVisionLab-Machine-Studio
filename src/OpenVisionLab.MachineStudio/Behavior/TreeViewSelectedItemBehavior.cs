using Microsoft.Xaml.Behaviors;
using System.Windows;
using System.Windows.Controls;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class TreeViewSelectedItemBehavior : Behavior<TreeView>
{
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(TreeNodeViewModel),
            typeof(TreeViewSelectedItemBehavior),
            new PropertyMetadata(null, OnSelectedItemChanged));

    public TreeNodeViewModel? SelectedItem
    {
        get => (TreeNodeViewModel?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SelectedItemChanged += OnTreeViewSelectedItemChanged;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.SelectedItemChanged -= OnTreeViewSelectedItemChanged;
        base.OnDetaching();
    }

    private void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node)
        {
            SelectedItem = node;
        }
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewSelectedItemBehavior behavior || behavior.AssociatedObject is null)
            return;

        if (e.NewValue is TreeNodeViewModel node)
        {
            var container = FindTreeViewItem(behavior.AssociatedObject, node);
            if (container is not null)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }
        }
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, TreeNodeViewModel target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem item)
            return item;

        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childItem)
                continue;

            if (childItem.DataContext == target)
                return childItem;

            var found = FindTreeViewItem(childItem, target);
            if (found is not null)
                return found;
        }

        return null;
    }
}
