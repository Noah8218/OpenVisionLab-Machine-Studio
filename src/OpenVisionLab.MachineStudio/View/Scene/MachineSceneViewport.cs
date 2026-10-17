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
    Cancel,
    MoveBy
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
    private double _gestureElevation;
    private LayoutItem? _pressedItem;
    private ModifierKeys _gestureModifiers;
    private SceneViewportProjection? _gestureProjection;
    private SceneViewportProjection? _viewProjection;
    private Rect? _marqueeBounds;
    private double _zoomFactor = 1d;
    private double _cameraYaw = SceneViewportProjection.DefaultYawDegrees;
    private double _cameraPitch = SceneViewportProjection.DefaultPitchDegrees;
    private LayoutTransformHandle? _transformHandle;
    private Point? _libraryDropPreviewPoint;
    private Rect _labelOverlayBounds = Rect.Empty;
    private Rect _labelHeaderBounds = Rect.Empty;
    private Rect _labelFooterBounds = Rect.Empty;

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

    public static readonly DependencyProperty EquipmentAxesProperty = DependencyProperty.Register(nameof(EquipmentAxes),
        typeof(IEnumerable<VirtualAxisDefinition>), typeof(MachineSceneViewport), new PropertyMetadata(null, OnEquipmentPresentationChanged));
    public static readonly DependencyProperty EquipmentDevicesProperty = DependencyProperty.Register(nameof(EquipmentDevices),
        typeof(IEnumerable<DeviceDefinition>), typeof(MachineSceneViewport), new PropertyMetadata(null, OnEquipmentPresentationChanged));
    public IEnumerable<VirtualAxisDefinition>? EquipmentAxes
    {
        get => (IEnumerable<VirtualAxisDefinition>?)GetValue(EquipmentAxesProperty);
        set => SetValue(EquipmentAxesProperty, value);
    }
    public IEnumerable<DeviceDefinition>? EquipmentDevices
    {
        get => (IEnumerable<DeviceDefinition>?)GetValue(EquipmentDevicesProperty);
        set => SetValue(EquipmentDevicesProperty, value);
    }

    public static readonly DependencyProperty ReferenceUnitFramesProperty = DependencyProperty.Register(nameof(ReferenceUnitFrames),
        typeof(IEnumerable<LayoutItem>), typeof(MachineSceneViewport), new PropertyMetadata(null, (d, _) =>
        {
            var viewport = (MachineSceneViewport)d;
            viewport.ResetViewProjection();
            viewport.InvalidateScene();
        }));
    public IEnumerable<LayoutItem>? ReferenceUnitFrames
    {
        get => (IEnumerable<LayoutItem>?)GetValue(ReferenceUnitFramesProperty);
        set => SetValue(ReferenceUnitFramesProperty, value);
    }

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

    public static readonly DependencyProperty IsPlacementMoveEnabledProperty = DependencyProperty.Register(
        nameof(IsPlacementMoveEnabled), typeof(bool), typeof(MachineSceneViewport), new PropertyMetadata(true, OnIsDesignModeChanged));

    public bool IsPlacementMoveEnabled
    {
        get => (bool)GetValue(IsPlacementMoveEnabledProperty);
        set => SetValue(IsPlacementMoveEnabledProperty, value);
    }

    public static readonly DependencyProperty IsObliqueViewProperty =
        DependencyProperty.Register(
            nameof(IsObliqueView),
            typeof(bool),
            typeof(MachineSceneViewport),
            new PropertyMetadata(true, OnIsObliqueViewChanged));

    public static readonly DependencyProperty UseEquipmentPresentationProperty = DependencyProperty.Register(
        nameof(UseEquipmentPresentation), typeof(bool), typeof(MachineSceneViewport), new PropertyMetadata(false, OnEquipmentPresentationChanged));
    public static readonly DependencyProperty LabelHeaderElementProperty = DependencyProperty.Register(
        nameof(LabelHeaderElement), typeof(FrameworkElement), typeof(MachineSceneViewport), new PropertyMetadata(null, OnEquipmentPresentationChanged));
    public static readonly DependencyProperty LabelFooterElementProperty = DependencyProperty.Register(
        nameof(LabelFooterElement), typeof(FrameworkElement), typeof(MachineSceneViewport), new PropertyMetadata(null, OnEquipmentPresentationChanged));

    public bool UseEquipmentPresentation
    {
        get => (bool)GetValue(UseEquipmentPresentationProperty);
        set => SetValue(UseEquipmentPresentationProperty, value);
    }
    public FrameworkElement? LabelHeaderElement
    {
        get => (FrameworkElement?)GetValue(LabelHeaderElementProperty);
        set => SetValue(LabelHeaderElementProperty, value);
    }
    public FrameworkElement? LabelFooterElement
    {
        get => (FrameworkElement?)GetValue(LabelFooterElementProperty);
        set => SetValue(LabelFooterElementProperty, value);
    }

    private static void OnEquipmentPresentationChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (MachineSceneViewport)element;
        viewport.OnLabelOverlayLayoutUpdated(null, EventArgs.Empty);
        viewport.DrawGrid();
        viewport.InvalidateScene();
    }

    public static readonly DependencyProperty LabelOverlayElementProperty = DependencyProperty.RegisterAttached(
        "LabelOverlayElement", typeof(FrameworkElement), typeof(MachineSceneViewport),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnLabelOverlayElementChanged));

    public static FrameworkElement? GetLabelOverlayElement(DependencyObject element) =>
        (FrameworkElement?)element.GetValue(LabelOverlayElementProperty);

    public static void SetLabelOverlayElement(DependencyObject element, FrameworkElement? value) =>
        element.SetValue(LabelOverlayElementProperty, value);

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

    public bool IsObliqueView
    {
        get => (bool)GetValue(IsObliqueViewProperty);
        set => SetValue(IsObliqueViewProperty, value);
    }

    internal double LastFormattedTextPixelsPerDip { get; private set; }
    internal Rect? LastRenderedSelectedLabelBounds { get; private set; }
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
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right) ||
            _pointerGesture != PointerGesture.None ||
            CreateCurrentProjection() is not { } projection ||
            e.ChangedButton == MouseButton.Right && !IsObliqueView)
        {
            return;
        }

        Focus();
        _gestureStart = e.GetPosition(this);
        _gestureProjection = projection;
        _pointerGesture = e.ChangedButton == MouseButton.Right ? PointerGesture.Orbiting : PointerGesture.Panning;
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
            _gestureElevation = GetSelectionReferenceElevation();
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
        if (!IsDesignMode || CreateCurrentProjection() is { CanMapToWorld: false })
        {
            if (IsObliqueView && CreateCurrentProjection() is { } camera)
            {
                _gestureStart = point;
                _gestureProjection = camera;
                _pressedItem = item;
                _pointerGesture = PointerGesture.PendingOrbit;
                Cursor = Cursors.ScrollAll;
                CaptureMouse();
            }
            else SetCurrentValue(SelectedItemProperty, item);
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

        if (item is not null && !IsPlacementMoveEnabled)
        {
            e.Handled = true;
            return;
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
        if (_pointerGesture == PointerGesture.PendingOrbit)
        {
            var delta = e.GetPosition(this) - _gestureStart;
            if (e.LeftButton != MouseButtonState.Pressed
                || Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pointerGesture = PointerGesture.Orbiting;
        }
        if (_pointerGesture == PointerGesture.Orbiting)
        {
            if (e.RightButton == MouseButtonState.Pressed || e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateOrbit(e.GetPosition(this));
                e.Handled = true;
            }
            return;
        }
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
                    projection.ToWorldDelta(point - _gestureStart)));
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
            case PointerGesture.Orbiting:
                UpdateOrbit(point);
                break;
            case PointerGesture.PendingOrbit:
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
        if (e.ChangedButton == MouseButton.Right && _pointerGesture == PointerGesture.Orbiting)
        {
            UpdateOrbit(e.GetPosition(this));
            ResetPointerGesture(releaseMouse: true);
            e.Handled = true;
            return;
        }
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
        if (!e.Handled && IsKeyboardFocused && _pointerGesture == PointerGesture.None)
        {
            var cameraKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.Modifiers == ModifierKeys.Alt && OrbitBy(cameraKey switch
            {
                Key.Left => new Vector(-60, 0), Key.Right => new Vector(60, 0),
                Key.Up => new Vector(0, -40), Key.Down => new Vector(0, 40), _ => default
            }))
            {
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && PanBy(cameraKey switch
            {
                Key.Left => new Vector(-30, 0), Key.Right => new Vector(30, 0),
                Key.Up => new Vector(0, -30), Key.Down => new Vector(0, 30), _ => default
            }))
            {
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.F or Key.Home)
            {
                if (e.Key == Key.Home) ResetCamera(); else FitToLayout();
                e.Handled = true;
                return;
            }
        }
        if (!e.Handled && RequestKeyboardMove(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
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
        else if (_pointerGesture is PointerGesture.Panning or PointerGesture.Orbiting)
        {
            _viewProjection = _gestureProjection;
            _cameraYaw = _viewProjection?.YawDegrees ?? _cameraYaw;
            _cameraPitch = _viewProjection?.PitchDegrees ?? _cameraPitch;
            DrawGrid();
        }
        ResetPointerGesture(releaseMouse: true);
        e.Handled = true;
    }

    internal bool RequestKeyboardMove(Key key, ModifierKeys modifiers)
    {
        if (!IsKeyboardFocused || !IsDesignMode || !IsPlacementMoveEnabled || _pointerGesture != PointerGesture.None ||
            SelectedItem?.IsSelected != true || SelectedItem.Component is null ||
            (modifiers & ~ModifierKeys.Shift) != ModifierKeys.None)
        {
            return false;
        }

        var step = modifiers.HasFlag(ModifierKeys.Shift) ? 10d : 1d;
        var delta = key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default
        };
        if (delta == default) return false;
        MoveRequested?.Invoke(this, new MachineSceneMoveRequestedEventArgs(MachineSceneMoveAction.MoveBy, delta));
        return true;
    }

    internal double ZoomFactor => _zoomFactor;
    internal double ViewYawDegrees => CreateCurrentProjection()?.YawDegrees ?? SceneViewportProjection.DefaultYawDegrees;
    internal double ViewPitchDegrees => CreateCurrentProjection()?.PitchDegrees ?? SceneViewportProjection.DefaultPitchDegrees;

    internal bool OrbitBy(Vector delta)
    {
        if (!IsObliqueView || delta == default || !double.IsFinite(delta.X) || !double.IsFinite(delta.Y) || CreateCurrentProjection() is not { } projection) return false;
        _viewProjection = OrbitProjection(projection, delta);
        _cameraYaw = _viewProjection.Value.YawDegrees;
        _cameraPitch = _viewProjection.Value.PitchDegrees;
        DrawGrid();
        InvalidateScene();
        return true;
    }

    internal void ResetCamera()
    {
        _cameraYaw = SceneViewportProjection.DefaultYawDegrees;
        _cameraPitch = SceneViewportProjection.DefaultPitchDegrees;
        _viewProjection = null;
        FitToLayout();
    }

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

    internal bool ZoomAtCenter(int wheelDelta) =>
        ZoomAt(new Point(ActualWidth / 2d, ActualHeight / 2d), wheelDelta);

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
        if (!IsDesignMode || CreateCurrentProjection() is not { Scale: > 0, CanMapToWorld: true } projection)
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
                projection.ToWorldDelta(screenDelta)));
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
            CreateCurrentProjection() is not { CanMapToWorld: true } projection)
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
                    projection.ToWorld(targetScreenPoint, GetSelectionReferenceElevation()),
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
            CreateCurrentProjection() is not { CanMapToWorld: true } projection)
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
                    projection.ToWorld(targetScreenPoint, GetSelectionReferenceElevation()),
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
        CreateDropProjection() is { CanMapToWorld: true } projection ? projection.ToWorld(screenPoint) : null;

    internal bool RequestLibraryComponentDrop(LayoutComponentKind kind, Point screenPoint)
    {
        if (!IsDesignMode || CreateDropProjection() is not { CanMapToWorld: true } projection)
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
        if (!IsDesignMode || CreateDropProjection() is not { CanMapToWorld: true })
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
        return projection.ToScreen(
            item.X,
            item.Y,
            projection.IsObliqueView ? item.BaseElevation + item.VerticalHeight : 0);
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

    private static void OnIsObliqueViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (MachineSceneViewport)d;
        viewport.ResetViewProjection();
        viewport.DrawGrid();
        viewport.InvalidateScene();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _resources = SceneRenderResources.Create(this);
        _textCache.Clear();
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;
        LayoutUpdated += OnLabelOverlayLayoutUpdated;
        ObserveCollection(ItemsSource as INotifyCollectionChanged);
        ObserveItems();
        ObserveSnapshotSource(null, SnapshotSource);
        OnLabelOverlayLayoutUpdated(null, EventArgs.Empty);
        DrawGrid();
        InvalidateScene();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLabelOverlayLayoutUpdated;
        _labelOverlayBounds = Rect.Empty;
        _labelHeaderBounds = Rect.Empty;
        _labelFooterBounds = Rect.Empty;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        ObserveSnapshotSource(SnapshotSource, null);
        ObserveCollection(null);
        ClearObservedItems();
    }

    private void OnLabelOverlayLayoutUpdated(object? sender, EventArgs e)
    {
        var bounds = GetVisibleOverlayBounds(GetLabelOverlayElement(this));
        var header = UseEquipmentPresentation ? GetVisibleOverlayBounds(LabelHeaderElement) : Rect.Empty;
        var footer = UseEquipmentPresentation ? GetVisibleOverlayBounds(LabelFooterElement) : Rect.Empty;
        if (bounds == _labelOverlayBounds && header == _labelHeaderBounds && footer == _labelFooterBounds) return;
        _labelOverlayBounds = bounds;
        _labelHeaderBounds = header;
        _labelFooterBounds = footer;
        InvalidateScene();
    }

    private Rect GetVisibleOverlayBounds(FrameworkElement? overlay)
    {
        if (overlay is not { IsVisible: true } || PresentationSource.FromVisual(this) is not { } source ||
            !ReferenceEquals(source, PresentationSource.FromVisual(overlay))) return Rect.Empty;
        var bounds = overlay.TransformToVisual(this).TransformBounds(new Rect(overlay.RenderSize));
        bounds.Intersect(new Rect(RenderSize));
        return bounds;
    }

    private static void OnLabelOverlayElementChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is MachineSceneViewport viewport) viewport.OnLabelOverlayLayoutUpdated(null, EventArgs.Empty);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResetViewProjection();
        DrawGrid();
        InvalidateScene();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLanguageChanged(sender, e)));
            return;
        }
        if (!IsLoaded) return;
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
            if (UseEquipmentPresentation) DrawGrid();
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
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (IsObliqueView)
        {
            if (UseEquipmentPresentation)
            {
                // Unit floors are drawn with their equipment geometry below.
                return;
            }
            for (double x = 0; x <= ActualWidth; x += 32d)
            {
                context.DrawLine(_resources.ObliqueGridPen, new Point(x, 0), new Point(x, ActualHeight));
            }

            for (double y = 0; y <= ActualHeight; y += 32d)
            {
                context.DrawLine(_resources.ObliqueGridPen, new Point(0, y), new Point(ActualWidth, y));
            }
            return;
        }

        if (UseEquipmentPresentation) return;
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

    private void DrawEquipmentFloorGrid(DrawingContext context)
    {
        if (CreateCurrentProjection() is not { } projection) return;
        var corners = CreateRenderItems((ReferenceUnitFrames ?? []).Concat(GetAuthoredItems()).ToArray(), null)
            .SelectMany(item => GetSceneGeometry(item).GetFootprintCorners()).ToArray();
        if (corners.Length == 0) return;
        var spacing = Math.Max(40, Math.Max(corners.Max(point => point.X) - corners.Min(point => point.X),
            corners.Max(point => point.Y) - corners.Min(point => point.Y)) / 48);
        var left = Math.Floor(corners.Min(point => point.X) / spacing) * spacing - spacing;
        var right = Math.Ceiling(corners.Max(point => point.X) / spacing) * spacing + spacing;
        var top = Math.Floor(corners.Min(point => point.Y) / spacing) * spacing - spacing;
        var bottom = Math.Ceiling(corners.Max(point => point.Y) / spacing) * spacing + spacing;
        context.PushOpacity(0.35);
        for (var x = left; x <= right; x += spacing)
            context.DrawLine(_resources!.ObliqueGridPen, projection.ToScreen(x, top), projection.ToScreen(x, bottom));
        for (var y = top; y <= bottom; y += spacing)
            context.DrawLine(_resources!.ObliqueGridPen, projection.ToScreen(left, y), projection.ToScreen(right, y));
        context.Pop();
    }

    private void RenderScene(SimulationSnapshot? snapshot)
    {
        if (_resources is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        LastFormattedTextPixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        LastRenderedSelectedLabelBounds = null;
        LastRenderedGripperValue = null;
        LastRenderedGripperText = null;
        LastRenderedWorkpiece = null;
        LastRenderedWorkpieceText = null;
        LastRenderedTransferOwnershipState = null;
        LastRenderedTransferOwnershipText = null;
        var items = ItemsSource?.ToArray() ?? Array.Empty<LayoutItem>();
        using var context = _sceneVisual.RenderOpen();
        if (items.Length == 0 && !(ReferenceUnitFrames?.Any() ?? false))
        {
            DrawLibraryDropPreview(context);
            return;
        }

        var authoredItems = items
            .Where(item => item.Component is not null)
            .OrderBy(item => item.ZIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (authoredItems.Length > 0 || ReferenceUnitFrames?.Any() == true)
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
        if (_resources is null) return;

        var authoredGeometry = CreateRenderItems((ReferenceUnitFrames ?? []).Concat(items).ToArray(), snapshot);
        var geometry = authoredGeometry.Concat(CreateUnitWorkpieceVisuals(authoredGeometry, snapshot)).ToArray();
        var projection = _pointerGesture is PointerGesture.Panning or PointerGesture.Orbiting
            ? CreateProjection(geometry) : _gestureProjection ?? CreateProjection(geometry);
        if (UseEquipmentPresentation)
        {
            DrawEquipmentLayout(context, geometry, snapshot, projection);
            return;
        }
        if (IsObliqueView)
        {
            DrawObliqueLayout(context, geometry, snapshot, projection);
            return;
        }

        var compactLabels = projection.Scale < 1.15d;
        var showRuntimeState = !IsDesignMode && snapshot is not null;

        foreach (var renderItem in geometry)
        {
            var center = projection.ToScreen(renderItem.X, renderItem.Y);
            var width = Math.Max(8, renderItem.Width * projection.Scale);
            var height = Math.Max(8, renderItem.Height * projection.Scale);
            var bounds = new Rect(center.X - (width / 2), center.Y - (height / 2), width, height);
            var selected = renderItem.Item.IsSelected;
            var (componentFill, componentOutline) = GetComponentSurfaceStyle(renderItem);

            if (HasCompositeBody(renderItem.Item.Kind)) DrawSchematicBody(context, renderItem, projection);
            context.PushTransform(new RotateTransform(renderItem.RotationDegrees, center.X, center.Y));
            switch (renderItem.Item.Kind)
            {
                case LayoutItemKind.MachineFrame:
                    break;

                case LayoutItemKind.LinearStage:
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
                        componentFill,
                        componentOutline,
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

                case LayoutItemKind.DigitalIo:
                    context.DrawRoundedRectangle(componentFill, componentOutline, bounds, 2, 2);
                    DrawText(context, renderItem.IsDetected == true ? "ON" : "OFF", bounds.Left + 3, bounds.Top + 3, 10, _resources!.TextPrimary);
                    break;

                case LayoutItemKind.DigitalSensor:
                    var isDetected = renderItem.IsDetected == true;
                    DrawSensorField(context, bounds, center, isDetected);
                    context.DrawRoundedRectangle(
                        componentFill,
                        componentOutline,
                        bounds,
                        4,
                        4);
                    context.DrawEllipse(_resources.VisionBrush, _resources.VisionPen, center, 3.5, 3.5);
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

                case LayoutItemKind.FeedPosition:
                case LayoutItemKind.ExitPosition:
                    context.DrawRoundedRectangle(_resources!.ConveyorComponentFill, selected ? _resources.SelectionPen : _resources.AxisPen, bounds, 3, 3);
                    DrawStatusBadge(context, renderItem.Item.Kind == LayoutItemKind.FeedPosition ? "⇥" : "⇤", bounds.Left + 4, bounds.Top + 3, 14, _resources.TextPrimary);
                    break;

                case LayoutItemKind.Camera:
                    break;
            }
            context.Pop();
        }

        foreach (var item in geometry.Where(item => item.Item.IsSelected))
        {
            if (UseEquipmentPresentation)
            {
                var pen = new Pen(_resources!.AccentBrush, 2 * Math.Min(ActualWidth / 1000d, ActualHeight / 600d)) { DashStyle = new DashStyle([5, 3], 0) };
                var footprint = GetSceneGeometry(item) with { Width = item.Width + 12, Height = item.Height + 12 };
                DrawPolygon(context, null, pen, footprint.GetFootprintCorners().Select(point => projection.ToScreen(point.X, point.Y, item.BaseElevation + item.VerticalHeight)).ToArray());
            }
            DrawSelectedComponentLabel(context, item, projection);
        }

        if (!IsDesignMode || !IsPlacementMoveEnabled || UseEquipmentPresentation)
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

    private VirtualAxisDefinition? EquipmentAxis(LayoutRenderItem item) => EquipmentAxes?.FirstOrDefault(axis => axis.Id == item.Item.BehaviorBindingId);
    private DeviceDefinition? EquipmentDevice(LayoutRenderItem item) => EquipmentDevices?.FirstOrDefault(device => device.Id == item.Item.BehaviorBindingId);
    private double EquipmentCameraX(LayoutRenderItem item)
    {
        var camera = EquipmentDevice(item)?.Camera;
        var axisId = camera?.Geometry is { } geometry ? geometry.AxisId : camera?.NumericalInput?.AxisId;
        var mount = GetAuthoredItems().FirstOrDefault(part => part.BehaviorBindingId == axisId && part.Kind == LayoutItemKind.LinearStage);
        var axis = EquipmentAxes?.FirstOrDefault(axis => axis.Id == axisId);
        if (mount is null || axis is null) return item.X;
        var position = SnapshotSource?.Latest?.Axes.FirstOrDefault(snapshot => snapshot.Id == axisId)?.Position ?? axis.HomePosition;
        return mount.Component!.Transform.X + position + (camera?.Geometry?.OffsetX ?? 0);
    }

    private SceneViewportGeometry GetEquipmentSceneGeometry(LayoutRenderItem item) => item.Item.Kind switch
    {
        LayoutItemKind.Conveyor => new(item.X, item.Y, item.Width, item.Height, 0, 0, 80),
        LayoutItemKind.LinearStage => new(item.Item.Component!.Transform.X + (EquipmentAxis(item)?.SoftLimitMax ?? item.Width) / 2 + 30, item.Y,
            (EquipmentAxis(item)?.SoftLimitMax ?? item.Width) + 80, 36, 0, 0, 50),
        LayoutItemKind.DigitalSensor => new(item.X, item.Y + 9, 18, 18, 0, 0, 104),
        LayoutItemKind.PneumaticCylinder => new(item.X, item.Y, 48, 60, 0, 78, 34),
        LayoutItemKind.Camera => new(EquipmentCameraX(item), item.Y, 40, 40, 0, item.BaseElevation + item.VerticalHeight - 36, 36),
        LayoutItemKind.DigitalIo => new(item.X, item.Y, 24, 24, 0, 0, 150),
        LayoutItemKind.FeedPosition => new(item.X, item.Y, item.Width, 120, 0, 80, 4),
        LayoutItemKind.ExitPosition when item.Item.Component?.MaterialRoute == "NG" => new(item.X, item.Y, item.Width, 64, 0, 0, 46),
        LayoutItemKind.ExitPosition => new(item.X, item.Y, item.Width, 120, 0, 80, 4),
        _ => GetSceneGeometry(item)
    };

    private void DrawEquipmentLayout(DrawingContext context, IReadOnlyList<LayoutRenderItem> items, SimulationSnapshot? snapshot, SceneViewportProjection projection)
    {
        var pixelScale = Math.Min(ActualWidth / 1000, ActualHeight / 600);
        var model = items.FirstOrDefault(item => item.Item.Kind == LayoutItemKind.Workpiece);
        if (model is not null && snapshot is not null)
        {
            var samples = snapshot.LayoutComponents.Where(component => component.Id.StartsWith("__manual-sample-", StringComparison.Ordinal) && component.IsWorkpiecePresent == true)
                .Select(component =>
                {
                    var visual = new LayoutComponentDefinition { Id = component.Id, Name = component.WorkpieceInstanceId ?? component.Name, Kind = LayoutComponentKind.Workpiece,
                        UnitId = model.Item.UnitId, Transform = new Transform2D { X = component.X, Y = component.Y }, Size = new Size2D { Width = component.Width, Height = component.Height },
                        VerticalEnvelope = new LayoutVerticalEnvelope { BaseElevation = Math.Abs(component.Y) <= 66 ? 80 : 0, Height = model.VerticalHeight } };
                    return CreateRenderItems(new[] { new LayoutItem(visual, 1, false) }, snapshot)[0];
                });
            items = items.Concat(samples).ToArray();
        }
        foreach (var group in items.Where(item => item.Item.Kind is not (LayoutItemKind.MachineFrame or LayoutItemKind.Workpiece)).GroupBy(item => item.Item.UnitId))
        {
            var footprints = group.SelectMany(item => GetEquipmentSceneGeometry(item).GetFootprintCorners()).ToArray();
            if (footprints.Length == 0) continue;
            var left = footprints.Min(point => point.X) - 50;
            var right = footprints.Max(point => point.X) + 50;
            var back = footprints.Min(point => point.Y) - 50;
            var front = footprints.Max(point => point.Y) + 50;
            Flat(left, back, right, front, 0, _resources!.DeviceFill.ToString(), _resources.TextSecondary.ToString());
            for (var x = Math.Ceiling(left / 100) * 100; x <= right; x += 100) Segment(x, back, x, front, 0, _resources!.TextSecondary.ToString(), .6);
            for (var y = Math.Ceiling(back / 100) * 100; y <= front; y += 100) Segment(left, y, right, y, 0, _resources!.TextSecondary.ToString(), .6);
            var unit = items.FirstOrDefault(item => item.Item.Kind == LayoutItemKind.MachineFrame && item.Item.UnitId == group.Key);
            if (unit is not null) Label(left + 10, front - 12, 0, unit.Item.Id.StartsWith("__unit-base-", StringComparison.Ordinal) && unit.Item.Name.EndsWith(" 받침대", StringComparison.Ordinal) ? unit.Item.Name[..^4] : unit.Item.Name, _resources!.TextSecondary.ToString(), 12, false, false);
        }
        var zonesDrawn = false;
        foreach (var item in items.Where(item => item.Item.Kind != LayoutItemKind.MachineFrame).OrderBy(Layer).ThenBy(item => item.Y).ThenBy(item => item.X))
        {
            if (item.Item.Kind == LayoutItemKind.Workpiece && (snapshot?.LayoutComponents.FirstOrDefault(component => component.Id == item.Item.Id)?.IsWorkpiecePresent == false
                || EquipmentDevice(item)?.Workpiece?.InitiallyPresent == false && IsDesignMode)) continue;
            if (!zonesDrawn && Layer(item) >= 3)
            {
                DrawSensorZones();
                zonesDrawn = true;
            }
            var g = GetEquipmentSceneGeometry(item);
            var x0 = g.X - g.Width / 2; var x1 = g.X + g.Width / 2;
            var y0 = g.Y - g.Height / 2; var y1 = g.Y + g.Height / 2;
            var top = g.BaseElevation + g.VerticalHeight;
            switch (item.Item.Kind)
            {
                case LayoutItemKind.Conveyor:
                    foreach (var x in new[] { x0 + Math.Min(30, g.Width / 8), x1 - Math.Min(42, g.Width / 7) })
                        Box(x, y0 + 12, x + 12, y1 - 12, 0, 60, "#2B4552");
                    Box(x0, y0, x1, y1, 60, 80, "#3D5A68");
                    for (var x = x0; x < x1; x += 40) Segment(x, y0 + 5, x, y1 - 5, 80.5, "#64848F");
                    break;
                case LayoutItemKind.LinearStage:
                case LayoutItemKind.RotaryStage:
                    var axisOrigin = item.Item.Component!.Transform.X;
                    var axisPosition = SnapshotSource?.Latest?.Axes.FirstOrDefault(axis => axis.Id == item.Item.BehaviorBindingId)?.Position ?? EquipmentAxis(item)?.HomePosition ?? 0;
                    Box(axisOrigin, item.Y - 14, axisOrigin + (EquipmentAxis(item)?.SoftLimitMax ?? item.Width) + 60, item.Y + 14, 0, 26, "#2F5966");
                    Box(axisOrigin + axisPosition, item.Y - 18, axisOrigin + axisPosition + 60, item.Y + 18, 26, 50, "#6DC5D6");
                    break;
                case LayoutItemKind.DigitalSensor:
                    Box(item.X - 8, item.Y, item.X + 8, item.Y + 16, 60, 100, item.IsDetected == true ? "#FFD977" : "#8D7A45");
                    var detectionY = EquipmentDevice(item)?.Sensor?.DetectionCenterY ?? item.Y;
                    Segment(item.X, item.Y, item.X, detectionY - item.Height / 2, 92, item.IsDetected == true ? "#FFD977" : "#8D7A45", 1, item.IsDetected == true ? null : "3 3");
                    break;
                case LayoutItemKind.PneumaticCylinder:
                    Box(item.X - 20, item.Y - 30, item.X + 20, item.Y + 30, 82, 110, "#70D9C5");
                    var tip = item.Y + 30 + (item.MotionProgress ?? 0) * (EquipmentDevice(item)?.Cylinder?.Stroke ?? g.Height);
                    Box(item.X - 5, item.Y + 30, item.X + 5, tip, 90, 98, "#C7D3D8");
                    Box(item.X - 26, tip, item.X + 26, tip + 6, 84, 106, "#E2C46F");
                    break;
                case LayoutItemKind.Camera:
                    var camera = EquipmentDevice(item)?.Camera;
                    var cameraX = EquipmentCameraX(item);
                    var cameraColor = camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D ? "#B48FF0" : "#92A4ED";
                    var mountedAxisId = camera?.Geometry is { } mountGeometry ? mountGeometry.AxisId : camera?.NumericalInput?.AxisId;
                    var mountingAxis = items.FirstOrDefault(part => part.Item.Kind == LayoutItemKind.LinearStage && part.Item.BehaviorBindingId == mountedAxisId);
                    if (mountingAxis is not null)
                    {
                        var carriageX = mountingAxis.Item.Component!.Transform.X + (SnapshotSource?.Latest?.Axes.FirstOrDefault(axis => axis.Id == mountedAxisId)?.Position ?? EquipmentAxis(mountingAxis)?.HomePosition ?? 0) + 30;
                        Box(carriageX - 6, mountingAxis.Y - 6, carriageX + 6, mountingAxis.Y + 6, 50, top + 10, "#4D6F7C");
                        Box(Math.Min(carriageX, cameraX) - 4, Math.Min(mountingAxis.Y, item.Y) - 4, Math.Max(carriageX, cameraX) + 4, Math.Max(mountingAxis.Y, item.Y) + 4, top + 4, top + 10, "#4D6F7C");
                    }
                    else
                    {
                        var poleY = item.Y + (item.Y >= 0 ? 95 : -95);
                        Box(cameraX - 5, poleY - 5, cameraX + 5, poleY + 5, 0, top + 10, "#4D6F7C");
                        Box(cameraX - 4, Math.Min(poleY, item.Y), cameraX + 4, Math.Max(poleY, item.Y), top + 4, top + 10, "#4D6F7C");
                    }
                    Box(cameraX - 18, item.Y - 18, cameraX + 18, item.Y + 18, top - 34, top, cameraColor);
                    break;
                case LayoutItemKind.DigitalIo:
                    Box(item.X - 5, item.Y - 5, item.X + 5, item.Y + 5, 0, 110, "#55707C");
                    Box(item.X - 10, item.Y - 10, item.X + 10, item.Y + 10, 110, 128, "#3F8F5A");
                    var on = snapshot?.Signals.FirstOrDefault(signal => signal.Id == item.Item.BehaviorBindingId)?.Value == true;
                    Box(item.X - 10, item.Y - 10, item.X + 10, item.Y + 10, 128, 146, on ? "#FF6B5E" : "#6A3A36");
                    break;
                case LayoutItemKind.FeedPosition:
                    Flat(x0, -56, x1, 56, 80.8, null, "#C9B46A", "5 4", 1.2); Label(item.X, y0 - 14, 82, OpenVisionLanguageService.T("R24.Feed"), _resources!.TextSecondary.ToString(), 11); break;
                case LayoutItemKind.ExitPosition:
                    if (item.Item.Component?.MaterialRoute == "NG") Box(x0, y0, x1, y1, 0, 46, "#5A3B42");
                    else Flat(x0, -56, x1, 56, 80.8, null, "#7BD88F", "5 4", 1.2);
                    Label(item.X, item.Item.Component?.MaterialRoute == "NG" ? item.Y + 44 : -70, item.Item.Component?.MaterialRoute == "NG" ? 50 : 82, item.Item.Component?.MaterialRoute == "NG" ? "NG " + OpenVisionLanguageService.T("R24.Box") : "OK " + OpenVisionLanguageService.T("R24.ExitArrow"), item.Item.Component?.MaterialRoute == "NG" ? _resources!.FaultBrush.ToString() : _resources!.SensorOnBrush.ToString(), 11);
                    break;
                default:
                    Box(x0, y0, x1, y1, g.BaseElevation, top, item.Item.Kind == LayoutItemKind.Workpiece ? "#E2C46F" : "#7F9FAE"); break;
            }
            if (!IsObliqueView && item.Item.IsSelected) Flat(x0 - 6, y0 - 6, x1 + 6, y1 + 6, top, null, _resources!.AccentBrush.ToString(), "5 3", 2);
        }
        if (!zonesDrawn) DrawSensorZones();
        foreach (var item in items.Where(item => item.Item.Kind == LayoutItemKind.Camera))
        {
            var camera = EquipmentDevice(item)?.Camera;
            var cameraX = EquipmentCameraX(item);
            var cameraColor = camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D ? "#B48FF0" : "#92A4ED";
            var fovWidth = camera?.Geometry?.FieldOfViewWidth ?? camera?.NumericalInput?.FieldOfView ?? 120;
            var fovHeight = camera?.Geometry?.FieldOfViewHeight ?? camera?.NumericalInput?.FieldOfViewHeight ?? fovWidth;
            var fovY = camera?.Geometry?.CenterY ?? 0;
            Flat(cameraX - fovWidth / 2, fovY - fovHeight / 2, cameraX + fovWidth / 2, fovY + fovHeight / 2, 81.5, null, cameraColor, "6 4", 1.2);
            if (IsObliqueView)
            {
                SpatialSegment(cameraX, item.Y, item.BaseElevation + item.VerticalHeight - 34, cameraX - fovWidth / 2, fovY - fovHeight / 2, 81.5, cameraColor, .6, "2 4");
                SpatialSegment(cameraX, item.Y, item.BaseElevation + item.VerticalHeight - 34, cameraX + fovWidth / 2, fovY + fovHeight / 2, 81.5, cameraColor, .6, "2 4");
            }
        }
        foreach (var item in items.Where(item => item.Item.IsSelected && item.Item.Kind != LayoutItemKind.Workpiece)) DrawSelectedComponentLabel(context, item, projection);

        static int Layer(LayoutRenderItem item) => item.Item.Kind switch { LayoutItemKind.LinearStage or LayoutItemKind.RotaryStage or LayoutItemKind.DigitalIo => 0, LayoutItemKind.Conveyor => 1, LayoutItemKind.ExitPosition when item.Item.Component?.MaterialRoute == "NG" => 4, LayoutItemKind.PneumaticCylinder or LayoutItemKind.FeedPosition or LayoutItemKind.ExitPosition => 2, LayoutItemKind.Workpiece => 3, LayoutItemKind.DigitalSensor => 4, LayoutItemKind.Camera => 5, _ => 6 };
        void DrawSensorZones()
        {
            foreach (var sensor in items.Where(item => item.Item.Kind == LayoutItemKind.DigitalSensor))
            {
                var center = EquipmentDevice(sensor)?.Sensor?.DetectionCenterY ?? sensor.Y;
                Flat(sensor.X - sensor.Width / 2, center - sensor.Height / 2, sensor.X + sensor.Width / 2, center + sensor.Height / 2, 81, sensor.IsDetected == true ? "#80FFD977" : null, "#E8C45F", sensor.IsDetected == true ? null : "3 3");
            }
        }
        static Brush Fill(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Pen Stroke(string color, double width, string? dash = null)
        {
            var pen = new Pen(Fill(color), width * pixelScale);
            if (dash is not null) pen.DashStyle = new DashStyle(dash.Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture) / width), 0);
            return pen;
        }
        void Polygon(Point[] points, string? color, string stroke, double width = 1, string? dash = null) =>
            DrawPolygon(context, color is null ? null : Fill(color), Stroke(stroke, width, dash), points);
        void Flat(double left, double back, double right, double front, double z, string? color, string stroke, string? dash = null, double width = 1) =>
            Polygon([projection.ToScreen(left, back, z), projection.ToScreen(right, back, z), projection.ToScreen(right, front, z), projection.ToScreen(left, front, z)], color, stroke, width, dash);
        void Box(double left, double back, double right, double front, double bottom, double upper, string color)
        {
            var rgb = (Color)ColorConverter.ConvertFromString(color);
            string Shade(double factor) => Color.FromRgb((byte)Math.Round(rgb.R * factor), (byte)Math.Round(rgb.G * factor), (byte)Math.Round(rgb.B * factor)).ToString();
            if (IsObliqueView)
            {
                Polygon([projection.ToScreen(left, front, bottom), projection.ToScreen(right, front, bottom), projection.ToScreen(right, front, upper), projection.ToScreen(left, front, upper)], Shade(.72), "#06141C", .8);
                Polygon([projection.ToScreen(right, front, bottom), projection.ToScreen(right, back, bottom), projection.ToScreen(right, back, upper), projection.ToScreen(right, front, upper)], Shade(.56), "#06141C", .8);
            }
            Flat(left, back, right, front, upper, color, "#06141C", null, .8);
        }
        void Segment(double ax, double ay, double bx, double by, double z, string color, double width = 1, string? dash = null) => context.DrawLine(Stroke(color, width, dash), projection.ToScreen(ax, ay, z), projection.ToScreen(bx, by, z));
        void SpatialSegment(double ax, double ay, double az, double bx, double by, double bz, string color, double width, string? dash) => context.DrawLine(Stroke(color, width, dash), projection.ToScreen(ax, ay, az), projection.ToScreen(bx, by, bz));
        void Label(double x, double y, double z, string text, string color, double size, bool bold = false, bool centered = true)
        {
            var point = projection.ToScreen(x, y, z);
            var formatted = GetText(text, size * pixelScale, Fill(color), semiBold: bold);
            context.DrawText(formatted, new Point(point.X - (centered ? formatted.Width / 2 : 0), point.Y - formatted.Baseline));
        }
    }

    private void DrawObliqueLayout(
        DrawingContext context,
        IReadOnlyList<LayoutRenderItem> items,
        SimulationSnapshot? snapshot,
        SceneViewportProjection projection)
    {
        var showRuntimeState = !IsDesignMode && snapshot is not null;
        // ponytail: depth-sorted box faces; use a z-buffer if intersecting authored geometry becomes supported.
        foreach (var body in GetOrderedSchematicFaces(items, projection))
        {
            var (_, stateOutline) = GetComponentSurfaceStyle(body.Item);
            var outline = HasCompositeBody(body.Item.Item.Kind)
                ? body.Item.Item.IsSelected && !UseEquipmentPresentation ? _resources!.SelectionPen : _resources!.SchematicOutlinePen : stateOutline;
            DrawPolygon(context, body.Face.Horizontal ? body.Fill : _resources!.SideFill(body.Fill), outline, body.Face.Points);
        }
        foreach (var item in items.OrderBy(item => projection.Depth(item.X, item.Y, item.BaseElevation + item.VerticalHeight / 2)))
        {
            var topBounds = GetTopFaceScreenBounds(item, projection);
            if (item.IsVerticalEnvelopeEstimated)
            {
                DrawStatusBadgeAtRight(
                    context,
                    OpenVisionLanguageService.T("Scene.SchematicEstimateShort", "추정", "EST"),
                    topBounds.Right - 3,
                    topBounds.Top + 2,
                    8,
                    _resources!.TextSecondary);
            }

            if (showRuntimeState)
            {
                DrawObliqueRuntimeStatus(context, item, snapshot!, topBounds);
            }
        }

        foreach (var item in items.Where(item => item.Item.IsSelected)) DrawSelectedComponentLabel(context, item, projection);

        if (!IsDesignMode || !IsPlacementMoveEnabled || UseEquipmentPresentation)
        {
            return;
        }

        var selectedItems = items.Where(item => item.Item.IsSelected).ToArray();
        if (selectedItems is [var transformItem])
        {
            DrawTransformHandles(context, transformItem, projection);
        }
        else if (selectedItems.Length > 1)
        {
            DrawSelectionTransformHandles(context, selectedItems, projection);
        }
    }

    private static bool HasCompositeBody(LayoutItemKind kind) => kind is LayoutItemKind.MachineFrame
        or LayoutItemKind.LinearStage or LayoutItemKind.PneumaticCylinder or LayoutItemKind.Conveyor or LayoutItemKind.Camera
        or LayoutItemKind.FeedPosition or LayoutItemKind.ExitPosition;

    private IEnumerable<(LayoutRenderItem Item, Brush Fill, (Point[] Points, double Depth, bool Horizontal) Face)> GetOrderedSchematicFaces(
        IReadOnlyList<LayoutRenderItem> items, SceneViewportProjection projection)
    {
        var volumes = items.SelectMany(item => GetSchematicVolumes(item).Select(volume => (Item: item, volume.Geometry, volume.Fill))).ToArray();
        var frames = volumes.Where(volume => volume.Item.Item.Kind == LayoutItemKind.MachineFrame).ToArray();
        var components = volumes.Where(volume => volume.Item.Item.Kind != LayoutItemKind.MachineFrame).ToArray();
        // A floor entirely below the parts cannot occlude them from above. Face-centroid depth alone loses this ordering on a wide table.
        bool separatedFloor = frames.Length > 0 && components.Length > 0
            && frames.Max(volume => volume.Geometry.BaseElevation + volume.Geometry.VerticalHeight) <= components.Min(volume => volume.Geometry.BaseElevation);
        bool floorInFront = projection.PitchDegrees < 0;
        return volumes.SelectMany(volume => GetVolumeFaces(volume.Geometry, projection).Select(face => (volume.Item, volume.Fill, Face: face)))
            .OrderBy(body => separatedFloor && (body.Item.Item.Kind == LayoutItemKind.MachineFrame) == floorInFront ? 1 : 0)
            .ThenBy(body => body.Face.Depth);
    }

    private IEnumerable<(SceneViewportGeometry Geometry, Brush Fill)> GetSchematicVolumes(LayoutRenderItem item)
    {
        var (fill, _) = GetComponentSurfaceStyle(item);
        var materials = _resources!;
        // Reference body proportions scale inside the existing authored envelope.
        switch (item.Item.Kind)
        {
            case LayoutItemKind.MachineFrame:
                foreach (var x in new[] { 10d, 320d })
                foreach (var y in new[] { 10d, 130d })
                    yield return Volume(x / 340, y / 155, 10d / 340, 10d / 155, 0, 80d / 92, materials.FrameSupportFill);
                yield return Volume(0, 0, 1, 1, 77d / 92, 15d / 92, fill);
                break;
            case LayoutItemKind.LinearStage:
                yield return Volume(0, 4d / 38, 1, 30d / 38, 0, 12d / 33, materials.AxisBaseFill);
                yield return Volume(0, 0, 30d / 90, 1, 13d / 33, 20d / 33, fill);
                break;
            case LayoutItemKind.PneumaticCylinder:
                yield return Volume(0, 0, 38d / 58, 1, 0, 25d / 28, fill);
                yield return Volume(38d / 58, 11d / 32, 12d / 58, 8d / 32, 9d / 28, 7d / 28, materials.CylinderRodFill);
                yield return Volume(50d / 58, 2d / 32, 8d / 58, 27d / 32, 3d / 28, 25d / 28, materials.CylinderJawFill);
                break;
            case LayoutItemKind.Camera:
                yield return Volume(40d / 52, 55d / 67, 12d / 52, 12d / 67, 0, 1, materials.CameraPostFill);
                yield return Volume(5d / 52, 10d / 67, 47d / 52, 57d / 67, 110d / 125, 10d / 125, materials.CameraPlateFill);
                yield return Volume(0, 0, 35d / 52, 35d / 67, 70d / 125, 38d / 125, fill);
                break;
            case LayoutItemKind.Conveyor:
                yield return Volume(0, 0, 1, 1, 0, 13d / 18, materials.ConveyorBaseFill);
                for (var index = 0; index < 9; index++)
                    yield return Volume((3d + index * 24) / 205, 1d / 55, 6d / 205, 53d / 55, 14d / 18, 4d / 18, materials.ConveyorRollerFill);
                break;
            default:
                yield return (GetSceneGeometry(item), fill);
                break;
        }

        (SceneViewportGeometry Geometry, Brush Fill) Volume(double x, double y, double width, double depth, double bottom, double height, Brush brush)
        {
            var center = RotatePoint(new Point(item.X + ((x + width / 2 - 0.5) * item.Width),
                item.Y + ((y + depth / 2 - 0.5) * item.Height)), new Point(item.X, item.Y), item.RotationDegrees);
            return (new SceneViewportGeometry(center.X, center.Y, width * item.Width, depth * item.Height,
                item.RotationDegrees, item.BaseElevation + bottom * item.VerticalHeight, height * item.VerticalHeight), brush);
        }
    }

    private void DrawSchematicBody(DrawingContext context, LayoutRenderItem item, SceneViewportProjection projection)
    {
        var (_, stateOutline) = GetComponentSurfaceStyle(item);
        var outline = HasCompositeBody(item.Item.Kind)
            ? item.Item.IsSelected ? _resources!.SelectionPen : _resources!.SchematicOutlinePen : stateOutline;
        foreach (var (volume, fill) in GetSchematicVolumes(item))
        {
            foreach (var face in GetVolumeFaces(volume, projection).OrderBy(face => face.Depth))
                DrawPolygon(context, face.Horizontal ? fill : _resources!.SideFill(fill), outline, face.Points);
        }
    }

    private static IEnumerable<(Point[] Points, double Depth, bool Horizontal)> GetVolumeFaces(SceneViewportGeometry volume, SceneViewportProjection projection)
    {
        var footprint = volume.GetFootprintCorners();
        var bottom = footprint.Select(point => projection.ToScreen(point.X, point.Y, volume.BaseElevation)).ToArray();
        var top = footprint.Select(point => projection.ToScreen(point.X, point.Y, volume.BaseElevation + volume.VerticalHeight)).ToArray();
        if (!projection.IsObliqueView)
        {
            yield return (top, 0, true);
            yield break;
        }
        var yaw = projection.YawDegrees * Math.PI / 180;
        var pitch = projection.PitchDegrees * Math.PI / 180;
        var eyeX = Math.Sin(yaw) * Math.Cos(pitch);
        var eyeY = Math.Cos(yaw) * Math.Cos(pitch);
        for (int index = 0; index < 4; index++)
        {
            int next = (index + 1) % 4;
            var edge = footprint[next] - footprint[index];
            if (edge.Y * eyeX - edge.X * eyeY <= 0.000001) continue;
            double depth = projection.Depth((footprint[index].X + footprint[next].X) / 2,
                (footprint[index].Y + footprint[next].Y) / 2, volume.BaseElevation + volume.VerticalHeight / 2);
            yield return ([bottom[index], bottom[next], top[next], top[index]], depth, false);
        }
        if (Math.Sin(pitch) > 0.000001)
            yield return (top, projection.Depth(volume.X, volume.Y, volume.BaseElevation + volume.VerticalHeight), true);
        else if (Math.Sin(pitch) < -0.000001)
            yield return (bottom, projection.Depth(volume.X, volume.Y, volume.BaseElevation), true);
    }

    private (Brush Fill, Pen Outline) GetComponentSurfaceStyle(
        LayoutRenderItem item)
    {
        var selected = item.Item.IsSelected && !UseEquipmentPresentation;
        var surface = item.Item.Kind switch
        {
            LayoutItemKind.MachineFrame => (_resources!.SchematicFrameFill, selected ? _resources.SelectionPen : _resources.FramePen),
            LayoutItemKind.LinearStage or LayoutItemKind.RotaryStage => (_resources!.AxisComponentFill, selected ? _resources.SelectionPen : _resources.AxisPen),
            LayoutItemKind.DigitalIo => (_resources!.DigitalIoComponentFill, selected ? _resources.SelectionPen : !IsDesignMode && item.IsDetected == true ? _resources.SensorOnPen : _resources.SchematicOutlinePen),
            LayoutItemKind.DigitalSensor when item.IsDetected == true => (_resources!.SensorOnFill, selected ? _resources.SelectionPen : _resources.SensorOnPen),
            LayoutItemKind.DigitalSensor => (_resources!.SensorComponentFill, selected ? _resources.SelectionPen : _resources.VisionPen),
            LayoutItemKind.PneumaticCylinder when item.CylinderState == PneumaticCylinderState.Fault => (_resources!.FaultFill, selected ? _resources.SelectionPen : _resources.FaultPen),
            LayoutItemKind.PneumaticCylinder when item.CylinderState == PneumaticCylinderState.Extended => (_resources!.SensorOnFill, selected ? _resources.SelectionPen : _resources.SensorOnPen),
            LayoutItemKind.PneumaticCylinder => (_resources!.CylinderComponentFill, selected ? _resources.SelectionPen : _resources.AxisPen),
            LayoutItemKind.Conveyor when item.ConveyorRunning == true => (_resources!.ConveyorComponentFill, selected ? _resources.SelectionPen : _resources.AxisPen),
            LayoutItemKind.Conveyor => (_resources!.ConveyorComponentFill, selected ? _resources.SelectionPen : _resources.AxisPen),
            LayoutItemKind.Workpiece when item.InspectionState == WorkpieceInspectionState.Failed ||
                                         item.TransferOwnershipState == WaferHandlerOwnershipState.InterlockFault => (_resources!.FaultFill, selected ? _resources.SelectionPen : _resources.FaultPen),
            LayoutItemKind.Workpiece when item.InspectionState == WorkpieceInspectionState.Passed => (_resources!.SensorOnFill, selected ? _resources.SelectionPen : _resources.SensorOnPen),
            LayoutItemKind.Workpiece => (_resources!.DeviceFill, selected ? _resources.SelectionPen : _resources.VisionPen),
            LayoutItemKind.Camera => (_resources!.CameraComponentFill, selected ? _resources.SelectionPen : _resources.VisionPen),
            _ => (_resources!.DeviceFill, selected ? _resources.SelectionPen : _resources.StructurePen)
        };
        return selected && item.Item.Kind is not (LayoutItemKind.Workpiece or LayoutItemKind.MachineFrame)
            && item.CylinderState != PneumaticCylinderState.Fault
            ? (_resources!.SelectedComponentFill, surface.Item2) : surface;
    }

    private void DrawObliqueRuntimeStatus(
        DrawingContext context,
        LayoutRenderItem item,
        SimulationSnapshot snapshot,
        Rect bounds)
    {
        string? status = null;
        Brush statusBrush = _resources!.TextPrimary;
        switch (item.Item.Kind)
        {
            case LayoutItemKind.LinearStage:
            case LayoutItemKind.RotaryStage:
                var axis = snapshot.Axes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, item.Item.BehaviorBindingId, StringComparison.Ordinal));
                if (axis is not null)
                {
                    status = item.Item.Kind == LayoutItemKind.RotaryStage
                        ? $"{axis.Position:F0}° {axis.State.ToString().ToUpperInvariant()}"
                        : $"{axis.Position:F0} {axis.State.ToString().ToUpperInvariant()}";
                }
                break;
            case LayoutItemKind.DigitalIo:
            case LayoutItemKind.DigitalSensor:
                status = item.IsDetected == true ? "ON" : "OFF";
                statusBrush = item.IsDetected == true ? _resources.SensorOnBrush : _resources.TextSecondary;
                break;
            case LayoutItemKind.PneumaticCylinder:
                var cylinderState = item.CylinderState ?? PneumaticCylinderState.Retracted;
                status = cylinderState.ToString().ToUpperInvariant();
                statusBrush = cylinderState == PneumaticCylinderState.Fault
                    ? _resources.FaultBrush
                    : cylinderState == PneumaticCylinderState.Extended ? _resources.SensorOnBrush : _resources.TextPrimary;
                break;
            case LayoutItemKind.Conveyor:
                var direction = item.ConveyorDirection ?? ConveyorDirection.Forward;
                status = item.ConveyorRunning == true
                    ? direction == ConveyorDirection.Forward ? "RUN >" : "RUN <"
                    : "STOP";
                statusBrush = item.ConveyorRunning == true ? _resources.AccentBrush : _resources.TextSecondary;
                break;
            case LayoutItemKind.Workpiece:
                var transferState = item.TransferOwnershipState;
                if (transferState is not null)
                {
                    var transferCode = transferState switch
                    {
                        WaferHandlerOwnershipState.Source => "SOURCE",
                        WaferHandlerOwnershipState.Handler => "HANDLER",
                        WaferHandlerOwnershipState.Destination => "DEST",
                        _ => "FAULT"
                    };
                    LastRenderedTransferOwnershipState = transferState;
                    LastRenderedTransferOwnershipText = transferCode;
                    DrawStatusBadge(context, transferCode, bounds.Left + 3, bounds.Top + 2, 8,
                        transferState == WaferHandlerOwnershipState.InterlockFault ? _resources.FaultBrush : _resources.TextSecondary);
                }
                var inspectionState = item.InspectionState ?? WorkpieceInspectionState.Pending;
                status = inspectionState switch
                {
                    WorkpieceInspectionState.Passed => "PASS",
                    WorkpieceInspectionState.Failed => "FAIL",
                    WorkpieceInspectionState.Skipped => "SKIP",
                    _ => "PEND"
                };
                statusBrush = inspectionState == WorkpieceInspectionState.Failed
                    ? _resources.FaultBrush
                    : inspectionState == WorkpieceInspectionState.Passed ? _resources.SensorOnBrush : _resources.TextSecondary;
                break;
        }

        if (!string.IsNullOrEmpty(status))
        {
            DrawStatusBadge(context, status, bounds.Left + 3, bounds.Bottom - 15, 8, statusBrush);
        }
    }

    private static void DrawPolygon(
        DrawingContext context,
        Brush? fill,
        Pen outline,
        IReadOnlyList<Point> points) => context.DrawGeometry(fill, outline, CreatePolygon(points));

    private static StreamGeometry CreatePolygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(points[0], true, true);
            geometryContext.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Rect Bounds(IReadOnlyList<Point> points)
    {
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void DrawTransformHandles(
        DrawingContext context,
        LayoutRenderItem item,
        SceneViewportProjection projection)
    {
        var handles = GetTransformHandleCenters(item, projection);
        var topCenter = GetRotatedLocalPoint(item, projection, 0d, -item.Height / 2d);
        DrawTransformHandleSet(context, handles, topCenter);
    }

    private void DrawSelectionTransformHandles(
        DrawingContext context,
        IReadOnlyList<LayoutRenderItem> items,
        SceneViewportProjection projection)
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

    private IEnumerable<LayoutRenderItem> CreateUnitWorkpieceVisuals(IReadOnlyList<LayoutRenderItem> geometry, SimulationSnapshot? snapshot)
    {
        if (IsDesignMode || snapshot is null) yield break;
        foreach (var slot in snapshot.UnitMaterialSlots.Where(slot => slot.InstanceId is not null))
        {
            var source = geometry.FirstOrDefault(part => part.Item.UnitId == slot.UnitId && part.Item.Kind == LayoutItemKind.Conveyor)
                ?? geometry.FirstOrDefault(part => part.Item.UnitId == slot.UnitId && part.Item.Kind == LayoutItemKind.MachineFrame);
            if (source is null) continue;
            var destination = geometry.FirstOrDefault(part => part.Item.UnitId == slot.DestinationUnitId && part.Item.Kind == LayoutItemKind.Conveyor);
            double x = source.X, y = source.Y;
            if (slot.State == "Moving")
            {
                x += ((destination?.X ?? x + source.Width / 2) - x) * slot.TransferProgress;
                y += ((destination?.Y ?? y) - y) * slot.TransferProgress;
            }
            var component = new LayoutComponentDefinition
            {
                Id = "__unit-piece-" + slot.UnitId, Name = slot.WorkpieceName + " · " + slot.InstanceId, UnitId = slot.UnitId,
                Kind = LayoutComponentKind.Workpiece, Transform = new Transform2D { X = x, Y = y },
                Size = new Size2D { Width = 40, Height = 24 },
                VerticalEnvelope = new LayoutVerticalEnvelope { BaseElevation = Math.Max(20, source.BaseElevation + source.VerticalHeight + 2), Height = 8 }
            };
            var visual = CreateRenderItems(new[] { new LayoutItem(component, 1, false) }, null)[0];
            yield return visual with { InspectionState = slot.Verdict switch { "OK" => WorkpieceInspectionState.Passed, "NG" => WorkpieceInspectionState.Failed, _ => WorkpieceInspectionState.Pending } };
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
            var verticalEnvelope = item.Component?.VerticalEnvelope;
            var hasExplicitVerticalEnvelope = verticalEnvelope is not null &&
                double.IsFinite(verticalEnvelope.BaseElevation) &&
                double.IsFinite(verticalEnvelope.Height) &&
                verticalEnvelope.Height > 0;
            var width = runtime?.Width ?? item.Width;
            var height = runtime?.Height ?? item.Height;
            return new LayoutRenderItem(
                item,
                runtime?.X ?? item.CurrentX,
                runtime?.Y ?? item.CurrentY,
                width,
                height,
                runtime?.RotationDegrees ?? item.RotationDegrees,
                hasExplicitVerticalEnvelope ? verticalEnvelope!.BaseElevation : 0,
                hasExplicitVerticalEnvelope
                    ? verticalEnvelope!.Height
                    : LayoutVerticalEnvelope.GetSchematicHeight(width, height),
                !hasExplicitVerticalEnvelope,
                item.Kind == LayoutItemKind.DigitalIo
                    ? snapshot?.Signals.FirstOrDefault(signal => signal.Id == item.BehaviorBindingId)?.Value == true
                    : runtime?.IsDetected,
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
        if (UseEquipmentPresentation)
        {
            foreach (var item in geometry.OrderByDescending(item => item.Item.Kind is LayoutItemKind.Camera or LayoutItemKind.DigitalIo ? 5 : item.Item.Kind == LayoutItemKind.DigitalSensor ? 4 : 2))
            {
                var g = GetEquipmentSceneGeometry(item);
                var corners = g.GetFootprintCorners().SelectMany(corner => new[] { projection.ToScreen(corner.X, corner.Y, g.BaseElevation), projection.ToScreen(corner.X, corner.Y, g.BaseElevation + g.VerticalHeight) }).ToArray();
                var bounds = Bounds(corners);
                bounds.Inflate(4 * Math.Min(ActualWidth / 1000, ActualHeight / 600), 4 * Math.Min(ActualWidth / 1000, ActualHeight / 600));
                if (bounds.Contains(point)) return item.Item;
            }
            return null;
        }
        if (IsObliqueView && _resources is not null)
        {
            var faces = GetOrderedSchematicFaces(geometry, projection).Reverse();
            foreach (var entry in faces)
            {
                var shape = CreatePolygon(entry.Face.Points);
                if (shape.FillContains(point) || shape.StrokeContains(_resources.SelectionPen, point)) return entry.Item.Item;
            }
            return null;
        }
        foreach (var renderItem in geometry.Reverse())
        {
            if (HasCompositeBody(renderItem.Item.Kind) && _resources is not null)
            {
                if (GetSchematicVolumes(renderItem).Any(volume => GetVolumeFaces(volume.Geometry, projection).Any(face => ContainsFace(face.Points))))
                    return renderItem.Item;
                continue;

                bool ContainsFace(Point[] face)
                {
                    var shape = CreatePolygon(face);
                    return shape.FillContains(point) || shape.StrokeContains(_resources.SelectionPen, point);
                }
            }
            if (IsObliqueView)
            {
                var obliqueBounds = GetScreenBounds(renderItem, projection);
                obliqueBounds.Inflate(3, 3);
                if (obliqueBounds.Contains(point))
                {
                    return renderItem.Item;
                }
                continue;
            }

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
        if (!IsDesignMode || !IsPlacementMoveEnabled || CreateCurrentProjection() is not { CanMapToWorld: true })
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
            SceneViewportProjection projection)
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
        SceneViewportProjection projection)
    {
        if (projection.IsObliqueView)
        {
            var firstFootprint = GetSceneGeometry(items[0]).GetFootprintCorners()
                .Select(point => projection.ToScreen(point.X, point.Y))
                .ToArray();
            var planBounds = Bounds(firstFootprint);
            foreach (var item in items.Skip(1))
            {
                var footprint = GetSceneGeometry(item).GetFootprintCorners()
                    .Select(point => projection.ToScreen(point.X, point.Y))
                    .ToArray();
                planBounds.Union(Bounds(footprint));
            }
            return planBounds;
        }

        var bounds = GetScreenBounds(items[0], projection);
        foreach (var item in items.Skip(1))
        {
            bounds.Union(GetScreenBounds(item, projection));
        }
        return bounds;
    }

    private static IReadOnlyDictionary<LayoutTransformHandle, Point> GetTransformHandleCenters(
        LayoutRenderItem item,
        SceneViewportProjection projection)
    {
        var topLeft = GetRotatedLocalPoint(item, projection, -item.Width / 2d, -item.Height / 2d);
        var topRight = GetRotatedLocalPoint(item, projection, item.Width / 2d, -item.Height / 2d);
        var bottomRight = GetRotatedLocalPoint(item, projection, item.Width / 2d, item.Height / 2d);
        var bottomLeft = GetRotatedLocalPoint(item, projection, -item.Width / 2d, item.Height / 2d);
        var topCenter = GetRotatedLocalPoint(item, projection, 0d, -item.Height / 2d);
        if (projection.IsObliqueView)
        {
            var center = projection.ToScreen(
                item.X,
                item.Y,
                item.BaseElevation + item.VerticalHeight);
            var obliqueOutward = topCenter - center;
            if (obliqueOutward.Length > 0)
            {
                obliqueOutward.Normalize();
            }
            return new Dictionary<LayoutTransformHandle, Point>
            {
                [LayoutTransformHandle.TopLeft] = topLeft,
                [LayoutTransformHandle.TopRight] = topRight,
                [LayoutTransformHandle.BottomRight] = bottomRight,
                [LayoutTransformHandle.BottomLeft] = bottomLeft,
                [LayoutTransformHandle.Rotation] = topCenter + (obliqueOutward * 24d)
            };
        }

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
        SceneViewportProjection projection,
        double localX,
        double localY)
    {
        if (projection.IsObliqueView)
        {
            var radians = item.RotationDegrees * Math.PI / 180d;
            var worldX = item.X + (localX * Math.Cos(radians)) - (localY * Math.Sin(radians));
            var worldY = item.Y + (localX * Math.Sin(radians)) + (localY * Math.Cos(radians));
            return projection.ToScreen(worldX, worldY, item.BaseElevation + item.VerticalHeight);
        }

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
                projection.ToWorldDelta(point - _gestureStart)));
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

    private SceneViewportProjection? CreateCurrentProjection()
    {
        var authoredItems = GetAuthoredItems();
        if ((authoredItems.Length == 0 && !(ReferenceUnitFrames?.Any() ?? false)) || ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }
        return CreateProjection(CreateRenderItems(authoredItems, SnapshotSource?.Latest));
    }

    private SceneViewportProjection? CreateDropProjection()
    {
        if (ActualWidth < 1 || ActualHeight < 1)
        {
            return null;
        }

        return CreateCurrentProjection()
            ?? _viewProjection
            ?? SceneViewportProjection.CreateEmpty(ActualWidth, ActualHeight, IsObliqueView, _cameraYaw, _cameraPitch);
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

    private SceneViewportProjection CreateProjection(IReadOnlyList<LayoutRenderItem> geometry) =>
        _viewProjection ??= UseEquipmentPresentation
            ? SceneViewportProjection.CreateEquipment(geometry.Where(item => item.Item.Kind is not (LayoutItemKind.MachineFrame or LayoutItemKind.Workpiece)).Select(GetEquipmentSceneGeometry).ToArray(), ActualWidth, ActualHeight, IsObliqueView)
            : SceneViewportProjection.Create(
            geometry.Concat(CreateRenderItems((ReferenceUnitFrames ?? []).ToArray(), null)).Select(GetSceneGeometry).ToArray(),
            ActualWidth,
            ActualHeight,
            IsObliqueView, _cameraYaw, _cameraPitch);

    private SceneViewportProjection OrbitProjection(SceneViewportProjection projection, Vector delta)
    {
        var geometry = CreateRenderItems((ReferenceUnitFrames ?? []).Concat(GetAuthoredItems()).ToArray(), SnapshotSource?.Latest).Select(GetSceneGeometry).ToArray();
        if (geometry.Length == 0) return projection.OrbitAt(default, 0, delta);
        double x = (geometry.Min(item => item.X - item.Width / 2) + geometry.Max(item => item.X + item.Width / 2)) / 2;
        double y = (geometry.Min(item => item.Y - item.Height / 2) + geometry.Max(item => item.Y + item.Height / 2)) / 2;
        double height = (geometry.Min(item => item.BaseElevation) + geometry.Max(item => item.BaseElevation + item.VerticalHeight)) / 2;
        return projection.OrbitAt(new Point(x, y), height, delta);
    }

    private void UpdateOrbit(Point point)
    {
        if (_gestureProjection is not { } projection) return;
        _viewProjection = OrbitProjection(projection, point - _gestureStart);
        _cameraYaw = _viewProjection.Value.YawDegrees;
        _cameraPitch = _viewProjection.Value.PitchDegrees;
        DrawGrid();
        InvalidateScene();
    }

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
            projection.ToWorld(point, _gestureElevation),
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

    private static Rect GetScreenBounds(LayoutRenderItem item, SceneViewportProjection projection)
    {
        if (projection.IsObliqueView)
        {
            return Bounds(GetProjectedVolumePoints(item, projection));
        }

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

    private static Rect GetTopFaceScreenBounds(LayoutRenderItem item, SceneViewportProjection projection) =>
        projection.IsObliqueView
            ? Bounds(GetSceneGeometry(item).GetFootprintCorners().Select(point =>
                projection.ToScreen(point.X, point.Y, item.BaseElevation + item.VerticalHeight)).ToArray())
            : GetScreenBounds(item, projection);

    private static Point[] GetProjectedVolumePoints(LayoutRenderItem item, SceneViewportProjection projection)
    {
        var geometry = GetSceneGeometry(item);
        return geometry.GetFootprintCorners().SelectMany(point => new[]
        {
            projection.ToScreen(point.X, point.Y, item.BaseElevation),
            projection.ToScreen(point.X, point.Y, item.BaseElevation + item.VerticalHeight)
        }).ToArray();
    }

    private static SceneViewportGeometry GetSceneGeometry(LayoutRenderItem item) => new(
        item.X,
        item.Y,
        item.Width,
        item.Height,
        item.RotationDegrees,
        item.BaseElevation,
        item.VerticalHeight);

    private double GetSelectionReferenceElevation()
    {
        var selected = CreateRenderItems(GetAuthoredItems(), SnapshotSource?.Latest)
            .Where(item => item.Item.IsSelected)
            .ToArray();
        return selected.Length == 1 ? selected[0].BaseElevation + selected[0].VerticalHeight : 0;
    }

    private static Rect NormalizeRect(Point first, Point second) => new(
        new Point(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y)),
        new Point(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y)));

    private void ResetPointerGesture(bool releaseMouse)
    {
        _pointerGesture = PointerGesture.None;
        _pressedItem = null;
        _gestureProjection = null;
        _gestureElevation = 0;
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

    private void DrawSelectedComponentLabel(DrawingContext context, LayoutRenderItem item, SceneViewportProjection projection)
    {
        if (UseEquipmentPresentation)
        {
            DrawEquipmentSelectedComponentLabel(context, item, projection);
            return;
        }
        var bounds = GetTopFaceScreenBounds(item, projection);
        var text = $"{item.Item.Name} {item.Item.Id}";
        var formatted = GetText(text, 11, _resources!.AccentBrush, Math.Max(1, ActualWidth - 12));
        var width = formatted.Width + 8;
        var x = Math.Clamp(bounds.Left + 3, 2, Math.Max(2, ActualWidth - width - 2));
        var y = Math.Clamp(bounds.Top - (IsDesignMode ? 44 : 20), 2, Math.Max(2, ActualHeight - formatted.Height - 4));
        if (_labelOverlayBounds.IntersectsWith(new Rect(x, y, width, formatted.Height + 2)))
        {
            if (_labelOverlayBounds.Left > 12)
            {
                formatted = GetText(text, 11, _resources.AccentBrush, _labelOverlayBounds.Left - 12);
                width = formatted.Width + 8;
                x = Math.Min(x, Math.Max(2, _labelOverlayBounds.Left - width - 2));
                y = Math.Min(y, Math.Max(2, ActualHeight - formatted.Height - 4));
            }
            else if (_labelOverlayBounds.Top > formatted.Height + 4)
            {
                y = _labelOverlayBounds.Top - formatted.Height - 4;
            }
            else
            {
                y = _labelOverlayBounds.Bottom + 2;
            }
        }
        var badge = new Rect(x, y, width, formatted.Height + 2);
        LastRenderedSelectedLabelBounds = badge;
        context.DrawRoundedRectangle(_resources.StatusBadgeFill, null, badge, 3, 3);
        context.DrawText(formatted, new Point(x + 4, y + 1));
    }

    private void DrawEquipmentSelectedComponentLabel(DrawingContext context, LayoutRenderItem item, SceneViewportProjection projection)
    {
        var pixelScale = Math.Min(ActualWidth / 1000, ActualHeight / 600);
        var geometry = GetEquipmentSceneGeometry(item);
        var point = projection.ToScreen(geometry.X, geometry.Y, geometry.BaseElevation + geometry.VerticalHeight + 22);
        var formatted = GetText($"{item.Item.CurrentName} · {item.Item.Id}", 13 * pixelScale,
            _resources!.AccentBrush, Math.Max(1, ActualWidth - 8), semiBold: true);
        var top = _labelHeaderBounds.IsEmpty ? 4 : Math.Max(4, _labelHeaderBounds.Bottom + 4);
        var bottom = _labelFooterBounds.IsEmpty ? ActualHeight - 4 : Math.Min(ActualHeight - 4, _labelFooterBounds.Top - 4);
        var origin = new Point(
            Math.Clamp(point.X - formatted.Width / 2, 4, Math.Max(4, ActualWidth - formatted.Width - 4)),
            Math.Clamp(point.Y - formatted.Baseline, top, Math.Max(top, bottom - formatted.Height)));
        LastRenderedSelectedLabelBounds = new Rect(origin, new Size(formatted.Width, formatted.Height));
        var outline = new Pen(_resources!.DeviceFill, 3 * pixelScale);
        context.DrawGeometry(null, outline, formatted.BuildGeometry(origin));
        context.DrawText(formatted, origin);
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
        PendingOrbit,
        Orbiting,
        Transforming
    }

    private sealed record LayoutRenderItem(
        LayoutItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        double RotationDegrees,
        double BaseElevation,
        double VerticalHeight,
        bool IsVerticalEnvelopeEstimated,
        bool? IsDetected,
        PneumaticCylinderState? CylinderState,
        double? MotionProgress,
        bool? ConveyorRunning,
        ConveyorDirection? ConveyorDirection,
        string? WorkpieceType,
        WorkpieceInspectionState? InspectionState,
        WaferHandlerOwnershipState? TransferOwnershipState);

    private void DrawCenteredText(DrawingContext context, string text, double fontSize, Brush brush)
    {
        var formatted = GetText(text, fontSize, brush);
        context.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, (ActualHeight - formatted.Height) / 2));
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double fontSize, Brush brush)
    {
        context.DrawText(GetText(text, fontSize, brush), new Point(x, y));
    }

    private FormattedText GetText(string text, double fontSize, Brush brush, double? maxWidth = null, bool semiBold = false)
    {
        var cacheKey = $"{fontSize:F1}|{brush.GetHashCode()}|{maxWidth}|{semiBold}|{text}";
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
        if (maxWidth is { } width) formatted.MaxTextWidth = width;
        if (semiBold) formatted.SetFontWeight(FontWeights.SemiBold);
        _textCache[cacheKey] = formatted;
        return formatted;
    }

    private sealed class SceneRenderResources
    {
        private readonly Dictionary<Brush, Brush> _sideFills = new();

        internal Brush SideFill(Brush fill)
        {
            if (fill is not SolidColorBrush solid) return fill;
            if (_sideFills.TryGetValue(fill, out var cached)) return cached;
            var color = solid.Color;
            var shaded = new SolidColorBrush(Color.FromArgb(color.A, (byte)(color.R * .76), (byte)(color.G * .76), (byte)(color.B * .76)));
            shaded.Freeze();
            _sideFills.Add(fill, shaded);
            return shaded;
        }

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
            SchematicFrameFill = ResolveBrush(owner, "Scene.Component.Frame", "#314F5E");
            AxisComponentFill = ResolveBrush(owner, "Scene.Component.Axis", "#6DC5D6");
            CylinderComponentFill = ResolveBrush(owner, "Scene.Component.Cylinder", "#70D9C5");
            SensorComponentFill = ResolveBrush(owner, "Scene.Component.Sensor", "#F0C76F");
            DigitalIoComponentFill = ResolveBrush(owner, "Scene.Component.DigitalIo", "#E4AA9D");
            CameraComponentFill = ResolveBrush(owner, "Scene.Component.Camera", "#92A4ED");
            ConveyorComponentFill = ResolveBrush(owner, "Scene.Component.Conveyor", "#88A7B6");
            FrameSupportFill = ResolveBrush(owner, "Scene.Component.FrameSupport", "#537687");
            AxisBaseFill = ResolveBrush(owner, "Scene.Component.AxisBase", "#476978");
            CylinderRodFill = ResolveBrush(owner, "Scene.Component.CylinderRod", "#BACBD3");
            CylinderJawFill = ResolveBrush(owner, "Scene.Component.CylinderJaw", "#EAC77D");
            CameraPostFill = ResolveBrush(owner, "Scene.Component.CameraPost", "#688A9B");
            CameraPlateFill = ResolveBrush(owner, "Scene.Component.CameraPlate", "#9CB7C4");
            ConveyorBaseFill = ResolveBrush(owner, "Scene.Component.ConveyorBase", "#375362");
            ConveyorRollerFill = ResolveBrush(owner, "Scene.Component.ConveyorRoller", "#9EB1B9");
            SelectedComponentFill = ResolveBrush(owner, "Scene.Component.Selected", "#B9FFF0");
            var grid = ResolveBrush(owner, "Scene.Grid", "#27313D");
            var obliqueGrid = ResolveBrush(owner, "Scene.ObliqueGrid", "#25414E");
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
            ObliqueGridPen = CreatePen(obliqueGrid, 0.7);
            MajorGridPen = CreatePen(gridMajor, 0.8);
            RailPen = CreatePen(border, 6);
            StructurePen = CreatePen(border, 2);
            SchematicOutlinePen = CreatePen(ResolveBrush(owner, "Scene.Component.Outline", "#06141C"), 1);
            SchematicDetailPen = CreatePen(TextSecondary, 1.1);
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
        public SolidColorBrush SchematicFrameFill { get; }
        public SolidColorBrush AxisComponentFill { get; }
        public SolidColorBrush CylinderComponentFill { get; }
        public SolidColorBrush SensorComponentFill { get; }
        public SolidColorBrush DigitalIoComponentFill { get; }
        public SolidColorBrush CameraComponentFill { get; }
        public SolidColorBrush ConveyorComponentFill { get; }
        public SolidColorBrush FrameSupportFill { get; }
        public SolidColorBrush AxisBaseFill { get; }
        public SolidColorBrush CylinderRodFill { get; }
        public SolidColorBrush CylinderJawFill { get; }
        public SolidColorBrush CameraPostFill { get; }
        public SolidColorBrush CameraPlateFill { get; }
        public SolidColorBrush ConveyorBaseFill { get; }
        public SolidColorBrush ConveyorRollerFill { get; }
        public SolidColorBrush SelectedComponentFill { get; }
        public SolidColorBrush VisionFieldFill { get; }
        public SolidColorBrush FrameFill { get; }
        public SolidColorBrush SensorOnFill { get; }
        public SolidColorBrush SensorOnFieldFill { get; }
        public SolidColorBrush FaultFill { get; }
        public SolidColorBrush StatusBadgeFill { get; }
        public SolidColorBrush MarqueeFill { get; }
        public IReadOnlyDictionary<LayoutItemKind, ImageSource> EquipmentImages { get; }
        public Pen GridPen { get; }
        public Pen ObliqueGridPen { get; }
        public Pen MajorGridPen { get; }
        public Pen RailPen { get; }
        public Pen StructurePen { get; }
        public Pen SchematicOutlinePen { get; }
        public Pen SchematicDetailPen { get; }
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
