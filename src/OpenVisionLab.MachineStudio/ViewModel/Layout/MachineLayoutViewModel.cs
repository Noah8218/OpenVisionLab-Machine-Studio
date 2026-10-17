using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Sequences;
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

public sealed record EquipmentUnitOverviewItem(
    string UnitId,
    string UnitName,
    string StationId,
    string StationName,
    int ComponentCount,
    int ActionCount,
    string CountText);

public sealed record EquipmentOutlinePartItem(LayoutItem Item, string KindText)
{
    public string DisplayName => Item.Component?.Kind == LayoutComponentKind.Workpiece
        ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Equipment.OutlineProductModelName"), Item.CurrentName)
        : Item.CurrentName;
    public string Symbol { get; init; } = "□";
    public bool IsHeightMapCamera { get; init; }
    public string DetailText => $"{Item.Id} / {KindText}";
    public string AutomationName => $"{Item.CurrentName} / {DetailText}";
    public string SelectionAutomationName => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineSelectPart"), Item.Id);
}

public sealed record EquipmentOutlineUnitItem(
    string? UnitId,
    string Name,
    string? StationName,
    int StationComponentCount,
    bool ShowStationHeader,
    int ComponentCount,
    bool IsActive,
    bool ShowUnitId,
    IReadOnlyList<EquipmentOutlinePartItem> Parts)
{
    public string? StationId { get; init; }
    public int ActionCount { get; init; }
    public string DetailText => UnitId is null ? string.Empty : string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineUnitDetail"), UnitId, ComponentCount, ActionCount);
    public bool CanOpenUnit => UnitId is not null;
    public string DisplayName => UnitId is not null && ShowUnitId ? $"{Name} / {UnitId}" : Name;
    public string AutomationItemStatus => IsActive
        ? OpenVisionLanguageService.T("Equipment.OutlineUnitActiveStatus", "현재 유닛", "Current unit")
        : string.Empty;
    public string AutomationName => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineUnitAutomationName", "{0} / 장치 수: {1}", "{0} / component count: {1}"),
        DisplayName, ComponentCount);
    public string RemoveAutomationName => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineRemoveUnitAutomationName", "{0} 유닛 삭제", "Delete unit {0}"),
        DisplayName);
}

public sealed record EquipmentOutlineStationItem(string Id, string Name, bool IsActive, IReadOnlyList<EquipmentOutlineUnitItem> Units)
{
    public string AutomationName => $"{Name} / {Id}";
}

public sealed class MachineLayoutViewModel : ViewModelBase, IDisposable
{
    private readonly ObservableCollection<LayoutItem> _items = new();
    private readonly HashSet<LayoutItem> _itemMembership = new();
    private readonly List<LayoutItem> _selectionOrder = new();
    private readonly RelayCommand _clearLibrarySearchCommand;
    private readonly RelayCommand _clearEquipmentOutlineSearchCommand;
    private readonly RelayCommand _previousEquipmentOutlinePageCommand;
    private readonly RelayCommand _nextEquipmentOutlinePageCommand;
    private readonly Func<bool>? _resolvePendingPlacementDraft;
    private LayoutItem? _selectedItem;
    private LayoutItem? _inspectorItem;
    private LayoutComponentEditorViewModel? _selectedComponentEditor;
    private Machine.Core.Projects.MachineProjectDocument? _project;
    private MachineLayoutDefinition? _definition;
    private string? _activeUnitId;
    private string _librarySearchText = string.Empty;
    private string _equipmentOutlineSearchText = string.Empty;
    private int _equipmentOutlinePageIndex;
    private int _equipmentOutlinePageCount = 1;
    private int _equipmentOutlineMatchCount;
    private bool _isEditable = true;
    private bool _isObliqueView = true;
    private bool _isPlacementMoveTool;
    private bool _isUpdatingSelection;
    private bool _isUpdatingDefinition;
    private bool _disposed;
    private IReadOnlyList<EquipmentUnitOverviewItem> _unitOverviewItems = Array.Empty<EquipmentUnitOverviewItem>();
    private IReadOnlyList<EquipmentOutlineUnitItem> _equipmentOutlineUnits = Array.Empty<EquipmentOutlineUnitItem>();
    private string? _activeStationId;
    private IReadOnlyList<EquipmentOutlineStationItem> _equipmentOutlineStations = Array.Empty<EquipmentOutlineStationItem>();
    private IReadOnlyList<EquipmentOutlinePartItem> _equipmentOutlineProducts = Array.Empty<EquipmentOutlinePartItem>();
    private readonly LayoutSelectionEditingWorkflow _selectionEditingWorkflow = new();

    public MachineLayoutViewModel(Func<bool>? resolvePendingPlacementDraft = null)
    {
        _resolvePendingPlacementDraft = resolvePendingPlacementDraft;
        _clearLibrarySearchCommand = new RelayCommand(
            _ => LibrarySearchText = string.Empty,
            _ => _librarySearchText.Length > 0);
        _clearEquipmentOutlineSearchCommand = new RelayCommand(
            _ => EquipmentOutlineSearchText = string.Empty,
            _ => _equipmentOutlineSearchText.Length > 0);
        ToggleSceneViewCommand = new RelayCommand(_ => IsObliqueView = !IsObliqueView);
        ShowObliqueViewCommand = new RelayCommand(_ => IsObliqueView = true);
        ShowTopViewCommand = new RelayCommand(_ => IsObliqueView = false);
        SelectEquipmentOutlinePartCommand = new RelayCommand(parameter =>
        {
            if (parameter is not LayoutItem item) return;
            Select(item.Id);
            if (SelectedItems.Count == 1 && ReferenceEquals(SelectedItem, item)) ShowOutlinePartUnit(item);
        });
        ToggleEquipmentOutlinePartCommand = new RelayCommand(parameter =>
        {
            if (parameter is not LayoutItem item) return;
            var wasSelected = item.IsSelected;
            ExtendSelection(item, toggle: true);
            if (item.IsSelected != wasSelected) ShowOutlinePartUnit(item);
        });
        _previousEquipmentOutlinePageCommand = new RelayCommand(
            _ => { _equipmentOutlinePageIndex--; RefreshEquipmentOutline(); },
            _ => _equipmentOutlinePageIndex > 0);
        _nextEquipmentOutlinePageCommand = new RelayCommand(
            _ => { _equipmentOutlinePageIndex++; RefreshEquipmentOutline(); },
            _ => _equipmentOutlinePageIndex + 1 < _equipmentOutlinePageCount);
    }

