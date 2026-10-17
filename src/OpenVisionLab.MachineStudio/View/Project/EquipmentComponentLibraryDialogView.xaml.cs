using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Project;

public partial class EquipmentComponentLibraryDialogView : UserControl
{
    private IInputElement? _previousFocus;

    public EquipmentComponentLibraryDialogView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opening = IsVisible;
        if (opening) _previousFocus = Keyboard.FocusedElement;
        var target = opening ? LibrarySearchTextBox : _previousFocus ?? Window.GetWindow(this);
        if (!opening) _previousFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible == opening && target is UIElement { IsVisible: true, IsEnabled: true } element)
                element.Focus();
        }));
    }
}
