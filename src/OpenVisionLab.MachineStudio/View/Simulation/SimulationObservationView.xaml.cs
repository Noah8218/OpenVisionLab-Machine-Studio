using System.Windows;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Simulation;

public partial class SimulationObservationView : UserControl
{
    public static readonly DependencyProperty IsSidebarProperty = DependencyProperty.Register(
        nameof(IsSidebar), typeof(bool), typeof(SimulationObservationView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public SimulationObservationView()
    {
        InitializeComponent();
    }

    public bool IsSidebar
    {
        get => (bool)GetValue(IsSidebarProperty);
        set => SetValue(IsSidebarProperty, value);
    }
}
