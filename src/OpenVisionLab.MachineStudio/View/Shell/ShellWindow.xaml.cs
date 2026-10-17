using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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

    public static readonly DependencyProperty IsEquipmentInspectorOpenProperty = DependencyProperty.RegisterAttached(
        "IsEquipmentInspectorOpen", typeof(bool), typeof(ShellWindow),
        new PropertyMetadata(false, OnEquipmentInspectorOpenChanged));

    public static readonly DependencyProperty IsEquipmentWorkspaceProperty = DependencyProperty.Register(
        "IsEquipmentWorkspace", typeof(bool), typeof(ShellWindow), new PropertyMetadata(false, OnEquipmentInspectorOpenChanged));

    public static bool GetIsEquipmentWorkspace(DependencyObject element) => (bool)element.GetValue(IsEquipmentWorkspaceProperty);
    public static void SetIsEquipmentWorkspace(DependencyObject element, bool value) => element.SetValue(IsEquipmentWorkspaceProperty, value);

    public static readonly DependencyProperty IsSequenceWorkspaceProperty = DependencyProperty.Register(
        "IsSequenceWorkspace", typeof(bool), typeof(ShellWindow), new PropertyMetadata(false, OnEquipmentInspectorOpenChanged));

    public static bool GetIsSequenceWorkspace(DependencyObject element) => (bool)element.GetValue(IsSequenceWorkspaceProperty);
    public static void SetIsSequenceWorkspace(DependencyObject element, bool value) => element.SetValue(IsSequenceWorkspaceProperty, value);

    public static readonly DependencyProperty IsInspectionWorkspaceProperty = DependencyProperty.Register(
        "IsInspectionWorkspace", typeof(bool), typeof(ShellWindow), new PropertyMetadata(false, OnEquipmentInspectorOpenChanged));

    public static bool GetIsInspectionWorkspace(DependencyObject element) => (bool)element.GetValue(IsInspectionWorkspaceProperty);
    public static void SetIsInspectionWorkspace(DependencyObject element, bool value) => element.SetValue(IsInspectionWorkspaceProperty, value);

    public static readonly DependencyProperty IsResultsWorkspaceProperty = DependencyProperty.Register(
        "IsResultsWorkspace", typeof(bool), typeof(ShellWindow), new PropertyMetadata(false, OnEquipmentInspectorOpenChanged));

    public static bool GetIsResultsWorkspace(DependencyObject element) => (bool)element.GetValue(IsResultsWorkspaceProperty);
    public static void SetIsResultsWorkspace(DependencyObject element, bool value) => element.SetValue(IsResultsWorkspaceProperty, value);

    private double _inspectorDefaultWidth;
    private double? _equipmentInspectorWidth;
    private bool _wasEquipmentWorkspace;
    private bool _wasSequenceWorkspace;
    private bool _wasAlignedWorkspace;
    private bool _closeApproved;
    private bool _closeResolutionRunning;
    private bool _workspaceWidthsInitialized;
    private bool _isNarrowLayout;

    public ShellWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAdaptiveLayout(ActualWidth);
        SizeChanged += (_, args) => UpdateAdaptiveLayout(args.NewSize.Width);
        WorkspaceRowsGrid.IsVisibleChanged += OnWorkspaceVisibilityChanged;
        WorkspaceColumnsGrid.SizeChanged += (_, _) => UpdateAdaptiveLayout(ActualWidth);
    }

    private static void OnEquipmentInspectorOpenChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var window = (ShellWindow)sender;
        if (window._workspaceWidthsInitialized) window.UpdateAdaptiveLayout(window.ActualWidth);
    }

    public static bool GetIsCompactLayout(DependencyObject element) =>
        (bool)element.GetValue(IsCompactLayoutProperty);

    public static void SetIsCompactLayout(DependencyObject element, bool value) =>
        element.SetValue(IsCompactLayoutProperty, value);

    public static bool GetIsNarrowLayout(DependencyObject element) =>
        (bool)element.GetValue(IsNarrowLayoutProperty);

    public static void SetIsNarrowLayout(DependencyObject element, bool value) =>
        element.SetValue(IsNarrowLayoutProperty, value);

    public static bool GetIsEquipmentInspectorOpen(DependencyObject element) =>
        (bool)element.GetValue(IsEquipmentInspectorOpenProperty);

    public static void SetIsEquipmentInspectorOpen(DependencyObject element, bool value) =>
        element.SetValue(IsEquipmentInspectorOpenProperty, value);

    private void OnWorkspaceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (WorkspaceRowsGrid.IsVisible && IsActive && Keyboard.FocusedElement is not UIElement { IsVisible: true })
                    Focus();
            }));
        }
    }

    private void UpdateAdaptiveLayout(double width)
    {
        StartupContent.Width = Math.Min(1040, width * 0.92);
        StartupContent.Margin = new Thickness(0, ActualHeight * 0.07, 0, 40);
        var compact = width <= 1400;
        var narrow = width <= 1000;
        var equipmentWorkspace = GetIsEquipmentWorkspace(this);
        var sequenceWorkspace = GetIsSequenceWorkspace(this);
        var alignedWorkspace = equipmentWorkspace || sequenceWorkspace || GetIsInspectionWorkspace(this) || GetIsResultsWorkspace(this);
        var singleWorkspaceBar = width >= 1280;
        var splitterWidth = equipmentWorkspace || sequenceWorkspace ? 5 : 4;
        if (!_workspaceWidthsInitialized || compact != GetIsCompactLayout(this) || narrow != _isNarrowLayout || equipmentWorkspace != _wasEquipmentWorkspace || sequenceWorkspace != _wasSequenceWorkspace || alignedWorkspace != _wasAlignedWorkspace)
        {
            LeftWorkspaceColumn.Width = new GridLength(narrow ? 185 : alignedWorkspace ? compact ? 210 : 230 : compact ? 215 : 240);
            StartupCardsPanel.Columns = narrow ? 1 : 3;
            StartupCardsPanel.Rows = narrow ? 3 : 1;
            _workspaceWidthsInitialized = true;
            _isNarrowLayout = narrow;
        }

        const double inspectorDefaultWidth = 360;
        if (equipmentWorkspace)
        {
            if (!_wasEquipmentWorkspace) RightWorkspaceColumn.Width = new GridLength(_equipmentInspectorWidth ?? inspectorDefaultWidth);
            _equipmentInspectorWidth = RightWorkspaceColumn.Width.Value;
        }
        else if (_inspectorDefaultWidth != inspectorDefaultWidth || _wasEquipmentWorkspace)
        {
            RightWorkspaceColumn.Width = new GridLength(inspectorDefaultWidth);
            _inspectorDefaultWidth = inspectorDefaultWidth;
        }
        _wasEquipmentWorkspace = equipmentWorkspace;
        _wasSequenceWorkspace = sequenceWorkspace;
        _wasAlignedWorkspace = alignedWorkspace;

        CommonWorkspaceToolbar.Height = singleWorkspaceBar ? 44 : double.NaN;
        CommonWorkspaceToolbar.RowDefinitions[0].Height = singleWorkspaceBar ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumnSpan(WorkspaceTabsRegion, compact && !singleWorkspaceBar ? 2 : 1);
        Grid.SetRow(CommonCommandBar, compact && !singleWorkspaceBar ? 1 : 0);
        Grid.SetColumn(CommonCommandBar, compact && !singleWorkspaceBar ? 0 : 1);
        Grid.SetColumnSpan(CommonCommandBar, compact && !singleWorkspaceBar ? 2 : 1);
        CommonCommandBar.VerticalAlignment = singleWorkspaceBar ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;

        SetIsCompactLayout(this, compact);
        SetIsNarrowLayout(this, narrow);

        var docked = GetIsEquipmentInspectorOpen(this);
        var availableWidth = Math.Min(width, WorkspaceColumnsGrid.ActualWidth);
        var inspectorMaxWidth = Math.Clamp(availableWidth - CenterWorkspaceColumn.MinWidth - splitterWidth, 300, 520);
        var inspectorWidth = Math.Clamp(RightWorkspaceColumn.Width.Value, 300, inspectorMaxWidth);
        var navigationSpace = availableWidth - CenterWorkspaceColumn.MinWidth - inspectorWidth - splitterWidth - (alignedWorkspace ? 0 : splitterWidth);
        var hideNavigation = docked && navigationSpace < 185;
        RightWorkspaceColumn.MinWidth = docked ? 300 : 0;
        RightWorkspaceColumn.MaxWidth = docked ? inspectorMaxWidth : 0;
        RightWorkspaceSplitterColumn.Width = new GridLength(docked ? splitterWidth : 0);
        RightWorkspaceSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        LeftWorkspaceColumn.MinWidth = hideNavigation ? 0 : 185;
        LeftWorkspaceColumn.MaxWidth = hideNavigation ? 0 : docked ? Math.Min(420, navigationSpace) : 420;
        LeftWorkspaceSplitterColumn.Width = new GridLength(hideNavigation || alignedWorkspace ? 0 : splitterWidth);
        LeftWorkspaceSplitter.Visibility = hideNavigation || alignedWorkspace ? Visibility.Collapsed : Visibility.Visible;
        LeftToolRegionView.Visibility = hideNavigation ? Visibility.Collapsed : Visibility.Visible;
    }

    internal ShellLayoutMetrics CaptureLayoutMetrics() =>
        new(
            ActualWidth,
            ActualHeight,
            ShellRoot.RowDefinitions[0].ActualHeight,
            ShellRoot.RowDefinitions[1].ActualHeight,
            CommonCommandBar.ActualHeight,
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
