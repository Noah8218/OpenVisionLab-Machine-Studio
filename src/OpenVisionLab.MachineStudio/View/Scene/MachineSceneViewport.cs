using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.View.Scene;

public sealed class MachineSceneSelectionRequestedEventArgs(
    LayoutItem item,
    ModifierKeys modifiers) : EventArgs
{
    public LayoutItem Item { get; } = item;
    public ModifierKeys Modifiers { get; } = modifiers;
}

public enum MachineSceneMoveAction
{
    Begin,
    Update,
    Commit,
    Cancel
}

public sealed class MachineSceneMoveRequestedEventArgs(
    MachineSceneMoveAction action,
    Vector delta) : EventArgs
{
    public MachineSceneMoveAction Action { get; } = action;
    public Vector Delta { get; } = delta;
}

public sealed class MachineSceneMarqueeSelectionRequestedEventArgs(
    IReadOnlyList<LayoutItem> items,
    ModifierKeys modifiers) : EventArgs
{
    public IReadOnlyList<LayoutItem> Items { get; } = items;
    public ModifierKeys Modifiers { get; } = modifiers;
}

public sealed class MachineSceneTransformRequestedEventArgs(
    MachineSceneMoveAction action,
    LayoutTransformHandle handle,
    Point worldPoint,
    ModifierKeys modifiers) : EventArgs
{
    public MachineSceneMoveAction Action { get; } = action;
    public LayoutTransformHandle Handle { get; } = handle;
    public Point WorldPoint { get; } = worldPoint;
    public ModifierKeys Modifiers { get; } = modifiers;
}

public sealed class MachineSceneLibraryComponentDropRequestedEventArgs(
    LayoutComponentKind kind,
    Point worldPoint) : EventArgs
{
    public LayoutComponentKind Kind { get; } = kind;
    public Point WorldPoint { get; } = worldPoint;
}

/// <summary>
/// Lightweight Phase 0 scene renderer. Static project definitions are read from
/// ItemsSource while high-frequency motion is read from immutable snapshots.
/// </summary>
public sealed class MachineSceneViewport : FrameworkElement
{
    private const string GripperSignalId = "do.gripper";
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _gridVisual = new();
    private readonly DrawingVisual _sceneVisual = new();
    private readonly Dictionary<string, FormattedText> _textCache = new(StringComparer.Ordinal);
    private readonly HashSet<INotifyPropertyChanged> _observedItems = new();
    private INotifyCollectionChanged? _observedCollection;
    private SceneRenderResources? _resources;
    private SimulationSnapshot? _lastSnapshot;
    private int _snapshotRenderQueued;
    private PointerGesture _pointerGesture;
    private Point _gestureStart;
    private LayoutItem? _pressedItem;
    private ModifierKeys _gestureModifiers;
    private LayoutProjection? _gestureProjection;
    private LayoutProjection? _viewProjection;
    private Rect? _marqueeBounds;
    private double _zoomFactor = 1d;
    private LayoutTransformHandle? _transformHandle;
    private Point? _libraryDropPreviewPoint;

    public event EventHandler<MachineSceneSelectionRequestedEventArgs>? SelectionRequested;
    public event EventHandler<MachineSceneMoveRequestedEventArgs>? MoveRequested;
    public event EventHandler<MachineSceneMarqueeSelectionRequestedEventArgs>? MarqueeSelectionRequested;
    public event EventHandler<MachineSceneTransformRequestedEventArgs>? TransformRequested;
    public event EventHandler<MachineSceneLibraryComponentDropRequestedEventArgs>?
        LibraryComponentDropRequested;

    public MachineSceneViewport()
    {
        _visuals = new VisualCollection(this) { _gridVisual, _sceneVisual };
        AllowDrop = true;
        Focusable = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable<LayoutItem>),
            typeof(MachineSceneViewport),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SnapshotSourceProperty =
        DependencyProperty.Register(
            nameof(SnapshotSource),
            typeof(SceneSnapshotStore),
            typeof(MachineSceneViewport),
            new PropertyMetadata(null, OnSnapshotSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(LayoutItem),
            typeof(MachineSceneViewport),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedItemChanged));

    public static readonly DependencyProperty IsDesignModeProperty =
        DependencyProperty.Register(
            nameof(IsDesignMode),
            typeof(bool),
            typeof(MachineSceneViewport),
            new PropertyMetadata(true, OnIsDesignModeChanged));

    public IEnumerable<LayoutItem>? ItemsSource
    {
        get => (IEnumerable<LayoutItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public SceneSnapshotStore? SnapshotSource
    {
        get => (SceneSnapshotStore?)GetValue(SnapshotSourceProperty);
        set => SetValue(SnapshotSourceProperty, value);
    }

    public LayoutItem? SelectedItem
    {
        get => (LayoutItem?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public bool IsDesignMode
    {
        get => (bool)GetValue(IsDesignModeProperty);
        set => SetValue(IsDesignModeProperty, value);
    }

    internal double LastFormattedTextPixelsPerDip { get; private set; }
    internal bool? LastRenderedGripperValue { get; private set; }
    internal string? LastRenderedGripperText { get; private set; }
    internal PickPlaceWorkpieceSnapshot? LastRenderedWorkpiece { get; private set; }
    internal string? LastRenderedWorkpieceText { get; private set; }
    internal WaferHandlerOwnershipState? LastRenderedTransferOwnershipState { get; private set; }
    internal string? LastRenderedTransferOwnershipText { get; private set; }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle ||
            _pointerGesture != PointerGesture.None ||
            CreateCurrentProjection() is not { } projection)
        {
            return;
        }

        Focus();
        _gestureStart = e.GetPosition(this);
        _gestureProjection = projection;
        _pointerGesture = PointerGesture.Panning;
        Cursor = Cursors.ScrollAll;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        UpdateLibraryDrag(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateLibraryDrag(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        ClearLibraryDropPreview();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        var item = GetLibraryItem(e.Data);
        var point = e.GetPosition(this);
        ClearLibraryDropPreview();
        if (item is null || !RequestLibraryComponentDrop(item.Kind, point))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_pointerGesture != PointerGesture.None)
        {
            return;
        }
        Focus();
        var point = e.GetPosition(this);
        var modifiers = Keyboard.Modifiers;
        var transformHandle = HitTestTransformHandle(point);
        if (transformHandle is not null)
        {
            _gestureStart = point;
            _gestureProjection = CreateCurrentProjection();
            _transformHandle = transformHandle;
            _pointerGesture = PointerGesture.Transforming;
            Cursor = GetTransformCursor(transformHandle.Value);
            TransformRequested?.Invoke(
                this,
                new MachineSceneTransformRequestedEventArgs(
                    MachineSceneMoveAction.Begin,
                    transformHandle.Value,
                    default,
                    modifiers));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var item = HitTestItem(point, SnapshotSource?.Latest);
        if (!IsDesignMode)
        {
            SetCurrentValue(SelectedItemProperty, item);
            e.Handled = true;
            return;
        }

        if (item is not null && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            RequestExtendedSelectionAt(point, modifiers);
            e.Handled = true;
            return;
        }

        if (item is not null && !item.IsSelected)
        {
            SetCurrentValue(SelectedItemProperty, item);
        }

        _gestureStart = point;
        _pressedItem = item;
        _gestureModifiers = modifiers;
        _gestureProjection = CreateCurrentProjection();
        _pointerGesture = item is null ? PointerGesture.PendingMarquee : PointerGesture.PendingMove;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pointerGesture == PointerGesture.Panning)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                UpdatePan(e.GetPosition(this));
                e.Handled = true;
            }
            return;
        }

        if (_pointerGesture == PointerGesture.None)
        {
            Cursor = HitTestTransformHandle(e.GetPosition(this)) is { } handle
                ? GetTransformCursor(handle)
                : null;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_pointerGesture == PointerGesture.Transforming)
        {
            UpdateTransform(point);
            e.Handled = true;
            return;
        }
        if (_pointerGesture is PointerGesture.PendingMove or PointerGesture.PendingMarquee &&
            Math.Abs(point.X - _gestureStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _gestureStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (_pointerGesture == PointerGesture.PendingMove)
        {
            _pointerGesture = PointerGesture.Moving;
            Cursor = Cursors.SizeAll;
            MoveRequested?.Invoke(
                this,
                new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.Begin, default));
        }
        else if (_pointerGesture == PointerGesture.PendingMarquee)
        {
            _pointerGesture = PointerGesture.Marquee;
            Cursor = Cursors.Cross;
        }

        if (_pointerGesture == PointerGesture.Moving && _gestureProjection is { Scale: > 0 } projection)
        {
            MoveRequested?.Invoke(
                this,
                new MachineSceneMoveRequestedEventArgs(
                    MachineSceneMoveAction.Update,
                    new Vector(
                        (point.X - _gestureStart.X) / projection.Scale,
                        (point.Y - _gestureStart.Y) / projection.Scale)));
        }
        else if (_pointerGesture == PointerGesture.Marquee)
        {
            _marqueeBounds = NormalizeRect(_gestureStart, point);
            InvalidateScene();
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = e.GetPosition(this);
        switch (_pointerGesture)
        {
            case PointerGesture.PendingMove:
                SetCurrentValue(SelectedItemProperty, _pressedItem);
                break;
            case PointerGesture.Moving:
                UpdateMove(point);
                MoveRequested?.Invoke(
                    this,
                    new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.Commit, default));
                break;
            case PointerGesture.PendingMarquee:
                if ((_gestureModifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
                {
                    SetCurrentValue(SelectedItemProperty, null);
                }
                break;
            case PointerGesture.Marquee:
                _marqueeBounds = NormalizeRect(_gestureStart, point);
                RaiseMarqueeSelection(_marqueeBounds.Value, _gestureModifiers);
                break;
            case PointerGesture.Transforming:
                UpdateTransform(point);
                RaiseTransform(MachineSceneMoveAction.Commit, point);
                break;
        }

        ResetPointerGesture(releaseMouse: true);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || _pointerGesture != PointerGesture.Panning)
        {
            return;
        }

        UpdatePan(e.GetPosition(this));
        ResetPointerGesture(releaseMouse: true);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_pointerGesture != PointerGesture.None)
        {
            return;
        }

        if (ZoomAt(e.GetPosition(this), e.Delta))
        {
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_pointerGesture == PointerGesture.Moving)
        {
            MoveRequested?.Invoke(
                this,
                new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.Cancel, default));
        }
        else if (_pointerGesture == PointerGesture.Transforming)
        {
            RaiseTransform(MachineSceneMoveAction.Cancel, default);
        }
        ResetPointerGesture(releaseMouse: false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _pointerGesture == PointerGesture.None)
        {
            return;
        }

        if (_pointerGesture == PointerGesture.Moving)
        {
            MoveRequested?.Invoke(
                this,
                new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.Cancel, default));
        }
        else if (_pointerGesture == PointerGesture.Transforming)
        {
            RaiseTransform(MachineSceneMoveAction.Cancel, default);
        }
        else if (_pointerGesture == PointerGesture.Panning)
        {
            _viewProjection = _gestureProjection;
            DrawGrid();
        }
        ResetPointerGesture(releaseMouse: true);
        e.Handled = true;
    }

