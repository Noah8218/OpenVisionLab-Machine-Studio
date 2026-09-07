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

    private void LoadAuthoringTargets()
    {
        SequenceAuthoringTargetCatalogSnapshot targetCatalog = _targetCatalog.Build(_project);
        _authoringTargets = targetCatalog.AuthoringTargets;
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
        OnPropertyChanged(nameof(Sequences));
        OnPropertyChanged(nameof(SelectedSequence));
        _stepEditors.RefreshLocalization();
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
            _stepEditors.Populate(SelectedSequence, _authoringTargets, _expectedStateTargets);
            SelectedStep = _stepEditors.Find(selectedStepId) ?? Steps.FirstOrDefault();
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

        SequenceCompilationResult result = new SequenceCompiler().Compile(
            SelectedSequence,
            _targetCatalog.BuildCompilationTargets(_project));
        foreach (SequenceCompilationError error in result.Errors)
        {
            ValidationMessages.Add($"{error.Code} [{error.StepId ?? "sequence"}]: {error.Message}");
        }

        _stepEditors.SetValidation(result.Errors);

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

    internal void Dispose()
    {
        _stepEditors.DefinitionChanged -= OnStepDefinitionChanged;
        _stepEditors.Dispose();
    }
}
