using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.Model;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SequenceEditorChangedEventArgs(bool StructureChanged);

public sealed record SequenceExpectedStateTarget(
    string Id,
    string Name,
    IReadOnlyList<string> States);

public sealed record SequenceValidationIssue(
    SequenceCompilationErrorCode Code,
    string SequenceId,
    string? StepId,
    string? TargetId,
    string PropertyName,
    string Message)
{
    public string DisplayText => $"{Code} [{StepId ?? "sequence"}]: {Message}";
    public string LocationText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Sequence.ValidationIssueLocationFormat"),
        SequenceId,
        StepId ?? "sequence",
        TargetId ?? "—",
        PropertyName);
}

public sealed class SequenceEditorViewModel : ViewModelBase
{
    private readonly SequenceDefinitionEditor _editor = new();
    private readonly SequenceStepTemplateCatalog _templateCatalog = new();
    private readonly SequenceAuthoringTargetCatalog _targetCatalog = new();
    private readonly SequenceStepEditorCollection _stepEditors;
    private readonly RelayCommand _findNextStepCommand;
    private readonly RelayCommand _clearStepSearchCommand;
    private string _stepSearchText = string.Empty;
    private int _stepSearchMatchCount;
    private int _nextStepSearchIndex;
    private int _processViewIndex;
    private bool _isProcessSceneVisible;
    private string? _taskSequenceId;
    private readonly Dictionary<string, HashSet<string>> _expandedTaskGroups = new(StringComparer.Ordinal);
    private readonly RelayCommand _selectOverviewStepCommand;
    private readonly RelayCommand _changeFlowZoomCommand;
    private readonly RelayCommand _beginRouteDestinationSelectionCommand;
    private readonly RelayCommand _cancelRouteDestinationSelectionCommand;
    private string? _routeDestinationProperty;
    private bool _isProcessAllUnits;
    private bool _isProcessSummary = true;
    private double _flowZoom = 1;
    private readonly RelayCommand _applyStepDraftCommand;
    private readonly RelayCommand _discardStepDraftCommand;
    private readonly AsyncRelayCommand _testSelectedStepCommand;
    private SequenceStepEditorItem? _selectedStepDraft;
    private readonly Dictionary<string, bool> _stepAdditionalExpanded = new();
    private string _stepDraftBase = string.Empty;
    private string _stepTimeoutText = string.Empty;
    private string _stepEjectionDelayText = string.Empty;
    private string _stepDraftErrorText = string.Empty;
    private string _stepTeachingName = string.Empty;
    private string _stepTeachingTargetId = string.Empty;
    private string _teachingSearchText = string.Empty;
    private string _teachingPreviewName = string.Empty;
    private string _teachingPreviewTargetId = string.Empty;
    private IReadOnlyList<KeyValuePair<string, string>> _teachingPositions = [];
    private readonly RelayCommand _copyTeachingPreviewCommand;
    private bool _isStepParameterInvalid;
    private string _stepTestErrorText = string.Empty;
    private SequenceStepPreviewResult? _stepTestResult;
    private CancellationTokenSource? _stepTestCancellation;
    private bool _isStepTestBusy;
    private bool _isLoadingSteps;
    private bool _isResolvingStepDraft;
    private bool _isApplyingStepDraft;
    private string _axisStepTargetId = string.Empty;
    private string _axisStepTemplateId = "move-axis-home";
    private readonly RelayCommand _addAxisStepCommand;
    private string _cameraStepTargetId = string.Empty;
    private readonly RelayCommand _addCameraStepCommand;
    private string _signalStepTargetId = string.Empty;
    private string _signalStepTemplateId = string.Empty;
    private readonly RelayCommand _addSignalStepCommand;
    private string _subsequenceStepTargetId = string.Empty;
    private readonly RelayCommand _addSubsequenceStepCommand;
    private string _unitStepTargetId = string.Empty;
    private string _unitStepTemplateId = string.Empty;
    private readonly RelayCommand _addUnitStepCommand;
    private string _transferStepOwnerId = string.Empty;
    private string _transferStepTargetId = string.Empty;
    private string _transferStepDestinationId = string.Empty;
    private readonly RelayCommand _addTransferStepCommand;
    private string _workpieceStepTargetId = string.Empty;
    private string _workpieceStepTemplateId = string.Empty;
    private readonly RelayCommand _addWorkpieceStepCommand;
    private readonly ICommand _addStepCommand;
    private readonly ICommand _addUnitSequenceCommand;
    private readonly ICommand _applyUnitRepeatCommand;
    private readonly ICommand _deleteStepCommand;
    private readonly ICommand _moveStepUpCommand;
    private readonly ICommand _moveStepDownCommand;
    private MachineProjectDocument _project = new();
    private IReadOnlyList<SequenceAuthoringTarget> _authoringTargets =
        Array.Empty<SequenceAuthoringTarget>();
    private IReadOnlyList<SequenceAuthoringTarget> _workpieceTargets =
        Array.Empty<SequenceAuthoringTarget>();
    private IReadOnlyList<SequenceExpectedStateTarget> _expectedStateTargets =
        Array.Empty<SequenceExpectedStateTarget>();
    private SequenceDefinition? _selectedSequence;
    private string _unitRepeatCount = "1";
    private SequenceStepEditorItem? _selectedStep;
    private SequenceStepTemplateDefinition? _selectedTemplate;
    private bool _isEditable = true;
    private bool _disposed;
    private string _validationSummary = OpenVisionLanguageService.T(
        "Sequence.NoSequenceSelected",
        "선택한 시퀀스 없음",
        "No sequence selected");
    private string _structuralEditStatus = OpenVisionLanguageService.T(
        "Sequence.SelectSequenceHint",
        "시퀀스를 선택해 단계를 편집하세요.",
        "Select a sequence to edit steps.");
    private SequenceValidationIssue? _selectedValidationIssue;
    private string _validationComparisonText = string.Empty;
    private string _lastValidationSignature = string.Empty;
    private int _lastValidationErrorCount;
    private bool _hasValidationSnapshot;
    private string? _runtimeSequenceId;
    private string? _runtimeStepId;

    public SequenceEditorViewModel()
    {
        _findNextStepCommand = new RelayCommand(_ => FindNextStep(), _ => !_disposed && !_isResolvingStepDraft && StepSearchMatchCount > 0);
        _clearStepSearchCommand = new RelayCommand(_ => StepSearchText = string.Empty, _ => !_disposed && !_isResolvingStepDraft && StepSearchText.Length > 0);
        _selectOverviewStepCommand = new RelayCommand(SelectOverviewStep, CanSelectOverviewStep);
        _beginRouteDestinationSelectionCommand = new RelayCommand(BeginRouteDestinationSelection, CanBeginRouteDestinationSelection);
        _cancelRouteDestinationSelectionCommand = new RelayCommand(_ => CancelRouteDestinationSelection(), _ => !_disposed && IsSelectingRouteDestination);
        _changeFlowZoomCommand = new RelayCommand(parameter => FlowZoom = (parameter as string) switch { "-" => FlowZoom - 0.1, "+" => FlowZoom + 0.1, _ => 1 },
            parameter => !_disposed && ((parameter as string) switch { "-" => FlowZoom > 0.6, "+" => FlowZoom < 1.4, _ => FlowZoom != 1 }));
        _copyTeachingPreviewCommand = new RelayCommand(_ => CopyTeachingPreview(), _ => CanCopyTeachingPreview);
        _applyStepDraftCommand = new RelayCommand(_ => TryApplyStepDraft(), _ => CanApplyStepDraft);
        _discardStepDraftCommand = new RelayCommand(_ => DiscardStepDraft(), _ => !_disposed && IsEditable && !IsStepTestBusy && HasPendingStepDraft);
        _testSelectedStepCommand = new AsyncRelayCommand(_ => TestSelectedStepAsync(), _ => CanTestSelectedStep,
            error => { if (!_disposed) { _stepTestErrorText = error.Message; StepTestResult = null; OnPropertyChanged(nameof(StepTestStatusText)); OnPropertyChanged(nameof(HasStepTestStatus)); } });
        _stepEditors = new SequenceStepEditorCollection(_templateCatalog, _targetCatalog);
        _stepEditors.DefinitionChanged += OnStepDefinitionChanged;
        _addAxisStepCommand = new RelayCommand(_ => AddStep(AxisStepTemplates.FirstOrDefault(template => template.Id == _axisStepTemplateId), _axisStepTargetId), _ => CanChangeStructure() && AxisStepPreview is not null);
        _addCameraStepCommand = new RelayCommand(_ => AddStep(Templates.FirstOrDefault(template => template.Id == "trigger-camera"), cameraTargetId: _cameraStepTargetId), _ => CanChangeStructure() && CameraStepPreview is not null && Templates.Any(template => template.Id == "trigger-camera"));
        _addSignalStepCommand = new RelayCommand(_ => AddStep(SignalStepTemplates.FirstOrDefault(template => template.Id == _signalStepTemplateId), signalTargetId: _signalStepTargetId), _ => CanChangeStructure() && SignalStepPreview is not null && Templates.Any(template => template.Id == _signalStepTemplateId));
        _addSubsequenceStepCommand = new RelayCommand(_ => AddStep(Templates.FirstOrDefault(template => template.Id == "call-subsequence"), subsequenceTargetId: _subsequenceStepTargetId), _ => CanChangeStructure() && SubsequenceStepPreview is not null && Templates.Any(template => template.Id == "call-subsequence"));
        _addUnitStepCommand = new RelayCommand(_ => AddStep(UnitStepTemplates.FirstOrDefault(template => template.Id == _unitStepTemplateId), unitTargetId: UnitStepTarget?.Id ?? string.Empty), _ => CanChangeStructure() && UnitStepPreview is not null && Templates.Any(template => template.Id == _unitStepTemplateId));
        _addTransferStepCommand = new RelayCommand(_ => AddStep(Templates.FirstOrDefault(template => template.Id == "transfer-unit"), transferTargetId: _transferStepTargetId, transferDestinationId: _transferStepDestinationId),
            _ => CanChangeStructure() && TransferStepPreview is not null && Templates.Any(template => template.Id == "transfer-unit"));
        _addWorkpieceStepCommand = new RelayCommand(_ => AddStep(WorkpieceStepTemplates.FirstOrDefault(template => template.Id == _workpieceStepTemplateId), workpieceTargetId: _workpieceStepTargetId),
            _ => CanChangeStructure() && WorkpieceStepPreview is not null && Templates.Any(template => template.Id == _workpieceStepTemplateId));
        _addStepCommand = new RelayCommand(parameter => AddStep(parameter), parameter => CanAddStep(parameter));
        _addUnitSequenceCommand = new RelayCommand(_ => AddUnitSequence(), _ => IsEditable && !_disposed &&
            _project.Stations.SelectMany(station => station.Units).Any(unit => !_project.Sequences.Any(sequence => sequence.UnitId == unit.Id)));
        _applyUnitRepeatCommand = new RelayCommand(_ => ApplyUnitRepeat(), _ => IsEditable && !_disposed && HasSequenceUnit);
        _deleteStepCommand = new RelayCommand(
            _ => DeleteSelectedStep(),
            _ => CanChangeStructure() && SelectedStep is not null);
        _moveStepUpCommand = new RelayCommand(_ => MoveSelectedStep(-1), _ => CanMoveSelectedStep(-1));
        _moveStepDownCommand = new RelayCommand(_ => MoveSelectedStep(1), _ => CanMoveSelectedStep(1));
    }

    public ObservableCollection<SequenceDefinition> Sequences { get; } = new();
    public ObservableCollection<SequenceStepEditorItem> Steps => _stepEditors.Items;
    public ObservableCollection<SequenceStepTemplateDefinition> Templates { get; } = new();
    public ObservableCollection<string> ValidationMessages { get; } = new();
    public ObservableCollection<SequenceValidationIssue> ValidationIssues { get; } = new();
    public ObservableCollection<SequenceTaskGroupViewModel> TaskGroups { get; } = new();
    public ObservableCollection<SequenceTaskLaneViewModel> ProcessLanes { get; } = new();
    public ICommand SelectOverviewStepCommand => _selectOverviewStepCommand;
    public ICommand ChangeFlowZoomCommand => _changeFlowZoomCommand;
    public ICommand BeginRouteDestinationSelectionCommand => _beginRouteDestinationSelectionCommand;
    public ICommand CancelRouteDestinationSelectionCommand => _cancelRouteDestinationSelectionCommand;
    public bool IsSelectingRouteDestination => _routeDestinationProperty is not null;
    public string RouteDestinationSelectionText => !IsSelectingRouteDestination ? string.Empty : $"{SelectedStep?.Order}. {SelectedStep?.DisplayName} · "
        + (_routeDestinationProperty == nameof(SequenceStepEditorItem.FailureStepId) ? "NG" : _routeDestinationProperty == nameof(SequenceStepEditorItem.ErrorStepId)
            ? OpenVisionLanguageService.T("R24.Inspector.AlarmRoute") : StepNextCaption);
    public bool IsProcessAllUnits
    {
        get => _isProcessAllUnits;
        set { if (!_disposed && SetProperty(ref _isProcessAllUnits, value)) { CancelRouteDestinationSelection(); RefreshProcessLanes(); } }
    }
    public bool IsProcessSummary
    {
        get => _isProcessSummary;
        set { if (!_disposed && SetProperty(ref _isProcessSummary, value)) { CancelRouteDestinationSelection(); RefreshProcessLanes(); } }
    }
    public double FlowZoom
    {
        get => _flowZoom;
        set
        {
            if (_disposed || !double.IsFinite(value) || !SetProperty(ref _flowZoom, Math.Round(Math.Clamp(value, 0.6, 1.4), 1))) return;
            OnPropertyChanged(nameof(FlowZoomText));
            _changeFlowZoomCommand.RaiseCanExecuteChanged();
        }
    }
    public string FlowZoomText => FlowZoom.ToString("P0", CultureInfo.CurrentCulture);
    public int ProcessViewIndex
    {
        get => _processViewIndex;
        set
        {
            if (value is < 0 or > 1 || !SetProperty(ref _processViewIndex, value)) return;
            CancelRouteDestinationSelection();
            OnPropertyChanged(nameof(IsProcessOverview));
            OnPropertyChanged(nameof(IsProcessTable));
            RefreshStepSearch();
        }
    }
    public bool IsProcessOverview { get => ProcessViewIndex == 0; set { if (value) ProcessViewIndex = 0; } }
    public bool IsProcessTable { get => ProcessViewIndex == 1; set { if (value) ProcessViewIndex = 1; } }
    public bool IsProcessSceneVisible { get => _isProcessSceneVisible; set => SetProperty(ref _isProcessSceneVisible, value); }
    public string StepSearchText
    {
        get => _stepSearchText;
        set
        {
            if (_disposed || _isResolvingStepDraft || !SetProperty(ref _stepSearchText, value ?? string.Empty)) return;
            RefreshStepSearch();
        }
    }

