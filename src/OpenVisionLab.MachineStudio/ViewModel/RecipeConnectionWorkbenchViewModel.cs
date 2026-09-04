using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Sequences;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class RecipeConnectionRowViewModel : ViewModelBase
{
    private bool _hasPreviewResult;
    private bool _isPreviewSuccessful;
    private bool _isPreviewWarning;
    private string _previewStatusText = string.Empty;
    private string _previewDetailText = string.Empty;
    private SequenceStepPreviewResult? _previewResult;

    public required string ComponentId { get; init; }
    public required string Name { get; init; }
    public required LayoutComponentKind Kind { get; init; }
    public required string KindText { get; init; }
    public required string BehaviorText { get; init; }
    public required string ConnectionText { get; init; }
    public required string SequenceText { get; init; }
    public required int SequenceUseCount { get; init; }
    public required string? FirstSequenceId { get; init; }
    public required string? FirstSequenceStepId { get; init; }
    public required SequenceStepAction? FirstSequenceAction { get; init; }
    public required string? SequenceTargetId { get; init; }
    public required bool CanAddSequenceStep { get; init; }
    public required IReadOnlySet<string> RelatedTargetIds { get; init; }
    public required bool IsConnected { get; init; }
    public required bool IsValid { get; init; }
    public required string ValidationText { get; init; }

    public string StatusText => OpenVisionLanguageService.T(
        IsValid ? "Connections.Valid" : "Connections.CheckRequired");
    public bool HasSequenceUse => FirstSequenceStepId is not null;
    public bool CanPreviewSequenceStep => HasSequenceUse && FirstSequenceAction is
        SequenceStepAction.MoveAxis or
        SequenceStepAction.Wait or
        SequenceStepAction.WaitAxisDone or
        SequenceStepAction.SetChannel or
        SequenceStepAction.SetSignal or
        SequenceStepAction.WaitSignal;
    public bool HasPreviewResult
    {
        get => _hasPreviewResult;
        private set => SetProperty(ref _hasPreviewResult, value);
    }
    public bool IsPreviewSuccessful
    {
        get => _isPreviewSuccessful;
        private set => SetProperty(ref _isPreviewSuccessful, value);
    }
    public bool IsPreviewWarning
    {
        get => _isPreviewWarning;
        private set => SetProperty(ref _isPreviewWarning, value);
    }
    public string PreviewStatusText
    {
        get => _previewStatusText;
        private set => SetProperty(ref _previewStatusText, value);
    }
    public string PreviewDetailText
    {
        get => _previewDetailText;
        private set => SetProperty(ref _previewDetailText, value);
    }
    public SequenceStepPreviewResult? PreviewResult
    {
        get => _previewResult;
        private set => SetProperty(ref _previewResult, value);
    }

    public void ApplyPreview(SequenceStepPreviewResult result, string observation)
    {
        PreviewResult = result;
        HasPreviewResult = true;
        IsPreviewSuccessful = result.Outcome == SequenceStepPreviewOutcome.Completed;
        IsPreviewWarning = result.Outcome is SequenceStepPreviewOutcome.LimitReached or SequenceStepPreviewOutcome.Faulted;
        PreviewStatusText = OpenVisionLanguageService.T(result.Outcome switch
        {
            SequenceStepPreviewOutcome.Completed => "Connections.PreviewCompleted",
            SequenceStepPreviewOutcome.LimitReached => "Connections.PreviewLimitReached",
            SequenceStepPreviewOutcome.Faulted => "Connections.PreviewFaulted",
            _ => "Connections.PreviewRejected"
        });
        PreviewDetailText = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.PreviewDetailFormat"),
            result.ExecutedTicks,
            result.MaximumTicks,
            observation);
    }
}

public sealed record LoadLockSetupOption(string Id, string DisplayName);

public sealed record SemiconductorProcessBlockItemPresentation(
    string? SequenceId,
    string StepId,
    string StepText,
    string DetailText,
    SequenceStepAction? Action,
    int? TimeoutMs,
    bool IsProposed,
    bool IsAlreadyConfigured,
    bool IsCustomized,
    bool IsProposedRemoval,
    bool IsUnavailable)
{
    public bool CanOpenSequenceStep => (IsAlreadyConfigured || IsCustomized)
        && !string.IsNullOrWhiteSpace(SequenceId)
        && !string.IsNullOrWhiteSpace(StepId);
    public bool CanAdjustTimeout => CanOpenSequenceStep
        && Action is { } action
        && SemiconductorProcessBlockComposer.CanAdjustTimeout(action)
        && TimeoutMs is not null;
}

