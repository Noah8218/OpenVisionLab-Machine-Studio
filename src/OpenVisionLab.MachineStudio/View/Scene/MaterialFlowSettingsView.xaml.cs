using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Scene;

public partial class MaterialFlowSettingsView : UserControl
{
    private IInputElement? _previousFocus;
    public MaterialFlowSettingsView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _previousFocus = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (IsVisible) MaterialWorkpieceNameTextBox.Focus(); }));
        }
        else
        {
            if (_previousFocus is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
            _previousFocus = null;
        }
    }
}
