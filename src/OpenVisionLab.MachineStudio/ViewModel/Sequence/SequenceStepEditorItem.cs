using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class SequenceStepEditorItem : ViewModelBase
{
    private readonly SequenceStepDefinition _definition;
    private readonly string _sequenceId;
    private readonly IReadOnlyList<SequenceStepAction> _availableActions;
    private readonly SequenceStepTemplateCatalog _templateCatalog;
    private readonly IReadOnlyList<SequenceAuthoringTarget> _authoringTargets;
    private readonly IReadOnlyList<SequenceAuthoringTarget> _destinationUnits;
    private IReadOnlyList<SequenceAuthoringTarget> _workpieceTargets;
    private readonly IReadOnlyList<SequenceExpectedStateTarget> _expectedStateTargets;
    private readonly bool _isTerminal;
    private IReadOnlyList<SequenceCompilationError> _validationErrors = Array.Empty<SequenceCompilationError>();
    private string _validationText = "Valid";
    private bool _isExecuting;
    private bool _isSearchMatch;
    private bool _isEditingSelection;

    public SequenceStepEditorItem(
        SequenceStepDefinition definition,
        string sequenceId,
        int order,
        IReadOnlyList<SequenceStepAction> nonTerminalActions,
        SequenceStepTemplateCatalog templateCatalog,
        IReadOnlyList<SequenceAuthoringTarget> authoringTargets,
        IReadOnlyList<SequenceAuthoringTarget> workpieceTargets,
        IReadOnlyList<SequenceExpectedStateTarget> expectedStateTargets,
        IReadOnlyList<SequenceAuthoringTarget>? destinationUnits = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _sequenceId = sequenceId ?? string.Empty;
        ArgumentNullException.ThrowIfNull(nonTerminalActions);
        _templateCatalog = templateCatalog ?? throw new ArgumentNullException(nameof(templateCatalog));
        _authoringTargets = authoringTargets ?? throw new ArgumentNullException(nameof(authoringTargets));
        _destinationUnits = destinationUnits ?? authoringTargets.Where(target => target.Kind == SequenceAuthoringTargetKind.Unit).ToArray();
        _workpieceTargets = workpieceTargets ?? throw new ArgumentNullException(nameof(workpieceTargets));
        _expectedStateTargets = expectedStateTargets ?? throw new ArgumentNullException(nameof(expectedStateTargets));
        Order = order;
        _isTerminal = definition.Action is SequenceStepAction.Complete or SequenceStepAction.None;
        if (_isTerminal)
        {
            _availableActions = new[] { definition.Action };
        }
        else
        {
            SequenceStepAction[] targetBackedActions = nonTerminalActions
                .Where(action => action == SequenceStepAction.Delay || _templateCatalog.GetTargets(action, _authoringTargets).Count != 0)
                .ToArray();
            _availableActions = targetBackedActions.Contains(definition.Action)
                ? targetBackedActions
                : new[] { definition.Action }.Concat(targetBackedActions).ToArray();
        }
    }

    internal string DefinitionSnapshot => JsonSerializer.Serialize(_definition);

    internal SequenceStepDefinition CopyDefinition() => JsonSerializer.Deserialize<SequenceStepDefinition>(DefinitionSnapshot)!;

    internal SequenceStepEditorItem CreateDraft() => new(CopyDefinition(), _sequenceId, Order, _availableActions,
        _templateCatalog, _authoringTargets, _workpieceTargets, _expectedStateTargets, _destinationUnits) { AxisDefinitions = AxisDefinitions };

    internal void ApplyDraft(SequenceStepDefinition draft)
    {
        if (!string.Equals(draft.Id, Id, StringComparison.Ordinal)) throw new ArgumentException("Step identity changed.", nameof(draft));
        _definition.Name = draft.Name;
        _definition.Action = draft.Action;
        _definition.TargetId = draft.TargetId;
        _definition.Parameter = draft.Parameter;
        _definition.TimeoutMs = draft.TimeoutMs;
        _definition.NextStepId = draft.NextStepId;
        _definition.ErrorStepId = draft.ErrorStepId;
        _definition.FailureStepId = draft.FailureStepId;
        _definition.ExpectedTargetId = draft.ExpectedTargetId;
        _definition.ExpectedState = draft.ExpectedState;
        _definition.WorkpieceComponentId = draft.WorkpieceComponentId;
        _definition.WaitForAxisCompletion = draft.WaitForAxisCompletion;
        _definition.EjectionDelayMilliseconds = draft.EjectionDelayMilliseconds;
        _definition.DestinationUnitId = draft.DestinationUnitId;
        OnPropertyChanged(string.Empty);
        NotifyInspectorPresentation();
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    public int Order { get; }
    public string SequenceId => _sequenceId;
    internal IReadOnlyList<VirtualAxisDefinition> AxisDefinitions { get; init; } = Array.Empty<VirtualAxisDefinition>();
    private bool? SignalValue => bool.TryParse(Parameter?.Trim(), out bool value) ? value : Parameter?.Trim() == "1" ? true : Parameter?.Trim() == "0" ? false : null;
    private string StepListTimeoutText => TimeoutMs == 0 ? OpenVisionLanguageService.T("R24.NoTimeout") : string.Format(CultureInfo.InvariantCulture, OpenVisionLanguageService.T("R24.StepTimeout"), (TimeoutMs / 1000m).ToString("0.###", CultureInfo.InvariantCulture));
    public string StepListActionText => Action == SequenceStepAction.SetSignal && SignalValue is { } value ? OpenVisionLanguageService.T(value ? "R24.OutputOn" : "R24.OutputOff") : OpenVisionLanguageService.T($"Sequence.Action.{Action}", Action.ToString(), Action.ToString());
    public string StepListParameterText
    {
        get
        {
            if (Action == SequenceStepAction.Delay) return $"{Parameter} s";
            if (Action == SequenceStepAction.SetSignal && SignalValue is not null) return string.Empty;
            if (Action == SequenceStepAction.WaitSignal) return $"{(SignalValue is { } value ? value ? "ON" : "OFF" : Parameter)} · {StepListTimeoutText}";
            if (Action == SequenceStepAction.WaitAxisDone && (string.IsNullOrWhiteSpace(Parameter) || string.Equals(Parameter.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase))) return StepListTimeoutText;
            string? unit = Action == SequenceStepAction.MoveAxis ? AxisDefinitions.FirstOrDefault(axis => axis.Id == TargetId)?.Unit : null;
            return string.IsNullOrEmpty(unit) ? Parameter : $"{Parameter} {unit}";
        }
    }
    public string StepListNextText => _isTerminal ? OpenVisionLanguageService.T("R24.CycleEnd") : string.IsNullOrEmpty(TransitionSummary) ? OpenVisionLanguageService.T("Sequence.Next") : TransitionSummary;
    public string Id => _definition.Id;
    public string TransitionSummary => Action is SequenceStepAction.Complete
        ? OpenVisionLanguageService.T("Sequence.Complete", "완료", "Complete")
        : string.Join("  |  ", new[]
        {
            FormatTransition("Sequence.Next", NextStepId),
            FormatTransition("Sequence.Error", ErrorStepId),
            FormatTransition("Sequence.Failure", FailureStepId)
        }.Where(transition => transition.Length != 0));
    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        internal set => SetProperty(ref _isSearchMatch, value);
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        internal set { if (SetProperty(ref _isExecuting, value)) OnPropertyChanged(nameof(StepListStatusText)); }
    }
    public bool IsEditingSelection { get => _isEditingSelection; internal set => SetProperty(ref _isEditingSelection, value); }
    public string DisplayName => OpenVisionLanguageService.TUserText(
        "sequence",
        $"{_sequenceId}.step.{Id}.name",
        Name);
    public string TargetDisplayName
    {
        get
        {
            if (Action is SequenceStepAction.None or SequenceStepAction.Complete or SequenceStepAction.Delay) return "—";
            string name = UsesWorkpieceAsTarget
                ? string.IsNullOrWhiteSpace(WorkpieceComponentId)
                    ? OpenVisionLanguageService.T("Sequence.NoWorkpieceAssociation", "작업물 연결 안 함", "No workpiece association")
                    : ResolveTargetName(WorkpieceComponentId, _workpieceTargets)
                : ResolveTargetName(TargetId, AvailableTargets);
            return HasDestinationUnit ? $"{name} → {ResolveTargetName(DestinationUnitId, _destinationUnits)}" : name;
        }
    }
    public string TargetIdentityText => Action is SequenceStepAction.None or SequenceStepAction.Complete or SequenceStepAction.Delay
        ? string.Empty
        : HasDestinationUnit ? $"{TargetId} → {DestinationUnitId}" : UsesWorkpieceAsTarget ? WorkpieceComponentId : TargetId;
    public string StepListTargetText => string.IsNullOrEmpty(TargetIdentityText) ? TargetDisplayName : $"{TargetDisplayName} ({TargetIdentityText})";
    public bool IsTerminal => _isTerminal;
    public IReadOnlyList<SequenceStepAction> AvailableActions => _availableActions;
    public IReadOnlyList<SequenceAuthoringTarget> AvailableTargets =>
        _definition.Action is SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece
            ? Array.Empty<SequenceAuthoringTarget>()
            : _templateCatalog.GetTargets(_definition.Action, _authoringTargets);
    public IReadOnlyList<SequenceAuthoringTarget> AvailableWorkpieceTargets => _workpieceTargets;
    public IReadOnlyList<SequenceAuthoringTarget> AvailableDestinationUnits => _destinationUnits;
    public bool HasDestinationUnit => Action == SequenceStepAction.TransferUnitWorkpiece;
    public bool HasAxisCompletionOption => Action == SequenceStepAction.MoveAxis;
    public bool WaitForAxisCompletion
    {
        get => _definition.WaitForAxisCompletion;
        set
        {
            if (_definition.WaitForAxisCompletion == value) return;
            _definition.WaitForAxisCompletion = value;
            OnPropertyChanged();
            NotifyInspectorPresentation();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public int EjectionDelayMilliseconds
    {
        get => _definition.EjectionDelayMilliseconds;
        set { if (_definition.EjectionDelayMilliseconds == value) return; _definition.EjectionDelayMilliseconds = value; OnPropertyChanged(); DefinitionChanged?.Invoke(this, EventArgs.Empty); }
    }
    public bool HasEjectionDelay => Action == SequenceStepAction.EjectUnitWorkpiece;
    public string DestinationUnitId
    {
        get => _definition.DestinationUnitId ?? string.Empty;
        set => SetNullable(_definition.DestinationUnitId, value, current => _definition.DestinationUnitId = current);
    }
    public IReadOnlyList<string> AvailableParameterOptions =>
        _templateCatalog.GetParameterOptions(_definition.Action);
    public bool HasTargetOptions => AvailableTargets.Count != 0;
    public bool UsesWorkpieceAsTarget => _definition.Action is
        SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece;
    public bool HasWorkpieceTargetOptions => _definition.Action is
        SequenceStepAction.TriggerCamera or SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece;
    public bool UsesParameterChoices => AvailableParameterOptions.Count != 0;
    public bool HasInspectorParameter => Action is SequenceStepAction.SetSignal or SequenceStepAction.SetChannel
        or SequenceStepAction.WaitSignal or SequenceStepAction.MoveAxis or SequenceStepAction.Delay
        or SequenceStepAction.TriggerCamera or SequenceStepAction.TransferUnitWorkpiece or SequenceStepAction.EjectUnitWorkpiece
        || !string.IsNullOrWhiteSpace(Parameter) && !((Action is SequenceStepAction.Wait or SequenceStepAction.WaitAxisDone)
            && string.Equals(Parameter.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase));
    public bool SupportsInspectorTimeout => Action is SequenceStepAction.WaitSignal or SequenceStepAction.WaitAxisDone or SequenceStepAction.Wait
        or SequenceStepAction.WaitVisionResult or SequenceStepAction.FeedUnitWorkpiece or SequenceStepAction.ReceiveUnitWorkpiece
        or SequenceStepAction.TransferUnitWorkpiece or SequenceStepAction.EjectUnitWorkpiece || HasAxisCompletionOption && WaitForAxisCompletion;
    public bool HasInspectorTimeout => SupportsInspectorTimeout || TimeoutMs != 0;
    public bool HasFailureRoute => Action is SequenceStepAction.WaitVisionResult or SequenceStepAction.BranchInspection;
    public bool HasInvalidFailureRoute => !HasFailureRoute && !string.IsNullOrEmpty(FailureStepId);
    public bool HasAdditionalSettings => !string.IsNullOrEmpty(ErrorStepId) || HasExpectedState || HasInvalidFailureRoute;
    public IReadOnlyList<SequenceAuthoringTarget> InspectorTargets => PreserveInspectorTarget(AvailableTargets, TargetId);
    public IReadOnlyList<SequenceAuthoringTarget> InspectorWorkpieceTargets => PreserveInspectorTarget(AvailableWorkpieceTargets, WorkpieceComponentId);
    public IReadOnlyList<SequenceAuthoringTarget> InspectorDestinationUnits => PreserveInspectorTarget(AvailableDestinationUnits, DestinationUnitId);
    public IReadOnlyList<string> InspectorParameterOptions => UsesParameterChoices && ParameterChoice is { } value && !AvailableParameterOptions.Contains(value)
        ? new[] { value }.Concat(AvailableParameterOptions).ToArray() : AvailableParameterOptions;
    public IReadOnlyList<SequenceExpectedStateTarget> InspectorExpectedTargets => ExpectedTargetId.Length > 0 && _expectedStateTargets.All(target => target.Id != ExpectedTargetId)
        ? new[] { new SequenceExpectedStateTarget(ExpectedTargetId, ExpectedTargetId, Array.Empty<string>()) }.Concat(_expectedStateTargets).ToArray() : _expectedStateTargets;
    public IReadOnlyList<string> InspectorExpectedStates => ExpectedState.Length > 0 && !AvailableExpectedStates.Contains(ExpectedState)
        ? new[] { ExpectedState }.Concat(AvailableExpectedStates).ToArray() : AvailableExpectedStates;
    public bool CanEditExpectedState => CanSetExpectedState || HasExpectedState;
    public bool IsParameterEditable => !_isTerminal;
    public bool IsTimeoutEditable => !_isTerminal;
    public IReadOnlyList<SequenceExpectedStateTarget> AvailableExpectedStateTargets =>
        _expectedStateTargets;
    public IReadOnlyList<string> AvailableExpectedStates =>
        _expectedStateTargets.FirstOrDefault(target =>
            string.Equals(target.Id, _definition.ExpectedTargetId, StringComparison.Ordinal))?.States
        ?? Array.Empty<string>();
    public bool CanSetExpectedState => _expectedStateTargets.Count != 0;
    public bool HasExpectedState
    {
        get => !string.IsNullOrWhiteSpace(_definition.ExpectedTargetId)
            || !string.IsNullOrWhiteSpace(_definition.ExpectedState);
        set
        {
            if (value == HasExpectedState)
            {
                return;
            }

            if (value && _expectedStateTargets.FirstOrDefault() is { } target)
            {
                _definition.ExpectedTargetId = target.Id;
                _definition.ExpectedState = target.States.FirstOrDefault();
            }
            else
            {
                _definition.ExpectedTargetId = null;
                _definition.ExpectedState = null;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpectedTargetId));
            OnPropertyChanged(nameof(ExpectedState));
            OnPropertyChanged(nameof(AvailableExpectedStates));
            NotifyInspectorPresentation();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Name
    {
        get => _definition.Name;
        set => SetString(_definition.Name, value, current => _definition.Name = current);
    }

    public SequenceStepAction Action
    {
        get => _definition.Action;
        set
        {
            if (_definition.Action == value
                || (_isTerminal && value != SequenceStepAction.Complete)
                || (!_isTerminal && value == SequenceStepAction.Complete))
            {
                return;
            }

            _definition.Action = value;
            SequenceDefinitionEditor.NormalizeStep(
                _definition,
                _templateCatalog,
                _authoringTargets);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableTargets));
            OnPropertyChanged(nameof(AvailableWorkpieceTargets));
            OnPropertyChanged(nameof(HasWorkpieceTargetOptions));
            OnPropertyChanged(nameof(UsesWorkpieceAsTarget));
            OnPropertyChanged(nameof(AvailableParameterOptions));
            OnPropertyChanged(nameof(HasTargetOptions));
            OnPropertyChanged(nameof(HasDestinationUnit));
            OnPropertyChanged(nameof(HasAxisCompletionOption));
            OnPropertyChanged(nameof(HasEjectionDelay));
            OnPropertyChanged(nameof(DestinationUnitId));
            OnPropertyChanged(nameof(UsesParameterChoices));
            OnPropertyChanged(nameof(IsParameterEditable));
            OnPropertyChanged(nameof(IsTimeoutEditable));
            NotifyAllEditableFields();
            NotifyInspectorPresentation();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string TargetId
    {
        get => _definition.TargetId;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_definition.TargetId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            bool moveWasAtTargetDefault = _definition.Action == SequenceStepAction.MoveAxis
                && string.Equals(
                    _definition.Parameter,
                    SequenceDefinitionEditor.FindDefaultParameter(_definition.TargetId, _authoringTargets)
                        ?? string.Empty,
                    StringComparison.Ordinal);
            _definition.TargetId = normalized;
            if (moveWasAtTargetDefault)
            {
                _definition.Parameter = SequenceDefinitionEditor.FindDefaultParameter(
                    normalized,
                    _authoringTargets) ?? string.Empty;
                OnPropertyChanged(nameof(Parameter));
            }

            OnPropertyChanged();
            NotifyTargetPresentation();
            NotifyInspectorPresentation();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Parameter
    {
        get => _definition.Parameter;
        set => SetString(_definition.Parameter, value, current => _definition.Parameter = current);
    }

    public string? ParameterChoice
    {
        get => UsesParameterChoices && SignalValue is { } value ? value ? "true" : "false" : Parameter;
        set { if (value is not null && AvailableParameterOptions.Contains(value)) Parameter = value; }
    }

    public string WorkpieceComponentId
    {
        get => _definition.WorkpieceComponentId ?? string.Empty;
        set => SetNullable(
            _definition.WorkpieceComponentId,
            value,
            current => _definition.WorkpieceComponentId = current);
    }

    public int TimeoutMs
    {
        get => _definition.TimeoutMs;
        set
        {
            if (_definition.TimeoutMs == value)
            {
                return;
            }

            _definition.TimeoutMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepListParameterText));
            NotifyInspectorPresentation();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string NextStepId
    {
        get => _definition.NextStepId ?? string.Empty;
        set => SetNullable(_definition.NextStepId, value, current => _definition.NextStepId = current);
    }

    public string ErrorStepId
    {
        get => _definition.ErrorStepId ?? string.Empty;
        set => SetNullable(_definition.ErrorStepId, value, current => _definition.ErrorStepId = current);
    }

    public string FailureStepId
    {
        get => _definition.FailureStepId ?? string.Empty;
        set => SetNullable(_definition.FailureStepId, value, current => _definition.FailureStepId = current);
    }

    public string ExpectedTargetId
    {
        get => _definition.ExpectedTargetId ?? string.Empty;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(_definition.ExpectedTargetId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _definition.ExpectedTargetId = normalized;
            IReadOnlyList<string> states = AvailableExpectedStates;
            if (!states.Contains(_definition.ExpectedState ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                _definition.ExpectedState = states.FirstOrDefault();
                OnPropertyChanged(nameof(ExpectedState));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableExpectedStates));
            OnPropertyChanged(nameof(HasExpectedState));
            NotifyInspectorPresentation();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ExpectedState
    {
        get => _definition.ExpectedState ?? string.Empty;
        set
        {
            SetNullable(_definition.ExpectedState, value, current => _definition.ExpectedState = current);
            OnPropertyChanged(nameof(HasExpectedState));
        }
    }

    public string StepListStatusText => IsExecuting ? OpenVisionLanguageService.T("R24.StepExecuting") : _validationErrors.Count != 0 ? ValidationText : string.Empty;

    public string ValidationText
    {
        get => _validationText;
        private set { if (SetProperty(ref _validationText, value)) OnPropertyChanged(nameof(StepListStatusText)); }
    }

    public event EventHandler? DefinitionChanged;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(StepListActionText));
        OnPropertyChanged(nameof(TransitionSummary));
        OnPropertyChanged(nameof(StepListNextText));
        OnPropertyChanged(nameof(AvailableActions));
        _workpieceTargets = _workpieceTargets
            .Select(target => string.IsNullOrEmpty(target.Id)
                ? target with
                {
                    Name = OpenVisionLanguageService.T(
                        "Sequence.NoWorkpieceAssociation",
                        "작업물 연결 안 함",
                        "No workpiece association")
                }
                : target)
            .ToArray();
        OnPropertyChanged(nameof(AvailableWorkpieceTargets));
        NotifyTargetPresentation();
        RefreshValidationText();
        NotifyInspectorPresentation();
        OnPropertyChanged(nameof(StepListStatusText));
    }

    public void SetValidation(IEnumerable<SequenceCompilationError> errors)
    {
        _validationErrors = errors.ToArray();
        RefreshValidationText();
        OnPropertyChanged(nameof(StepListStatusText));
    }

    private void RefreshValidationText()
    {
        string[] messages = _validationErrors.Select(error => error.Message).ToArray();
        ValidationText = messages.Length == 0
            ? HasExpectedState
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T(
                        "Sequence.ExpectedStateFormat",
                        "기대 상태: {0} = {1}",
                        "Expected: {0} = {1}"),
                    ExpectedTargetId,
                    ExpectedState)
                : OpenVisionLanguageService.T("Sequence.StepValid", "유효", "Valid")
            : string.Join(" ", messages);
    }

    private void SetString(
        string current,
        string? value,
        Action<string> apply,
        [CallerMemberName] string propertyName = "")
    {
        string normalized = value ?? string.Empty;
        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return;
        }

        apply(normalized);
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(Name)) OnPropertyChanged(nameof(DisplayName));
        if (propertyName == nameof(Parameter))
        {
            OnPropertyChanged(nameof(ParameterChoice));
            OnPropertyChanged(nameof(StepListParameterText));
            OnPropertyChanged(nameof(StepListActionText));
        }
        NotifyInspectorPresentation(propertyName);
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetNullable(
        string? current,
        string? value,
        Action<string?> apply,
        [CallerMemberName] string propertyName = "")
    {
        string? normalized = string.IsNullOrWhiteSpace(value) ? null : value;
        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return;
        }

        apply(normalized);
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(NextStepId) or nameof(ErrorStepId) or nameof(FailureStepId))
        {
            OnPropertyChanged(nameof(TransitionSummary));
            OnPropertyChanged(nameof(StepListNextText));
        }
        if (propertyName is nameof(WorkpieceComponentId) or nameof(DestinationUnitId)) NotifyTargetPresentation();
        NotifyInspectorPresentation(propertyName);
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyList<SequenceAuthoringTarget> PreserveInspectorTarget(IReadOnlyList<SequenceAuthoringTarget> targets, string id) =>
        id.Length > 0 && targets.All(target => target.Id != id)
            ? new[] { new SequenceAuthoringTarget(id, id, targets.FirstOrDefault()?.Kind ?? SequenceAuthoringTargetKind.DigitalInput) }.Concat(targets).ToArray() : targets;

    private void NotifyInspectorPresentation([CallerMemberName] string propertyName = "")
    {
        OnPropertyChanged(nameof(HasInspectorParameter));
        OnPropertyChanged(nameof(HasInspectorTimeout));
        OnPropertyChanged(nameof(SupportsInspectorTimeout));
        OnPropertyChanged(nameof(HasFailureRoute));
        OnPropertyChanged(nameof(HasInvalidFailureRoute));
        OnPropertyChanged(nameof(HasAdditionalSettings));
        if (propertyName is nameof(ApplyDraft) or nameof(Action) or nameof(TargetId)) OnPropertyChanged(nameof(InspectorTargets));
        OnPropertyChanged(nameof(TargetId));
        if (propertyName is nameof(ApplyDraft) or nameof(Action) or nameof(WorkpieceComponentId) or nameof(RefreshLocalization)) OnPropertyChanged(nameof(InspectorWorkpieceTargets));
        OnPropertyChanged(nameof(WorkpieceComponentId));
        if (propertyName is nameof(ApplyDraft) or nameof(Action) or nameof(DestinationUnitId)) OnPropertyChanged(nameof(InspectorDestinationUnits));
        OnPropertyChanged(nameof(DestinationUnitId));
        if (propertyName is nameof(ApplyDraft) or nameof(Action) or nameof(Parameter)) OnPropertyChanged(nameof(InspectorParameterOptions));
        if (propertyName is nameof(ApplyDraft) or nameof(HasExpectedState) or nameof(ExpectedTargetId)) OnPropertyChanged(nameof(InspectorExpectedTargets));
        OnPropertyChanged(nameof(ExpectedTargetId));
        if (propertyName is nameof(ApplyDraft) or nameof(HasExpectedState) or nameof(ExpectedTargetId) or nameof(ExpectedState)) OnPropertyChanged(nameof(InspectorExpectedStates));
        OnPropertyChanged(nameof(ExpectedState));
        OnPropertyChanged(nameof(CanEditExpectedState));
    }

    private string ResolveTargetName(string id, IReadOnlyList<SequenceAuthoringTarget> targets)
    {
        if (string.IsNullOrWhiteSpace(id)) return OpenVisionLanguageService.T("Sequence.TargetUnassigned", "대상 미설정", "No target selected");
        var target = targets.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return target is not null ? SequenceAuthoringTargetCatalog.GetName(target)
            : $"{OpenVisionLanguageService.T("Sequence.TargetMissing", "미연결 대상", "Missing target")} · {id}";
    }

    private void NotifyTargetPresentation()
    {
        OnPropertyChanged(nameof(TargetDisplayName));
        OnPropertyChanged(nameof(TargetIdentityText));
        OnPropertyChanged(nameof(StepListTargetText));
        OnPropertyChanged(nameof(StepListParameterText));
    }

    private static string FormatTransition(string labelKey, string? targetId) =>
        string.IsNullOrWhiteSpace(targetId)
            ? string.Empty
            : $"{OpenVisionLanguageService.T(labelKey)} → {targetId}";

    private void NotifyAllEditableFields()
    {
        NotifyTargetPresentation();
        OnPropertyChanged(nameof(StepListActionText));
        OnPropertyChanged(nameof(TargetId));
        OnPropertyChanged(nameof(Parameter));
        OnPropertyChanged(nameof(ParameterChoice));
        OnPropertyChanged(nameof(WorkpieceComponentId));
        OnPropertyChanged(nameof(TimeoutMs));
        OnPropertyChanged(nameof(NextStepId));
        OnPropertyChanged(nameof(ErrorStepId));
        OnPropertyChanged(nameof(FailureStepId));
        OnPropertyChanged(nameof(TransitionSummary));
        OnPropertyChanged(nameof(StepListNextText));
        OnPropertyChanged(nameof(HasExpectedState));
        OnPropertyChanged(nameof(ExpectedTargetId));
        OnPropertyChanged(nameof(ExpectedState));
        OnPropertyChanged(nameof(AvailableExpectedStates));
    }
}
