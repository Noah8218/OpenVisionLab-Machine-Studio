using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Scene;

public partial class SceneDocumentView : UserControl
{
    private IInputElement? _stationUnitReturnFocus;

    public SceneDocumentView()
    {
        InitializeComponent();
    }

    private void StationUnitEditor_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opening = e.NewValue is true;
        if (opening) _stationUnitReturnFocus = Keyboard.FocusedElement;
        var target = opening ? StationUnitNameTextBox : _stationUnitReturnFocus;
        if (!opening) _stationUnitReturnFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && StationUnitEditorOverlay.IsVisible == opening
                && target is UIElement { IsVisible: true, IsEnabled: true } element)
            {
                element.Focus();
            }
        }));
    }

    private void LargeOverviewScrollViewer_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToTop();
        }
    }
}