    public ObservableCollection<LayoutItem> Items => _items;
    public IEnumerable<LayoutItem> SceneItems => _activeUnitId is not null
        ? _items.Where(item => string.Equals(item.UnitId, _activeUnitId, StringComparison.Ordinal))
        : _activeStationId is not null
            ? _items.Where(item => _project?.Stations.FirstOrDefault(station => station.Id == _activeStationId)?.Units.Any(unit => unit.Id == item.UnitId) == true)
            : _items;
    public IReadOnlyList<ComponentLibraryItem> LibraryItems { get; private set; } = CreateLibraryItems();
    public IReadOnlyList<ComponentLibraryItem> FilteredLibraryItems => FilterLibraryItems();
    public bool HasNoLibrarySearchResults => FilteredLibraryItems.Count == 0;
    public bool IsLibrarySearchEmpty => _librarySearchText.Length == 0;
    public IReadOnlyList<EquipmentUnitOverviewItem> UnitOverviewItems => _unitOverviewItems;
    public IEnumerable<LayoutItem> ReferenceUnitFrames => (_project?.Stations.SelectMany(station => station.Units)
        ?? Enumerable.Empty<OpenVisionLab.Machine.Core.Projects.MachineUnitDefinition>())
        .Where(unit => _activeUnitId is null || unit.Id == _activeUnitId)
        .Where(unit => _activeStationId is null || _project!.Stations.Any(station => station.Id == _activeStationId && station.Units.Contains(unit)))
        .Where(unit => !_items.Any(item => item.UnitId == unit.Id && item.Kind == LayoutItemKind.MachineFrame))
        .Select((unit, index) =>
        {
            var conveyor = _items.FirstOrDefault(item => item.UnitId == unit.Id && item.Kind == LayoutItemKind.Conveyor);
            double x = unit.BaseOrigin?.X ?? (conveyor?.CurrentX - 2.5 ?? index * 480);
            double depth = unit.BaseOrigin?.Y ?? (conveyor?.CurrentY - 2.5 ?? 0);
            return new LayoutItem(new LayoutComponentDefinition
            {
                Id = "__unit-base-" + unit.Id, Name = unit.Name + " 받침대", UnitId = unit.Id,
                Kind = LayoutComponentKind.MachineFrame, Transform = new Transform2D { X = x + 10, Y = depth + 12.5 },
                Size = new Size2D { Width = 340, Height = 155 },
                VerticalEnvelope = new LayoutVerticalEnvelope { BaseElevation = -105, Height = 92 }, ZIndex = -1000
            }, 1, false);
        });
    public IReadOnlyList<EquipmentOutlineUnitItem> EquipmentOutlineUnits => _equipmentOutlineUnits;
    public IReadOnlyList<EquipmentOutlineStationItem> EquipmentOutlineStations => _equipmentOutlineStations;
    public IReadOnlyList<EquipmentOutlinePartItem> EquipmentOutlineProducts => _equipmentOutlineProducts;
    public IEnumerable<EquipmentOutlinePartItem> EquipmentOutlineUnassignedParts => _equipmentOutlineUnits.Where(unit => unit.UnitId is null).SelectMany(unit => unit.Parts);
    public bool EquipmentOutlineIsEmpty => _items.All(item => item.Component is null);
    public bool IsEquipmentOverviewActive => _activeUnitId is null && _activeStationId is null && !HasSelection;
    public bool HasEquipmentUnitScopeSelection => !HasSelection && _activeUnitId is not null
        && _project?.Stations.SelectMany(station => station.Units).Any(unit => unit.Id == _activeUnitId) == true;
    public bool HasEquipmentStationScopeSelection => !HasSelection && _activeUnitId is null && _activeStationId is not null
        && _project?.Stations.Any(station => station.Id == _activeStationId) == true;
    public bool HasEquipmentScopeSelection => HasEquipmentUnitScopeSelection || HasEquipmentStationScopeSelection;
    public string EquipmentScopeName => HasEquipmentScopeSelection ? SceneTitleText : string.Empty;
    public string EquipmentScopeId => HasEquipmentUnitScopeSelection ? _activeUnitId! : HasEquipmentStationScopeSelection ? _activeStationId! : string.Empty;
    public string EquipmentScopeSymbol => HasEquipmentUnitScopeSelection ? "▣" : "▤";
    public string EquipmentScopeNameCaption => HasEquipmentUnitScopeSelection
        ? OpenVisionLanguageService.T("R24.Scope.UnitName", "유닛 이름", "Unit name")
        : OpenVisionLanguageService.T("R24.Scope.StationName", "스테이션 이름", "Station name");
    public string EquipmentScopeStationCaption => OpenVisionLanguageService.T("R24.Scope.Station", "스테이션", "Station");
    public string EquipmentScopeStationName => _project?.Stations.FirstOrDefault(station => station.Units.Any(unit => unit.Id == _activeUnitId))?.Name ?? string.Empty;
    public string EquipmentScopeHint => OpenVisionLanguageService.T("R24.Scope.UnitHint", "유닛 안의 스텝은 순서대로, 유닛끼리는 동시에 진행합니다.", "Steps within a unit run in order; units run in parallel.");
    public string EquipmentUnitSequenceActionText => OpenVisionLanguageService.T("R24.Scope.EditSequence", "시퀀스 편집 →", "Edit sequence →");
    public string EquipmentUnitDeleteActionText => OpenVisionLanguageService.T("R24.Scope.DeleteUnit", "유닛 삭제…", "Delete unit…");
    public string EquipmentScopeStepCaption => OpenVisionLanguageService.T("R24.Scope.Steps", "스텝", "Steps");
    public string EquipmentScopeDeviceCountText => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Count"),
        _items.Count(item => item.Component is not null && item.Component.Kind != LayoutComponentKind.Workpiece && item.UnitId == _activeUnitId));
    public string EquipmentScopeStepCountText => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Count"),
        _project?.Sequences.Where(sequence => sequence.UnitId == _activeUnitId).Sum(sequence => sequence.Steps.Count) ?? 0);
    public bool HasEquipmentPartSelection => HasSelection && SelectedItem?.Kind != LayoutItemKind.Workpiece;
    public string EquipmentOverviewUnitCountText => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Count"), EquipmentOverviewUnitCount);
    public string EquipmentOverviewDeviceCountText => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Count"), EquipmentOverviewDeviceCount);
    public int EquipmentOverviewUnitCount => _unitOverviewItems.Count;
    public int EquipmentOverviewDeviceCount => _items.Count(item => item.Component is not null && item.Component.Kind != LayoutComponentKind.Workpiece);
    public string EquipmentOutlineOverviewDetail => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineEquipmentDetail"), _unitOverviewItems.Count,
        _items.Count(item => item.Component is not null && item.Component.Kind != LayoutComponentKind.Workpiece));
    public bool EquipmentOutlineHasNoMatches => !string.IsNullOrWhiteSpace(_equipmentOutlineSearchText) && _equipmentOutlineMatchCount == 0;
    public bool EquipmentOutlineHasPages => _equipmentOutlinePageCount > 1;
    public bool IsEquipmentOutlineSearchEmpty => _equipmentOutlineSearchText.Length == 0;
    public string EquipmentOutlinePageText => $"{_equipmentOutlinePageIndex + 1} / {_equipmentOutlinePageCount}";
    public string EquipmentOutlineCountText => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineCount", "{0}개", "{0} parts"),
        _items.Count(item => item.Component is not null));
    public string EquipmentOutlineSelectionText => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.OutlineSelection", "{0}개 선택", "{0} selected"), SelectionCount);

    public MachineLayoutDefinition? Definition => _definition;
    public string? ActiveUnitId => _activeUnitId;
    public string? ActiveStationId => _activeStationId;
    public bool IsUnitView => _activeUnitId is not null;
    public bool IsLargeOverview => _activeUnitId is null && _activeStationId is null && _items.Count > 100;
    public string LargeOverviewTitleText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(
            "Equipment.LargeOverviewTitle",
            "전체 장비 / {0}개 유닛",
            "Whole machine / {0} units"),
        _unitOverviewItems.Count);
    public string SceneTitleText => _activeUnitId is null
        ? _project?.Stations.FirstOrDefault(station => station.Id == _activeStationId)?.Name ?? _definition?.Name ?? "Layout"
        : _project?.Stations
            .SelectMany(station => station.Units)
            .FirstOrDefault(unit => string.Equals(unit.Id, _activeUnitId, StringComparison.Ordinal))?.Name
            ?? _activeUnitId;
    public string EquipmentSceneTitleText => $"{(_activeUnitId is null && _activeStationId is null ? OpenVisionLanguageService.T("Equipment.OverviewButton") : SceneTitleText)} · {OpenVisionLanguageService.T(IsObliqueView ? "Scene.ViewOblique" : "Scene.ViewPlan")}";
    public string EquipmentScopeContextText
    {
        get
        {
            if (_project is null)
            {
                return string.Empty;
            }

            if (_activeUnitId is null)
            {
                return _project.Stations.FirstOrDefault(station => station.Id == _activeStationId)?.Name
                    ?? OpenVisionLanguageService.T("Equipment.OverviewButton", "전체 장비", "Whole machine");
            }

            foreach (var station in _project.Stations)
            {
                var unit = station.Units.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, _activeUnitId, StringComparison.Ordinal));
                if (unit is not null)
                {
                    return $"{station.Name} / {unit.Name}";
                }
            }

            return _activeUnitId;
        }
    }
    public LayoutItem? SelectedItem
    {
        get => _selectedItem;
        set => SetSelection(value is null ? Array.Empty<LayoutItem>() : new[] { value }, value);
    }

    public IReadOnlyList<LayoutItem> SelectedItems => _selectionOrder.ToArray();
    public int SelectionCount => _items.Count(item => item.IsSelected);
    public bool HasSelection => SelectionCount > 0;
    public bool HasMultipleSelection => SelectionCount > 1;
    public string EquipmentSelectionNameText => SelectionCount == 1 ? SelectedItem?.CurrentName ?? string.Empty : SelectionSummaryText;
    public string EquipmentSelectionIdText => SelectionCount == 1 ? SelectedItem?.Id ?? string.Empty : string.Empty;
    public string EquipmentSelectionDetailText
    {
        get
        {
            if (SelectionCount != 1 || SelectedItem?.Component is not { } component) return string.Empty;
            var heightMap = component.Kind == LayoutComponentKind.Camera && _project?.Devices.FirstOrDefault(device => device.Id == component.BehaviorBindingId)?.Camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D;
            var kind = component.Kind == LayoutComponentKind.Camera
                ? OpenVisionLanguageService.T(heightMap ? "Equipment.OutlineCamera3D" : "Equipment.OutlineCamera2D")
                : OpenVisionLanguageService.T(component.Kind == LayoutComponentKind.DigitalSensor ? "Scene.EquipmentLegendSensor" : $"Properties.Value.{component.Kind}", component.Kind.ToString(), component.Kind.ToString());
            return $"{component.Id} · {kind}";
        }
    }
    public IReadOnlyList<(string SequenceId, string StepId)> EquipmentSelectionStepReferences
    {
        get
        {
            var ids = SelectedItems.SelectMany(item => new[] { item.Id, item.BehaviorBindingId })
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToHashSet(StringComparer.Ordinal);
            foreach (var item in SelectedItems.Where(item => item.Component is not null && !string.IsNullOrWhiteSpace(item.BehaviorBindingId)))
            {
                var devices = _project?.Devices.Where(device => string.Equals(device.Id, item.BehaviorBindingId, StringComparison.Ordinal)).Take(2).ToArray() ?? [];
                if (devices.Length != 1) continue;
                foreach (var channelId in devices[0].ChannelIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    if (_project!.Devices.Count(device => device.ChannelIds.Contains(channelId, StringComparer.Ordinal)) == 1) ids.Add(channelId);
                }
            }
            return _project?.Sequences.SelectMany(sequence => sequence.Steps.Where(step =>
                    ReferencesOwnedItem(step.TargetId, ids) || ReferencesOwnedItem(step.WorkpieceComponentId, ids) || ReferencesOwnedItem(step.ExpectedTargetId, ids))
                .Select(step => (sequence.Id, step.Id))).ToArray() ?? [];
        }
    }
    public string EquipmentSelectionStepsText => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Scene.EquipmentSteps"), EquipmentSelectionStepReferences.Count);

    public string SelectionSummaryText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(
            "Inspector.SelectedComponents",
            "{0}개 장비 선택",
            "{0} components selected"),
        SelectionCount);
    public LayoutComponentEditorViewModel? SelectedComponentEditor => _selectedComponentEditor;

    public string LibrarySearchText
    {
        get => _librarySearchText;
        set
        {
            if (SetProperty(ref _librarySearchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(FilteredLibraryItems));
                OnPropertyChanged(nameof(HasNoLibrarySearchResults));
                OnPropertyChanged(nameof(IsLibrarySearchEmpty));
                _clearLibrarySearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EquipmentOutlineSearchText
    {
        get => _equipmentOutlineSearchText;
        set
        {
            if (!SetProperty(ref _equipmentOutlineSearchText, value ?? string.Empty)) return;
            _equipmentOutlinePageIndex = 0;
            OnPropertyChanged(nameof(IsEquipmentOutlineSearchEmpty));
            RefreshEquipmentOutline();
            _clearEquipmentOutlineSearchCommand.RaiseCanExecuteChanged();
        }
    }

    public ICommand SelectEquipmentOutlinePartCommand { get; }
    public ICommand ToggleEquipmentOutlinePartCommand { get; }
    public ICommand ClearLibrarySearchCommand => _clearLibrarySearchCommand;
    public ICommand ClearEquipmentOutlineSearchCommand => _clearEquipmentOutlineSearchCommand;
    public ICommand PreviousEquipmentOutlinePageCommand => _previousEquipmentOutlinePageCommand;
    public ICommand NextEquipmentOutlinePageCommand => _nextEquipmentOutlinePageCommand;

    public ICommand ToggleSceneViewCommand { get; }
    public ICommand ShowObliqueViewCommand { get; }
    public ICommand ShowTopViewCommand { get; }

    public bool IsPlacementMoveTool
    {
        get => _isPlacementMoveTool;
        set
        {
            if (!SetProperty(ref _isPlacementMoveTool, value)) return;
            OnPropertyChanged(nameof(IsPlacementSelectionTool));
        }
    }

    public bool IsPlacementSelectionTool
    {
        get => !IsPlacementMoveTool;
        set { if (value) IsPlacementMoveTool = false; }
    }

    public bool IsObliqueView
    {
        get => _isObliqueView;
        set
        {
            if (SetProperty(ref _isObliqueView, value))
            {
                OnPropertyChanged(nameof(IsTopView));
                OnPropertyChanged(nameof(SceneViewToggleText));
                OnPropertyChanged(nameof(EquipmentSceneTitleText));
            }
        }
    }

    public bool IsTopView => !IsObliqueView;

    public string SceneViewToggleText => OpenVisionLanguageService.T(
        IsObliqueView ? "Scene.ViewPlan" : "Scene.ViewOblique",
        IsObliqueView ? "평면 보기" : "사선 3D 보기",
        IsObliqueView ? "Top view" : "Oblique 3D view");

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
        ThrowIfDisposed();
        CancelSelectionDrag();
        CancelSelectionTransform();
        DetachItemHandlers();

        ArgumentNullException.ThrowIfNull(project);
        var keepActiveUnit = ReferenceEquals(_project, project)
            && _activeUnitId is not null
            && project.Stations.SelectMany(station => station.Units)
                .Any(unit => string.Equals(unit.Id, _activeUnitId, StringComparison.Ordinal));
        var keepActiveStation = ReferenceEquals(_project, project)
            && project.Stations.Any(station => station.Id == _activeStationId);
        _project = project;
        if (!keepActiveStation) _activeStationId = null;
        if (!keepActiveUnit)
        {
            _activeUnitId = null;
        }
        SelectedItem = null;
        _itemMembership.Clear();
        _items.Clear();
        _definition = ResolveDefinition(project);

        if (_definition is not null)
        {
            foreach (var component in _definition.Components.OrderBy(item => item.ZIndex).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                var item = new LayoutItem(component, _definition.GridSize, _definition.SnapToGrid);
                item.DefinitionChanged += OnItemDefinitionChanged;
                item.PropertyChanged += OnItemPropertyChanged;
                _itemMembership.Add(item);
                _items.Add(item);
            }

            OnPropertyChanged(nameof(Definition));
            OnPropertyChanged(nameof(GridSize));
            OnPropertyChanged(nameof(LayoutTitleText));
            RebuildUnitOverviewItems();
            NotifySceneScopeChanged();
            return;
        }

        if (project.Layouts.Count == 0)
        {
            foreach (var axis in project.Axes)
            {
                var item = new LayoutItem(axis.Id, axis.Name, LayoutItemKind.Axis, axis.Position, axis);
                item.PropertyChanged += OnItemPropertyChanged;
                _itemMembership.Add(item);
                _items.Add(item);
            }

            foreach (var device in project.Devices)
            {
                var item = new LayoutItem(device.Id, device.Name, LayoutItemKind.Device, device.MountPosition, device);
                item.PropertyChanged += OnItemPropertyChanged;
                _itemMembership.Add(item);
                _items.Add(item);
            }
        }

        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(GridSize));
        OnPropertyChanged(nameof(LayoutTitleText));
        RebuildUnitOverviewItems();
        NotifySceneScopeChanged();
    }

    public void ShowUnit(string unitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        if (string.Equals(_activeUnitId, unitId, StringComparison.Ordinal))
        {
            return;
        }

        if (!TryResolvePendingPlacementDraft()) return;

        _activeStationId = null;
        _activeUnitId = unitId;
        NotifySceneScopeChanged();
    }

    public void ShowStation(string stationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        if (_project?.Stations.Any(station => station.Id == stationId) != true || _activeStationId == stationId) return;
        if (!TryResolvePendingPlacementDraft()) return;
        _activeUnitId = null;
        _activeStationId = stationId;
        NotifySceneScopeChanged();
    }

    public void ShowOverview()
    {
        if (_activeUnitId is null && _activeStationId is null)
        {
            return;
        }

        if (!TryResolvePendingPlacementDraft()) return;

        _activeUnitId = null;
        _activeStationId = null;
        NotifySceneScopeChanged();
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
        var itemsById = _items.ToLookup(item => item.Id, StringComparer.Ordinal);
        var selected = componentIds.Distinct(StringComparer.Ordinal)
            .SelectMany(id => itemsById[id])
            .ToArray();
        var primary = selected.FirstOrDefault(item => string.Equals(
                item.Id,
                primaryComponentId,
                StringComparison.Ordinal))
            ?? selected.LastOrDefault();
        SetSelection(selected, primary, resetOrder: true);
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
        var targets = items.Where(_itemMembership.Contains).Distinct().ToArray();
        if (mode == LayoutSelectionMode.Replace)
        {
            SetSelection(targets, targets.LastOrDefault(), resetOrder: true);
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

    public bool MoveSelectionBy(double deltaX, double deltaY)
    {
        var changed = ApplyDefinitionUpdate(() => _selectionEditingWorkflow.MoveSelectionBy(
            SelectedItems,
            deltaX,
            deltaY,
            IsEditable));
        if (changed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    internal bool ApplySelectedPlacementDraft(LayoutItem item, LayoutPlacementDraft draft)
    {
        if (!IsEditable || !ReferenceEquals(_inspectorItem, item) || item.Component is null)
        {
            return false;
        }

        var changed = item.CurrentName != draft.Name || item.CurrentX != draft.PositionX || item.CurrentY != draft.PositionDepth ||
            item.CurrentVerticalBaseElevation != draft.BaseElevation || item.CurrentRotationDegrees != draft.RotationDegrees ||
            item.CurrentWidth != draft.FootprintWidth || item.CurrentHeight != draft.FootprintDepth ||
            draft.ApplyVerticalHeight && item.VerticalHeight != draft.VerticalHeight || draft.ApplyUnitAssignment && item.CurrentUnitId != draft.UnitId;
        if (!changed)
        {
            return true;
        }

        ApplyDefinitionUpdate(() =>
        {
            item.CurrentName = draft.Name;
            if (draft.ApplyUnitAssignment) item.CurrentUnitId = draft.UnitId;
            item.SetCurrentX(draft.PositionX, snapToGrid: false);
            item.SetCurrentY(draft.PositionDepth, snapToGrid: false);
            item.CurrentRotationDegrees = draft.RotationDegrees;
            item.CurrentWidth = draft.FootprintWidth;
            item.CurrentHeight = draft.FootprintDepth;
            item.CurrentVerticalBaseElevation = draft.BaseElevation;
            if (draft.ApplyVerticalHeight) item.CurrentVerticalHeight = draft.VerticalHeight;
        });
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool ApplySelectedAxisDriveDraft(LayoutItem item, VirtualAxisDefinition axis, double min, double max, double home, double speed)
    {
        if (!IsEditable || !ReferenceEquals(_inspectorItem, item) || item.Component is null ||
            _project?.Axes.Contains(axis) != true || item.CurrentBehaviorBindingId != axis.Id)
        {
            return false;
        }

        if (axis.SoftLimitMin == min && axis.SoftLimitMax == max && axis.HomePosition == home && axis.MaxVelocity == speed)
        {
            return true;
        }

        ApplyDefinitionUpdate(() =>
        {
            axis.SoftLimitMin = min;
            axis.SoftLimitMax = max;
            axis.HomePosition = home;
            axis.MaxVelocity = speed;
        });
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ApplySelectedSettingsDraft(LayoutItem item, Action update)
    {
        if (!IsEditable || _disposed || !ReferenceEquals(_inspectorItem, item)) return false;
        ApplyDefinitionUpdate(update);
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
        return true;
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

    public void RefreshSequenceReferences()
    {
        OnPropertyChanged(nameof(EquipmentSelectionStepReferences));
        OnPropertyChanged(nameof(EquipmentSelectionStepsText));
    }

    public void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        LibraryItems = CreateLibraryItems();
        OnPropertyChanged(nameof(LibraryItems));
        OnPropertyChanged(nameof(FilteredLibraryItems));
        OnPropertyChanged(nameof(HasNoLibrarySearchResults));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(EquipmentSelectionNameText));
        OnPropertyChanged(nameof(EquipmentSelectionIdText));
        OnPropertyChanged(nameof(EquipmentSelectionDetailText));
        RefreshSequenceReferences();
        OnPropertyChanged(nameof(EquipmentOutlineCountText));
        OnPropertyChanged(nameof(EquipmentOutlineSelectionText));
        OnPropertyChanged(nameof(EquipmentScopeContextText));
        OnPropertyChanged(nameof(SceneViewToggleText));
        OnPropertyChanged(nameof(EquipmentSceneTitleText));
        NotifyEquipmentScopeChanged();
        RebuildUnitOverviewItems();
        SelectedComponentEditor?.RefreshLocalization();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSelectionDrag();
        CancelSelectionTransform();
        DetachItemHandlers();
        _selectedComponentEditor?.Dispose();
        _selectedComponentEditor = null;
        _inspectorItem = null;
        _selectionOrder.Clear();
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
                OpenVisionLanguageService.T("Layout.Library.Workpiece.Description")),
            new ComponentLibraryItem(LayoutComponentKind.FeedPosition, OpenVisionLanguageService.T("MaterialFlow.FeedPosition"),
                OpenVisionLanguageService.T("Layout.Library.Category.Material"), OpenVisionLanguageService.T("MaterialFlow.FeedPositionHint")),
            new ComponentLibraryItem(LayoutComponentKind.ExitPosition, OpenVisionLanguageService.T("MaterialFlow.ExitPosition"),
                OpenVisionLanguageService.T("Layout.Library.Category.Material"), OpenVisionLanguageService.T("MaterialFlow.ExitPositionHint")),
            new ComponentLibraryItem(
                LayoutComponentKind.DigitalIo,
                OpenVisionLanguageService.T("Properties.Value.DigitalIo"),
                OpenVisionLanguageService.T("Layout.Library.Category.Sensors"),
                OpenVisionLanguageService.T("Layout.Library.DigitalIo.Description")),
            new ComponentLibraryItem(
                LayoutComponentKind.Camera,
                OpenVisionLanguageService.T("Layout.Library.Camera.Name"),
                OpenVisionLanguageService.T("Layout.Library.Category.Vision"),
                OpenVisionLanguageService.T("Layout.Library.Camera.Description"))
        };

    private IReadOnlyList<ComponentLibraryItem> FilterLibraryItems()
    {
        var searchText = _librarySearchText.Trim();
        if (searchText.Length == 0)
        {
            return LibraryItems;
        }

        return LibraryItems.Where(item =>
                (item.Kind == LayoutComponentKind.LinearStage
                    && "axis".Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                item.Kind.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.Category.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.Description.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
    }

    private void SetSelection(IEnumerable<LayoutItem> items, LayoutItem? primary, bool resetOrder = false)
    {
        ThrowIfDisposed();
        var requested = items.Where(_itemMembership.Contains).Distinct().ToArray();
        var selected = requested.ToHashSet();
        if (primary is not null && !selected.Contains(primary))
        {
            primary = null;
        }

        if ((!selected.SetEquals(SelectedItems) || !ReferenceEquals(primary, _selectedItem)) &&
            !TryResolvePendingPlacementDraft()) return;

        if (resetOrder)
        {
            _selectionOrder.Clear();
        }
        _selectionOrder.RemoveAll(item => !selected.Contains(item));
        foreach (var item in requested)
        {
            if (!_selectionOrder.Contains(item))
            {
                _selectionOrder.Add(item);
            }
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

    private bool TryResolvePendingPlacementDraft()
    {
        var editor = _selectedComponentEditor;
        return editor is null || !editor.HasPendingInspectorDraft ||
            (_resolvePendingPlacementDraft?.Invoke() == true && !editor.HasPendingInspectorDraft);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (args.PropertyName is nameof(LayoutItem.CurrentUnitId) or nameof(LayoutItem.UnitId))
        {
            OnPropertyChanged(nameof(SceneItems));
            RebuildUnitOverviewItems();
        }
        else if (args.PropertyName is nameof(LayoutItem.CurrentName) or nameof(LayoutItem.Name))
        {
            RefreshEquipmentOutline();
            OnPropertyChanged(nameof(EquipmentSelectionNameText));
        }

        if (_isUpdatingSelection || args.PropertyName != nameof(LayoutItem.IsSelected))
        {
            return;
        }

        var changedItem = sender as LayoutItem;
        if (changedItem?.IsSelected == true && !_selectionOrder.Contains(changedItem))
        {
            _selectionOrder.Add(changedItem);
        }
        else if (changedItem?.IsSelected == false)
        {
            _selectionOrder.Remove(changedItem);
        }
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
        if (_disposed)
        {
            return;
        }

        SetProperty(ref _selectedItem, value, nameof(SelectedItem));
    }

    private void RefreshSelectedComponentEditor()
    {
        var first = _selectionOrder.FirstOrDefault(item => item.Component is not null);
        if (ReferenceEquals(_inspectorItem, first))
        {
            return;
        }

        _selectedComponentEditor?.Dispose();
        _inspectorItem = first;
        _selectedComponentEditor = first?.Component is not null && _project is not null && _definition is not null
            ? new LayoutComponentEditorViewModel(
                _project,
                _definition,
                first,
                OnSelectedComponentDefinitionChanged,
                () => IsEditable && !_disposed && ReferenceEquals(_inspectorItem, first),
                ApplySelectedPlacementDraft,
                ApplySelectedAxisDriveDraft,
                update => ApplySelectedSettingsDraft(first, update))
            : null;
        OnPropertyChanged(nameof(SelectedComponentEditor));
    }

    private void NotifyEquipmentScopeChanged()
    {
        OnPropertyChanged(nameof(HasEquipmentUnitScopeSelection));
        OnPropertyChanged(nameof(HasEquipmentStationScopeSelection));
        OnPropertyChanged(nameof(HasEquipmentScopeSelection));
        OnPropertyChanged(nameof(EquipmentScopeName));
        OnPropertyChanged(nameof(EquipmentScopeId));
        OnPropertyChanged(nameof(EquipmentScopeSymbol));
        OnPropertyChanged(nameof(EquipmentScopeNameCaption));
        OnPropertyChanged(nameof(EquipmentScopeStationCaption));
        OnPropertyChanged(nameof(EquipmentScopeStationName));
        OnPropertyChanged(nameof(EquipmentScopeHint));
        OnPropertyChanged(nameof(EquipmentUnitSequenceActionText));
        OnPropertyChanged(nameof(EquipmentUnitDeleteActionText));
        OnPropertyChanged(nameof(EquipmentScopeStepCaption));
        OnPropertyChanged(nameof(EquipmentScopeDeviceCountText));
        OnPropertyChanged(nameof(EquipmentScopeStepCountText));
    }

    private void NotifySelectionChanged()
    {
        RefreshSelectedComponentEditor();
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasEquipmentPartSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(EquipmentSelectionNameText));
        OnPropertyChanged(nameof(EquipmentSelectionIdText));
        OnPropertyChanged(nameof(EquipmentSelectionDetailText));
        RefreshSequenceReferences();
        OnPropertyChanged(nameof(EquipmentOutlineSelectionText));
        OnPropertyChanged(nameof(IsEquipmentOverviewActive));
        NotifyEquipmentScopeChanged();
    }

    private void NotifySceneScopeChanged()
    {
        OnPropertyChanged(nameof(ActiveUnitId));
        OnPropertyChanged(nameof(ActiveStationId));
        OnPropertyChanged(nameof(IsEquipmentOverviewActive));
        NotifyEquipmentScopeChanged();
        OnPropertyChanged(nameof(IsUnitView));
        OnPropertyChanged(nameof(IsLargeOverview));
        OnPropertyChanged(nameof(SceneItems));
        OnPropertyChanged(nameof(SceneTitleText));
        OnPropertyChanged(nameof(EquipmentSceneTitleText));
        OnPropertyChanged(nameof(EquipmentScopeContextText));
        OnPropertyChanged(nameof(LargeOverviewTitleText));
        RefreshEquipmentOutline();
    }

    private void RebuildUnitOverviewItems()
    {
        if (_project is null)
        {
            _unitOverviewItems = Array.Empty<EquipmentUnitOverviewItem>();
        }
        else
        {
            var steps = _project.Sequences.SelectMany(sequence => sequence.Steps).ToArray();
            _unitOverviewItems = _project.Stations
                .SelectMany(station => station.Units.Select(unit =>
                {
                    var components = _items
                        .Where(item => string.Equals(item.UnitId, unit.Id, StringComparison.Ordinal))
                        .ToArray();
                    var ownedIds = new HashSet<string>(components.Select(component => component.Id), StringComparer.Ordinal);
                    foreach (var bindingId in components.Select(component => component.Component?.BehaviorBindingId)
                                 .Where(bindingId => !string.IsNullOrWhiteSpace(bindingId)))
                    {
                        ownedIds.Add(bindingId!);
                    }

                    var unitSequence = _project.Sequences.FirstOrDefault(sequence => sequence.UnitId == unit.Id);
                    var actionCount = unitSequence is not null ? unitSequence.Steps.Count(step => step.Action is not (SequenceStepAction.Complete or SequenceStepAction.None)) : steps.Count(step =>
                        ReferencesOwnedItem(step.TargetId, ownedIds)
                        || ReferencesOwnedItem(step.WorkpieceComponentId, ownedIds)
                        || ReferencesOwnedItem(step.ExpectedTargetId, ownedIds));
                    var countText = string.Format(
                        CultureInfo.CurrentCulture,
                        OpenVisionLanguageService.T(
                            "Equipment.LargeOverviewCount",
                            "{0}개 부품 / {1}개 동작",
                            "{0} components / {1} actions"),
                        components.Length,
                        actionCount);
                    return new EquipmentUnitOverviewItem(
                        unit.Id,
                        unit.Name,
                        station.Id,
                        station.Name,
                        components.Length,
                        actionCount,
                        countText);
                }))
                .ToArray();
        }

        OnPropertyChanged(nameof(UnitOverviewItems));
        OnPropertyChanged(nameof(ReferenceUnitFrames));
        OnPropertyChanged(nameof(LargeOverviewTitleText));
        RefreshEquipmentOutline();
    }

    private void ShowOutlinePartUnit(LayoutItem item)
    {
        var unitId = item.UnitId;
        if (unitId is not null && _project?.Stations.SelectMany(station => station.Units)
                .Any(unit => string.Equals(unit.Id, unitId, StringComparison.Ordinal)) == true)
        {
            ShowUnit(unitId);
        }
        else
        {
            ShowOverview();
        }
    }

    private void RefreshEquipmentOutline()
    {
        var query = _equipmentOutlineSearchText.Trim();
        var unitLookup = _unitOverviewItems.ToDictionary(unit => unit.UnitId, StringComparer.Ordinal);
        var componentOrder = new Dictionary<LayoutComponentDefinition, int>();
        if (_definition is not null)
            for (var index = 0; index < _definition.Components.Count; index++) componentOrder.TryAdd(_definition.Components[index], index);
        var parts = _items.Where(item => item.Component is not null).OrderBy(item => componentOrder.GetValueOrDefault(item.Component!, int.MaxValue)).Select(item =>
        {
            var kind = item.Component!.Kind;
            var heightMap = kind == LayoutComponentKind.Camera && _project?.Devices.FirstOrDefault(device => device.Id == item.Component.BehaviorBindingId)?.Camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D;
            var kindText = kind switch
            {
                LayoutComponentKind.Camera => OpenVisionLanguageService.T(heightMap ? "Equipment.OutlineCamera3D" : "Equipment.OutlineCamera2D"),
                LayoutComponentKind.DigitalSensor => OpenVisionLanguageService.T("Properties.Sensor"),
                LayoutComponentKind.PneumaticCylinder => OpenVisionLanguageService.T("Properties.Cylinder"),
                _ => OpenVisionLanguageService.T($"Properties.Value.{kind}", kind.ToString(), kind.ToString())
            };
            var libraryItem = LibraryItems.FirstOrDefault(candidate => candidate.Kind == kind);
            if (kind == LayoutComponentKind.Camera && libraryItem is not null)
                libraryItem = libraryItem with { CameraInputKind = heightMap ? NumericalCameraInputKind.HeightMap3D : NumericalCameraInputKind.Image2D };
            return new EquipmentOutlinePartItem(item, kindText)
            {
                Symbol = kind == LayoutComponentKind.Workpiece ? "◆" : libraryItem?.Symbol ?? "□",
                IsHeightMapCamera = heightMap
            };
        }).ToArray();
        var matches = parts.Where(part => query.Length == 0
            || MatchesEquipmentQuery(query, part.Item.CurrentName, part.Item.Id, part.KindText, part.Item.Component!.Kind.ToString(),
                OpenVisionLanguageService.T($"Properties.Value.{part.Item.Component.Kind}", part.Item.Component.Kind.ToString(), part.Item.Component.Kind.ToString()))
            || (part.Item.Component.Kind == LayoutComponentKind.LinearStage && "axis".Contains(query, StringComparison.OrdinalIgnoreCase))
            || (part.Item.UnitId is { } id && unitLookup.TryGetValue(id, out var unit)
                && MatchesEquipmentQuery(query, unit.UnitName, unit.UnitId, unit.StationName, unit.StationId))).ToArray();
        var matchedGroups = query.Length == 0 ? 0 : _unitOverviewItems.Count(unit => MatchesEquipmentQuery(query, unit.UnitName, unit.UnitId))
            + (_project?.Stations.Count(station => MatchesEquipmentQuery(query, station.Name, station.Id)) ?? 0);
        _equipmentOutlineMatchCount = matches.Length + matchedGroups;
        _equipmentOutlinePageCount = Math.Max(1, (matches.Length + 59) / 60);
        _equipmentOutlinePageIndex = Math.Clamp(_equipmentOutlinePageIndex, 0, _equipmentOutlinePageCount - 1);
        var visible = matches.Skip(_equipmentOutlinePageIndex * 60).Take(60).ToArray();
        _equipmentOutlineProducts = visible.Where(part => part.Item.Component!.Kind == LayoutComponentKind.Workpiece).ToArray();
        var devices = parts.Where(part => part.Item.Component!.Kind != LayoutComponentKind.Workpiece).ToArray();
        var visibleDevices = visible.Where(part => part.Item.Component!.Kind != LayoutComponentKind.Workpiece).ToArray();
        var stationCounts = _unitOverviewItems.GroupBy(unit => unit.StationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => devices.Count(part => group.Any(unit => unit.UnitId == part.Item.UnitId)), StringComparer.Ordinal);
        var duplicateUnitNames = _unitOverviewItems.GroupBy(unit => unit.UnitName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var units = new List<EquipmentOutlineUnitItem>();
        string? previousStationId = null;
        foreach (var unit in _unitOverviewItems)
        {
            var unitParts = visibleDevices.Where(part => part.Item.UnitId == unit.UnitId).ToArray();
            if (query.Length > 0 && unitParts.Length == 0 && !visible.Any(part => part.Item.UnitId == unit.UnitId)
                && !MatchesEquipmentQuery(query, unit.UnitName, unit.UnitId, unit.StationName, unit.StationId)) continue;
            units.Add(new EquipmentOutlineUnitItem(unit.UnitId, unit.UnitName, unit.StationName,
                stationCounts[unit.StationId], previousStationId != unit.StationId,
                devices.Count(part => part.Item.UnitId == unit.UnitId), unit.UnitId == _activeUnitId,
                duplicateUnitNames.Contains(unit.UnitName), unitParts) { StationId = unit.StationId, ActionCount = unit.ActionCount });
            previousStationId = unit.StationId;
        }
        var unassigned = devices.Where(part => part.Item.UnitId is null || !unitLookup.ContainsKey(part.Item.UnitId)).ToArray();
        var visibleUnassigned = visibleDevices.Where(part => part.Item.UnitId is null || !unitLookup.ContainsKey(part.Item.UnitId)).ToArray();
        if (unassigned.Length > 0 && (query.Length == 0 || visibleUnassigned.Length > 0))
            units.Add(new EquipmentOutlineUnitItem(null, OpenVisionLanguageService.T("Equipment.OutlineUnassigned"),
                null, 0, false, unassigned.Length, false, false, visibleUnassigned));
        _equipmentOutlineUnits = units;
        _equipmentOutlineStations = (_project?.Stations ?? []).Where(station => query.Length == 0
            || units.Any(unit => unit.StationId == station.Id) || MatchesEquipmentQuery(query, station.Name, station.Id))
            .Select(station => new EquipmentOutlineStationItem(station.Id, station.Name, station.Id == _activeStationId,
                units.Where(unit => unit.StationId == station.Id).ToArray())).ToArray();
        OnPropertyChanged(nameof(EquipmentOutlineUnits));
        OnPropertyChanged(nameof(EquipmentOutlineStations));
        OnPropertyChanged(nameof(EquipmentOutlineProducts));
        OnPropertyChanged(nameof(EquipmentOutlineUnassignedParts));
        OnPropertyChanged(nameof(EquipmentOutlineIsEmpty));
        OnPropertyChanged(nameof(EquipmentOutlineOverviewDetail));
        OnPropertyChanged(nameof(EquipmentOverviewUnitCount));
        OnPropertyChanged(nameof(EquipmentOverviewUnitCountText));
        OnPropertyChanged(nameof(EquipmentOverviewDeviceCount));
        OnPropertyChanged(nameof(EquipmentOverviewDeviceCountText));
        OnPropertyChanged(nameof(IsEquipmentOverviewActive));
        NotifyEquipmentScopeChanged();
        OnPropertyChanged(nameof(EquipmentOutlineHasNoMatches));
        OnPropertyChanged(nameof(EquipmentOutlineHasPages));
        OnPropertyChanged(nameof(EquipmentOutlinePageText));
        OnPropertyChanged(nameof(EquipmentOutlineCountText));
        _previousEquipmentOutlinePageCommand.RaiseCanExecuteChanged();
        _nextEquipmentOutlinePageCommand.RaiseCanExecuteChanged();
    }

    private static bool MatchesEquipmentQuery(string query, params string?[] values) =>
        values.Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);

    private static bool ReferencesOwnedItem(string? candidateId, HashSet<string> ownedIds) =>
        !string.IsNullOrWhiteSpace(candidateId) && ownedIds.Contains(candidateId);

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
        if (!_disposed && !_isUpdatingDefinition)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSelectedComponentDefinitionChanged()
    {
        if (!_disposed)
        {
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DetachItemHandlers()
    {
        foreach (var item in _items)
        {
            item.DefinitionChanged -= OnItemDefinitionChanged;
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MachineLayoutViewModel));
        }
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
