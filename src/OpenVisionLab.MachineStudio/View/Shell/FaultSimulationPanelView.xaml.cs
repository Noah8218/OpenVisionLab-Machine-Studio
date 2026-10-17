using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace OpenVisionLab.MachineStudio.View.Shell;

public partial class FaultSimulationPanelView : UserControl
{
    public static readonly DependencyProperty IsEmbeddedProperty = DependencyProperty.Register(nameof(IsEmbedded), typeof(bool), typeof(FaultSimulationPanelView), new PropertyMetadata(false));

    public bool IsEmbedded { get => (bool)GetValue(IsEmbeddedProperty); set => SetValue(IsEmbeddedProperty, value); }

    public FaultSimulationPanelView() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Parent is Popup popup) popup.SetCurrentValue(Popup.IsOpenProperty, false);
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEmbedded || e.Key != Key.Escape) return;
        OnCloseClick(sender, e);
        e.Handled = true;
    }
}
