using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Project;

public partial class NewProjectNameDialogView : UserControl
{
    private IInputElement? _previousFocus;

    public NewProjectNameDialogView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
        ProjectNameTextBox.IsEnabledChanged += OnNameInputEnabledChanged;
    }

    private void OnNameInputEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || !ProjectNameTextBox.IsEnabled) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && ProjectNameTextBox.IsEnabled) ProjectNameTextBox.Focus();
        }));
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opening = IsVisible;
        if (opening) _previousFocus = Keyboard.FocusedElement;
        var target = opening ? ProjectNameTextBox : _previousFocus ?? Window.GetWindow(this);
        if (!opening) _previousFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible == opening && target is UIElement { IsVisible: true, IsEnabled: true } element)
                element.Focus();
        }));
    }
}