public sealed record SemiconductorManagedTimeoutAdjustmentItemPresentation(
    string StepText,
    string DetailText);

public sealed class RecipeConnectionWorkbenchViewModel : ViewModelBase
{
    private readonly Action<string?> _selectComponent;
    private readonly Action<string, string> _openSequenceStep;
    private readonly Action<string, string> _openProcessBlockSequenceStep;
    private readonly Func<string, string?> _addSequenceStep;
    private readonly Func<bool> _applyVirtualCameraWorkflow;
    private readonly Action<int> _checkpointTemplateApplied;
    private readonly RecipeConnectionRowCatalog _rowCatalog = new();
    private readonly RelayCommand _openSequenceStepCommand;
    private readonly RelayCommand _addSequenceStepCommand;
    private readonly RelayCommand _createVirtualCameraWorkflowCommand;
    private MachineProjectDocument? _project;
    private RecipeConnectionRowViewModel? _selectedRow;
    private bool _isSynchronizingSelection;
    private bool _isEditable = true;
    public RecipeCheckpointTemplateViewModel CheckpointTemplate { get; }
    public StationSkeletonSetupViewModel StationSetups { get; }
    public SemanticEquipmentSetupViewModel SemanticSetups { get; }
    public LoadLockSetupViewModel LoadLocks { get; }

    public RecipeConnectionWorkbenchViewModel(
        Action<string?> selectComponent,
        Action<string, string> openSequenceStep,
        Func<string, string?> addSequenceStep,
        Func<string?> validateSimulationReadiness,
        Func<string, string, string, Task<SequenceStepPreviewResult>> previewSequenceStep,
        Func<string, Task<RecipeDryRunResult>> runRecipeDryRun,
        Action<RecipeDryRunStepPresentation> playRecipeDryRunStep,
        Func<bool> applyVirtualCameraWorkflow,
        Func<SemiconductorStationSetupDefinition, int> applyStationSkeleton,
        Func<LoadLockDefinition, int> applyLoadLockSetup,
        Func<WaferHandlerDefinition, int> applyWaferHandlerSetup,
        Func<PrealignerDefinition, int> applyPrealignerSetup,
        Func<InspectionHandoffDefinition, int> applyInspectionHandoffSetup,
        Func<InspectionSortRouterDefinition, int> applyInspectionSortRouterSetup,
        Func<OhtHandoffDefinition, int> applyOhtHandoffSetup,
        Func<IReadOnlyList<SemiconductorProcessBlockKind>, int> applyProcessBlock,
        Func<SemiconductorManagedTimeoutAdjustmentPreview, int> applyProcessBlockTimeouts,
        Action<int> checkpointTemplateApplied,
        Action<string, string>? openProcessBlockSequenceStep = null)
    {
        _selectComponent = selectComponent;
        _openSequenceStep = openSequenceStep;
        _openProcessBlockSequenceStep = openProcessBlockSequenceStep ?? openSequenceStep;
        _addSequenceStep = addSequenceStep;
        _applyVirtualCameraWorkflow = applyVirtualCameraWorkflow;
        _checkpointTemplateApplied = checkpointTemplateApplied;
        CheckpointTemplate = new RecipeCheckpointTemplateViewModel(
            ApplyCheckpointTemplate,
            ClearCheckpointTemplateCompetingPreviews);
        StationSetups = new StationSkeletonSetupViewModel(
            applyStationSkeleton,
            ClearStationSetupCompetingPreviews);
        SemanticSetups = new SemanticEquipmentSetupViewModel(
            applyWaferHandlerSetup,
            applyPrealignerSetup,
            applyInspectionHandoffSetup,
            applyInspectionSortRouterSetup,
            applyOhtHandoffSetup,
            ClearSemanticSetupCompetingPreviews);
        ProcessBlocks = new RecipeProcessBlockViewModel(
            applyProcessBlock,
            applyProcessBlockTimeouts,
            ClearProcessBlockCompetingPreviews,
            StationSkeletonSetupViewModel.CreateStationSkeletonItem);
        LoadLocks = new LoadLockSetupViewModel(
            applyLoadLockSetup,
            ClearLoadLockCompetingPreviews);
        DryRun = new RecipeDryRunViewModel(
            validateSimulationReadiness,
            runRecipeDryRun,
            openSequenceStep,
            playRecipeDryRunStep,
            SelectDryRunComponent,
            ResolveDryRunComponentId);
        SequenceStepPreview = new RecipeSequenceStepPreviewViewModel(
            previewSequenceStep,
            () => IsEditable,
            () => DryRun.ReadinessPassed == true,
            row => Rows.Contains(row));
        _openSequenceStepCommand = new RelayCommand(
            OpenSequenceStep,
            parameter => IsEditable && CanOpenSequenceStep(parameter));
        _addSequenceStepCommand = new RelayCommand(
            AddSequenceStep,
            parameter => IsEditable
                         && parameter is RecipeConnectionRowViewModel
                             {
                                 IsValid: true,
                                 CanAddSequenceStep: true
                             });
        _createVirtualCameraWorkflowCommand = new RelayCommand(
            _ => CreateVirtualCameraWorkflow(),
            _ => IsEditable && CanCreateVirtualCameraWorkflow);
        DryRun.PropertyChanged += OnDryRunPropertyChanged;
    }

