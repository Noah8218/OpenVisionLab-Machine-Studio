using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>Owns the stopped-recipe material-settings draft. Applying settings never starts a run.</summary>
public sealed class MaterialFlowSettingsViewModel : ViewModelBase
{
    private readonly Func<MachineProjectDocument> _projectProvider;
    private readonly Func<string?> _unitIdProvider;
    private readonly Func<bool> _canEdit;
    private readonly Action _definitionChanged;
    private readonly Func<Action, bool> _applyChange;
    private readonly Dictionary<string, string> _slotDrafts = new(StringComparer.Ordinal);
    private readonly ProjectDocumentStore _store = new();
    private bool _useTotalFeedCount;
    private string _totalFeedCount = string.Empty;
    private string _okRoute = string.Empty;
    private string _ngRoute = string.Empty;
    private string _baselineJson = string.Empty;
    private bool _isOpen;
    private bool _enabled;
    private string _workpieceName = string.Empty;
    private string _slotName = string.Empty;
    private string _repeatCount = "1";
    private string _validationKey = string.Empty;
    private MachineUnitDefinition? _selectedUnit;
    private string? _projectId;
    private MachineProjectDocument? _openedProject;

    public MaterialFlowSettingsViewModel(Func<MachineProjectDocument> projectProvider, Func<string?> unitIdProvider,
        Func<bool> canEdit, Action definitionChanged, Func<Action, bool>? applyChange = null)
    {
        _projectProvider = projectProvider;
        _unitIdProvider = unitIdProvider;
        _canEdit = canEdit;
        _definitionChanged = definitionChanged;
        _applyChange = applyChange ?? (change => { change(); return true; });
        OpenCommand = new RelayCommand(_ => Open(), _ => _canEdit());
        ApplyCommand = new RelayCommand(_ => Apply(), _ => IsOpen && _canEdit());
        CancelCommand = new RelayCommand(_ => IsOpen = false);
        ResetCommand = new RelayCommand(_ =>
        {
            Enabled = false;
            WorkpieceName = OpenVisionLanguageService.T("MaterialFlow.SampleProduct");
            RepeatCount = "1";
            UseTotalFeedCount = false;
            TotalFeedCount = string.Empty;
            OkRoute = new MaterialFlowDefinition().OkRoute;
            NgRoute = new MaterialFlowDefinition().NgRoute;
            SlotName = DefaultSlotName(SelectedUnit);
            SetValidation(string.Empty);
        });
    }

