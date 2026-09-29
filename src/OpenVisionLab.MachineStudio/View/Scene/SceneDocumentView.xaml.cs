using System.Windows;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Scene;

public partial class SceneDocumentView : UserControl
{
    public SceneDocumentView()
    {
        InitializeComponent();
    }

    private void LargeOverviewScrollViewer_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToTop();
        }
    }
}
