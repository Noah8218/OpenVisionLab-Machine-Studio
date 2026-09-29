using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
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
    private readonly ICommand _addStepCommand;
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
        _stepEditors = new SequenceStepEditorCollection(_templateCatalog, _targetCatalog);
        _stepEditors.DefinitionChanged += OnStepDefinitionChanged;
        _addStepCommand = new RelayCommand(_ => AddStep(), _ => CanAddStep());
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
                ResetValidationComparison();
                LoadAuthoringTargets();
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
        _runtimeSequenceId = null;
        _runtimeStepId = null;
        _project = project;
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
        if (SelectedSequence is null)
        {
            LoadAuthoringTargets();
            LoadSteps();
        }
    }

    public void RefreshAuthoringTargets()
    {
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
        OnPropertyChanged(nameof(CurrentRuntimeStepName));
    }

    private void LoadAuthoringTargets()
    {
        SequenceAuthoringTargetCatalogSnapshot targetCatalog = _targetCatalog.Build(_project);
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
    }

    public void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(Sequences));
        OnPropertyChanged(nameof(SelectedSequence));
        OnPropertyChanged(nameof(Templates));
        OnPropertyChanged(nameof(SelectedTemplate));
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(StructuralEditStatus));
        OnPropertyChanged(nameof(ValidationComparisonText));
        _stepEditors.RefreshLocalization();
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
        if (_disposed || !IsEditable || SelectedSequence is null || target is null)
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
        _stepEditors.Clear();
        SelectedStep = null;
        if (SelectedSequence is not null)
        {
        _stepEditors.Populate(SelectedSequence, _authoringTargets, _workpieceTargets, _expectedStateTargets);
            SelectedStep = _stepEditors.Find(selectedStepId) ?? Steps.FirstOrDefault();
        }

        UpdateExecutionStepIndicators();

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
            _targetCatalog.GetTargetsForSequence(_authoringTargets, SelectedSequence));
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
        !_disposed
        && IsEditable
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
            return;
        }

        SequenceCompilationResult result = new SequenceCompiler().Compile(
            SelectedSequence,
            _targetCatalog.BuildCompilationTargets(_project));
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

        SelectedValidationIssue = ValidationIssues.FirstOrDefault(issue =>
            string.Equals(GetIssueKey(issue), previousIssueKey, StringComparison.Ordinal))
            ?? ValidationIssues.FirstOrDefault();

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
        SequenceCompilationErrorCode.ErrorStepNotFound
            or SequenceCompilationErrorCode.FailureStepRequired
            or SequenceCompilationErrorCode.FailureStepNotFound
            or SequenceCompilationErrorCode.FailureStepNotAllowed => "Step.ErrorStepId",
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
        _stepEditors.DefinitionChanged -= OnStepDefinitionChanged;
        _stepEditors.Dispose();
        InvalidateCommands();
    }
}
