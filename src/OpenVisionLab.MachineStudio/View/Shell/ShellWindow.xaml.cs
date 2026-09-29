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

    public static readonly DependencyProperty IsNarrowLayoutProperty =
        DependencyProperty.RegisterAttached(
            "IsNarrowLayout",
            typeof(bool),
            typeof(ShellWindow),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private bool _closeApproved;
    private bool _closeResolutionRunning;
    private bool _workspaceWidthsInitialized;
    private bool _isNarrowLayout;

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

    public static bool GetIsNarrowLayout(DependencyObject element) =>
        (bool)element.GetValue(IsNarrowLayoutProperty);

    public static void SetIsNarrowLayout(DependencyObject element, bool value) =>
        element.SetValue(IsNarrowLayoutProperty, value);

    private void UpdateAdaptiveLayout(double width)
    {
        var compact = width <= 1400;
        var narrow = width <= 1000;
        if (!_workspaceWidthsInitialized || compact != GetIsCompactLayout(this) || narrow != _isNarrowLayout)
        {
            LeftWorkspaceColumn.Width = new GridLength(narrow ? 185 : compact ? 215 : 240);
            StartupCardsPanel.Columns = narrow ? 1 : 3;
            StartupCardsPanel.Rows = narrow ? 3 : 1;
            _workspaceWidthsInitialized = true;
            _isNarrowLayout = narrow;
        }

        SetIsCompactLayout(this, compact);
        SetIsNarrowLayout(this, narrow);
    }

    internal ShellLayoutMetrics CaptureLayoutMetrics() =>
        new(
            ActualWidth,
            ActualHeight,
            ShellRoot.RowDefinitions[0].ActualHeight,
            ShellRoot.RowDefinitions[1].ActualHeight,
            WorkspaceRowsGrid.RowDefinitions[1].ActualHeight,
            ShellRoot.RowDefinitions[3].ActualHeight,
            LeftWorkspaceColumn.ActualWidth,
            CenterWorkspaceColumn.ActualWidth,
            RightToolRegionView.ActualWidth,
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
