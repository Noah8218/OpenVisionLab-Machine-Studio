using System.Windows;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Inspector;

public partial class CameraImageSourceView : UserControl
{
    public static readonly DependencyProperty CaptureSectionProperty = DependencyProperty.Register(nameof(CaptureSection), typeof(int), typeof(CameraImageSourceView), new PropertyMetadata(-1));

    // Presentation section only; every instance binds to the existing camera-scoped draft owner.
    public int CaptureSection { get => (int)GetValue(CaptureSectionProperty); set => SetValue(CaptureSectionProperty, value); }

    public CameraImageSourceView() => InitializeComponent();
}