    internal double ZoomFactor => _zoomFactor;

    internal bool ZoomAt(Point anchor, int wheelDelta)
    {
        if (wheelDelta == 0 || CreateCurrentProjection() is not { } projection)
        {
            return false;
        }

        var targetZoom = Math.Clamp(
            _zoomFactor * Math.Pow(1.12d, wheelDelta / 120d),
            0.25d,
            8d);
        var factor = targetZoom / _zoomFactor;
        if (Math.Abs(factor - 1d) < 0.000001d)
        {
            return false;
        }

        _zoomFactor = targetZoom;
        _viewProjection = projection.ZoomAt(anchor, factor);
        DrawGrid();
        InvalidateScene();
        return true;
    }

    internal bool PanBy(Vector delta)
    {
        if (CreateCurrentProjection() is not { } projection)
        {
            return false;
        }

        _viewProjection = projection.Translate(delta);
        DrawGrid();
        InvalidateScene();
        return true;
    }

    internal void FitToLayout()
    {
        _viewProjection = null;
        _zoomFactor = 1d;
        DrawGrid();
        InvalidateScene();
    }

    internal bool SelectItemAt(Point point)
    {
        var item = HitTestItem(point, SnapshotSource?.Latest);
        SetCurrentValue(SelectedItemProperty, item);
        return item is not null;
    }

    internal bool RequestExtendedSelectionAt(Point point, ModifierKeys modifiers)
    {
        var item = HitTestItem(point, SnapshotSource?.Latest);
        if (item is null)
        {
            return false;
        }

        SelectionRequested?.Invoke(
            this,
            new MachineSceneSelectionRequestedEventArgs(item, modifiers));
        return true;
    }

    internal bool RequestSelectionDrag(string itemId, Vector screenDelta)
    {
        if (!IsDesignMode || CreateCurrentProjection() is not { Scale: > 0 } projection)
        {
            return false;
        }

        var item = GetAuthoredItems().FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            itemId,
            StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }
        if (!item.IsSelected)
        {
            SetCurrentValue(SelectedItemProperty, item);
        }

