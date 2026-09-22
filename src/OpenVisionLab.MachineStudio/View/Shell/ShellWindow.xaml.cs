using System.ComponentModel;
using System.Windows;

namespace OpenVisionLab.MachineStudio.View.Shell;

public partial class ShellWindow : MachineFluentWindow
{
    public static readonly DependencyProperty IsCompactLayoutProperty =
        DependencyProperty.RegisterAttached(
            "IsCompactLayout",
            typeof(bool),
            typeof(ShellWindow),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private bool _closeApproved;
    private bool _closeResolutionRunning;

    public ShellWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAdaptiveLayout(ActualWidth);
        SizeChanged += (_, args) => UpdateAdaptiveLayout(args.NewSize.Width);
    }

    public static bool GetIsCompactLayout(DependencyObject element) =>
        (bool)element.GetValue(IsCompactLayoutProperty);

    public static void SetIsCompactLayout(DependencyObject element, bool value) =>
        element.SetValue(IsCompactLayoutProperty, value);

    private void UpdateAdaptiveLayout(double width)
    {
        var compact = width < 1500;
        LeftWorkspaceColumn.Width = new GridLength(compact ? 350 : 360);
        RightWorkspaceColumn.Width = new GridLength(compact ? 300 : 360);

        SetIsCompactLayout(this, compact);
    }

    internal ShellLayoutMetrics CaptureLayoutMetrics() =>
        new(
            ActualWidth,
            ActualHeight,
            ShellRoot.RowDefinitions[0].ActualHeight,
            ShellRoot.RowDefinitions[1].ActualHeight,
            ShellRoot.RowDefinitions[2].ActualHeight,
            ShellRoot.RowDefinitions[4].ActualHeight,
            LeftWorkspaceColumn.ActualWidth,
            CenterWorkspaceColumn.ActualWidth,
            RightWorkspaceColumn.ActualWidth,
            BottomWorkspaceRow.ActualHeight,
            WorkspaceColumnsGrid.ActualWidth,
            WorkspaceRowsGrid.ActualHeight);

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeApproved || DataContext is not IShellCloseHost closeHost)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeResolutionRunning)
        {
            return;
        }

        _closeResolutionRunning = true;
        try
        {
            if (await closeHost.RequestCloseAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            closeHost.PresentCloseFailure(exception);
        }
        finally
        {
            _closeResolutionRunning = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
