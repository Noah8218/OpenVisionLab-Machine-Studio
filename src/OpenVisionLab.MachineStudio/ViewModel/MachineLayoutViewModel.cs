using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public enum LayoutSelectionAlignment
{
    Left,
    HorizontalCenter,
    Right,
    Top,
    VerticalCenter,
    Bottom
}

public enum LayoutLayerOrder
{
    SendToBack,
    SendBackward,
    BringForward,
    BringToFront
}

public enum LayoutSelectionMode
{
    Replace,
    Add,
    Toggle
}

public enum LayoutTransformHandle
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
    Rotation
}

public sealed class MachineLayoutViewModel : ViewModelBase
{
    private readonly ObservableCollection<LayoutItem> _items = new();
    private LayoutItem? _selectedItem;
    private LayoutComponentEditorViewModel? _selectedComponentEditor;
    private Machine.Core.Projects.MachineProjectDocument? _project;
    private MachineLayoutDefinition? _definition;
    private bool _isEditable = true;
    private bool _isUpdatingSelection;
    private bool _isUpdatingDefinition;
    private IReadOnlyDictionary<LayoutItem, (double X, double Y)>? _dragStartPositions;
    private LayoutTransformStart? _transformStart;

    public ObservableCollection<LayoutItem> Items => _items;
    public IReadOnlyList<ComponentLibraryItem> LibraryItems { get; private set; } = CreateLibraryItems();

    public MachineLayoutDefinition? Definition => _definition;

    public LayoutItem? SelectedItem
    {
        get => _selectedItem;
        set => SetSelection(value is null ? Array.Empty<LayoutItem>() : new[] { value }, value);
    }

