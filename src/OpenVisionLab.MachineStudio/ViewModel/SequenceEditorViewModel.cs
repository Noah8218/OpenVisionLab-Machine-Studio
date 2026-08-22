using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SequenceEditorChangedEventArgs(bool StructureChanged);

public sealed record SequenceExpectedStateTarget(
    string Id,
    string Name,
    IReadOnlyList<string> States);

public sealed class SequenceEditorViewModel : ViewModelBase
{
    private static readonly SequenceStepAction[] SupportedNonTerminalActions =
    {
        SequenceStepAction.SetSignal,
        SequenceStepAction.WaitSignal,
        SequenceStepAction.MoveAxis,
        SequenceStepAction.WaitAxisDone,
        SequenceStepAction.TriggerCamera,
        SequenceStepAction.WaitVisionResult
    };

    private readonly SequenceDefinitionEditor _editor = new();
    private readonly SequenceStepTemplateCatalog _templateCatalog = new();
    private readonly ICommand _addStepCommand;
    private readonly ICommand _deleteStepCommand;
    private readonly ICommand _moveStepUpCommand;
    private readonly ICommand _moveStepDownCommand;
    private MachineProjectDocument _project = new();
    private IReadOnlyList<SequenceAuthoringTarget> _authoringTargets =
        Array.Empty<SequenceAuthoringTarget>();
    private IReadOnlyList<SequenceExpectedStateTarget> _expectedStateTargets =
        Array.Empty<SequenceExpectedStateTarget>();
    private SequenceDefinition? _selectedSequence;
    private SequenceStepEditorItem? _selectedStep;
    private SequenceStepTemplateDefinition? _selectedTemplate;
    private bool _isEditable = true;
    private string _validationSummary = "No sequence selected";
    private string _structuralEditStatus = "Select a sequence to edit steps.";

    public SequenceEditorViewModel()
    {
        _addStepCommand = new RelayCommand(_ => AddStep(), _ => CanAddStep());
        _deleteStepCommand = new RelayCommand(
            _ => DeleteSelectedStep(),
            _ => CanChangeStructure() && SelectedStep is not null);
        _moveStepUpCommand = new RelayCommand(_ => MoveSelectedStep(-1), _ => CanMoveSelectedStep(-1));
        _moveStepDownCommand = new RelayCommand(_ => MoveSelectedStep(1), _ => CanMoveSelectedStep(1));
    }

