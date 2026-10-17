using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace OpenVisionLab.MachineStudio.View.Shell;

public partial class StudioTitleBar : UserControl
{
    private FrameworkElement? _faultWorkspace;
    private FrameworkElement? _faultToolbar;

    public StudioTitleBar()
    {
        InitializeComponent();
    }

    private void OnFaultPopupOpened(object? sender, EventArgs e)
    {
        var window = Window.GetWindow(this);
        if (FaultSimulationPopup.Child is FrameworkElement panel && window is not null)
        {
            panel.SetBinding(ShellWindow.IsCompactLayoutProperty, new Binding { Path = new PropertyPath(ShellWindow.IsCompactLayoutProperty), Source = window, Mode = BindingMode.OneWay });
            _faultWorkspace = window.FindName("WorkspaceRowsGrid") as FrameworkElement;
            _faultToolbar = window.FindName("CommonWorkspaceToolbar") as FrameworkElement;
            if (_faultWorkspace is not null) _faultWorkspace.SizeChanged += OnFaultViewportSizeChanged;
            if (_faultToolbar is not null) _faultToolbar.SizeChanged += OnFaultViewportSizeChanged;
            UpdateFaultDrawerPlacement();
            panel.Focus();
        }
    }

    private void OnFaultViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFaultDrawerPlacement();

    private void UpdateFaultDrawerPlacement()
    {
        if (FaultSimulationPopup.Child is not FrameworkElement panel) return;
        if (_faultWorkspace is { IsVisible: true, ActualHeight: > 0 } && _faultToolbar is not null)
        {
            FaultSimulationPopup.SetCurrentValue(Popup.PlacementTargetProperty, _faultWorkspace);
            FaultSimulationPopup.Placement = PlacementMode.Right;
            FaultSimulationPopup.HorizontalOffset = -panel.Width;
            FaultSimulationPopup.VerticalOffset = _faultToolbar.ActualHeight;
            panel.Height = Math.Max(0, _faultWorkspace.ActualHeight - _faultToolbar.ActualHeight);
        }
        else
        {
            FaultSimulationPopup.SetCurrentValue(Popup.PlacementTargetProperty, FaultSimulationButton);
            FaultSimulationPopup.Placement = PlacementMode.Bottom;
            FaultSimulationPopup.HorizontalOffset = 0;
            FaultSimulationPopup.VerticalOffset = 10;
            panel.ClearValue(HeightProperty);
        }
    }

    private void OnFaultPopupClosed(object? sender, EventArgs e)
    {
        ReleaseFaultDrawerPlacement();
        FaultSimulationButton.Focus();
    }

    private void OnTitleBarUnloaded(object sender, RoutedEventArgs e)
    {
        FaultSimulationPopup.SetCurrentValue(Popup.IsOpenProperty, false);
        ReleaseFaultDrawerPlacement();
    }

    private void ReleaseFaultDrawerPlacement()
    {
        if (_faultWorkspace is not null) _faultWorkspace.SizeChanged -= OnFaultViewportSizeChanged;
        if (_faultToolbar is not null) _faultToolbar.SizeChanged -= OnFaultViewportSizeChanged;
        _faultWorkspace = null;
        _faultToolbar = null;
        if (FaultSimulationPopup.Child is FrameworkElement panel)
        {
            BindingOperations.ClearBinding(panel, ShellWindow.IsCompactLayoutProperty);
            panel.ClearValue(HeightProperty);
        }
    }
}
