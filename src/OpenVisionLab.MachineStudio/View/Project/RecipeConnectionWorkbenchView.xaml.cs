using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Project;

public partial class RecipeConnectionWorkbenchView : UserControl
{
    private IInputElement? _previousFocus;

    public RecipeConnectionWorkbenchView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
        SizeChanged += (_, _) => UpdateDialogSize();
    }

    private void UpdateDialogSize()
    {
        ConnectionsDialogBorder.Width = Math.Max(0, Math.Min(1100, ActualWidth - 40));
        ConnectionsDialogBorder.Height = Math.Max(0, ActualHeight - 60);
        ConnectionSetupScrollViewer.MaxHeight = Math.Max(0, Math.Min(420, (ActualHeight - 160) * 0.55));
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opening = IsVisible;
        if (opening)
        {
            UpdateDialogSize();
            _previousFocus = Keyboard.FocusedElement;
            if (_previousFocus is null or Window && Window.GetWindow(this) is Window owner)
                _previousFocus = FindViewMenu(owner) ?? _previousFocus;
            while (_previousFocus is MenuItem menuItem && ItemsControl.ItemsControlFromItemContainer(menuItem) is MenuItem parent)
                _previousFocus = parent;
        }
        var target = opening ? CloseRecipeConnectionsButton : _previousFocus ?? Window.GetWindow(this);
        if (!opening) _previousFocus = null;
        Dispatcher.BeginInvoke(opening ? DispatcherPriority.Input : DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (IsVisible == opening && target is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        }));
    }

    private static UIElement? FindViewMenu(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "ViewMenuItem" } menu) return menu;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindViewMenu(VisualTreeHelper.GetChild(root, index)) is UIElement found) return found;
        return null;
    }
}
