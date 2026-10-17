using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Simulation;

public partial class SimulationTestWorkspaceView : UserControl
{
    private IInputElement? _previousFocus;

    public SimulationTestWorkspaceView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => TestWorkspaceScrollViewer.MaxHeight = Math.Max(0, ActualHeight - 210);

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opening = IsVisible;
        if (opening)
        {
            _previousFocus = Keyboard.FocusedElement;
            if (_previousFocus is null or Window && Window.GetWindow(this) is Window owner)
                _previousFocus = FindViewMenu(owner) ?? _previousFocus;
            while (_previousFocus is MenuItem menuItem && ItemsControl.ItemsControlFromItemContainer(menuItem) is MenuItem parent)
                _previousFocus = parent;
        }
        UIElement firstFocus = ScenarioProfileComboBox.IsEnabled ? ScenarioProfileComboBox : CloseTestScenarioButton;
        var target = opening ? firstFocus : _previousFocus ?? Window.GetWindow(this);
        if (!opening) _previousFocus = null;
        Dispatcher.BeginInvoke(opening ? DispatcherPriority.Input : DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (IsVisible == opening && target is UIElement { IsVisible: true, IsEnabled: true } element)
                element.Focus();
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
