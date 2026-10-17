using System.Windows;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Shell;

public partial class StudioStatusBarView : UserControl
{
    public static readonly DependencyProperty IsR24WorkspaceProperty = DependencyProperty.Register(nameof(IsR24Workspace), typeof(bool), typeof(StudioStatusBarView), new PropertyMetadata(false));

    public bool IsR24Workspace
    {
        get => (bool)GetValue(IsR24WorkspaceProperty);
        set => SetValue(IsR24WorkspaceProperty, value);
    }

    public StudioStatusBarView()
    {
        InitializeComponent();
    }
}
