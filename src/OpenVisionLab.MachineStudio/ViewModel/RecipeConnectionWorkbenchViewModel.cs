using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;

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
    public required IReadOnlySet<string> RelatedTargetIds { get; init; }
    public required bool IsConnected { get; init; }
    public required bool IsValid { get; init; }
    public required string ValidationText { get; init; }

    public string StatusText => OpenVisionLanguageService.T(
        IsValid ? "Connections.Valid" : "Connections.CheckRequired");
    public bool HasSequenceUse => FirstSequenceStepId is not null;
    public bool CanAddSequenceStep => !HasSequenceUse && SequenceTargetId is not null;
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

public sealed record RecipeDryRunStepPresentation(
    string SequenceId,
    string StepId,
    string? ComponentId,
    string OrderText,
    string Name,
    string TickText,
    bool HasIssue,
    bool HasCheckpoint,
    bool HasCheckpointMismatch,
    string CheckpointText,
    SimulationSnapshot BoundarySnapshot);

public sealed record RecipeCheckpointTemplateItemPresentation(
    string RoleText,
    string DetailText,
    bool IsProposed,
    bool IsAlreadyConfigured,
    bool IsUnavailable);

public sealed record SemiconductorStationSkeletonItemPresentation(
    string RoleText,
    string DetailText,
    bool IsProposed,
    bool IsAlreadyConfigured,
    bool IsUnavailable);

public sealed record LoadLockSetupOption(string Id, string DisplayName);

