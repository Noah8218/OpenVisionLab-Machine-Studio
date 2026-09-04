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
    private readonly LayoutSelectionEditingWorkflow _selectionEditingWorkflow = new();

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

    public bool BeginSelectionDrag() =>
        _selectionEditingWorkflow.BeginSelectionDrag(SelectedItems, IsEditable);

    public bool UpdateSelectionDrag(double deltaX, double deltaY) =>
        ApplyDefinitionUpdate(() => _selectionEditingWorkflow.UpdateSelectionDrag(
            deltaX,
            deltaY,
            SelectedItem,
            Definition?.SnapToGrid != false,
            GridSize));

    public bool CompleteSelectionDrag()
    {
        var changed = _selectionEditingWorkflow.CompleteSelectionDrag();
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public void CancelSelectionDrag() =>
        ApplyDefinitionUpdate(_selectionEditingWorkflow.CancelSelectionDrag);

    public bool BeginSelectionTransform(LayoutTransformHandle handle) =>
        _selectionEditingWorkflow.BeginSelectionTransform(SelectedItems, handle, IsEditable);

    public bool UpdateSelectionTransform(
        double pointerX,
        double pointerY,
        bool preserveAspectRatio = false) =>
        ApplyDefinitionUpdate(() => _selectionEditingWorkflow.UpdateSelectionTransform(
            pointerX,
            pointerY,
            Definition?.SnapToGrid != false,
            GridSize,
            preserveAspectRatio));

    public bool CompleteSelectionTransform()
    {
        var changed = _selectionEditingWorkflow.CompleteSelectionTransform();
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public void CancelSelectionTransform() =>
        ApplyDefinitionUpdate(_selectionEditingWorkflow.CancelSelectionTransform);

    public bool NudgeSelection(string direction)
    {
        var changed = ApplyDefinitionUpdate(() => _selectionEditingWorkflow.NudgeSelection(
            SelectedItems,
            direction,
            Definition?.SnapToGrid != false,
            GridSize));
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public bool AlignSelection(LayoutSelectionAlignment alignment)
    {
        var changed = ApplyDefinitionUpdate(() => _selectionEditingWorkflow.AlignSelection(
            SelectedItems,
            SelectedItem,
            alignment));
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public bool CanChangeSelectionLayerOrder(LayoutLayerOrder order) =>
        _selectionEditingWorkflow.CanChangeSelectionLayerOrder(Items, SelectedItems, order);

    public bool ChangeSelectionLayerOrder(LayoutLayerOrder order)
    {
        var changed = ApplyDefinitionUpdate(() => _selectionEditingWorkflow.ChangeSelectionLayerOrder(
            Items,
            SelectedItems,
            order));
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
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

    private T ApplyDefinitionUpdate<T>(Func<T> update)
    {
        _isUpdatingDefinition = true;
        try
        {
            return update();
        }
        finally
        {
            _isUpdatingDefinition = false;
        }
    }

    private void ApplyDefinitionUpdate(Action update)
    {
        _isUpdatingDefinition = true;
        try
        {
            update();
        }
        finally
        {
            _isUpdatingDefinition = false;
        }
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

}
