using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace OpenVisionLab.MachineStudio.View.Shell;

public partial class GlobalCommandBarView : UserControl
{
    public GlobalCommandBarView()
    {
        InitializeComponent();
    }

    private void OnPopupOpened(object? sender, EventArgs e) => ((Popup)sender!).Child.Focus();

    private void OnPopupClosed(object? sender, EventArgs e) => ((Popup)sender!).PlacementTarget.Focus();

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not FrameworkElement { Parent: Popup popup }) return;
        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        e.Handled = true;
    }
}