public sealed record RecipeDryRunEquipmentStatePresentation(
    string Text,
    bool IsLoadLock = false,
    bool IsWaferHandler = false,
    bool IsInspectionSorter = false,
    bool IsInspectionHandoff = false,
    bool IsOhtHandoff = false,
    bool IsPrealigner = false,
    bool IsFault = false);

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
    private enum ProcessBlockItemFilter
    {
        All,
        Customized,
        Removal,
        Conflict
    }

    private readonly Action<string?> _selectComponent;
    private readonly Action<string, string> _openSequenceStep;
    private readonly Action<string, string> _openProcessBlockSequenceStep;
    private readonly Func<string, string?> _addSequenceStep;
    private readonly Func<string?> _validateSimulationReadiness;
    private readonly Func<string, string, string, Task<SequenceStepPreviewResult>> _previewSequenceStep;
    private readonly Func<string, Task<RecipeDryRunResult>> _runRecipeDryRun;
    private readonly Action<RecipeDryRunStepPresentation> _playRecipeDryRunStep;
    private readonly Func<SemiconductorStationSetupDefinition, int> _applyStationSkeleton;
    private readonly Func<LoadLockDefinition, int> _applyLoadLockSetup;
    private readonly Func<WaferHandlerDefinition, int> _applyWaferHandlerSetup;
    private readonly Func<PrealignerDefinition, int> _applyPrealignerSetup;
    private readonly Func<InspectionHandoffDefinition, int> _applyInspectionHandoffSetup;
    private readonly Func<InspectionSortRouterDefinition, int> _applyInspectionSortRouterSetup;
    private readonly Func<OhtHandoffDefinition, int> _applyOhtHandoffSetup;
    private readonly Func<IReadOnlyList<SemiconductorProcessBlockKind>, int> _applyProcessBlock;
    private readonly Func<SemiconductorManagedTimeoutAdjustmentPreview, int> _applyProcessBlockTimeouts;
    private readonly Action<int> _checkpointTemplateApplied;
    private readonly SemiconductorStationSkeletonTemplate _stationSkeletonTemplate = new();
    private readonly SemiconductorProcessBlockComposer _processBlockComposer = new();
    private readonly RepresentativeRecipeCheckpointTemplate _checkpointTemplate = new();
    private readonly RelayCommand _openSequenceStepCommand;
    private readonly RelayCommand _addSequenceStepCommand;
    private readonly RelayCommand _validateSimulationReadinessCommand;
    private readonly AsyncRelayCommand _previewSequenceStepCommand;
    private readonly AsyncRelayCommand _runRecipeDryRunCommand;
    private readonly RelayCommand _openRecipeDryRunStepCommand;
    private readonly RelayCommand _playRecipeDryRunStepCommand;
    private readonly RelayCommand _previewStationSkeletonCommand;
    private readonly RelayCommand _applyStationSkeletonCommand;
    private readonly RelayCommand _cancelStationSkeletonCommand;
    private readonly RelayCommand _resetStationSetupCommand;
    private readonly RelayCommand _previewLoadLockSetupCommand;
    private readonly RelayCommand _applyLoadLockSetupCommand;
    private readonly RelayCommand _cancelLoadLockSetupCommand;
    private readonly RelayCommand _resetLoadLockSetupCommand;
    private readonly RelayCommand _previewWaferHandlerSetupCommand;
    private readonly RelayCommand _applyWaferHandlerSetupCommand;
    private readonly RelayCommand _cancelWaferHandlerSetupCommand;
    private readonly RelayCommand _resetWaferHandlerSetupCommand;
    private readonly RelayCommand _previewPrealignerSetupCommand;
    private readonly RelayCommand _applyPrealignerSetupCommand;
    private readonly RelayCommand _cancelPrealignerSetupCommand;
    private readonly RelayCommand _resetPrealignerSetupCommand;
    private readonly RelayCommand _previewInspectionHandoffSetupCommand;
    private readonly RelayCommand _applyInspectionHandoffSetupCommand;
    private readonly RelayCommand _cancelInspectionHandoffSetupCommand;
    private readonly RelayCommand _resetInspectionHandoffSetupCommand;
    private readonly RelayCommand _previewInspectionSortRouterSetupCommand;
    private readonly RelayCommand _applyInspectionSortRouterSetupCommand;
    private readonly RelayCommand _cancelInspectionSortRouterSetupCommand;
    private readonly RelayCommand _resetInspectionSortRouterSetupCommand;
    private readonly RelayCommand _previewOhtHandoffSetupCommand;
    private readonly RelayCommand _applyOhtHandoffSetupCommand;
    private readonly RelayCommand _cancelOhtHandoffSetupCommand;
    private readonly RelayCommand _resetOhtHandoffSetupCommand;
    private readonly RelayCommand _previewProcessBlockCommand;
    private readonly RelayCommand _applyProcessBlockCommand;
    private readonly RelayCommand _cancelProcessBlockCommand;
    private readonly RelayCommand _previewProcessBlockTimeoutsCommand;
    private readonly RelayCommand _applyProcessBlockTimeoutsCommand;
    private readonly RelayCommand _cancelProcessBlockTimeoutsCommand;
    private readonly RelayCommand _previewCheckpointTemplateCommand;
    private readonly RelayCommand _applyCheckpointTemplateCommand;
    private readonly RelayCommand _cancelCheckpointTemplateCommand;
    private MachineProjectDocument? _project;
    private RecipeConnectionRowViewModel? _selectedRow;
    private SemiconductorProcessBlockItemPresentation? _selectedProcessBlockItem;
    private bool _isSynchronizingSelection;
    private bool _isPreservingProcessBlockPlan;
    private bool _isEditable = true;
    private bool? _readinessPassed;
    private string? _readinessError;
    private int _definitionRevision;
    private bool _isRecipeDryRunRunning;
    private RecipeDryRunResult? _recipeDryRunResult;
    private RecipeDryRunStepPresentation? _selectedRecipeDryRunStep;
    private string _recipeDryRunStatusText = string.Empty;
    private string _recipeDryRunDetailText = string.Empty;
    private string _recipeDryRunIssueText = string.Empty;
    private SemiconductorStationSkeletonPreview? _stationSkeletonPreview;
    private string _stationName = SemiconductorStationSetupDefinition.DefaultStationName;
    private string _waferType = SemiconductorStationSetupDefinition.DefaultWaferType;
    private string _axisTravelText = string.Empty;
    private string _transportSpeedText = string.Empty;
    private string _entrySensorPositionText = string.Empty;
    private string _processSensorPositionText = string.Empty;
    private string _cylinderTravelTimeText = string.Empty;
    private bool _stationSetupWasInvalid;
    private bool _isLoadLockSetupVisible;
    private LoadLockDefinition? _savedLoadLockSetup;
    private string? _outerDoorComponentId;
    private string? _innerDoorComponentId;
    private string? _evacuateCommandChannelId;
    private string? _ventCommandChannelId;
    private string? _vacuumReadySensorChannelId;
    private string? _atmosphereReadySensorChannelId;
    private string _pumpDownDurationText = string.Empty;
    private string _ventDurationText = string.Empty;
    private bool _isWaferHandlerSetupVisible;
    private bool _isPrealignerSetupVisible;
    private WaferHandlerDefinition? _savedWaferHandlerSetup;
    private PrealignerDefinition? _savedPrealignerSetup;
    private string? _waferHandlerHorizontalAxisId;
    private string? _waferHandlerVerticalAxisId;
    private string? _waferHandlerWorkpieceComponentId;
    private string? _waferHandlerSourcePresentSensorChannelId;
    private string? _waferHandlerGateOpenSensorChannelId;
    private string? _waferHandlerPickCommandChannelId;
    private string? _waferHandlerPlaceCommandChannelId;
    private string? _waferHandlerHoldingFeedbackChannelId;
    private string? _waferHandlerPlacedFeedbackChannelId;
    private string _waferHandlerPickHorizontalText = string.Empty;
    private string _waferHandlerPickVerticalText = string.Empty;
    private string _waferHandlerPlaceHorizontalText = string.Empty;
    private string _waferHandlerPlaceVerticalText = string.Empty;
    private string? _prealignerRotaryStageComponentId;
    private string? _prealignerClampCylinderComponentId;
    private string? _prealignerWaferPresentSensorChannelId;
    private string? _prealignerAlignmentAcceptedCommandChannelId;
    private string? _prealignerAlignmentReadyFeedbackChannelId;
    private string? _prealignerAlignmentCompleteFeedbackChannelId;
    private string _prealignerAlignmentTargetText = string.Empty;
    private string _prealignerAlignmentToleranceText = string.Empty;
    private bool _isInspectionHandoffSetupVisible;
    private bool _isInspectionSortRouterSetupVisible;
    private bool _isOhtHandoffSetupVisible;
    private InspectionHandoffDefinition? _savedInspectionHandoffSetup;
    private InspectionSortRouterDefinition? _savedInspectionSortRouterSetup;
    private OhtHandoffDefinition? _savedOhtHandoffSetup;
    private string? _inspectionHandoffCameraId;
    private string? _inspectionHandoffPositionSensorChannelId;
    private string? _inspectionHandoffAcceptedChannelId;
    private string? _inspectionHandoffReadyChannelId;
    private string? _inspectionHandoffCompleteChannelId;
    private string? _inspectionSortCameraId;
    private string? _inspectionSortPassConveyorId;
    private string? _inspectionSortNgConveyorId;
    private string? _inspectionSortPassFeedbackChannelId;
    private string? _inspectionSortNgFeedbackChannelId;
    private string? _ohtTransportConveyorId;
    private string? _ohtRouteAvailableChannelId;
    private string? _ohtVehicleDockedChannelId;
    private string? _ohtLoadPortReadyChannelId;
    private string? _ohtCarrierReceivedChannelId;
    private string? _ohtHandoffReadyChannelId;
    private string? _ohtCarrierTransferredChannelId;
    private SemiconductorProcessBlockPlanPreview? _processBlockPreview;
    private bool _isLoadBlockSelected = true;
    private bool _isAlignBlockSelected = true;
    private bool _isProcessBlockSelected = true;
    private bool _isInspectBlockSelected = true;
    private bool _isUnloadBlockSelected = true;
    private ProcessBlockItemFilter _processBlockItemFilter;
    private string _processBlockTimeoutText = "5000";
    private SemiconductorManagedTimeoutAdjustmentPreview? _processBlockTimeoutPreview;
    private RepresentativeRecipeCheckpointTemplatePreview? _checkpointTemplatePreview;

    public RecipeConnectionWorkbenchViewModel(
        Action<string?> selectComponent,
        Action<string, string> openSequenceStep,
        Func<string, string?> addSequenceStep,
        Func<string?> validateSimulationReadiness,
        Func<string, string, string, Task<SequenceStepPreviewResult>> previewSequenceStep,
        Func<string, Task<RecipeDryRunResult>> runRecipeDryRun,
        Action<RecipeDryRunStepPresentation> playRecipeDryRunStep,
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
        _validateSimulationReadiness = validateSimulationReadiness;
        _previewSequenceStep = previewSequenceStep;
        _runRecipeDryRun = runRecipeDryRun;
        _playRecipeDryRunStep = playRecipeDryRunStep;
        _applyStationSkeleton = applyStationSkeleton;
        _applyLoadLockSetup = applyLoadLockSetup;
        _applyWaferHandlerSetup = applyWaferHandlerSetup;
        _applyPrealignerSetup = applyPrealignerSetup;
        _applyInspectionHandoffSetup = applyInspectionHandoffSetup;
        _applyInspectionSortRouterSetup = applyInspectionSortRouterSetup;
        _applyOhtHandoffSetup = applyOhtHandoffSetup;
        _applyProcessBlock = applyProcessBlock;
        _applyProcessBlockTimeouts = applyProcessBlockTimeouts;
        _checkpointTemplateApplied = checkpointTemplateApplied;
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
        _validateSimulationReadinessCommand = new RelayCommand(
            _ => ValidateSimulationReadiness(),
            _ => IsEditable);
        _previewSequenceStepCommand = new AsyncRelayCommand(
            PreviewSequenceStepAsync,
            parameter => IsEditable
                         && _readinessPassed == true
                         && parameter is RecipeConnectionRowViewModel { CanPreviewSequenceStep: true });
        _runRecipeDryRunCommand = new AsyncRelayCommand(
            RunRecipeDryRunAsync,
            _ => IsEditable
                 && _readinessPassed == true
                 && !_isRecipeDryRunRunning
                 && ResolveRecipeSequenceId() is not null);
        _openRecipeDryRunStepCommand = new RelayCommand(
            OpenRecipeDryRunStep,
            parameter => IsEditable && parameter is RecipeDryRunStepPresentation);
        _playRecipeDryRunStepCommand = new RelayCommand(
            PlayRecipeDryRunStep,
            parameter => IsEditable && parameter is RecipeDryRunStepPresentation);
        _previewStationSkeletonCommand = new RelayCommand(
            _ => PreviewStationSkeleton(),
            _ => IsEditable);
        _applyStationSkeletonCommand = new RelayCommand(
            _ => ApplyStationSkeleton(),
            ignored => IsEditable
                 && _stationSkeletonPreview is { UnavailableCount: 0 }
                 && TryCreateStationSetup(out _));
        _cancelStationSkeletonCommand = new RelayCommand(
            _ => ClearStationSkeletonPreview(),
            _ => IsStationSkeletonPreviewVisible);
        _resetStationSetupCommand = new RelayCommand(
            _ => ResetStationSetup(),
            _ => IsEditable && IsStationSkeletonPreviewVisible);
        _previewLoadLockSetupCommand = new RelayCommand(
            _ => PreviewLoadLockSetup(),
            _ => IsEditable && _project is not null);
        _applyLoadLockSetupCommand = new RelayCommand(
            _ => ApplyLoadLockSetup(),
            ignored => IsEditable && IsLoadLockSetupVisible && TryCreateLoadLockSetup(out _));
        _cancelLoadLockSetupCommand = new RelayCommand(
            _ => ClearLoadLockSetup(),
            _ => IsLoadLockSetupVisible);
        _resetLoadLockSetupCommand = new RelayCommand(
            _ => ResetLoadLockSetup(),
            _ => IsEditable && IsLoadLockSetupVisible);
        _previewWaferHandlerSetupCommand = new RelayCommand(
            _ => PreviewWaferHandlerSetup(),
            _ => IsEditable && _project is not null);
        _applyWaferHandlerSetupCommand = new RelayCommand(
            _ => ApplyWaferHandlerSetup(),
            _ => IsEditable && IsWaferHandlerSetupVisible && CanCreateWaferHandlerSetup());
        _cancelWaferHandlerSetupCommand = new RelayCommand(
            _ => ClearWaferHandlerSetup(),
            _ => IsWaferHandlerSetupVisible);
        _resetWaferHandlerSetupCommand = new RelayCommand(
            _ => ResetWaferHandlerSetup(),
            _ => IsEditable && IsWaferHandlerSetupVisible);
        _previewPrealignerSetupCommand = new RelayCommand(
            _ => PreviewPrealignerSetup(),
            _ => IsEditable && _project is not null);
        _applyPrealignerSetupCommand = new RelayCommand(
            _ => ApplyPrealignerSetup(),
            _ => IsEditable && IsPrealignerSetupVisible && CanCreatePrealignerSetup());
        _cancelPrealignerSetupCommand = new RelayCommand(
            _ => ClearPrealignerSetup(),
            _ => IsPrealignerSetupVisible);
        _resetPrealignerSetupCommand = new RelayCommand(
            _ => ResetPrealignerSetup(),
            _ => IsEditable && IsPrealignerSetupVisible);
        _previewInspectionHandoffSetupCommand = new RelayCommand(
            _ => PreviewInspectionHandoffSetup(),
            _ => IsEditable && _project is not null);
        _applyInspectionHandoffSetupCommand = new RelayCommand(
            _ => ApplyInspectionHandoffSetup(),
            _ => IsEditable && IsInspectionHandoffSetupVisible && CanCreateInspectionHandoffSetup());
        _cancelInspectionHandoffSetupCommand = new RelayCommand(
            _ => ClearInspectionHandoffSetup(),
            _ => IsInspectionHandoffSetupVisible);
        _resetInspectionHandoffSetupCommand = new RelayCommand(
            _ => ResetInspectionHandoffSetup(),
            _ => IsEditable && IsInspectionHandoffSetupVisible);
        _previewInspectionSortRouterSetupCommand = new RelayCommand(
            _ => PreviewInspectionSortRouterSetup(),
            _ => IsEditable && _project is not null);
        _applyInspectionSortRouterSetupCommand = new RelayCommand(
            _ => ApplyInspectionSortRouterSetup(),
            _ => IsEditable && IsInspectionSortRouterSetupVisible && CanCreateInspectionSortRouterSetup());
        _cancelInspectionSortRouterSetupCommand = new RelayCommand(
            _ => ClearInspectionSortRouterSetup(),
            _ => IsInspectionSortRouterSetupVisible);
        _resetInspectionSortRouterSetupCommand = new RelayCommand(
            _ => ResetInspectionSortRouterSetup(),
            _ => IsEditable && IsInspectionSortRouterSetupVisible);
        _previewOhtHandoffSetupCommand = new RelayCommand(
            _ => PreviewOhtHandoffSetup(),
            _ => IsEditable && _project is not null);
        _applyOhtHandoffSetupCommand = new RelayCommand(
            _ => ApplyOhtHandoffSetup(),
            _ => IsEditable && IsOhtHandoffSetupVisible && CanCreateOhtHandoffSetup());
        _cancelOhtHandoffSetupCommand = new RelayCommand(
            _ => ClearOhtHandoffSetup(),
            _ => IsOhtHandoffSetupVisible);
        _resetOhtHandoffSetupCommand = new RelayCommand(
            _ => ResetOhtHandoffSetup(),
            _ => IsEditable && IsOhtHandoffSetupVisible);
        _previewProcessBlockCommand = new RelayCommand(
            _ => OpenProcessBlockPlan(),
            _ => IsEditable);
        _applyProcessBlockCommand = new RelayCommand(
            _ => ApplyProcessBlock(),
            _ => IsEditable && _processBlockPreview?.CanApply == true);
        _cancelProcessBlockCommand = new RelayCommand(
            _ => CancelProcessBlockPreview(),
            _ => IsProcessBlockPreviewVisible);
        _previewProcessBlockTimeoutsCommand = new RelayCommand(
            _ => PreviewProcessBlockTimeouts(),
            _ => IsEditable
                 && IsProcessBlockPreviewVisible
                 && IsProcessBlockTimeoutValid
                 && CompatibleProcessBlockTimeoutCount > 0);
        _applyProcessBlockTimeoutsCommand = new RelayCommand(
            _ => ApplyProcessBlockTimeouts(),
            _ => IsEditable && _processBlockTimeoutPreview?.CanApply == true);
        _cancelProcessBlockTimeoutsCommand = new RelayCommand(
            _ => ClearProcessBlockTimeoutPreview(),
            _ => IsProcessBlockTimeoutPreviewVisible);
        _previewCheckpointTemplateCommand = new RelayCommand(
            _ => PreviewCheckpointTemplate(),
            _ => IsEditable && ResolveRecipeSequenceId() is not null);
        _applyCheckpointTemplateCommand = new RelayCommand(
            _ => ApplyCheckpointTemplate(),
            _ => IsEditable && _checkpointTemplatePreview?.ProposedCount > 0);
        _cancelCheckpointTemplateCommand = new RelayCommand(
            _ => ClearCheckpointTemplatePreview(),
            _ => IsCheckpointTemplatePreviewVisible);
    }

    public ObservableCollection<RecipeConnectionRowViewModel> Rows { get; } = new();
    public ObservableCollection<RecipeDryRunStepPresentation> RecipeDryRunTimeline { get; } = new();
    public ObservableCollection<RecipeDryRunEquipmentStatePresentation> RecipeDryRunFinalStates { get; } = new();
    public ObservableCollection<SemiconductorStationSkeletonItemPresentation> StationSkeletonItems { get; } = new();
    public ObservableCollection<SemiconductorStationSkeletonItemPresentation> ProcessBlockConnectionItems { get; } = new();
    public ObservableCollection<SemiconductorProcessBlockItemPresentation> ProcessBlockItems { get; } = new();
    public ObservableCollection<SemiconductorProcessBlockItemPresentation> VisibleProcessBlockItems { get; } = new();
    public ObservableCollection<SemiconductorManagedTimeoutAdjustmentItemPresentation> ProcessBlockTimeoutItems { get; } = new();
    public ObservableCollection<RecipeCheckpointTemplateItemPresentation> CheckpointTemplateItems { get; } = new();
    public ObservableCollection<LoadLockSetupOption> LoadLockDoorOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> LoadLockOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> LoadLockInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerAxisOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerWorkpieceOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerStageOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerCylinderOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionCameraOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionConveyorOptions { get; } = new();
    public ICommand OpenSequenceStepCommand => _openSequenceStepCommand;
    public ICommand AddSequenceStepCommand => _addSequenceStepCommand;
    public ICommand ValidateSimulationReadinessCommand => _validateSimulationReadinessCommand;
    public ICommand PreviewSequenceStepCommand => _previewSequenceStepCommand;
    public ICommand RunRecipeDryRunCommand => _runRecipeDryRunCommand;
    public ICommand OpenRecipeDryRunStepCommand => _openRecipeDryRunStepCommand;
    public ICommand PlayRecipeDryRunStepCommand => _playRecipeDryRunStepCommand;
    public ICommand PreviewStationSkeletonCommand => _previewStationSkeletonCommand;
    public ICommand ApplyStationSkeletonCommand => _applyStationSkeletonCommand;
    public ICommand CancelStationSkeletonCommand => _cancelStationSkeletonCommand;
    public ICommand ResetStationSetupCommand => _resetStationSetupCommand;
    public ICommand PreviewLoadLockSetupCommand => _previewLoadLockSetupCommand;
    public ICommand ApplyLoadLockSetupCommand => _applyLoadLockSetupCommand;
    public ICommand CancelLoadLockSetupCommand => _cancelLoadLockSetupCommand;
    public ICommand ResetLoadLockSetupCommand => _resetLoadLockSetupCommand;
    public ICommand PreviewWaferHandlerSetupCommand => _previewWaferHandlerSetupCommand;
    public ICommand ApplyWaferHandlerSetupCommand => _applyWaferHandlerSetupCommand;
    public ICommand CancelWaferHandlerSetupCommand => _cancelWaferHandlerSetupCommand;
    public ICommand ResetWaferHandlerSetupCommand => _resetWaferHandlerSetupCommand;
    public ICommand PreviewPrealignerSetupCommand => _previewPrealignerSetupCommand;
    public ICommand ApplyPrealignerSetupCommand => _applyPrealignerSetupCommand;
    public ICommand CancelPrealignerSetupCommand => _cancelPrealignerSetupCommand;
    public ICommand ResetPrealignerSetupCommand => _resetPrealignerSetupCommand;
    public ICommand PreviewInspectionHandoffSetupCommand => _previewInspectionHandoffSetupCommand;
    public ICommand ApplyInspectionHandoffSetupCommand => _applyInspectionHandoffSetupCommand;
    public ICommand CancelInspectionHandoffSetupCommand => _cancelInspectionHandoffSetupCommand;
    public ICommand ResetInspectionHandoffSetupCommand => _resetInspectionHandoffSetupCommand;
    public ICommand PreviewInspectionSortRouterSetupCommand => _previewInspectionSortRouterSetupCommand;
    public ICommand ApplyInspectionSortRouterSetupCommand => _applyInspectionSortRouterSetupCommand;
    public ICommand CancelInspectionSortRouterSetupCommand => _cancelInspectionSortRouterSetupCommand;
    public ICommand ResetInspectionSortRouterSetupCommand => _resetInspectionSortRouterSetupCommand;
    public ICommand PreviewOhtHandoffSetupCommand => _previewOhtHandoffSetupCommand;
    public ICommand ApplyOhtHandoffSetupCommand => _applyOhtHandoffSetupCommand;
    public ICommand CancelOhtHandoffSetupCommand => _cancelOhtHandoffSetupCommand;
    public ICommand ResetOhtHandoffSetupCommand => _resetOhtHandoffSetupCommand;
    public ICommand PreviewProcessBlockCommand => _previewProcessBlockCommand;
    public ICommand ApplyProcessBlockCommand => _applyProcessBlockCommand;
    public ICommand CancelProcessBlockCommand => _cancelProcessBlockCommand;
    public ICommand PreviewProcessBlockTimeoutsCommand => _previewProcessBlockTimeoutsCommand;
    public ICommand ApplyProcessBlockTimeoutsCommand => _applyProcessBlockTimeoutsCommand;
    public ICommand CancelProcessBlockTimeoutsCommand => _cancelProcessBlockTimeoutsCommand;
    public ICommand PreviewCheckpointTemplateCommand => _previewCheckpointTemplateCommand;
    public ICommand ApplyCheckpointTemplateCommand => _applyCheckpointTemplateCommand;
    public ICommand CancelCheckpointTemplateCommand => _cancelCheckpointTemplateCommand;

    public event EventHandler? ProcessBlockPreviewClosed;

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
            _validateSimulationReadinessCommand.RaiseCanExecuteChanged();
            _previewSequenceStepCommand.RaiseCanExecuteChanged();
            _runRecipeDryRunCommand.RaiseCanExecuteChanged();
            _openRecipeDryRunStepCommand.RaiseCanExecuteChanged();
            _playRecipeDryRunStepCommand.RaiseCanExecuteChanged();
            _previewStationSkeletonCommand.RaiseCanExecuteChanged();
            _applyStationSkeletonCommand.RaiseCanExecuteChanged();
            _resetStationSetupCommand.RaiseCanExecuteChanged();
            _previewLoadLockSetupCommand.RaiseCanExecuteChanged();
            _applyLoadLockSetupCommand.RaiseCanExecuteChanged();
            _resetLoadLockSetupCommand.RaiseCanExecuteChanged();
            _previewWaferHandlerSetupCommand.RaiseCanExecuteChanged();
            _applyWaferHandlerSetupCommand.RaiseCanExecuteChanged();
            _resetWaferHandlerSetupCommand.RaiseCanExecuteChanged();
            _previewPrealignerSetupCommand.RaiseCanExecuteChanged();
            _applyPrealignerSetupCommand.RaiseCanExecuteChanged();
            _resetPrealignerSetupCommand.RaiseCanExecuteChanged();
            _previewInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
            _applyInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
            _resetInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
            _previewInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
            _applyInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
            _resetInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
            _previewOhtHandoffSetupCommand.RaiseCanExecuteChanged();
            _applyOhtHandoffSetupCommand.RaiseCanExecuteChanged();
            _resetOhtHandoffSetupCommand.RaiseCanExecuteChanged();
            _previewProcessBlockCommand.RaiseCanExecuteChanged();
            _applyProcessBlockCommand.RaiseCanExecuteChanged();
            _previewProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
            _applyProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
            _previewCheckpointTemplateCommand.RaiseCanExecuteChanged();
            _applyCheckpointTemplateCommand.RaiseCanExecuteChanged();
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

    public SemiconductorProcessBlockItemPresentation? SelectedProcessBlockItem
    {
        get => _selectedProcessBlockItem;
        set => SetProperty(ref _selectedProcessBlockItem, value);
    }

    public bool HasRows => Rows.Count > 0;
    public int ComponentCount => Rows.Count;
    public int ConnectedCount => Rows.Count(row => row.IsConnected);
    public int SequenceUseCount => Rows.Sum(row => row.SequenceUseCount);
    public int RecipeStepCount => ResolveRecipeSequence()?.Steps.Count ?? 0;
    public int CheckpointStepCount => ResolveRecipeSequence()?.Steps.Count(step =>
        !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
        && !string.IsNullOrWhiteSpace(step.ExpectedState)) ?? 0;
    public string CheckpointCoverageText => Format(
        "Connections.CheckpointCoverageFormat",
        CheckpointStepCount,
        RecipeStepCount);
    public bool IsStationSkeletonPreviewVisible => _stationSkeletonPreview is not null;
    public int StationSkeletonProposedCount => _stationSkeletonPreview?.ProposedCount ?? 0;
    public string StationSkeletonSummaryText => Format(
        "Connections.StationSkeletonSummaryFormat",
        _stationSkeletonPreview?.ProposedCount ?? 0,
        _stationSkeletonPreview?.ExistingCount ?? 0,
        _stationSkeletonPreview?.UnavailableCount ?? 0);
    public string StationSkeletonApplyText => Format(
        StationSkeletonProposedCount > 0
            ? "Connections.StationSetupApplyWithRolesFormat"
            : "Connections.StationSetupApplyOnly",
        StationSkeletonProposedCount);
    public string StationName
    {
        get => _stationName;
        set => SetStationSetupText(ref _stationName, value, nameof(StationName));
    }
    public string WaferType
    {
        get => _waferType;
        set => SetStationSetupText(ref _waferType, value, nameof(WaferType));
    }
    public string AxisTravelText
    {
        get => _axisTravelText;
        set => SetStationSetupText(ref _axisTravelText, value, nameof(AxisTravelText));
    }
    public string TransportSpeedText
    {
        get => _transportSpeedText;
        set => SetStationSetupText(ref _transportSpeedText, value, nameof(TransportSpeedText));
    }
    public string EntrySensorPositionText
    {
        get => _entrySensorPositionText;
        set => SetStationSetupText(ref _entrySensorPositionText, value, nameof(EntrySensorPositionText));
    }
    public string ProcessSensorPositionText
    {
        get => _processSensorPositionText;
        set => SetStationSetupText(ref _processSensorPositionText, value, nameof(ProcessSensorPositionText));
    }
    public string CylinderTravelTimeText
    {
        get => _cylinderTravelTimeText;
        set => SetStationSetupText(ref _cylinderTravelTimeText, value, nameof(CylinderTravelTimeText));
    }
    public bool IsStationNameValid => !string.IsNullOrWhiteSpace(StationName);
    public bool IsWaferTypeValid => !string.IsNullOrWhiteSpace(WaferType);
    public bool IsAxisTravelValid => TryPositiveDouble(AxisTravelText, out _);
    public bool IsTransportSpeedValid => TryPositiveDouble(TransportSpeedText, out _);
    public bool IsEntrySensorPositionValid => TryFiniteDouble(EntrySensorPositionText, out var entry)
        && (!TryFiniteDouble(ProcessSensorPositionText, out var process) || entry < process);
    public bool IsProcessSensorPositionValid => TryFiniteDouble(ProcessSensorPositionText, out var process)
        && (!TryFiniteDouble(EntrySensorPositionText, out var entry) || entry < process);
    public bool IsCylinderTravelTimeValid => int.TryParse(
        CylinderTravelTimeText,
        NumberStyles.Integer,
        CultureInfo.CurrentCulture,
        out var timing) && timing > 0;
    public bool HasStationSetupValidationError => !TryCreateStationSetup(out _);
    public string StationSetupValidationText => HasStationSetupValidationError
        ? OpenVisionLanguageService.T("Connections.StationSetupValidationError")
        : _stationSetupWasInvalid
            ? OpenVisionLanguageService.T("Connections.StationSetupInvalidRestored")
            : OpenVisionLanguageService.T("Connections.StationSetupValidationReady");
    public bool IsLoadLockSetupVisible => _isLoadLockSetupVisible;
    public string? OuterDoorComponentId
    {
        get => _outerDoorComponentId;
        set => SetLoadLockSelection(ref _outerDoorComponentId, value, nameof(OuterDoorComponentId));
    }
    public string? InnerDoorComponentId
    {
        get => _innerDoorComponentId;
        set => SetLoadLockSelection(ref _innerDoorComponentId, value, nameof(InnerDoorComponentId));
    }
    public string? EvacuateCommandChannelId
    {
        get => _evacuateCommandChannelId;
        set => SetLoadLockSelection(ref _evacuateCommandChannelId, value, nameof(EvacuateCommandChannelId));
    }
    public string? VentCommandChannelId
    {
        get => _ventCommandChannelId;
        set => SetLoadLockSelection(ref _ventCommandChannelId, value, nameof(VentCommandChannelId));
    }
    public string? VacuumReadySensorChannelId
    {
        get => _vacuumReadySensorChannelId;
        set => SetLoadLockSelection(ref _vacuumReadySensorChannelId, value, nameof(VacuumReadySensorChannelId));
    }
    public string? AtmosphereReadySensorChannelId
    {
        get => _atmosphereReadySensorChannelId;
        set => SetLoadLockSelection(ref _atmosphereReadySensorChannelId, value, nameof(AtmosphereReadySensorChannelId));
    }
    public string PumpDownDurationText
    {
        get => _pumpDownDurationText;
        set => SetLoadLockText(ref _pumpDownDurationText, value, nameof(PumpDownDurationText));
    }
    public string VentDurationText
    {
        get => _ventDurationText;
        set => SetLoadLockText(ref _ventDurationText, value, nameof(VentDurationText));
    }
    public bool IsOuterDoorComponentValid => IsLoadLockDoor(OuterDoorComponentId)
        && !string.Equals(OuterDoorComponentId, InnerDoorComponentId, StringComparison.Ordinal);
    public bool IsInnerDoorComponentValid => IsLoadLockDoor(InnerDoorComponentId)
        && !string.Equals(OuterDoorComponentId, InnerDoorComponentId, StringComparison.Ordinal);
    public bool IsEvacuateCommandChannelValid => IsLoadLockChannel(EvacuateCommandChannelId, ChannelKind.DigitalOutput)
        && !string.Equals(EvacuateCommandChannelId, VentCommandChannelId, StringComparison.Ordinal);
    public bool IsVentCommandChannelValid => IsLoadLockChannel(VentCommandChannelId, ChannelKind.DigitalOutput)
        && !string.Equals(EvacuateCommandChannelId, VentCommandChannelId, StringComparison.Ordinal);
    public bool IsVacuumReadySensorChannelValid => IsLoadLockChannel(VacuumReadySensorChannelId, ChannelKind.DigitalInput)
        && !string.Equals(VacuumReadySensorChannelId, AtmosphereReadySensorChannelId, StringComparison.Ordinal);
    public bool IsAtmosphereReadySensorChannelValid => IsLoadLockChannel(AtmosphereReadySensorChannelId, ChannelKind.DigitalInput)
        && !string.Equals(VacuumReadySensorChannelId, AtmosphereReadySensorChannelId, StringComparison.Ordinal);
    public bool IsPumpDownDurationValid => IsLoadLockDurationValid(PumpDownDurationText);
    public bool IsVentDurationValid => IsLoadLockDurationValid(VentDurationText);
    public bool HasMultipleLoadLocks => _project?.Devices.Count(device => device.Kind == DeviceKind.LoadLock) > 1;
    public bool HasLoadLockSetupValidationError => !TryCreateLoadLockSetup(out _);
    public string LoadLockSetupValidationText => HasMultipleLoadLocks
        ? OpenVisionLanguageService.T("Connections.LoadLockSetupMultipleError")
        : HasLoadLockSetupValidationError
            ? OpenVisionLanguageService.T("Connections.LoadLockSetupValidationError")
            : OpenVisionLanguageService.T("Connections.LoadLockSetupValidationReady");
    public bool IsWaferHandlerSetupVisible => _isWaferHandlerSetupVisible;
    public bool IsPrealignerSetupVisible => _isPrealignerSetupVisible;
    public string? WaferHandlerHorizontalAxisId { get => _waferHandlerHorizontalAxisId; set => SetSemanticSelection(ref _waferHandlerHorizontalAxisId, value, nameof(WaferHandlerHorizontalAxisId)); }
    public string? WaferHandlerVerticalAxisId { get => _waferHandlerVerticalAxisId; set => SetSemanticSelection(ref _waferHandlerVerticalAxisId, value, nameof(WaferHandlerVerticalAxisId)); }
    public string? WaferHandlerWorkpieceComponentId { get => _waferHandlerWorkpieceComponentId; set => SetSemanticSelection(ref _waferHandlerWorkpieceComponentId, value, nameof(WaferHandlerWorkpieceComponentId)); }
    public string? WaferHandlerSourcePresentSensorChannelId { get => _waferHandlerSourcePresentSensorChannelId; set => SetSemanticSelection(ref _waferHandlerSourcePresentSensorChannelId, value, nameof(WaferHandlerSourcePresentSensorChannelId)); }
    public string? WaferHandlerGateOpenSensorChannelId { get => _waferHandlerGateOpenSensorChannelId; set => SetSemanticSelection(ref _waferHandlerGateOpenSensorChannelId, value, nameof(WaferHandlerGateOpenSensorChannelId)); }
    public string? WaferHandlerPickCommandChannelId { get => _waferHandlerPickCommandChannelId; set => SetSemanticSelection(ref _waferHandlerPickCommandChannelId, value, nameof(WaferHandlerPickCommandChannelId)); }
    public string? WaferHandlerPlaceCommandChannelId { get => _waferHandlerPlaceCommandChannelId; set => SetSemanticSelection(ref _waferHandlerPlaceCommandChannelId, value, nameof(WaferHandlerPlaceCommandChannelId)); }
    public string? WaferHandlerHoldingFeedbackChannelId { get => _waferHandlerHoldingFeedbackChannelId; set => SetSemanticSelection(ref _waferHandlerHoldingFeedbackChannelId, value, nameof(WaferHandlerHoldingFeedbackChannelId)); }
    public string? WaferHandlerPlacedFeedbackChannelId { get => _waferHandlerPlacedFeedbackChannelId; set => SetSemanticSelection(ref _waferHandlerPlacedFeedbackChannelId, value, nameof(WaferHandlerPlacedFeedbackChannelId)); }
    public string WaferHandlerPickHorizontalText { get => _waferHandlerPickHorizontalText; set => SetSemanticText(ref _waferHandlerPickHorizontalText, value, nameof(WaferHandlerPickHorizontalText)); }
    public string WaferHandlerPickVerticalText { get => _waferHandlerPickVerticalText; set => SetSemanticText(ref _waferHandlerPickVerticalText, value, nameof(WaferHandlerPickVerticalText)); }
    public string WaferHandlerPlaceHorizontalText { get => _waferHandlerPlaceHorizontalText; set => SetSemanticText(ref _waferHandlerPlaceHorizontalText, value, nameof(WaferHandlerPlaceHorizontalText)); }
    public string WaferHandlerPlaceVerticalText { get => _waferHandlerPlaceVerticalText; set => SetSemanticText(ref _waferHandlerPlaceVerticalText, value, nameof(WaferHandlerPlaceVerticalText)); }
    public bool HasMultipleWaferHandlers => _project?.Devices.Count(device => device.Kind == DeviceKind.Handler) > 1;
    public bool IsWaferHandlerHorizontalAxisValid => IsLinearAxis(WaferHandlerHorizontalAxisId);
    public bool IsWaferHandlerVerticalAxisValid => IsLinearAxis(WaferHandlerVerticalAxisId) && !string.Equals(WaferHandlerHorizontalAxisId, WaferHandlerVerticalAxisId, StringComparison.Ordinal);
    public bool IsWaferHandlerWorkpieceValid => IsLayoutComponent(WaferHandlerWorkpieceComponentId, LayoutComponentKind.Workpiece);
    public bool IsWaferHandlerSourcePresentValid => IsSemanticChannel(WaferHandlerSourcePresentSensorChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerGateOpenValid => IsSemanticChannel(WaferHandlerGateOpenSensorChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerPickCommandValid => IsSemanticChannel(WaferHandlerPickCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsWaferHandlerPlaceCommandValid => IsSemanticChannel(WaferHandlerPlaceCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsWaferHandlerHoldingFeedbackValid => IsSemanticChannel(WaferHandlerHoldingFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerPlacedFeedbackValid => IsSemanticChannel(WaferHandlerPlacedFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerPickHorizontalValid => IsAxisPosition(WaferHandlerHorizontalAxisId, WaferHandlerPickHorizontalText);
    public bool IsWaferHandlerPlaceHorizontalValid => IsAxisPosition(WaferHandlerHorizontalAxisId, WaferHandlerPlaceHorizontalText);
    public bool IsWaferHandlerPickVerticalValid => IsAxisPosition(WaferHandlerVerticalAxisId, WaferHandlerPickVerticalText);
    public bool IsWaferHandlerPlaceVerticalValid => IsAxisPosition(WaferHandlerVerticalAxisId, WaferHandlerPlaceVerticalText);
    public bool HasWaferHandlerSetupValidationError => !TryCreateWaferHandlerSetup(out _);
    public string WaferHandlerSetupValidationText => HasMultipleWaferHandlers
        ? OpenVisionLanguageService.T("Connections.WaferHandlerSetupMultipleError")
        : HasWaferHandlerSetupValidationError
            ? OpenVisionLanguageService.T("Connections.WaferHandlerSetupValidationError")
            : OpenVisionLanguageService.T("Connections.WaferHandlerSetupValidationReady");
    public string? PrealignerRotaryStageComponentId { get => _prealignerRotaryStageComponentId; set => SetSemanticSelection(ref _prealignerRotaryStageComponentId, value, nameof(PrealignerRotaryStageComponentId)); }
    public string? PrealignerClampCylinderComponentId { get => _prealignerClampCylinderComponentId; set => SetSemanticSelection(ref _prealignerClampCylinderComponentId, value, nameof(PrealignerClampCylinderComponentId)); }
    public string? PrealignerWaferPresentSensorChannelId { get => _prealignerWaferPresentSensorChannelId; set => SetSemanticSelection(ref _prealignerWaferPresentSensorChannelId, value, nameof(PrealignerWaferPresentSensorChannelId)); }
    public string? PrealignerAlignmentAcceptedCommandChannelId { get => _prealignerAlignmentAcceptedCommandChannelId; set => SetSemanticSelection(ref _prealignerAlignmentAcceptedCommandChannelId, value, nameof(PrealignerAlignmentAcceptedCommandChannelId)); }
    public string? PrealignerAlignmentReadyFeedbackChannelId { get => _prealignerAlignmentReadyFeedbackChannelId; set => SetSemanticSelection(ref _prealignerAlignmentReadyFeedbackChannelId, value, nameof(PrealignerAlignmentReadyFeedbackChannelId)); }
    public string? PrealignerAlignmentCompleteFeedbackChannelId { get => _prealignerAlignmentCompleteFeedbackChannelId; set => SetSemanticSelection(ref _prealignerAlignmentCompleteFeedbackChannelId, value, nameof(PrealignerAlignmentCompleteFeedbackChannelId)); }
    public string PrealignerAlignmentTargetText { get => _prealignerAlignmentTargetText; set => SetSemanticText(ref _prealignerAlignmentTargetText, value, nameof(PrealignerAlignmentTargetText)); }
    public string PrealignerAlignmentToleranceText { get => _prealignerAlignmentToleranceText; set => SetSemanticText(ref _prealignerAlignmentToleranceText, value, nameof(PrealignerAlignmentToleranceText)); }
    public bool HasMultiplePrealigners => _project?.Devices.Count(device => device.Kind == DeviceKind.Prealigner) > 1;
    public bool IsPrealignerRotaryStageValid => TryGetPrealignerStage(PrealignerRotaryStageComponentId, out _);
    public bool IsPrealignerClampCylinderValid => IsLayoutComponent(PrealignerClampCylinderComponentId, LayoutComponentKind.PneumaticCylinder);
    public bool IsPrealignerWaferPresentValid => IsSemanticChannel(PrealignerWaferPresentSensorChannelId, ChannelKind.DigitalInput);
    public bool IsPrealignerAlignmentAcceptedValid => IsSemanticChannel(PrealignerAlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsPrealignerAlignmentReadyValid => IsSemanticChannel(PrealignerAlignmentReadyFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsPrealignerAlignmentCompleteValid => IsSemanticChannel(PrealignerAlignmentCompleteFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsPrealignerAlignmentTargetValid => IsRotaryPosition(PrealignerRotaryStageComponentId, PrealignerAlignmentTargetText);
    public bool IsPrealignerAlignmentToleranceValid => TryPositiveDouble(PrealignerAlignmentToleranceText, out _);
    public bool HasPrealignerSetupValidationError => !TryCreatePrealignerSetup(out _);
    public string PrealignerSetupValidationText => HasMultiplePrealigners
        ? OpenVisionLanguageService.T("Connections.PrealignerSetupMultipleError")
        : HasPrealignerSetupValidationError
            ? OpenVisionLanguageService.T("Connections.PrealignerSetupValidationError")
            : OpenVisionLanguageService.T("Connections.PrealignerSetupValidationReady");
    public bool IsInspectionHandoffSetupVisible => _isInspectionHandoffSetupVisible;
    public bool IsInspectionSortRouterSetupVisible => _isInspectionSortRouterSetupVisible;
    public bool IsOhtHandoffSetupVisible => _isOhtHandoffSetupVisible;
    public string? InspectionHandoffCameraId { get => _inspectionHandoffCameraId; set => SetSemanticSelection(ref _inspectionHandoffCameraId, value, nameof(InspectionHandoffCameraId)); }
    public string? InspectionHandoffPositionSensorChannelId { get => _inspectionHandoffPositionSensorChannelId; set => SetSemanticSelection(ref _inspectionHandoffPositionSensorChannelId, value, nameof(InspectionHandoffPositionSensorChannelId)); }
    public string? InspectionHandoffAcceptedChannelId { get => _inspectionHandoffAcceptedChannelId; set => SetSemanticSelection(ref _inspectionHandoffAcceptedChannelId, value, nameof(InspectionHandoffAcceptedChannelId)); }
    public string? InspectionHandoffReadyChannelId { get => _inspectionHandoffReadyChannelId; set => SetSemanticSelection(ref _inspectionHandoffReadyChannelId, value, nameof(InspectionHandoffReadyChannelId)); }
    public string? InspectionHandoffCompleteChannelId { get => _inspectionHandoffCompleteChannelId; set => SetSemanticSelection(ref _inspectionHandoffCompleteChannelId, value, nameof(InspectionHandoffCompleteChannelId)); }
    public bool IsInspectionHandoffCameraValid => IsDeviceCamera(InspectionHandoffCameraId);
    public bool IsInspectionHandoffPositionValid => IsSemanticChannel(InspectionHandoffPositionSensorChannelId, ChannelKind.DigitalInput);
    public bool IsInspectionHandoffAcceptedValid => IsSemanticChannel(InspectionHandoffAcceptedChannelId, ChannelKind.DigitalOutput);
    public bool IsInspectionHandoffReadyValid => IsSemanticChannel(InspectionHandoffReadyChannelId, ChannelKind.DigitalInput);
    public bool IsInspectionHandoffCompleteValid => IsSemanticChannel(InspectionHandoffCompleteChannelId, ChannelKind.DigitalInput);
    public bool HasMultipleInspectionHandoffs => _project?.Devices.Count(device => device.Kind == DeviceKind.Inspection) > 1;
    public bool HasInspectionHandoffSetupValidationError => !TryCreateInspectionHandoffSetup(out _);
    public string InspectionHandoffSetupValidationText => HasMultipleInspectionHandoffs
        ? OpenVisionLanguageService.T("Connections.InspectionHandoffSetupMultipleError")
        : HasInspectionHandoffSetupValidationError
            ? OpenVisionLanguageService.T("Connections.InspectionHandoffSetupValidationError")
            : OpenVisionLanguageService.T("Connections.InspectionHandoffSetupValidationReady");
    public string? InspectionSortCameraId { get => _inspectionSortCameraId; set => SetSemanticSelection(ref _inspectionSortCameraId, value, nameof(InspectionSortCameraId)); }
    public string? InspectionSortPassConveyorId { get => _inspectionSortPassConveyorId; set => SetSemanticSelection(ref _inspectionSortPassConveyorId, value, nameof(InspectionSortPassConveyorId)); }
    public string? InspectionSortNgConveyorId { get => _inspectionSortNgConveyorId; set => SetSemanticSelection(ref _inspectionSortNgConveyorId, value, nameof(InspectionSortNgConveyorId)); }
    public string? InspectionSortPassFeedbackChannelId { get => _inspectionSortPassFeedbackChannelId; set => SetSemanticSelection(ref _inspectionSortPassFeedbackChannelId, value, nameof(InspectionSortPassFeedbackChannelId)); }
    public string? InspectionSortNgFeedbackChannelId { get => _inspectionSortNgFeedbackChannelId; set => SetSemanticSelection(ref _inspectionSortNgFeedbackChannelId, value, nameof(InspectionSortNgFeedbackChannelId)); }
    public bool IsInspectionSortCameraValid => IsDeviceCamera(InspectionSortCameraId);
    public bool IsInspectionSortPassConveyorValid => IsLayoutComponent(InspectionSortPassConveyorId, LayoutComponentKind.Conveyor);
    public bool IsInspectionSortNgConveyorValid => IsLayoutComponent(InspectionSortNgConveyorId, LayoutComponentKind.Conveyor) && !string.Equals(InspectionSortPassConveyorId, InspectionSortNgConveyorId, StringComparison.Ordinal);
    public bool IsInspectionSortPassFeedbackValid => IsSemanticChannel(InspectionSortPassFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsInspectionSortNgFeedbackValid => IsSemanticChannel(InspectionSortNgFeedbackChannelId, ChannelKind.DigitalInput) && !string.Equals(InspectionSortPassFeedbackChannelId, InspectionSortNgFeedbackChannelId, StringComparison.Ordinal);
    public bool HasMultipleInspectionSortRouters => _project?.Devices.Count(device => device.Kind == DeviceKind.Sorter) > 1;
    public bool HasInspectionSortRouterSetupValidationError => !TryCreateInspectionSortRouterSetup(out _);
    public string InspectionSortRouterSetupValidationText => HasMultipleInspectionSortRouters
        ? OpenVisionLanguageService.T("Connections.InspectionSortSetupMultipleError")
        : HasInspectionSortRouterSetupValidationError
            ? OpenVisionLanguageService.T("Connections.InspectionSortSetupValidationError")
            : OpenVisionLanguageService.T("Connections.InspectionSortSetupValidationReady");
    public string? OhtTransportConveyorId { get => _ohtTransportConveyorId; set => SetSemanticSelection(ref _ohtTransportConveyorId, value, nameof(OhtTransportConveyorId)); }
    public string? OhtRouteAvailableChannelId { get => _ohtRouteAvailableChannelId; set => SetSemanticSelection(ref _ohtRouteAvailableChannelId, value, nameof(OhtRouteAvailableChannelId)); }
    public string? OhtVehicleDockedChannelId { get => _ohtVehicleDockedChannelId; set => SetSemanticSelection(ref _ohtVehicleDockedChannelId, value, nameof(OhtVehicleDockedChannelId)); }
    public string? OhtLoadPortReadyChannelId { get => _ohtLoadPortReadyChannelId; set => SetSemanticSelection(ref _ohtLoadPortReadyChannelId, value, nameof(OhtLoadPortReadyChannelId)); }
    public string? OhtCarrierReceivedChannelId { get => _ohtCarrierReceivedChannelId; set => SetSemanticSelection(ref _ohtCarrierReceivedChannelId, value, nameof(OhtCarrierReceivedChannelId)); }
    public string? OhtHandoffReadyChannelId { get => _ohtHandoffReadyChannelId; set => SetSemanticSelection(ref _ohtHandoffReadyChannelId, value, nameof(OhtHandoffReadyChannelId)); }
    public string? OhtCarrierTransferredChannelId { get => _ohtCarrierTransferredChannelId; set => SetSemanticSelection(ref _ohtCarrierTransferredChannelId, value, nameof(OhtCarrierTransferredChannelId)); }
    public bool IsOhtTransportConveyorValid => IsLayoutComponent(OhtTransportConveyorId, LayoutComponentKind.Conveyor);
    public bool IsOhtRouteAvailableValid => IsSemanticChannel(OhtRouteAvailableChannelId, ChannelKind.DigitalInput);
    public bool IsOhtVehicleDockedValid => IsSemanticChannel(OhtVehicleDockedChannelId, ChannelKind.DigitalInput);
    public bool IsOhtLoadPortReadyValid => IsSemanticChannel(OhtLoadPortReadyChannelId, ChannelKind.DigitalInput);
    public bool IsOhtCarrierReceivedValid => IsSemanticChannel(OhtCarrierReceivedChannelId, ChannelKind.DigitalInput);
    public bool IsOhtHandoffReadyValid => IsSemanticChannel(OhtHandoffReadyChannelId, ChannelKind.DigitalInput);
    public bool IsOhtCarrierTransferredValid => IsSemanticChannel(OhtCarrierTransferredChannelId, ChannelKind.DigitalInput);
    public bool HasMultipleOhtHandoffs => _project?.Devices.Count(device => device.Kind == DeviceKind.Oht) > 1;
    public bool HasOhtHandoffSetupValidationError => !TryCreateOhtHandoffSetup(out _);
    public string OhtHandoffSetupValidationText => HasMultipleOhtHandoffs
        ? OpenVisionLanguageService.T("Connections.OhtSetupMultipleError")
        : HasOhtHandoffSetupValidationError
            ? OpenVisionLanguageService.T("Connections.OhtSetupValidationError")
            : OpenVisionLanguageService.T("Connections.OhtSetupValidationReady");
    public bool IsProcessBlockPreviewVisible => _processBlockPreview is not null;
    public bool IsLoadBlockSelected
    {
        get => _isLoadBlockSelected;
        set => SetProcessBlockSelection(ref _isLoadBlockSelected, value, nameof(IsLoadBlockSelected));
    }
    public bool IsAlignBlockSelected
    {
        get => _isAlignBlockSelected;
        set => SetProcessBlockSelection(ref _isAlignBlockSelected, value, nameof(IsAlignBlockSelected));
    }
    public bool IsProcessBlockSelected
    {
        get => _isProcessBlockSelected;
        set => SetProcessBlockSelection(ref _isProcessBlockSelected, value, nameof(IsProcessBlockSelected));
    }
    public bool IsInspectBlockSelected
    {
        get => _isInspectBlockSelected;
        set => SetProcessBlockSelection(ref _isInspectBlockSelected, value, nameof(IsInspectBlockSelected));
    }
    public bool IsUnloadBlockSelected
    {
        get => _isUnloadBlockSelected;
        set => SetProcessBlockSelection(ref _isUnloadBlockSelected, value, nameof(IsUnloadBlockSelected));
    }
    public int SelectedProcessBlockCount => SelectedProcessBlockKinds().Count;
    public int ExistingProcessBlockCount => _processBlockPreview?.ExistingKinds.Count ?? 0;
    public bool HasProcessBlockSelection => SelectedProcessBlockCount > 0;
    public bool IsProcessBlockFilterAll
    {
        get => _processBlockItemFilter == ProcessBlockItemFilter.All;
        set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.All); }
    }
    public bool IsProcessBlockFilterCustomized
    {
        get => _processBlockItemFilter == ProcessBlockItemFilter.Customized;
        set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.Customized); }
    }
    public bool IsProcessBlockFilterRemoval
    {
        get => _processBlockItemFilter == ProcessBlockItemFilter.Removal;
        set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.Removal); }
    }
    public bool IsProcessBlockFilterConflict
    {
        get => _processBlockItemFilter == ProcessBlockItemFilter.Conflict;
        set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.Conflict); }
    }
    public string ProcessBlockFilterAllText => FormatProcessBlockFilter(
        "Connections.ProcessBlockFilterAll",
        ProcessBlockItems.Count);
    public string ProcessBlockFilterCustomizedText => FormatProcessBlockFilter(
        "Connections.ProcessBlockStepCustomized",
        ProcessBlockItems.Count(item => item.IsCustomized));
    public string ProcessBlockFilterRemovalText => FormatProcessBlockFilter(
        "Connections.ProcessBlockStepProposedRemoval",
        ProcessBlockItems.Count(item => item.IsProposedRemoval));
    public string ProcessBlockFilterConflictText => FormatProcessBlockFilter(
        "Connections.ProcessBlockFilterConflict",
        ProcessBlockItems.Count(item => item.IsUnavailable));
    public bool HasVisibleProcessBlockItems => VisibleProcessBlockItems.Count > 0;
    public int CompatibleProcessBlockTimeoutCount =>
        VisibleProcessBlockItems.Count(item => item.CanAdjustTimeout);
    public string ProcessBlockTimeoutText
    {
        get => _processBlockTimeoutText;
        set
        {
            if (!SetProperty(ref _processBlockTimeoutText, value))
            {
                return;
            }
            ClearProcessBlockTimeoutPreview();
            OnPropertyChanged(nameof(IsProcessBlockTimeoutValid));
            OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
            _previewProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsProcessBlockTimeoutValid => int.TryParse(
        ProcessBlockTimeoutText,
        NumberStyles.Integer,
        CultureInfo.CurrentCulture,
        out var timeout) && timeout >= 0;
    public bool IsProcessBlockTimeoutPreviewVisible => _processBlockTimeoutPreview is not null;
    public string ProcessBlockTimeoutScopeText => Format(
        "Connections.ProcessBlockTimeoutScopeFormat",
        CompatibleProcessBlockTimeoutCount,
        VisibleProcessBlockItems.Count);
    public string ProcessBlockTimeoutValidationText => OpenVisionLanguageService.T(
        !IsProcessBlockTimeoutValid
            ? "Connections.ProcessBlockTimeoutInvalid"
            : CompatibleProcessBlockTimeoutCount == 0
                ? "Connections.ProcessBlockTimeoutNoCompatible"
                : _processBlockTimeoutPreview is { CanApply: true }
                    ? "Connections.ProcessBlockTimeoutReady"
                    : _processBlockTimeoutPreview is not null
                        ? "Connections.ProcessBlockTimeoutNoChanges"
                        : "Connections.ProcessBlockTimeoutHint");
    public string ProcessBlockTimeoutApplyText => Format(
        "Connections.ProcessBlockTimeoutApplyFormat",
        _processBlockTimeoutPreview?.ChangedCount ?? 0);
    public string ProcessBlockKindText => Format(
        "Connections.ProcessBlockEditSelectionFormat",
        SelectedProcessBlockCount,
        ExistingProcessBlockCount);
    public string ProcessBlockSummaryText => (_processBlockPreview?.CustomizedStepCount ?? 0) > 0
        ? Format(
            "Connections.ProcessBlockEditSummaryWithCustomizedFormat",
            _processBlockPreview?.ProposedConnectionCount ?? 0,
            _processBlockPreview?.ProposedStepCount ?? 0,
            _processBlockPreview?.RemovedStepCount ?? 0,
            _processBlockPreview?.ExistingStepCount ?? 0,
            _processBlockPreview?.CustomizedStepCount ?? 0,
            _processBlockPreview?.UnavailableCount ?? 0)
        : Format(
            "Connections.ProcessBlockEditSummaryFormat",
            _processBlockPreview?.ProposedConnectionCount ?? 0,
            _processBlockPreview?.ProposedStepCount ?? 0,
            _processBlockPreview?.RemovedStepCount ?? 0,
            _processBlockPreview?.ExistingStepCount ?? 0,
            _processBlockPreview?.UnavailableCount ?? 0);
    public string ProcessBlockApplyText => Format(
        "Connections.ProcessBlockEditApplyFormat",
        _processBlockPreview?.ProposedConnectionCount ?? 0,
        _processBlockPreview?.ProposedStepCount ?? 0,
        _processBlockPreview?.RemovedStepCount ?? 0);
    public bool HasProcessBlockPlanError => _processBlockPreview is
        { CanApply: false, UnavailableCount: > 0 }
        || (_processBlockPreview is { ExistingKinds.Count: 0 } && !HasProcessBlockSelection);
    public string ProcessBlockValidationText => OpenVisionLanguageService.T(_processBlockPreview switch
    {
        { UnavailableCount: > 0 } => "Connections.ProcessBlockEditPlanUnavailable",
        { CanApply: true } => "Connections.ProcessBlockEditPlanReady",
        { ExistingKinds.Count: 0 } when !HasProcessBlockSelection => "Connections.ProcessBlockEditPlanEmpty",
        _ => "Connections.ProcessBlockEditPlanNoChanges"
    });
    public bool IsCheckpointTemplatePreviewVisible => _checkpointTemplatePreview is not null;
    public int CheckpointTemplateProposedCount => _checkpointTemplatePreview?.ProposedCount ?? 0;
    public string CheckpointTemplateSummaryText => Format(
        "Connections.CheckpointTemplateSummaryFormat",
        _checkpointTemplatePreview?.ProposedCount ?? 0,
        _checkpointTemplatePreview?.ExistingCount ?? 0,
        _checkpointTemplatePreview?.UnavailableCount ?? 0);
    public string CheckpointTemplateApplyText => Format(
        "Connections.CheckpointTemplateApplyFormat",
        CheckpointTemplateProposedCount);
    public bool HasValidationErrors => Rows.Any(row => !row.IsValid);
    public string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Connections.SummaryFormat"),
        ComponentCount,
        ConnectedCount,
        SequenceUseCount);
    public string ValidationSummaryText => OpenVisionLanguageService.T(
        HasValidationErrors ? "Connections.ValidationErrors" : "Connections.ValidationPassed");
    public bool? ReadinessPassed => _readinessPassed;
    public string ReadinessStatusText => OpenVisionLanguageService.T(
        _readinessPassed switch
        {
            true => "Connections.ReadinessPassed",
            false => "Connections.ReadinessFailed",
            null => "Connections.ReadinessNotChecked"
        });
    public string ReadinessDetailText => _readinessPassed switch
    {
        true => OpenVisionLanguageService.T("Connections.ReadinessPassedDetail"),
        false => Format("Connections.ReadinessFailedDetail", _readinessError ?? string.Empty),
        null => OpenVisionLanguageService.T("Connections.ReadinessNotCheckedDetail")
    };
    public bool IsRecipeDryRunRunning
    {
        get => _isRecipeDryRunRunning;
        private set
        {
            if (SetProperty(ref _isRecipeDryRunRunning, value))
            {
                _runRecipeDryRunCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public RecipeDryRunResult? RecipeDryRunResult
    {
        get => _recipeDryRunResult;
        private set => SetProperty(ref _recipeDryRunResult, value);
    }
    public bool HasRecipeDryRunResult => RecipeDryRunResult is not null;
    public bool RecipeDryRunPassed => RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed;
    public bool RecipeDryRunWarning => RecipeDryRunResult is { Outcome: not RecipeDryRunOutcome.Completed };
    public bool HasRecipeDryRunIssue => RecipeDryRunResult?.FirstIssue is not null
        || RecipeDryRunResult?.FirstCheckpointMismatch is not null;
    public RecipeDryRunStepPresentation? SelectedRecipeDryRunStep
    {
        get => _selectedRecipeDryRunStep;
        set
        {
            if (!SetProperty(ref _selectedRecipeDryRunStep, value))
            {
                return;
            }

            SelectedRow = value?.ComponentId is { } componentId
                ? Rows.FirstOrDefault(row => string.Equals(row.ComponentId, componentId, StringComparison.Ordinal))
                : null;
        }
    }
    public string RecipeDryRunStatusText
    {
        get => _recipeDryRunStatusText;
        private set => SetProperty(ref _recipeDryRunStatusText, value);
    }
    public string RecipeDryRunDetailText
    {
        get => _recipeDryRunDetailText;
        private set => SetProperty(ref _recipeDryRunDetailText, value);
    }
    public string RecipeDryRunIssueText
    {
        get => _recipeDryRunIssueText;
        private set => SetProperty(ref _recipeDryRunIssueText, value);
    }

    public void Load(MachineProjectDocument project, string? selectedComponentId = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _definitionRevision++;
        _readinessPassed = null;
        _readinessError = null;
        ClearRecipeDryRun();
        ClearStationSkeletonPreview();
        ClearLoadLockSetup();
        ClearWaferHandlerSetup();
        ClearPrealignerSetup();
        ClearInspectionHandoffSetup();
        ClearInspectionSortRouterSetup();
        ClearOhtHandoffSetup();
        ClearProcessBlockPreview();
        ClearCheckpointTemplatePreview();
        var layout = ResolveActiveLayout(project);
        var validation = new MachineProjectLayoutValidator().Validate(project);
        var componentNames = layout?.Components.ToDictionary(
            component => component.Id,
            component => DisplayName(component.Name, component.Id),
            StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);

        Rows.Clear();
        if (layout is not null)
        {
            foreach (var component in layout.Components
                         .OrderBy(component => component.ZIndex)
                         .ThenBy(component => component.Id, StringComparer.Ordinal))
            {
                Rows.Add(CreateRow(project, component, componentNames, validation));
            }
        }

        SynchronizeSelection(selectedComponentId);
        RaiseSummaryChanged();
        RaiseReadinessChanged();
    }

    public void RefreshDefinitionPreservingProcessBlockPlan(string? selectedComponentId = null)
    {
        var processBlockKinds = _processBlockPreview?.Kinds.ToArray();
        var selectedStepId = SelectedProcessBlockItem?.StepId;
        _isPreservingProcessBlockPlan = processBlockKinds is not null;
        try
        {
            Load(_project ?? throw new InvalidOperationException("No project is loaded."), selectedComponentId);
        }
        finally
        {
            _isPreservingProcessBlockPlan = false;
        }
        if (processBlockKinds is null)
        {
            return;
        }

        SetProcessBlockSelections(processBlockKinds);
        PreviewProcessBlockPlan();
        SelectProcessBlockStep(selectedStepId);
    }

    public SemiconductorProcessBlockItemPresentation? SelectProcessBlockStep(string? stepId)
    {
        var item = ProcessBlockItems.FirstOrDefault(candidate => string.Equals(
            candidate.StepId,
            stepId,
            StringComparison.Ordinal));
        SelectedProcessBlockItem = null;
        SelectedProcessBlockItem = item;
        return item;
    }

    public void SynchronizeSelection(string? componentId)
    {
        _isSynchronizingSelection = true;
        try
        {
            SelectedRow = Rows.FirstOrDefault(row =>
                string.Equals(row.ComponentId, componentId, StringComparison.Ordinal));
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    public void RefreshLocalization()
    {
        if (_project is not null)
        {
            var hadStationSkeletonPreview = IsStationSkeletonPreviewVisible;
            var stationSetupDraft = hadStationSkeletonPreview
                ? new[]
                {
                    StationName,
                    WaferType,
                    AxisTravelText,
                    TransportSpeedText,
                    EntrySensorPositionText,
                    ProcessSensorPositionText,
                    CylinderTravelTimeText
                }
                : null;
            var hadLoadLockSetup = IsLoadLockSetupVisible;
            var loadLockDraft = hadLoadLockSetup
                ? new[]
                {
                    OuterDoorComponentId,
                    InnerDoorComponentId,
                    EvacuateCommandChannelId,
                    VentCommandChannelId,
                    VacuumReadySensorChannelId,
                    AtmosphereReadySensorChannelId,
                    PumpDownDurationText,
                    VentDurationText
                }
                : null;
            var hadWaferHandlerSetup = IsWaferHandlerSetupVisible;
            var waferHandlerDraft = hadWaferHandlerSetup ? CaptureWaferHandlerDraft() : null;
            var hadPrealignerSetup = IsPrealignerSetupVisible;
            var prealignerDraft = hadPrealignerSetup ? CapturePrealignerDraft() : null;
            var hadCheckpointTemplatePreview = IsCheckpointTemplatePreviewVisible;
            var processBlockKinds = _processBlockPreview?.Kinds.ToArray();
            var readinessPassed = _readinessPassed;
            var readinessError = _readinessError;
            _isPreservingProcessBlockPlan = processBlockKinds is not null;
            try
            {
                Load(_project, SelectedRow?.ComponentId);
            }
            finally
            {
                _isPreservingProcessBlockPlan = false;
            }
            if (hadStationSkeletonPreview)
            {
                PreviewStationSkeleton();
                StationName = stationSetupDraft![0];
                WaferType = stationSetupDraft[1];
                AxisTravelText = stationSetupDraft[2];
                TransportSpeedText = stationSetupDraft[3];
                EntrySensorPositionText = stationSetupDraft[4];
                ProcessSensorPositionText = stationSetupDraft[5];
                CylinderTravelTimeText = stationSetupDraft[6];
            }
            if (hadLoadLockSetup)
            {
                PreviewLoadLockSetup();
                OuterDoorComponentId = loadLockDraft![0];
                InnerDoorComponentId = loadLockDraft[1];
                EvacuateCommandChannelId = loadLockDraft[2];
                VentCommandChannelId = loadLockDraft[3];
                VacuumReadySensorChannelId = loadLockDraft[4];
                AtmosphereReadySensorChannelId = loadLockDraft[5];
                PumpDownDurationText = loadLockDraft[6] ?? string.Empty;
                VentDurationText = loadLockDraft[7] ?? string.Empty;
            }
            if (hadWaferHandlerSetup)
            {
                PreviewWaferHandlerSetup();
                RestoreWaferHandlerDraft(waferHandlerDraft!);
            }
            if (hadPrealignerSetup)
            {
                PreviewPrealignerSetup();
                RestorePrealignerDraft(prealignerDraft!);
            }
            if (hadCheckpointTemplatePreview)
            {
                PreviewCheckpointTemplate();
            }
            if (processBlockKinds is not null)
            {
                SetProcessBlockSelections(processBlockKinds);
                PreviewProcessBlockPlan();
            }
            _readinessPassed = readinessPassed;
            _readinessError = readinessError;
            RaiseReadinessChanged();
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

    private static bool CanOpenSequenceStep(object? parameter) => parameter switch
    {
        RecipeConnectionRowViewModel { HasSequenceUse: true } => true,
        SemiconductorProcessBlockItemPresentation { CanOpenSequenceStep: true } => true,
        _ => false
    };

    private void OpenRecipeDryRunStep(object? parameter)
    {
        if (parameter is RecipeDryRunStepPresentation step)
        {
            SelectedRecipeDryRunStep = step;
            _openSequenceStep(step.SequenceId, step.StepId);
        }
    }

    private void PlayRecipeDryRunStep(object? parameter)
    {
        if (parameter is RecipeDryRunStepPresentation step)
        {
            SelectedRecipeDryRunStep = step;
            _playRecipeDryRunStep(step);
        }
    }

    private void AddSequenceStep(object? parameter)
    {
        if (parameter is RecipeConnectionRowViewModel { SequenceTargetId: { } targetId })
        {
            _addSequenceStep(targetId);
        }
    }

    private void ValidateSimulationReadiness()
    {
        _readinessError = _validateSimulationReadiness();
        _readinessPassed = _readinessError is null;
        RaiseReadinessChanged();
    }

    private void PreviewStationSkeleton()
    {
        if (_project is null)
        {
            return;
        }

        ClearProcessBlockPreview();
        ClearCheckpointTemplatePreview();
        ClearLoadLockSetup();

        var setup = _stationSkeletonTemplate.ResolveSetup(_project);
        _stationSetupWasInvalid = _project.SemiconductorStationSetup is not null
            && !SemiconductorStationSkeletonTemplate.IsValidSetup(_project.SemiconductorStationSetup);
        ApplyStationSetupText(setup);
        _stationSkeletonPreview = _stationSkeletonTemplate.Preview(_project, setup);
        StationSkeletonItems.Clear();
        foreach (var entry in _stationSkeletonPreview.Entries)
        {
            StationSkeletonItems.Add(CreateStationSkeletonItem(entry));
        }
        RaiseStationSkeletonChanged();
    }

    private void ApplyStationSkeleton()
    {
        if (_project is null
            || _stationSkeletonPreview is not { UnavailableCount: 0 }
            || !TryCreateStationSetup(out var setup))
        {
            return;
        }

        if (_applyStationSkeleton(setup) <= 0)
        {
            PreviewStationSkeleton();
        }
    }

    private void ResetStationSetup()
    {
        _stationSetupWasInvalid = false;
        ApplyStationSetupText(new SemiconductorStationSetupDefinition());
    }

    private void ApplyStationSetupText(SemiconductorStationSetupDefinition setup)
    {
        _stationName = setup.StationName;
        _waferType = setup.WaferType;
        _axisTravelText = FormatNumber(setup.AxisTravel);
        _transportSpeedText = FormatNumber(setup.TransportSpeed);
        _entrySensorPositionText = FormatNumber(setup.EntrySensorPosition);
        _processSensorPositionText = FormatNumber(setup.ProcessSensorPosition);
        _cylinderTravelTimeText = setup.CylinderTravelTimeMilliseconds.ToString(CultureInfo.CurrentCulture);
        RaiseStationSetupChanged();
    }

    private void SetStationSetupText(ref string field, string value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }
        _stationSetupWasInvalid = false;
        RaiseStationSetupValidationChanged();
    }

    private bool TryCreateStationSetup(out SemiconductorStationSetupDefinition setup)
    {
        var axisValid = TryPositiveDouble(AxisTravelText, out var axisTravel);
        var speedValid = TryPositiveDouble(TransportSpeedText, out var transportSpeed);
        var entryValid = TryFiniteDouble(EntrySensorPositionText, out var entryPosition);
        var processValid = TryFiniteDouble(ProcessSensorPositionText, out var processPosition);
        var timingValid = int.TryParse(
            CylinderTravelTimeText,
            NumberStyles.Integer,
            CultureInfo.CurrentCulture,
            out var cylinderTiming) && cylinderTiming > 0;
        setup = new SemiconductorStationSetupDefinition
        {
            StationName = StationName.Trim(),
            WaferType = WaferType.Trim(),
            AxisTravel = axisTravel,
            TransportSpeed = transportSpeed,
            EntrySensorPosition = entryPosition,
            ProcessSensorPosition = processPosition,
            CylinderTravelTimeMilliseconds = cylinderTiming
        };
        return IsStationNameValid
            && IsWaferTypeValid
            && axisValid
            && speedValid
            && entryValid
            && processValid
            && timingValid
            && SemiconductorStationSkeletonTemplate.IsValidSetup(setup);
    }

    private static bool TryPositiveDouble(string text, out double value) =>
        TryFiniteDouble(text, out value) && value > 0;

    private static bool TryFiniteDouble(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
         || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value);

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private delegate bool TryCreateDefinition<T>(out T definition)
        where T : class;
    private void ApplySemanticSetup<T>(
        Func<T, int> apply,
        TryCreateDefinition<T> tryCreate,
        Func<T, bool> isNoChange,
        Action clear,
        Action preview)
        where T : class
    {
        if (!tryCreate(out var setup))
        {
            return;
        }

        if (apply(setup) > 0 || isNoChange(setup))
        {
            clear();
        }
        else
        {
            preview();
        }
    }

    private void ClearStationSkeletonPreview()
    {
        _stationSkeletonPreview = null;
        StationSkeletonItems.Clear();
        RaiseStationSkeletonChanged();
    }

    private void PreviewLoadLockSetup()
    {
        if (_project is null)
        {
            return;
        }

        ClearStationSkeletonPreview();
        ClearProcessBlockPreview();
        ClearCheckpointTemplatePreview();
        LoadLockDoorOptions.Clear();
        LoadLockOutputOptions.Clear();
        LoadLockInputOptions.Clear();

        var layout = ResolveActiveLayout(_project);
        foreach (var component in (layout?.Components ?? [])
                     .Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder)
                     .OrderBy(component => component.Name, StringComparer.CurrentCulture)
                     .ThenBy(component => component.Id, StringComparer.Ordinal))
        {
            LoadLockDoorOptions.Add(new LoadLockSetupOption(component.Id, DisplayName(component.Name, component.Id)));
        }
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture)
                     .ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = new LoadLockSetupOption(channel.Id, DisplayName(channel.Name, channel.Id));
            if (channel.Kind == ChannelKind.DigitalOutput)
            {
                LoadLockOutputOptions.Add(option);
            }
            else if (channel.Kind == ChannelKind.DigitalInput)
            {
                LoadLockInputOptions.Add(option);
            }
        }

        var existing = _project.Devices
            .Where(device => device is { Kind: DeviceKind.LoadLock, LoadLock: not null })
            .ToArray();
        _savedLoadLockSetup = existing.Length == 1 ? CloneLoadLock(existing[0].LoadLock!) : null;
        var draft = _savedLoadLockSetup ?? SuggestLoadLockSetup();
        AddMissingLoadLockOption(LoadLockDoorOptions, draft.OuterDoorComponentId);
        AddMissingLoadLockOption(LoadLockDoorOptions, draft.InnerDoorComponentId);
        AddMissingLoadLockOption(LoadLockOutputOptions, draft.EvacuateCommandChannelId);
        AddMissingLoadLockOption(LoadLockOutputOptions, draft.VentCommandChannelId);
        AddMissingLoadLockOption(LoadLockInputOptions, draft.VacuumReadySensorChannelId);
        AddMissingLoadLockOption(LoadLockInputOptions, draft.AtmosphereReadySensorChannelId);
        ApplyLoadLockSetupDraft(draft);
        _isLoadLockSetupVisible = true;
        RaiseLoadLockSetupChanged();
    }

    private void ApplyLoadLockSetup()
    {
        ApplySemanticSetup(
            _applyLoadLockSetup,
            TryCreateLoadLockSetup,
            IsLoadLockSetupEquivalentToSaved,
            ClearLoadLockSetup,
            PreviewLoadLockSetup);
    }

    private void ResetLoadLockSetup() => ApplyLoadLockSetupDraft(
        _savedLoadLockSetup is null ? SuggestLoadLockSetup() : CloneLoadLock(_savedLoadLockSetup));

    private void ClearLoadLockSetup()
    {
        _isLoadLockSetupVisible = false;
        _savedLoadLockSetup = null;
        LoadLockDoorOptions.Clear();
        LoadLockOutputOptions.Clear();
        LoadLockInputOptions.Clear();
        RaiseLoadLockSetupChanged();
    }

    private LoadLockDefinition SuggestLoadLockSetup()
    {
        var doors = LoadLockDoorOptions.Take(2).Select(option => option.Id).ToArray();
        var outputs = LoadLockOutputOptions.Take(2).Select(option => option.Id).ToArray();
        var inputs = LoadLockInputOptions.Take(2).Select(option => option.Id).ToArray();
        var step = Math.Max(1, _project?.Simulation.FixedStepMilliseconds ?? 5);
        var duration = Math.Max(500, step);
        duration -= duration % step;
        return new LoadLockDefinition
        {
            OuterDoorComponentId = doors.ElementAtOrDefault(0) ?? string.Empty,
            InnerDoorComponentId = doors.ElementAtOrDefault(1) ?? string.Empty,
            EvacuateCommandChannelId = outputs.ElementAtOrDefault(0) ?? string.Empty,
            VentCommandChannelId = outputs.ElementAtOrDefault(1) ?? string.Empty,
            VacuumReadySensorChannelId = inputs.ElementAtOrDefault(0) ?? string.Empty,
            AtmosphereReadySensorChannelId = inputs.ElementAtOrDefault(1) ?? string.Empty,
            PumpDownDurationMilliseconds = duration,
            VentDurationMilliseconds = duration
        };
    }

    private void ApplyLoadLockSetupDraft(LoadLockDefinition setup)
    {
        _outerDoorComponentId = setup.OuterDoorComponentId;
        _innerDoorComponentId = setup.InnerDoorComponentId;
        _evacuateCommandChannelId = setup.EvacuateCommandChannelId;
        _ventCommandChannelId = setup.VentCommandChannelId;
        _vacuumReadySensorChannelId = setup.VacuumReadySensorChannelId;
        _atmosphereReadySensorChannelId = setup.AtmosphereReadySensorChannelId;
        _pumpDownDurationText = setup.PumpDownDurationMilliseconds.ToString(CultureInfo.CurrentCulture);
        _ventDurationText = setup.VentDurationMilliseconds.ToString(CultureInfo.CurrentCulture);
        RaiseLoadLockSetupValidationChanged();
    }

    private void SetLoadLockSelection(ref string? field, string? value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            RaiseLoadLockSetupValidationChanged();
        }
    }

    private void SetLoadLockText(ref string field, string value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            RaiseLoadLockSetupValidationChanged();
        }
    }

    private bool TryCreateLoadLockSetup(out LoadLockDefinition setup)
    {
        var pumpValid = TryLoadLockDuration(PumpDownDurationText, out var pumpDuration);
        var ventValid = TryLoadLockDuration(VentDurationText, out var ventDuration);
        setup = new LoadLockDefinition
        {
            OuterDoorComponentId = OuterDoorComponentId ?? string.Empty,
            InnerDoorComponentId = InnerDoorComponentId ?? string.Empty,
            EvacuateCommandChannelId = EvacuateCommandChannelId ?? string.Empty,
            VentCommandChannelId = VentCommandChannelId ?? string.Empty,
            VacuumReadySensorChannelId = VacuumReadySensorChannelId ?? string.Empty,
            AtmosphereReadySensorChannelId = AtmosphereReadySensorChannelId ?? string.Empty,
            PumpDownDurationMilliseconds = pumpDuration,
            VentDurationMilliseconds = ventDuration
        };
        return !HasMultipleLoadLocks
            && IsOuterDoorComponentValid
            && IsInnerDoorComponentValid
            && IsEvacuateCommandChannelValid
            && IsVentCommandChannelValid
            && IsVacuumReadySensorChannelValid
            && IsAtmosphereReadySensorChannelValid
            && pumpValid
            && ventValid;
    }

    private bool IsLoadLockSetupEquivalentToSaved(LoadLockDefinition setup) =>
        _savedLoadLockSetup is not null
        && string.Equals(_savedLoadLockSetup.OuterDoorComponentId, setup.OuterDoorComponentId, StringComparison.Ordinal)
        && string.Equals(_savedLoadLockSetup.InnerDoorComponentId, setup.InnerDoorComponentId, StringComparison.Ordinal)
        && string.Equals(_savedLoadLockSetup.EvacuateCommandChannelId, setup.EvacuateCommandChannelId, StringComparison.Ordinal)
        && string.Equals(_savedLoadLockSetup.VentCommandChannelId, setup.VentCommandChannelId, StringComparison.Ordinal)
        && string.Equals(_savedLoadLockSetup.VacuumReadySensorChannelId, setup.VacuumReadySensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedLoadLockSetup.AtmosphereReadySensorChannelId, setup.AtmosphereReadySensorChannelId, StringComparison.Ordinal)
        && _savedLoadLockSetup.PumpDownDurationMilliseconds == setup.PumpDownDurationMilliseconds
        && _savedLoadLockSetup.VentDurationMilliseconds == setup.VentDurationMilliseconds;

    private bool IsLoadLockDoor(string? id)
    {
        var layout = _project is null ? null : ResolveActiveLayout(_project);
        return !string.IsNullOrWhiteSpace(id)
            && layout?.Components.Any(component => component.Kind == LayoutComponentKind.PneumaticCylinder
                && string.Equals(component.Id, id, StringComparison.Ordinal)) == true;
    }

    private bool IsLoadLockChannel(string? id, ChannelKind kind) =>
        !string.IsNullOrWhiteSpace(id)
        && _project?.Channels.Any(channel => channel.Kind == kind
            && string.Equals(channel.Id, id, StringComparison.Ordinal)) == true;

    private bool IsLoadLockDurationValid(string text) => TryLoadLockDuration(text, out _);

    private bool TryLoadLockDuration(string text, out int duration)
    {
        var step = Math.Max(1, _project?.Simulation.FixedStepMilliseconds ?? 5);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out duration)
            && duration > 0
            && duration % step == 0;
    }

    private static void AddMissingLoadLockOption(
        ObservableCollection<LoadLockSetupOption> options,
        string id)
    {
        if (!string.IsNullOrWhiteSpace(id)
            && options.All(option => !string.Equals(option.Id, id, StringComparison.Ordinal)))
        {
            options.Add(new LoadLockSetupOption(
                id,
                $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})"));
        }
    }

    private static LoadLockDefinition CloneLoadLock(LoadLockDefinition setup) => new()
    {
        OuterDoorComponentId = setup.OuterDoorComponentId,
        InnerDoorComponentId = setup.InnerDoorComponentId,
        EvacuateCommandChannelId = setup.EvacuateCommandChannelId,
        VentCommandChannelId = setup.VentCommandChannelId,
        VacuumReadySensorChannelId = setup.VacuumReadySensorChannelId,
        AtmosphereReadySensorChannelId = setup.AtmosphereReadySensorChannelId,
        PumpDownDurationMilliseconds = setup.PumpDownDurationMilliseconds,
        VentDurationMilliseconds = setup.VentDurationMilliseconds
    };

    private WaferHandlerDraft CaptureWaferHandlerDraft() => new(WaferHandlerHorizontalAxisId, WaferHandlerVerticalAxisId, WaferHandlerWorkpieceComponentId, WaferHandlerSourcePresentSensorChannelId, WaferHandlerGateOpenSensorChannelId, WaferHandlerPickCommandChannelId, WaferHandlerPlaceCommandChannelId, WaferHandlerHoldingFeedbackChannelId, WaferHandlerPlacedFeedbackChannelId, WaferHandlerPickHorizontalText, WaferHandlerPickVerticalText, WaferHandlerPlaceHorizontalText, WaferHandlerPlaceVerticalText);
    private void RestoreWaferHandlerDraft(WaferHandlerDraft draft)
    {
        _waferHandlerHorizontalAxisId = draft.HorizontalAxisId; _waferHandlerVerticalAxisId = draft.VerticalAxisId; _waferHandlerWorkpieceComponentId = draft.WorkpieceId; _waferHandlerSourcePresentSensorChannelId = draft.SourceInputId; _waferHandlerGateOpenSensorChannelId = draft.GateInputId; _waferHandlerPickCommandChannelId = draft.PickOutputId; _waferHandlerPlaceCommandChannelId = draft.PlaceOutputId; _waferHandlerHoldingFeedbackChannelId = draft.HoldingInputId; _waferHandlerPlacedFeedbackChannelId = draft.PlacedInputId; _waferHandlerPickHorizontalText = draft.PickHorizontal; _waferHandlerPickVerticalText = draft.PickVertical; _waferHandlerPlaceHorizontalText = draft.PlaceHorizontal; _waferHandlerPlaceVerticalText = draft.PlaceVertical; RaiseSemanticSetupChanged();
    }
    private PrealignerDraft CapturePrealignerDraft() => new(PrealignerRotaryStageComponentId, PrealignerClampCylinderComponentId, PrealignerWaferPresentSensorChannelId, PrealignerAlignmentAcceptedCommandChannelId, PrealignerAlignmentReadyFeedbackChannelId, PrealignerAlignmentCompleteFeedbackChannelId, PrealignerAlignmentTargetText, PrealignerAlignmentToleranceText);
    private void RestorePrealignerDraft(PrealignerDraft draft)
    {
        _prealignerRotaryStageComponentId = draft.StageId; _prealignerClampCylinderComponentId = draft.ClampId; _prealignerWaferPresentSensorChannelId = draft.WaferPresentId; _prealignerAlignmentAcceptedCommandChannelId = draft.AcceptedId; _prealignerAlignmentReadyFeedbackChannelId = draft.ReadyId; _prealignerAlignmentCompleteFeedbackChannelId = draft.CompleteId; _prealignerAlignmentTargetText = draft.Target; _prealignerAlignmentToleranceText = draft.Tolerance; RaiseSemanticSetupChanged();
    }
    private void SetSemanticSelection(ref string? field, string? value, string propertyName) { if (SetProperty(ref field, value, propertyName)) RaiseSemanticSetupValidationChanged(); }
    private void SetSemanticText(ref string field, string value, string propertyName) { if (SetProperty(ref field, value, propertyName)) RaiseSemanticSetupValidationChanged(); }
    private bool IsLinearAxis(string? id) => _project?.Axes.Any(axis => axis.Kind == AxisKind.Linear && string.Equals(axis.Id, id, StringComparison.Ordinal)) == true;
    private bool IsLayoutComponent(string? id, LayoutComponentKind kind) => ResolveActiveLayout(_project!)?.Components.Any(component => component.Kind == kind && string.Equals(component.Id, id, StringComparison.Ordinal)) == true;
    private bool IsSemanticChannel(string? id, ChannelKind kind) => _project?.Channels.Any(channel => channel.Kind == kind && string.Equals(channel.Id, id, StringComparison.Ordinal)) == true;
    private bool IsDeviceCamera(string? id) => _project?.Devices.Any(device => device is { Kind: DeviceKind.Camera, Camera: not null } && string.Equals(device.Id, id, StringComparison.Ordinal)) == true;
    private bool IsAxisPosition(string? axisId, string text) => TryGetAxis(axisId, out var axis) && TryFiniteDouble(text, out var value) && axis.SoftLimitMin.HasValue && axis.SoftLimitMax.HasValue && value >= axis.SoftLimitMin.Value && value <= axis.SoftLimitMax.Value;
    private bool IsRotaryPosition(string? stageId, string text) => TryGetPrealignerStage(stageId, out var stage) && TryFiniteDouble(text, out var value) && _project!.Axes.FirstOrDefault(axis => string.Equals(axis.Id, stage.BehaviorBindingId, StringComparison.Ordinal)) is { Kind: AxisKind.Rotary, SoftLimitMin: not null, SoftLimitMax: not null } axis && value >= axis.SoftLimitMin.Value && value <= axis.SoftLimitMax.Value;
    private bool TryGetAxis(string? id, out OpenVisionLab.Machine.Core.Axes.VirtualAxisDefinition axis) { axis = _project?.Axes.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))!; return axis is not null; }
    private bool TryGetPrealignerStage(string? id, out OpenVisionLab.Machine.Core.Layouts.LayoutComponentDefinition stage) { var candidate = ResolveActiveLayout(_project!)?.Components.FirstOrDefault(component => component.Kind == LayoutComponentKind.RotaryStage && string.Equals(component.Id, id, StringComparison.Ordinal)); stage = candidate!; return candidate is not null && _project!.Axes.Any(axis => axis.Kind == AxisKind.Rotary && string.Equals(axis.Id, candidate.BehaviorBindingId, StringComparison.Ordinal)); }
    private double AxisLimitValue(string? axisId, int which) { return TryGetAxis(axisId, out var axis) && axis.SoftLimitMin.HasValue && axis.SoftLimitMax.HasValue ? which == 0 ? axis.SoftLimitMin.Value : axis.SoftLimitMax.Value : 0; }
    private double AxisLimitValueForStage(string? stageId, int which) => TryGetPrealignerStage(stageId, out var stage) && _project!.Axes.FirstOrDefault(axis => string.Equals(axis.Id, stage.BehaviorBindingId, StringComparison.Ordinal)) is { SoftLimitMin: not null, SoftLimitMax: not null } axis ? which == 0 ? axis.SoftLimitMin.Value : axis.SoftLimitMax.Value : 0;
    private static double ParseDouble(string text) => TryFiniteDouble(text, out var value) ? value : double.NaN;
    private bool CanCreateWaferHandlerSetup() => TryCreateWaferHandlerSetup(out var _);
    private bool CanCreatePrealignerSetup() => TryCreatePrealignerSetup(out var _);
    private bool CanCreateInspectionHandoffSetup() => TryCreateInspectionHandoffSetup(out var _);
    private bool CanCreateInspectionSortRouterSetup() => TryCreateInspectionSortRouterSetup(out var _);
    private bool CanCreateOhtHandoffSetup() => TryCreateOhtHandoffSetup(out var _);
    private static void AddMissingSemanticOption(ObservableCollection<LoadLockSetupOption> options, string id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !string.Equals(option.Id, id, StringComparison.Ordinal))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static WaferHandlerDefinition CloneWaferHandler(WaferHandlerDefinition setup) => new() { HorizontalAxisId = setup.HorizontalAxisId, VerticalAxisId = setup.VerticalAxisId, WorkpieceComponentId = setup.WorkpieceComponentId, SourcePresentSensorChannelId = setup.SourcePresentSensorChannelId, GateOpenSensorChannelId = setup.GateOpenSensorChannelId, PickCommandChannelId = setup.PickCommandChannelId, PlaceCommandChannelId = setup.PlaceCommandChannelId, HoldingFeedbackChannelId = setup.HoldingFeedbackChannelId, PlacedFeedbackChannelId = setup.PlacedFeedbackChannelId, PickHorizontalPosition = setup.PickHorizontalPosition, PickVerticalPosition = setup.PickVerticalPosition, PlaceHorizontalPosition = setup.PlaceHorizontalPosition, PlaceVerticalPosition = setup.PlaceVerticalPosition };
    private static PrealignerDefinition ClonePrealigner(PrealignerDefinition setup) => new() { RotaryStageComponentId = setup.RotaryStageComponentId, ClampCylinderComponentId = setup.ClampCylinderComponentId, WaferPresentSensorChannelId = setup.WaferPresentSensorChannelId, AlignmentAcceptedCommandChannelId = setup.AlignmentAcceptedCommandChannelId, AlignmentReadyFeedbackChannelId = setup.AlignmentReadyFeedbackChannelId, AlignmentCompleteFeedbackChannelId = setup.AlignmentCompleteFeedbackChannelId, AlignmentTargetDegrees = setup.AlignmentTargetDegrees, AlignmentToleranceDegrees = setup.AlignmentToleranceDegrees };
    private static InspectionHandoffDefinition CloneInspectionHandoff(InspectionHandoffDefinition setup) => new() { CameraId = setup.CameraId, InspectionPositionSensorChannelId = setup.InspectionPositionSensorChannelId, ResultAcceptedCommandChannelId = setup.ResultAcceptedCommandChannelId, InspectionReadyFeedbackChannelId = setup.InspectionReadyFeedbackChannelId, InspectionCompleteFeedbackChannelId = setup.InspectionCompleteFeedbackChannelId };
    private static InspectionSortRouterDefinition CloneInspectionSortRouter(InspectionSortRouterDefinition setup) => new() { CameraId = setup.CameraId, PassConveyorComponentId = setup.PassConveyorComponentId, NgConveyorComponentId = setup.NgConveyorComponentId, PassRoutedFeedbackChannelId = setup.PassRoutedFeedbackChannelId, NgRoutedFeedbackChannelId = setup.NgRoutedFeedbackChannelId };
    private static OhtHandoffDefinition CloneOhtHandoff(OhtHandoffDefinition setup) => new() { TransportConveyorComponentId = setup.TransportConveyorComponentId, RouteAvailableSensorChannelId = setup.RouteAvailableSensorChannelId, VehicleDockedSensorChannelId = setup.VehicleDockedSensorChannelId, LoadPortReadySensorChannelId = setup.LoadPortReadySensorChannelId, CarrierReceivedSensorChannelId = setup.CarrierReceivedSensorChannelId, HandoffReadyFeedbackChannelId = setup.HandoffReadyFeedbackChannelId, CarrierTransferredFeedbackChannelId = setup.CarrierTransferredFeedbackChannelId };

    private sealed record WaferHandlerDraft(string? HorizontalAxisId, string? VerticalAxisId, string? WorkpieceId, string? SourceInputId, string? GateInputId, string? PickOutputId, string? PlaceOutputId, string? HoldingInputId, string? PlacedInputId, string PickHorizontal, string PickVertical, string PlaceHorizontal, string PlaceVertical);
    private sealed record PrealignerDraft(string? StageId, string? ClampId, string? WaferPresentId, string? AcceptedId, string? ReadyId, string? CompleteId, string Target, string Tolerance);

    private void PreviewWaferHandlerSetup()
    {
        if (_project is null) return;
        ClearStationSkeletonPreview();
        ClearLoadLockSetup();
        ClearPrealignerSetup();
        ClearProcessBlockPreview();
        ClearCheckpointTemplatePreview();
        WaferHandlerAxisOptions.Clear();
        WaferHandlerWorkpieceOptions.Clear();
        WaferHandlerInputOptions.Clear();
        WaferHandlerOutputOptions.Clear();
        foreach (var axis in _project.Axes.Where(axis => axis.Kind == AxisKind.Linear).OrderBy(axis => axis.Name, StringComparer.CurrentCulture).ThenBy(axis => axis.Id, StringComparer.Ordinal))
            WaferHandlerAxisOptions.Add(new LoadLockSetupOption(axis.Id, DisplayName(axis.Name, axis.Id)));
        var layout = ResolveActiveLayout(_project);
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Workpiece).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal))
            WaferHandlerWorkpieceOptions.Add(new LoadLockSetupOption(component.Id, DisplayName(component.Name, component.Id)));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = new LoadLockSetupOption(channel.Id, DisplayName(channel.Name, channel.Id));
            if (channel.Kind == ChannelKind.DigitalInput) WaferHandlerInputOptions.Add(option);
            if (channel.Kind == ChannelKind.DigitalOutput) WaferHandlerOutputOptions.Add(option);
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Handler, WaferHandler: not null }).ToArray();
        _savedWaferHandlerSetup = existing.Length == 1 ? CloneWaferHandler(existing[0].WaferHandler!) : null;
        var draft = _savedWaferHandlerSetup ?? SuggestWaferHandlerSetup();
        AddMissingSemanticOption(WaferHandlerAxisOptions, draft.HorizontalAxisId);
        AddMissingSemanticOption(WaferHandlerAxisOptions, draft.VerticalAxisId);
        AddMissingSemanticOption(WaferHandlerWorkpieceOptions, draft.WorkpieceComponentId);
        foreach (var id in new[] { draft.SourcePresentSensorChannelId, draft.GateOpenSensorChannelId, draft.HoldingFeedbackChannelId, draft.PlacedFeedbackChannelId }) AddMissingSemanticOption(WaferHandlerInputOptions, id);
        foreach (var id in new[] { draft.PickCommandChannelId, draft.PlaceCommandChannelId }) AddMissingSemanticOption(WaferHandlerOutputOptions, id);
        ApplyWaferHandlerDraft(draft);
        _isWaferHandlerSetupVisible = true;
        RaiseSemanticSetupChanged();
    }

    private void ApplyWaferHandlerSetup()
    {
        ApplySemanticSetup(
            _applyWaferHandlerSetup,
            TryCreateWaferHandlerSetup,
            IsWaferHandlerSetupEquivalentToSaved,
            ClearWaferHandlerSetup,
            PreviewWaferHandlerSetup);
    }

    private void ResetWaferHandlerSetup() => ApplyWaferHandlerDraft(_savedWaferHandlerSetup is null ? SuggestWaferHandlerSetup() : CloneWaferHandler(_savedWaferHandlerSetup));

    private void ClearWaferHandlerSetup()
    {
        _isWaferHandlerSetupVisible = false;
        _savedWaferHandlerSetup = null;
        WaferHandlerAxisOptions.Clear(); WaferHandlerWorkpieceOptions.Clear(); WaferHandlerInputOptions.Clear(); WaferHandlerOutputOptions.Clear();
        RaiseSemanticSetupChanged();
    }

    private WaferHandlerDefinition SuggestWaferHandlerSetup() => new()
    {
        HorizontalAxisId = WaferHandlerAxisOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        VerticalAxisId = WaferHandlerAxisOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        WorkpieceComponentId = WaferHandlerWorkpieceOptions.FirstOrDefault()?.Id ?? string.Empty,
        SourcePresentSensorChannelId = WaferHandlerInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        GateOpenSensorChannelId = WaferHandlerInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        HoldingFeedbackChannelId = WaferHandlerInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        PlacedFeedbackChannelId = WaferHandlerInputOptions.ElementAtOrDefault(3)?.Id ?? string.Empty,
        PickCommandChannelId = WaferHandlerOutputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        PlaceCommandChannelId = WaferHandlerOutputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        PickHorizontalPosition = AxisLimitValue(WaferHandlerAxisOptions.ElementAtOrDefault(0)?.Id, 0),
        PickVerticalPosition = AxisLimitValue(WaferHandlerAxisOptions.ElementAtOrDefault(1)?.Id, 0),
        PlaceHorizontalPosition = AxisLimitValue(WaferHandlerAxisOptions.ElementAtOrDefault(0)?.Id, 1),
        PlaceVerticalPosition = AxisLimitValue(WaferHandlerAxisOptions.ElementAtOrDefault(1)?.Id, 1)
    };

    private void ApplyWaferHandlerDraft(WaferHandlerDefinition setup)
    {
        _waferHandlerHorizontalAxisId = setup.HorizontalAxisId; _waferHandlerVerticalAxisId = setup.VerticalAxisId; _waferHandlerWorkpieceComponentId = setup.WorkpieceComponentId;
        _waferHandlerSourcePresentSensorChannelId = setup.SourcePresentSensorChannelId; _waferHandlerGateOpenSensorChannelId = setup.GateOpenSensorChannelId; _waferHandlerPickCommandChannelId = setup.PickCommandChannelId; _waferHandlerPlaceCommandChannelId = setup.PlaceCommandChannelId; _waferHandlerHoldingFeedbackChannelId = setup.HoldingFeedbackChannelId; _waferHandlerPlacedFeedbackChannelId = setup.PlacedFeedbackChannelId;
        _waferHandlerPickHorizontalText = FormatNumber(setup.PickHorizontalPosition); _waferHandlerPickVerticalText = FormatNumber(setup.PickVerticalPosition); _waferHandlerPlaceHorizontalText = FormatNumber(setup.PlaceHorizontalPosition); _waferHandlerPlaceVerticalText = FormatNumber(setup.PlaceVerticalPosition);
        RaiseSemanticSetupChanged();
    }

    private bool TryCreateWaferHandlerSetup(out WaferHandlerDefinition setup)
    {
        var channelIds = new[] { WaferHandlerSourcePresentSensorChannelId, WaferHandlerGateOpenSensorChannelId, WaferHandlerPickCommandChannelId, WaferHandlerPlaceCommandChannelId, WaferHandlerHoldingFeedbackChannelId, WaferHandlerPlacedFeedbackChannelId };
        setup = new WaferHandlerDefinition { HorizontalAxisId = WaferHandlerHorizontalAxisId ?? string.Empty, VerticalAxisId = WaferHandlerVerticalAxisId ?? string.Empty, WorkpieceComponentId = WaferHandlerWorkpieceComponentId ?? string.Empty, SourcePresentSensorChannelId = WaferHandlerSourcePresentSensorChannelId ?? string.Empty, GateOpenSensorChannelId = WaferHandlerGateOpenSensorChannelId ?? string.Empty, PickCommandChannelId = WaferHandlerPickCommandChannelId ?? string.Empty, PlaceCommandChannelId = WaferHandlerPlaceCommandChannelId ?? string.Empty, HoldingFeedbackChannelId = WaferHandlerHoldingFeedbackChannelId ?? string.Empty, PlacedFeedbackChannelId = WaferHandlerPlacedFeedbackChannelId ?? string.Empty, PickHorizontalPosition = ParseDouble(WaferHandlerPickHorizontalText), PickVerticalPosition = ParseDouble(WaferHandlerPickVerticalText), PlaceHorizontalPosition = ParseDouble(WaferHandlerPlaceHorizontalText), PlaceVerticalPosition = ParseDouble(WaferHandlerPlaceVerticalText) };
        return !HasMultipleWaferHandlers && IsWaferHandlerHorizontalAxisValid && IsWaferHandlerVerticalAxisValid && IsWaferHandlerWorkpieceValid && channelIds.All(id => !string.IsNullOrWhiteSpace(id)) && channelIds.Distinct(StringComparer.Ordinal).Count() == channelIds.Length && IsWaferHandlerSourcePresentValid && IsWaferHandlerGateOpenValid && IsWaferHandlerPickCommandValid && IsWaferHandlerPlaceCommandValid && IsWaferHandlerHoldingFeedbackValid && IsWaferHandlerPlacedFeedbackValid && IsWaferHandlerPickHorizontalValid && IsWaferHandlerPickVerticalValid && IsWaferHandlerPlaceHorizontalValid && IsWaferHandlerPlaceVerticalValid;
    }

    private bool IsWaferHandlerSetupEquivalentToSaved(WaferHandlerDefinition setup) =>
        _savedWaferHandlerSetup is not null
        && string.Equals(_savedWaferHandlerSetup.HorizontalAxisId, setup.HorizontalAxisId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.VerticalAxisId, setup.VerticalAxisId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.WorkpieceComponentId, setup.WorkpieceComponentId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.SourcePresentSensorChannelId, setup.SourcePresentSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.GateOpenSensorChannelId, setup.GateOpenSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.PickCommandChannelId, setup.PickCommandChannelId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.PlaceCommandChannelId, setup.PlaceCommandChannelId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.HoldingFeedbackChannelId, setup.HoldingFeedbackChannelId, StringComparison.Ordinal)
        && string.Equals(_savedWaferHandlerSetup.PlacedFeedbackChannelId, setup.PlacedFeedbackChannelId, StringComparison.Ordinal)
        && _savedWaferHandlerSetup.PickHorizontalPosition == setup.PickHorizontalPosition
        && _savedWaferHandlerSetup.PickVerticalPosition == setup.PickVerticalPosition
        && _savedWaferHandlerSetup.PlaceHorizontalPosition == setup.PlaceHorizontalPosition
        && _savedWaferHandlerSetup.PlaceVerticalPosition == setup.PlaceVerticalPosition;

    private void PreviewPrealignerSetup()
    {
        if (_project is null) return;
        ClearStationSkeletonPreview(); ClearLoadLockSetup(); ClearWaferHandlerSetup(); ClearProcessBlockPreview(); ClearCheckpointTemplatePreview();
        PrealignerStageOptions.Clear(); PrealignerCylinderOptions.Clear(); PrealignerInputOptions.Clear(); PrealignerOutputOptions.Clear();
        var layout = ResolveActiveLayout(_project);
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.RotaryStage).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) PrealignerStageOptions.Add(new LoadLockSetupOption(component.Id, DisplayName(component.Name, component.Id)));
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) PrealignerCylinderOptions.Add(new LoadLockSetupOption(component.Id, DisplayName(component.Name, component.Id)));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = new LoadLockSetupOption(channel.Id, DisplayName(channel.Name, channel.Id));
            if (channel.Kind == ChannelKind.DigitalInput) PrealignerInputOptions.Add(option);
            if (channel.Kind == ChannelKind.DigitalOutput) PrealignerOutputOptions.Add(option);
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Prealigner, Prealigner: not null }).ToArray();
        _savedPrealignerSetup = existing.Length == 1 ? ClonePrealigner(existing[0].Prealigner!) : null;
        var draft = _savedPrealignerSetup ?? SuggestPrealignerSetup();
        AddMissingSemanticOption(PrealignerStageOptions, draft.RotaryStageComponentId); AddMissingSemanticOption(PrealignerCylinderOptions, draft.ClampCylinderComponentId);
        foreach (var id in new[] { draft.WaferPresentSensorChannelId, draft.AlignmentReadyFeedbackChannelId, draft.AlignmentCompleteFeedbackChannelId }) AddMissingSemanticOption(PrealignerInputOptions, id);
        AddMissingSemanticOption(PrealignerOutputOptions, draft.AlignmentAcceptedCommandChannelId);
        ApplyPrealignerDraft(draft); _isPrealignerSetupVisible = true; RaiseSemanticSetupChanged();
    }

    private void ApplyPrealignerSetup()
    {
        ApplySemanticSetup(
            _applyPrealignerSetup,
            TryCreatePrealignerSetup,
            IsPrealignerSetupEquivalentToSaved,
            ClearPrealignerSetup,
            PreviewPrealignerSetup);
    }

    private void ResetPrealignerSetup() => ApplyPrealignerDraft(_savedPrealignerSetup is null ? SuggestPrealignerSetup() : ClonePrealigner(_savedPrealignerSetup));

    private void ClearPrealignerSetup()
    {
        _isPrealignerSetupVisible = false; _savedPrealignerSetup = null;
        PrealignerStageOptions.Clear(); PrealignerCylinderOptions.Clear(); PrealignerInputOptions.Clear(); PrealignerOutputOptions.Clear(); RaiseSemanticSetupChanged();
    }

    private PrealignerDefinition SuggestPrealignerSetup() => new()
    {
        RotaryStageComponentId = PrealignerStageOptions.FirstOrDefault()?.Id ?? string.Empty,
        ClampCylinderComponentId = PrealignerCylinderOptions.FirstOrDefault()?.Id ?? string.Empty,
        WaferPresentSensorChannelId = PrealignerInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        AlignmentReadyFeedbackChannelId = PrealignerInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        AlignmentCompleteFeedbackChannelId = PrealignerInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        AlignmentAcceptedCommandChannelId = PrealignerOutputOptions.FirstOrDefault()?.Id ?? string.Empty,
        AlignmentTargetDegrees = AxisLimitValueForStage(PrealignerStageOptions.FirstOrDefault()?.Id, 0),
        AlignmentToleranceDegrees = 0.1
    };

    private void ApplyPrealignerDraft(PrealignerDefinition setup)
    {
        _prealignerRotaryStageComponentId = setup.RotaryStageComponentId; _prealignerClampCylinderComponentId = setup.ClampCylinderComponentId; _prealignerWaferPresentSensorChannelId = setup.WaferPresentSensorChannelId; _prealignerAlignmentAcceptedCommandChannelId = setup.AlignmentAcceptedCommandChannelId; _prealignerAlignmentReadyFeedbackChannelId = setup.AlignmentReadyFeedbackChannelId; _prealignerAlignmentCompleteFeedbackChannelId = setup.AlignmentCompleteFeedbackChannelId; _prealignerAlignmentTargetText = FormatNumber(setup.AlignmentTargetDegrees); _prealignerAlignmentToleranceText = FormatNumber(setup.AlignmentToleranceDegrees); RaiseSemanticSetupChanged();
    }

    private bool TryCreatePrealignerSetup(out PrealignerDefinition setup)
    {
        var channelIds = new[] { PrealignerWaferPresentSensorChannelId, PrealignerAlignmentAcceptedCommandChannelId, PrealignerAlignmentReadyFeedbackChannelId, PrealignerAlignmentCompleteFeedbackChannelId };
        setup = new PrealignerDefinition { RotaryStageComponentId = PrealignerRotaryStageComponentId ?? string.Empty, ClampCylinderComponentId = PrealignerClampCylinderComponentId ?? string.Empty, WaferPresentSensorChannelId = PrealignerWaferPresentSensorChannelId ?? string.Empty, AlignmentAcceptedCommandChannelId = PrealignerAlignmentAcceptedCommandChannelId ?? string.Empty, AlignmentReadyFeedbackChannelId = PrealignerAlignmentReadyFeedbackChannelId ?? string.Empty, AlignmentCompleteFeedbackChannelId = PrealignerAlignmentCompleteFeedbackChannelId ?? string.Empty, AlignmentTargetDegrees = ParseDouble(PrealignerAlignmentTargetText), AlignmentToleranceDegrees = ParseDouble(PrealignerAlignmentToleranceText) };
        return !HasMultiplePrealigners && IsPrealignerRotaryStageValid && IsPrealignerClampCylinderValid && channelIds.All(id => !string.IsNullOrWhiteSpace(id)) && channelIds.Distinct(StringComparer.Ordinal).Count() == channelIds.Length && IsPrealignerWaferPresentValid && IsPrealignerAlignmentAcceptedValid && IsPrealignerAlignmentReadyValid && IsPrealignerAlignmentCompleteValid && IsPrealignerAlignmentTargetValid && IsPrealignerAlignmentToleranceValid;
    }

    private bool IsPrealignerSetupEquivalentToSaved(PrealignerDefinition setup) =>
        _savedPrealignerSetup is not null
        && string.Equals(_savedPrealignerSetup.RotaryStageComponentId, setup.RotaryStageComponentId, StringComparison.Ordinal)
        && string.Equals(_savedPrealignerSetup.ClampCylinderComponentId, setup.ClampCylinderComponentId, StringComparison.Ordinal)
        && string.Equals(_savedPrealignerSetup.WaferPresentSensorChannelId, setup.WaferPresentSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedPrealignerSetup.AlignmentAcceptedCommandChannelId, setup.AlignmentAcceptedCommandChannelId, StringComparison.Ordinal)
        && string.Equals(_savedPrealignerSetup.AlignmentReadyFeedbackChannelId, setup.AlignmentReadyFeedbackChannelId, StringComparison.Ordinal)
        && string.Equals(_savedPrealignerSetup.AlignmentCompleteFeedbackChannelId, setup.AlignmentCompleteFeedbackChannelId, StringComparison.Ordinal)
        && _savedPrealignerSetup.AlignmentTargetDegrees == setup.AlignmentTargetDegrees
        && _savedPrealignerSetup.AlignmentToleranceDegrees == setup.AlignmentToleranceDegrees;

    private void PreviewInspectionHandoffSetup()
    {
        if (_project is null) return;
        ClearStationSkeletonPreview(); ClearLoadLockSetup(); ClearWaferHandlerSetup(); ClearPrealignerSetup(); ClearInspectionSortRouterSetup(); ClearOhtHandoffSetup(); ClearProcessBlockPreview(); ClearCheckpointTemplatePreview();
        InspectionCameraOptions.Clear(); InspectionInputOptions.Clear(); InspectionOutputOptions.Clear(); InspectionConveyorOptions.Clear();
        PopulateInspectionChannelsAndCameras();
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Inspection, InspectionHandoff: not null }).ToArray();
        _savedInspectionHandoffSetup = existing.Length == 1 ? CloneInspectionHandoff(existing[0].InspectionHandoff!) : null;
        var draft = _savedInspectionHandoffSetup ?? SuggestInspectionHandoffSetup();
        AddMissingSemanticOption(InspectionCameraOptions, draft.CameraId);
        foreach (var id in new[] { draft.InspectionPositionSensorChannelId, draft.InspectionReadyFeedbackChannelId, draft.InspectionCompleteFeedbackChannelId }) AddMissingSemanticOption(InspectionInputOptions, id);
        AddMissingSemanticOption(InspectionOutputOptions, draft.ResultAcceptedCommandChannelId);
        ApplyInspectionHandoffDraft(draft); _isInspectionHandoffSetupVisible = true; RaiseSemanticSetupChanged();
    }

    private void ApplyInspectionHandoffSetup()
    {
        ApplySemanticSetup(
            _applyInspectionHandoffSetup,
            TryCreateInspectionHandoffSetup,
            IsInspectionHandoffSetupEquivalentToSaved,
            ClearInspectionHandoffSetup,
            PreviewInspectionHandoffSetup);
    }

    private void ResetInspectionHandoffSetup() => ApplyInspectionHandoffDraft(_savedInspectionHandoffSetup is null ? SuggestInspectionHandoffSetup() : CloneInspectionHandoff(_savedInspectionHandoffSetup));

    private void ClearInspectionHandoffSetup()
    {
        _isInspectionHandoffSetupVisible = false; _savedInspectionHandoffSetup = null;
        RaiseSemanticSetupChanged();
    }

    private InspectionHandoffDefinition SuggestInspectionHandoffSetup() => new()
    {
        CameraId = InspectionCameraOptions.FirstOrDefault()?.Id ?? string.Empty,
        InspectionPositionSensorChannelId = InspectionInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        InspectionReadyFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        InspectionCompleteFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        ResultAcceptedCommandChannelId = InspectionOutputOptions.FirstOrDefault()?.Id ?? string.Empty
    };

    private void ApplyInspectionHandoffDraft(InspectionHandoffDefinition setup)
    {
        _inspectionHandoffCameraId = setup.CameraId; _inspectionHandoffPositionSensorChannelId = setup.InspectionPositionSensorChannelId; _inspectionHandoffAcceptedChannelId = setup.ResultAcceptedCommandChannelId; _inspectionHandoffReadyChannelId = setup.InspectionReadyFeedbackChannelId; _inspectionHandoffCompleteChannelId = setup.InspectionCompleteFeedbackChannelId; RaiseSemanticSetupChanged();
    }

    private bool TryCreateInspectionHandoffSetup(out InspectionHandoffDefinition setup)
    {
        var ids = new[] { InspectionHandoffPositionSensorChannelId, InspectionHandoffAcceptedChannelId, InspectionHandoffReadyChannelId, InspectionHandoffCompleteChannelId };
        setup = new InspectionHandoffDefinition { CameraId = InspectionHandoffCameraId ?? string.Empty, InspectionPositionSensorChannelId = InspectionHandoffPositionSensorChannelId ?? string.Empty, ResultAcceptedCommandChannelId = InspectionHandoffAcceptedChannelId ?? string.Empty, InspectionReadyFeedbackChannelId = InspectionHandoffReadyChannelId ?? string.Empty, InspectionCompleteFeedbackChannelId = InspectionHandoffCompleteChannelId ?? string.Empty };
        return !HasMultipleInspectionHandoffs && IsInspectionHandoffCameraValid && IsInspectionHandoffPositionValid && IsInspectionHandoffAcceptedValid && IsInspectionHandoffReadyValid && IsInspectionHandoffCompleteValid && ids.All(id => !string.IsNullOrWhiteSpace(id)) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    }

    private bool IsInspectionHandoffSetupEquivalentToSaved(InspectionHandoffDefinition setup) =>
        _savedInspectionHandoffSetup is not null
        && string.Equals(_savedInspectionHandoffSetup.CameraId, setup.CameraId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionHandoffSetup.InspectionPositionSensorChannelId, setup.InspectionPositionSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionHandoffSetup.ResultAcceptedCommandChannelId, setup.ResultAcceptedCommandChannelId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionHandoffSetup.InspectionReadyFeedbackChannelId, setup.InspectionReadyFeedbackChannelId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionHandoffSetup.InspectionCompleteFeedbackChannelId, setup.InspectionCompleteFeedbackChannelId, StringComparison.Ordinal);

    private void PreviewInspectionSortRouterSetup()
    {
        if (_project is null) return;
        ClearStationSkeletonPreview(); ClearLoadLockSetup(); ClearWaferHandlerSetup(); ClearPrealignerSetup(); ClearInspectionHandoffSetup(); ClearOhtHandoffSetup(); ClearProcessBlockPreview(); ClearCheckpointTemplatePreview();
        InspectionCameraOptions.Clear(); InspectionInputOptions.Clear(); InspectionOutputOptions.Clear(); InspectionConveyorOptions.Clear();
        PopulateInspectionChannelsAndCameras();
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Sorter, InspectionSortRouter: not null }).ToArray();
        _savedInspectionSortRouterSetup = existing.Length == 1 ? CloneInspectionSortRouter(existing[0].InspectionSortRouter!) : null;
        var draft = _savedInspectionSortRouterSetup ?? SuggestInspectionSortRouterSetup();
        AddMissingSemanticOption(InspectionCameraOptions, draft.CameraId); AddMissingSemanticOption(InspectionConveyorOptions, draft.PassConveyorComponentId); AddMissingSemanticOption(InspectionConveyorOptions, draft.NgConveyorComponentId); AddMissingSemanticOption(InspectionInputOptions, draft.PassRoutedFeedbackChannelId); AddMissingSemanticOption(InspectionInputOptions, draft.NgRoutedFeedbackChannelId);
        ApplyInspectionSortRouterDraft(draft); _isInspectionSortRouterSetupVisible = true; RaiseSemanticSetupChanged();
    }

    private void ApplyInspectionSortRouterSetup()
    {
        ApplySemanticSetup(
            _applyInspectionSortRouterSetup,
            TryCreateInspectionSortRouterSetup,
            IsInspectionSortRouterSetupEquivalentToSaved,
            ClearInspectionSortRouterSetup,
            PreviewInspectionSortRouterSetup);
    }

    private void ResetInspectionSortRouterSetup() => ApplyInspectionSortRouterDraft(_savedInspectionSortRouterSetup is null ? SuggestInspectionSortRouterSetup() : CloneInspectionSortRouter(_savedInspectionSortRouterSetup));

    private void ClearInspectionSortRouterSetup()
    {
        _isInspectionSortRouterSetupVisible = false; _savedInspectionSortRouterSetup = null;
        RaiseSemanticSetupChanged();
    }

    private InspectionSortRouterDefinition SuggestInspectionSortRouterSetup() => new()
    {
        CameraId = InspectionCameraOptions.FirstOrDefault()?.Id ?? string.Empty,
        PassConveyorComponentId = InspectionConveyorOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        NgConveyorComponentId = InspectionConveyorOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        PassRoutedFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        NgRoutedFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty
    };

    private void ApplyInspectionSortRouterDraft(InspectionSortRouterDefinition setup)
    {
        _inspectionSortCameraId = setup.CameraId; _inspectionSortPassConveyorId = setup.PassConveyorComponentId; _inspectionSortNgConveyorId = setup.NgConveyorComponentId; _inspectionSortPassFeedbackChannelId = setup.PassRoutedFeedbackChannelId; _inspectionSortNgFeedbackChannelId = setup.NgRoutedFeedbackChannelId; RaiseSemanticSetupChanged();
    }

    private bool TryCreateInspectionSortRouterSetup(out InspectionSortRouterDefinition setup)
    {
        setup = new InspectionSortRouterDefinition { CameraId = InspectionSortCameraId ?? string.Empty, PassConveyorComponentId = InspectionSortPassConveyorId ?? string.Empty, NgConveyorComponentId = InspectionSortNgConveyorId ?? string.Empty, PassRoutedFeedbackChannelId = InspectionSortPassFeedbackChannelId ?? string.Empty, NgRoutedFeedbackChannelId = InspectionSortNgFeedbackChannelId ?? string.Empty };
        return !HasMultipleInspectionSortRouters && IsInspectionSortCameraValid && IsInspectionSortPassConveyorValid && IsInspectionSortNgConveyorValid && IsInspectionSortPassFeedbackValid && IsInspectionSortNgFeedbackValid;
    }

    private bool IsInspectionSortRouterSetupEquivalentToSaved(InspectionSortRouterDefinition setup) =>
        _savedInspectionSortRouterSetup is not null
        && string.Equals(_savedInspectionSortRouterSetup.CameraId, setup.CameraId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionSortRouterSetup.PassConveyorComponentId, setup.PassConveyorComponentId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionSortRouterSetup.NgConveyorComponentId, setup.NgConveyorComponentId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionSortRouterSetup.PassRoutedFeedbackChannelId, setup.PassRoutedFeedbackChannelId, StringComparison.Ordinal)
        && string.Equals(_savedInspectionSortRouterSetup.NgRoutedFeedbackChannelId, setup.NgRoutedFeedbackChannelId, StringComparison.Ordinal);

    private void PreviewOhtHandoffSetup()
    {
        if (_project is null) return;
        ClearStationSkeletonPreview(); ClearLoadLockSetup(); ClearWaferHandlerSetup(); ClearPrealignerSetup(); ClearInspectionHandoffSetup(); ClearInspectionSortRouterSetup(); ClearProcessBlockPreview(); ClearCheckpointTemplatePreview();
        InspectionCameraOptions.Clear(); InspectionInputOptions.Clear(); InspectionOutputOptions.Clear(); InspectionConveyorOptions.Clear();
        PopulateInspectionChannelsAndCameras();
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Oht, OhtHandoff: not null }).ToArray();
        _savedOhtHandoffSetup = existing.Length == 1 ? CloneOhtHandoff(existing[0].OhtHandoff!) : null;
        var draft = _savedOhtHandoffSetup ?? SuggestOhtHandoffSetup();
        AddMissingSemanticOption(InspectionConveyorOptions, draft.TransportConveyorComponentId);
        foreach (var id in new[] { draft.RouteAvailableSensorChannelId, draft.VehicleDockedSensorChannelId, draft.LoadPortReadySensorChannelId, draft.CarrierReceivedSensorChannelId, draft.HandoffReadyFeedbackChannelId, draft.CarrierTransferredFeedbackChannelId }) AddMissingSemanticOption(InspectionInputOptions, id);
        ApplyOhtHandoffDraft(draft); _isOhtHandoffSetupVisible = true; RaiseSemanticSetupChanged();
    }

    private void ApplyOhtHandoffSetup()
    {
        ApplySemanticSetup(
            _applyOhtHandoffSetup,
            TryCreateOhtHandoffSetup,
            IsOhtHandoffSetupEquivalentToSaved,
            ClearOhtHandoffSetup,
            PreviewOhtHandoffSetup);
    }

    private void ResetOhtHandoffSetup() => ApplyOhtHandoffDraft(_savedOhtHandoffSetup is null ? SuggestOhtHandoffSetup() : CloneOhtHandoff(_savedOhtHandoffSetup));

    private void ClearOhtHandoffSetup()
    {
        _isOhtHandoffSetupVisible = false; _savedOhtHandoffSetup = null;
        RaiseSemanticSetupChanged();
    }

    private OhtHandoffDefinition SuggestOhtHandoffSetup() => new()
    {
        TransportConveyorComponentId = InspectionConveyorOptions.FirstOrDefault()?.Id ?? string.Empty,
        RouteAvailableSensorChannelId = InspectionInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        VehicleDockedSensorChannelId = InspectionInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        LoadPortReadySensorChannelId = InspectionInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        CarrierReceivedSensorChannelId = InspectionInputOptions.ElementAtOrDefault(3)?.Id ?? string.Empty,
        HandoffReadyFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(4)?.Id ?? string.Empty,
        CarrierTransferredFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(5)?.Id ?? string.Empty
    };

    private void ApplyOhtHandoffDraft(OhtHandoffDefinition setup)
    {
        _ohtTransportConveyorId = setup.TransportConveyorComponentId; _ohtRouteAvailableChannelId = setup.RouteAvailableSensorChannelId; _ohtVehicleDockedChannelId = setup.VehicleDockedSensorChannelId; _ohtLoadPortReadyChannelId = setup.LoadPortReadySensorChannelId; _ohtCarrierReceivedChannelId = setup.CarrierReceivedSensorChannelId; _ohtHandoffReadyChannelId = setup.HandoffReadyFeedbackChannelId; _ohtCarrierTransferredChannelId = setup.CarrierTransferredFeedbackChannelId; RaiseSemanticSetupChanged();
    }

    private bool TryCreateOhtHandoffSetup(out OhtHandoffDefinition setup)
    {
        var ids = new[] { OhtRouteAvailableChannelId, OhtVehicleDockedChannelId, OhtLoadPortReadyChannelId, OhtCarrierReceivedChannelId, OhtHandoffReadyChannelId, OhtCarrierTransferredChannelId };
        setup = new OhtHandoffDefinition { TransportConveyorComponentId = OhtTransportConveyorId ?? string.Empty, RouteAvailableSensorChannelId = OhtRouteAvailableChannelId ?? string.Empty, VehicleDockedSensorChannelId = OhtVehicleDockedChannelId ?? string.Empty, LoadPortReadySensorChannelId = OhtLoadPortReadyChannelId ?? string.Empty, CarrierReceivedSensorChannelId = OhtCarrierReceivedChannelId ?? string.Empty, HandoffReadyFeedbackChannelId = OhtHandoffReadyChannelId ?? string.Empty, CarrierTransferredFeedbackChannelId = OhtCarrierTransferredChannelId ?? string.Empty };
        return !HasMultipleOhtHandoffs && IsOhtTransportConveyorValid && ids.All(id => !string.IsNullOrWhiteSpace(id)) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length && IsOhtRouteAvailableValid && IsOhtVehicleDockedValid && IsOhtLoadPortReadyValid && IsOhtCarrierReceivedValid && IsOhtHandoffReadyValid && IsOhtCarrierTransferredValid;
    }

    private bool IsOhtHandoffSetupEquivalentToSaved(OhtHandoffDefinition setup) =>
        _savedOhtHandoffSetup is not null
        && string.Equals(_savedOhtHandoffSetup.TransportConveyorComponentId, setup.TransportConveyorComponentId, StringComparison.Ordinal)
        && string.Equals(_savedOhtHandoffSetup.RouteAvailableSensorChannelId, setup.RouteAvailableSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedOhtHandoffSetup.VehicleDockedSensorChannelId, setup.VehicleDockedSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedOhtHandoffSetup.LoadPortReadySensorChannelId, setup.LoadPortReadySensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedOhtHandoffSetup.CarrierReceivedSensorChannelId, setup.CarrierReceivedSensorChannelId, StringComparison.Ordinal)
        && string.Equals(_savedOhtHandoffSetup.HandoffReadyFeedbackChannelId, setup.HandoffReadyFeedbackChannelId, StringComparison.Ordinal)
        && string.Equals(_savedOhtHandoffSetup.CarrierTransferredFeedbackChannelId, setup.CarrierTransferredFeedbackChannelId, StringComparison.Ordinal);

    private void PopulateInspectionChannelsAndCameras()
    {
        if (_project is null) return;
        foreach (var camera in _project.Devices.Where(device => device is { Kind: DeviceKind.Camera, Camera: not null }).OrderBy(device => device.Name, StringComparer.CurrentCulture).ThenBy(device => device.Id, StringComparer.Ordinal)) InspectionCameraOptions.Add(new LoadLockSetupOption(camera.Id, DisplayName(camera.Name, camera.Id)));
        foreach (var component in (ResolveActiveLayout(_project)?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Conveyor).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) InspectionConveyorOptions.Add(new LoadLockSetupOption(component.Id, DisplayName(component.Name, component.Id)));
        foreach (var channel in _project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalInput).OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal)) InspectionInputOptions.Add(new LoadLockSetupOption(channel.Id, DisplayName(channel.Name, channel.Id)));
        foreach (var channel in _project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalOutput).OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal)) InspectionOutputOptions.Add(new LoadLockSetupOption(channel.Id, DisplayName(channel.Name, channel.Id)));
    }

    private void OpenProcessBlockPlan()
    {
        IReadOnlyList<SemiconductorProcessBlockKind> existingKinds = _project is null
            ? []
            : _processBlockComposer.RecognizeExistingKinds(_project);
        SetProcessBlockSelections(existingKinds.Count > 0
            ? existingKinds
            : Enum.GetValues<SemiconductorProcessBlockKind>());
        PreviewProcessBlockPlan();
    }

    private void PreviewProcessBlockPlan()
    {
        if (_project is null)
        {
            return;
        }
        ClearStationSkeletonPreview();
        ClearCheckpointTemplatePreview();
        ClearLoadLockSetup();
        var selectedStepId = SelectedProcessBlockItem?.StepId;
        _processBlockPreview = _processBlockComposer.Preview(_project, SelectedProcessBlockKinds());
        ProcessBlockConnectionItems.Clear();
        foreach (var entry in _processBlockPreview.Station.Entries.Where(entry =>
                     entry.Status != SemiconductorStationSkeletonStatus.Existing))
        {
            ProcessBlockConnectionItems.Add(CreateStationSkeletonItem(entry));
        }
        ProcessBlockItems.Clear();
        var sequence = ResolveRecipeSequence();
        var sequenceId = sequence?.Id;
        foreach (var entry in _processBlockPreview.Steps)
        {
            var statusText = entry.Status switch
            {
                SemiconductorProcessBlockStepStatus.Proposed => OpenVisionLanguageService.T("Connections.ProcessBlockStepProposed"),
                SemiconductorProcessBlockStepStatus.Existing => OpenVisionLanguageService.T("Connections.ProcessBlockStepExisting"),
                SemiconductorProcessBlockStepStatus.Customized => OpenVisionLanguageService.T("Connections.ProcessBlockStepCustomized"),
                SemiconductorProcessBlockStepStatus.ProposedRemoval => OpenVisionLanguageService.T("Connections.ProcessBlockStepProposedRemoval"),
                _ => OpenVisionLanguageService.T("Connections.ProcessBlockStepUnavailable")
            };
            var currentStep = sequence?.Steps.FirstOrDefault(step => string.Equals(
                step.Id,
                entry.StepId,
                StringComparison.Ordinal));
            var currentValue = string.IsNullOrWhiteSpace(currentStep?.Parameter) ? "—" : currentStep.Parameter;
            var templateValue = string.IsNullOrWhiteSpace(entry.Parameter) ? "—" : entry.Parameter;
            var currentTimeout = currentStep?.TimeoutMs.ToString("N0", CultureInfo.CurrentCulture);
            var templateTimeout = entry.TimeoutMs.ToString("N0", CultureInfo.CurrentCulture);
            var detailText = currentStep is null
                ? $"{statusText} · {entry.Action} · {entry.TargetId}"
                : $"{statusText} · {OpenVisionLanguageService.T("Sequence.Action")}: "
                  + $"{WithTemplateDifference(currentStep.Action.ToString(), entry.Action.ToString())} · "
                  + $"{OpenVisionLanguageService.T("Sequence.Target")}: {WithTemplateDifference(currentStep.TargetId, entry.TargetId)} · "
                  + $"{OpenVisionLanguageService.T("Sequence.Value")}: "
                  + $"{WithTemplateDifference(currentValue, templateValue)} · "
                  + $"{OpenVisionLanguageService.T("Sequence.Timeout")}: "
                  + $"{WithTemplateDifference($"{currentTimeout} ms", $"{templateTimeout} ms")}";
            ProcessBlockItems.Add(new SemiconductorProcessBlockItemPresentation(
                sequenceId,
                entry.StepId,
                OpenVisionLanguageService.T($"Connections.ProcessBlockStep.{entry.StepId}"),
                detailText,
                currentStep?.Action,
                currentStep?.TimeoutMs,
                entry.Status == SemiconductorProcessBlockStepStatus.Proposed,
                entry.Status == SemiconductorProcessBlockStepStatus.Existing,
                entry.Status == SemiconductorProcessBlockStepStatus.Customized,
                entry.Status == SemiconductorProcessBlockStepStatus.ProposedRemoval,
                entry.Status == SemiconductorProcessBlockStepStatus.Unavailable));
        }
        SelectedProcessBlockItem = ProcessBlockItems.FirstOrDefault(item => string.Equals(
            item.StepId,
            selectedStepId,
            StringComparison.Ordinal));
        RefreshVisibleProcessBlockItems();
        RaiseProcessBlockChanged();
    }

    private void SetProcessBlockItemFilter(ProcessBlockItemFilter filter)
    {
        if (_processBlockItemFilter == filter)
        {
            return;
        }
        _processBlockItemFilter = filter;
        ClearProcessBlockTimeoutPreview();
        RefreshVisibleProcessBlockItems();
        OnPropertyChanged(nameof(IsProcessBlockFilterAll));
        OnPropertyChanged(nameof(IsProcessBlockFilterCustomized));
        OnPropertyChanged(nameof(IsProcessBlockFilterRemoval));
        OnPropertyChanged(nameof(IsProcessBlockFilterConflict));
    }

    private void RefreshVisibleProcessBlockItems()
    {
        var selectedStepId = SelectedProcessBlockItem?.StepId;
        VisibleProcessBlockItems.Clear();
        foreach (var item in ProcessBlockItems.Where(item => _processBlockItemFilter switch
                 {
                     ProcessBlockItemFilter.Customized => item.IsCustomized,
                     ProcessBlockItemFilter.Removal => item.IsProposedRemoval,
                     ProcessBlockItemFilter.Conflict => item.IsUnavailable,
                     _ => true
                 }))
        {
            VisibleProcessBlockItems.Add(item);
        }
        SelectedProcessBlockItem = VisibleProcessBlockItems.FirstOrDefault(item => string.Equals(
            item.StepId,
            selectedStepId,
            StringComparison.Ordinal));
        OnPropertyChanged(nameof(ProcessBlockFilterAllText));
        OnPropertyChanged(nameof(ProcessBlockFilterCustomizedText));
        OnPropertyChanged(nameof(ProcessBlockFilterRemovalText));
        OnPropertyChanged(nameof(ProcessBlockFilterConflictText));
        OnPropertyChanged(nameof(HasVisibleProcessBlockItems));
        OnPropertyChanged(nameof(CompatibleProcessBlockTimeoutCount));
        OnPropertyChanged(nameof(ProcessBlockTimeoutScopeText));
        OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
        _previewProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
    }

    private string FormatProcessBlockFilter(string labelKey, int count) => Format(
        "Connections.ProcessBlockFilterCountFormat",
        OpenVisionLanguageService.T(labelKey),
        count);

    private void SetProcessBlockSelection(ref bool field, bool value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }
        if (IsProcessBlockPreviewVisible)
        {
            PreviewProcessBlockPlan();
        }
    }

    private void SetProcessBlockSelections(IEnumerable<SemiconductorProcessBlockKind> kinds)
    {
        var selected = kinds.ToHashSet();
        _isLoadBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Load);
        _isAlignBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Align);
        _isProcessBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Process);
        _isInspectBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Inspect);
        _isUnloadBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Unload);
        OnPropertyChanged(nameof(IsLoadBlockSelected));
        OnPropertyChanged(nameof(IsAlignBlockSelected));
        OnPropertyChanged(nameof(IsProcessBlockSelected));
        OnPropertyChanged(nameof(IsInspectBlockSelected));
        OnPropertyChanged(nameof(IsUnloadBlockSelected));
    }

    private IReadOnlyList<SemiconductorProcessBlockKind> SelectedProcessBlockKinds()
    {
        var kinds = new List<SemiconductorProcessBlockKind>(5);
        if (IsLoadBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Load);
        if (IsAlignBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Align);
        if (IsProcessBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Process);
        if (IsInspectBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Inspect);
        if (IsUnloadBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Unload);
        return kinds;
    }

    private void PreviewProcessBlockTimeouts()
    {
        if (_project is null
            || !int.TryParse(
                ProcessBlockTimeoutText,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out var timeout)
            || timeout < 0)
        {
            return;
        }

        _processBlockTimeoutPreview = _processBlockComposer.PreviewTimeoutAdjustment(
            _project,
            VisibleProcessBlockItems
                .Where(item => item.CanAdjustTimeout)
                .Select(item => item.StepId),
            timeout);
        ProcessBlockTimeoutItems.Clear();
        foreach (var entry in _processBlockTimeoutPreview.Entries)
        {
            ProcessBlockTimeoutItems.Add(new SemiconductorManagedTimeoutAdjustmentItemPresentation(
                OpenVisionLanguageService.T($"Connections.ProcessBlockStep.{entry.StepId}"),
                Format(
                    "Connections.ProcessBlockTimeoutItemFormat",
                    entry.Action,
                    entry.TargetId,
                    entry.CurrentTimeoutMs,
                    entry.ProposedTimeoutMs)));
        }
        RaiseProcessBlockTimeoutChanged();
    }

    private void ApplyProcessBlockTimeouts()
    {
        if (_processBlockTimeoutPreview is not { CanApply: true } preview)
        {
            return;
        }

        if (_applyProcessBlockTimeouts(preview) <= 0)
        {
            PreviewProcessBlockTimeouts();
        }
    }

    private void ClearProcessBlockTimeoutPreview()
    {
        if (_processBlockTimeoutPreview is null && ProcessBlockTimeoutItems.Count == 0)
        {
            return;
        }

        _processBlockTimeoutPreview = null;
        ProcessBlockTimeoutItems.Clear();
        RaiseProcessBlockTimeoutChanged();
    }

    private void RaiseProcessBlockTimeoutChanged()
    {
        OnPropertyChanged(nameof(IsProcessBlockTimeoutPreviewVisible));
        OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
        OnPropertyChanged(nameof(ProcessBlockTimeoutApplyText));
        _previewProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
        _applyProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
        _cancelProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
    }

    private void ApplyProcessBlock()
    {
        if (_processBlockPreview is not { CanApply: true } preview)
        {
            return;
        }
        _applyProcessBlock(preview.Kinds);
    }

    private void ClearProcessBlockPreview()
    {
        var wasVisible = IsProcessBlockPreviewVisible;
        _processBlockPreview = null;
        ClearProcessBlockTimeoutPreview();
        ProcessBlockConnectionItems.Clear();
        ProcessBlockItems.Clear();
        VisibleProcessBlockItems.Clear();
        SelectedProcessBlockItem = null;
        RaiseProcessBlockChanged();
        if (wasVisible && !_isPreservingProcessBlockPlan)
        {
            ProcessBlockPreviewClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelProcessBlockPreview() => ClearProcessBlockPreview();

    private static SemiconductorStationSkeletonItemPresentation CreateStationSkeletonItem(
        SemiconductorStationSkeletonEntry entry)
    {
        var roleText = OpenVisionLanguageService.T($"Connections.StationSkeletonRole.{entry.Role}");
        var target = entry.Role == SemiconductorStationSkeletonRole.RequiredIo
            ? Format("Connections.StationSkeletonIoFormat", entry.ExistingCount + entry.AddedCount)
            : entry.TargetId ?? "—";
        var detailText = entry.Status switch
        {
            SemiconductorStationSkeletonStatus.Proposed => Format(
                "Connections.StationSkeletonProposedFormat",
                target),
            SemiconductorStationSkeletonStatus.Existing => Format(
                "Connections.StationSkeletonExistingFormat",
                target),
            _ when entry.UnavailableReason
                == SemiconductorStationSkeletonUnavailableReason.ActiveLayoutConflict =>
                OpenVisionLanguageService.T("Connections.StationSkeletonLayoutConflict"),
            _ when entry.UnavailableReason
                == SemiconductorStationSkeletonUnavailableReason.AutomaticSequenceConflict =>
                OpenVisionLanguageService.T("Connections.StationSkeletonSequenceConflict"),
            _ => OpenVisionLanguageService.T("Connections.StationSkeletonUnavailable")
        };
        return new SemiconductorStationSkeletonItemPresentation(
            roleText,
            detailText,
            entry.Status == SemiconductorStationSkeletonStatus.Proposed,
            entry.Status == SemiconductorStationSkeletonStatus.Existing,
            entry.Status == SemiconductorStationSkeletonStatus.Unavailable);
    }

    private void PreviewCheckpointTemplate()
    {
        var sequenceId = ResolveRecipeSequenceId();
        if (_project is null || sequenceId is null)
        {
            return;
        }

        ClearStationSkeletonPreview();
        ClearProcessBlockPreview();
        ClearLoadLockSetup();

        _checkpointTemplatePreview = _checkpointTemplate.Preview(_project, sequenceId);
        CheckpointTemplateItems.Clear();
        foreach (var entry in _checkpointTemplatePreview.Entries)
        {
            CheckpointTemplateItems.Add(CreateCheckpointTemplateItem(entry));
        }
        RaiseCheckpointTemplateChanged();
    }

    private void ApplyCheckpointTemplate()
    {
        if (_project is null || _checkpointTemplatePreview is null)
        {
            return;
        }

        var selectedComponentId = SelectedRow?.ComponentId;
        var applied = _checkpointTemplate.Apply(_project, _checkpointTemplatePreview);
        _checkpointTemplateApplied(applied);
        Load(_project, selectedComponentId);
    }

    private void ClearCheckpointTemplatePreview()
    {
        _checkpointTemplatePreview = null;
        CheckpointTemplateItems.Clear();
        RaiseCheckpointTemplateChanged();
    }

    private static RecipeCheckpointTemplateItemPresentation CreateCheckpointTemplateItem(
        RepresentativeCheckpointTemplateEntry entry)
    {
        var roleText = OpenVisionLanguageService.T(
            $"Connections.CheckpointTemplateRole.{entry.Role}");
        var detailText = entry.Status switch
        {
            RepresentativeCheckpointTemplateStatus.Proposed => Format(
                "Connections.CheckpointTemplateProposedFormat",
                entry.StepName ?? entry.StepId ?? "—",
                entry.ExpectedTargetId ?? "—",
                entry.ExpectedState ?? "—"),
            RepresentativeCheckpointTemplateStatus.AlreadyConfigured => Format(
                "Connections.CheckpointTemplateExistingFormat",
                entry.StepName ?? entry.StepId ?? "—",
                entry.ExpectedTargetId ?? "—",
                entry.ExpectedState ?? "—"),
            _ when entry.UnavailableReason
                == RepresentativeCheckpointUnavailableReason.StepAlreadyHasCheckpoint => Format(
                    "Connections.CheckpointTemplateConflictFormat",
                    entry.StepName ?? entry.StepId ?? "—"),
            _ => OpenVisionLanguageService.T("Connections.CheckpointTemplateUnavailable")
        };
        return new RecipeCheckpointTemplateItemPresentation(
            roleText,
            detailText,
            entry.Status == RepresentativeCheckpointTemplateStatus.Proposed,
            entry.Status == RepresentativeCheckpointTemplateStatus.AlreadyConfigured,
            entry.Status == RepresentativeCheckpointTemplateStatus.Unavailable);
    }

    private async Task PreviewSequenceStepAsync(object? parameter)
    {
        if (parameter is not RecipeConnectionRowViewModel
            {
                FirstSequenceId: { } sequenceId,
                FirstSequenceStepId: { } stepId
            } row)
        {
            return;
        }

        var result = await _previewSequenceStep(sequenceId, stepId, row.ComponentId);
        if (!Rows.Contains(row) || _readinessPassed != true)
        {
            return;
        }

        row.ApplyPreview(result, BuildObservation(row, result));
    }

    private async Task RunRecipeDryRunAsync(object? parameter)
    {
        var sequenceId = ResolveRecipeSequenceId();
        if (sequenceId is null)
        {
            return;
        }

        var revision = _definitionRevision;
        IsRecipeDryRunRunning = true;
        try
        {
            var result = await _runRecipeDryRun(sequenceId);
            if (revision != _definitionRevision || _readinessPassed != true)
            {
                return;
            }

            ApplyRecipeDryRun(result);
        }
        finally
        {
            IsRecipeDryRunRunning = false;
        }
    }

    private static RecipeConnectionRowViewModel CreateRow(
        MachineProjectDocument project,
        LayoutComponentDefinition component,
        IReadOnlyDictionary<string, string> componentNames,
        MachineProjectLayoutValidationResult validation)
    {
        var device = project.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
        var targetIds = new HashSet<string>(StringComparer.Ordinal) { component.Id };
        if (!string.IsNullOrWhiteSpace(component.BehaviorBindingId))
        {
            targetIds.Add(component.BehaviorBindingId);
        }

        var behaviorText = OpenVisionLanguageService.T("Connections.None");
        var connectionText = OpenVisionLanguageService.T("Connections.NotApplicable");
        string? sequenceTargetId = null;
        var isConnected = false;

        switch (component.Kind)
        {
            case LayoutComponentKind.LinearStage:
            case LayoutComponentKind.RotaryStage:
            {
                var axis = project.Axes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, component.BehaviorBindingId, StringComparison.Ordinal));
                behaviorText = axis is null
                    ? OpenVisionLanguageService.T("Connections.MissingAxis")
                    : DisplayName(axis.Name, axis.Id);
                connectionText = Format("Connections.StageLinkFormat", component.Id, axis?.Id ?? "—");
                if (axis is not null)
                {
                    targetIds.Add(axis.Id);
                    sequenceTargetId = axis.Id;
                    isConnected = true;
                }
                break;
            }
            case LayoutComponentKind.DigitalSensor:
            {
                var sensor = device?.Sensor;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.SensorLinkFormat",
                    sensor?.OutputChannelId ?? "—",
                    ResolveName(componentNames, sensor?.TargetComponentId));
                AddTarget(targetIds, sensor?.OutputChannelId);
                AddTarget(targetIds, sensor?.TargetComponentId);
                sequenceTargetId = sensor?.OutputChannelId;
                isConnected = sensor is not null;
                break;
            }
            case LayoutComponentKind.PneumaticCylinder:
            {
                var cylinder = device?.Cylinder;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.CylinderLinkFormat",
                    cylinder?.ExtendCommandChannelId ?? "—",
                    cylinder?.ExtendedSensorChannelId ?? "—",
                    cylinder?.RetractedSensorChannelId ?? "—");
                AddTarget(targetIds, cylinder?.ExtendCommandChannelId);
                AddTarget(targetIds, cylinder?.ExtendedSensorChannelId);
                AddTarget(targetIds, cylinder?.RetractedSensorChannelId);
                sequenceTargetId = cylinder?.ExtendCommandChannelId;
                isConnected = cylinder is not null;
                break;
            }
            case LayoutComponentKind.Conveyor:
            {
                var conveyor = device?.Conveyor;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.ConveyorLinkFormat",
                    conveyor?.RunCommandChannelId ?? "—",
                    conveyor?.ReverseCommandChannelId ?? "—");
                AddTarget(targetIds, conveyor?.RunCommandChannelId);
                AddTarget(targetIds, conveyor?.ReverseCommandChannelId);
                sequenceTargetId = conveyor?.RunCommandChannelId;
                isConnected = conveyor is not null;
                break;
            }
            case LayoutComponentKind.Workpiece:
            {
                var workpiece = device?.Workpiece;
                behaviorText = device is null
                    ? OpenVisionLanguageService.T("Connections.MissingDevice")
                    : DisplayName(device.Name, device.Id);
                connectionText = Format(
                    "Connections.WorkpieceLinkFormat",
                    ResolveName(componentNames, workpiece?.ConveyorComponentId));
                AddTarget(targetIds, workpiece?.ConveyorComponentId);
                isConnected = workpiece is not null;
                break;
            }
            case LayoutComponentKind.MachineFrame:
                behaviorText = OpenVisionLanguageService.T("Connections.StaticComponent");
                break;
        }

        var sequenceUses = project.Sequences
            .SelectMany(sequence => sequence.Steps.Select(step => (Sequence: sequence, Step: step)))
            .Where(item => targetIds.Contains(item.Step.TargetId))
            .ToArray();
        var sequenceText = sequenceUses.Length == 0
            ? OpenVisionLanguageService.T("Connections.NoSequenceUse")
            : string.Join(", ", sequenceUses.Take(3).Select(item => item.Step.Name)) +
              (sequenceUses.Length > 3 ? $" (+{sequenceUses.Length - 3})" : string.Empty);
        var errors = validation.Errors
            .Where(error => string.Equals(error.ComponentId, component.Id, StringComparison.Ordinal))
            .ToArray();

        return new RecipeConnectionRowViewModel
        {
            ComponentId = component.Id,
            Name = component.Name,
            Kind = component.Kind,
            KindText = OpenVisionLanguageService.T(
                $"Properties.Value.{component.Kind}",
                component.Kind.ToString(),
                component.Kind.ToString()),
            BehaviorText = behaviorText,
            ConnectionText = connectionText,
            SequenceText = sequenceText,
            SequenceUseCount = sequenceUses.Length,
            FirstSequenceId = sequenceUses.FirstOrDefault().Sequence?.Id,
            FirstSequenceStepId = sequenceUses.FirstOrDefault().Step?.Id,
            FirstSequenceAction = sequenceUses.FirstOrDefault().Step?.Action,
            SequenceTargetId = sequenceTargetId,
            RelatedTargetIds = targetIds,
            IsConnected = isConnected && errors.Length == 0,
            IsValid = errors.Length == 0,
            ValidationText = errors.Length == 0
                ? OpenVisionLanguageService.T("Connections.Valid")
                : errors[0].Message
        };
    }

    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project)
    {
        if (!string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId))
        {
            return project.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, project.Simulation.ActiveLayoutId, StringComparison.Ordinal));
        }

        return project.Layouts.Count == 1 ? project.Layouts[0] : null;
    }

    private static string ResolveName(IReadOnlyDictionary<string, string> names, string? id) =>
        !string.IsNullOrWhiteSpace(id) && names.TryGetValue(id, out var name) ? name : id ?? "—";

    private static void AddTarget(ISet<string> targets, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            targets.Add(id);
        }
    }

    private static string DisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}";

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

    private void ApplyRecipeDryRun(RecipeDryRunResult result)
    {
        RecipeDryRunResult = result;
        RecipeDryRunStatusText = OpenVisionLanguageService.T(result.Outcome switch
        {
            RecipeDryRunOutcome.Completed => "Connections.DryRunCompleted",
            RecipeDryRunOutcome.CompletedWithIssue => "Connections.DryRunCompletedWithIssue",
            RecipeDryRunOutcome.CompletedWithMismatch => "Connections.DryRunCompletedWithMismatch",
            RecipeDryRunOutcome.LimitReached => "Connections.DryRunLimitReached",
            RecipeDryRunOutcome.Faulted => "Connections.DryRunFaulted",
            _ => "Connections.DryRunRejected"
        });
        RecipeDryRunDetailText = Format(
            "Connections.DryRunDetailFormat",
            string.IsNullOrWhiteSpace(result.SequenceName) ? result.SequenceId : result.SequenceName,
            result.ExecutedTicks,
            result.MaximumTicks,
            result.Timeline.Count);
        RecipeDryRunIssueText = result.FirstIssue is not null
            ? Format(
                "Connections.DryRunIssueFormat",
                result.FirstIssue.StepId,
                result.FirstIssue.Tick,
                result.FirstIssue.Code,
                result.FirstIssue.Detail)
            : result.FirstCheckpointMismatch is { } mismatch
                ? Format(
                    "Connections.DryRunMismatchFormat",
                    mismatch.StepId,
                    mismatch.Tick,
                    mismatch.TargetId,
                    mismatch.ExpectedState,
                    mismatch.ActualState)
                : OpenVisionLanguageService.T("Connections.DryRunNoIssue");

        RecipeDryRunTimeline.Clear();
        _selectedRecipeDryRunStep = null;
        OnPropertyChanged(nameof(SelectedRecipeDryRunStep));
        var sequence = _project?.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, result.SequenceId, StringComparison.Ordinal));
        for (var index = 0; index < result.Timeline.Count; index++)
        {
            var trace = result.Timeline[index];
            var authoredStep = sequence?.Steps.FirstOrDefault(step =>
                string.Equals(step.Id, trace.StepId, StringComparison.Ordinal));
            string? relatedTargetId = trace.Checkpoint?.TargetId ?? authoredStep?.TargetId;
            var componentId = Rows.FirstOrDefault(row =>
                relatedTargetId is not null && row.RelatedTargetIds.Contains(relatedTargetId))?.ComponentId;
            RecipeDryRunTimeline.Add(new RecipeDryRunStepPresentation(
                result.SequenceId,
                trace.StepId,
                componentId,
                $"#{index + 1}",
                trace.Name,
                Format("Connections.DryRunTickRangeFormat", trace.StartedTick, trace.EndedTick),
                trace.HasIssue,
                trace.HasCheckpoint,
                trace.HasCheckpointMismatch,
                trace.Checkpoint is null
                    ? string.Empty
                    : Format(
                        "Connections.DryRunCheckpointFormat",
                        trace.Checkpoint.TargetId,
                        trace.Checkpoint.ExpectedState,
                        trace.Checkpoint.ActualState),
                trace.BoundarySnapshot));
        }
        SelectedRecipeDryRunStep = RecipeDryRunTimeline.FirstOrDefault(step => step.HasIssue)
            ?? RecipeDryRunTimeline.FirstOrDefault(step => step.HasCheckpointMismatch);

        RecipeDryRunFinalStates.Clear();
        if (result.FinalSnapshot is { } snapshot)
        {
            foreach (var prealigner in snapshot.Prealigners)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatPrealignerStatus(prealigner),
                    IsPrealigner: true,
                    IsFault: prealigner.State == PrealignerState.InterlockFault));
            }

            foreach (var handoff in snapshot.InspectionHandoffs)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatInspectionHandoffStatus(handoff),
                    IsInspectionHandoff: true,
                    IsFault: handoff.State == InspectionHandoffState.InterlockFault));
            }

            foreach (var handoff in snapshot.OhtHandoffs)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatOhtHandoffStatus(handoff),
                    IsOhtHandoff: true,
                    IsFault: handoff.State == OhtHandoffOwnershipState.InterlockFault));
            }

            foreach (var sorter in snapshot.InspectionSortRouters)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatInspectionSorterStatus(sorter),
                    IsInspectionSorter: true,
                    IsFault: sorter.State == InspectionSortRouteState.InterlockFault));
            }

            foreach (var handler in snapshot.WaferHandlers)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatWaferHandlerStatus(handler),
                    IsWaferHandler: true,
                    IsFault: handler.State == WaferHandlerOwnershipState.InterlockFault));
            }

            foreach (var loadLock in snapshot.LoadLocks)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatLoadLockStatus(loadLock),
                    IsLoadLock: true,
                    IsFault: loadLock.State == LoadLockState.InterlockFault));
            }

            foreach (var axis in snapshot.Axes)
            {
                RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                    "Connections.DryRunAxisStateFormat",
                    axis.Name,
                    axis.Position,
                    axis.State)));
            }

            foreach (var component in snapshot.LayoutComponents)
            {
                switch (component.Kind)
                {
                    case LayoutComponentKind.PneumaticCylinder when component.CylinderState is { } cylinder:
                        RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunCylinderStateFormat",
                            component.Name,
                            cylinder,
                            component.MotionProgress ?? 0)));
                        break;
                    case LayoutComponentKind.Conveyor when component.ConveyorRunning is { } running:
                        RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunConveyorStateFormat",
                            component.Name,
                            running ? "ON" : "OFF",
                            component.ConveyorDirection?.ToString() ?? "—")));
                        break;
                    case LayoutComponentKind.DigitalSensor when component.IsDetected is { } detected:
                        RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunSensorStateFormat",
                            component.Name,
                            detected ? "ON" : "OFF")));
                        break;
                    case LayoutComponentKind.Workpiece:
                        RecipeDryRunFinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunWorkpieceStateFormat",
                            component.Name,
                            component.X,
                            component.Y)));
                        break;
                }
            }
        }

        OnPropertyChanged(nameof(HasRecipeDryRunResult));
        OnPropertyChanged(nameof(RecipeDryRunPassed));
        OnPropertyChanged(nameof(RecipeDryRunWarning));
        OnPropertyChanged(nameof(HasRecipeDryRunIssue));
    }

    private void ClearRecipeDryRun()
    {
        RecipeDryRunResult = null;
        RecipeDryRunStatusText = string.Empty;
        RecipeDryRunDetailText = string.Empty;
        RecipeDryRunIssueText = string.Empty;
        _selectedRecipeDryRunStep = null;
        OnPropertyChanged(nameof(SelectedRecipeDryRunStep));
        RecipeDryRunTimeline.Clear();
        RecipeDryRunFinalStates.Clear();
        OnPropertyChanged(nameof(HasRecipeDryRunResult));
        OnPropertyChanged(nameof(RecipeDryRunPassed));
        OnPropertyChanged(nameof(RecipeDryRunWarning));
        OnPropertyChanged(nameof(HasRecipeDryRunIssue));
    }

    private static string BuildObservation(
        RecipeConnectionRowViewModel row,
        SequenceStepPreviewResult result)
    {
        var snapshot = result.FinalSnapshot;
        if (snapshot is null)
        {
            return result.Detail;
        }

        var axis = snapshot.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, result.TargetId, StringComparison.Ordinal));
        if (axis is not null)
        {
            return Format("Connections.PreviewAxisFormat", axis.Position, axis.State);
        }

        var signal = snapshot.Signals.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, result.TargetId, StringComparison.Ordinal));
        var component = snapshot.LayoutComponents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, row.ComponentId, StringComparison.Ordinal));
        if (component?.CylinderState is { } cylinderState)
        {
            return Format(
                "Connections.PreviewCylinderFormat",
                signal?.Value == true ? "ON" : "OFF",
                cylinderState,
                component.MotionProgress ?? 0);
        }

        if (component?.ConveyorRunning is { } conveyorRunning)
        {
            return Format(
                "Connections.PreviewConveyorFormat",
                conveyorRunning ? "ON" : "OFF",
                component.ConveyorDirection?.ToString() ?? "—");
        }

        if (component?.IsDetected is { } detected)
        {
            return Format("Connections.PreviewSensorFormat", detected ? "ON" : "OFF");
        }

        return signal is null
            ? result.Detail
            : Format("Connections.PreviewSignalFormat", signal.Id, signal.Value ? "ON" : "OFF");
    }

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);

    internal static string FormatLoadLockStatus(LoadLockSnapshot snapshot) => Format(
        "Connections.DryRunLoadLockStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.LoadLockState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.IsVacuumReady
            ? "Connections.LoadLockReady"
            : "Connections.LoadLockNotReady"),
        OpenVisionLanguageService.T(snapshot.IsAtmosphereReady
            ? "Connections.LoadLockReady"
            : "Connections.LoadLockNotReady"),
        OpenVisionLanguageService.T(snapshot.IsOuterDoorPermitted
            ? "Connections.LoadLockDoorAllowed"
            : "Connections.LoadLockDoorBlocked"),
        OpenVisionLanguageService.T(snapshot.IsInnerDoorPermitted
            ? "Connections.LoadLockDoorAllowed"
            : "Connections.LoadLockDoorBlocked"));

    internal static string FormatWaferHandlerStatus(WaferHandlerSnapshot snapshot) => Format(
        "Connections.DryRunWaferHandlerStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.WaferHandlerState.{snapshot.State}"),
        snapshot.HorizontalPosition,
        snapshot.VerticalPosition,
        OpenVisionLanguageService.T(snapshot.IsGateOpen
            ? "Connections.WaferHandlerGateOpen"
            : "Connections.WaferHandlerGateClosed"));

    internal static string FormatInspectionSorterStatus(InspectionSortRouterSnapshot snapshot) => Format(
        "Connections.DryRunInspectionSorterStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.InspectionSortState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.Decision is { } decision
            ? $"Connections.InspectionSortDecision.{decision}"
            : "Connections.InspectionSortDecision.Awaiting"));

    internal static string FormatInspectionHandoffStatus(InspectionHandoffSnapshot snapshot) => Format(
        "Connections.DryRunInspectionHandoffStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.InspectionHandoffState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.Decision is { } decision
            ? $"Connections.InspectionSortDecision.{decision}"
            : "Connections.InspectionSortDecision.Awaiting"),
        OpenVisionLanguageService.T(snapshot.IsMaterialPresent
            ? "Connections.InspectionMaterialPresent"
            : "Connections.InspectionMaterialAbsent"),
        OpenVisionLanguageService.T(snapshot.IsResultAccepted
            ? "Connections.InspectionResultAccepted"
            : "Connections.InspectionResultPending"));

    internal static string FormatOhtHandoffStatus(OhtHandoffSnapshot snapshot) => Format(
        "Connections.DryRunOhtHandoffStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.OhtHandoffState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.IsRouteAvailable
            ? "Connections.OhtRouteAvailable"
            : "Connections.OhtRouteBlocked"),
        OpenVisionLanguageService.T(snapshot.IsVehicleDocked
            ? "Connections.OhtVehicleDocked"
            : "Connections.OhtVehicleAway"),
        OpenVisionLanguageService.T(snapshot.IsLoadPortReady
            ? "Connections.OhtLoadPortReady"
            : "Connections.OhtLoadPortNotReady"));

    internal static string FormatPrealignerStatus(PrealignerSnapshot snapshot) => Format(
        "Connections.DryRunPrealignerStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.PrealignerState.{snapshot.State}"),
        snapshot.RotaryPositionDegrees,
        snapshot.AlignmentTargetDegrees,
        OpenVisionLanguageService.T(snapshot.IsWaferPresent
            ? "Connections.PrealignerWaferPresent"
            : "Connections.PrealignerWaferAbsent"));

    private static string WithTemplateDifference(string current, string template) =>
        string.Equals(current, template, StringComparison.Ordinal)
            ? current
            : $"{current} ({Format("Connections.ProcessBlockTemplateValueFormat", template)})";

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
        _previewStationSkeletonCommand.RaiseCanExecuteChanged();
        _previewLoadLockSetupCommand.RaiseCanExecuteChanged();
        _previewWaferHandlerSetupCommand.RaiseCanExecuteChanged();
        _previewPrealignerSetupCommand.RaiseCanExecuteChanged();
        _previewInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
        _previewInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
        _previewOhtHandoffSetupCommand.RaiseCanExecuteChanged();
        _previewProcessBlockCommand.RaiseCanExecuteChanged();
        _previewCheckpointTemplateCommand.RaiseCanExecuteChanged();
    }

    private void RaiseStationSkeletonChanged()
    {
        OnPropertyChanged(nameof(IsStationSkeletonPreviewVisible));
        OnPropertyChanged(nameof(StationSkeletonProposedCount));
        OnPropertyChanged(nameof(StationSkeletonSummaryText));
        OnPropertyChanged(nameof(StationSkeletonApplyText));
        _applyStationSkeletonCommand.RaiseCanExecuteChanged();
        _cancelStationSkeletonCommand.RaiseCanExecuteChanged();
        _resetStationSetupCommand.RaiseCanExecuteChanged();
    }

    private void RaiseLoadLockSetupChanged()
    {
        OnPropertyChanged(nameof(IsLoadLockSetupVisible));
        RaiseLoadLockSetupValidationChanged();
        _cancelLoadLockSetupCommand.RaiseCanExecuteChanged();
        _resetLoadLockSetupCommand.RaiseCanExecuteChanged();
    }

    private void RaiseLoadLockSetupValidationChanged()
    {
        OnPropertyChanged(nameof(OuterDoorComponentId));
        OnPropertyChanged(nameof(InnerDoorComponentId));
        OnPropertyChanged(nameof(EvacuateCommandChannelId));
        OnPropertyChanged(nameof(VentCommandChannelId));
        OnPropertyChanged(nameof(VacuumReadySensorChannelId));
        OnPropertyChanged(nameof(AtmosphereReadySensorChannelId));
        OnPropertyChanged(nameof(PumpDownDurationText));
        OnPropertyChanged(nameof(VentDurationText));
        OnPropertyChanged(nameof(IsOuterDoorComponentValid));
        OnPropertyChanged(nameof(IsInnerDoorComponentValid));
        OnPropertyChanged(nameof(IsEvacuateCommandChannelValid));
        OnPropertyChanged(nameof(IsVentCommandChannelValid));
        OnPropertyChanged(nameof(IsVacuumReadySensorChannelValid));
        OnPropertyChanged(nameof(IsAtmosphereReadySensorChannelValid));
        OnPropertyChanged(nameof(IsPumpDownDurationValid));
        OnPropertyChanged(nameof(IsVentDurationValid));
        OnPropertyChanged(nameof(HasMultipleLoadLocks));
        OnPropertyChanged(nameof(HasLoadLockSetupValidationError));
        OnPropertyChanged(nameof(LoadLockSetupValidationText));
        _applyLoadLockSetupCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSemanticSetupChanged()
    {
        OnPropertyChanged(nameof(IsWaferHandlerSetupVisible));
        OnPropertyChanged(nameof(IsPrealignerSetupVisible));
        OnPropertyChanged(nameof(IsInspectionHandoffSetupVisible));
        OnPropertyChanged(nameof(IsInspectionSortRouterSetupVisible));
        OnPropertyChanged(nameof(IsOhtHandoffSetupVisible));
        RaiseSemanticSetupValidationChanged();
        _cancelWaferHandlerSetupCommand.RaiseCanExecuteChanged();
        _resetWaferHandlerSetupCommand.RaiseCanExecuteChanged();
        _cancelPrealignerSetupCommand.RaiseCanExecuteChanged();
        _resetPrealignerSetupCommand.RaiseCanExecuteChanged();
        _cancelInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
        _resetInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
        _cancelInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
        _resetInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
        _cancelOhtHandoffSetupCommand.RaiseCanExecuteChanged();
        _resetOhtHandoffSetupCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSemanticSetupValidationChanged()
    {
        foreach (var property in new[]
        {
            nameof(WaferHandlerHorizontalAxisId), nameof(WaferHandlerVerticalAxisId), nameof(WaferHandlerWorkpieceComponentId), nameof(WaferHandlerSourcePresentSensorChannelId), nameof(WaferHandlerGateOpenSensorChannelId), nameof(WaferHandlerPickCommandChannelId), nameof(WaferHandlerPlaceCommandChannelId), nameof(WaferHandlerHoldingFeedbackChannelId), nameof(WaferHandlerPlacedFeedbackChannelId), nameof(WaferHandlerPickHorizontalText), nameof(WaferHandlerPickVerticalText), nameof(WaferHandlerPlaceHorizontalText), nameof(WaferHandlerPlaceVerticalText), nameof(IsWaferHandlerHorizontalAxisValid), nameof(IsWaferHandlerVerticalAxisValid), nameof(IsWaferHandlerWorkpieceValid), nameof(IsWaferHandlerSourcePresentValid), nameof(IsWaferHandlerGateOpenValid), nameof(IsWaferHandlerPickCommandValid), nameof(IsWaferHandlerPlaceCommandValid), nameof(IsWaferHandlerHoldingFeedbackValid), nameof(IsWaferHandlerPlacedFeedbackValid), nameof(IsWaferHandlerPickHorizontalValid), nameof(IsWaferHandlerPickVerticalValid), nameof(IsWaferHandlerPlaceHorizontalValid), nameof(IsWaferHandlerPlaceVerticalValid), nameof(HasMultipleWaferHandlers), nameof(HasWaferHandlerSetupValidationError), nameof(WaferHandlerSetupValidationText), nameof(PrealignerRotaryStageComponentId), nameof(PrealignerClampCylinderComponentId), nameof(PrealignerWaferPresentSensorChannelId), nameof(PrealignerAlignmentAcceptedCommandChannelId), nameof(PrealignerAlignmentReadyFeedbackChannelId), nameof(PrealignerAlignmentCompleteFeedbackChannelId), nameof(PrealignerAlignmentTargetText), nameof(PrealignerAlignmentToleranceText), nameof(IsPrealignerRotaryStageValid), nameof(IsPrealignerClampCylinderValid), nameof(IsPrealignerWaferPresentValid), nameof(IsPrealignerAlignmentAcceptedValid), nameof(IsPrealignerAlignmentReadyValid), nameof(IsPrealignerAlignmentCompleteValid), nameof(IsPrealignerAlignmentTargetValid), nameof(IsPrealignerAlignmentToleranceValid), nameof(HasMultiplePrealigners), nameof(HasPrealignerSetupValidationError), nameof(PrealignerSetupValidationText), nameof(InspectionHandoffCameraId), nameof(InspectionHandoffPositionSensorChannelId), nameof(InspectionHandoffAcceptedChannelId), nameof(InspectionHandoffReadyChannelId), nameof(InspectionHandoffCompleteChannelId), nameof(IsInspectionHandoffCameraValid), nameof(IsInspectionHandoffPositionValid), nameof(IsInspectionHandoffAcceptedValid), nameof(IsInspectionHandoffReadyValid), nameof(IsInspectionHandoffCompleteValid), nameof(HasMultipleInspectionHandoffs), nameof(HasInspectionHandoffSetupValidationError), nameof(InspectionHandoffSetupValidationText), nameof(InspectionSortCameraId), nameof(InspectionSortPassConveyorId), nameof(InspectionSortNgConveyorId), nameof(InspectionSortPassFeedbackChannelId), nameof(InspectionSortNgFeedbackChannelId), nameof(IsInspectionSortCameraValid), nameof(IsInspectionSortPassConveyorValid), nameof(IsInspectionSortNgConveyorValid), nameof(IsInspectionSortPassFeedbackValid), nameof(IsInspectionSortNgFeedbackValid), nameof(HasMultipleInspectionSortRouters), nameof(HasInspectionSortRouterSetupValidationError), nameof(InspectionSortRouterSetupValidationText), nameof(OhtTransportConveyorId), nameof(OhtRouteAvailableChannelId), nameof(OhtVehicleDockedChannelId), nameof(OhtLoadPortReadyChannelId), nameof(OhtCarrierReceivedChannelId), nameof(OhtHandoffReadyChannelId), nameof(OhtCarrierTransferredChannelId), nameof(IsOhtTransportConveyorValid), nameof(IsOhtRouteAvailableValid), nameof(IsOhtVehicleDockedValid), nameof(IsOhtLoadPortReadyValid), nameof(IsOhtCarrierReceivedValid), nameof(IsOhtHandoffReadyValid), nameof(IsOhtCarrierTransferredValid), nameof(HasMultipleOhtHandoffs), nameof(HasOhtHandoffSetupValidationError), nameof(OhtHandoffSetupValidationText)
        }) OnPropertyChanged(property);
        _applyWaferHandlerSetupCommand.RaiseCanExecuteChanged();
        _applyPrealignerSetupCommand.RaiseCanExecuteChanged();
        _applyInspectionHandoffSetupCommand.RaiseCanExecuteChanged();
        _applyInspectionSortRouterSetupCommand.RaiseCanExecuteChanged();
        _applyOhtHandoffSetupCommand.RaiseCanExecuteChanged();
    }

    private void RaiseProcessBlockChanged()
    {
        OnPropertyChanged(nameof(IsProcessBlockPreviewVisible));
        OnPropertyChanged(nameof(ProcessBlockKindText));
        OnPropertyChanged(nameof(ProcessBlockSummaryText));
        OnPropertyChanged(nameof(ProcessBlockApplyText));
        OnPropertyChanged(nameof(SelectedProcessBlockCount));
        OnPropertyChanged(nameof(ExistingProcessBlockCount));
        OnPropertyChanged(nameof(HasProcessBlockSelection));
        OnPropertyChanged(nameof(HasProcessBlockPlanError));
        OnPropertyChanged(nameof(ProcessBlockValidationText));
        OnPropertyChanged(nameof(CompatibleProcessBlockTimeoutCount));
        OnPropertyChanged(nameof(ProcessBlockTimeoutScopeText));
        OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
        _applyProcessBlockCommand.RaiseCanExecuteChanged();
        _cancelProcessBlockCommand.RaiseCanExecuteChanged();
        _previewProcessBlockTimeoutsCommand.RaiseCanExecuteChanged();
    }

    private void RaiseStationSetupChanged()
    {
        OnPropertyChanged(nameof(StationName));
        OnPropertyChanged(nameof(WaferType));
        OnPropertyChanged(nameof(AxisTravelText));
        OnPropertyChanged(nameof(TransportSpeedText));
        OnPropertyChanged(nameof(EntrySensorPositionText));
        OnPropertyChanged(nameof(ProcessSensorPositionText));
        OnPropertyChanged(nameof(CylinderTravelTimeText));
        RaiseStationSetupValidationChanged();
    }

    private void RaiseStationSetupValidationChanged()
    {
        OnPropertyChanged(nameof(IsStationNameValid));
        OnPropertyChanged(nameof(IsWaferTypeValid));
        OnPropertyChanged(nameof(IsAxisTravelValid));
        OnPropertyChanged(nameof(IsTransportSpeedValid));
        OnPropertyChanged(nameof(IsEntrySensorPositionValid));
        OnPropertyChanged(nameof(IsProcessSensorPositionValid));
        OnPropertyChanged(nameof(IsCylinderTravelTimeValid));
        OnPropertyChanged(nameof(HasStationSetupValidationError));
        OnPropertyChanged(nameof(StationSetupValidationText));
        _applyStationSkeletonCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCheckpointTemplateChanged()
    {
        OnPropertyChanged(nameof(IsCheckpointTemplatePreviewVisible));
        OnPropertyChanged(nameof(CheckpointTemplateProposedCount));
        OnPropertyChanged(nameof(CheckpointTemplateSummaryText));
        OnPropertyChanged(nameof(CheckpointTemplateApplyText));
        _applyCheckpointTemplateCommand.RaiseCanExecuteChanged();
        _cancelCheckpointTemplateCommand.RaiseCanExecuteChanged();
    }

    private void RaiseReadinessChanged()
    {
        OnPropertyChanged(nameof(ReadinessPassed));
        OnPropertyChanged(nameof(ReadinessStatusText));
        OnPropertyChanged(nameof(ReadinessDetailText));
        _previewSequenceStepCommand.RaiseCanExecuteChanged();
        _runRecipeDryRunCommand.RaiseCanExecuteChanged();
    }
}