    public int StepSearchMatchCount => _stepSearchMatchCount;
    public string StepSearchStatusText => string.IsNullOrWhiteSpace(StepSearchText) ? string.Empty
        : StepSearchMatchCount == 0 ? OpenVisionLanguageService.T("Sequence.SearchNoMatches")
        : Format("Sequence.SearchMatchCount", StepSearchMatchCount);

    public bool HasSequences => Sequences.Count != 0;
    public bool HasTemplates => Templates.Count != 0;
    public IReadOnlyList<SequenceAuthoringTarget> AvailableUnits => new[] { new SequenceAuthoringTarget("", OpenVisionLanguageService.T("Sequence.CommonSequence"), SequenceAuthoringTargetKind.Unit) }
        .Concat(_project.Stations.SelectMany(station => station.Units).Select(unit => new SequenceAuthoringTarget(unit.Id, unit.Name, SequenceAuthoringTargetKind.Unit))).ToArray();
    public string SelectedUnitId
    {
        get => SelectedSequence?.UnitId ?? string.Empty;
        set
        {
            if (!IsEditable || IsStepTestBusy || SelectedSequence is null || SelectedUnitId == value || !ResolvePendingStepDraft()) return;
            if (!string.IsNullOrEmpty(value) && _project.Sequences.Any(sequence => sequence != SelectedSequence && sequence.UnitId == value))
            {
                StructuralEditStatus = OpenVisionLanguageService.T("Sequence.UnitAlreadyHasSequence");
                OnPropertyChanged();
                return;
            }
            SelectedSequence.UnitId = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
            ReloadUnitRepeatDraft();
            LoadAuthoringTargets();
            LoadSteps();
            Validate();
            DefinitionChanged?.Invoke(this, new SequenceEditorChangedEventArgs(false));
        }
    }

    public string SequenceUnitNameText => SequenceUnit?.Name ?? SelectedSequence?.Name ?? string.Empty;
    public string StepListCountText => Format("R24.StepCount", Steps.Count);
    public bool CanChooseStepTemplate => CanChangeStructure() && HasTemplates;
    public bool HasStepTestStatus => IsStepTestBusy || StepTestResult is not null || !string.IsNullOrEmpty(_stepTestErrorText);
    public bool HasSequenceUnit => SequenceUnit is not null;
    public string UnitRepeatCount { get => _unitRepeatCount; set => SetProperty(ref _unitRepeatCount, value ?? ""); }
    private MachineUnitDefinition? SequenceUnit => _project.Stations.SelectMany(station => station.Units).FirstOrDefault(unit => unit.Id == SelectedSequence?.UnitId);