    public ICommand OpenCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ResetCommand { get; }
    public bool IsOpen
    {
        get => _isOpen;
        private set { if (SetProperty(ref _isOpen, value)) InvalidateCommands(); }
    }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string WorkpieceName { get => _workpieceName; set => SetProperty(ref _workpieceName, value ?? ""); }
    public string SlotName { get => _slotName; set => SetProperty(ref _slotName, value ?? ""); }
    public string RepeatCount { get => _repeatCount; set => SetProperty(ref _repeatCount, value ?? ""); }
    public bool UseTotalFeedCount { get => _useTotalFeedCount; set { if (SetProperty(ref _useTotalFeedCount, value)) OnPropertyChanged(nameof(UsesLegacyRepeats)); } }
    public bool UsesLegacyRepeats => !UseTotalFeedCount;
    public bool CanEdit => _canEdit();
    public string TotalFeedCount { get => _totalFeedCount; set => SetProperty(ref _totalFeedCount, value ?? ""); }
    public string OkRoute { get => _okRoute; set => SetProperty(ref _okRoute, value ?? ""); }
    public string NgRoute { get => _ngRoute; set => SetProperty(ref _ngRoute, value ?? ""); }
    public IReadOnlyList<MaterialPositionSettingsViewModel> Positions { get; private set; } = [];
    public string ValidationText => string.IsNullOrEmpty(_validationKey) ? string.Empty : OpenVisionLanguageService.T(_validationKey);
    public IReadOnlyList<MachineUnitDefinition> Units { get; private set; } = [];
    public MachineUnitDefinition? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (ReferenceEquals(_selectedUnit, value)) return;
            if (_selectedUnit is not null) _slotDrafts[_selectedUnit.Id] = SlotName;
            SetProperty(ref _selectedUnit, value);
            SlotName = value is not null && _slotDrafts.TryGetValue(value.Id, out var draft) ? draft
                : string.IsNullOrWhiteSpace(value?.SlotName) ? DefaultSlotName(value) : value.SlotName;
            RepeatCount = (value?.RepeatCount ?? 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    public void InvalidateCommands()
    {
        ((RelayCommand)OpenCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
    }

    public void RefreshLocalization() => OnPropertyChanged(nameof(ValidationText));

    private static string DefaultSlotName(MachineUnitDefinition? unit) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("MaterialFlow.SlotDefaultFormat"), unit?.Name);

    private void SetValidation(string key)
    {
        _validationKey = key;
        OnPropertyChanged(nameof(ValidationText));
    }

    private void Open()
    {
        if (!_canEdit()) return;
        var project = _projectProvider();
        _projectId = project.Id;
        _openedProject = project;
        _baselineJson = _store.Serialize(project);
        _slotDrafts.Clear();
        Units = project.Stations.SelectMany(station => station.Units).ToArray();
        OnPropertyChanged(nameof(Units));
        _selectedUnit = null;
        SelectedUnit = Units.FirstOrDefault(unit => unit.Id == _unitIdProvider()) ?? Units.FirstOrDefault();
        Enabled = project.MaterialFlow.Enabled;
        WorkpieceName = project.MaterialFlow.WorkpieceName;
        UseTotalFeedCount = project.MaterialFlow.TotalFeedCount is not null;
        TotalFeedCount = project.MaterialFlow.TotalFeedCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        OkRoute = project.MaterialFlow.OkRoute;
        NgRoute = project.MaterialFlow.NgRoute;
        Positions = project.Layouts.SelectMany(layout => layout.Components
            .Where(component => component.Kind is LayoutComponentKind.FeedPosition or LayoutComponentKind.ExitPosition)
            .Select(component => new MaterialPositionSettingsViewModel(layout, component, Units))).ToArray();
        OnPropertyChanged(nameof(Positions));
        SetValidation(string.Empty);
        IsOpen = true;
    }

    private void Apply()
    {
        if (!IsOpen || !_canEdit()) return;
        var project = _projectProvider();
        if (project.Id != _projectId || !ReferenceEquals(project, _openedProject) || _store.Serialize(project) != _baselineJson)
        {
            SetValidation("MaterialFlow.RecipeChanged");
            return;
        }
        int count = 0, total = 0;
        if (string.IsNullOrWhiteSpace(WorkpieceName) || WorkpieceName.Trim().Length > 60
            || !UseTotalFeedCount && (!int.TryParse(RepeatCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count is < 1 or > 100)
            || UseTotalFeedCount && (!Enabled || !int.TryParse(TotalFeedCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out total) || total is < 1 or > 100)
            || SelectedUnit is not null && string.IsNullOrWhiteSpace(SlotName) || SlotName.Trim().Length > 60
            || string.IsNullOrWhiteSpace(OkRoute) || OkRoute.Trim().Length > 60 || string.IsNullOrWhiteSpace(NgRoute) || NgRoute.Trim().Length > 60
            || _slotDrafts.Values.Any(name => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60))
        {
            SetValidation(UseTotalFeedCount ? "MaterialFlow.InvalidTotalSettings" : "MaterialFlow.InvalidSettings");
            return;
        }
        if (Enabled && Units.Count == 0)
        {
            SetValidation("MaterialFlow.UnitRequired");
            return;
        }
        if (Enabled && (Positions.Any(position => !Units.Any(unit => unit.Id == position.UnitId)
            || !position.Conveyors.Any(conveyor => conveyor.Id == position.ConveyorId)
            || position.IsExit && position.Route is not ("auto" or "OK" or "NG"))
            || Positions.GroupBy(position => (position.UnitId, position.IsExit, Route: position.IsExit ? position.Route : null)).Any(group => group.Count() > 1)))
        {
            SetValidation("MaterialFlow.InvalidPositions");
            return;
        }
        if (SelectedUnit is not null) _slotDrafts[SelectedUnit.Id] = SlotName;
        if (!_applyChange(() =>
        {
            project.MaterialFlow.Enabled = Enabled;
            project.MaterialFlow.WorkpieceName = WorkpieceName.Trim();
            project.MaterialFlow.TotalFeedCount = UseTotalFeedCount ? total : null;
            project.MaterialFlow.OkRoute = OkRoute.Trim();
            project.MaterialFlow.NgRoute = NgRoute.Trim();
            foreach (var unit in project.Stations.SelectMany(station => station.Units))
            {
                if (!UseTotalFeedCount) unit.RepeatCount = count;
                if (_slotDrafts.TryGetValue(unit.Id, out var name)) unit.SlotName = name.Trim();
            }
            foreach (var position in Positions)
            {
                var component = project.Layouts.SelectMany(layout => layout.Components).Single(component => component.Id == position.Id);
                component.UnitId = position.UnitId;
                component.MaterialConveyorId = position.ConveyorId;
                component.MaterialRoute = position.IsExit ? position.Route : null;
            }
        }))
        {
            SetValidation("MaterialFlow.RecipeChanged");
            return;
        }
        _definitionChanged();
        IsOpen = false;
    }
}