        MoveRequested?.Invoke(
            this,
            new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.Begin, default));
        MoveRequested?.Invoke(
            this,
            new MachineSceneMoveRequestedEventArgs(
                MachineSceneMoveAction.Update,
                new Vector(screenDelta.X / projection.Scale, screenDelta.Y / projection.Scale)));
        MoveRequested?.Invoke(
            this,
            new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.Commit, default));
        return true;
    }

    internal IReadOnlyList<string> RequestMarqueeSelection(Rect bounds, ModifierKeys modifiers)
    {
        if (!IsDesignMode)
        {
            return Array.Empty<string>();
        }

        var selected = GetItemsInMarquee(bounds);
        MarqueeSelectionRequested?.Invoke(
            this,
            new MachineSceneMarqueeSelectionRequestedEventArgs(selected, modifiers));
        return selected.Select(item => item.Id).ToArray();
    }

    internal Point? GetTransformHandleCenter(string itemId, LayoutTransformHandle handle)
    {
        var authoredItems = GetAuthoredItems();
        if (!IsDesignMode || authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var geometry = CreateRenderItems(authoredItems, SnapshotSource?.Latest);
        var item = geometry.SingleOrDefault(candidate =>
            candidate.Item.IsSelected && string.Equals(candidate.Item.Id, itemId, StringComparison.Ordinal));
        if (item is null || geometry.Count(candidate => candidate.Item.IsSelected) != 1)
        {
            return null;
        }
        return GetTransformHandleCenters(item, CreateProjection(geometry))[handle];
    }

    internal bool RequestSelectionTransform(
        string itemId,
        LayoutTransformHandle handle,
        Point targetScreenPoint,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (GetTransformHandleCenter(itemId, handle) is null ||
            CreateCurrentProjection() is not { } projection)
        {
            return false;
        }

        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(
                MachineSceneMoveAction.Begin,
                handle,
                default,
                modifiers));
        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(
                MachineSceneMoveAction.Update,
                handle,
                projection.ToWorld(targetScreenPoint),
                modifiers));
        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(
                MachineSceneMoveAction.Commit,
                handle,
                default,
                modifiers));
        return true;
    }

    internal Point? GetSelectionTransformHandleCenter(LayoutTransformHandle handle)
    {
        var authoredItems = GetAuthoredItems();
        if (!IsDesignMode || authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var geometry = CreateRenderItems(authoredItems, SnapshotSource?.Latest);
        var selected = geometry.Where(item => item.Item.IsSelected).ToArray();
        return selected.Length < 2
            ? null
            : GetSelectionTransformHandleCenters(selected, CreateProjection(geometry))[handle];
    }

    internal bool RequestSelectionTransform(
        LayoutTransformHandle handle,
        Point targetScreenPoint,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (GetSelectionTransformHandleCenter(handle) is null ||
            CreateCurrentProjection() is not { } projection)
        {
            return false;
        }

        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(
                MachineSceneMoveAction.Begin,
                handle,
                default,
                modifiers));
        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(
                MachineSceneMoveAction.Update,
                handle,
                projection.ToWorld(targetScreenPoint),
                modifiers));
        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(
                MachineSceneMoveAction.Commit,
                handle,
                default,
                modifiers));
        return true;
    }

    internal Point? GetDropWorldPoint(Point screenPoint) =>
        CreateDropProjection()?.ToWorld(screenPoint);

    internal bool RequestLibraryComponentDrop(LayoutComponentKind kind, Point screenPoint)
    {
        if (!IsDesignMode || CreateDropProjection() is not { } projection)
        {
            return false;
        }

        LibraryComponentDropRequested?.Invoke(
            this,
            new MachineSceneLibraryComponentDropRequestedEventArgs(
                kind,
                projection.ToWorld(screenPoint)));
        return true;
    }

    internal bool ShowLibraryDropPreview(Point screenPoint)
    {
        if (!IsDesignMode || CreateDropProjection() is null)
        {
            return false;
        }

        _libraryDropPreviewPoint = screenPoint;
        InvalidateScene();
        return true;
    }

    internal Rect? GetItemScreenBounds(string itemId)
    {
        var authoredItems = GetAuthoredItems();
        if (authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var geometry = CreateRenderItems(authoredItems, SnapshotSource?.Latest);
        var item = geometry.FirstOrDefault(candidate => string.Equals(
            candidate.Item.Id,
            itemId,
            StringComparison.Ordinal));
        var projection = CreateProjection(geometry);
        return item is null ? null : GetScreenBounds(item, projection);
    }

    internal Point? GetItemCenter(string itemId)
    {
        var authoredItems = GetAuthoredItems();
        if (authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var geometry = CreateRenderItems(authoredItems, SnapshotSource?.Latest);
        var item = geometry.FirstOrDefault(candidate =>
            string.Equals(candidate.Item.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return null;
        }

        var projection = CreateProjection(geometry);
        return projection.ToScreen(item.X, item.Y);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _textCache.Clear();
        DrawGrid();
        InvalidateScene();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (MachineSceneViewport)d;
        viewport.ResetViewProjection();
        viewport.ObserveCollection(e.NewValue as INotifyCollectionChanged);
        viewport.ObserveItems();
        viewport.InvalidateScene();
    }

    private static void OnSnapshotSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (MachineSceneViewport)d;
        if (viewport.IsLoaded)
        {
            viewport.ObserveSnapshotSource(
                e.OldValue as SceneSnapshotStore,
                e.NewValue as SceneSnapshotStore);
        }

        viewport.InvalidateScene();
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MachineSceneViewport)d).InvalidateScene();
    }

    private static void OnIsDesignModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MachineSceneViewport)d).InvalidateScene();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _resources = SceneRenderResources.Create(this);
        _textCache.Clear();
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;
        ObserveCollection(ItemsSource as INotifyCollectionChanged);
        ObserveItems();
        ObserveSnapshotSource(null, SnapshotSource);
        DrawGrid();
        InvalidateScene();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        ObserveSnapshotSource(SnapshotSource, null);
        ObserveCollection(null);
        ClearObservedItems();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResetViewProjection();
        DrawGrid();
        InvalidateScene();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _textCache.Clear();
        InvalidateScene();
    }

    private void ObserveSnapshotSource(SceneSnapshotStore? oldSource, SceneSnapshotStore? newSource)
    {
        if (oldSource is not null)
        {
            oldSource.SnapshotPublished -= OnSnapshotPublished;
        }

        if (newSource is not null)
        {
            newSource.SnapshotPublished += OnSnapshotPublished;
        }
    }

    private void OnSnapshotPublished(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Interlocked.Exchange(ref _snapshotRenderQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    Interlocked.Exchange(ref _snapshotRenderQueued, 0);
                    if (!IsLoaded)
                    {
                        return;
                    }

                    var snapshot = SnapshotSource?.Latest;
                    if (!ReferenceEquals(snapshot, _lastSnapshot))
                    {
                        _lastSnapshot = snapshot;
                        RenderScene(snapshot);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Render);
        }
        catch (InvalidOperationException) when (
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _snapshotRenderQueued, 0);
        }
    }

    private void ObserveCollection(INotifyCollectionChanged? collection)
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnCollectionChanged;
        }

        _observedCollection = collection;
        if (_observedCollection is not null && IsLoaded)
        {
            _observedCollection.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResetViewProjection();
        ObserveItems();
        InvalidateScene();
    }

    private void ObserveItems()
    {
        ClearObservedItems();
        if (!IsLoaded)
        {
            return;
        }

        foreach (var item in ItemsSource?.OfType<INotifyPropertyChanged>() ?? Array.Empty<INotifyPropertyChanged>())
        {
            if (_observedItems.Add(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    private void ClearObservedItems()
    {
        foreach (var item in _observedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _observedItems.Clear();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateScene();

    private void InvalidateScene()
    {
        _lastSnapshot = null;
        if (IsLoaded)
        {
            RenderScene(SnapshotSource?.Latest);
        }
    }

    private void DrawGrid()
    {
        if (_resources is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        using var context = _gridVisual.RenderOpen();
        var projection = CreateCurrentProjection();
        var minorSpacing = projection is { Scale: > 0 }
            ? Math.Clamp(40d * projection.Value.Scale, 16d, 120d)
            : 40d;
        var origin = projection?.ToScreen(0, 0) ?? default;
        var startX = PositiveModulo(origin.X, minorSpacing);
        var startY = PositiveModulo(origin.Y, minorSpacing);
        var majorSpacing = minorSpacing * 5;
        for (double x = startX; x <= ActualWidth; x += minorSpacing)
        {
            var pen = Math.Abs(PositiveModulo(x - origin.X, majorSpacing)) < 0.1
                ? _resources.MajorGridPen
                : _resources.GridPen;
            context.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        }

        for (double y = startY; y <= ActualHeight; y += minorSpacing)
        {
            var pen = Math.Abs(PositiveModulo(y - origin.Y, majorSpacing)) < 0.1
                ? _resources.MajorGridPen
                : _resources.GridPen;
            context.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
        }
    }

    private void RenderScene(SimulationSnapshot? snapshot)
    {
        if (_resources is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        LastFormattedTextPixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        LastRenderedGripperValue = null;
        LastRenderedGripperText = null;
        LastRenderedWorkpiece = null;
        LastRenderedWorkpieceText = null;
        LastRenderedTransferOwnershipState = null;
        LastRenderedTransferOwnershipText = null;
        var items = ItemsSource?.ToArray() ?? Array.Empty<LayoutItem>();
        using var context = _sceneVisual.RenderOpen();
        if (items.Length == 0)
        {
            DrawCenteredText(context, "Load a machine project to begin", 12, _resources.TextSecondary);
            DrawLibraryDropPreview(context);
            return;
        }

        var authoredItems = items
            .Where(item => item.Component is not null)
            .OrderBy(item => item.ZIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (authoredItems.Length > 0)
        {
            DrawAuthoredLayout(context, authoredItems, snapshot);
            if (_marqueeBounds is { } marquee)
            {
                context.DrawRectangle(_resources.MarqueeFill, _resources.MarqueePen, marquee);
            }
            DrawLibraryDropPreview(context);
            return;
        }

        var axisItems = items.Where(item => item.Kind == LayoutItemKind.Axis).ToArray();
        var deviceItems = items.Where(item => item.Kind == LayoutItemKind.Device).ToArray();
        var railMaximum = Math.Max(0, ActualHeight - 90);
        var railMinimum = Math.Min(160, railMaximum);
        var railY = Math.Clamp(ActualHeight * 0.62, railMinimum, railMaximum);
        var railLeft = 72d;
        var railRight = Math.Max(railLeft + 160, ActualWidth - 72);
        var (worldMinimum, worldMaximum) = ResolveWorldRange(axisItems, deviceItems);
        IReadOnlyDictionary<LayoutItem, double> axisRailYs = CreateLegacyAxisRailYs(axisItems, railY);
        var deviceRailY = axisRailYs.Count == 0 ? railY : axisRailYs.Values.Min();

        foreach (var axisRailY in axisRailYs.Values)
        {
            context.DrawLine(_resources.RailPen, new Point(railLeft, axisRailY), new Point(railRight, axisRailY));
        }
        if (axisRailYs.Count > 0)
        {
            DrawRailTicks(context, railLeft, railRight, axisRailYs.Values.Max(), worldMinimum, worldMaximum);
        }

        foreach (var device in deviceItems)
        {
            DrawDevice(context, device, railLeft, railRight, deviceRailY, worldMinimum, worldMaximum);
        }

        foreach (var axis in axisItems)
        {
            DrawAxis(context, axis, snapshot, railLeft, railRight, axisRailYs[axis], worldMinimum, worldMaximum);
        }
        if (!IsDesignMode && snapshot is not null && axisRailYs.Count > 0)
        {
            var workAreaBottom = Math.Max(96, axisRailYs.Values.Min() - 38);
            var workAreaTop = Math.Max(
                56,
                workAreaBottom - Math.Clamp(ActualHeight * 0.28, 120, 240));
            DrawPickPlaceWorkpieces(
                context,
                snapshot,
                railLeft,
                railRight,
                workAreaTop,
                workAreaBottom,
                worldMinimum,
                worldMaximum);
        }
        DrawLibraryDropPreview(context);
    }

    private void DrawLibraryDropPreview(DrawingContext context)
    {
        if (_libraryDropPreviewPoint is not { } point)
        {
            return;
        }

        context.DrawEllipse(_resources!.MarqueeFill, _resources.MarqueePen, point, 13, 13);
        context.DrawLine(_resources.SelectionPen, point + new Vector(-8, 0), point + new Vector(8, 0));
        context.DrawLine(_resources.SelectionPen, point + new Vector(0, -8), point + new Vector(0, 8));
    }

    private void DrawAuthoredLayout(
        DrawingContext context,
        IReadOnlyList<LayoutItem> items,
        SimulationSnapshot? snapshot)
    {
        var geometry = CreateRenderItems(items, snapshot);
        var projection = _gestureProjection ?? CreateProjection(geometry);
        var compactLabels = projection.Scale < 1.15d;
        var showRuntimeState = !IsDesignMode && snapshot is not null;

        foreach (var renderItem in geometry)
        {
            var center = projection.ToScreen(renderItem.X, renderItem.Y);
            var width = Math.Max(8, renderItem.Width * projection.Scale);
            var height = Math.Max(8, renderItem.Height * projection.Scale);
            var bounds = new Rect(center.X - (width / 2), center.Y - (height / 2), width, height);
            var selected = renderItem.Item.IsSelected;

            context.PushTransform(new RotateTransform(renderItem.RotationDegrees, center.X, center.Y));
            switch (renderItem.Item.Kind)
            {
                case LayoutItemKind.MachineFrame:
                    context.DrawRoundedRectangle(
                        _resources!.FrameFill,
                        selected ? _resources.SelectionPen : _resources.FramePen,
                        bounds,
                        8,
                        8);
                    DrawEquipmentImage(context, LayoutItemKind.MachineFrame, bounds, 0.58);
                    break;

                case LayoutItemKind.LinearStage:
                    context.DrawRoundedRectangle(
                        _resources!.AxisFill,
                        selected ? _resources.SelectionPen : _resources.AxisPen,
                        bounds,
                        5,
                        5);
                    DrawEquipmentImage(context, LayoutItemKind.LinearStage, bounds, 0.94);
                    var axis = snapshot?.Axes.FirstOrDefault(item =>
                        string.Equals(item.Id, renderItem.Item.BehaviorBindingId, StringComparison.Ordinal));
                    if (showRuntimeState && axis is not null)
                    {
                        var stageState = compactLabels
                            ? $"{axis.Position:F0} {axis.State.ToString().ToUpperInvariant()}"
                            : $"{axis.Position:F1} mm  {axis.State.ToString().ToUpperInvariant()}";
                        DrawStatusBadge(
                            context,
                            stageState,
                            bounds.Left + 4,
                            bounds.Bottom - 17,
                            9,
                            _resources.TextPrimary);
                    }
                    break;

                case LayoutItemKind.RotaryStage:
                    context.DrawEllipse(
                        _resources!.AxisFill,
                        selected ? _resources.SelectionPen : _resources.AxisPen,
                        center,
                        bounds.Width / 2,
                        bounds.Height / 2);
                    context.DrawEllipse(
                        _resources.DeviceFill,
                        _resources.StructurePen,
                        center,
                        Math.Max(4, bounds.Width * 0.16),
                        Math.Max(4, bounds.Height * 0.16));
                    context.DrawLine(
                        _resources.SelectionPen,
                        new Point(center.X, center.Y - (bounds.Height * 0.16)),
                        new Point(center.X, bounds.Top + 5));
                    var rotaryAxis = snapshot?.Axes.FirstOrDefault(item =>
                        string.Equals(item.Id, renderItem.Item.BehaviorBindingId, StringComparison.Ordinal));
                    if (showRuntimeState && rotaryAxis is not null)
                    {
                        var stageState = compactLabels
                            ? $"{rotaryAxis.Position:F0}° {rotaryAxis.State.ToString().ToUpperInvariant()}"
                            : $"{rotaryAxis.Position:F1} deg  {rotaryAxis.State.ToString().ToUpperInvariant()}";
                        DrawStatusBadge(
                            context,
                            stageState,
                            bounds.Left + 4,
                            bounds.Bottom - 17,
                            9,
                            _resources.TextPrimary);
                    }
                    break;

                case LayoutItemKind.DigitalSensor:
                    var isDetected = renderItem.IsDetected == true;
                    DrawSensorField(context, bounds, center, isDetected);
                    context.DrawRoundedRectangle(
                        isDetected ? _resources!.SensorOnFill : _resources!.DeviceFill,
                        selected
                            ? _resources.SelectionPen
                            : isDetected ? _resources.SensorOnPen : _resources.VisionPen,
                        bounds,
                        4,
                        4);
                    DrawEquipmentImage(context, LayoutItemKind.DigitalSensor, bounds, 0.96);
                    if (showRuntimeState)
                    {
                        var indicatorCenter = new Point(center.X, bounds.Top + Math.Min(8, bounds.Height * 0.18));
                        var indicatorRadius = Math.Max(5, Math.Min(7, bounds.Width * 0.38));
                        context.DrawEllipse(
                            _resources.DeviceFill,
                            isDetected ? _resources.SensorOnPen : _resources.VisionPen,
                            indicatorCenter,
                            indicatorRadius,
                            indicatorRadius);
                        DrawText(
                            context,
                            isDetected ? "1" : "0",
                            indicatorCenter.X - 3,
                            indicatorCenter.Y - 7,
                            9.5,
                            isDetected ? _resources.SensorOnBrush : _resources.VisionBrush);
                    }
                    break;

                case LayoutItemKind.PneumaticCylinder:
                    var cylinderState = renderItem.CylinderState
                        ?? PneumaticCylinderState.Retracted;
                    var motionProgress = Math.Clamp(renderItem.MotionProgress ?? 0d, 0d, 1d);
                    var cylinderActive = cylinderState == PneumaticCylinderState.Extended;
                    var cylinderFaulted = cylinderState == PneumaticCylinderState.Fault;
                    context.DrawRoundedRectangle(
                        cylinderFaulted
                            ? _resources!.FaultFill
                            : cylinderActive ? _resources!.SensorOnFill : _resources!.DeviceFill,
                        selected
                            ? _resources.SelectionPen
                            : cylinderFaulted
                                ? _resources.FaultPen
                                : cylinderActive ? _resources.SensorOnPen : _resources.AxisPen,
                        bounds,
                        5,
                        5);
                    DrawEquipmentImage(context, LayoutItemKind.PneumaticCylinder, bounds, 0.90);
                    if (showRuntimeState)
                    {
                        var travelStart = bounds.Left + 8;
                        var travelEnd = bounds.Right - 8;
                        var travelY = bounds.Bottom - 7;
                        context.DrawLine(
                            _resources.StructurePen,
                            new Point(travelStart, travelY),
                            new Point(travelEnd, travelY));
                        var marker = new Point(
                            travelStart + ((travelEnd - travelStart) * motionProgress),
                            travelY);
                        context.DrawEllipse(
                            cylinderFaulted ? _resources.FaultBrush : _resources.AccentBrush,
                            null,
                            marker,
                            3.5,
                            3.5);
                        DrawStatusBadge(
                            context,
                            cylinderState.ToString().ToUpperInvariant(),
                            bounds.Left + 4,
                            bounds.Top + 3,
                            8.5,
                            cylinderFaulted
                                ? _resources.FaultBrush
                                : cylinderActive ? _resources.SensorOnBrush : _resources.TextPrimary);
                    }
                    break;

                case LayoutItemKind.Conveyor:
                    var conveyorRunning = renderItem.ConveyorRunning == true;
                    var conveyorDirection = renderItem.ConveyorDirection ?? ConveyorDirection.Forward;
                    context.DrawRoundedRectangle(
                        conveyorRunning ? _resources!.AxisFill : _resources!.FrameFill,
                        selected ? _resources.SelectionPen : _resources.AxisPen,
                        bounds,
                        5,
                        5);
                    DrawEquipmentImage(context, LayoutItemKind.Conveyor, bounds, 0.88);
                    var arrowDirection = conveyorDirection == ConveyorDirection.Forward ? 1d : -1d;
                    if (showRuntimeState && conveyorRunning)
                    {
                        for (var arrowIndex = -1; arrowIndex <= 1; arrowIndex++)
                        {
                            var arrowX = center.X + (arrowIndex * bounds.Width * 0.22);
                            context.DrawLine(
                                _resources.AxisPen,
                                new Point(arrowX - (6 * arrowDirection), center.Y - 5),
                                new Point(arrowX, center.Y));
                            context.DrawLine(
                                _resources.AxisPen,
                                new Point(arrowX - (6 * arrowDirection), center.Y + 5),
                                new Point(arrowX, center.Y));
                        }
                    }
                    if (showRuntimeState)
                    {
                        var conveyorState = conveyorRunning
                            ? compactLabels
                                ? conveyorDirection == ConveyorDirection.Forward ? "RUN >" : "RUN <"
                                : $"RUN {conveyorDirection.ToString().ToUpperInvariant()}"
                            : compactLabels ? "STOP" : "STOPPED";
                        DrawStatusBadgeAtRight(
                            context,
                            conveyorState,
                            bounds.Right - 4,
                            bounds.Top + 4,
                            9,
                            conveyorRunning ? _resources.AccentBrush : _resources.TextSecondary);
                    }
                    break;

                case LayoutItemKind.Workpiece:
                    var inspectionState = renderItem.InspectionState
                        ?? WorkpieceInspectionState.Pending;
                    var transferState = renderItem.TransferOwnershipState;
                    var transferFaulted = transferState == WaferHandlerOwnershipState.InterlockFault;
                    var inspectionFailed = inspectionState == WorkpieceInspectionState.Failed;
                    var inspectionPassed = inspectionState == WorkpieceInspectionState.Passed;
                    context.DrawRoundedRectangle(
                        transferFaulted || inspectionFailed ? _resources!.FaultFill : _resources!.DeviceFill,
                        selected
                            ? _resources.SelectionPen
                            : transferFaulted || inspectionFailed
                                ? _resources.FaultPen
                                : transferState == WaferHandlerOwnershipState.Handler
                                    ? _resources.SensorOnPen
                                    : transferState == WaferHandlerOwnershipState.Destination
                                        ? _resources.AxisPen
                                : inspectionPassed ? _resources.SensorOnPen : _resources.VisionPen,
                        bounds,
                        4,
                        4);
                    DrawEquipmentImage(context, LayoutItemKind.Workpiece, bounds, 0.92);
                    if (showRuntimeState)
                    {
                        if (transferState is not null)
                        {
                            var transferCode = transferState switch
                            {
                                WaferHandlerOwnershipState.Source => "SOURCE",
                                WaferHandlerOwnershipState.Handler => "HANDLER",
                                WaferHandlerOwnershipState.Destination => "DEST",
                                _ => "FAULT"
                            };
                            var transferBrush = transferState switch
                            {
                                WaferHandlerOwnershipState.Handler => _resources.SensorOnBrush,
                                WaferHandlerOwnershipState.Destination => _resources.AccentBrush,
                                WaferHandlerOwnershipState.InterlockFault => _resources.FaultBrush,
                                _ => _resources.TextSecondary
                            };
                            LastRenderedTransferOwnershipState = transferState;
                            LastRenderedTransferOwnershipText = transferCode;
                            DrawStatusBadge(
                                context,
                                transferCode,
                                bounds.Left + 3,
                                bounds.Top + 3,
                                8,
                                transferBrush);
                        }
                        var inspectionCode = inspectionState switch
                        {
                            WorkpieceInspectionState.Passed => "PASS",
                            WorkpieceInspectionState.Failed => "FAIL",
                            WorkpieceInspectionState.Skipped => "SKIP",
                            _ => "PEND"
                        };
                        DrawStatusBadge(
                            context,
                            inspectionCode,
                            bounds.Left + 3,
                            bounds.Bottom - 15,
                            8,
                            inspectionFailed
                                ? _resources.FaultBrush
                                : inspectionPassed ? _resources.SensorOnBrush : _resources.TextSecondary);
                    }
                    break;
            }
            context.Pop();
        }

        if (!IsDesignMode)
        {
            return;
        }

        var selectedItems = geometry.Where(item => item.Item.IsSelected).ToArray();
        if (selectedItems is [var transformItem])
        {
            DrawTransformHandles(context, transformItem, projection);
        }
        else if (selectedItems.Length > 1)
        {
            DrawSelectionTransformHandles(context, selectedItems, projection);
        }
    }

    private void DrawTransformHandles(
        DrawingContext context,
        LayoutRenderItem item,
        LayoutProjection projection)
    {
        var handles = GetTransformHandleCenters(item, projection);
        var topCenter = GetRotatedLocalPoint(item, projection, 0d, -item.Height / 2d);
        DrawTransformHandleSet(context, handles, topCenter);
    }

    private void DrawSelectionTransformHandles(
        DrawingContext context,
        IReadOnlyList<LayoutRenderItem> items,
        LayoutProjection projection)
    {
        var bounds = GetSelectionScreenBounds(items, projection);
        context.DrawRectangle(null, _resources!.SelectionPen, bounds);
        var handles = GetSelectionTransformHandleCenters(items, projection);
        DrawTransformHandleSet(
            context,
            handles,
            new Point(bounds.Left + (bounds.Width / 2d), bounds.Top));
    }

    private void DrawTransformHandleSet(
        DrawingContext context,
        IReadOnlyDictionary<LayoutTransformHandle, Point> handles,
        Point topCenter)
    {
        context.DrawLine(
            _resources!.SelectionPen,
            topCenter,
            handles[LayoutTransformHandle.Rotation]);
        foreach (var handle in Enum.GetValues<LayoutTransformHandle>())
        {
            var center = handles[handle];
            if (handle == LayoutTransformHandle.Rotation)
            {
                context.DrawEllipse(_resources.DeviceFill, _resources.SelectionPen, center, 5, 5);
            }
            else
            {
                context.DrawRectangle(
                    _resources.DeviceFill,
                    _resources.SelectionPen,
                    new Rect(center.X - 4, center.Y - 4, 8, 8));
            }
        }
    }

    private LayoutRenderItem[] CreateRenderItems(
        IReadOnlyList<LayoutItem> items,
        SimulationSnapshot? snapshot)
    {
        var runtimeById = !IsDesignMode && snapshot is not null
            ? snapshot.LayoutComponents.ToDictionary(item => item.Id, StringComparer.Ordinal)
            : new Dictionary<string, LayoutComponentSnapshot>(StringComparer.Ordinal);
        return items.Select(item =>
        {
            runtimeById.TryGetValue(item.Id, out var runtime);
            return new LayoutRenderItem(
                item,
                runtime?.X ?? item.CurrentX,
                runtime?.Y ?? item.CurrentY,
                runtime?.Width ?? item.Width,
                runtime?.Height ?? item.Height,
                runtime?.RotationDegrees ?? item.RotationDegrees,
                runtime?.IsDetected,
                runtime?.CylinderState,
                runtime?.MotionProgress,
                runtime?.ConveyorRunning,
                runtime?.ConveyorDirection,
                runtime?.WorkpieceType,
                runtime?.InspectionState,
                runtime?.TransferOwnershipState);
        }).ToArray();
    }

    private LayoutItem? HitTestItem(Point point, SimulationSnapshot? snapshot)
    {
        LayoutItem? authoredItem = HitTestAuthoredItem(point, snapshot);
        return authoredItem ?? HitTestLegacyItem(point, snapshot);
    }

    private LayoutItem? HitTestAuthoredItem(Point point, SimulationSnapshot? snapshot)
    {
        var authoredItems = GetAuthoredItems();
        if (authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var geometry = CreateRenderItems(authoredItems, snapshot);
        var projection = CreateProjection(geometry);
        foreach (var renderItem in geometry.Reverse())
        {
            var center = projection.ToScreen(renderItem.X, renderItem.Y);
            var width = Math.Max(8, renderItem.Width * projection.Scale);
            var height = Math.Max(8, renderItem.Height * projection.Scale);
            var bounds = new Rect(
                center.X - (width / 2),
                center.Y - (height / 2),
                width,
                height);
            bounds.Inflate(3, 3);
            if (bounds.Contains(RotatePoint(point, center, -renderItem.RotationDegrees)))
            {
                return renderItem.Item;
            }
        }

        return null;
    }

    private LayoutItem? HitTestLegacyItem(Point point, SimulationSnapshot? snapshot)
    {
        var items = ItemsSource?.ToArray() ?? Array.Empty<LayoutItem>();
        if (items.Any(item => item.Component is not null) || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var axisItems = items.Where(item => item.Kind == LayoutItemKind.Axis).ToArray();
        var deviceItems = items.Where(item => item.Kind == LayoutItemKind.Device).ToArray();
        var railMaximum = Math.Max(0, ActualHeight - 90);
        var railMinimum = Math.Min(160, railMaximum);
        var railY = Math.Clamp(ActualHeight * 0.62, railMinimum, railMaximum);
        var railLeft = 72d;
        var railRight = Math.Max(railLeft + 160, ActualWidth - 72);
        var (worldMinimum, worldMaximum) = ResolveWorldRange(axisItems, deviceItems);
        IReadOnlyDictionary<LayoutItem, double> axisRailYs = CreateLegacyAxisRailYs(axisItems, railY);

        foreach (var axis in axisItems.Reverse())
        {
            var state = snapshot?.Axes.FirstOrDefault(candidate => candidate.Id == axis.Id);
            var position = state?.Position ?? (axis.Model as VirtualAxisDefinition)?.HomePosition ?? axis.Position.X;
            var x = WorldToScreen(position, railLeft, railRight, worldMinimum, worldMaximum);
            if (new Rect(x - 44, axisRailYs[axis] - 24, 88, 48).Contains(point))
            {
                return axis;
            }
        }

        var deviceRailY = axisRailYs.Count == 0 ? railY : axisRailYs.Values.Min();
        foreach (var device in deviceItems.Reverse())
        {
            var x = WorldToScreen(device.Position.X, railLeft, railRight, worldMinimum, worldMaximum);
            var isCamera = device.Model is DeviceDefinition { Kind: DeviceKind.Camera };
            var y = Math.Max(68, deviceRailY - (isCamera ? 170 : 105));
            if (new Rect(x - 46, y, 92, 42).Contains(point))
            {
                return device;
            }
        }

        return null;
    }

    private LayoutTransformHandle? HitTestTransformHandle(Point point)
    {
        if (!IsDesignMode)
        {
            return null;
        }

        var authoredItems = GetAuthoredItems();
        var geometry = CreateRenderItems(authoredItems, SnapshotSource?.Latest);
        var selected = geometry.Where(item => item.Item.IsSelected).ToArray();
        if (selected.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        var projection = CreateProjection(geometry);
        var handles = selected.Length == 1
            ? GetTransformHandleCenters(selected[0], projection)
            : GetSelectionTransformHandleCenters(selected, projection);
        foreach (var handle in Enum.GetValues<LayoutTransformHandle>().Reverse())
        {
            if ((handles[handle] - point).Length <= 9d)
            {
                return handle;
            }
        }
        return null;
    }

    private static IReadOnlyDictionary<LayoutTransformHandle, Point>
        GetSelectionTransformHandleCenters(
            IReadOnlyList<LayoutRenderItem> items,
            LayoutProjection projection)
    {
        var bounds = GetSelectionScreenBounds(items, projection);
        var topCenter = new Point(bounds.Left + (bounds.Width / 2d), bounds.Top);
        return new Dictionary<LayoutTransformHandle, Point>
        {
            [LayoutTransformHandle.TopLeft] = bounds.TopLeft,
            [LayoutTransformHandle.TopRight] = bounds.TopRight,
            [LayoutTransformHandle.BottomRight] = bounds.BottomRight,
            [LayoutTransformHandle.BottomLeft] = bounds.BottomLeft,
            [LayoutTransformHandle.Rotation] = topCenter + new Vector(0, -24d)
        };
    }

    private static Rect GetSelectionScreenBounds(
        IReadOnlyList<LayoutRenderItem> items,
        LayoutProjection projection)
    {
        var bounds = GetScreenBounds(items[0], projection);
        foreach (var item in items.Skip(1))
        {
            bounds.Union(GetScreenBounds(item, projection));
        }
        return bounds;
    }

    private static IReadOnlyDictionary<LayoutTransformHandle, Point> GetTransformHandleCenters(
        LayoutRenderItem item,
        LayoutProjection projection)
    {
        var topLeft = GetRotatedLocalPoint(item, projection, -item.Width / 2d, -item.Height / 2d);
        var topRight = GetRotatedLocalPoint(item, projection, item.Width / 2d, -item.Height / 2d);
        var bottomRight = GetRotatedLocalPoint(item, projection, item.Width / 2d, item.Height / 2d);
        var bottomLeft = GetRotatedLocalPoint(item, projection, -item.Width / 2d, item.Height / 2d);
        var topCenter = GetRotatedLocalPoint(item, projection, 0d, -item.Height / 2d);
        var rotationRadians = (item.RotationDegrees - 90d) * Math.PI / 180d;
        var outward = new Vector(Math.Cos(rotationRadians), Math.Sin(rotationRadians));
        return new Dictionary<LayoutTransformHandle, Point>
        {
            [LayoutTransformHandle.TopLeft] = topLeft,
            [LayoutTransformHandle.TopRight] = topRight,
            [LayoutTransformHandle.BottomRight] = bottomRight,
            [LayoutTransformHandle.BottomLeft] = bottomLeft,
            [LayoutTransformHandle.Rotation] = topCenter + (outward * 24d)
        };
    }

    private static Point GetRotatedLocalPoint(
        LayoutRenderItem item,
        LayoutProjection projection,
        double localX,
        double localY)
    {
        var center = projection.ToScreen(item.X, item.Y);
        var point = new Point(
            center.X + (localX * projection.Scale),
            center.Y + (localY * projection.Scale));
        return RotatePoint(point, center, item.RotationDegrees);
    }

    private void UpdateMove(Point point)
    {
        if (_gestureProjection is not { Scale: > 0 } projection)
        {
            return;
        }

        MoveRequested?.Invoke(
            this,
            new MachineSceneMoveRequestedEventArgs(
                MachineSceneMoveAction.Update,
                new Vector(
                    (point.X - _gestureStart.X) / projection.Scale,
                    (point.Y - _gestureStart.Y) / projection.Scale)));
    }

    private void RaiseMarqueeSelection(Rect bounds, ModifierKeys modifiers)
    {
        var items = GetItemsInMarquee(bounds);
        MarqueeSelectionRequested?.Invoke(
            this,
            new MachineSceneMarqueeSelectionRequestedEventArgs(items, modifiers));
    }

    private LayoutItem[] GetItemsInMarquee(Rect bounds)
    {
        var authoredItems = GetAuthoredItems();
        if (authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return Array.Empty<LayoutItem>();
        }

        var geometry = CreateRenderItems(authoredItems, SnapshotSource?.Latest);
        var projection = CreateProjection(geometry);
        return geometry
            .Where(item => bounds.Contains(GetScreenBounds(item, projection)))
            .Select(item => item.Item)
            .ToArray();
    }

    private LayoutProjection? CreateCurrentProjection()
    {
        var authoredItems = GetAuthoredItems();
        if (authoredItems.Length == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }
        return CreateProjection(CreateRenderItems(authoredItems, SnapshotSource?.Latest));
    }

    private LayoutProjection? CreateDropProjection()
    {
        if (ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        return CreateCurrentProjection()
            ?? _viewProjection
            ?? LayoutProjection.CreateEmpty(ActualWidth, ActualHeight);
    }

    private void UpdateLibraryDrag(DragEventArgs e)
    {
        var item = GetLibraryItem(e.Data);
        if (!IsDesignMode || item is null || !ShowLibraryDropPreview(e.GetPosition(this)))
        {
            e.Effects = DragDropEffects.None;
            ClearLibraryDropPreview();
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void ClearLibraryDropPreview()
    {
        if (_libraryDropPreviewPoint is null)
        {
            return;
        }
        _libraryDropPreviewPoint = null;
        InvalidateScene();
    }

    private static ComponentLibraryItem? GetLibraryItem(IDataObject data) =>
        data.GetDataPresent(typeof(ComponentLibraryItem))
            ? data.GetData(typeof(ComponentLibraryItem)) as ComponentLibraryItem
            : null;

    private LayoutProjection CreateProjection(IReadOnlyList<LayoutRenderItem> geometry) =>
        _viewProjection ??= LayoutProjection.Create(geometry, ActualWidth, ActualHeight);

    private void UpdatePan(Point point)
    {
        if (_gestureProjection is not { } projection)
        {
            return;
        }

        _viewProjection = projection.Translate(point - _gestureStart);
        DrawGrid();
        InvalidateScene();
    }

    private void UpdateTransform(Point point)
    {
        if (_gestureProjection is not { } projection)
        {
            return;
        }
        RaiseTransform(
            MachineSceneMoveAction.Update,
            projection.ToWorld(point),
            Keyboard.Modifiers);
    }

    private void RaiseTransform(
        MachineSceneMoveAction action,
        Point worldPoint,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_transformHandle is not { } handle)
        {
            return;
        }
        TransformRequested?.Invoke(
            this,
            new MachineSceneTransformRequestedEventArgs(action, handle, worldPoint, modifiers));
    }

    private static Cursor GetTransformCursor(LayoutTransformHandle handle) => handle switch
    {
        LayoutTransformHandle.TopLeft or LayoutTransformHandle.BottomRight => Cursors.SizeNWSE,
        LayoutTransformHandle.TopRight or LayoutTransformHandle.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Hand
    };

    private void ResetViewProjection()
    {
        _viewProjection = null;
        _zoomFactor = 1d;
    }

    private static double PositiveModulo(double value, double divisor) =>
        ((value % divisor) + divisor) % divisor;

    private static Rect GetScreenBounds(LayoutRenderItem item, LayoutProjection projection)
    {
        var center = projection.ToScreen(item.X, item.Y);
        var width = Math.Max(8, item.Width * projection.Scale);
        var height = Math.Max(8, item.Height * projection.Scale);
        var radians = item.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var rotatedWidth = (width * cosine) + (height * sine);
        var rotatedHeight = (width * sine) + (height * cosine);
        return new Rect(
            center.X - (rotatedWidth / 2),
            center.Y - (rotatedHeight / 2),
            rotatedWidth,
            rotatedHeight);
    }

    private static Rect NormalizeRect(Point first, Point second) => new(
        new Point(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y)),
        new Point(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y)));

    private void ResetPointerGesture(bool releaseMouse)
    {
        _pointerGesture = PointerGesture.None;
        _pressedItem = null;
        _gestureProjection = null;
        _marqueeBounds = null;
        _transformHandle = null;
        Cursor = null;
        if (releaseMouse && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        InvalidateScene();
    }

    private LayoutItem[] GetAuthoredItems() => ItemsSource?
        .Where(item => item.Component is not null)
        .OrderBy(item => item.ZIndex)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToArray() ?? Array.Empty<LayoutItem>();

    private static Point RotatePoint(Point point, Point center, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var x = point.X - center.X;
        var y = point.Y - center.Y;
        return new Point(
            center.X + (x * cosine) - (y * sine),
            center.Y + (x * sine) + (y * cosine));
    }

    private void DrawEquipmentImage(
        DrawingContext context,
        LayoutItemKind kind,
        Rect bounds,
        double opacity)
    {
        if (_resources is null || !_resources.EquipmentImages.TryGetValue(kind, out var image))
        {
            return;
        }

        var padding = kind == LayoutItemKind.MachineFrame ? 5d : 3d;
        var availableWidth = Math.Max(1, bounds.Width - (padding * 2));
        var availableHeight = Math.Max(1, bounds.Height - (padding * 2));
        var scale = Math.Min(availableWidth / image.Width, availableHeight / image.Height);
        var imageWidth = image.Width * scale;
        var imageHeight = image.Height * scale;
        var destination = new Rect(
            bounds.Left + ((bounds.Width - imageWidth) / 2),
            bounds.Top + ((bounds.Height - imageHeight) / 2),
            imageWidth,
            imageHeight);
        context.PushOpacity(opacity);
        context.DrawImage(image, destination);
        context.Pop();
    }

    private void DrawSensorField(DrawingContext context, Rect bounds, Point center, bool isDetected)
    {
        var field = new StreamGeometry();
        using (var geometry = field.Open())
        {
            geometry.BeginFigure(new Point(bounds.Right, center.Y - 7), true, true);
            geometry.LineTo(new Point(bounds.Right + 22, center.Y - 15), true, false);
            geometry.LineTo(new Point(bounds.Right + 22, center.Y + 15), true, false);
            geometry.LineTo(new Point(bounds.Right, center.Y + 7), true, false);
        }
        field.Freeze();
        context.DrawGeometry(
            isDetected ? _resources!.SensorOnFieldFill : _resources!.VisionFieldFill,
            isDetected ? _resources.SensorOnDashPen : _resources.VisionDashPen,
            field);
    }

    private void DrawStatusBadge(
        DrawingContext context,
        string text,
        double x,
        double y,
        double fontSize,
        Brush brush)
    {
        var formatted = GetText(text, fontSize, brush);
        var badge = new Rect(x, y, formatted.Width + 8, formatted.Height + 2);
        context.DrawRoundedRectangle(_resources!.StatusBadgeFill, null, badge, 3, 3);
        context.DrawText(formatted, new Point(x + 4, y + 1));
    }

    private void DrawStatusBadgeAtRight(
        DrawingContext context,
        string text,
        double right,
        double y,
        double fontSize,
        Brush brush)
    {
        var formatted = GetText(text, fontSize, brush);
        DrawStatusBadge(context, text, right - formatted.Width - 8, y, fontSize, brush);
    }

    private void DrawAxis(
        DrawingContext context,
        LayoutItem item,
        SimulationSnapshot? snapshot,
        double railLeft,
        double railRight,
        double railY,
        double worldMinimum,
        double worldMaximum)
    {
        var state = snapshot?.Axes.FirstOrDefault(axis => axis.Id == item.Id);
        var position = state?.Position ?? (item.Model as VirtualAxisDefinition)?.HomePosition ?? item.Position.X;
        var x = WorldToScreen(position, railLeft, railRight, worldMinimum, worldMaximum);
        var stage = new Rect(x - 44, railY - 24, 88, 48);
        context.DrawRoundedRectangle(
            _resources!.AxisFill,
            item.IsSelected ? _resources.SelectionPen : _resources.AxisPen,
            stage,
            5,
            5);
        context.DrawEllipse(_resources.AccentBrush, null, new Point(x, railY), 5, 5);

        DrawText(context, item.Name, stage.Left + 8, stage.Top + 7, 11.5, _resources.TextPrimary);
        var status = state is null
            ? $"{position:F3} mm"
            : $"{state.Position:F3} mm  ·  {state.State.ToString().ToUpperInvariant()}";
        DrawText(context, status, stage.Left + 8, stage.Top + 26, 10, _resources.TextSecondary);

        if (!IsDesignMode &&
            string.Equals(item.Id, "x", StringComparison.Ordinal) &&
            snapshot?.Signals.FirstOrDefault(signal =>
                string.Equals(signal.Id, GripperSignalId, StringComparison.Ordinal)) is { } gripper)
        {
            LastRenderedGripperValue = gripper.Value;
            LastRenderedGripperText = gripper.Value
                ? OpenVisionLanguageService.T("Scene.GripperClosed")
                : OpenVisionLanguageService.T("Scene.GripperOpen");
            DrawStatusBadge(
                context,
                LastRenderedGripperText,
                stage.Left,
                stage.Bottom + 4,
                9,
                gripper.Value ? _resources.SensorOnBrush : _resources.TextSecondary);
        }
    }

    private void DrawDevice(
        DrawingContext context,
        LayoutItem item,
        double railLeft,
        double railRight,
        double railY,
        double worldMinimum,
        double worldMaximum)
    {
        var x = WorldToScreen(item.Position.X, railLeft, railRight, worldMinimum, worldMaximum);
        var isCamera = item.Model is DeviceDefinition { Kind: DeviceKind.Camera };
        var y = Math.Max(68, railY - (isCamera ? 170 : 105));
        if (isCamera)
        {
            var fieldOfView = new StreamGeometry();
            using (var geometry = fieldOfView.Open())
            {
                geometry.BeginFigure(new Point(x - 18, y + 32), true, true);
                geometry.LineTo(new Point(x - 74, railY - 26), true, false);
                geometry.LineTo(new Point(x + 74, railY - 26), true, false);
                geometry.LineTo(new Point(x + 18, y + 32), true, false);
            }
            fieldOfView.Freeze();
            context.DrawGeometry(_resources!.VisionFieldFill, _resources.VisionDashPen, fieldOfView);
        }

        var body = new Rect(x - 46, y, 92, 42);
        context.DrawRoundedRectangle(_resources!.DeviceFill, _resources.VisionPen, body, 5, 5);
        DrawText(context, item.Name, body.Left + 8, body.Top + 7, 11.5, _resources.TextPrimary);
        DrawText(context, isCamera ? "VIRTUAL CAMERA" : "VIRTUAL DEVICE", body.Left + 8, body.Top + 25, 9.5, _resources.VisionBrush);
    }

    private void DrawPickPlaceWorkpieces(
        DrawingContext context,
        SimulationSnapshot snapshot,
        double left,
        double right,
        double top,
        double bottom,
        double worldMinimum,
        double worldMaximum)
    {
        foreach (var workpiece in snapshot.Workpieces)
        {
            var center = new Point(
                WorldToScreen(workpiece.X, left, right, worldMinimum, worldMaximum),
                WorldToScreen(workpiece.Y, bottom, top, worldMinimum, worldMaximum));
            var bounds = new Rect(center.X - 22, center.Y - 22, 44, 44);
            var pen = workpiece.State switch
            {
                PickPlaceWorkpieceState.Attached => _resources!.SensorOnPen,
                PickPlaceWorkpieceState.Placed => _resources!.AxisPen,
                _ => _resources!.StructurePen
            };
            var brush = workpiece.State == PickPlaceWorkpieceState.Attached
                ? _resources!.SensorOnBrush
                : workpiece.State == PickPlaceWorkpieceState.Placed
                    ? _resources!.AccentBrush
                    : _resources!.TextSecondary;
            var stateText = OpenVisionLanguageService.T(workpiece.State switch
            {
                PickPlaceWorkpieceState.Attached => "Scene.WorkpieceAttached",
                PickPlaceWorkpieceState.Placed => "Scene.WorkpiecePlaced",
                _ => "Scene.WorkpieceAvailable"
            });

            context.DrawRoundedRectangle(_resources!.DeviceFill, pen, bounds, 7, 7);
            DrawEquipmentImage(context, LayoutItemKind.Workpiece, bounds, 0.96);

            LastRenderedWorkpiece = workpiece;
            LastRenderedWorkpieceText = stateText;
            var label = $"{workpiece.Id.ToUpperInvariant()} · {stateText}";
            var formatted = GetText(label, 9, brush);
            var labelX = Math.Clamp(
                center.X - ((formatted.Width + 8) / 2),
                4,
                Math.Max(4, ActualWidth - formatted.Width - 12));
            DrawStatusBadge(context, label, labelX, bounds.Bottom + 4, 9, brush);
        }
    }

    private void DrawRailTicks(
        DrawingContext context,
        double railLeft,
        double railRight,
        double railY,
        double worldMinimum,
        double worldMaximum)
    {
        const int divisions = 6;
        for (var i = 0; i <= divisions; i++)
        {
            var ratio = (double)i / divisions;
            var x = railLeft + ((railRight - railLeft) * ratio);
            context.DrawLine(_resources!.MajorGridPen, new Point(x, railY + 28), new Point(x, railY + 36));
            var value = worldMinimum + ((worldMaximum - worldMinimum) * ratio);
            DrawText(context, value.ToString("F0", CultureInfo.InvariantCulture), x - 8, railY + 40, 9.5, _resources!.TextSecondary);
        }
    }

    private static (double Minimum, double Maximum) ResolveWorldRange(
        IEnumerable<LayoutItem> axes,
        IEnumerable<LayoutItem> devices)
    {
        var axisDefinitions = axes.Select(item => item.Model).OfType<VirtualAxisDefinition>().ToArray();
        var minimum = axisDefinitions.Select(axis => axis.SoftLimitMin ?? 0).DefaultIfEmpty(0).Min();
        var maximum = axisDefinitions.Select(axis => axis.SoftLimitMax ?? 300).DefaultIfEmpty(300).Max();
        foreach (var device in devices)
        {
            minimum = Math.Min(minimum, device.Position.X);
            maximum = Math.Max(maximum, device.Position.X);
        }

        return maximum - minimum < 1 ? (minimum, minimum + 300) : (minimum, maximum);
    }

    private static IReadOnlyDictionary<LayoutItem, double> CreateLegacyAxisRailYs(
        IReadOnlyList<LayoutItem> axes,
        double bottomRailY)
    {
        if (axes.Count == 0)
        {
            return new Dictionary<LayoutItem, double>();
        }

        LayoutItem[] ordered = axes
            .OrderBy(item => item.Position.Y)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 1)
        {
            return new Dictionary<LayoutItem, double> { [ordered[0]] = bottomRailY };
        }

        var spacing = Math.Min(96d, Math.Max(0d, bottomRailY - 110d) / (ordered.Length - 1));
        var topRailY = bottomRailY - (spacing * (ordered.Length - 1));
        return ordered
            .Select((item, index) => (item, RailY: topRailY + (spacing * index)))
            .ToDictionary(pair => pair.item, pair => pair.RailY);
    }

    private static double WorldToScreen(
        double value,
        double screenMinimum,
        double screenMaximum,
        double worldMinimum,
        double worldMaximum)
    {
        var ratio = Math.Clamp((value - worldMinimum) / (worldMaximum - worldMinimum), 0, 1);
        return screenMinimum + ((screenMaximum - screenMinimum) * ratio);
    }

    private enum PointerGesture
    {
        None,
        PendingMove,
        Moving,
        PendingMarquee,
        Marquee,
        Panning,
        Transforming
    }

    private sealed record LayoutRenderItem(
        LayoutItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        double RotationDegrees,
        bool? IsDetected,
        PneumaticCylinderState? CylinderState,
        double? MotionProgress,
        bool? ConveyorRunning,
        ConveyorDirection? ConveyorDirection,
        string? WorkpieceType,
        WorkpieceInspectionState? InspectionState,
        WaferHandlerOwnershipState? TransferOwnershipState);

    private readonly record struct LayoutProjection(
        double MinimumX,
        double MinimumY,
        double Scale,
        double OffsetX,
        double OffsetY)
    {
        private const double Padding = 48;

        public static LayoutProjection Create(
            IReadOnlyList<LayoutRenderItem> items,
            double viewportWidth,
            double viewportHeight)
        {
            var minimumX = items.Min(item => item.X - (item.Width / 2));
            var maximumX = items.Max(item => item.X + (item.Width / 2));
            var minimumY = items.Min(item => item.Y - (item.Height / 2));
            var maximumY = items.Max(item => item.Y + (item.Height / 2));
            var worldWidth = Math.Max(1, maximumX - minimumX);
            var worldHeight = Math.Max(1, maximumY - minimumY);
            var availableWidth = Math.Max(1, viewportWidth - (Padding * 2));
            var availableHeight = Math.Max(1, viewportHeight - (Padding * 2));
            var scale = Math.Min(availableWidth / worldWidth, availableHeight / worldHeight);
            var offsetX = Padding + ((availableWidth - (worldWidth * scale)) / 2);
            var offsetY = Padding + ((availableHeight - (worldHeight * scale)) / 2);
            return new LayoutProjection(minimumX, minimumY, scale, offsetX, offsetY);
        }

        public static LayoutProjection CreateEmpty(double viewportWidth, double viewportHeight) => new(
            0,
            0,
            1,
            viewportWidth / 2,
            viewportHeight / 2);

        public Point ToScreen(double x, double y) =>
            new(OffsetX + ((x - MinimumX) * Scale), OffsetY + ((y - MinimumY) * Scale));

        public Point ToWorld(Point point) => new(
            MinimumX + ((point.X - OffsetX) / Scale),
            MinimumY + ((point.Y - OffsetY) / Scale));

        public LayoutProjection ZoomAt(Point anchor, double factor) => new(
            MinimumX,
            MinimumY,
            Scale * factor,
            anchor.X - ((anchor.X - OffsetX) * factor),
            anchor.Y - ((anchor.Y - OffsetY) * factor));

        public LayoutProjection Translate(Vector delta) => new(
            MinimumX,
            MinimumY,
            Scale,
            OffsetX + delta.X,
            OffsetY + delta.Y);
    }

    private void DrawCenteredText(DrawingContext context, string text, double fontSize, Brush brush)
    {
        var formatted = GetText(text, fontSize, brush);
        context.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, (ActualHeight - formatted.Height) / 2));
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double fontSize, Brush brush)
    {
        context.DrawText(GetText(text, fontSize, brush), new Point(x, y));
    }

    private FormattedText GetText(string text, double fontSize, Brush brush)
    {
        var cacheKey = $"{fontSize:F1}|{brush.GetHashCode()}|{text}";
        if (_textCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        LastFormattedTextPixelsPerDip = formatted.PixelsPerDip;
        _textCache[cacheKey] = formatted;
        return formatted;
    }

    private sealed class SceneRenderResources
    {
        private SceneRenderResources(FrameworkElement owner)
        {
            TextPrimary = ResolveBrush(owner, "Text.Primary", "#F3F6F9");
            TextSecondary = ResolveBrush(owner, "Text.Secondary", "#AAB5C0");
            AccentBrush = ResolveBrush(owner, "Accent.Primary", "#3A8DFF");
            VisionBrush = ResolveBrush(owner, "State.Vision", "#3BC9DB");
            SensorOnBrush = ResolveBrush(owner, "State.Running", "#2CCB78");
            FaultBrush = ResolveBrush(owner, "State.Fault", "#FF5D5D");
            AxisFill = ResolveBrush(owner, "Accent.Soft", "#17375A");
            DeviceFill = ResolveBrush(owner, "Surface.Raised", "#202833");
            var grid = ResolveBrush(owner, "Scene.Grid", "#27313D");
            var gridMajor = ResolveBrush(owner, "Scene.GridMajor", "#344252");
            var border = ResolveBrush(owner, "Border.Default", "#354251");

            VisionFieldFill = VisionBrush.Clone();
            VisionFieldFill.Opacity = 0.10;
            VisionFieldFill.Freeze();

            FrameFill = DeviceFill.Clone();
            FrameFill.Opacity = 0.28;
            FrameFill.Freeze();

            SensorOnFill = SensorOnBrush.Clone();
            SensorOnFill.Opacity = 0.24;
            SensorOnFill.Freeze();

            SensorOnFieldFill = SensorOnBrush.Clone();
            SensorOnFieldFill.Opacity = 0.12;
            SensorOnFieldFill.Freeze();

            FaultFill = FaultBrush.Clone();
            FaultFill.Opacity = 0.18;
            FaultFill.Freeze();

            StatusBadgeFill = DeviceFill.Clone();
            StatusBadgeFill.Opacity = 0.90;
            StatusBadgeFill.Freeze();

            MarqueeFill = AccentBrush.Clone();
            MarqueeFill.Opacity = 0.12;
            MarqueeFill.Freeze();

            EquipmentImages = new Dictionary<LayoutItemKind, ImageSource>
            {
                [LayoutItemKind.MachineFrame] = LoadEquipmentImage(
                    "machine-frame.png",
                    new Int32Rect(102, 175, 1332, 720)),
                [LayoutItemKind.LinearStage] = LoadEquipmentImage(
                    "linear-stage.png",
                    new Int32Rect(79, 255, 1611, 347)),
                [LayoutItemKind.DigitalSensor] = LoadEquipmentImage(
                    "digital-sensor.png",
                    new Int32Rect(347, 146, 312, 1281)),
                [LayoutItemKind.PneumaticCylinder] = LoadEquipmentImage(
                    "pneumatic-cylinder.png",
                    new Int32Rect(26, 363, 1492, 303)),
                [LayoutItemKind.Conveyor] = LoadEquipmentImage(
                    "conveyor.png",
                    new Int32Rect(72, 238, 1630, 445)),
                [LayoutItemKind.Workpiece] = LoadEquipmentImage(
                    "workpiece.png",
                    new Int32Rect(266, 218, 722, 803))
            };

            GridPen = CreatePen(grid, 0.5);
            MajorGridPen = CreatePen(gridMajor, 0.8);
            RailPen = CreatePen(border, 6);
            StructurePen = CreatePen(border, 2);
            AxisPen = CreatePen(AccentBrush, 1.5);
            VisionPen = CreatePen(VisionBrush, 1.4);
            FramePen = CreatePen(border, 1.2, new DashStyle(new[] { 7d, 4d }, 0));
            SensorOnPen = CreatePen(SensorOnBrush, 1.8);
            FaultPen = CreatePen(FaultBrush, 1.8);
            SelectionPen = CreatePen(AccentBrush, 2.5);
            MarqueePen = CreatePen(AccentBrush, 1, new DashStyle(new[] { 4d, 3d }, 0));
            VisionDashPen = CreatePen(VisionBrush, 1, new DashStyle(new[] { 5d, 4d }, 0));
            SensorOnDashPen = CreatePen(SensorOnBrush, 1, new DashStyle(new[] { 5d, 4d }, 0));
        }

        public SolidColorBrush TextPrimary { get; }
        public SolidColorBrush TextSecondary { get; }
        public SolidColorBrush AccentBrush { get; }
        public SolidColorBrush VisionBrush { get; }
        public SolidColorBrush SensorOnBrush { get; }
        public SolidColorBrush FaultBrush { get; }
        public SolidColorBrush AxisFill { get; }
        public SolidColorBrush DeviceFill { get; }
        public SolidColorBrush VisionFieldFill { get; }
        public SolidColorBrush FrameFill { get; }
        public SolidColorBrush SensorOnFill { get; }
        public SolidColorBrush SensorOnFieldFill { get; }
        public SolidColorBrush FaultFill { get; }
        public SolidColorBrush StatusBadgeFill { get; }
        public SolidColorBrush MarqueeFill { get; }
        public IReadOnlyDictionary<LayoutItemKind, ImageSource> EquipmentImages { get; }
        public Pen GridPen { get; }
        public Pen MajorGridPen { get; }
        public Pen RailPen { get; }
        public Pen StructurePen { get; }
        public Pen AxisPen { get; }
        public Pen VisionPen { get; }
        public Pen FramePen { get; }
        public Pen SensorOnPen { get; }
        public Pen FaultPen { get; }
        public Pen SelectionPen { get; }
        public Pen MarqueePen { get; }
        public Pen VisionDashPen { get; }
        public Pen SensorOnDashPen { get; }

        public static SceneRenderResources Create(FrameworkElement owner) => new(owner);

        private static SolidColorBrush ResolveBrush(FrameworkElement owner, string key, string fallback)
        {
            var source = owner.TryFindResource(key) as SolidColorBrush;
            var brush = source?.Clone() ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
            brush.Freeze();
            return brush;
        }

        private static ImageSource LoadEquipmentImage(string fileName, Int32Rect crop)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(
                $"pack://application:,,,/OpenVisionLab.MachineStudio;component/Assets/Equipment/{fileName}",
                UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var cropped = new CroppedBitmap(bitmap, crop);
            cropped.Freeze();
            return cropped;
        }

        private static Pen CreatePen(Brush brush, double thickness, DashStyle? dashStyle = null)
        {
            var pen = new Pen(brush, thickness) { DashStyle = dashStyle ?? DashStyles.Solid };
            pen.Freeze();
            return pen;
        }
    }
}