    public IReadOnlyList<SequenceAuthoringTarget> AxisStepTargets => _disposed || SelectedSequence is null || !_project.Sequences.Contains(SelectedSequence)
        ? [] : _targetCatalog.GetTargetsForSequence(_targetCatalog.Build(_project, SelectedSequence.UnitId).AuthoringTargets, SelectedSequence)
            .Where(target => target.Kind == SequenceAuthoringTargetKind.Axis && _project.Axes.Count(axis => axis.Id == target.Id) == 1).ToArray();
    public IReadOnlyList<SequenceStepTemplateDefinition> AxisStepTemplates => _templateCatalog.Templates
        .Where(template => template.Action is SequenceStepAction.MoveAxis or SequenceStepAction.WaitAxisDone).ToArray();
    public string AxisStepTargetId
    {
        get => _axisStepTargetId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy || !SetProperty(ref _axisStepTargetId, value ?? string.Empty)) return;
            RefreshAxisStepAuthoring();
        }
    }
    public string AxisStepTemplateId
    {
        get => _axisStepTemplateId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy || !AxisStepTemplates.Any(template => template.Id == value) || !SetProperty(ref _axisStepTemplateId, value)) return;
            RefreshAxisStepAuthoring();
        }
    }
    public SequenceAuthoringTarget? AxisStepTarget => AxisStepTargets.SingleOrDefault(target => target.Id == _axisStepTargetId);
    public SequenceStepDefinition? AxisStepPreview
    {
        get
        {
            if (SelectedSequence is not { } sequence || AxisStepTarget is not { } target) return null;
            return _templateCatalog.CreateDraft(_axisStepTemplateId, $"step-{NextStepOrdinal(sequence)}", [target]).Step;
        }
    }
    public string AxisStepPreviewValue => AxisStepPreview is not { Parameter.Length: > 0 } preview ? "—"
        : preview.Parameter + (_project.Axes.FirstOrDefault(axis => axis.Id == preview.TargetId)?.Unit is { Length: > 0 } unit ? " " + unit : string.Empty);
    public string AxisStepPreviewTimeout => AxisStepPreview is { TimeoutMs: > 0 } preview ? preview.TimeoutMs.ToString(CultureInfo.CurrentCulture) : "—";

    public IReadOnlyList<SequenceAuthoringTarget> CameraStepTargets => _disposed || SelectedSequence is null || !_project.Sequences.Contains(SelectedSequence)
        ? [] : _targetCatalog.GetTargetsForSequence(_targetCatalog.Build(_project, SelectedSequence.UnitId).AuthoringTargets, SelectedSequence)
            .Where(target => target.Kind == SequenceAuthoringTargetKind.Camera && _project.Devices.Count(device => device.Id == target.Id) == 1).ToArray();
    public string CameraStepTargetId
    {
        get => _cameraStepTargetId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy || !SetProperty(ref _cameraStepTargetId, value ?? string.Empty)) return;
            RefreshCameraStepAuthoring();
        }
    }
    public SequenceAuthoringTarget? CameraStepTarget => CameraStepTargets.SingleOrDefault(target => target.Id == _cameraStepTargetId);
    public SequenceStepDefinition? CameraStepPreview => SelectedSequence is not { } sequence || CameraStepTarget is not { } target
        ? null : _templateCatalog.CreateDraft("trigger-camera", $"step-{NextStepOrdinal(sequence)}", [target]).Step;
    public string CameraStepHint => OpenVisionLanguageService.T(CameraStepTargets.Count == 0 ? "R40.CameraStepNoTargets" : "R40.CameraStepHint");
    public string CameraStepPreviewRecipe => CameraStepPreview?.Parameter ?? "—";
    public string CameraStepPreviewTimeout => CameraStepPreview?.TimeoutMs.ToString(CultureInfo.CurrentCulture) ?? "—";

    public IReadOnlyList<SequenceAuthoringTarget> SignalStepTargets => _disposed || SelectedSequence is null || !_project.Sequences.Contains(SelectedSequence)
        ? [] : _targetCatalog.GetTargetsForSequence(_targetCatalog.Build(_project, SelectedSequence.UnitId).AuthoringTargets, SelectedSequence)
            .Where(target => (target.Kind is SequenceAuthoringTargetKind.DigitalInput or SequenceAuthoringTargetKind.DigitalOutput)
                && !string.IsNullOrWhiteSpace(target.Id) && _project.Channels.Count(channel => channel.Id == target.Id) == 1).ToArray();
    public IReadOnlyList<SequenceStepTemplateDefinition> SignalStepTemplates => SignalStepTarget is not { } target ? []
        : _templateCatalog.Templates.Where(template => (template.Action is SequenceStepAction.SetSignal or SequenceStepAction.WaitSignal) && template.TargetKinds.Contains(target.Kind)).ToArray();
    public string SignalStepTargetId
    {
        get => _signalStepTargetId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy || !SetProperty(ref _signalStepTargetId, value ?? string.Empty)) return;
            _signalStepTemplateId = string.Empty;
            RefreshSignalStepAuthoring();
        }
    }
    public string SignalStepTemplateId
    {
        get => _signalStepTemplateId;
        set
        {
            value ??= string.Empty;
            if (_disposed || !IsEditable || IsStepTestBusy || value.Length > 0 && !SignalStepTemplates.Any(template => template.Id == value)
                || !SetProperty(ref _signalStepTemplateId, value)) return;
            RefreshSignalStepAuthoring();
        }
    }
    public SequenceAuthoringTarget? SignalStepTarget => SignalStepTargets.SingleOrDefault(target => target.Id == _signalStepTargetId);
    public string SignalStepTargetKindText => SignalStepTarget?.Kind switch
    {
        SequenceAuthoringTargetKind.DigitalInput => OpenVisionLanguageService.T("Io.DigitalInput"),
        SequenceAuthoringTargetKind.DigitalOutput => OpenVisionLanguageService.T("Io.DigitalOutput"),
        _ => string.Empty
    };
    public SequenceStepDefinition? SignalStepPreview => SelectedSequence is not { } sequence || SignalStepTarget is not { } target
        || SignalStepTemplates.FirstOrDefault(candidate => candidate.Id == _signalStepTemplateId) is not { } template
        ? null : _templateCatalog.CreateDraft(template.Id, $"step-{NextStepOrdinal(sequence)}", [target]).Step;
    public string SignalStepPreviewValue => SignalStepPreview?.Parameter switch { "true" => "ON", "false" => "OFF", _ => "—" };
    public string SignalStepPreviewTimeout => SignalStepPreview?.TimeoutMs.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string SignalStepHint => OpenVisionLanguageService.T(SignalStepTargets.Count == 0 ? "R40.SignalStepNoTargets" : "R40.SignalStepHint");

    public IReadOnlyList<SequenceAuthoringTarget> SubsequenceStepTargets => _disposed || SelectedSequence is null || !_project.Sequences.Contains(SelectedSequence)
        ? [] : _targetCatalog.GetTargetsForSequence(_targetCatalog.Build(_project, SelectedSequence.UnitId).AuthoringTargets, SelectedSequence)
            .Where(target => target.Kind == SequenceAuthoringTargetKind.Subsequence && !string.IsNullOrWhiteSpace(target.Id)
                && _project.Sequences.Count(sequence => sequence.Id == target.Id) == 1).ToArray();
    public string SubsequenceStepTargetId
    {
        get => _subsequenceStepTargetId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy || !SetProperty(ref _subsequenceStepTargetId, value ?? string.Empty)) return;
            RefreshSubsequenceStepAuthoring();
        }
    }
    public SequenceAuthoringTarget? SubsequenceStepTarget => SubsequenceStepTargets.SingleOrDefault(target => target.Id == _subsequenceStepTargetId);
    public SequenceStepDefinition? SubsequenceStepPreview => SelectedSequence is not { } sequence || SubsequenceStepTarget is not { } target
        ? null : _templateCatalog.CreateDraft("call-subsequence", $"step-{NextStepOrdinal(sequence)}", [target]).Step;
    public string SubsequenceStepPreviewTimeout => SubsequenceStepPreview?.TimeoutMs.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string SubsequenceStepHint => OpenVisionLanguageService.T(SubsequenceStepTargets.Count == 0 ? "R40.SubsequenceStepNoTargets" : "R40.SubsequenceStepHint");

    public SequenceAuthoringTarget? UnitStepTarget => _disposed || SelectedSequence is null || !_project.Sequences.Contains(SelectedSequence)
        ? null : _targetCatalog.GetTargetsForSequence(_targetCatalog.Build(_project, SelectedSequence.UnitId).AuthoringTargets, SelectedSequence)
            .SingleOrDefault(target => target.Kind == SequenceAuthoringTargetKind.Unit && !string.IsNullOrWhiteSpace(target.Id)
                && _project.Stations.SelectMany(station => station.Units).Count(unit => unit.Id == target.Id) == 1);
    public string UnitStepTargetText => UnitStepTarget?.Name ?? "—";
    public IReadOnlyList<SequenceStepTemplateDefinition> UnitStepTemplates => UnitStepTarget is null ? [] : _templateCatalog.Templates
        .Where(template => template.Action is SequenceStepAction.FeedUnitWorkpiece or SequenceStepAction.ReceiveUnitWorkpiece or SequenceStepAction.EjectUnitWorkpiece).ToArray();
    public string UnitStepTemplateId
    {
        get => _unitStepTemplateId;
        set
        {
            value ??= string.Empty;
            if (_disposed || !IsEditable || IsStepTestBusy || value.Length > 0 && !UnitStepTemplates.Any(template => template.Id == value)) return;
            string targetId = UnitStepTarget?.Id ?? string.Empty;
            bool targetChanged = _unitStepTargetId != targetId;
            _unitStepTargetId = targetId;
            if (!SetProperty(ref _unitStepTemplateId, value) && !targetChanged) return;
            RefreshUnitStepAuthoring();
        }
    }
    public SequenceStepDefinition? UnitStepPreview => SelectedSequence is not { } sequence || UnitStepTarget is not { } target || target.Id != _unitStepTargetId
        || UnitStepTemplates.FirstOrDefault(candidate => candidate.Id == _unitStepTemplateId) is not { } template
        ? null : _templateCatalog.CreateDraft(template.Id, $"step-{NextStepOrdinal(sequence)}", [target]).Step;
    public string UnitStepPreviewValue => UnitStepPreview?.Parameter == "auto" ? OpenVisionLanguageService.T("MaterialFlow.AutoRoute") : "—";
    public string UnitStepPreviewTimeout => UnitStepPreview?.TimeoutMs.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string UnitStepHint => OpenVisionLanguageService.T(UnitStepTarget is null ? "R40.UnitStepNoTarget" : "R40.UnitStepHint");

    public IReadOnlyList<SequenceAuthoringTarget> TransferStepTargets => UnitStepTarget is null ? []
        : _targetCatalog.Build(_project, SelectedSequence!.UnitId).AuthoringTargets
            .Where(target => target.Kind == SequenceAuthoringTargetKind.Conveyor && !string.IsNullOrWhiteSpace(target.Id))
            .GroupBy(target => target.Id, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    public IReadOnlyList<SequenceAuthoringTarget> TransferStepDestinations => UnitStepTarget is not { } owner ? []
        : _targetCatalog.Build(_project, SelectedSequence!.UnitId).AuthoringTargets
            .Where(target => target.Kind == SequenceAuthoringTargetKind.Unit && target.Id != owner.Id && !string.IsNullOrWhiteSpace(target.Id)
                && _project.Stations.SelectMany(station => station.Units).Count(unit => unit.Id == target.Id) == 1).ToArray();
    public string TransferStepTargetId
    {
        get => _transferStepTargetId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy) return;
            string ownerId = UnitStepTarget?.Id ?? string.Empty;
            bool ownerChanged = _transferStepOwnerId != ownerId;
            if (ownerChanged) { _transferStepTargetId = string.Empty; _transferStepDestinationId = string.Empty; }
            _transferStepOwnerId = ownerId;
            if (!SetProperty(ref _transferStepTargetId, value ?? string.Empty) && !ownerChanged) return;
            RefreshTransferStepAuthoring();
        }
    }
    public string TransferStepDestinationId
    {
        get => _transferStepDestinationId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy) return;
            string ownerId = UnitStepTarget?.Id ?? string.Empty;
            bool ownerChanged = _transferStepOwnerId != ownerId;
            if (ownerChanged) { _transferStepTargetId = string.Empty; _transferStepDestinationId = string.Empty; }
            _transferStepOwnerId = ownerId;
            if (!SetProperty(ref _transferStepDestinationId, value ?? string.Empty) && !ownerChanged) return;
            RefreshTransferStepAuthoring();
        }
    }
    public SequenceAuthoringTarget? TransferStepTarget => TransferStepTargets.SingleOrDefault(target => target.Id == _transferStepTargetId);
    public SequenceAuthoringTarget? TransferStepDestination => TransferStepDestinations.SingleOrDefault(target => target.Id == _transferStepDestinationId);
    public SequenceStepDefinition? TransferStepPreview
    {
        get
        {
            if (SelectedSequence is not { } sequence || UnitStepTarget is not { } owner || owner.Id != _transferStepOwnerId
                || TransferStepTarget is not { } target || TransferStepDestination is not { } destination) return null;
            var step = _templateCatalog.CreateDraft("transfer-unit", $"step-{NextStepOrdinal(sequence)}", [target]).Step;
            if (step is not null) step.DestinationUnitId = destination.Id;
            return step;
        }
    }
    public string TransferStepPreviewValue => TransferStepPreview is { } step ? step.Parameter + " s" : "—";
    public string TransferStepPreviewTimeout => TransferStepPreview?.TimeoutMs.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string TransferStepHint => OpenVisionLanguageService.T(TransferStepTargets.Count == 0 || TransferStepDestinations.Count == 0 ? "R40.TransferStepUnavailable" : "R40.TransferStepHint");

    public IReadOnlyList<SequenceAuthoringTarget> WorkpieceStepTargets => _disposed || SelectedSequence is null || !_project.Sequences.Contains(SelectedSequence) ? []
        : _targetCatalog.Build(_project, SelectedSequence.UnitId).WorkpieceTargets.Where(target => !string.IsNullOrWhiteSpace(target.Id))
            .GroupBy(target => target.Id, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    public IReadOnlyList<SequenceStepTemplateDefinition> WorkpieceStepTemplates => WorkpieceStepTarget is null ? [] : _templateCatalog.Templates
        .Where(template => template.Action is SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece).ToArray();
    public string WorkpieceStepTargetId
    {
        get => _workpieceStepTargetId;
        set
        {
            if (_disposed || !IsEditable || IsStepTestBusy || !SetProperty(ref _workpieceStepTargetId, value ?? string.Empty)) return;
            _workpieceStepTemplateId = string.Empty;
            RefreshWorkpieceStepAuthoring();
        }
    }
    public string WorkpieceStepTemplateId
    {
        get => _workpieceStepTemplateId;
        set
        {
            value ??= string.Empty;
            if (_disposed || !IsEditable || IsStepTestBusy || value.Length > 0 && !WorkpieceStepTemplates.Any(template => template.Id == value)
                || !SetProperty(ref _workpieceStepTemplateId, value)) return;
            RefreshWorkpieceStepAuthoring();
        }
    }
    public SequenceAuthoringTarget? WorkpieceStepTarget => WorkpieceStepTargets.SingleOrDefault(target => target.Id == _workpieceStepTargetId);
    public SequenceStepDefinition? WorkpieceStepPreview
    {
        get
        {
            if (SelectedSequence is not { } sequence || WorkpieceStepTarget is not { } target
                || WorkpieceStepTemplates.FirstOrDefault(template => template.Id == _workpieceStepTemplateId) is not { } template) return null;
            var step = _templateCatalog.CreateDraft(template.Id, $"step-{NextStepOrdinal(sequence)}", [target]).Step;
            if (step is not null) step.WorkpieceComponentId = target.Id;
            return step;
        }
    }
    public string WorkpieceStepPreviewTimeout => WorkpieceStepPreview?.TimeoutMs.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string WorkpieceStepHint => OpenVisionLanguageService.T(WorkpieceStepTargets.Count == 0 ? "R40.WorkpieceStepNoTargets" : "R40.WorkpieceStepHint");

    public SequenceStepTemplateDefinition? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public SequenceDefinition? SelectedSequence
    {
        get => _selectedSequence;
        set
        {
            if (!ReferenceEquals(_selectedSequence, value) && !_isLoadingSteps && !ResolvePendingStepDraft())
            {
                OnPropertyChanged();
                return;
            }
            if (SetProperty(ref _selectedSequence, value))
            {
                _axisStepTargetId = string.Empty;
                _cameraStepTargetId = string.Empty;
                _signalStepTargetId = string.Empty;
                _signalStepTemplateId = string.Empty;
                _subsequenceStepTargetId = string.Empty;
                _workpieceStepTargetId = string.Empty;
                _workpieceStepTemplateId = string.Empty;
                _transferStepOwnerId = string.Empty;
                _transferStepTargetId = string.Empty;
                _transferStepDestinationId = string.Empty;
                _unitStepTargetId = string.Empty;
                _unitStepTemplateId = string.Empty;
                ResetValidationComparison();
                LoadAuthoringTargets();
                LoadSteps();
                OnPropertyChanged(nameof(SelectedUnitId));
                ReloadUnitRepeatDraft();
            }
        }
    }

    public SequenceStepEditorItem? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!ReferenceEquals(_selectedStep, value) && !_isLoadingSteps && !ResolvePendingStepDraft())
            {
                OnPropertyChanged();
                return;
            }
            if (SetProperty(ref _selectedStep, value))
            {
                ReloadStepDraft();
                OnPropertyChanged(nameof(HasSelectedStep));
                RefreshTaskIndicators(revealSelection: true);
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelectedStep => SelectedStep is not null;
    public bool HasExecutingStep => string.Equals(SelectedSequence?.Id, _runtimeSequenceId, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(_runtimeStepId);
    public string CurrentRuntimeStepName => HasExecutingStep
        ? Steps.FirstOrDefault(step => string.Equals(step.Id, _runtimeStepId, StringComparison.Ordinal))?.DisplayName
            ?? _runtimeStepId ?? string.Empty
        : string.Empty;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value))
            {
                return;
            }

            InvalidateCommands();
            if (!value) { CancelRouteDestinationSelection(); _stepTestCancellation?.Cancel(); }
            OnPropertyChanged(nameof(IsStepDraftEditable));
            RefreshStepDraftState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal Func<PlacementDraftDecision> PendingStepDraftPrompt { get; set; } = () => PlacementDraftDecision.Cancel;
    internal Func<bool>? CanSelectOverviewSequence { get; set; }
    internal Func<SequenceDefinition, bool>? SelectOverviewSequence { get; set; }
    public SequenceStepEditorItem? SelectedStepDraft => _selectedStepDraft;
    public bool HasPendingStepDraft => SelectedStepDraft is not null && SelectedStep is not null
        && (SelectedStepDraft.DefinitionSnapshot != _stepDraftBase
            || StepTimeoutText != FormatStepTimeout(SelectedStepDraft.TimeoutMs)
            || StepEjectionDelayText != SelectedStepDraft.EjectionDelayMilliseconds.ToString(CultureInfo.InvariantCulture));
    public bool IsStepDraftEditable => !_disposed && IsEditable && !IsStepTestBusy && HasSelectedStep;
    public bool CanApplyStepDraft => IsStepDraftEditable && HasPendingStepDraft && string.IsNullOrEmpty(StepDraftErrorText);
    public string StepDraftErrorText { get => _stepDraftErrorText; private set => SetProperty(ref _stepDraftErrorText, value); }
    public bool IsStepParameterInvalid { get => _isStepParameterInvalid; private set => SetProperty(ref _isStepParameterInvalid, value); }
    public string StepParameterLabel
    {
        get
        {
            if (SelectedStepDraft?.Action is SequenceStepAction.SetSignal or SequenceStepAction.SetChannel) return OpenVisionLanguageService.T("R24.Inspector.OutputValue");
            if (SelectedStepDraft?.Action == SequenceStepAction.TriggerCamera) return OpenVisionLanguageService.T("R24.Inspector.CameraRecipeId");
            if (SelectedStepDraft?.Action == SequenceStepAction.EjectUnitWorkpiece) return OpenVisionLanguageService.T("R24.Inspector.EjectRoute");
            if (SelectedStepDraft?.Action == SequenceStepAction.TransferUnitWorkpiece) return OpenVisionLanguageService.T("R24.Inspector.TransferTime");
            if (SelectedStepDraft?.Action == SequenceStepAction.WaitSignal) return OpenVisionLanguageService.T("R24.StepExpectedValue");
            if (SelectedStepDraft?.Action == SequenceStepAction.Delay) return OpenVisionLanguageService.T("Sequence.Action.Delay") + " (s)";
            if (SelectedStepDraft?.Action != SequenceStepAction.MoveAxis) return OpenVisionLanguageService.T("Sequence.Value");
            string? unit = _project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, SelectedStepDraft.TargetId, StringComparison.Ordinal))?.Unit;
            return string.IsNullOrWhiteSpace(unit) ? OpenVisionLanguageService.T("R24.AxisTargetPosition")
                : string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.AxisTargetPositionFormat"), unit);
        }
    }
    public string StepTargetCaption => OpenVisionLanguageService.T(SelectedStepDraft?.Action is SequenceStepAction.SetSignal or SequenceStepAction.WaitSignal ? "R24.StepSignal" : "Sequence.Target");
    public string StepParameterCaption => SelectedStepDraft?.Action == SequenceStepAction.MoveAxis ? OpenVisionLanguageService.T("R24.AxisTargetPosition")
        : SelectedStepDraft?.Action == SequenceStepAction.Delay ? OpenVisionLanguageService.T("Sequence.Action.Delay") : StepParameterLabel;
    public string StepParameterUnitText
    {
        get
        {
            if (SelectedStepDraft?.Action is SequenceStepAction.Delay or SequenceStepAction.TransferUnitWorkpiece) return " (s)";
            if (SelectedStepDraft?.Action != SequenceStepAction.MoveAxis) return string.Empty;
            string? unit = _project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, SelectedStepDraft.TargetId, StringComparison.Ordinal))?.Unit;
            return string.IsNullOrWhiteSpace(unit) ? string.Empty : $" ({unit})";
        }
    }
    public IReadOnlyList<KeyValuePair<string, string>> StepTeachingOptions
    {
        get
        {
            var direct = new KeyValuePair<string, string>(string.Empty, OpenVisionLanguageService.T("R24.TeachingDirectInput"));
            var axis = SelectedStepDraft?.Action == SequenceStepAction.MoveAxis
                ? _project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, SelectedStepDraft.TargetId, StringComparison.Ordinal)) : null;
            return new[] { direct }.Concat((axis?.TeachingPositions ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value))
                .Select(pair => new KeyValuePair<string, string>(pair.Key, $"{pair.Key} = {pair.Value.ToString("R", CultureInfo.CurrentCulture)}{(string.IsNullOrWhiteSpace(axis?.Unit) ? string.Empty : " " + axis.Unit)}"))).ToArray();
        }
    }
    public string StepTeachingName
    {
        get => _stepTeachingName;
        set
        {
            if (value is null || !IsStepDraftEditable || SelectedStepDraft?.Action != SequenceStepAction.MoveAxis || value == _stepTeachingName) return;
            if (value.Length == 0) { SetProperty(ref _stepTeachingName, string.Empty); return; }
            var axis = _project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, SelectedStepDraft.TargetId, StringComparison.Ordinal));
            if (axis?.TeachingPositions?.TryGetValue(value, out double position) != true || !double.IsFinite(position)) return;
            _stepTeachingTargetId = SelectedStepDraft.TargetId;
            SetProperty(ref _stepTeachingName, value);
            SelectedStepDraft.Parameter = position.ToString("R", CultureInfo.InvariantCulture);
        }
    }
    public IReadOnlyList<KeyValuePair<string, string>> TeachingPositions => _teachingPositions;
    public string TeachingSearchText
    {
        get => _teachingSearchText;
        set
        {
            if (_disposed || !SetProperty(ref _teachingSearchText, value ?? string.Empty)) return;
            _teachingPreviewName = string.Empty;
            RefreshTeachingPositions();
        }
    }
    public string TeachingPreviewName
    {
        get => _teachingPreviewName;
        set
        {
            string name = _teachingPositions.Any(position => position.Key == value) ? value : string.Empty;
            if (_disposed || !SetProperty(ref _teachingPreviewName, name)) return;
            OnPropertyChanged(nameof(TeachingPreviewValue));
            _copyTeachingPreviewCommand.RaiseCanExecuteChanged();
        }
    }
    public string TeachingPreviewValue => _teachingPositions.FirstOrDefault(position => position.Key == _teachingPreviewName).Value ?? "—";
    public string TeachingAxisText => TeachingAxis is { } axis ? $"{axis.Name} ({axis.Id})" : "—";
    public string TeachingPositionCountText => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R40.TeachingMatchCount"), _teachingPositions.Count);
    private VirtualAxisDefinition? TeachingAxis => !_disposed && SelectedStepDraft?.Action == SequenceStepAction.MoveAxis
        && SelectedSequence is not null && _project.Sequences.Contains(SelectedSequence)
        ? _project.Axes.FirstOrDefault(axis => axis.Id == SelectedStepDraft.TargetId) : null;
    private bool CanCopyTeachingPreview => IsStepDraftEditable && TeachingAxis is { } axis && _teachingPreviewTargetId == axis.Id
        && axis.TeachingPositions is { } positions && positions.TryGetValue(_teachingPreviewName, out double position) && double.IsFinite(position)
        && _teachingPositions.Any(item => item.Key == _teachingPreviewName && item.Value == FormatTeachingPosition(axis, position));

    public IReadOnlyList<KeyValuePair<string, string>> StepNextStepOptions
    {
        get
        {
            var options = new List<KeyValuePair<string, string>> { new(string.Empty, OpenVisionLanguageService.T("R24.DefaultNextStep")) };
            options.AddRange(Steps.Select(step => new KeyValuePair<string, string>(step.Id, $"{step.Order}. {step.DisplayName}")));
            string id = StepNextStepId;
            if (id.Length > 0 && options.All(option => option.Key != id)) options.Add(new(id, id));
            return options;
        }
    }
    public string StepNextStepId
    {
        get => SelectedStepDraft?.NextStepId ?? string.Empty;
        set
        {
            if (value is null || !IsStepDraftEditable || SelectedStepDraft is null || value == StepNextStepId) return;
            SelectedStepDraft.NextStepId = value;
        }
    }
    public KeyValuePair<string, string>? StepNextStepOption
    {
        get => StepNextStepOptions.First(option => option.Key == StepNextStepId);
        set { if (value is { } option) StepNextStepId = option.Key; }
    }
    private string StepAdditionalKey => $"{_project.Id}/{SelectedSequence?.Id}/{SelectedStepDraft?.Id}";
    public bool IsStepAdditionalExpanded
    {
        get => _stepAdditionalExpanded.TryGetValue(StepAdditionalKey, out bool expanded) ? expanded : SelectedStepDraft?.HasAdditionalSettings == true;
        set { _stepAdditionalExpanded[StepAdditionalKey] = value; OnPropertyChanged(); }
    }
    public string StepAdditionalStatus => OpenVisionLanguageService.T(SelectedStepDraft?.HasAdditionalSettings == true ? "R24.Inspector.Configured" : "R24.Inspector.Unconfigured");
    public string StepNextCaption => OpenVisionLanguageService.T(SelectedStepDraft?.HasFailureRoute == true ? "R24.Inspector.OnOK" : "R24.NextStep");
    public IReadOnlyList<KeyValuePair<string, string>> StepAlarmOptions => InspectorRouteOptions(SelectedStepDraft?.ErrorStepId ?? string.Empty);
    public IReadOnlyList<KeyValuePair<string, string>> StepFailureOptions => InspectorRouteOptions(SelectedStepDraft?.FailureStepId ?? string.Empty);
    public KeyValuePair<string, string>? StepAlarmOption
    {
        get => StepAlarmOptions.First(option => option.Key == (SelectedStepDraft?.ErrorStepId ?? string.Empty));
        set { if (value is { } option && IsStepDraftEditable && SelectedStepDraft is { } draft) draft.ErrorStepId = option.Key; }
    }
    public KeyValuePair<string, string>? StepFailureOption
    {
        get => StepFailureOptions.First(option => option.Key == (SelectedStepDraft?.FailureStepId ?? string.Empty));
        set { if (value is { } option && IsStepDraftEditable && SelectedStepDraft is { } draft) draft.FailureStepId = option.Key; }
    }
    public string StepTimeoutHint
    {
        get
        {
            if (SelectedStepDraft?.SupportsInspectorTimeout != true) return OpenVisionLanguageService.T("R24.Inspector.UnsupportedTimeout");
            if (!TryParseStepTimeout(StepTimeoutText, out int milliseconds)) return OpenVisionLanguageService.T("R24.Inspector.InvalidTimeout");
            if (milliseconds == 0) return OpenVisionLanguageService.T(SelectedStepDraft?.Action == SequenceStepAction.WaitVisionResult ? "R24.Inspector.PositiveTimeout" : "R24.Inspector.NoTimeout");
            string id = SelectedStepDraft?.ErrorStepId ?? string.Empty;
            if (id.Length == 0) return OpenVisionLanguageService.T("R24.Inspector.TimeoutError");
            var step = Steps.FirstOrDefault(step => step.Id == id);
            return step is null ? OpenVisionLanguageService.T("R24.Inspector.InvalidAlarmRoute")
                : string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Inspector.TimeoutRoute"), $"{step.Order}. {step.DisplayName} ({step.Id})");
        }
    }
    public string StepDraftStatusText => OpenVisionLanguageService.T(HasPendingStepDraft ? "Sequence.StepDraftPending" : "Sequence.StepDraftApplied");
    public string StepTimeoutText
    {
        get => _stepTimeoutText;
        set { if (!SetProperty(ref _stepTimeoutText, value ?? string.Empty)) return; UpdateDraftNumbers(); }
    }
    public string StepEjectionDelayText
    {
        get => _stepEjectionDelayText;
        set { if (!SetProperty(ref _stepEjectionDelayText, value ?? string.Empty)) return; UpdateDraftNumbers(); }
    }
    public bool IsStepTestBusy { get => _isStepTestBusy; private set { if (!SetProperty(ref _isStepTestBusy, value)) return; if (value) CancelRouteDestinationSelection(); OnPropertyChanged(nameof(IsStepDraftEditable)); RefreshStepDraftState(); } }
    public SequenceStepPreviewResult? StepTestResult { get => _stepTestResult; private set { if (!SetProperty(ref _stepTestResult, value)) return; OnPropertyChanged(nameof(StepTestStatusText)); OnPropertyChanged(nameof(HasStepTestStatus)); } }
    public string StepTestStatusText => IsStepTestBusy ? OpenVisionLanguageService.T("Sequence.StepTestBusy")
        : !string.IsNullOrEmpty(_stepTestErrorText) ? OpenVisionLanguageService.T("Sequence.StepTestIncomplete") + " · " + _stepTestErrorText
        : StepTestResult is { } result ? $"{OpenVisionLanguageService.T(result.IsCompleted ? "Sequence.StepTestComplete" : "Sequence.StepTestIncomplete")} · {result.ExecutedTicks}/{result.MaximumTicks} · {result.Detail}"
        : OpenVisionLanguageService.T(IsStepTestSupported ? "Sequence.StepTestScope" : "Sequence.StepTestUnsupported");
    public bool IsStepTestSupported => SelectedStep?.Action is SequenceStepAction.MoveAxis or SequenceStepAction.Wait or SequenceStepAction.WaitAxisDone
        or SequenceStepAction.SetChannel or SequenceStepAction.SetSignal or SequenceStepAction.WaitSignal;
    private bool CanTestSelectedStep => !_disposed && IsEditable && !IsStepTestBusy && IsStepTestSupported;
    public ICommand ApplyStepDraftCommand => _applyStepDraftCommand;
    public ICommand DiscardStepDraftCommand => _discardStepDraftCommand;
    public ICommand TestSelectedStepCommand => _testSelectedStepCommand;

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public string StructuralEditStatus
    {
        get => _structuralEditStatus;
        private set => SetProperty(ref _structuralEditStatus, value);
    }

    public SequenceValidationIssue? SelectedValidationIssue
    {
        get => _selectedValidationIssue;
        set
        {
            if (_disposed || !SetProperty(ref _selectedValidationIssue, value) || value is null)
            {
                return;
            }

            if (!string.Equals(SelectedSequence?.Id, value.SequenceId, StringComparison.Ordinal))
            {
                SelectSequence(value.SequenceId);
            }

            SelectedStep = Steps.FirstOrDefault(step =>
                string.Equals(step.Id, value.StepId, StringComparison.Ordinal));
        }
    }

    public string ValidationComparisonText => _validationComparisonText;

    public ICommand FindNextStepCommand => _findNextStepCommand;
    public ICommand ClearStepSearchCommand => _clearStepSearchCommand;
    public ICommand AddStepCommand => _addStepCommand;
    public ICommand AddAxisStepCommand => _addAxisStepCommand;
    public ICommand AddCameraStepCommand => _addCameraStepCommand;
    public ICommand AddSignalStepCommand => _addSignalStepCommand;
    public ICommand AddSubsequenceStepCommand => _addSubsequenceStepCommand;
    public ICommand AddUnitStepCommand => _addUnitStepCommand;
    public ICommand AddTransferStepCommand => _addTransferStepCommand;
    public ICommand AddWorkpieceStepCommand => _addWorkpieceStepCommand;
    public ICommand CopyTeachingPreviewCommand => _copyTeachingPreviewCommand;
    public ICommand AddUnitSequenceCommand => _addUnitSequenceCommand;
    public ICommand ApplyUnitRepeatCommand => _applyUnitRepeatCommand;
    public ICommand DeleteStepCommand => _deleteStepCommand;
    public ICommand MoveStepUpCommand => _moveStepUpCommand;
    public ICommand MoveStepDownCommand => _moveStepDownCommand;

    internal void InvalidateCommands()
    {
        OnPropertyChanged(nameof(CanChooseStepTemplate));
        _findNextStepCommand.RaiseCanExecuteChanged();
        _clearStepSearchCommand.RaiseCanExecuteChanged();
        ((RelayCommand)_addStepCommand).RaiseCanExecuteChanged();
        _addAxisStepCommand.RaiseCanExecuteChanged();
        _addCameraStepCommand.RaiseCanExecuteChanged();
        _addSignalStepCommand.RaiseCanExecuteChanged();
        _addSubsequenceStepCommand.RaiseCanExecuteChanged();
        _addUnitStepCommand.RaiseCanExecuteChanged();
        _addTransferStepCommand.RaiseCanExecuteChanged();
        _addWorkpieceStepCommand.RaiseCanExecuteChanged();
        _copyTeachingPreviewCommand.RaiseCanExecuteChanged();
        ((RelayCommand)_applyUnitRepeatCommand).RaiseCanExecuteChanged();
        ((RelayCommand)_deleteStepCommand).RaiseCanExecuteChanged();
        ((RelayCommand)_moveStepUpCommand).RaiseCanExecuteChanged();
        ((RelayCommand)_moveStepDownCommand).RaiseCanExecuteChanged();
    }

    public event EventHandler<SequenceEditorChangedEventArgs>? DefinitionChanged;
    public event EventHandler? AxisStepAdded;
    public event EventHandler? CameraStepAdded;
    public event EventHandler? SignalStepAdded;
    public event EventHandler? SubsequenceStepAdded;
    public event EventHandler? UnitStepAdded;
    public event EventHandler? TransferStepAdded;
    public event EventHandler? WorkpieceStepAdded;

    public void Load(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _stepTestCancellation?.Cancel();
        DetachStepDraft();
        _selectedStep = null;
        _selectedSequence = null;
        _runtimeSequenceId = null;
        _runtimeStepId = null;
        _project = project;
        _axisStepTargetId = string.Empty;
        _axisStepTemplateId = "move-axis-home";
        _cameraStepTargetId = string.Empty;
        _signalStepTargetId = string.Empty;
        _signalStepTemplateId = string.Empty;
        _subsequenceStepTargetId = string.Empty;
        _workpieceStepTargetId = string.Empty;
        _workpieceStepTemplateId = string.Empty;
        _transferStepOwnerId = string.Empty;
        _transferStepTargetId = string.Empty;
        _transferStepDestinationId = string.Empty;
        _unitStepTargetId = string.Empty;
        _unitStepTemplateId = string.Empty;
        TaskGroups.Clear();
        ProcessLanes.Clear();
        _expandedTaskGroups.Clear();
        _taskSequenceId = null;
        IsProcessAllUnits = false;
        IsProcessSummary = true;
        FlowZoom = 1;
        ProcessViewIndex = 0;
        IsProcessSceneVisible = false;
        _stepSearchText = string.Empty;
        OnPropertyChanged(nameof(StepSearchText));
        OnPropertyChanged(nameof(AvailableUnits));
        ResetValidationComparison();
        string? preferredId = project.Simulation.AutomaticRun?.SequenceId
            ?? SelectedSequence?.Id
            ?? project.Sequences.FirstOrDefault()?.Id;

        Sequences.Clear();
        foreach (SequenceDefinition sequence in project.Sequences)
        {
            Sequences.Add(sequence);
        }
        OnPropertyChanged(nameof(HasSequences));

        SelectedSequence = Sequences.FirstOrDefault(sequence =>
            string.Equals(sequence.Id, preferredId, StringComparison.Ordinal))
            ?? Sequences.FirstOrDefault();
        ReloadUnitRepeatDraft();
        if (SelectedSequence is null)
        {
            LoadAuthoringTargets();
            LoadSteps();
        }
    }

    public void RefreshAuthoringTargets()
    {
        if (!ResolvePendingStepDraft()) return;
        string? selectedStepId = SelectedStep?.Id;
        LoadAuthoringTargets();
        LoadSteps(selectedStepId);
    }

    public void ApplyRuntimeStep(string? sequenceId, string? stepId)
    {
        if (_disposed || (string.Equals(_runtimeSequenceId, sequenceId, StringComparison.Ordinal)
            && string.Equals(_runtimeStepId, stepId, StringComparison.Ordinal)))
        {
            return;
        }

        _runtimeSequenceId = sequenceId;
        _runtimeStepId = stepId;
        UpdateExecutionStepIndicators();
    }

    private void UpdateExecutionStepIndicators()
    {
        bool selectedSequenceIsRunning = string.Equals(SelectedSequence?.Id, _runtimeSequenceId, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(_runtimeStepId);
        foreach (SequenceStepEditorItem step in Steps)
        {
            step.IsExecuting = selectedSequenceIsRunning && string.Equals(step.Id, _runtimeStepId, StringComparison.Ordinal);
        }
        OnPropertyChanged(nameof(HasExecutingStep));
        OnPropertyChanged(nameof(StepListCountText));
        OnPropertyChanged(nameof(CurrentRuntimeStepName));
        RefreshTaskIndicators();
    }

    private void LoadAuthoringTargets()
    {
        SequenceAuthoringTargetCatalogSnapshot targetCatalog = _targetCatalog.Build(_project, SelectedSequence?.UnitId);
        _authoringTargets = targetCatalog.AuthoringTargets;
        _workpieceTargets = targetCatalog.WorkpieceTargets;
        _expectedStateTargets = targetCatalog.ExpectedStateTargets;
        string? preferredTemplateId = SelectedTemplate?.Id;
        Templates.Clear();
        foreach (SequenceStepTemplateDefinition template in
                 _templateCatalog.GetAvailableTemplates(_targetCatalog.GetTargetsForSequence(_authoringTargets, SelectedSequence)))
        {
            Templates.Add(template);
        }
        SelectedTemplate = Templates.FirstOrDefault(template =>
            string.Equals(template.Id, preferredTemplateId, StringComparison.Ordinal))
            ?? Templates.FirstOrDefault();
        OnPropertyChanged(nameof(HasTemplates));
        RefreshAxisStepAuthoring(refreshTargets: true);
        RefreshCameraStepAuthoring(refreshTargets: true);
        RefreshSignalStepAuthoring(refreshTargets: true);
        RefreshSubsequenceStepAuthoring(refreshTargets: true);
        RefreshUnitStepAuthoring(refreshTargets: true);
        RefreshTransferStepAuthoring(refreshTargets: true);
        RefreshWorkpieceStepAuthoring(refreshTargets: true);
    }

    public void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        RefreshAxisStepAuthoring(refreshTargets: true);
        RefreshCameraStepAuthoring(refreshTargets: true);
        RefreshSignalStepAuthoring(refreshTargets: true);
        RefreshSubsequenceStepAuthoring(refreshTargets: true);
        RefreshUnitStepAuthoring(refreshTargets: true);
        RefreshTransferStepAuthoring(refreshTargets: true);
        RefreshWorkpieceStepAuthoring(refreshTargets: true);
        OnPropertyChanged(nameof(AvailableUnits));
        OnPropertyChanged(nameof(SelectedUnitId));
        OnPropertyChanged(nameof(Sequences));
        OnPropertyChanged(nameof(SelectedSequence));
        OnPropertyChanged(nameof(Templates));
        OnPropertyChanged(nameof(SelectedTemplate));
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(StepListCountText));
        OnPropertyChanged(nameof(SequenceUnitNameText));
        OnPropertyChanged(nameof(StructuralEditStatus));
        OnPropertyChanged(nameof(ValidationComparisonText));
        _stepEditors.RefreshLocalization();
        RefreshStepSearch();
        SelectedStepDraft?.RefreshLocalization();
        RefreshStepDraftState();
        OnPropertyChanged(nameof(StepTestStatusText));
        OnPropertyChanged(nameof(CurrentRuntimeStepName));
        Validate();
    }

    public void SelectSequence(string sequenceId)
    {
        SelectedSequence = Sequences.FirstOrDefault(sequence =>
            string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal));
    }

    public void SelectStep(string stepId)
    {
        SequenceDefinition? owner = Sequences.FirstOrDefault(sequence =>
            sequence.Steps.Any(step => string.Equals(step.Id, stepId, StringComparison.Ordinal)));
        if (owner is null)
        {
            return;
        }

        SelectStep(owner.Id, stepId);
    }

    public void SelectStep(string sequenceId, string stepId)
    {
        SelectedSequence = Sequences.FirstOrDefault(sequence =>
            string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal));
        SelectedStep = Steps.FirstOrDefault(step =>
            string.Equals(step.Id, stepId, StringComparison.Ordinal));
    }

    public string? TryAddStepForTarget(string targetId)
    {
        SequenceAuthoringTarget? target = _targetCatalog.GetTargetsForSequence(_authoringTargets, SelectedSequence).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, targetId, StringComparison.Ordinal));
        if (_disposed || !IsEditable || IsStepTestBusy || SelectedSequence is null || target is null || !ResolvePendingStepDraft())
        {
            StructuralEditStatus = OpenVisionLanguageService.T(
                "Sequence.SelectEditableHint",
                "편집 가능한 시퀀스와 호환되는 대상을 선택하세요.",
                "Select an editable sequence and a compatible target.");
            return null;
        }

        string templateId = target.Kind switch
        {
            SequenceAuthoringTargetKind.DigitalInput => "wait-input-on",
            SequenceAuthoringTargetKind.DigitalOutput => "set-output-on",
            SequenceAuthoringTargetKind.Axis => "move-axis-home",
            SequenceAuthoringTargetKind.Camera => "trigger-camera",
            SequenceAuthoringTargetKind.Subsequence => "call-subsequence",
            _ => string.Empty
        };
        int ordinal = NextStepOrdinal(SelectedSequence);
        SequenceStepDraftResult draft = _templateCatalog.CreateDraft(
            templateId,
            $"step-{ordinal}",
            [target]);
        if (!draft.IsCreated || draft.Step is null)
        {
            StructuralEditStatus = draft.Message;
            return null;
        }

        SequenceEditResult result = _editor.InsertBeforeTerminal(SelectedSequence, draft.Step);
        ApplyStructuralResult(result, draft.Step.Id);
        return result.IsAccepted ? draft.Step.Id : null;
    }

    private void LoadSteps(string? selectedStepId = null)
    {
        RememberTaskExpansion();
        // 새 선택 알림이 같은 ID의 이전 그룹을 펼치지 않도록 먼저 표시 자료를 비운다.
        TaskGroups.Clear();
        ProcessLanes.Clear();
        _taskSequenceId = null;
        _isLoadingSteps = true;
        _stepEditors.Clear();
        SelectedStep = null;
        if (SelectedSequence is not null)
        {
            _stepEditors.Populate(SelectedSequence, _authoringTargets, _workpieceTargets, _expectedStateTargets, _project.Axes);
            SelectedStep = _stepEditors.Find(selectedStepId) ?? Steps.FirstOrDefault();
        }

        _isLoadingSteps = false;
        RefreshStepSearch();
        UpdateExecutionStepIndicators();

        Validate();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshStepSearch()
    {
        string query = StepSearchText.Trim();
        _stepSearchMatchCount = 0;
        _nextStepSearchIndex = 0;
        var scope = SearchScopeSteps().ToHashSet();
        foreach (var step in Steps.Concat(ProcessLanes.SelectMany(lane => lane.Steps)).Distinct())
        {
            step.IsSearchMatch = scope.Contains(step) && query.Length > 0
                && $"{step.Id} {step.DisplayName} {step.StepListTargetText}".Contains(query, StringComparison.OrdinalIgnoreCase);
            if (step.IsSearchMatch) _stepSearchMatchCount++;
        }
        OnPropertyChanged(nameof(StepSearchMatchCount));
        OnPropertyChanged(nameof(StepSearchStatusText));
        _findNextStepCommand.RaiseCanExecuteChanged();
        _clearStepSearchCommand.RaiseCanExecuteChanged();
        RefreshTaskIndicators();
    }

    private void RefreshTaskGroups()
    {
        RememberTaskExpansion();
        TaskGroups.Clear();
        _taskSequenceId = SelectedSequence?.Id;
        if (SelectedSequence is null) { RefreshProcessLanes(); return; }
        _expandedTaskGroups.TryGetValue(SelectedSequence.Id, out var expanded);
        foreach (var group in SequenceTaskOverview.Build(_project, SelectedSequence))
        {
            var item = new SequenceTaskGroupViewModel(group.Kind, group.StepIndices.Select(index => Steps[index]).ToArray());
            item.IsExpanded = expanded?.Contains(item.Key) == true;
            TaskGroups.Add(item);
        }
        RefreshTaskIndicators(revealSelection: true);
        RefreshProcessLanes();
    }

    private void RememberTaskExpansion()
    {
        if (_taskSequenceId is not null) _expandedTaskGroups[_taskSequenceId] = TaskGroups.Where(group => group.IsExpanded).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var lane in ProcessLanes)
        {
            if (lane.Definition is not { } sequence || sequence.Id == _taskSequenceId || !lane.Groups.Any(group => group.IsGroup)) continue;
            _expandedTaskGroups[sequence.Id] = lane.Groups.Where(group => group.IsExpanded).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        }
    }

    private IEnumerable<SequenceStepEditorItem> SearchScopeSteps() => IsProcessAllUnits
        ? ProcessLanes.SelectMany(lane => lane.Steps) : Steps;

    private void RefreshProcessLanes()
    {
        if (_disposed) return;
        RememberTaskExpansion();
        ProcessLanes.Clear();
        if (!IsProcessAllUnits)
        {
            if (SelectedSequence is { } selected) AddLane(selected, SequenceUnitNameText);
        }
        else
        {
            var units = _project.Stations.SelectMany(station => station.Units).ToArray();
            foreach (var unit in units)
            {
                var sequences = Sequences.Where(sequence => sequence.UnitId == unit.Id).ToArray();
                if (sequences.Length == 0) AddLane(null, unit.Name);
                foreach (var sequence in sequences) AddLane(sequence, unit.Name);
            }
            foreach (var sequence in Sequences.Where(sequence => !units.Any(unit => unit.Id == sequence.UnitId))) AddLane(sequence, sequence.Name);
        }
        RefreshTaskIndicators(revealSelection: true);
        _selectOverviewStepCommand.RaiseCanExecuteChanged();
        _beginRouteDestinationSelectionCommand.RaiseCanExecuteChanged();
        RefreshStepSearch();

        void AddLane(SequenceDefinition? sequence, string name)
        {
            IReadOnlyList<SequenceStepEditorItem> steps = Array.Empty<SequenceStepEditorItem>();
            IReadOnlyList<SequenceValidationIssue> issues = Array.Empty<SequenceValidationIssue>();
            IReadOnlyList<SequenceTaskGroupViewModel> groups = Array.Empty<SequenceTaskGroupViewModel>();
            if (sequence is not null)
            {
                bool selected = ReferenceEquals(sequence, SelectedSequence);
                if (selected) { steps = Steps.ToArray(); issues = ValidationIssues.ToArray(); }
                else
                {
                    var targets = _targetCatalog.Build(_project, sequence.UnitId);
                    // Other lanes are display snapshots; retain no second editing session or subscriptions.
                    using var collection = new SequenceStepEditorCollection(_templateCatalog, _targetCatalog);
                    collection.Populate(sequence, targets.AuthoringTargets, targets.WorkpieceTargets, targets.ExpectedStateTargets, _project.Axes);
                    steps = collection.Items.ToArray();
                    int repeat = _project.Stations.SelectMany(station => station.Units).FirstOrDefault(unit => unit.Id == sequence.UnitId)?.RepeatCount ?? 1;
                    var errors = new SequenceCompiler().Compile(sequence, _targetCatalog.BuildCompilationTargets(_project), repeat).Errors;
                    collection.SetValidation(errors);
                    issues = errors.Select(error => new SequenceValidationIssue(error.Code, sequence.Id, error.StepId, null, ResolveValidationProperty(error.Code), error.Message)).ToArray();
                }
                groups = IsProcessSummary && selected ? TaskGroups.ToArray()
                    : IsProcessSummary ? SequenceTaskOverview.Build(_project, sequence).Select(group => new SequenceTaskGroupViewModel(group.Kind, group.StepIndices.Select(index => steps[index]).ToArray())).ToArray()
                    : steps.Select(step => new SequenceTaskGroupViewModel(SequenceTaskKind.Step, new[] { step })).ToArray();
                if (IsProcessSummary && !selected && _expandedTaskGroups.TryGetValue(sequence.Id, out var expanded))
                    foreach (var group in groups) group.IsExpanded = expanded.Contains(group.Key);
            }
            ProcessLanes.Add(new SequenceTaskLaneViewModel(sequence, name, steps, groups, issues));
        }
    }

    private bool CanSelectOverviewStep(object? parameter) => !_disposed && !_isResolvingStepDraft && parameter is SequenceStepEditorItem step
        && (IsSelectingRouteDestination ? IsStepDraftEditable && CanSelectOverviewSequence?.Invoke() != false && IsProcessOverview
            && SelectedStep is { } source && source.DefinitionSnapshot == _stepDraftBase && Steps.Contains(step) && SelectedSequence is { } sequence
            && _project.Sequences.Contains(sequence) && sequence.Steps.Count(candidate => candidate.Id == step.Id) == 1
            && sequence.Steps.ElementAtOrDefault(source.Order - 1)?.Id == source.Id
            : Steps.Contains(step) || (CanSelectOverviewSequence?.Invoke() != false && ProcessLanes.Any(lane => lane.Steps.Contains(step))));

    private bool CanBeginRouteDestinationSelection(object? parameter) => !_disposed && !_isResolvingStepDraft && !IsSelectingRouteDestination
        && !IsStepTestBusy && IsProcessOverview && CanSelectOverviewSequence?.Invoke() != false && parameter is SequenceTaskRoute route
        && ProcessLanes.Any(lane => lane.Routes.Any(candidate => ReferenceEquals(candidate, route))) && CanSelectOverviewStep(route.Source);

    private void BeginRouteDestinationSelection(object? parameter)
    {
        if (!CanBeginRouteDestinationSelection(parameter) || parameter is not SequenceTaskRoute route) return;
        var sequence = ProcessLanes.First(lane => lane.Routes.Any(candidate => ReferenceEquals(candidate, route))).Definition;
        var source = sequence?.Steps.ElementAtOrDefault(route.Source.Order - 1);
        var project = _project;
        SelectOverviewStep(route.Source);
        var selected = SelectedStep;
        if (_disposed || !ReferenceEquals(project, _project) || !IsStepDraftEditable || !IsProcessOverview || CanSelectOverviewSequence?.Invoke() == false || !ReferenceEquals(SelectedSequence, sequence)
            || source is null || selected is null || !ReferenceEquals(sequence?.Steps.ElementAtOrDefault(selected.Order - 1), source)
            || source.Id != selected.Id || selected.Action is SequenceStepAction.Complete or SequenceStepAction.None
            || route.PropertyName is not (nameof(SequenceStepEditorItem.NextStepId) or nameof(SequenceStepEditorItem.ErrorStepId) or nameof(SequenceStepEditorItem.FailureStepId))
            || (route.PropertyName == nameof(SequenceStepEditorItem.FailureStepId) && !selected.HasFailureRoute)) return;
        IsProcessSummary = false;
        _routeDestinationProperty = route.PropertyName;
        RefreshRouteDestinationSelection();
        OnPropertyChanged(nameof(SelectedStep));
    }

    private void CancelRouteDestinationSelection()
    {
        if (!IsSelectingRouteDestination) return;
        _routeDestinationProperty = null;
        RefreshRouteDestinationSelection();
    }

    private void RefreshRouteDestinationSelection()
    {
        OnPropertyChanged(nameof(IsSelectingRouteDestination));
        OnPropertyChanged(nameof(RouteDestinationSelectionText));
        _beginRouteDestinationSelectionCommand.RaiseCanExecuteChanged();
        _cancelRouteDestinationSelectionCommand.RaiseCanExecuteChanged();
        _selectOverviewStepCommand.RaiseCanExecuteChanged();
    }

    private void SelectOverviewStep(object? parameter)
    {
        if (!CanSelectOverviewStep(parameter) || parameter is not SequenceStepEditorItem step) return;
        if (IsSelectingRouteDestination)
        {
            if (SelectedStepDraft is not { } draft) return;
            switch (_routeDestinationProperty)
            {
                case nameof(SequenceStepEditorItem.NextStepId): draft.NextStepId = step.Id; break;
                case nameof(SequenceStepEditorItem.ErrorStepId): draft.ErrorStepId = step.Id; break;
                case nameof(SequenceStepEditorItem.FailureStepId): draft.FailureStepId = step.Id; break;
            }
            CancelRouteDestinationSelection();
            OnPropertyChanged(nameof(SelectedStep));
            return;
        }
        if (Steps.Contains(step)) { SelectedStep = step; return; }
        var lane = ProcessLanes.First(item => item.Steps.Contains(step));
        var sequence = lane.Definition;
        var target = sequence?.Steps.ElementAtOrDefault(step.Order - 1);
        var project = _project;
        if (sequence is null || target is null) return;
        var navigate = SelectOverviewSequence;
        if (navigate is not null)
        {
            if (!navigate(sequence)) return;
        }
        else
        {
            if (!ResolvePendingStepDraft()) return;
        }
        if (_disposed || !ReferenceEquals(project, _project) || !_project.Sequences.Contains(sequence) || !sequence.Steps.Contains(target)) return;
        if (navigate is null) SelectedSequence = sequence;
        if (!ReferenceEquals(SelectedSequence, sequence)) return;
        SelectedStep = Steps.ElementAtOrDefault(sequence.Steps.IndexOf(target));
    }

    private void RefreshTaskIndicators(bool revealSelection = false)
    {
        foreach (var step in Steps) step.IsEditingSelection = ReferenceEquals(step, SelectedStep);
        foreach (var group in TaskGroups)
        {
            group.Refresh(SelectedStep?.Id, ValidationIssues);
            if (revealSelection && group.IsSelected) group.IsExpanded = true;
        }
        foreach (var lane in ProcessLanes)
        {
            bool selected = ReferenceEquals(lane.Definition, SelectedSequence);
            foreach (var step in lane.Steps)
            {
                step.IsEditingSelection = selected && ReferenceEquals(step, SelectedStep);
                step.IsExecuting = step.SequenceId == _runtimeSequenceId && step.Id == _runtimeStepId;
            }
            foreach (var group in lane.Groups)
            {
                group.Refresh(selected ? SelectedStep?.Id : null, lane.Issues);
                if (revealSelection && group.IsSelected) group.IsExpanded = true;
            }
        }
    }

    private void FindNextStep()
    {
        if (_disposed || _isResolvingStepDraft) return;
        var matches = SearchScopeSteps().Where(step => step.IsSearchMatch).ToArray();
        if (matches.Length == 0) return;
        var next = matches[_nextStepSearchIndex % matches.Length];
        CancelRouteDestinationSelection();
        SelectOverviewStep(next);
        if (SelectedSequence?.Id != next.SequenceId || SelectedStep?.Id != next.Id) return;
        if (IsProcessOverview) { IsProcessSummary = false; FlowZoom = 1; }
        _nextStepSearchIndex = Math.Max(0, Array.FindIndex(SearchScopeSteps().Where(step => step.IsSearchMatch).ToArray(), step => step.SequenceId == next.SequenceId && step.Order == next.Order) + 1);
        OnPropertyChanged(nameof(SelectedStep));
    }

    private void AddStep(object? parameter, string? axisTargetId = null, string? cameraTargetId = null, string? signalTargetId = null,
        string? subsequenceTargetId = null, string? unitTargetId = null, string? transferTargetId = null, string? transferDestinationId = null,
        string? workpieceTargetId = null)
    {
        if (parameter is not null && (parameter is not SequenceStepTemplateDefinition supplied || !Templates.Contains(supplied))) return;
        var sequence = SelectedSequence;
        string? transferOwnerId = transferTargetId is not null ? UnitStepTarget?.Id : null;
        var template = parameter as SequenceStepTemplateDefinition ?? SelectedTemplate;
        if (_disposed || !IsEditable || IsStepTestBusy || !ResolvePendingStepDraft() || SelectedSequence is null || template is null)
        {
            return;
        }

        var targets = _targetCatalog.GetTargetsForSequence(_authoringTargets, SelectedSequence);
        if (axisTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || axisTargetId != _axisStepTargetId || template.Id != _axisStepTemplateId || !CanChangeStructure()) return;
            var target = AxisStepTargets.SingleOrDefault(candidate => candidate.Id == axisTargetId);
            if (target is null) { RefreshAxisStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        if (cameraTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || cameraTargetId != _cameraStepTargetId || template.Id != "trigger-camera" || !Templates.Contains(template) || !CanChangeStructure()) return;
            var target = CameraStepTargets.SingleOrDefault(candidate => candidate.Id == cameraTargetId);
            if (target is null) { RefreshCameraStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        if (signalTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || signalTargetId != _signalStepTargetId || template.Id != _signalStepTemplateId || !Templates.Contains(template) || !CanChangeStructure()) return;
            var target = SignalStepTargets.SingleOrDefault(candidate => candidate.Id == signalTargetId);
            if (target is null || !SignalStepTemplates.Contains(template)) { RefreshSignalStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        if (subsequenceTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || subsequenceTargetId != _subsequenceStepTargetId || template.Id != "call-subsequence" || !Templates.Contains(template) || !CanChangeStructure()) return;
            var target = SubsequenceStepTargets.SingleOrDefault(candidate => candidate.Id == subsequenceTargetId);
            if (target is null) { RefreshSubsequenceStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        if (unitTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || unitTargetId != _unitStepTargetId || template.Id != _unitStepTemplateId || !Templates.Contains(template) || !CanChangeStructure()) return;
            var target = UnitStepTarget;
            if (target?.Id != unitTargetId || !UnitStepTemplates.Contains(template)) { RefreshUnitStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        if (transferTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || transferTargetId != _transferStepTargetId || transferDestinationId != _transferStepDestinationId
                || transferOwnerId != _transferStepOwnerId || transferOwnerId != UnitStepTarget?.Id
                || template.Id != "transfer-unit" || !Templates.Contains(template) || !CanChangeStructure()) return;
            var target = TransferStepTarget;
            if (target is null || TransferStepDestination is null) { RefreshTransferStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        if (workpieceTargetId is not null)
        {
            if (!ReferenceEquals(sequence, SelectedSequence) || !_project.Sequences.Contains(SelectedSequence)
                || workpieceTargetId != _workpieceStepTargetId || template.Id != _workpieceStepTemplateId || !Templates.Contains(template) || !CanChangeStructure()) return;
            var target = WorkpieceStepTarget;
            if (target is null || !WorkpieceStepTemplates.Contains(template)) { RefreshWorkpieceStepAuthoring(refreshTargets: true); return; }
            targets = [target];
        }
        int ordinal = NextStepOrdinal(SelectedSequence);
        SequenceStepDraftResult draft = _templateCatalog.CreateDraft(template.Id, $"step-{ordinal}", targets);
        if (!draft.IsCreated || draft.Step is null)
        {
            StructuralEditStatus = draft.Message;
            return;
        }

        SequenceStepDefinition step = draft.Step;
        if (transferTargetId is not null) step.DestinationUnitId = transferDestinationId;
        if (workpieceTargetId is not null) step.WorkpieceComponentId = workpieceTargetId;
        SequenceEditResult result = _editor.InsertBeforeTerminal(SelectedSequence, step);
        ApplyStructuralResult(result, step.Id);
        if (axisTargetId is not null && result.IsAccepted)
        {
            _axisStepTargetId = string.Empty;
            RefreshAxisStepAuthoring();
            AxisStepAdded?.Invoke(this, EventArgs.Empty);
        }
        if (cameraTargetId is not null && result.IsAccepted)
        {
            _cameraStepTargetId = string.Empty;
            RefreshCameraStepAuthoring();
            CameraStepAdded?.Invoke(this, EventArgs.Empty);
        }
        if (signalTargetId is not null && result.IsAccepted)
        {
            _signalStepTargetId = string.Empty;
            _signalStepTemplateId = string.Empty;
            RefreshSignalStepAuthoring();
            SignalStepAdded?.Invoke(this, EventArgs.Empty);
        }
        if (subsequenceTargetId is not null && result.IsAccepted)
        {
            _subsequenceStepTargetId = string.Empty;
            RefreshSubsequenceStepAuthoring();
            SubsequenceStepAdded?.Invoke(this, EventArgs.Empty);
        }
        if (unitTargetId is not null && result.IsAccepted)
        {
            _unitStepTemplateId = string.Empty;
            RefreshUnitStepAuthoring();
            UnitStepAdded?.Invoke(this, EventArgs.Empty);
        }
        if (transferTargetId is not null && result.IsAccepted)
        {
            _transferStepTargetId = string.Empty;
            _transferStepDestinationId = string.Empty;
            RefreshTransferStepAuthoring();
            TransferStepAdded?.Invoke(this, EventArgs.Empty);
        }
        if (workpieceTargetId is not null && result.IsAccepted)
        {
            _workpieceStepTargetId = string.Empty;
            _workpieceStepTemplateId = string.Empty;
            RefreshWorkpieceStepAuthoring();
            WorkpieceStepAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshWorkpieceStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            if (!WorkpieceStepTargets.Any(target => target.Id == _workpieceStepTargetId)) _workpieceStepTargetId = string.Empty;
            if (!WorkpieceStepTemplates.Any(template => template.Id == _workpieceStepTemplateId)) _workpieceStepTemplateId = string.Empty;
            OnPropertyChanged(nameof(WorkpieceStepTargets));
        }
        OnPropertyChanged(nameof(WorkpieceStepTemplates));
        OnPropertyChanged(nameof(WorkpieceStepTargetId));
        OnPropertyChanged(nameof(WorkpieceStepTemplateId));
        OnPropertyChanged(nameof(WorkpieceStepTarget));
        OnPropertyChanged(nameof(WorkpieceStepPreview));
        OnPropertyChanged(nameof(WorkpieceStepPreviewTimeout));
        OnPropertyChanged(nameof(WorkpieceStepHint));
        _addWorkpieceStepCommand.RaiseCanExecuteChanged();
    }

    private void RefreshTransferStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            string ownerId = UnitStepTarget?.Id ?? string.Empty;
            if (_transferStepOwnerId != ownerId) { _transferStepTargetId = string.Empty; _transferStepDestinationId = string.Empty; }
            _transferStepOwnerId = ownerId;
            if (!TransferStepTargets.Any(target => target.Id == _transferStepTargetId)) _transferStepTargetId = string.Empty;
            if (!TransferStepDestinations.Any(target => target.Id == _transferStepDestinationId)) _transferStepDestinationId = string.Empty;
            OnPropertyChanged(nameof(TransferStepTargets));
            OnPropertyChanged(nameof(TransferStepDestinations));
        }
        OnPropertyChanged(nameof(TransferStepTargetId));
        OnPropertyChanged(nameof(TransferStepDestinationId));
        OnPropertyChanged(nameof(TransferStepTarget));
        OnPropertyChanged(nameof(TransferStepDestination));
        OnPropertyChanged(nameof(TransferStepPreview));
        OnPropertyChanged(nameof(TransferStepPreviewValue));
        OnPropertyChanged(nameof(TransferStepPreviewTimeout));
        OnPropertyChanged(nameof(TransferStepHint));
        _addTransferStepCommand.RaiseCanExecuteChanged();
    }

    private void RefreshUnitStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            string targetId = UnitStepTarget?.Id ?? string.Empty;
            if (_unitStepTargetId != targetId || !UnitStepTemplates.Any(template => template.Id == _unitStepTemplateId)) _unitStepTemplateId = string.Empty;
            _unitStepTargetId = targetId;
        }
        OnPropertyChanged(nameof(UnitStepTarget));
        OnPropertyChanged(nameof(UnitStepTargetText));
        OnPropertyChanged(nameof(UnitStepTemplates));
        OnPropertyChanged(nameof(UnitStepTemplateId));
        OnPropertyChanged(nameof(UnitStepPreview));
        OnPropertyChanged(nameof(UnitStepPreviewValue));
        OnPropertyChanged(nameof(UnitStepPreviewTimeout));
        OnPropertyChanged(nameof(UnitStepHint));
        _addUnitStepCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSubsequenceStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            if (!SubsequenceStepTargets.Any(target => target.Id == _subsequenceStepTargetId)) _subsequenceStepTargetId = string.Empty;
            OnPropertyChanged(nameof(SubsequenceStepTargets));
        }
        OnPropertyChanged(nameof(SubsequenceStepTargetId));
        OnPropertyChanged(nameof(SubsequenceStepTarget));
        OnPropertyChanged(nameof(SubsequenceStepPreview));
        OnPropertyChanged(nameof(SubsequenceStepPreviewTimeout));
        OnPropertyChanged(nameof(SubsequenceStepHint));
        _addSubsequenceStepCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSignalStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            if (!SignalStepTargets.Any(target => target.Id == _signalStepTargetId)) _signalStepTargetId = string.Empty;
            if (!SignalStepTemplates.Any(template => template.Id == _signalStepTemplateId)) _signalStepTemplateId = string.Empty;
            OnPropertyChanged(nameof(SignalStepTargets));
        }
        OnPropertyChanged(nameof(SignalStepTemplates));
        OnPropertyChanged(nameof(SignalStepTargetId));
        OnPropertyChanged(nameof(SignalStepTemplateId));
        OnPropertyChanged(nameof(SignalStepTarget));
        OnPropertyChanged(nameof(SignalStepTargetKindText));
        OnPropertyChanged(nameof(SignalStepPreview));
        OnPropertyChanged(nameof(SignalStepPreviewValue));
        OnPropertyChanged(nameof(SignalStepPreviewTimeout));
        OnPropertyChanged(nameof(SignalStepHint));
        _addSignalStepCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCameraStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            if (!CameraStepTargets.Any(target => target.Id == _cameraStepTargetId)) _cameraStepTargetId = string.Empty;
            OnPropertyChanged(nameof(CameraStepTargets));
        }
        OnPropertyChanged(nameof(CameraStepTargetId));
        OnPropertyChanged(nameof(CameraStepTarget));
        OnPropertyChanged(nameof(CameraStepPreview));
        OnPropertyChanged(nameof(CameraStepHint));
        OnPropertyChanged(nameof(CameraStepPreviewRecipe));
        OnPropertyChanged(nameof(CameraStepPreviewTimeout));
        _addCameraStepCommand.RaiseCanExecuteChanged();
    }

    private void RefreshAxisStepAuthoring(bool refreshTargets = false)
    {
        if (refreshTargets)
        {
            if (!AxisStepTargets.Any(target => target.Id == _axisStepTargetId)) _axisStepTargetId = string.Empty;
            OnPropertyChanged(nameof(AxisStepTargets));
        }
        OnPropertyChanged(nameof(AxisStepTargetId));
        OnPropertyChanged(nameof(AxisStepTemplateId));
        OnPropertyChanged(nameof(AxisStepTarget));
        OnPropertyChanged(nameof(AxisStepPreview));
        OnPropertyChanged(nameof(AxisStepPreviewValue));
        OnPropertyChanged(nameof(AxisStepPreviewTimeout));
        _addAxisStepCommand.RaiseCanExecuteChanged();
    }

    private void DeleteSelectedStep()
    {
        if (_disposed || !IsEditable || IsStepTestBusy || !ResolvePendingStepDraft() || SelectedSequence is null || SelectedStep is null)
        {
            return;
        }

        SequenceEditResult result = _editor.Delete(SelectedSequence, SelectedStep.Id);
        ApplyStructuralResult(result, null);
    }

    private void MoveSelectedStep(int offset)
    {
        if (_disposed || !IsEditable || IsStepTestBusy || !ResolvePendingStepDraft() || SelectedSequence is null || SelectedStep is null)
        {
            return;
        }

        string selectedId = SelectedStep.Id;
        SequenceEditResult result = _editor.Move(SelectedSequence, selectedId, offset);
        ApplyStructuralResult(result, selectedId);
    }

    private void ApplyStructuralResult(SequenceEditResult result, string? selectedStepId)
    {
        StructuralEditStatus = result.Message;
        if (!result.IsAccepted)
        {
            return;
        }

        LoadSteps(selectedStepId);
        StructuralEditStatus = result.Message;
        DefinitionChanged?.Invoke(this, new SequenceEditorChangedEventArgs(true));
    }

    private bool CanChangeStructure() =>
        !_disposed
        && IsEditable
        && !IsStepTestBusy
        && SelectedSequence is not null
        && SequenceDefinitionEditor.IsStrictLinear(SelectedSequence);

    private bool CanAddStep(object? parameter) =>
        CanChangeStructure() && (parameter is null ? SelectedTemplate is not null : parameter is SequenceStepTemplateDefinition template && Templates.Contains(template));

    private bool CanMoveSelectedStep(int offset)
    {
        if (!CanChangeStructure() || SelectedStep is null || SelectedStep.Action == SequenceStepAction.Complete)
        {
            return false;
        }

        int index = Steps.IndexOf(SelectedStep);
        int target = index + offset;
        return target >= 0 && target < Steps.Count - 1;
    }

    internal void ReloadUnitRepeatDraft()
    {
        UnitRepeatCount = (SequenceUnit?.RepeatCount ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(HasSequenceUnit));
        OnPropertyChanged(nameof(SequenceUnitNameText));
        ((RelayCommand)_applyUnitRepeatCommand).RaiseCanExecuteChanged();
    }

    private void ApplyUnitRepeat()
    {
        if (_disposed || !IsEditable || SequenceUnit is not { } unit) return;
        if (!int.TryParse(UnitRepeatCount, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) || count is < 1 or > 100)
        {
            StructuralEditStatus = OpenVisionLanguageService.T("Sequence.UnitRepeatError");
            return;
        }
        if (unit.RepeatCount == count) return;
        unit.RepeatCount = count;
        StructuralEditStatus = Format("Sequence.UnitRepeatApplied", unit.Name, count);
        Validate();
        DefinitionChanged?.Invoke(this, new SequenceEditorChangedEventArgs(false));
    }

    private void OnStepDefinitionChanged(object? sender, EventArgs args)
    {
        if (!_isApplyingStepDraft && !HasPendingStepDraft) ReloadStepDraft();
        RefreshStepDraftState();
        RefreshStepSearch();
        Validate();
        DefinitionChanged?.Invoke(this, new SequenceEditorChangedEventArgs(false));
    }

    private IReadOnlyList<KeyValuePair<string, string>> InspectorRouteOptions(string id)
    {
        var options = new List<KeyValuePair<string, string>> { new(string.Empty, OpenVisionLanguageService.T("R24.Inspector.Unconfigured")) };
        options.AddRange(Steps.Select(step => new KeyValuePair<string, string>(step.Id, $"{step.Order}. {step.DisplayName} ({step.Id})")));
        if (id.Length > 0 && options.All(option => option.Key != id)) options.Add(new(id, id));
        return options;
    }

    private void DetachStepDraft()
    {
        CancelRouteDestinationSelection();
        if (_selectedStepDraft is not null) _selectedStepDraft.DefinitionChanged -= OnStepDraftChanged;
        _selectedStepDraft = null;
        _stepTeachingName = string.Empty;
        _stepTeachingTargetId = string.Empty;
        _teachingSearchText = string.Empty;
        _teachingPreviewName = string.Empty;
        _teachingPreviewTargetId = string.Empty;
        OnPropertyChanged(nameof(TeachingSearchText));
        RefreshTeachingPositions();
    }

    private void ReloadStepDraft()
    {
        _stepTestCancellation?.Cancel();
        DetachStepDraft();
        _selectedStepDraft = SelectedStep?.CreateDraft();
        _stepDraftBase = SelectedStep?.DefinitionSnapshot ?? string.Empty;
        _stepTeachingName = string.Empty;
        _stepTeachingTargetId = SelectedStepDraft?.TargetId ?? string.Empty;
        _stepTimeoutText = SelectedStepDraft is { } timeoutDraft ? FormatStepTimeout(timeoutDraft.TimeoutMs) : string.Empty;
        _stepEjectionDelayText = SelectedStepDraft?.EjectionDelayMilliseconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        if (_selectedStepDraft is not null) _selectedStepDraft.DefinitionChanged += OnStepDraftChanged;
        StepTestResult = null;
        _stepTestErrorText = string.Empty;
        OnPropertyChanged(nameof(SelectedStepDraft));
        OnPropertyChanged(nameof(StepTimeoutText));
        OnPropertyChanged(nameof(StepEjectionDelayText));
        OnPropertyChanged(nameof(IsStepDraftEditable));
        RefreshStepDraftState();
    }

    private void OnStepDraftChanged(object? sender, EventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, SelectedStepDraft)) return;
        if (TryParseStepTimeout(StepTimeoutText, out int timeout) && timeout != SelectedStepDraft!.TimeoutMs)
        {
            _stepTimeoutText = FormatStepTimeout(SelectedStepDraft.TimeoutMs);
            OnPropertyChanged(nameof(StepTimeoutText));
        }
        if (int.TryParse(StepEjectionDelayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) && delay != SelectedStepDraft!.EjectionDelayMilliseconds)
        {
            _stepEjectionDelayText = SelectedStepDraft.EjectionDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(StepEjectionDelayText));
        }
        StepTestResult = null;
        RefreshStepDraftState();
    }

    private static string FormatStepTimeout(int milliseconds) => (milliseconds / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryParseStepTimeout(string text, out int milliseconds)
    {
        milliseconds = 0;
        string input = text.Trim();
        int decimalPoint = input.IndexOf('.');
        if (decimalPoint >= 0 && input.Length > decimalPoint + 4 && input[(decimalPoint + 4)..].TrimEnd('0').Length != 0) return false;
        if (!decimal.TryParse(input, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds) || seconds < 0 || seconds > int.MaxValue / 1000m) return false;
        decimal value = seconds * 1000m;
        if (value != decimal.Truncate(value)) return false;
        milliseconds = (int)value;
        return true;
    }

    private void UpdateDraftNumbers()
    {
        if (SelectedStepDraft is { } draft)
        {
            if (TryParseStepTimeout(StepTimeoutText, out int timeout)) draft.TimeoutMs = timeout;
            if (int.TryParse(StepEjectionDelayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay)) draft.EjectionDelayMilliseconds = delay;
        }
        RefreshStepDraftState();
    }

    private void RefreshStepDraftState()
    {
        OnPropertyChanged(nameof(HasStepTestStatus));
        OnPropertyChanged(nameof(CanChooseStepTemplate));
        bool parameterInvalid = false;
        StepDraftErrorText = string.Empty;
        if (SelectedStepDraft is { } draft && SelectedSequence is { } sequence)
        {
            if (SelectedStep?.DefinitionSnapshot != _stepDraftBase)
                StepDraftErrorText = OpenVisionLanguageService.T("Sequence.StepDraftStale");
            else if (!TryParseStepTimeout(StepTimeoutText, out int timeout) || timeout < 0
                || !int.TryParse(StepEjectionDelayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay < 0)
                StepDraftErrorText = OpenVisionLanguageService.T("Sequence.StepDraftIntegerError");
            else
            {
                var candidate = JsonSerializer.Deserialize<SequenceDefinition>(JsonSerializer.Serialize(sequence))!;
                int index = candidate.Steps.FindIndex(step => step.Id == draft.Id);
                if (index >= 0) candidate.Steps[index] = draft.CopyDefinition();
                var validation = new SequenceCompiler().Compile(candidate, _targetCatalog.BuildCompilationTargets(_project), SequenceUnit?.RepeatCount ?? 1);
                var errors = validation.Errors.Where(error => error.StepId == draft.Id).ToArray();
                draft.SetValidation(errors);
                parameterInvalid = draft.Action == SequenceStepAction.MoveAxis && errors.Any(error => error.Code == SequenceCompilationErrorCode.InvalidNumericParameter);
                if (errors.Length != 0) StepDraftErrorText = OpenVisionLanguageService.T("Sequence.StepDraftInvalid") + " " + string.Join(" ", errors.Select(error => error.Message));
            }
        }
        IsStepParameterInvalid = parameterInvalid;
        if (_stepTeachingName.Length > 0)
        {
            var axis = SelectedStepDraft?.Action == SequenceStepAction.MoveAxis && SelectedStepDraft.TargetId == _stepTeachingTargetId
                ? _project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, SelectedStepDraft.TargetId, StringComparison.Ordinal)) : null;
            if (axis?.TeachingPositions?.TryGetValue(_stepTeachingName, out double position) != true
                || !double.TryParse(SelectedStepDraft?.Parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out double parameter) || parameter != position)
                _stepTeachingName = string.Empty;
        }
        RefreshTeachingPositions();
        OnPropertyChanged(nameof(StepTeachingOptions));
        OnPropertyChanged(nameof(StepTeachingName));
        OnPropertyChanged(nameof(IsStepAdditionalExpanded));
        OnPropertyChanged(nameof(StepAdditionalStatus));
        OnPropertyChanged(nameof(StepNextCaption));
        OnPropertyChanged(nameof(RouteDestinationSelectionText));
        OnPropertyChanged(nameof(StepAlarmOptions));
        OnPropertyChanged(nameof(StepAlarmOption));
        OnPropertyChanged(nameof(StepFailureOptions));
        OnPropertyChanged(nameof(StepFailureOption));
        OnPropertyChanged(nameof(StepTimeoutHint));
        OnPropertyChanged(nameof(StepNextStepOptions));
        OnPropertyChanged(nameof(StepNextStepId));
        OnPropertyChanged(nameof(StepNextStepOption));
        OnPropertyChanged(nameof(StepTargetCaption));
        OnPropertyChanged(nameof(StepParameterLabel));
        OnPropertyChanged(nameof(StepParameterCaption));
        OnPropertyChanged(nameof(StepParameterUnitText));
        OnPropertyChanged(nameof(HasPendingStepDraft));
        OnPropertyChanged(nameof(StepDraftStatusText));
        OnPropertyChanged(nameof(CanApplyStepDraft));
        OnPropertyChanged(nameof(IsStepTestSupported));
        OnPropertyChanged(nameof(StepTestStatusText));
        _applyStepDraftCommand.RaiseCanExecuteChanged();
        _discardStepDraftCommand.RaiseCanExecuteChanged();
        _testSelectedStepCommand.RaiseCanExecuteChanged();
    }

    private static string FormatTeachingPosition(VirtualAxisDefinition axis, double position) => position.ToString("R", CultureInfo.CurrentCulture)
        + (string.IsNullOrWhiteSpace(axis.Unit) ? string.Empty : " " + axis.Unit);

    private void RefreshTeachingPositions()
    {
        var axis = TeachingAxis;
        string targetId = axis?.Id ?? string.Empty;
        if (_teachingPreviewTargetId != targetId)
        {
            _teachingPreviewTargetId = targetId;
            _teachingSearchText = string.Empty;
            _teachingPreviewName = string.Empty;
            OnPropertyChanged(nameof(TeachingSearchText));
        }
        string search = _teachingSearchText.Trim();
        var positions = (axis?.TeachingPositions ?? []).Where(position => !string.IsNullOrWhiteSpace(position.Key) && double.IsFinite(position.Value))
            .Select(position => new KeyValuePair<string, string>(position.Key, FormatTeachingPosition(axis!, position.Value)))
            .Where(position => search.Length == 0 || position.Key.Contains(search, StringComparison.OrdinalIgnoreCase) || position.Value.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!_teachingPositions.SequenceEqual(positions))
        {
            _teachingPreviewName = string.Empty;
            _teachingPositions = positions;
            OnPropertyChanged(nameof(TeachingPositions));
        }
        OnPropertyChanged(nameof(TeachingPreviewName));
        OnPropertyChanged(nameof(TeachingPreviewValue));
        OnPropertyChanged(nameof(TeachingPositionCountText));
        OnPropertyChanged(nameof(TeachingAxisText));
        _copyTeachingPreviewCommand.RaiseCanExecuteChanged();
    }

    private void CopyTeachingPreview()
    {
        string name = _teachingPreviewName;
        RefreshTeachingPositions();
        if (!CanCopyTeachingPreview || name != _teachingPreviewName) return;
        StepTeachingName = string.Empty;
        StepTeachingName = name;
    }

    internal bool TryApplyStepDraft()
    {
        RefreshStepDraftState();
        if (!CanApplyStepDraft || SelectedStepDraft is not { } draft || SelectedStep is null) return !HasPendingStepDraft;
        _isApplyingStepDraft = true;
        try { SelectedStep.ApplyDraft(draft.CopyDefinition()); }
        finally { _isApplyingStepDraft = false; }
        ReloadStepDraft();
        return true;
    }

    internal void DiscardStepDraft()
    {
        if (_disposed || !IsEditable || IsStepTestBusy) return;
        ReloadStepDraft();
    }

    internal bool ResolvePendingStepDraft()
    {
        if (!HasPendingStepDraft) return true;
        if (_isResolvingStepDraft || _disposed || !IsEditable || IsStepTestBusy) return false;
        _isResolvingStepDraft = true;
        try
        {
            switch (PendingStepDraftPrompt())
            {
                case PlacementDraftDecision.Apply: return TryApplyStepDraft();
                case PlacementDraftDecision.Discard: DiscardStepDraft(); return true;
                default: return false;
            }
        }
        finally { _isResolvingStepDraft = false; }
    }

    internal async Task TestSelectedStepAsync()
    {
        if (!CanTestSelectedStep || !ResolvePendingStepDraft() || SelectedSequence is null || SelectedStep is null) return;
        if (!CanTestSelectedStep) return;
        var project = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(_project));
        string sequenceId = SelectedSequence.Id, stepId = SelectedStep.Id, targetId = SelectedStep.TargetId;
        string? deviceId = project.Devices.FirstOrDefault(device => device.ChannelIds.Contains(targetId))?.Id;
        var layout = project.Simulation.ActiveLayoutId is { Length: > 0 } layoutId
            ? project.Layouts.FirstOrDefault(candidate => candidate.Id == layoutId)
            : project.Layouts.Count == 1 ? project.Layouts[0] : null;
        string componentId = layout?.Components.FirstOrDefault(component => component.BehaviorBindingId == targetId
            || (deviceId is not null && component.BehaviorBindingId == deviceId))?.Id ?? string.Empty;
        using var cancellation = new CancellationTokenSource();
        _stepTestCancellation = cancellation;
        _stepTestErrorText = string.Empty;
        IsStepTestBusy = true;
        StepTestResult = null;
        try
        {
            var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(project, sequenceId, stepId, componentId, cancellationToken: cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested && SelectedSequence?.Id == sequenceId && SelectedStep?.Id == stepId) StepTestResult = result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_stepTestCancellation, cancellation)) _stepTestCancellation = null;
            IsStepTestBusy = false;
        }
    }

    private void AddUnitSequence()
    {
        if (_disposed || !IsEditable || IsStepTestBusy || !ResolvePendingStepDraft()) return;
        var unit = _project.Stations.SelectMany(station => station.Units).FirstOrDefault(unit => !_project.Sequences.Any(sequence => sequence.UnitId == unit.Id));
        if (unit is null) return;
        string id = "sequence." + unit.Id;
        for (int ordinal = 2; _project.Sequences.Any(sequence => sequence.Id == id); ordinal++) id = "sequence." + unit.Id + "." + ordinal;
        var definition = new SequenceDefinition { Id = id, Name = Format("Sequence.UnitSequenceName", unit.Name), UnitId = unit.Id, WatchdogTimeoutMs = 0 };
        definition.Steps.Add(new SequenceStepDefinition { Id = "complete", Name = OpenVisionLanguageService.T("Sequence.Complete"), Action = SequenceStepAction.Complete });
        _project.Sequences.Add(definition);
        Sequences.Add(definition);
        SelectedSequence = definition;
        OnPropertyChanged(nameof(HasSequences));
        DefinitionChanged?.Invoke(this, new SequenceEditorChangedEventArgs(true));
    }

    private void Validate()
    {
        var previousIssueKey = SelectedValidationIssue is null
            ? null
            : GetIssueKey(SelectedValidationIssue);
        ValidationMessages.Clear();
        ValidationIssues.Clear();
        if (SelectedSequence is null)
        {
            ValidationSummary = OpenVisionLanguageService.T(
                "Sequence.NoSequenceSelected",
                "선택한 시퀀스 없음",
                "No sequence selected");
            StructuralEditStatus = OpenVisionLanguageService.T(
                "Sequence.SelectSequenceHint",
                "시퀀스를 선택해 단계를 편집하세요.",
                "Select a sequence to edit steps.");
            UpdateValidationComparison(0, string.Empty);
            RefreshTaskGroups();
            return;
        }

        SequenceCompilationResult result = new SequenceCompiler().Compile(
            SelectedSequence,
            _targetCatalog.BuildCompilationTargets(_project),
            _project.Stations.SelectMany(station => station.Units).FirstOrDefault(unit => unit.Id == SelectedSequence.UnitId)?.RepeatCount ?? 1);
        foreach (SequenceCompilationError error in result.Errors)
        {
            ValidationMessages.Add($"{error.Code} [{error.StepId ?? "sequence"}]: {error.Message}");
            var step = SelectedSequence.Steps.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, error.StepId, StringComparison.Ordinal));
            ValidationIssues.Add(new SequenceValidationIssue(
                error.Code,
                SelectedSequence.Id,
                error.StepId,
                step?.TargetId,
                ResolveValidationProperty(error.Code),
                error.Message));
        }

        _selectedValidationIssue = ValidationIssues.FirstOrDefault(issue =>
            string.Equals(GetIssueKey(issue), previousIssueKey, StringComparison.Ordinal));
        OnPropertyChanged(nameof(SelectedValidationIssue));

        _stepEditors.SetValidation(result.Errors);

        ValidationSummary = result.IsSuccess
            ? Format("Sequence.ValidSummary", Steps.Count)
            : Format("Sequence.InvalidSummary", result.Errors.Count);
        StructuralEditStatus = SequenceDefinitionEditor.IsStrictLinear(SelectedSequence)
            ? OpenVisionLanguageService.T(
                "Sequence.StructuralLinear",
                "선형 경로: 설계 모드에서 추가·삭제·순서 변경을 사용할 수 있습니다.",
                "Linear path: add, remove, and reorder are available in Design mode.")
            : OpenVisionLanguageService.T(
                "Sequence.StructuralBranched",
                "분기 경로: 필드만 편집할 수 있으며 구조 명령은 잠겨 있습니다.",
                "Branched path: edit fields only; structural commands are locked.");
        UpdateValidationComparison(
            result.Errors.Count,
            string.Join("|", ValidationIssues.Select(GetIssueKey)));
        RefreshTaskGroups();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ResetValidationComparison()
    {
        _hasValidationSnapshot = false;
        _lastValidationSignature = string.Empty;
        _lastValidationErrorCount = 0;
        _validationComparisonText = string.Empty;
        _selectedValidationIssue = null;
        OnPropertyChanged(nameof(SelectedValidationIssue));
        OnPropertyChanged(nameof(ValidationComparisonText));
    }

    private void UpdateValidationComparison(int errorCount, string signature)
    {
        var key = !_hasValidationSnapshot
            ? "Sequence.ValidationInitial"
            : string.Equals(signature, _lastValidationSignature, StringComparison.Ordinal)
                ? "Sequence.ValidationUnchanged"
                : errorCount < _lastValidationErrorCount
                    ? "Sequence.ValidationImproved"
                    : errorCount > _lastValidationErrorCount
                        ? "Sequence.ValidationRegressed"
                        : "Sequence.ValidationChanged";
        var previousCount = _lastValidationErrorCount;
        _validationComparisonText = key switch
        {
            "Sequence.ValidationInitial" => Format(key, errorCount),
            "Sequence.ValidationUnchanged" => Format(key, errorCount),
            _ => Format(key, previousCount, errorCount)
        };
        _lastValidationErrorCount = errorCount;
        _lastValidationSignature = signature;
        _hasValidationSnapshot = true;
        OnPropertyChanged(nameof(ValidationComparisonText));
    }

    private static string GetIssueKey(SequenceValidationIssue issue) =>
        $"{issue.Code}|{issue.StepId}|{issue.TargetId}|{issue.PropertyName}|{issue.Message}";

    private static string ResolveValidationProperty(SequenceCompilationErrorCode code) => code switch
    {
        SequenceCompilationErrorCode.DefinitionRequired
            or SequenceCompilationErrorCode.SequenceIdRequired => "Sequence.Id",
        SequenceCompilationErrorCode.NoSteps => "Sequence.Steps",
        SequenceCompilationErrorCode.InvalidWatchdogTimeout => "Sequence.WatchdogTimeoutMs",
        SequenceCompilationErrorCode.StepIdRequired
            or SequenceCompilationErrorCode.DuplicateStepId => "Step.Id",
        SequenceCompilationErrorCode.UnsupportedAction => "Step.Action",
        SequenceCompilationErrorCode.TargetIdRequired
            or SequenceCompilationErrorCode.UnexpectedTargetId
            or SequenceCompilationErrorCode.UnknownSignal
            or SequenceCompilationErrorCode.UnknownAxis
            or SequenceCompilationErrorCode.UnknownCamera => "Step.TargetId",
        SequenceCompilationErrorCode.UnexpectedWorkpieceComponentId
            or SequenceCompilationErrorCode.UnknownWorkpieceComponent => "Step.WorkpieceComponentId",
        SequenceCompilationErrorCode.InvalidBooleanParameter
            or SequenceCompilationErrorCode.InvalidNumericParameter
            or SequenceCompilationErrorCode.UnexpectedParameter
            or SequenceCompilationErrorCode.InvalidSignalKind => "Step.Parameter",
        SequenceCompilationErrorCode.InvalidTimeout => "Step.TimeoutMs",
        SequenceCompilationErrorCode.UnknownSubsequence
            or SequenceCompilationErrorCode.SubsequenceCycle => "Step.TargetId",
        SequenceCompilationErrorCode.NextStepNotFound
            or SequenceCompilationErrorCode.MissingSuccessor
            or SequenceCompilationErrorCode.CompleteStepHasTransition => "Step.NextStepId",
        SequenceCompilationErrorCode.ErrorStepNotFound => "Step.ErrorStepId",
        SequenceCompilationErrorCode.FailureStepRequired
            or SequenceCompilationErrorCode.FailureStepNotFound
            or SequenceCompilationErrorCode.FailureStepNotAllowed => "Step.FailureStepId",
        SequenceCompilationErrorCode.RecipeIdRequired => "Step.Parameter",
        SequenceCompilationErrorCode.ExpectedTargetIdRequired => "Step.ExpectedTargetId",
        SequenceCompilationErrorCode.ExpectedStateRequired => "Step.ExpectedState",
        _ => "Step"
    };

    private static string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);

    private static int NextStepOrdinal(SequenceDefinition sequence)
    {
        var ids = sequence.Steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (ids.Contains($"step-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }

    internal void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _axisStepTargetId = string.Empty;
        AxisStepAdded = null;
        _cameraStepTargetId = string.Empty;
        CameraStepAdded = null;
        _signalStepTargetId = string.Empty;
        _signalStepTemplateId = string.Empty;
        SignalStepAdded = null;
        _subsequenceStepTargetId = string.Empty;
        SubsequenceStepAdded = null;
        _unitStepTargetId = string.Empty;
        _unitStepTemplateId = string.Empty;
        UnitStepAdded = null;
        _transferStepOwnerId = string.Empty;
        _transferStepTargetId = string.Empty;
        _transferStepDestinationId = string.Empty;
        TransferStepAdded = null;
        _workpieceStepTargetId = string.Empty;
        _workpieceStepTemplateId = string.Empty;
        WorkpieceStepAdded = null;
        RefreshSignalStepAuthoring(refreshTargets: true);
        RefreshSubsequenceStepAuthoring(refreshTargets: true);
        RefreshUnitStepAuthoring(refreshTargets: true);
        RefreshTransferStepAuthoring(refreshTargets: true);
        RefreshWorkpieceStepAuthoring(refreshTargets: true);
        RefreshCameraStepAuthoring(refreshTargets: true);
        RefreshAxisStepAuthoring(refreshTargets: true);
        CanSelectOverviewSequence = null;
        SelectOverviewSequence = null;
        ProcessLanes.Clear();
        _expandedTaskGroups.Clear();
        _taskSequenceId = null;
        _changeFlowZoomCommand.RaiseCanExecuteChanged();
        _selectOverviewStepCommand.RaiseCanExecuteChanged();
        _beginRouteDestinationSelectionCommand.RaiseCanExecuteChanged();
        _cancelRouteDestinationSelectionCommand.RaiseCanExecuteChanged();
        _stepTestCancellation?.Cancel();
        DetachStepDraft();
        _stepEditors.DefinitionChanged -= OnStepDefinitionChanged;
        _stepEditors.Dispose();
        InvalidateCommands();
    }
}
