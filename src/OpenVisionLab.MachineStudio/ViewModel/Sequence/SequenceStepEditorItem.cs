using System.Runtime.CompilerServices;
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
    private IReadOnlyList<SequenceAuthoringTarget> _workpieceTargets;
    private readonly IReadOnlyList<SequenceExpectedStateTarget> _expectedStateTargets;
    private readonly bool _isTerminal;
    private IReadOnlyList<SequenceCompilationError> _validationErrors = Array.Empty<SequenceCompilationError>();
    private string _validationText = "Valid";
    private bool _isExecuting;

    public SequenceStepEditorItem(
        SequenceStepDefinition definition,
        string sequenceId,
        int order,
        IReadOnlyList<SequenceStepAction> nonTerminalActions,
        SequenceStepTemplateCatalog templateCatalog,
        IReadOnlyList<SequenceAuthoringTarget> authoringTargets,
        IReadOnlyList<SequenceAuthoringTarget> workpieceTargets,
        IReadOnlyList<SequenceExpectedStateTarget> expectedStateTargets)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _sequenceId = sequenceId ?? string.Empty;
        ArgumentNullException.ThrowIfNull(nonTerminalActions);
        _templateCatalog = templateCatalog ?? throw new ArgumentNullException(nameof(templateCatalog));
        _authoringTargets = authoringTargets ?? throw new ArgumentNullException(nameof(authoringTargets));
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
                .Where(action => _templateCatalog.GetTargets(action, _authoringTargets).Count != 0)
                .ToArray();
            _availableActions = targetBackedActions.Contains(definition.Action)
                ? targetBackedActions
                : new[] { definition.Action }.Concat(targetBackedActions).ToArray();
        }
    }

    public int Order { get; }
    public string Id => _definition.Id;
    public string TransitionSummary => Action is SequenceStepAction.Complete
        ? OpenVisionLanguageService.T("Sequence.Complete", "완료", "Complete")
        : string.Join("  |  ", new[]
        {
            FormatTransition("Sequence.Next", NextStepId),
            FormatTransition("Sequence.Error", ErrorStepId),
            FormatTransition("Sequence.Failure", FailureStepId)
        }.Where(transition => transition.Length != 0));
    public bool IsExecuting
    {
        get => _isExecuting;
        internal set => SetProperty(ref _isExecuting, value);
    }
    public string DisplayName => OpenVisionLanguageService.TUserText(
        "sequence",
        $"{_sequenceId}.step.{Id}.name",
        Name);
    public bool IsTerminal => _isTerminal;
    public IReadOnlyList<SequenceStepAction> AvailableActions => _availableActions;
    public IReadOnlyList<SequenceAuthoringTarget> AvailableTargets =>
        _definition.Action is SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece
            ? Array.Empty<SequenceAuthoringTarget>()
            : _templateCatalog.GetTargets(_definition.Action, _authoringTargets);
    public IReadOnlyList<SequenceAuthoringTarget> AvailableWorkpieceTargets => _workpieceTargets;
    public IReadOnlyList<string> AvailableParameterOptions =>
        _templateCatalog.GetParameterOptions(_definition.Action);
    public bool HasTargetOptions => AvailableTargets.Count != 0;
    public bool UsesWorkpieceAsTarget => _definition.Action is
        SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece;
    public bool HasWorkpieceTargetOptions => _definition.Action is
        SequenceStepAction.TriggerCamera or SequenceStepAction.FeedWorkpiece or SequenceStepAction.EjectWorkpiece;
    public bool UsesParameterChoices => AvailableParameterOptions.Count != 0;
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
            OnPropertyChanged(nameof(UsesParameterChoices));
            OnPropertyChanged(nameof(IsParameterEditable));
            OnPropertyChanged(nameof(IsTimeoutEditable));
            NotifyAllEditableFields();
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
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Parameter
    {
        get => _definition.Parameter;
        set => SetString(_definition.Parameter, value, current => _definition.Parameter = current);
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

    public string ValidationText
    {
        get => _validationText;
        private set => SetProperty(ref _validationText, value);
    }

    public event EventHandler? DefinitionChanged;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(TransitionSummary));
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
        RefreshValidationText();
    }

    public void SetValidation(IEnumerable<SequenceCompilationError> errors)
    {
        _validationErrors = errors.ToArray();
        RefreshValidationText();
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
        }
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatTransition(string labelKey, string? targetId) =>
        string.IsNullOrWhiteSpace(targetId)
            ? string.Empty
            : $"{OpenVisionLanguageService.T(labelKey)} → {targetId}";

    private void NotifyAllEditableFields()
    {
        OnPropertyChanged(nameof(TargetId));
        OnPropertyChanged(nameof(Parameter));
        OnPropertyChanged(nameof(WorkpieceComponentId));
        OnPropertyChanged(nameof(TimeoutMs));
        OnPropertyChanged(nameof(NextStepId));
        OnPropertyChanged(nameof(ErrorStepId));
        OnPropertyChanged(nameof(FailureStepId));
        OnPropertyChanged(nameof(TransitionSummary));
        OnPropertyChanged(nameof(HasExpectedState));
        OnPropertyChanged(nameof(ExpectedTargetId));
        OnPropertyChanged(nameof(ExpectedState));
        OnPropertyChanged(nameof(AvailableExpectedStates));
    }
}