    public ObservableCollection<SequenceDefinition> Sequences { get; } = new();
    public ObservableCollection<SequenceStepEditorItem> Steps { get; } = new();
    public ObservableCollection<SequenceStepTemplateDefinition> Templates { get; } = new();
    public ObservableCollection<string> ValidationMessages { get; } = new();
    public bool HasSequences => Sequences.Count != 0;
    public bool HasTemplates => Templates.Count != 0;

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
            if (SetProperty(ref _selectedSequence, value))
            {
                LoadSteps();
            }
        }
    }

    public SequenceStepEditorItem? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (SetProperty(ref _selectedStep, value))
            {
                OnPropertyChanged(nameof(HasSelectedStep));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelectedStep => SelectedStep is not null;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            SetProperty(ref _isEditable, value);
        }
    }

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

    public ICommand AddStepCommand => _addStepCommand;
    public ICommand DeleteStepCommand => _deleteStepCommand;
    public ICommand MoveStepUpCommand => _moveStepUpCommand;
    public ICommand MoveStepDownCommand => _moveStepDownCommand;

    internal void InvalidateCommands()
    {
        ((RelayCommand)_addStepCommand).RaiseCanExecuteChanged();
        ((RelayCommand)_deleteStepCommand).RaiseCanExecuteChanged();
        ((RelayCommand)_moveStepUpCommand).RaiseCanExecuteChanged();
        ((RelayCommand)_moveStepDownCommand).RaiseCanExecuteChanged();
    }

    public event EventHandler<SequenceEditorChangedEventArgs>? DefinitionChanged;

    public void Load(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        LoadAuthoringTargets();
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
        if (SelectedSequence is null)
        {
            LoadSteps();
        }
    }

    public void RefreshAuthoringTargets()
    {
        string? selectedStepId = SelectedStep?.Id;
        LoadAuthoringTargets();
        LoadSteps(selectedStepId);
    }

    private void LoadAuthoringTargets()
    {
        _authoringTargets = BuildAuthoringTargets(_project);
        _expectedStateTargets = BuildExpectedStateTargets(_project);
        string? preferredTemplateId = SelectedTemplate?.Id;
        Templates.Clear();
        foreach (SequenceStepTemplateDefinition template in
                 _templateCatalog.GetAvailableTemplates(_authoringTargets))
        {
            Templates.Add(template);
        }
        SelectedTemplate = Templates.FirstOrDefault(template =>
            string.Equals(template.Id, preferredTemplateId, StringComparison.Ordinal))
            ?? Templates.FirstOrDefault();
        OnPropertyChanged(nameof(HasTemplates));
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Sequences));
        OnPropertyChanged(nameof(SelectedSequence));
        foreach (SequenceStepEditorItem step in Steps)
        {
            step.RefreshLocalization();
        }
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
        SequenceAuthoringTarget? target = _authoringTargets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, targetId, StringComparison.Ordinal));
        if (!IsEditable || SelectedSequence is null || target is null)
        {
            StructuralEditStatus = "Select an editable sequence and a compatible target.";
            return null;
        }

        string templateId = target.Kind switch
        {
            SequenceAuthoringTargetKind.DigitalInput => "wait-input-on",
            SequenceAuthoringTargetKind.DigitalOutput => "set-output-on",
            SequenceAuthoringTargetKind.Axis => "move-axis-home",
            SequenceAuthoringTargetKind.Camera => "trigger-camera",
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
        foreach (SequenceStepEditorItem step in Steps)
        {
            step.DefinitionChanged -= OnStepDefinitionChanged;
        }

        Steps.Clear();
        SelectedStep = null;
        if (SelectedSequence is not null)
        {
            for (var index = 0; index < SelectedSequence.Steps.Count; index++)
            {
                var item = new SequenceStepEditorItem(
                    SelectedSequence.Steps[index],
                    SelectedSequence.Id,
                    index + 1,
                    SupportedNonTerminalActions,
                    _templateCatalog,
                    _authoringTargets,
                    _expectedStateTargets);
                item.DefinitionChanged += OnStepDefinitionChanged;
                Steps.Add(item);
            }

            SelectedStep = Steps.FirstOrDefault(step =>
                string.Equals(step.Id, selectedStepId, StringComparison.Ordinal))
                ?? Steps.FirstOrDefault();
        }

        Validate();
        CommandManager.InvalidateRequerySuggested();
    }

    private void AddStep()
    {
        if (SelectedSequence is null || SelectedTemplate is null)
        {
            return;
        }

        int ordinal = NextStepOrdinal(SelectedSequence);
        SequenceStepDraftResult draft = _templateCatalog.CreateDraft(
            SelectedTemplate.Id,
            $"step-{ordinal}",
            _authoringTargets);
        if (!draft.IsCreated || draft.Step is null)
        {
            StructuralEditStatus = draft.Message;
            return;
        }

        SequenceStepDefinition step = draft.Step;
        SequenceEditResult result = _editor.InsertBeforeTerminal(SelectedSequence, step);
        ApplyStructuralResult(result, step.Id);
    }

    private void DeleteSelectedStep()
    {
        if (SelectedSequence is null || SelectedStep is null)
        {
            return;
        }

        SequenceEditResult result = _editor.Delete(SelectedSequence, SelectedStep.Id);
        ApplyStructuralResult(result, null);
    }

    private void MoveSelectedStep(int offset)
    {
        if (SelectedSequence is null || SelectedStep is null)
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
        IsEditable
        && SelectedSequence is not null
        && SequenceDefinitionEditor.IsStrictLinear(SelectedSequence);

    private bool CanAddStep() =>
        CanChangeStructure() && SelectedTemplate is not null;

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

    private void OnStepDefinitionChanged(object? sender, EventArgs args)
    {
        Validate();
        DefinitionChanged?.Invoke(this, new SequenceEditorChangedEventArgs(false));
    }

    private void Validate()
    {
        ValidationMessages.Clear();
        if (SelectedSequence is null)
        {
            ValidationSummary = "No sequence selected";
            StructuralEditStatus = "Select a sequence to edit steps.";
            return;
        }

        var channelKinds = _project.Channels
            .GroupBy(channel => channel.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Kind, StringComparer.Ordinal);
        var targets = new SequenceCompilationTargets(
            channelKinds,
            _project.Axes.Select(axis => axis.Id),
            _project.Devices.Where(device => device.Kind == DeviceKind.Camera).Select(device => device.Id));
        SequenceCompilationResult result = new SequenceCompiler().Compile(SelectedSequence, targets);
        foreach (SequenceCompilationError error in result.Errors)
        {
            ValidationMessages.Add($"{error.Code} [{error.StepId ?? "sequence"}]: {error.Message}");
        }

        foreach (SequenceStepEditorItem step in Steps)
        {
            step.SetValidation(result.Errors.Where(error =>
                string.Equals(error.StepId, step.Id, StringComparison.Ordinal)));
        }

        ValidationSummary = result.IsSuccess
            ? $"VALID · {Steps.Count} steps"
            : $"INVALID · {result.Errors.Count} issue(s)";
        StructuralEditStatus = SequenceDefinitionEditor.IsStrictLinear(SelectedSequence)
            ? "Linear path · add, remove, and reorder are available in Design mode."
            : "Branched path · edit fields only; structural commands are locked.";
        CommandManager.InvalidateRequerySuggested();
    }

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

    private static IReadOnlyList<SequenceAuthoringTarget> BuildAuthoringTargets(
        MachineProjectDocument project)
    {
        var targets = new List<SequenceAuthoringTarget>();
        targets.AddRange(project.Channels
            .Where(channel => channel.Kind is ChannelKind.DigitalInput or ChannelKind.DigitalOutput)
            .Select(channel => new SequenceAuthoringTarget(
                channel.Id,
                TargetDisplayName(channel.Name, channel.Id),
                channel.Kind == ChannelKind.DigitalInput
                    ? SequenceAuthoringTargetKind.DigitalInput
                    : SequenceAuthoringTargetKind.DigitalOutput)));
        targets.AddRange(project.Axes.Select(axis => new SequenceAuthoringTarget(
            axis.Id,
            TargetDisplayName(axis.Name, axis.Id),
            SequenceAuthoringTargetKind.Axis,
            axis.HomePosition.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        targets.AddRange(project.Devices
            .Where(device => device.Kind == DeviceKind.Camera)
            .Select(device => new SequenceAuthoringTarget(
                device.Id,
                TargetDisplayName(device.Name, device.Id),
                SequenceAuthoringTargetKind.Camera)));
        return targets;
    }

    private static IReadOnlyList<SequenceExpectedStateTarget> BuildExpectedStateTargets(
        MachineProjectDocument project)
    {
        var targets = project.Axes
            .Select(axis => new SequenceExpectedStateTarget(
                axis.Id,
                TargetDisplayName(axis.Name, axis.Id),
                Enum.GetNames<AxisState>()))
            .ToList();
        MachineLayoutDefinition? layout = project.Simulation.ActiveLayoutId is { Length: > 0 } activeLayoutId
            ? project.Layouts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, activeLayoutId, StringComparison.Ordinal))
            : project.Layouts.Count == 1
                ? project.Layouts[0]
                : null;
        if (layout is null)
        {
            return targets;
        }

        foreach (LayoutComponentDefinition component in layout.Components)
        {
            IReadOnlyList<string>? states = component.Kind switch
            {
                LayoutComponentKind.PneumaticCylinder => Enum.GetNames<PneumaticCylinderState>(),
                LayoutComponentKind.Conveyor => ["Stopped", "ForwardRunning", "ReverseRunning"],
                LayoutComponentKind.DigitalSensor => ["Clear", "Detected"],
                LayoutComponentKind.Workpiece => Enum.GetNames<WorkpieceInspectionState>(),
                _ => null
            };
            if (states is not null)
            {
                targets.Add(new SequenceExpectedStateTarget(
                    component.Id,
                    TargetDisplayName(component.Name, component.Id),
                    states));
            }
        }

        return targets;
    }

    private static string TargetDisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : $"{name} · {id}";
}

public sealed class SequenceStepEditorItem : ViewModelBase
{
    private readonly SequenceStepDefinition _definition;
    private readonly string _sequenceId;
    private readonly IReadOnlyList<SequenceStepAction> _availableActions;
    private readonly SequenceStepTemplateCatalog _templateCatalog;
    private readonly IReadOnlyList<SequenceAuthoringTarget> _authoringTargets;
    private readonly IReadOnlyList<SequenceExpectedStateTarget> _expectedStateTargets;
    private readonly bool _isTerminal;
    private string _validationText = "Valid";

    public SequenceStepEditorItem(
        SequenceStepDefinition definition,
        string sequenceId,
        int order,
        IReadOnlyList<SequenceStepAction> nonTerminalActions,
        SequenceStepTemplateCatalog templateCatalog,
        IReadOnlyList<SequenceAuthoringTarget> authoringTargets,
        IReadOnlyList<SequenceExpectedStateTarget> expectedStateTargets)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _sequenceId = sequenceId ?? string.Empty;
        ArgumentNullException.ThrowIfNull(nonTerminalActions);
        _templateCatalog = templateCatalog ?? throw new ArgumentNullException(nameof(templateCatalog));
        _authoringTargets = authoringTargets ?? throw new ArgumentNullException(nameof(authoringTargets));
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
    public string DisplayName => OpenVisionLanguageService.TUserText(
        "sequence",
        $"{_sequenceId}.step.{Id}.name",
        Name);
    public bool IsTerminal => _isTerminal;
    public IReadOnlyList<SequenceStepAction> AvailableActions => _availableActions;
    public IReadOnlyList<SequenceAuthoringTarget> AvailableTargets =>
        _templateCatalog.GetTargets(_definition.Action, _authoringTargets);
    public IReadOnlyList<string> AvailableParameterOptions =>
        _templateCatalog.GetParameterOptions(_definition.Action);
    public bool HasTargetOptions => AvailableTargets.Count != 0;
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
            NormalizeForAction();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableTargets));
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
                    DefaultParameterFor(_definition.TargetId),
                    StringComparison.Ordinal);
            _definition.TargetId = normalized;
            if (moveWasAtTargetDefault)
            {
                _definition.Parameter = DefaultParameterFor(normalized);
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

    public void RefreshLocalization() => OnPropertyChanged(nameof(DisplayName));

    public void SetValidation(IEnumerable<SequenceCompilationError> errors)
    {
        string[] messages = errors.Select(error => error.Message).ToArray();
        ValidationText = messages.Length == 0
            ? HasExpectedState
                ? $"Expected · {ExpectedTargetId} = {ExpectedState}"
                : "Valid"
            : string.Join(" ", messages);
    }

    private void NormalizeForAction()
    {
        if (_definition.Action == SequenceStepAction.Complete)
        {
            _definition.TargetId = string.Empty;
            _definition.Parameter = string.Empty;
            _definition.TimeoutMs = 0;
            _definition.NextStepId = null;
            _definition.ErrorStepId = null;
            _definition.FailureStepId = null;
            return;
        }

        IReadOnlyList<SequenceAuthoringTarget> targets = AvailableTargets;
        if (targets.Count == 0)
        {
            _definition.TargetId = string.Empty;
        }
        else if (!targets.Any(target =>
                     string.Equals(target.Id, _definition.TargetId, StringComparison.Ordinal)))
        {
            _definition.TargetId = targets[0].Id;
        }

        IReadOnlyList<string> parameterOptions = AvailableParameterOptions;
        if (parameterOptions.Count != 0
            && !parameterOptions.Contains(_definition.Parameter, StringComparer.OrdinalIgnoreCase))
        {
            _definition.Parameter = parameterOptions[0];
        }

        if (_definition.Action == SequenceStepAction.MoveAxis
            && (!double.TryParse(
                    _definition.Parameter,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double position)
                || !double.IsFinite(position)))
        {
            _definition.Parameter = targets.FirstOrDefault(target =>
                    string.Equals(target.Id, _definition.TargetId, StringComparison.Ordinal))
                ?.DefaultParameter ?? "0";
        }

        if (_definition.Action == SequenceStepAction.TriggerCamera
            && string.IsNullOrWhiteSpace(_definition.Parameter))
        {
            _definition.Parameter = "default";
        }

        if (_definition.Action is SequenceStepAction.SetSignal
            or SequenceStepAction.MoveAxis
            or SequenceStepAction.TriggerCamera)
        {
            _definition.TimeoutMs = 0;
        }

        if (_definition.Action is SequenceStepAction.WaitAxisDone or SequenceStepAction.WaitVisionResult)
        {
            _definition.Parameter = string.Empty;
        }

        if (_definition.Action == SequenceStepAction.WaitVisionResult && _definition.TimeoutMs <= 0)
        {
            _definition.TimeoutMs = 1000;
        }

        if (_definition.Action != SequenceStepAction.WaitVisionResult)
        {
            _definition.FailureStepId = null;
        }
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
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAllEditableFields()
    {
        OnPropertyChanged(nameof(TargetId));
        OnPropertyChanged(nameof(Parameter));
        OnPropertyChanged(nameof(TimeoutMs));
        OnPropertyChanged(nameof(NextStepId));
        OnPropertyChanged(nameof(ErrorStepId));
        OnPropertyChanged(nameof(FailureStepId));
        OnPropertyChanged(nameof(HasExpectedState));
        OnPropertyChanged(nameof(ExpectedTargetId));
        OnPropertyChanged(nameof(ExpectedState));
        OnPropertyChanged(nameof(AvailableExpectedStates));
    }

    private string DefaultParameterFor(string targetId) =>
        _authoringTargets.FirstOrDefault(target =>
            string.Equals(target.Id, targetId, StringComparison.Ordinal))?.DefaultParameter ?? string.Empty;
}