    public IReadOnlyList<LayoutItem> SelectedItems => _items.Where(item => item.IsSelected).ToArray();
    public int SelectionCount => _items.Count(item => item.IsSelected);
    public bool HasSelection => SelectionCount > 0;
    public bool HasMultipleSelection => SelectionCount > 1;
    public string SelectionSummaryText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(
            "Inspector.SelectedComponents",
            "{0}개 장비 선택",
            "{0} components selected"),
        SelectionCount);
    public LayoutComponentEditorViewModel? SelectedComponentEditor => _selectedComponentEditor;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!value)
            {
                CancelSelectionDrag();
                CancelSelectionTransform();
            }
            SetProperty(ref _isEditable, value);
        }
    }

    public double GridSize => _definition?.GridSize ?? 10.0;

    public string LayoutTitleText => _definition is null
        ? "Layout"
        : $"Layout · {_definition.Name}";

    public event EventHandler? DefinitionChanged;

    public void Load(Machine.Core.Projects.MachineProjectDocument project)
    {
        CancelSelectionDrag();
        CancelSelectionTransform();
        foreach (var item in _items)
        {
            item.DefinitionChanged -= OnItemDefinitionChanged;
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _project = project ?? throw new ArgumentNullException(nameof(project));
        SelectedItem = null;
        _items.Clear();
        _definition = ResolveDefinition(project);

        if (_definition is not null)
        {
            foreach (var component in _definition.Components.OrderBy(item => item.ZIndex).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                var item = new LayoutItem(component, _definition.GridSize, _definition.SnapToGrid);
                item.DefinitionChanged += OnItemDefinitionChanged;
                item.PropertyChanged += OnItemPropertyChanged;
                _items.Add(item);
            }

            OnPropertyChanged(nameof(Definition));
            OnPropertyChanged(nameof(GridSize));
            OnPropertyChanged(nameof(LayoutTitleText));
            return;
        }

        if (project.Layouts.Count == 0)
        {
            foreach (var axis in project.Axes)
            {
                var item = new LayoutItem(axis.Id, axis.Name, LayoutItemKind.Axis, axis.Position, axis);
                item.PropertyChanged += OnItemPropertyChanged;
                _items.Add(item);
            }

            foreach (var device in project.Devices)
            {
                var item = new LayoutItem(device.Id, device.Name, LayoutItemKind.Device, device.MountPosition, device);
                item.PropertyChanged += OnItemPropertyChanged;
                _items.Add(item);
            }
        }

        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(GridSize));
        OnPropertyChanged(nameof(LayoutTitleText));
    }

    public void Select(string componentId)
    {
        var item = _items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, componentId, StringComparison.Ordinal));
        SetSelection(item is null ? Array.Empty<LayoutItem>() : new[] { item }, item);
    }

    public void SelectMany(IEnumerable<string> componentIds, string? primaryComponentId = null)
    {
        ArgumentNullException.ThrowIfNull(componentIds);
        var ids = componentIds.ToHashSet(StringComparer.Ordinal);
        var selected = _items.Where(item => ids.Contains(item.Id)).ToArray();
        var primary = selected.FirstOrDefault(item => string.Equals(
                item.Id,
                primaryComponentId,
                StringComparison.Ordinal))
            ?? selected.LastOrDefault();
        SetSelection(selected, primary);
    }

    public void ExtendSelection(LayoutItem? item, bool toggle)
    {
        if (item is null)
        {
            return;
        }

        var selected = SelectedItems.ToList();
        if (toggle && item.IsSelected)
        {
            selected.Remove(item);
            var primary = _selectedItem is not null && selected.Contains(_selectedItem)
                ? _selectedItem
                : selected.LastOrDefault();
            SetSelection(selected, primary);
            return;
        }

        if (!selected.Contains(item))
        {
            selected.Add(item);
        }
        SetSelection(selected, item);
    }

    public void SelectRegion(IEnumerable<LayoutItem> items, LayoutSelectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(items);
        var targets = items.Where(item => _items.Contains(item)).Distinct().ToArray();
        if (mode == LayoutSelectionMode.Replace)
        {
            SetSelection(targets, targets.LastOrDefault());
            return;
        }

        var selected = SelectedItems.ToList();
        foreach (var item in targets)
        {
            if (mode == LayoutSelectionMode.Toggle && selected.Remove(item))
            {
                continue;
            }
            if (!selected.Contains(item))
            {
                selected.Add(item);
            }
        }

        var primary = targets.LastOrDefault(selected.Contains)
            ?? (_selectedItem is not null && selected.Contains(_selectedItem) ? _selectedItem : selected.LastOrDefault());
        SetSelection(selected, primary);
    }

    public bool BeginSelectionDrag()
    {
        if (!IsEditable || _dragStartPositions is not null || _transformStart is not null)
        {
            return false;
        }

        var selected = SelectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length == 0)
        {
            return false;
        }

        _dragStartPositions = selected.ToDictionary(item => item, item => (item.CurrentX, item.CurrentY));
        return true;
    }

    public bool UpdateSelectionDrag(double deltaX, double deltaY)
    {
        if (_dragStartPositions is null || _dragStartPositions.Count == 0)
        {
            return false;
        }

        var primary = SelectedItem is not null && _dragStartPositions.ContainsKey(SelectedItem)
            ? SelectedItem
            : _dragStartPositions.Keys.First();
        var primaryStart = _dragStartPositions[primary];
        var appliedX = Definition?.SnapToGrid == false
            ? deltaX
            : SnapCoordinate(primaryStart.X + deltaX) - primaryStart.X;
        var appliedY = Definition?.SnapToGrid == false
            ? deltaY
            : SnapCoordinate(primaryStart.Y + deltaY) - primaryStart.Y;

        _isUpdatingDefinition = true;
        try
        {
            foreach (var (item, start) in _dragStartPositions)
            {
                item.SetCurrentX(start.X + appliedX, snapToGrid: false);
                item.SetCurrentY(start.Y + appliedY, snapToGrid: false);
            }
        }
        finally
        {
            _isUpdatingDefinition = false;
        }
        return true;
    }

    public bool CompleteSelectionDrag()
    {
        if (_dragStartPositions is null)
        {
            return false;
        }

        var changed = _dragStartPositions.Any(entry =>
            entry.Key.CurrentX != entry.Value.X || entry.Key.CurrentY != entry.Value.Y);
        _dragStartPositions = null;
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public void CancelSelectionDrag()
    {
        if (_dragStartPositions is null)
        {
            return;
        }

        _isUpdatingDefinition = true;
        try
        {
            foreach (var (item, start) in _dragStartPositions)
            {
                item.SetCurrentX(start.X, snapToGrid: false);
                item.SetCurrentY(start.Y, snapToGrid: false);
            }
        }
        finally
        {
            _isUpdatingDefinition = false;
            _dragStartPositions = null;
        }
    }

    public bool BeginSelectionTransform(LayoutTransformHandle handle)
    {
        if (!IsEditable || _dragStartPositions is not null || _transformStart is not null)
        {
            return false;
        }

        var selected = SelectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length == 0)
        {
            return false;
        }

        var items = selected.Select(item => new LayoutTransformItemStart(
            item,
            item.CurrentX,
            item.CurrentY,
            item.CurrentWidth,
            item.CurrentHeight,
            item.CurrentRotationDegrees)).ToArray();
        var bounds = GetTransformBounds(items);
        _transformStart = new LayoutTransformStart(
            items,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            handle);
        return true;
    }

    public bool UpdateSelectionTransform(
        double pointerX,
        double pointerY,
        bool preserveAspectRatio = false)
    {
        if (_transformStart is not { } start ||
            !double.IsFinite(pointerX) ||
            !double.IsFinite(pointerY))
        {
            return false;
        }

        if (start.Items.Count == 1)
        {
            return UpdateSingleSelectionTransform(start, pointerX, pointerY, preserveAspectRatio);
        }

        return start.Handle == LayoutTransformHandle.Rotation
            ? UpdateGroupRotation(start, pointerX, pointerY)
            : UpdateGroupResize(start, pointerX, pointerY, preserveAspectRatio);
    }

    private bool UpdateSingleSelectionTransform(
        LayoutTransformStart start,
        double pointerX,
        double pointerY,
        bool preserveAspectRatio)
    {
        var item = start.Items[0];
        if (start.Handle == LayoutTransformHandle.Rotation)
        {
            var deltaX = pointerX - item.X;
            var deltaY = pointerY - item.Y;
            if (Math.Abs(deltaX) < 0.000001d && Math.Abs(deltaY) < 0.000001d)
            {
                return false;
            }

            SetTransformValues(
                item.Item,
                item.X,
                item.Y,
                item.Width,
                item.Height,
                NormalizeRotation((Math.Atan2(deltaY, deltaX) * 180d / Math.PI) + 90d));
            return true;
        }

        var (signX, signY) = GetResizeSigns(start.Handle);
        var radians = item.RotationDegrees * Math.PI / 180d;
        var axisXX = Math.Cos(radians);
        var axisXY = Math.Sin(radians);
        var axisYX = -axisXY;
        var axisYY = axisXX;
        var fixedX = item.X - (signX * item.Width * axisXX / 2d) -
            (signY * item.Height * axisYX / 2d);
        var fixedY = item.Y - (signX * item.Width * axisXY / 2d) -
            (signY * item.Height * axisYY / 2d);
        var pointerDeltaX = pointerX - fixedX;
        var pointerDeltaY = pointerY - fixedY;
        var width = signX * ((pointerDeltaX * axisXX) + (pointerDeltaY * axisXY));
        var height = signY * ((pointerDeltaX * axisYX) + (pointerDeltaY * axisYY));
        var minimumSize = Definition?.SnapToGrid == false ? 1d : GridSize;
        if (preserveAspectRatio)
        {
            (width, height) = ConstrainAspectRatio(
                width,
                height,
                item.Width,
                item.Height,
                Math.Max(minimumSize / item.Width, minimumSize / item.Height));
        }
        else if (Definition?.SnapToGrid != false)
        {
            width = SnapCoordinate(width);
            height = SnapCoordinate(height);
        }
        width = Math.Max(minimumSize, width);
        height = Math.Max(minimumSize, height);

        SetTransformValues(
            item.Item,
            fixedX + (signX * width * axisXX / 2d) + (signY * height * axisYX / 2d),
            fixedY + (signX * width * axisXY / 2d) + (signY * height * axisYY / 2d),
            width,
            height,
            item.RotationDegrees);
        return true;
    }

    private bool UpdateGroupResize(
        LayoutTransformStart start,
        double pointerX,
        double pointerY,
        bool preserveAspectRatio)
    {
        var (signX, signY) = GetResizeSigns(start.Handle);
        var fixedX = start.X - (signX * start.Width / 2d);
        var fixedY = start.Y - (signY * start.Height / 2d);
        var width = signX * (pointerX - fixedX);
        var height = signY * (pointerY - fixedY);
        var minimumSize = Definition?.SnapToGrid == false ? 1d : GridSize;
        var minimumWidth = start.Width * start.Items.Max(item => minimumSize / item.Width);
        var minimumHeight = start.Height * start.Items.Max(item => minimumSize / item.Height);
        if (preserveAspectRatio)
        {
            (width, height) = ConstrainAspectRatio(
                width,
                height,
                start.Width,
                start.Height,
                Math.Max(minimumWidth / start.Width, minimumHeight / start.Height));
        }
        else
        {
            if (Definition?.SnapToGrid != false)
            {
                width = SnapCoordinate(width);
                height = SnapCoordinate(height);
            }
            width = Math.Max(minimumWidth, width);
            height = Math.Max(minimumHeight, height);
        }
        var scaleX = width / start.Width;
        var scaleY = height / start.Height;

        SetTransformValues(start.Items.Select(item => new LayoutTransformValue(
            item.Item,
            fixedX + ((item.X - fixedX) * scaleX),
            fixedY + ((item.Y - fixedY) * scaleY),
            item.Width * scaleX,
            item.Height * scaleY,
            item.RotationDegrees)));
        return true;
    }

    private (double Width, double Height) ConstrainAspectRatio(
        double candidateWidth,
        double candidateHeight,
        double initialWidth,
        double initialHeight,
        double minimumScale)
    {
        var scaleX = Math.Max(minimumScale, candidateWidth / initialWidth);
        var scaleY = Math.Max(minimumScale, candidateHeight / initialHeight);
        var useWidth = Math.Abs(scaleX - 1d) >= Math.Abs(scaleY - 1d);
        var scale = useWidth ? scaleX : scaleY;
        if (Definition?.SnapToGrid != false)
        {
            var initialPrimarySize = useWidth ? initialWidth : initialHeight;
            scale = Math.Max(
                minimumScale,
                SnapCoordinate(initialPrimarySize * scale) / initialPrimarySize);
        }
        return (initialWidth * scale, initialHeight * scale);
    }

    private bool UpdateGroupRotation(
        LayoutTransformStart start,
        double pointerX,
        double pointerY)
    {
        var deltaX = pointerX - start.X;
        var deltaY = pointerY - start.Y;
        if (Math.Abs(deltaX) < 0.000001d && Math.Abs(deltaY) < 0.000001d)
        {
            return false;
        }

        var rotation = NormalizeRotation(
            (Math.Atan2(deltaY, deltaX) * 180d / Math.PI) + 90d);
        var radians = rotation * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        SetTransformValues(start.Items.Select(item =>
        {
            var x = item.X - start.X;
            var y = item.Y - start.Y;
            return new LayoutTransformValue(
                item.Item,
                start.X + (x * cosine) - (y * sine),
                start.Y + (x * sine) + (y * cosine),
                item.Width,
                item.Height,
                NormalizeRotation(item.RotationDegrees + rotation));
        }));
        return true;
    }

    public bool CompleteSelectionTransform()
    {
        if (_transformStart is not { } start)
        {
            return false;
        }

        var changed = start.Items.Any(item =>
            item.Item.CurrentX != item.X ||
            item.Item.CurrentY != item.Y ||
            item.Item.CurrentWidth != item.Width ||
            item.Item.CurrentHeight != item.Height ||
            item.Item.CurrentRotationDegrees != item.RotationDegrees);
        _transformStart = null;
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public void CancelSelectionTransform()
    {
        if (_transformStart is not { } start)
        {
            return;
        }

        SetTransformValues(start.Items.Select(item => new LayoutTransformValue(
            item.Item,
            item.X,
            item.Y,
            item.Width,
            item.Height,
            item.RotationDegrees)));
        _transformStart = null;
    }

    public bool NudgeSelection(string direction)
    {
        var step = Definition?.SnapToGrid == false ? 1d : GridSize;
        return direction switch
        {
            "Left" => MoveSelection(-step, 0),
            "Right" => MoveSelection(step, 0),
            "Up" => MoveSelection(0, -step),
            "Down" => MoveSelection(0, step),
            _ => false
        };
    }

    public bool AlignSelection(LayoutSelectionAlignment alignment)
    {
        var selected = SelectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length < 2 || SelectedItem is not { Component: not null } primary)
        {
            return false;
        }

        var (primaryHalfWidth, primaryHalfHeight) = GetRotatedHalfExtents(primary);
        var anchor = alignment switch
        {
            LayoutSelectionAlignment.Left => primary.CurrentX - primaryHalfWidth,
            LayoutSelectionAlignment.HorizontalCenter => primary.CurrentX,
            LayoutSelectionAlignment.Right => primary.CurrentX + primaryHalfWidth,
            LayoutSelectionAlignment.Top => primary.CurrentY - primaryHalfHeight,
            LayoutSelectionAlignment.VerticalCenter => primary.CurrentY,
            LayoutSelectionAlignment.Bottom => primary.CurrentY + primaryHalfHeight,
            _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, null)
        };

        var changed = false;
        _isUpdatingDefinition = true;
        try
        {
            foreach (var item in selected)
            {
                var (halfWidth, halfHeight) = GetRotatedHalfExtents(item);
                switch (alignment)
                {
                    case LayoutSelectionAlignment.Left:
                        changed |= SetX(item, anchor + halfWidth);
                        break;
                    case LayoutSelectionAlignment.HorizontalCenter:
                        changed |= SetX(item, anchor);
                        break;
                    case LayoutSelectionAlignment.Right:
                        changed |= SetX(item, anchor - halfWidth);
                        break;
                    case LayoutSelectionAlignment.Top:
                        changed |= SetY(item, anchor + halfHeight);
                        break;
                    case LayoutSelectionAlignment.VerticalCenter:
                        changed |= SetY(item, anchor);
                        break;
                    case LayoutSelectionAlignment.Bottom:
                        changed |= SetY(item, anchor - halfHeight);
                        break;
                }
            }
        }
        finally
        {
            _isUpdatingDefinition = false;
        }

        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public bool CanChangeSelectionLayerOrder(LayoutLayerOrder order)
    {
        var (items, selected) = GetLayerOrderState();
        if (selected.Count == 0 || selected.Count == items.Count)
        {
            return false;
        }

        return order switch
        {
            LayoutLayerOrder.SendToBack or LayoutLayerOrder.SendBackward =>
                items.TakeWhile(selected.Contains).Count() != selected.Count,
            LayoutLayerOrder.BringForward or LayoutLayerOrder.BringToFront =>
                items.AsEnumerable().Reverse().TakeWhile(selected.Contains).Count() != selected.Count,
            _ => false
        };
    }

    public bool ChangeSelectionLayerOrder(LayoutLayerOrder order)
    {
        if (!CanChangeSelectionLayerOrder(order))
        {
            return false;
        }

        var (items, selected) = GetLayerOrderState();
        switch (order)
        {
            case LayoutLayerOrder.SendToBack:
                items = items.Where(selected.Contains).Concat(items.Where(item => !selected.Contains(item))).ToList();
                break;
            case LayoutLayerOrder.BringToFront:
                items = items.Where(item => !selected.Contains(item)).Concat(items.Where(selected.Contains)).ToList();
                break;
            case LayoutLayerOrder.SendBackward:
                for (var index = 1; index < items.Count; index++)
                {
                    if (selected.Contains(items[index]) && !selected.Contains(items[index - 1]))
                    {
                        (items[index - 1], items[index]) = (items[index], items[index - 1]);
                    }
                }
                break;
            case LayoutLayerOrder.BringForward:
                for (var index = items.Count - 2; index >= 0; index--)
                {
                    if (selected.Contains(items[index]) && !selected.Contains(items[index + 1]))
                    {
                        (items[index], items[index + 1]) = (items[index + 1], items[index]);
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order, null);
        }

        _isUpdatingDefinition = true;
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                items[index].SetZIndex(index);
            }
        }
        finally
        {
            _isUpdatingDefinition = false;
        }

        DefinitionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RefreshLocalization()
    {
        LibraryItems = CreateLibraryItems();
        OnPropertyChanged(nameof(LibraryItems));
        OnPropertyChanged(nameof(SelectionSummaryText));
        SelectedComponentEditor?.RefreshLocalization();
    }

    private static IReadOnlyList<ComponentLibraryItem> CreateLibraryItems() =>
        new[]
        {
            new ComponentLibraryItem(
                LayoutComponentKind.MachineFrame,
                OpenVisionLanguageService.T("Layout.Library.MachineFrame.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Mechanics"),
                OpenVisionLanguageService.T("Layout.Library.MachineFrame.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.LinearStage,
                OpenVisionLanguageService.T("Layout.Library.LinearStage.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Motion"),
                OpenVisionLanguageService.T("Layout.Library.LinearStage.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.RotaryStage,
                OpenVisionLanguageService.T("Layout.Library.RotaryStage.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Motion"),
                OpenVisionLanguageService.T("Layout.Library.RotaryStage.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.DigitalSensor,
                OpenVisionLanguageService.T("Layout.Library.DigitalSensor.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Sensors"),
                OpenVisionLanguageService.T("Layout.Library.DigitalSensor.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.PneumaticCylinder,
                OpenVisionLanguageService.T("Layout.Library.PneumaticCylinder.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Actuators"),
                OpenVisionLanguageService.T("Layout.Library.PneumaticCylinder.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.Conveyor,
                OpenVisionLanguageService.T("Layout.Library.Conveyor.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Transport"),
                OpenVisionLanguageService.T("Layout.Library.Conveyor.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.Workpiece,
                OpenVisionLanguageService.T("Layout.Library.Workpiece.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Material"),
                OpenVisionLanguageService.T("Layout.Library.Workpiece.Description"))
        };

    private void SetSelection(IEnumerable<LayoutItem> items, LayoutItem? primary)
    {
        var selected = items.Where(item => _items.Contains(item)).ToHashSet();
        if (primary is not null && !selected.Contains(primary))
        {
            primary = null;
        }

        _isUpdatingSelection = true;
        try
        {
            foreach (var item in _items)
            {
                item.IsSelected = selected.Contains(item);
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        SetPrimarySelection(primary ?? _items.LastOrDefault(selected.Contains));
        NotifySelectionChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_isUpdatingSelection || args.PropertyName != nameof(LayoutItem.IsSelected))
        {
            return;
        }

        var changedItem = sender as LayoutItem;
        var primary = changedItem?.IsSelected == true
            ? changedItem
            : _selectedItem?.IsSelected == true
                ? _selectedItem
                : _items.LastOrDefault(item => item.IsSelected);
        SetPrimarySelection(primary);
        NotifySelectionChanged();
    }

    private void SetPrimarySelection(LayoutItem? value)
    {
        if (!SetProperty(ref _selectedItem, value, nameof(SelectedItem)))
        {
            return;
        }

        _selectedComponentEditor?.Dispose();
        _selectedComponentEditor = value?.Component is not null && _project is not null && _definition is not null
            ? new LayoutComponentEditorViewModel(
                _project,
                _definition,
                value,
                OnSelectedComponentDefinitionChanged)
            : null;
        OnPropertyChanged(nameof(SelectedComponentEditor));
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
    }

    private bool MoveSelection(double deltaX, double deltaY)
    {
        var selected = SelectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length == 0)
        {
            return false;
        }

        _isUpdatingDefinition = true;
        try
        {
            foreach (var item in selected)
            {
                item.SetCurrentX(item.CurrentX + deltaX, snapToGrid: false);
                item.SetCurrentY(item.CurrentY + deltaY, snapToGrid: false);
            }
        }
        finally
        {
            _isUpdatingDefinition = false;
        }

        DefinitionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private (List<LayoutItem> Items, HashSet<LayoutItem> Selected) GetLayerOrderState()
    {
        var items = _items
            .Where(item => item.Component is not null)
            .OrderBy(item => item.ZIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        return (items, SelectedItems.Where(item => item.Component is not null).ToHashSet());
    }

    private double SnapCoordinate(double value) =>
        Math.Round(value / GridSize, MidpointRounding.AwayFromZero) * GridSize;

    private void SetTransformValues(
        LayoutItem item,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        _isUpdatingDefinition = true;
        try
        {
            item.SetCurrentX(x, snapToGrid: false);
            item.SetCurrentY(y, snapToGrid: false);
            item.CurrentWidth = width;
            item.CurrentHeight = height;
            item.CurrentRotationDegrees = rotationDegrees;
        }
        finally
        {
            _isUpdatingDefinition = false;
        }
    }

    private void SetTransformValues(IEnumerable<LayoutTransformValue> values)
    {
        _isUpdatingDefinition = true;
        try
        {
            foreach (var value in values)
            {
                value.Item.SetCurrentX(value.X, snapToGrid: false);
                value.Item.SetCurrentY(value.Y, snapToGrid: false);
                value.Item.CurrentWidth = value.Width;
                value.Item.CurrentHeight = value.Height;
                value.Item.CurrentRotationDegrees = value.RotationDegrees;
            }
        }
        finally
        {
            _isUpdatingDefinition = false;
        }
    }

    private static (double SignX, double SignY) GetResizeSigns(LayoutTransformHandle handle) => handle switch
    {
        LayoutTransformHandle.TopLeft => (-1d, -1d),
        LayoutTransformHandle.TopRight => (1d, -1d),
        LayoutTransformHandle.BottomRight => (1d, 1d),
        LayoutTransformHandle.BottomLeft => (-1d, 1d),
        _ => throw new ArgumentOutOfRangeException(nameof(handle), handle, null)
    };

    private static double NormalizeRotation(double degrees)
    {
        var normalized = ((degrees % 360d) + 360d) % 360d;
        return normalized >= 180d ? normalized - 360d : normalized;
    }

    private static (double HalfWidth, double HalfHeight) GetRotatedHalfExtents(LayoutItem item)
        => GetRotatedHalfExtents(item.Width, item.Height, item.RotationDegrees);

    private static (double HalfWidth, double HalfHeight) GetRotatedHalfExtents(
        double width,
        double height,
        double rotationDegrees)
    {
        var radians = rotationDegrees * Math.PI / 180d;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        return (
            ((width * cosine) + (height * sine)) / 2d,
            ((width * sine) + (height * cosine)) / 2d);
    }

    private static (double X, double Y, double Width, double Height) GetTransformBounds(
        IReadOnlyList<LayoutTransformItemStart> items)
    {
        var minimumX = double.PositiveInfinity;
        var minimumY = double.PositiveInfinity;
        var maximumX = double.NegativeInfinity;
        var maximumY = double.NegativeInfinity;
        foreach (var item in items)
        {
            var (halfWidth, halfHeight) = GetRotatedHalfExtents(
                item.Width,
                item.Height,
                item.RotationDegrees);
            minimumX = Math.Min(minimumX, item.X - halfWidth);
            minimumY = Math.Min(minimumY, item.Y - halfHeight);
            maximumX = Math.Max(maximumX, item.X + halfWidth);
            maximumY = Math.Max(maximumY, item.Y + halfHeight);
        }
        return (
            (minimumX + maximumX) / 2d,
            (minimumY + maximumY) / 2d,
            maximumX - minimumX,
            maximumY - minimumY);
    }

    private static bool SetX(LayoutItem item, double value)
    {
        if (item.CurrentX == value)
        {
            return false;
        }
        item.SetCurrentX(value, snapToGrid: false);
        return true;
    }

    private static bool SetY(LayoutItem item, double value)
    {
        if (item.CurrentY == value)
        {
            return false;
        }
        item.SetCurrentY(value, snapToGrid: false);
        return true;
    }

    private void OnItemDefinitionChanged(object? sender, EventArgs args)
    {
        if (!_isUpdatingDefinition)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSelectedComponentDefinitionChanged()
    {
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static MachineLayoutDefinition? ResolveDefinition(
        Machine.Core.Projects.MachineProjectDocument project)
    {
        var activeLayoutId = project.Simulation.ActiveLayoutId;
        if (!string.IsNullOrWhiteSpace(activeLayoutId))
        {
            return project.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, activeLayoutId, StringComparison.Ordinal));
        }

        return project.Layouts.Count == 1 ? project.Layouts[0] : null;
    }

    private sealed record LayoutTransformItemStart(
        LayoutItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        double RotationDegrees);

    private sealed record LayoutTransformStart(
        IReadOnlyList<LayoutTransformItemStart> Items,
        double X,
        double Y,
        double Width,
        double Height,
        LayoutTransformHandle Handle);

    private sealed record LayoutTransformValue(
        LayoutItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        double RotationDegrees);
}