    public ObservableCollection<RecipeConnectionRowViewModel> Rows { get; } = new();
    public RecipeProcessBlockViewModel ProcessBlocks { get; }
    public RecipeDryRunViewModel DryRun { get; }
    public RecipeSequenceStepPreviewViewModel SequenceStepPreview { get; }
    public ICommand OpenSequenceStepCommand => _openSequenceStepCommand;
    public ICommand AddSequenceStepCommand => _addSequenceStepCommand;
    // Compatibility facade for existing DirectExeSmokeHost call sites. New UI
    // paths use SequenceStepPreview directly so this parent owns no preview
    // command or orchestration state.
    public ICommand PreviewSequenceStepCommand => SequenceStepPreview.PreviewSequenceStepCommand;
    public ICommand CreateVirtualCameraWorkflowCommand => _createVirtualCameraWorkflowCommand;
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value))
            {
                return;
            }

            _openSequenceStepCommand.RaiseCanExecuteChanged();
            _addSequenceStepCommand.RaiseCanExecuteChanged();
            _createVirtualCameraWorkflowCommand.RaiseCanExecuteChanged();
            DryRun.IsEditable = value;
            SequenceStepPreview.RefreshCanExecute();
            StationSetups.IsEditable = value;
            SemanticSetups.IsEditable = value;
            ProcessBlocks.IsEditable = value;
            LoadLocks.IsEditable = value;
            CheckpointTemplate.IsEditable = value;
        }
    }

    public RecipeConnectionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value) || _isSynchronizingSelection)
            {
                return;
            }

            _selectComponent(value?.ComponentId);
        }
    }

    public bool HasRows => Rows.Count > 0;
    public bool CanCreateVirtualCameraWorkflow => _project is not null
        && !_project.Devices.Any(device => device.Kind == DeviceKind.Camera);
    public int ComponentCount => Rows.Count;
    public int ConnectedCount => Rows.Count(row => row.IsConnected);
    public int SequenceUseCount => Rows.Sum(row => row.SequenceUseCount);
    public int RecipeStepCount => ResolveRecipeSequence()?.Steps.Count ?? 0;
    public int CheckpointStepCount => ResolveRecipeSequence()?.Steps.Count(step => !string.IsNullOrWhiteSpace(step.ExpectedTargetId) && !string.IsNullOrWhiteSpace(step.ExpectedState)) ?? 0;
    public string CheckpointCoverageText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Connections.CheckpointCoverageFormat"),
        CheckpointStepCount,
        RecipeStepCount);
    public bool HasValidationErrors => Rows.Any(row => !row.IsValid);
    public string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Connections.SummaryFormat"),
        ComponentCount,
        ConnectedCount,
        SequenceUseCount);
    public string ValidationSummaryText => OpenVisionLanguageService.T(
        HasValidationErrors ? "Connections.ValidationErrors" : "Connections.ValidationPassed");
    // Compatibility facade for existing DirectExeSmokeHost call sites. New UI
    // and MainViewModel paths use DryRun directly so this parent owns no state.
    public ICommand ValidateSimulationReadinessCommand => DryRun.ValidateSimulationReadinessCommand;
    public ICommand RunRecipeDryRunCommand => DryRun.RunRecipeDryRunCommand;
    public ICommand OpenRecipeDryRunStepCommand => DryRun.OpenRecipeDryRunStepCommand;
    public ICommand PlayRecipeDryRunStepCommand => DryRun.PlayRecipeDryRunStepCommand;
    public ObservableCollection<RecipeDryRunStepPresentation> RecipeDryRunTimeline => DryRun.Timeline;
    public ObservableCollection<RecipeDryRunEquipmentStatePresentation> RecipeDryRunFinalStates => DryRun.FinalStates;
    public bool? ReadinessPassed => DryRun.ReadinessPassed;
    public string ReadinessStatusText => DryRun.ReadinessStatusText;
    public string ReadinessDetailText => DryRun.ReadinessDetailText;
    public bool IsRecipeDryRunRunning => DryRun.IsRecipeDryRunRunning;
    public RecipeDryRunResult? RecipeDryRunResult => DryRun.RecipeDryRunResult;
    public bool HasRecipeDryRunResult => DryRun.HasRecipeDryRunResult;
    public bool RecipeDryRunPassed => DryRun.RecipeDryRunPassed;
    public bool RecipeDryRunWarning => DryRun.RecipeDryRunWarning;
    public bool HasRecipeDryRunIssue => DryRun.HasRecipeDryRunIssue;
    public RecipeDryRunStepPresentation? SelectedRecipeDryRunStep
    {
        get => DryRun.SelectedRecipeDryRunStep;
        set => DryRun.SelectedRecipeDryRunStep = value;
    }
    public string RecipeDryRunStatusText => DryRun.RecipeDryRunStatusText;
    public string RecipeDryRunDetailText => DryRun.RecipeDryRunDetailText;
    public string RecipeDryRunIssueText => DryRun.RecipeDryRunIssueText;

    public void Load(
        MachineProjectDocument project,
        string? selectedComponentId = null,
        bool preserveReadiness = false)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        DryRun.Load(project, preserveReadiness);
        CheckpointTemplate.Load(project);
        StationSetups.Load(project);
        SemanticSetups.ClearPreviewForCompetingSetup();
        ProcessBlocks.ClearPreviewForCompetingSetup();
        LoadLocks.ClearPreviewForCompetingSetup();
        SemanticSetups.Load(project);
        ProcessBlocks.Load(project);
        LoadLocks.Load(project);
        var validation = new MachineProjectLayoutValidator().Validate(project);
        var canEditSequenceStructure = SequenceDefinitionEditor.IsStrictLinear(ResolveRecipeSequence());

        var wasSynchronizingSelection = _isSynchronizingSelection;
        _isSynchronizingSelection = true;
        try
        {
            Rows.Clear();
            foreach (var row in _rowCatalog.BuildRows(
                         project,
                         validation,
                         canEditSequenceStructure))
            {
                Rows.Add(row);
            }

            SynchronizeSelection(selectedComponentId);
        }
        finally
        {
            _isSynchronizingSelection = wasSynchronizingSelection;
        }
        OnPropertyChanged(nameof(CanCreateVirtualCameraWorkflow));
        _createVirtualCameraWorkflowCommand.RaiseCanExecuteChanged();
        RaiseSummaryChanged();
    }

    public void RefreshDefinitionPreservingProcessBlockPlan(string? selectedComponentId = null)
    {
        ProcessBlocks.RefreshDefinitionPreservingPlan(
            () => Load(_project ?? throw new InvalidOperationException("No project is loaded."), selectedComponentId),
            selectedComponentId);
    }

    public void SynchronizeSelection(string? componentId)
    {
        var wasSynchronizingSelection = _isSynchronizingSelection;
        _isSynchronizingSelection = true;
        try
        {
            SelectedRow = Rows.FirstOrDefault(row =>
                string.Equals(row.ComponentId, componentId, StringComparison.Ordinal));
        }
        finally
        {
            _isSynchronizingSelection = wasSynchronizingSelection;
        }
    }

    public void RefreshLocalization()
    {
        if (_project is not null)
        {
            CheckpointTemplate.RefreshLocalization(() =>
                StationSetups.RefreshLocalization(() =>
                    SemanticSetups.RefreshLocalization(() =>
                        LoadLocks.RefreshLocalization(() =>
                            ProcessBlocks.PreservePlanAcross(() => Load(
                                _project,
                                SelectedRow?.ComponentId,
                                preserveReadiness: true))))));
        }
    }

    private void OpenSequenceStep(object? parameter)
    {
        switch (parameter)
        {
            case RecipeConnectionRowViewModel
            {
                FirstSequenceId: { } sequenceId,
                FirstSequenceStepId: { } stepId
            }:
                _openSequenceStep(sequenceId, stepId);
                break;
            case SemiconductorProcessBlockItemPresentation
            {
                CanOpenSequenceStep: true,
                SequenceId: { } sequenceId,
                StepId: { } stepId
            }:
                _openProcessBlockSequenceStep(sequenceId, stepId);
                break;
        }
    }

    private void CreateVirtualCameraWorkflow()
    {
        if (!_applyVirtualCameraWorkflow())
        {
            return;
        }

        OnPropertyChanged(nameof(CanCreateVirtualCameraWorkflow));
        _createVirtualCameraWorkflowCommand.RaiseCanExecuteChanged();
    }

    private static bool CanOpenSequenceStep(object? parameter) => parameter switch
    {
        RecipeConnectionRowViewModel { HasSequenceUse: true } => true,
        SemiconductorProcessBlockItemPresentation { CanOpenSequenceStep: true } => true,
        _ => false
    };

    private void AddSequenceStep(object? parameter)
    {
        if (parameter is RecipeConnectionRowViewModel { SequenceTargetId: { } targetId })
        {
            _addSequenceStep(targetId);
        }
    }

    private void ClearStationSetupCompetingPreviews()
    {
        ProcessBlocks.ClearPreviewForCompetingSetup();
        CheckpointTemplate.ClearPreviewForCompetingSetup();
        LoadLocks.ClearPreviewForCompetingSetup();
    }

    private void ClearSemanticSetupCompetingPreviews()
    {
        StationSetups.ClearPreviewForCompetingSetup();
        LoadLocks.ClearPreviewForCompetingSetup();
        SemanticSetups.ClearPreviewForCompetingSetup();
        ProcessBlocks.ClearPreviewForCompetingSetup();
        CheckpointTemplate.ClearPreviewForCompetingSetup();
    }

    private void ClearProcessBlockCompetingPreviews()
    {
        StationSetups.ClearPreviewForCompetingSetup();
        LoadLocks.ClearPreviewForCompetingSetup();
        SemanticSetups.ClearPreviewForCompetingSetup();
        CheckpointTemplate.ClearPreviewForCompetingSetup();
    }

    private void ClearLoadLockCompetingPreviews()
    {
        StationSetups.ClearPreviewForCompetingSetup();
        SemanticSetups.ClearPreviewForCompetingSetup();
        ProcessBlocks.ClearPreviewForCompetingSetup();
        CheckpointTemplate.ClearPreviewForCompetingSetup();
    }

    private void ClearCheckpointTemplateCompetingPreviews()
    {
        StationSetups.ClearPreviewForCompetingSetup();
        SemanticSetups.ClearPreviewForCompetingSetup();
        ProcessBlocks.ClearPreviewForCompetingSetup();
        LoadLocks.ClearPreviewForCompetingSetup();
    }

    private int ApplyCheckpointTemplate(RepresentativeRecipeCheckpointTemplatePreview preview)
    {
        if (_project is null)
        {
            return 0;
        }

        var selectedComponentId = SelectedRow?.ComponentId;
        var applied = new RepresentativeRecipeCheckpointTemplate().Apply(_project, preview);
        _checkpointTemplateApplied(applied);
        Load(_project, selectedComponentId);
        return applied;
    }

    private string? ResolveRecipeSequenceId() =>
        _project?.Simulation.AutomaticRun?.SequenceId
        ?? _project?.Sequences.FirstOrDefault()?.Id;

    private SequenceDefinition? ResolveRecipeSequence()
    {
        var sequenceId = ResolveRecipeSequenceId();
        return sequenceId is null
            ? null
            : _project?.Sequences.FirstOrDefault(sequence =>
                string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal));
    }

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ComponentCount));
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(SequenceUseCount));
        OnPropertyChanged(nameof(RecipeStepCount));
        OnPropertyChanged(nameof(CheckpointStepCount));
        OnPropertyChanged(nameof(CheckpointCoverageText));
        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ValidationSummaryText));
    }

    private string? ResolveDryRunComponentId(string? relatedTargetId) =>
        relatedTargetId is null
            ? null
            : Rows.FirstOrDefault(row => row.RelatedTargetIds.Contains(relatedTargetId))?.ComponentId;

    private void SelectDryRunComponent(string? componentId)
    {
        SelectedRow = Rows.FirstOrDefault(row =>
            string.Equals(row.ComponentId, componentId, StringComparison.Ordinal));
    }

    private void OnDryRunPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RecipeDryRunViewModel.ReadinessPassed))
        {
            SequenceStepPreview.RefreshCanExecute();
        }
    }
}
