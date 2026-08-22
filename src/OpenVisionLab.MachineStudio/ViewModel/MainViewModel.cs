using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpenVisionLab;
using OpenVisionLab.Logging;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Vision.Contracts;
using OpenVisionLab.Machine.Vision.Models;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using OpenVisionLab.Wpf.MessageDialogs;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum UnsavedProjectDecision
{
    Save,
    Discard,
    Cancel
}

public sealed record ScenarioAssertionOutcomePresentation(
    bool IsPassed,
    string StatusText,
    string Summary);

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private enum BatchArtifactState
    {
        None,
        MemoryOnly,
        Saved,
        Restored,
        StaleRejected,
        SaveFailed
    }

    private const string CycleStartInputId = "di.cycle-start";
    private const string CycleActiveOutputId = "do.cycle-active";
    private const string CycleDoneOutputId = "do.cycle-done";
    private const int SimulationFixedStepMilliseconds = 5;
    private static readonly TimeSpan SimulationFixedStep =
        TimeSpan.FromMilliseconds(SimulationFixedStepMilliseconds);
    private static readonly TimeSpan MonitorRefreshInterval = TimeSpan.FromMilliseconds(50);
    private readonly ProjectDocumentStore _projectStore = new();
    private readonly SemiconductorStationSkeletonTemplate _stationSkeletonTemplate = new();
    private readonly SemiconductorProcessBlockComposer _processBlockComposer = new();
    private readonly LayoutEditHistory _layoutEditHistory = new();
    private readonly LayoutComponentClipboard _layoutClipboard = new();
    private readonly ILogger _logger = new ConsoleLogger();
    private readonly ISimulationEngine _engine;
    private readonly SceneSnapshotStore _dryRunPlaybackSnapshots = new();
    private readonly CancellationTokenSource _runtimeCancellation = new();
    private readonly Task _runtimeTask;
    private readonly string? _startupSamplePath;
    private MachineProjectDocument _project;
    private string? _currentProjectPath;
    private string _savedProjectEvidence = string.Empty;
    private bool _hasUnsavedChanges;
    private string _title = "OpenVisionLab Machine Studio";
    private string _statusMessage = "Ready";
    private bool _isRunning;
    private bool _isDesignMode = true;
    private bool _isCompactLayout;
    private TimeSpan _simulationTime;
    private long _tickIndex;
    private AxisSnapshot? _currentAxis;
    private VirtualCameraSnapshot? _currentCamera;
    private string? _selectedCameraId;
    private string? _selectedCameraRecipe;
    private SequenceExecutionSnapshot? _currentSequence;
    private AutomaticRunSnapshot _automaticRun = AutomaticRunSnapshot.NotConfigured;
    private DeterministicConditionScenarioSnapshot _conditionScenario =
        DeterministicConditionScenarioSnapshot.NotConfigured;
    private SimulationControlOwner _controlOwner = SimulationControlOwner.Definition;
    private bool? _cycleStartInput;
    private bool? _cycleActiveOutput;
    private bool? _cycleDoneOutput;
    private bool _isApplyingProject;
    private bool _isStartupChoiceVisible;
    private int _selectedLeftToolTabIndex;
    private ICommand? _sceneSelectionRequestedCommand;
    private ICommand? _sceneMoveRequestedCommand;
    private ICommand? _sceneMarqueeSelectionRequestedCommand;
    private ICommand? _sceneTransformRequestedCommand;
    private ICommand? _sceneLibraryComponentDropRequestedCommand;
    private ICommand? _startBlankLayoutCommand;
    private ICommand? _openBundledSampleCommand;
    private ICommand? _newProjectCommand;
    private ICommand? _openProjectCommand;
    private ICommand? _saveProjectCommand;
    private ICommand? _saveProjectAsCommand;
    private ICommand? _runCommand;
    private ICommand? _pauseCommand;
    private ICommand? _stepCommand;
    private ICommand? _resetCommand;
    private ICommand? _startTestScenarioCommand;
    private ICommand? _stopTestScenarioCommand;
    private ICommand? _replayTestScenarioCommand;
    private ICommand? _runScenarioBatchCommand;
    private ICommand? _cancelScenarioBatchCommand;
    private ICommand? _acceptBatchBaselineCommand;
    private ICommand? _clearBatchBaselineCommand;
    private ICommand? _navigateToBatchMismatchCommand;
    private ICommand? _cycleStartCommand;
    private ICommand? _startManualEquipmentControlCommand;
    private ICommand? _startManualCameraControlCommand;
    private ICommand? _triggerCameraCommand;
    private ICommand? _moveAxisAbsoluteCommand;
    private ICommand? _moveAxisRelativeCommand;
    private ICommand? _moveAxisVelocityCommand;
    private ICommand? _beginAxisJogNegativeCommand;
    private ICommand? _beginAxisJogPositiveCommand;
    private ICommand? _endAxisJogCommand;
    private ICommand? _homeAxisCommand;
    private ICommand? _stopAxisMotionCommand;
    private ICommand? _runMultiAxisCommissioningRecipeCommand;
    private ICommand? _stopMultiAxisCommissioningRecipeCommand;
    private ICommand? _validateMultiAxisCommissioningRecipeCommand;
    private ICommand? _acceptCommissioningBaselineCommand;
    private ICommand? _clearCommissioningBaselineCommand;
    private ICommand? _navigateToCommissioningMismatchCommand;
    private ICommand? _forceSensorOnCommand;
    private ICommand? _forceSensorOffCommand;
    private ICommand? _clearSensorForceCommand;
    private ICommand? _extendCylinderCommand;
    private ICommand? _retractCylinderCommand;
    private ICommand? _runConveyorForwardCommand;
    private ICommand? _runConveyorReverseCommand;
    private ICommand? _stopConveyorCommand;
    private ICommand? _addLayoutComponentCommand;
    private ICommand? _deleteLayoutComponentCommand;
    private ICommand? _nudgeLayoutComponentCommand;
    private ICommand? _alignLayoutSelectionCommand;
    private ICommand? _changeLayoutLayerOrderCommand;
    private ICommand? _undoLayoutEditCommand;
    private ICommand? _redoLayoutEditCommand;
    private ICommand? _copyLayoutSelectionCommand;
    private ICommand? _duplicateLayoutSelectionCommand;
    private ICommand? _pasteLayoutSelectionCommand;
    private ICommand? _previousDryRunPlaybackStepCommand;
    private ICommand? _nextDryRunPlaybackStepCommand;
    private ICommand? _exitDryRunPlaybackCommand;
    private ICommand? _returnToProcessPlanCommand;
    private ICommand? _previousProcessPlanReviewStepCommand;
    private ICommand? _nextProcessPlanReviewStepCommand;
    private ICommand? _exitCommand;
    private bool _runtimeDefinitionDirty;
    private bool _isSynchronizingSimulationWorkspace;
    private CancellationTokenSource? _batchCancellation;
    private DeterministicSimulationBatchResultPackage? _latestBatchResult;
    private DeterministicSimulationRunResultPackage? _acceptedBatchBaseline;
    private bool _isBatchRunning;
    private bool _testScenarioOwnsRun;
    private bool _batchWasCanceled;
    private int _batchCompletedRuns;
    private BatchArtifactState _batchArtifactState;
    private DeterministicMultiAxisCommissioningResultPackage? _latestCommissioningResult;
    private DeterministicMultiAxisCommissioningBaseline? _acceptedCommissioningBaseline;
    private DeterministicMultiAxisCommissioningResultHistory _commissioningHistory;
    private DeterministicCommissioningResultHistoryEntry? _selectedCommissioningHistoryEntry;
    private DeterministicCommissioningBaselineComparison? _commissioningBaselineComparison;
    private bool _isCommissioningValidationRunning;
    private int _commissioningCompletedRuns;
    private BatchArtifactState _commissioningArtifactState;
    private DeterministicVisionExecutionRecorder? _activeVisionEvidenceRecorder;
    private DeterministicVisionExecutionEvidencePackage? _latestVisionEvidence;
    private DeterministicVisionExecutionComparison? _visionEvidenceComparison;
    private BatchArtifactState _visionEvidenceArtifactState;
    private bool _disposed;
    private bool _isRestoringLayoutHistory;
    private LayoutAuthoringState _layoutAuthoringState = null!;
    private OpenVisionLanguageOption _selectedLanguageOption;
    private bool _axisJogInteractionActive;
    private string? _axisJogAxisId;
    private Task<SimulationCommandResult>? _axisJogStartTask;
    private string _axisTargetPositionText = string.Empty;
    private string? _axisTargetAxisId;
    private string _axisRelativeDistanceText = "10.000";
    private string _axisCommandVelocityText = "50.000";
    private AxisDriveTuningEditorViewModel? _axisDriveTuningEditor;
    private int _selectedDocumentTabIndex;
    private string? _processPlanReturnStepId;
    private (string SequenceId, string StepId)[] _processPlanReviewSteps = [];
    private int _processPlanReviewIndex = -1;
    private RecipeDryRunStepPresentation[] _dryRunPlaybackSteps = [];
    private int _dryRunPlaybackIndex = -1;
    private bool _isDryRunPlaybackActive;
    private bool IsValidationBusy => _isBatchRunning || _isCommissioningValidationRunning;

    internal Func<UnsavedProjectDecision> UnsavedProjectPrompt { get; set; }

    public MainViewModel(
        MachineProjectDocument? initialProject = null,
        string? initialProjectPath = null,
        string? startupSamplePath = null)
    {
        OpenVisionLanguageService.Load();
        UnsavedProjectPrompt = ShowUnsavedProjectPrompt;
        _selectedLanguageOption = OpenVisionLanguageService.LanguageOptions
            .First(option => option.Language == OpenVisionLanguageService.CurrentLanguage);
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;
        _project = initialProject ?? new MachineProjectDocument { Name = "Untitled" };
        _startupSamplePath = string.IsNullOrWhiteSpace(startupSamplePath)
            ? null
            : Path.GetFullPath(startupSamplePath);
        _isStartupChoiceVisible = initialProject is null && _startupSamplePath is not null;
        _commissioningHistory = DeterministicMultiAxisCommissioningResultHistory.Empty(_project.Id);
        _currentProjectPath = string.IsNullOrWhiteSpace(initialProjectPath)
            ? null
            : Path.GetFullPath(initialProjectPath);
        var initialRuntime = BuildRuntimeConfiguration(_project);
        _engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = SimulationFixedStep });

        ProjectTree = new ProjectTreeViewModel();
        Properties = new PropertiesViewModel();
        Layout = new MachineLayoutViewModel();
        RecipeConnections = new RecipeConnectionWorkbenchViewModel(
            componentId =>
            {
                if (componentId is null)
                {
                    Layout.SelectedItem = null;
                }
                else
                {
                    Layout.Select(componentId);
                }
            },
            OpenConnectionSequenceStep,
            AddConnectionSequenceStep,
            ValidateConnectionSimulationReadiness,
            RunConnectionSequenceStepPreviewAsync,
            RunConnectionRecipeDryRunAsync,
            ShowConnectionDryRunStep,
            ApplyConnectionStationSkeleton,
            ApplyConnectionLoadLockSetup,
            ApplyConnectionWaferHandlerSetup,
            ApplyConnectionPrealignerSetup,
            ApplyConnectionInspectionHandoffSetup,
            ApplyConnectionInspectionSortRouterSetup,
            ApplyConnectionOhtHandoffSetup,
            ApplyConnectionProcessBlock,
            ApplyConnectionProcessBlockTimeouts,
            OnConnectionCheckpointTemplateApplied,
            OpenProcessBlockSequenceStep);
        SequenceEditor = new SequenceEditorViewModel();
        SimulationWorkspace = new SimulationWorkspaceViewModel();
        MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeEditorViewModel(
            OnMultiAxisCommissioningRecipeChanged);
        CameraImageSourceEditor = new CameraImageSourceEditorViewModel(OnCameraImageSourceApplied);
        SemiconductorRecipes = new SemiconductorRecipeGalleryViewModel(
            CreateSemiconductorRecipeCopyAsync);
        SceneSnapshots = new SceneSnapshotStore();
        DigitalIo = new DigitalIoCommissioningViewModel(DispatchDigitalIoCommandAsync);
        FaultManager = new FaultManagerViewModel(DispatchFaultCommandAsync);
        ProjectTree.PropertyChanged += OnProjectTreePropertyChanged;
        Layout.PropertyChanged += OnLayoutPropertyChanged;
        SimulationWorkspace.PropertyChanged += OnSimulationWorkspacePropertyChanged;
        RecipeConnections.ProcessBlockPreviewClosed += OnProcessBlockPreviewClosed;
        Layout.DefinitionChanged += OnLayoutDefinitionChanged;
        SequenceEditor.DefinitionChanged += OnSequenceDefinitionChanged;

        ApplyProjectPresentation(_project);
        _layoutAuthoringState = CaptureLayoutAuthoringState();
        RestoreCommissioningResult();
        RestoreVisionEvidence();
        var initialSnapshot = _engine.CurrentSnapshot;
        SceneSnapshots.Publish(initialSnapshot);
        ApplyMonitorSnapshot(initialSnapshot);
        AcceptCurrentProjectAsSaved();
        Log("System", "Deterministic machine runtime ready · fixed step 5 ms");

        _runtimeTask = StartAndConsumeRuntimeAsync(initialRuntime);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(RunStatusText));
            NotifyManualCommissioningChanged(invalidateCommands: false);
            NotifyAxisCommissioningChanged(invalidateCommands: false);
            NotifySensorCommissioningChanged(invalidateCommands: false);
            NotifyCylinderCommissioningChanged(invalidateCommands: false);
            NotifyConveyorCommissioningChanged(invalidateCommands: false);
            NotifyCameraCommissioningChanged(invalidateCommands: false);
            NotifyMultiAxisCommissioningRecipeChanged(invalidateCommands: false);
            InvalidateCommands();
        }
    }

    public bool IsDesignMode
    {
        get => _isDesignMode;
        set
        {
            if (!SetProperty(ref _isDesignMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsRunMode));
            OnPropertyChanged(nameof(IsSceneEditable));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(ControlOwnerText));
            OnPropertyChanged(nameof(SceneControlText));
            OnPropertyChanged(nameof(LeftPanelHeaderText));
            OnPropertyChanged(nameof(RightPanelHeaderText));
            if (!value)
            {
                ExitDryRunPlayback();
            }
            Layout.IsEditable = IsSceneEditable;
            RecipeConnections.IsEditable = value;
            SequenceEditor.IsEditable = value;
            UpdateRunToolAvailability();
            NotifyModeDependentCommandsChanged();
            InvalidateModeCommands();
            SequenceEditor.InvalidateCommands();
            DigitalIo.InvalidateCommands();
            FaultManager.InvalidateCommands();

            if (value && IsRunning)
            {
                _ = PauseForDesignAsync();
            }
        }
    }

    public bool IsRunMode
    {
        get => !_isDesignMode;
        set => IsDesignMode = !value;
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (!SetProperty(ref _hasUnsavedChanges, value))
            {
                return;
            }

            RefreshProjectIdentity();
        }
    }

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        set => SetProperty(ref _isCompactLayout, value);
    }

    public ProjectTreeViewModel ProjectTree { get; }
    public PropertiesViewModel Properties { get; }
    public AxisDriveTuningEditorViewModel? AxisDriveTuningEditor
    {
        get => _axisDriveTuningEditor;
        private set
        {
            if (SetProperty(ref _axisDriveTuningEditor, value))
            {
                OnPropertyChanged(nameof(HasSelectedAxisDefinition));
            }
        }
    }

    public int SelectedDocumentTabIndex
    {
        get => _selectedDocumentTabIndex;
        set => SetProperty(ref _selectedDocumentTabIndex, value);
    }

    public int SelectedLeftToolTabIndex
    {
        get => _selectedLeftToolTabIndex;
        set => SetProperty(ref _selectedLeftToolTabIndex, value);
    }

    public bool IsStartupChoiceVisible
    {
        get => _isStartupChoiceVisible;
        private set => SetProperty(ref _isStartupChoiceVisible, value);
    }

    public bool HasProcessPlanReturnContext => _processPlanReturnStepId is not null;
    public string? ProcessPlanReturnStepId => _processPlanReturnStepId;
    public string ProcessPlanReviewPositionText => _processPlanReviewIndex < 0
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessPlanReviewPositionFormat"),
            _processPlanReviewIndex + 1,
            _processPlanReviewSteps.Length);
    public bool HasSelectedAxisDefinition => AxisDriveTuningEditor is not null;
    public MachineLayoutViewModel Layout { get; }
    public RecipeConnectionWorkbenchViewModel RecipeConnections { get; }
    public SequenceEditorViewModel SequenceEditor { get; }
    public SimulationWorkspaceViewModel SimulationWorkspace { get; }
    public MultiAxisCommissioningRecipeEditorViewModel MultiAxisCommissioningRecipe { get; }
    public CameraImageSourceEditorViewModel CameraImageSourceEditor { get; }
    public SemiconductorRecipeGalleryViewModel SemiconductorRecipes { get; }
    public SceneSnapshotStore SceneSnapshots { get; }
    public SceneSnapshotStore SceneSnapshotSource => IsDryRunPlaybackActive
        ? _dryRunPlaybackSnapshots
        : SceneSnapshots;
    private SimulationSnapshot PresentationSnapshot =>
        SceneSnapshotSource.Latest ?? SceneSnapshots.Latest ?? _engine.CurrentSnapshot;
    public bool IsSceneEditable => IsDesignMode && !IsDryRunPlaybackActive;
    public bool IsDryRunPlaybackActive => _isDryRunPlaybackActive;
    public string DryRunPlaybackTitleText => _dryRunPlaybackIndex < 0
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.DryRunPlaybackTitleFormat"),
            _dryRunPlaybackIndex + 1,
            _dryRunPlaybackSteps.Length,
            _dryRunPlaybackSteps[_dryRunPlaybackIndex].Name);
    public string DryRunPlaybackDetailText => _dryRunPlaybackIndex < 0
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.DryRunPlaybackDetailFormat"),
            _dryRunPlaybackSteps[_dryRunPlaybackIndex].TickText);
    public bool HasDryRunPlaybackCheckpoint => _dryRunPlaybackIndex >= 0
        && _dryRunPlaybackSteps[_dryRunPlaybackIndex].HasCheckpoint;
    public bool HasDryRunPlaybackMismatch => _dryRunPlaybackIndex >= 0
        && _dryRunPlaybackSteps[_dryRunPlaybackIndex].HasCheckpointMismatch;
    public string DryRunPlaybackCheckpointText => _dryRunPlaybackIndex < 0
        ? string.Empty
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].CheckpointText;
    private LoadLockSnapshot? DryRunPlaybackLoadLock => _dryRunPlaybackIndex < 0
        ? null
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].BoundarySnapshot.LoadLocks.FirstOrDefault();
    public bool HasDryRunPlaybackLoadLock => DryRunPlaybackLoadLock is not null;
    public bool IsDryRunPlaybackLoadLockFault =>
        DryRunPlaybackLoadLock?.State == LoadLockState.InterlockFault;
    public string DryRunPlaybackLoadLockText => DryRunPlaybackLoadLock is { } loadLock
        ? RecipeConnectionWorkbenchViewModel.FormatLoadLockStatus(loadLock)
        : string.Empty;
    private WaferHandlerSnapshot? DryRunPlaybackWaferHandler => _dryRunPlaybackIndex < 0
        ? null
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].BoundarySnapshot.WaferHandlers.FirstOrDefault();
    public bool HasDryRunPlaybackWaferHandler => DryRunPlaybackWaferHandler is not null;
    public bool IsDryRunPlaybackWaferHandlerFault =>
        DryRunPlaybackWaferHandler?.State == WaferHandlerOwnershipState.InterlockFault;
    public string DryRunPlaybackWaferHandlerText => DryRunPlaybackWaferHandler is { } handler
        ? RecipeConnectionWorkbenchViewModel.FormatWaferHandlerStatus(handler)
        : string.Empty;
    private InspectionSortRouterSnapshot? DryRunPlaybackInspectionSorter => _dryRunPlaybackIndex < 0
        ? null
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].BoundarySnapshot.InspectionSortRouters.FirstOrDefault();
    public bool HasDryRunPlaybackInspectionSorter => DryRunPlaybackInspectionSorter is not null;
    public bool IsDryRunPlaybackInspectionSorterFault =>
        DryRunPlaybackInspectionSorter?.State == InspectionSortRouteState.InterlockFault;
    public string DryRunPlaybackInspectionSorterText => DryRunPlaybackInspectionSorter is { } sorter
        ? RecipeConnectionWorkbenchViewModel.FormatInspectionSorterStatus(sorter)
        : string.Empty;
    private InspectionHandoffSnapshot? DryRunPlaybackInspectionHandoff => _dryRunPlaybackIndex < 0
        ? null
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].BoundarySnapshot.InspectionHandoffs.FirstOrDefault();
    public bool HasDryRunPlaybackInspectionHandoff => DryRunPlaybackInspectionHandoff is not null;
    public bool IsDryRunPlaybackInspectionHandoffFault =>
        DryRunPlaybackInspectionHandoff?.State == InspectionHandoffState.InterlockFault;
    public string DryRunPlaybackInspectionHandoffText => DryRunPlaybackInspectionHandoff is { } handoff
        ? RecipeConnectionWorkbenchViewModel.FormatInspectionHandoffStatus(handoff)
        : string.Empty;
    private OhtHandoffSnapshot? DryRunPlaybackOhtHandoff => _dryRunPlaybackIndex < 0
        ? null
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].BoundarySnapshot.OhtHandoffs.FirstOrDefault();
    public bool HasDryRunPlaybackOhtHandoff => DryRunPlaybackOhtHandoff is not null;
    public bool IsDryRunPlaybackOhtHandoffFault =>
        DryRunPlaybackOhtHandoff?.State == OhtHandoffOwnershipState.InterlockFault;
    public string DryRunPlaybackOhtHandoffText => DryRunPlaybackOhtHandoff is { } handoff
        ? RecipeConnectionWorkbenchViewModel.FormatOhtHandoffStatus(handoff)
        : string.Empty;
    private PrealignerSnapshot? DryRunPlaybackPrealigner => _dryRunPlaybackIndex < 0
        ? null
        : _dryRunPlaybackSteps[_dryRunPlaybackIndex].BoundarySnapshot.Prealigners.FirstOrDefault();
    public bool HasDryRunPlaybackPrealigner => DryRunPlaybackPrealigner is not null;
    public bool IsDryRunPlaybackPrealignerFault =>
        DryRunPlaybackPrealigner?.State == PrealignerState.InterlockFault;
    public string DryRunPlaybackPrealignerText => DryRunPlaybackPrealigner is { } prealigner
        ? RecipeConnectionWorkbenchViewModel.FormatPrealignerStatus(prealigner)
        : string.Empty;
    public DigitalIoCommissioningViewModel DigitalIo { get; }
    public FaultManagerViewModel FaultManager { get; }
    public ObservableCollection<string> LogMessages { get; } = new();
    public IReadOnlyList<OpenVisionLanguageOption> LanguageOptions => OpenVisionLanguageService.LanguageOptions;

    public DeterministicConditionScenarioSnapshot ConditionScenario => _conditionScenario;
    public IReadOnlyList<SimulationScenarioTargetOption> ConditionScenarioTargets
    {
        get
        {
            var snapshot = SceneSnapshots.Latest ?? _engine.CurrentSnapshot;
            return snapshot.Axes
                .Select(axis => new SimulationScenarioTargetOption(axis.Id, axis.Name))
                .Concat(snapshot.LayoutComponents.Select(component =>
                    new SimulationScenarioTargetOption(component.Id, component.Name)))
                .DistinctBy(target => target.Id, StringComparer.Ordinal)
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }
    public IReadOnlyList<SimulationScenarioTargetOption> ScheduledFaultTargets =>
        new SimulationFaultTargetCatalog()
            .GetTargets(
                SceneSnapshots.Latest ?? _engine.CurrentSnapshot,
                SimulationWorkspace.ScheduledFaultKind)
            .Select(target => new SimulationScenarioTargetOption(target.Id, target.Name))
            .ToArray();
    public IReadOnlyList<SimulationScenarioTargetOption> RecoverySequences =>
        (SceneSnapshots.Latest ?? _engine.CurrentSnapshot).Sequences
            .Select(sequence => new SimulationScenarioTargetOption(
                sequence.SequenceId,
                sequence.SequenceId))
            .OrderBy(sequence => sequence.Id, StringComparer.Ordinal)
            .ToArray();
    public string ConditionScenarioStateText => !_conditionScenario.IsConfigured
        ? OpenVisionLanguageService.T("Simulation.ScenarioNotConfigured")
        : OpenVisionLanguageService.T(
            $"Simulation.ConditionState.{_conditionScenario.State}",
            _conditionScenario.State.ToString(),
            _conditionScenario.State.ToString());
    public string ConditionScenarioProgressText => !_conditionScenario.IsConfigured
        ? OpenVisionLanguageService.T("Simulation.ScenarioNoProgress")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.ScenarioProgress"),
            _conditionScenario.ExecutedTicks,
            _conditionScenario.DurationTicks);
    public string ConditionScenarioHealthText => !_conditionScenario.IsConfigured
        ? OpenVisionLanguageService.T("Simulation.ScenarioNoHealth")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.ScenarioHealth"),
            _conditionScenario.HealthScore);
    public bool CanStartTestScenario => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !_conditionScenario.IsActive
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanStopTestScenario => IsRunMode && !IsValidationBusy && _conditionScenario.IsActive;
    public bool CanReplayTestScenario => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool IsBatchRunning => _isBatchRunning;
    public bool IsScenarioConfigurationEnabled => !IsValidationBusy;
    public int BatchCompletedRuns => _batchCompletedRuns;
    public bool CanRunScenarioBatch => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanAcceptBatchBaseline => !_isBatchRunning
        && _latestBatchResult is { IsComplete: true, IsSuccess: true, Runs.Length: > 0 };
    public bool CanClearBatchBaseline => !_isBatchRunning && _acceptedBatchBaseline is not null;
    public bool CanNavigateToBatchMismatch => !_isBatchRunning
        && _latestBatchResult?.FirstMismatch is not null;
    public string BatchStatusText => _isBatchRunning
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.BatchRunning"),
            _batchCompletedRuns,
            SimulationWorkspace.BatchRepetitionCount)
        : _batchWasCanceled
            ? OpenVisionLanguageService.T("Simulation.BatchCanceled")
            : _latestBatchResult is null
                ? OpenVisionLanguageService.T("Simulation.BatchIdle")
                : _latestBatchResult.IsSuccess
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        OpenVisionLanguageService.T("Simulation.BatchPassed"),
                        _latestBatchResult.CompletedRuns)
                    : OpenVisionLanguageService.T("Simulation.BatchMismatch");
    public string BatchResultText
    {
        get
        {
            if (_latestBatchResult is null)
            {
                return OpenVisionLanguageService.T("Simulation.BatchNoResult");
            }

            if (_latestBatchResult.IsSuccess)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Simulation.BatchResultPassed"),
                    _latestBatchResult.CompletedRuns,
                    ShortHash(_latestBatchResult.EvidenceHash));
            }

            var mismatch = _latestBatchResult.FirstMismatch;
            return mismatch is null
                ? OpenVisionLanguageService.T("Simulation.BatchMismatch")
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Simulation.BatchResultMismatch"),
                    mismatch.RunIndex,
                    mismatch.EvidenceKind,
                    mismatch.TargetId,
                    mismatch.ObservedTickIndex);
        }
    }
    public string BatchBaselineText => _acceptedBatchBaseline is null
        ? OpenVisionLanguageService.T("Simulation.BatchBaselineNone")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.BatchBaselineAccepted"),
            ShortHash(_acceptedBatchBaseline.EvidenceHash));
    public string BatchArtifactStatusText => _batchArtifactState switch
    {
        BatchArtifactState.MemoryOnly => OpenVisionLanguageService.T("Simulation.BatchArtifactMemoryOnly"),
        BatchArtifactState.Saved => OpenVisionLanguageService.T("Simulation.BatchArtifactSaved"),
        BatchArtifactState.Restored => OpenVisionLanguageService.T("Simulation.BatchArtifactRestored"),
        BatchArtifactState.StaleRejected => OpenVisionLanguageService.T("Simulation.BatchArtifactStale"),
        BatchArtifactState.SaveFailed => OpenVisionLanguageService.T("Simulation.BatchArtifactSaveFailed"),
        _ => OpenVisionLanguageService.T("Simulation.BatchArtifactNone")
    };
    public IReadOnlyList<ScenarioAssertionOutcomePresentation> BatchAssertionOutcomes
    {
        get
        {
            var result = _latestBatchResult?.Runs.LastOrDefault()?.Result;
            return result is null || result.AssertionOutcomes.IsDefaultOrEmpty
                ? []
                : result.AssertionOutcomes.Select(CreateAssertionOutcomePresentation).ToArray();
        }
    }
    public bool HasBatchAssertionOutcomes =>
        _latestBatchResult?.Runs.LastOrDefault()?.Result.AssertionOutcomes.IsDefaultOrEmpty == false;
    internal DeterministicSimulationBatchResultPackage? LatestBatchResult => _latestBatchResult;
    internal bool HasAcceptedBatchBaseline => _acceptedBatchBaseline is not null;
    internal bool BatchWasCanceled => _batchWasCanceled;
    internal bool HasRestoredBatchArtifacts =>
        _batchArtifactState == BatchArtifactState.Restored
        && _latestBatchResult is not null
        && _acceptedBatchBaseline is not null;
    internal bool RejectedStaleBatchArtifacts => _batchArtifactState == BatchArtifactState.StaleRejected;

    public OpenVisionLanguageOption SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedLanguageOption, value))
            {
                return;
            }

            _selectedLanguageOption = value;
            OnPropertyChanged();
            OpenVisionLanguageService.SetLanguage(value.Language);
        }
    }

    public bool HasSelectedEquipment => Layout.SelectedItem?.Component is not null;
    public bool HasSelectedAxisStage => Layout.SelectedItem?.Component?.Kind is
        LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage;
    public bool HasSelectedManualEquipment => HasSelectedAxisStage
        || HasSelectedDigitalSensor
        || HasSelectedPneumaticCylinder
        || HasSelectedConveyor;
    public EquipmentStatusPresentation? SelectedEquipmentStatus => Layout.SelectedItem is null
        ? null
        : EquipmentStatusPresentation.Create(
            Layout.SelectedItem,
            PresentationSnapshot,
            _project);

    public string ModeText => IsDesignMode
        ? OpenVisionLanguageService.T("Shell.Design")
        : OpenVisionLanguageService.T("Shell.Run");
    public string StateText => IsRunning
        ? OpenVisionLanguageService.T("Shell.Running")
        : OpenVisionLanguageService.T("Shell.Paused");
    public string LeftPanelHeaderText => IsRunMode
        ? OpenVisionLanguageService.T("Shell.RunSummary")
        : OpenVisionLanguageService.T("Shell.Project");
    public string RightPanelHeaderText => IsRunMode
        ? OpenVisionLanguageService.T("Shell.Runtime")
        : OpenVisionLanguageService.T("Shell.Properties");
    public string ProjectStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(HasUnsavedChanges
            ? "Shell.ProjectStatusUnsaved"
            : "Shell.ProjectStatus"),
        ProjectDisplayName);
    public string SelectionStatusText => Layout.SelectionCount > 1 && Layout.SelectedItem is not null
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Shell.SelectionMultiple"),
            Layout.SelectionCount,
            Layout.SelectedItem.Name)
        : Layout.SelectedItem is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Shell.SelectionStatus"),
                Layout.SelectedItem.Name)
        : ProjectTree.SelectedNode is null
            ? OpenVisionLanguageService.T("Shell.SelectionNone")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Shell.SelectionStatus"),
                ProjectTree.SelectedNode.DisplayName);
    public string SimulationStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Shell.SimulationStatus"),
        _simulationTime);
    public string TickStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Shell.TickStatus"),
        _tickIndex);
    public string FixedStepStatusText => OpenVisionLanguageService.T("Shell.FixedStep");
    public string RunStatusText => StateText;
    public string AxisCountText => _project.Axes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string LayoutComponentCountText => _project.Layouts
        .SelectMany(layout => layout.Components)
        .Count()
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string CameraCountText => _project.Devices.Count(device => device.Kind == DeviceKind.Camera)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasVirtualCamera => _project.Devices.Any(device => device.Kind == DeviceKind.Camera);
    public IReadOnlyList<DeviceDefinition> VirtualCameras => _project.Devices
        .Where(device => device.Kind == DeviceKind.Camera)
        .ToArray();
    public DeviceDefinition? SelectedVirtualCamera
    {
        get => _project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera
            && string.Equals(device.Id, _selectedCameraId, StringComparison.Ordinal));
        set => SelectVirtualCamera(value?.Id);
    }
    public string? SelectedCameraId
    {
        get => _selectedCameraId;
        set => SelectVirtualCamera(value);
    }
    public IReadOnlyList<string> CurrentCameraRecipes => GetCameraRecipes(_selectedCameraId);
    public string? SelectedCameraRecipe
    {
        get => _selectedCameraRecipe;
        set
        {
            if (string.IsNullOrWhiteSpace(value) && CurrentCameraRecipes.Count > 0)
            {
                return;
            }

            if (string.Equals(_selectedCameraRecipe, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedCameraRecipe = value;
            OnPropertyChanged();
            RefreshVisionEvidenceContext();
            NotifyCameraCommissioningChanged();
        }
    }
    public bool HasEmbeddedSequence => _project.Sequences.Count > 0;
    public bool HasAutomaticRun => _project.Simulation.AutomaticRun is not null;
    public bool HasAuthoredLayout => _project.Layouts.Count > 0;
    public bool HasCycleStartInput => _project.Channels.Any(channel =>
        string.Equals(channel.Id, CycleStartInputId, StringComparison.Ordinal)
        && channel.Kind == global::OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput);
    public string ControlOwnerHelpText => HasAutomaticRun
        ? OpenVisionLanguageService.T("Shell.ControlOwnerAutomatic")
        : HasEmbeddedSequence
        ? OpenVisionLanguageService.T("Shell.ControlOwnerSequence")
        : OpenVisionLanguageService.T("Shell.ControlOwnerManual");
    public string ControlOwnerText => IsRunMode
        ? OpenVisionLanguageService.T(
            $"Shell.ControlOwnerLabel.{_controlOwner}",
            _controlOwner.ToString(),
            _controlOwner.ToString())
        : OpenVisionLanguageService.T("Shell.Definition");
    public string SceneTitleText => string.IsNullOrWhiteSpace(ProjectDisplayName)
        ? "UNTITLED MACHINE"
        : ProjectDisplayName.ToUpperInvariant();
    public string SceneControlText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Shell.SceneControl"),
        ControlOwnerText);
    public string CurrentAxisName => _currentAxis?.Name ?? OpenVisionLanguageService.T("Shell.NoAxis");
    public string CurrentAxisStateText => _currentAxis is null
        ? OpenVisionLanguageService.T("Shell.Unavailable")
        : LocalizeRuntimeState(_currentAxis.State.ToString());
    public string CurrentAxisPositionText => _currentAxis is null
        ? "—"
        : $"{_currentAxis.Position:F3} {CurrentAxisUnit}";
    public string CurrentAxisVelocityText => _currentAxis is null
        ? "—"
        : $"{_currentAxis.Velocity:F3} {CurrentAxisUnit}/s";
    public string CurrentAxisHomeText => CurrentAxisDefinition is null
        ? "—"
        : $"{CurrentAxisDefinition.HomePosition:F3} {CurrentAxisUnit}";
    public string CurrentAxisLimitsText => CurrentAxisDefinition is null
        ? "—"
        : $"{CurrentAxisDefinition.SoftLimitMin ?? 0:F3} … {CurrentAxisDefinition.SoftLimitMax ?? 300:F3} {CurrentAxisUnit}";
    public string CurrentAxisFollowingErrorText => _currentAxis is null
        ? "—"
        : $"{_currentAxis.FollowingError:F3} / {_currentAxis.FollowingErrorLimit:F3} {CurrentAxisUnit}";
    public string CurrentAxisDriveTuningText => _currentAxis is null
        ? "—"
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.DriveTuningFormat"),
            _currentAxis.MaximumVelocity,
            _currentAxis.Acceleration,
            _currentAxis.Deceleration,
            CurrentAxisUnit);
    public bool IsCurrentAxisDriveAlarmActive => _currentAxis?.DriveAlarmActive == true;
    public string CurrentAxisDriveAlarmText => _currentAxis is null
        ? "—"
        : OpenVisionLanguageService.T(
            IsCurrentAxisDriveAlarmActive ? "Axis.DriveAlarmActive" : "Axis.DriveAlarmReady");
    public string CurrentAxisUnitText => CurrentAxisUnit;
    public string CurrentAxisVelocityUnitText => $"{CurrentAxisUnit}/s";
    public string AxisTargetPositionText
    {
        get => _axisTargetPositionText;
        set
        {
            if (!SetProperty(ref _axisTargetPositionText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAxisTargetPositionValid));
            OnPropertyChanged(nameof(HasAxisTargetPositionError));
            OnPropertyChanged(nameof(AxisTargetPositionValidationText));
            OnPropertyChanged(nameof(CanMoveAxisAbsolute));
            InvalidateCommands();
        }
    }
    public bool IsAxisTargetPositionValid => TryGetAxisTargetPosition(out _);
    public bool HasAxisTargetPositionError => HasSelectedAxisStage
        && _currentAxis is not null
        && !IsAxisTargetPositionValid;
    public string AxisTargetPositionValidationText
    {
        get
        {
            if (!HasSelectedAxisStage || _currentAxis is null)
            {
                return string.Empty;
            }

            if (!TryParseAxisTargetPosition(out var target))
            {
                return OpenVisionLanguageService.T("Axis.TargetInvalid");
            }

            var minimum = CurrentAxisDefinition?.SoftLimitMin ?? 0;
            var maximum = CurrentAxisDefinition?.SoftLimitMax ?? 300;
            return target < minimum || target > maximum
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.TargetOutOfRange"),
                    minimum,
                    maximum,
                    CurrentAxisUnit)
                : string.Empty;
        }
    }
    public string AxisRelativeDistanceText
    {
        get => _axisRelativeDistanceText;
        set
        {
            if (!SetProperty(ref _axisRelativeDistanceText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAxisRelativeDistanceValid));
            OnPropertyChanged(nameof(HasAxisRelativeDistanceError));
            OnPropertyChanged(nameof(AxisRelativeDistanceValidationText));
            OnPropertyChanged(nameof(CanMoveAxisRelative));
            InvalidateCommands();
        }
    }
    public bool IsAxisRelativeDistanceValid => TryGetAxisRelativeDistance(out _);
    public bool HasAxisRelativeDistanceError => HasSelectedAxisStage
        && _currentAxis is not null
        && !IsAxisRelativeDistanceValid;
    public string AxisRelativeDistanceValidationText => HasAxisRelativeDistanceError
        ? OpenVisionLanguageService.T("Axis.RelativeInvalid")
        : string.Empty;
    public string AxisCommandVelocityText
    {
        get => _axisCommandVelocityText;
        set
        {
            if (!SetProperty(ref _axisCommandVelocityText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAxisCommandVelocityValid));
            OnPropertyChanged(nameof(HasAxisCommandVelocityError));
            OnPropertyChanged(nameof(AxisCommandVelocityValidationText));
            OnPropertyChanged(nameof(CanMoveAxisVelocity));
            InvalidateCommands();
        }
    }
    public bool IsAxisCommandVelocityValid => TryGetAxisCommandVelocity(out _);
    public bool HasAxisCommandVelocityError => HasSelectedAxisStage
        && _currentAxis is not null
        && !IsAxisCommandVelocityValid;
    public string AxisCommandVelocityValidationText
    {
        get
        {
            if (!HasAxisCommandVelocityError)
            {
                return string.Empty;
            }

            if (!TryParseAxisCommandVelocity(out var velocity) || velocity == 0)
            {
                return OpenVisionLanguageService.T("Axis.VelocityInvalid");
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Axis.VelocityOutOfRange"),
                CurrentAxisDefinition?.MaxVelocity ?? 0,
                CurrentAxisVelocityUnitText);
        }
    }
    public bool IsCurrentAxisInterlocked => _currentAxis?.State == AxisState.Error;
    public string CurrentAxisInterlockText => _currentAxis is null
        ? "—"
        : OpenVisionLanguageService.T(
            IsCurrentAxisInterlocked ? "Axis.InterlockBlocked" : "Axis.InterlockReady");
    public string AxisCommissioningHintText => _currentAxis is null
        ? OpenVisionLanguageService.T("Axis.NoAxisHint")
        : IsCurrentAxisDriveAlarmActive
            ? OpenVisionLanguageService.T("Axis.ClearDriveAlarmHint")
            : IsCurrentAxisInterlocked
                ? OpenVisionLanguageService.T("Axis.ClearInterlockHint")
                : _controlOwner == SimulationControlOwner.Manual && IsRunning
                    ? OpenVisionLanguageService.T("Axis.ManualVelocityMoveHint")
                    : IsRunning || _automaticRun.IsActive ||
                      _currentSequence?.Status == SequenceExecutionStatus.Running
                        ? OpenVisionLanguageService.T("Axis.ResetForManualHint")
                        : OpenVisionLanguageService.T("Axis.VelocityMoveStartManualHint");
    public bool CanStartManualEquipmentControl => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !IsRunning
        && !_automaticRun.IsActive
        && _currentSequence?.Status != SequenceExecutionStatus.Running
        && (Layout.SelectedItem?.Component?.Kind switch
        {
            LayoutComponentKind.LinearStage => _currentAxis is not null
                && !IsCurrentAxisInterlocked,
            LayoutComponentKind.RotaryStage => _currentAxis is not null
                && !IsCurrentAxisInterlocked,
            LayoutComponentKind.DigitalSensor => HasSelectedDigitalSensor
                && !IsCurrentSensorFaulted,
            LayoutComponentKind.PneumaticCylinder => HasSelectedPneumaticCylinder
                && !IsCurrentCylinderInterlocked,
            LayoutComponentKind.Conveyor => HasSelectedConveyor,
            _ => false
        });
    public bool IsMultiAxisCommissioningRecipeSelection => IsDesignMode
        && ProjectTree.SelectedNode?.Kind is
            global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.Project or
            global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.Axes;
    public bool HasMultiAxisCommissioningRecipe => MultiAxisCommissioningRecipe.IsConfigured;
    public bool CanRunMultiAxisCommissioningRecipe => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && MultiAxisCommissioningRecipe.IsValid
        && !_conditionScenario.IsActive
        && !_automaticRun.IsActive
        && _currentSequence?.Status != SequenceExecutionStatus.Running
        && _controlOwner != SimulationControlOwner.EmbeddedSequence
        && MultiAxisCommissioningRecipe.Targets.All(target =>
            target.RuntimeState != AxisState.Moving);
    public bool CanStopMultiAxisCommissioningRecipe => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && _controlOwner == SimulationControlOwner.Manual
        && MultiAxisCommissioningRecipe.Targets.Any(target =>
            target.RuntimeState == AxisState.Moving);
    public bool IsCommissioningValidationRunning => _isCommissioningValidationRunning;
    public bool IsCommissioningValidationConfigurationEnabled => !_isCommissioningValidationRunning;
    public bool CanValidateMultiAxisCommissioningRecipe => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !IsRunning
        && !_runtimeDefinitionDirty
        && MultiAxisCommissioningRecipe.IsValid
        && MultiAxisCommissioningRecipe.Targets.All(target => target.RuntimeState != AxisState.Moving);
    public string CommissioningValidationStatusText => _isCommissioningValidationRunning
        ? string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.RecipeValidationRunning"),
            _commissioningCompletedRuns,
            MultiAxisCommissioningRecipe.ValidationRepetitions)
        : _latestCommissioningResult is null
            ? OpenVisionLanguageService.T("Axis.RecipeValidationReady")
            : _latestCommissioningResult.IsSuccess
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeValidationPassed"),
                    _latestCommissioningResult.CompletedRuns)
                : OpenVisionLanguageService.T("Axis.RecipeValidationMismatch");
    public string CommissioningValidationResultText
    {
        get
        {
            if (_latestCommissioningResult is null)
            {
                return OpenVisionLanguageService.T("Axis.RecipeValidationNoResult");
            }
            if (_latestCommissioningResult.IsSuccess)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeValidationEvidence"),
                    ShortHash(_latestCommissioningResult.EvidenceHash));
            }
            var mismatch = _latestCommissioningResult.FirstMismatch;
            return mismatch is null
                ? OpenVisionLanguageService.T("Axis.RecipeValidationMismatch")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeValidationMismatchDetail"),
                    mismatch.RunIndex,
                    mismatch.EvidenceKind,
                    mismatch.TickIndex);
        }
    }
    public string CommissioningEvidenceStatusText => _commissioningArtifactState switch
    {
        BatchArtifactState.MemoryOnly => OpenVisionLanguageService.T("Axis.RecipeEvidenceMemoryOnly"),
        BatchArtifactState.Saved => OpenVisionLanguageService.T("Axis.RecipeEvidenceSaved"),
        BatchArtifactState.Restored => OpenVisionLanguageService.T("Axis.RecipeEvidenceRestored"),
        BatchArtifactState.StaleRejected => OpenVisionLanguageService.T("Axis.RecipeEvidenceStale"),
        BatchArtifactState.SaveFailed => OpenVisionLanguageService.T("Axis.RecipeEvidenceSaveFailed"),
        _ => OpenVisionLanguageService.T("Axis.RecipeEvidenceNone")
    };
    public IReadOnlyList<DeterministicCommissioningResultHistoryEntry> CommissioningResultHistoryEntries =>
        _commissioningHistory.Entries.IsDefault
            ? Array.Empty<DeterministicCommissioningResultHistoryEntry>()
            : _commissioningHistory.Entries;
    public DeterministicCommissioningResultHistoryEntry? SelectedCommissioningHistoryEntry
    {
        get => _selectedCommissioningHistoryEntry;
        set
        {
            if (SetProperty(ref _selectedCommissioningHistoryEntry, value))
            {
                OnPropertyChanged(nameof(CanAcceptCommissioningBaseline));
                InvalidateCommands();
            }
        }
    }
    public bool CanAcceptCommissioningBaseline => !IsValidationBusy
        && SelectedCommissioningHistoryEntry?.Reference is not null;
    public bool CanClearCommissioningBaseline => !IsValidationBusy
        && _acceptedCommissioningBaseline is not null;
    public bool CanNavigateToCommissioningMismatch => !IsValidationBusy
        && !string.IsNullOrWhiteSpace(_commissioningBaselineComparison?.FirstMismatch?.TargetId);
    public string CommissioningHistoryStatusText => _commissioningHistory.Entries.IsDefaultOrEmpty
        ? OpenVisionLanguageService.T("Axis.RecipeHistoryEmpty")
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.RecipeHistorySummary"),
            _commissioningHistory.Entries.Length,
            DeterministicMultiAxisCommissioningResultHistory.MaximumEntries);
    public string CommissioningBaselineStatusText
    {
        get
        {
            if (_acceptedCommissioningBaseline is null)
            {
                return OpenVisionLanguageService.T("Axis.RecipeBaselineNone");
            }
            if (_commissioningBaselineComparison is null)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeBaselineAccepted"),
                    ShortHash(_acceptedCommissioningBaseline.EvidenceHash));
            }
            if (_commissioningBaselineComparison.IsMatch)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeBaselineMatch"),
                    ShortHash(_acceptedCommissioningBaseline.EvidenceHash));
            }
            var mismatch = _commissioningBaselineComparison.FirstMismatch;
            return mismatch is null
                ? OpenVisionLanguageService.T("Axis.RecipeBaselineMismatch")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeBaselineMismatchDetail"),
                    string.IsNullOrWhiteSpace(mismatch.TargetId)
                        ? MultiAxisCommissioningRecipe.Name
                        : mismatch.TargetId,
                    mismatch.EvidenceKind,
                    mismatch.TickIndex);
        }
    }
    internal DeterministicMultiAxisCommissioningResultPackage? LatestCommissioningResult =>
        _latestCommissioningResult;
    internal DeterministicMultiAxisCommissioningBaseline? AcceptedCommissioningBaseline =>
        _acceptedCommissioningBaseline;
    internal DeterministicMultiAxisCommissioningResultHistory CommissioningResultHistory =>
        _commissioningHistory;
    internal DeterministicCommissioningBaselineComparison? CommissioningBaselineComparison =>
        _commissioningBaselineComparison;
    internal bool HasRestoredCommissioningResult =>
        _commissioningArtifactState == BatchArtifactState.Restored
        && _latestCommissioningResult is not null;
    internal bool RejectedStaleCommissioningResult =>
        _commissioningArtifactState == BatchArtifactState.StaleRejected;
    public bool CanJogAxis => _axisJogInteractionActive ||
        (CanUseManualAxis && _currentAxis?.State != AxisState.Moving);
    public bool CanMoveAxisAbsolute => CanUseManualAxis
        && !_axisJogInteractionActive
        && _currentAxis?.State != AxisState.Moving
        && TryGetAxisTargetPosition(out _);
    public bool CanMoveAxisRelative => CanUseManualAxis
        && !_axisJogInteractionActive
        && _currentAxis?.State != AxisState.Moving
        && TryGetAxisRelativeDistance(out _);
    public bool CanMoveAxisVelocity => CanUseManualAxis
        && !_axisJogInteractionActive
        && _currentAxis?.State != AxisState.Moving
        && TryGetAxisCommandVelocity(out _);
    public bool HasSelectedDigitalSensor => CurrentSensorSnapshot is not null;
    public bool IsCurrentSensorFaulted => CurrentSensorOutputChannelId is { } channelId
        && FaultManager.ActiveFaults.Any(fault =>
            fault.Kind == SimulationFaultKind.StuckDigitalInput
            && string.Equals(fault.TargetId, channelId, StringComparison.Ordinal));
    public bool IsCurrentSensorManuallyForced =>
        !IsCurrentSensorFaulted && CurrentSensorSignal?.OverrideValue.HasValue == true;
    public string CurrentSensorForceText => !HasSelectedDigitalSensor
        ? "??"
        : IsCurrentSensorFaulted
            ? OpenVisionLanguageService.T("Sensor.FaultOverride")
            : CurrentSensorSignal?.OverrideValue switch
            {
                true => OpenVisionLanguageService.T("Sensor.ForcedOn"),
                false => OpenVisionLanguageService.T("Sensor.ForcedOff"),
                _ => OpenVisionLanguageService.T("Sensor.ForceReleased")
            };
    public string SensorCommissioningHintText => !HasSelectedDigitalSensor
        ? OpenVisionLanguageService.T("Sensor.NoSensorHint")
        : IsCurrentSensorFaulted
            ? OpenVisionLanguageService.T("Sensor.ClearFaultHint")
            : _controlOwner == SimulationControlOwner.Manual
                ? IsRunning
                    ? OpenVisionLanguageService.T("Sensor.ManualRunningHint")
                    : OpenVisionLanguageService.T("Sensor.ManualPausedHint")
                : IsRunning || _automaticRun.IsActive ||
                  _currentSequence?.Status == SequenceExecutionStatus.Running
                    ? OpenVisionLanguageService.T("Sensor.ResetForManualHint")
                    : OpenVisionLanguageService.T("Sensor.StartManualHint");
    public bool CanForceSensorOn => CanUseManualSensor
        && CurrentSensorSignal?.OverrideValue != true;
    public bool CanForceSensorOff => CanUseManualSensor
        && CurrentSensorSignal?.OverrideValue != false;
    public bool CanClearSensorForce => CanUseManualSensor && IsCurrentSensorManuallyForced;
    public bool HasSelectedPneumaticCylinder => CurrentCylinderSnapshot is not null;
    public bool IsCurrentCylinderInterlocked => CurrentCylinderSnapshot is { } cylinder
        && FaultManager.ActiveFaults.Any(fault =>
            fault.Kind == SimulationFaultKind.CylinderTravelBlocked
            && string.Equals(fault.TargetId, cylinder.Id, StringComparison.Ordinal));
    public string CurrentCylinderInterlockText => OpenVisionLanguageService.T(
        IsCurrentCylinderInterlocked ? "Cylinder.InterlockBlocked" : "Cylinder.InterlockReady");
    public string CylinderCommissioningHintText => !HasSelectedPneumaticCylinder
        ? OpenVisionLanguageService.T("Cylinder.NoCylinderHint")
        : IsCurrentCylinderInterlocked
            ? OpenVisionLanguageService.T("Cylinder.ClearInterlockHint")
            : _controlOwner == SimulationControlOwner.Manual
                ? IsRunning
                    ? OpenVisionLanguageService.T("Cylinder.ManualRunningHint")
                    : OpenVisionLanguageService.T("Cylinder.ManualPausedHint")
                : IsRunning || _automaticRun.IsActive ||
                  _currentSequence?.Status == SequenceExecutionStatus.Running
                    ? OpenVisionLanguageService.T("Cylinder.ResetForManualHint")
                    : OpenVisionLanguageService.T("Cylinder.StartManualHint");
    public bool CanExtendCylinder => CanUseManualCylinder
        && CurrentCylinderSnapshot?.CylinderState is not PneumaticCylinderState.Extending
            and not PneumaticCylinderState.Extended;
    public bool CanRetractCylinder => CanUseManualCylinder
        && CurrentCylinderSnapshot?.CylinderState is not PneumaticCylinderState.Retracting
            and not PneumaticCylinderState.Retracted;
    public bool HasSelectedConveyor => CurrentConveyorSnapshot is not null;
    public string ConveyorCommissioningHintText => !HasSelectedConveyor
        ? OpenVisionLanguageService.T("Conveyor.NoConveyorHint")
        : _controlOwner == SimulationControlOwner.Manual
            ? IsRunning
                ? OpenVisionLanguageService.T("Conveyor.ManualRunningHint")
                : OpenVisionLanguageService.T("Conveyor.ManualPausedHint")
            : IsRunning || _automaticRun.IsActive ||
              _currentSequence?.Status == SequenceExecutionStatus.Running
                ? OpenVisionLanguageService.T("Conveyor.ResetForManualHint")
                : OpenVisionLanguageService.T("Conveyor.StartManualHint");
    public bool CanRunConveyorForward => CanUseManualConveyor
        && CurrentConveyorSnapshot is { } conveyor
        && (conveyor.ConveyorRunning != true
            || conveyor.ConveyorDirection != ConveyorDirection.Forward);
    public bool CanRunConveyorReverse => CanUseManualConveyor
        && CurrentConveyorSnapshot is { } conveyor
        && (conveyor.ConveyorRunning != true
            || conveyor.ConveyorDirection != ConveyorDirection.Reverse);
    public bool CanStopConveyor => CanUseManualConveyor
        && CurrentConveyorSnapshot?.ConveyorRunning == true;
    public string CurrentCameraName => _currentCamera?.Name
        ?? _project.Devices.FirstOrDefault(device => device.Kind == DeviceKind.Camera)?.Name
        ?? OpenVisionLanguageService.T("Shell.NoCamera");
    public string CurrentCameraStateText => _currentCamera is null
        ? OpenVisionLanguageService.T("Shell.Unavailable")
        : LocalizeRuntimeState(_currentCamera.State.ToString());
    public string CurrentCameraResultText => _currentCamera?.Result?.Decision switch
    {
        PlaceholderInspectionDecision.Pass => OpenVisionLanguageService.T("Shell.ResultPass"),
        PlaceholderInspectionDecision.Fail => OpenVisionLanguageService.T("Shell.ResultFail"),
        _ when _currentCamera?.State is VirtualCameraState.Exposing or VirtualCameraState.Transferring
            => OpenVisionLanguageService.T("Shell.ResultPending"),
        _ => "—"
    };
    public string CurrentCameraFrameText => _currentCamera?.CurrentAcquisitionId ?? "—";
    public string CurrentCameraExposureTicksText => (_currentCamera?.ExposureTicksRemaining ?? 0)
        .ToString(CultureInfo.InvariantCulture);
    public string CurrentCameraTransferTicksText => (_currentCamera?.TransferTicksRemaining ?? 0)
        .ToString(CultureInfo.InvariantCulture);
    public string CurrentCameraSourceText => CurrentCameraDefinition?.Camera?.SingleImageSource?
        .SourceRelativePath ?? "—";
    public string CurrentCameraFrameHashText => _currentCamera?.FrameEvidence?.ContentSha256 ?? "—";
    public string CurrentCameraInspectionIdText =>
        _currentCamera?.Result?.InspectionEvidence?.InspectionId ?? "—";
    public string CurrentCameraInspectionMessageText =>
        _currentCamera?.Result?.InspectionEvidence?.Message ?? "—";
    public string CurrentCameraInspectionMetricsText =>
        _currentCamera?.Result?.InspectionEvidence?.Metrics is { Count: > 0 } metrics
            ? string.Join(
                " · ",
                metrics.OrderBy(metric => metric.Key, StringComparer.Ordinal)
                    .Select(metric =>
                        $"{metric.Key}={metric.Value.ToString("G17", CultureInfo.InvariantCulture)}"))
            : "—";
    public string CurrentVisionEvidenceHashText => _latestVisionEvidence?.ShortEvidenceHash ?? "—";
    public string VisionEvidenceStatusText => _activeVisionEvidenceRecorder is not null
        ? OpenVisionLanguageService.T("Camera.EvidenceCapturing")
        : OpenVisionLanguageService.T(_visionEvidenceArtifactState switch
        {
            BatchArtifactState.Saved => "Camera.EvidenceSaved",
            BatchArtifactState.Restored => "Camera.EvidenceRestored",
            BatchArtifactState.StaleRejected => "Camera.EvidenceStale",
            BatchArtifactState.SaveFailed => "Camera.EvidenceSaveFailed",
            _ => "Camera.EvidenceNone"
        });
    public string VisionEvidenceComparisonText => _visionEvidenceComparison switch
    {
        null => OpenVisionLanguageService.T("Camera.EvidenceNoComparison"),
        { IsMatch: true } => OpenVisionLanguageService.T("Camera.EvidenceMatch"),
        { MismatchCode: { } mismatchCode } => string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Camera.EvidenceMismatch"),
            mismatchCode),
        _ => OpenVisionLanguageService.T("Camera.EvidenceNoComparison")
    };
    public string CurrentCameraEvidenceDetailsText => string.Join(
        Environment.NewLine,
        $"{OpenVisionLanguageService.T("Camera.InspectionId")}: {CurrentCameraInspectionIdText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMessage")}: {CurrentCameraInspectionMessageText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMetrics")}: {CurrentCameraInspectionMetricsText}",
        $"{OpenVisionLanguageService.T("Camera.ExecutionEvidence")}: {CurrentVisionEvidenceHashText}",
        VisionEvidenceStatusText,
        VisionEvidenceComparisonText);
    internal DeterministicVisionExecutionEvidencePackage? LatestVisionEvidence =>
        _latestVisionEvidence;
    internal DeterministicVisionExecutionComparison? VisionEvidenceComparison =>
        _visionEvidenceComparison;
    public string CameraCommissioningHintText => CurrentCameraDefinition is null
        ? OpenVisionLanguageService.T("Camera.NoCameraHint")
        : !HasUsableCameraImageSource
            ? OpenVisionLanguageService.T("Camera.ConfigureSourceHint")
            : _controlOwner == SimulationControlOwner.Manual
                ? IsRunning
                    ? OpenVisionLanguageService.T("Camera.PauseBeforeTriggerHint")
                    : _currentCamera?.State is VirtualCameraState.Exposing or VirtualCameraState.Transferring
                        ? OpenVisionLanguageService.T("Camera.StepAcquisitionHint")
                        : OpenVisionLanguageService.T("Camera.TriggerReadyHint")
                : IsRunning || _automaticRun.IsActive ||
                  _currentSequence?.Status == SequenceExecutionStatus.Running
                    ? OpenVisionLanguageService.T("Camera.ResetForManualHint")
                    : OpenVisionLanguageService.T("Camera.StartManualHint");
    public bool CanStartManualCameraControl => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !IsRunning
        && _controlOwner != SimulationControlOwner.Manual
        && !_automaticRun.IsActive
        && _currentSequence?.Status != SequenceExecutionStatus.Running
        && _currentCamera is not null;
    public bool CanTriggerCamera => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !IsRunning
        && _engine.CurrentSnapshot.RunMode == SimulationRunMode.Paused
        && _controlOwner == SimulationControlOwner.Manual
        && _currentCamera?.State is VirtualCameraState.Idle or VirtualCameraState.FrameReady
        && HasUsableCameraImageSource;
    public string CurrentSequenceName => ResolveSequenceName(_currentSequence?.SequenceId);
    public string CurrentSequenceStateText => _currentSequence is null
        ? OpenVisionLanguageService.T("Shell.NotConfigured")
        : LocalizeRuntimeState(_currentSequence.Status.ToString());
    public string CurrentSequenceStepText => ResolveStepName(
        _currentSequence?.SequenceId,
        _currentSequence?.CurrentStepId);
    public string AutomaticRunStateText => !_automaticRun.IsConfigured
        ? OpenVisionLanguageService.T("Shell.AutomaticRunNotConfigured")
        : _automaticRun.IsWaitingForRepeat
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Shell.AutomaticRunWaiting"),
                _automaticRun.RemainingDelayTicks)
            : _automaticRun.IsActive
                ? OpenVisionLanguageService.T("Shell.AutomaticRunRunning")
                : OpenVisionLanguageService.T("Shell.AutomaticRunReady");
    public string CompletedCycleCountText => _automaticRun.CompletedCycleCount
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string CycleStartSignalText => FormatSignal(_cycleStartInput);
    public string CycleActiveSignalText => FormatSignal(_cycleActiveOutput);
    public string CycleDoneSignalText => FormatSignal(_cycleDoneOutput);

    public ICommand StartBlankLayoutCommand => _startBlankLayoutCommand ??= CreateRelayCommand(
        _ => StartBlankLayout(),
        _ => IsStartupChoiceVisible);

    public ICommand OpenBundledSampleCommand => _openBundledSampleCommand ??= CreateAsyncCommand(
        async _ => await OpenBundledSampleAsync(),
        _ => IsStartupChoiceVisible && _startupSamplePath is not null);

    public ICommand NewProjectCommand => _newProjectCommand ??= CreateAsyncCommand(async _ =>
    {
        await CreateNewProjectAsync();
    }, _ => !_isApplyingProject && !IsValidationBusy);

    public ICommand OpenProjectCommand => _openProjectCommand ??= CreateAsyncCommand(async _ =>
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Machine projects (*.ovmachine)|*.ovmachine|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenProjectReplacingCurrentAsync(dialog.FileName);
    }, _ => !_isApplyingProject && !IsValidationBusy);

    public ICommand SaveProjectCommand => _saveProjectCommand ??= CreateAsyncCommand(async _ =>
    {
        await TrySaveCurrentProjectAsync();
    }, _ => !_isApplyingProject && !IsValidationBusy && !string.IsNullOrWhiteSpace(_project.Name));

    public ICommand SaveProjectAsCommand => _saveProjectAsCommand ??= CreateAsyncCommand(
        async _ => await TrySaveCurrentProjectAsync(saveAs: true),
        _ => !_isApplyingProject && !IsValidationBusy && !string.IsNullOrWhiteSpace(_project.Name));

    internal async Task<bool> OpenProjectAsync(string path)
    {
        var project = await _projectStore.LoadAsync(path);
        if (!await ApplyProjectAsync(project))
        {
            return false;
        }

        _currentProjectPath = Path.GetFullPath(path);
        RefreshProjectIdentity();
        CameraImageSourceEditor.Load(_project, _currentProjectPath, _selectedCameraId);
        RestoreBatchArtifacts();
        RestoreCommissioningResult();
        RestoreVisionEvidence();
        IsStartupChoiceVisible = false;
        Log("Project", $"Opened {project.Name}");
        return true;
    }

    internal async Task<bool> OpenProjectReplacingCurrentAsync(string path) =>
        await TryResolveUnsavedChangesAsync() && await OpenProjectAsync(path);

    internal async Task<bool> CreateNewProjectAsync()
    {
        if (!await TryResolveUnsavedChangesAsync()
            || !await ApplyProjectAsync(new MachineProjectDocument { Name = "Untitled" }))
        {
            return false;
        }

        _currentProjectPath = null;
        RefreshProjectIdentity();
        CameraImageSourceEditor.SetProjectPath(null, isSaved: true);
        ClearVisionEvidence();
        IsStartupChoiceVisible = false;
        SelectedLeftToolTabIndex = 1;
        Log("Project", "Created new project");
        return true;
    }

    private void StartBlankLayout()
    {
        IsStartupChoiceVisible = false;
        SelectedLeftToolTabIndex = 1;
        StatusMessage = OpenVisionLanguageService.T("Scene.BlankLayoutReadyStatus");
        InvalidateCommands();
    }

    private async Task OpenBundledSampleAsync()
    {
        if (_startupSamplePath is null)
        {
            return;
        }

        var project = await _projectStore.LoadAsync(_startupSamplePath);
        if (!await ApplyProjectAsync(project))
        {
            return;
        }

        _currentProjectPath = null;
        CameraImageSourceEditor.SetProjectPath(null, isSaved: true);
        IsStartupChoiceVisible = false;
        SelectedLeftToolTabIndex = 0;
        StatusMessage = OpenVisionLanguageService.T("Scene.SampleOpenedStatus");
        Log("Project", $"Opened bundled sample · {project.Name}");
        InvalidateCommands();
    }

    internal async Task SaveProjectAsync(string path)
    {
        await CommitFocusedEditorAsync();
        SimulationWorkspace.SaveProjectScenario(_project.Simulation);
        await _projectStore.SaveAsync(_project, path);
        _currentProjectPath = Path.GetFullPath(path);
        RefreshProjectIdentity();
        CameraImageSourceEditor.SetProjectPath(_currentProjectPath, isSaved: true);
        NotifyCameraCommissioningChanged();
        RelinkBatchProjectPath(_currentProjectPath);
        PersistBatchArtifacts();
        RelinkCommissioningProjectPath(_currentProjectPath);
        PersistCommissioningResult();
        RelinkVisionEvidenceProjectPath(_currentProjectPath);
        RefreshVisionEvidenceContext();
        PersistVisionEvidence();
        AcceptCurrentProjectAsSaved();
        Log("Project", $"Saved {_project.Name}");
    }

    private async Task<bool> CreateSemiconductorRecipeCopyAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Machine projects (*.ovmachine)|*.ovmachine|All files (*.*)|*.*",
                FileName = recipe.FileName,
                DefaultExt = ".ovmachine",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            destinationPath = dialog.FileName;
        }

        if (!await TryResolveUnsavedChangesAsync())
        {
            return false;
        }

        var sourcePath = Path.GetFullPath(recipe.SourcePath);
        var copyPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourcePath, copyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                OpenVisionLanguageService.T("Gallery.TemplateOverwriteRejected"));
        }

        var project = await _projectStore.LoadAsync(sourcePath);
        var now = DateTimeOffset.UtcNow;
        project.Id = Guid.NewGuid().ToString("n");
        project.CreatedAt = now;
        project.ModifiedAt = now;
        await _projectStore.SaveAsync(project, copyPath);
        if (!await OpenProjectAsync(copyPath))
        {
            return false;
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.CopySucceeded"),
            recipe.DisplayName);
        Log("Project", $"Created semiconductor recipe copy · {recipe.DisplayName}");
        return true;
    }

    private async Task<bool> TrySaveCurrentProjectAsync(bool saveAs = false)
    {
        try
        {
            if (!saveAs && _currentProjectPath is not null)
            {
                await SaveProjectAsync(_currentProjectPath);
                return true;
            }

            return await TrySaveProjectAsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = OpenVisionLanguageService.T(
                "Project.SaveFailedStatus",
                "프로젝트를 저장하지 못했습니다",
                "The project could not be saved");
            Log("Project", $"Save failed · {exception.Message}");
            ShowProjectSaveFailure(exception.Message);
            return false;
        }
    }

    private async Task<bool> TrySaveProjectAsAsync()
    {
        await CommitFocusedEditorAsync();
        var dialog = new SaveFileDialog
        {
            Filter = "Machine projects (*.ovmachine)|*.ovmachine|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_project.Name)
                ? "machine-project.ovmachine"
                : $"{_project.Name}.ovmachine"
        };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        await SaveProjectAsync(dialog.FileName);
        return true;
    }

    private static async Task CommitFocusedEditorAsync()
    {
        Keyboard.ClearFocus();
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            await dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
        }
    }

    public ICommand RunCommand => _runCommand ??= CreateAsyncCommand(async _ =>
    {
        if (!await EnsureRuntimeDefinitionAppliedAsync())
        {
            return;
        }

        IsDesignMode = false;
        if (HasAutomaticRun)
        {
            if (_engine.CurrentSnapshot.AutomaticRun.IsActive)
            {
                var resumeCommand = new PlayCommand();
                var resumeResult = await _engine.EnqueueCommandAsync(resumeCommand);
                if (!resumeResult.IsAccepted)
                {
                    Log(
                        "Simulation",
                        $"Resume rejected · {resumeResult.ErrorCode}: {resumeResult.Detail}");
                    return;
                }

                IsRunning = true;
                StatusMessage = "Automatic simulation running";
                Log("Simulation", $"Simulation resumed · {ShortCommandId(resumeCommand)}");
                return;
            }

            var automaticCommand = new StartAutomaticRunCommand();
            var automaticResult = await _engine.EnqueueCommandAsync(automaticCommand);
            if (!automaticResult.IsAccepted)
            {
                Log(
                    "Simulation",
                    $"Automatic run rejected · {automaticResult.ErrorCode}: {automaticResult.Detail}");
                return;
            }

            IsRunning = true;
            StatusMessage = "Automatic simulation running";
            Log("Simulation", $"Simulation ON requested · {ShortCommandId(automaticCommand)}");
            return;
        }

        if (!await EnsureActiveSequenceStartedAsync())
        {
            return;
        }

        var command = new PlayCommand();
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            Log("Simulation", $"Run rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        IsRunning = true;
        StatusMessage = "Simulation running";
        Log("Simulation", $"Run requested · {ShortCommandId(command)}");
    }, _ => CanRun());

    public ICommand PauseCommand => _pauseCommand ??= CreateAsyncCommand(async _ =>
    {
        var command = new PauseCommand();
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            Log("Simulation", $"Pause rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        IsRunning = false;
        StatusMessage = "Simulation paused";
        Log("Simulation", $"Pause requested · {ShortCommandId(command)}");
    }, _ => !_isApplyingProject && !IsValidationBusy && IsRunMode && IsRunning);

    public ICommand StopCommand => PauseCommand;

    public ICommand StepCommand => _stepCommand ??= CreateAsyncCommand(async _ =>
    {
        if (_controlOwner == SimulationControlOwner.Manual)
        {
            // Manual commissioning advances the already-authored runtime state only.
        }
        else if (HasAutomaticRun)
        {
            if (!_engine.CurrentSnapshot.AutomaticRun.IsActive)
            {
                return;
            }
        }
        else if (!await EnsureActiveSequenceStartedAsync())
        {
            return;
        }

        var command = new StepCommand();
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            Log("Simulation", $"Step rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }
        Log("Simulation", $"Single 5 ms tick applied · {ShortCommandId(command)}");
    }, _ => CanStep());

    public ICommand ResetCommand => _resetCommand ??= CreateAsyncCommand(async _ =>
    {
        var command = new ResetCommand();
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            Log("Simulation", $"Reset rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        IsRunning = false;
        _activeVisionEvidenceRecorder = null;
        RefreshVisionEvidenceContext();
        StatusMessage = "Simulation reset";
        Log("Simulation", $"Reset applied · {ShortCommandId(command)}");
    }, _ => !_isApplyingProject && !IsValidationBusy && IsRunMode && !_runtimeDefinitionDirty);

    public ICommand StartTestScenarioCommand => _startTestScenarioCommand ??= CreateAsyncCommand(
        async _ => await StartTestScenarioAsync(),
        _ => CanStartTestScenario);

    public ICommand StopTestScenarioCommand => _stopTestScenarioCommand ??= CreateAsyncCommand(
        async _ => await StopTestScenarioAsync(),
        _ => CanStopTestScenario);

    public ICommand ReplayTestScenarioCommand => _replayTestScenarioCommand ??= CreateAsyncCommand(
        async _ => await ReplayTestScenarioAsync(),
        _ => CanReplayTestScenario);

    public ICommand RunScenarioBatchCommand => _runScenarioBatchCommand ??= CreateAsyncCommand(
        async _ => await RunScenarioBatchAsync(),
        _ => CanRunScenarioBatch);

    public ICommand CancelScenarioBatchCommand => _cancelScenarioBatchCommand ??= CreateRelayCommand(
        _ => _batchCancellation?.Cancel(),
        _ => _isBatchRunning);

    public ICommand AcceptBatchBaselineCommand => _acceptBatchBaselineCommand ??= CreateRelayCommand(
        _ => AcceptBatchBaseline(),
        _ => CanAcceptBatchBaseline);

    public ICommand ClearBatchBaselineCommand => _clearBatchBaselineCommand ??= CreateRelayCommand(
        _ => ClearBatchBaseline(),
        _ => CanClearBatchBaseline);

    public ICommand NavigateToBatchMismatchCommand => _navigateToBatchMismatchCommand ??= CreateRelayCommand(
        _ => NavigateToBatchMismatch(),
        _ => CanNavigateToBatchMismatch);

    public ICommand CycleStartCommand => _cycleStartCommand ??= CreateAsyncCommand(async _ =>
    {
        var command = new SetVirtualInputCommand(CycleStartInputId, true);
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            Log("I/O", $"Cycle Start rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }
        StatusMessage = "Cycle Start input applied";
        Log("I/O", $"Cycle Start input requested · {ShortCommandId(command)}");
    }, _ =>
        IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && HasEmbeddedSequence
        && HasCycleStartInput
        && ReadSignal(_engine.CurrentSnapshot, CycleStartInputId) == false
        && GetActiveSequenceSnapshot()?.Status == SequenceExecutionStatus.Running);

    public ICommand StartManualEquipmentControlCommand => _startManualEquipmentControlCommand ??=
        CreateAsyncCommand(
            async _ => await StartManualEquipmentControlAsync(),
            _ => CanStartManualEquipmentControl);

    public ICommand StartManualCameraControlCommand => _startManualCameraControlCommand ??=
        CreateAsyncCommand(
            async _ => await StartManualCameraControlAsync(),
            _ => CanStartManualCameraControl);

    public ICommand TriggerCameraCommand => _triggerCameraCommand ??= CreateAsyncCommand(
        async _ => await TriggerSelectedCameraAsync(),
        _ => CanTriggerCamera);

    public ICommand MoveAxisAbsoluteCommand => _moveAxisAbsoluteCommand ??=
        CreateAsyncCommand(async _ =>
        {
            if (_currentAxis is not null && TryGetAxisTargetPosition(out var target))
            {
                await DispatchAxisCommandAsync(
                    new MoveAbsoluteCommand(_currentAxis.Id, target),
                    "Axis.ActionMove");
            }
        }, _ => CanMoveAxisAbsolute);

    public ICommand MoveAxisRelativeCommand => _moveAxisRelativeCommand ??=
        CreateAsyncCommand(async _ =>
        {
            if (_currentAxis is not null && TryGetAxisRelativeDistance(out var distance))
            {
                await DispatchAxisCommandAsync(
                    new MoveRelativeCommand(_currentAxis.Id, distance),
                    "Axis.ActionMoveRelative");
            }
        }, _ => CanMoveAxisRelative);

    public ICommand MoveAxisVelocityCommand => _moveAxisVelocityCommand ??=
        CreateAsyncCommand(async _ =>
        {
            if (_currentAxis is not null && TryGetAxisCommandVelocity(out var velocity))
            {
                await DispatchAxisCommandAsync(
                    new MoveVelocityCommand(_currentAxis.Id, velocity),
                    "Axis.ActionMoveVelocity");
            }
        }, _ => CanMoveAxisVelocity);

    public ICommand BeginAxisJogNegativeCommand => _beginAxisJogNegativeCommand ??= CreateRelayCommand(
        _ => BeginAxisJog(AxisJogDirection.Negative),
        _ => CanJogAxis);

    public ICommand BeginAxisJogPositiveCommand => _beginAxisJogPositiveCommand ??= CreateRelayCommand(
        _ => BeginAxisJog(AxisJogDirection.Positive),
        _ => CanJogAxis);

    public ICommand EndAxisJogCommand => _endAxisJogCommand ??= CreateAsyncCommand(
        async _ => await EndAxisJogAsync(),
        _ => _axisJogInteractionActive);

    public ICommand HomeAxisCommand => _homeAxisCommand ??= CreateAsyncCommand(async _ =>
    {
        if (_currentAxis is not null)
        {
            await DispatchAxisCommandAsync(
                new HomeAxisCommand(_currentAxis.Id),
                "Axis.ActionHome");
        }
    }, _ => CanUseManualAxis && !_axisJogInteractionActive &&
        _currentAxis?.State != AxisState.Moving);

    public ICommand StopAxisMotionCommand => _stopAxisMotionCommand ??=
        CreateAsyncCommand(
            async _ => await StopAxisMotionAsync(),
            _ => CanUseManualAxis &&
                (_axisJogInteractionActive || _currentAxis?.State == AxisState.Moving));

    public ICommand RunMultiAxisCommissioningRecipeCommand =>
        _runMultiAxisCommissioningRecipeCommand ??= CreateAsyncCommand(
            async _ => await RunMultiAxisCommissioningRecipeAsync(),
            _ => CanRunMultiAxisCommissioningRecipe);

    public ICommand StopMultiAxisCommissioningRecipeCommand =>
        _stopMultiAxisCommissioningRecipeCommand ??= CreateAsyncCommand(
            async _ => await StopMultiAxisCommissioningRecipeAsync(),
            _ => CanStopMultiAxisCommissioningRecipe);

    public ICommand ValidateMultiAxisCommissioningRecipeCommand =>
        _validateMultiAxisCommissioningRecipeCommand ??= CreateAsyncCommand(
            async _ => await ValidateMultiAxisCommissioningRecipeAsync(),
            _ => CanValidateMultiAxisCommissioningRecipe);

    public ICommand AcceptCommissioningBaselineCommand =>
        _acceptCommissioningBaselineCommand ??= CreateRelayCommand(
            _ => AcceptCommissioningBaseline(),
            _ => CanAcceptCommissioningBaseline);

    public ICommand ClearCommissioningBaselineCommand =>
        _clearCommissioningBaselineCommand ??= CreateRelayCommand(
            _ => ClearCommissioningBaseline(),
            _ => CanClearCommissioningBaseline);

    public ICommand NavigateToCommissioningMismatchCommand =>
        _navigateToCommissioningMismatchCommand ??= CreateRelayCommand(
            _ => NavigateToCommissioningMismatch(),
            _ => CanNavigateToCommissioningMismatch);

    public ICommand ForceSensorOnCommand => _forceSensorOnCommand ??=
        CreateAsyncCommand(
            async _ => await SetSensorForceAsync(true),
            _ => CanForceSensorOn);

    public ICommand ForceSensorOffCommand => _forceSensorOffCommand ??=
        CreateAsyncCommand(
            async _ => await SetSensorForceAsync(false),
            _ => CanForceSensorOff);

    public ICommand ClearSensorForceCommand => _clearSensorForceCommand ??=
        CreateAsyncCommand(
            async _ => await SetSensorForceAsync(null),
            _ => CanClearSensorForce);

    public ICommand ExtendCylinderCommand => _extendCylinderCommand ??=
        CreateAsyncCommand(
            async _ => await SetCylinderCommandAsync(extend: true),
            _ => CanExtendCylinder);

    public ICommand RetractCylinderCommand => _retractCylinderCommand ??=
        CreateAsyncCommand(
            async _ => await SetCylinderCommandAsync(extend: false),
            _ => CanRetractCylinder);

    public ICommand RunConveyorForwardCommand => _runConveyorForwardCommand ??=
        CreateAsyncCommand(
            async _ => await SetConveyorCommandAsync(true, ConveyorDirection.Forward),
            _ => CanRunConveyorForward);

    public ICommand RunConveyorReverseCommand => _runConveyorReverseCommand ??=
        CreateAsyncCommand(
            async _ => await SetConveyorCommandAsync(true, ConveyorDirection.Reverse),
            _ => CanRunConveyorReverse);

    public ICommand StopConveyorCommand => _stopConveyorCommand ??=
        CreateAsyncCommand(
            async _ => await SetConveyorCommandAsync(
                false,
                CurrentConveyorSnapshot?.ConveyorDirection ?? ConveyorDirection.Forward),
            _ => CanStopConveyor);

    internal bool BeginAxisJog(AxisJogDirection direction)
    {
        if (!CanJogAxis || _axisJogInteractionActive || _currentAxis is null)
        {
            return false;
        }

        _axisJogInteractionActive = true;
        _axisJogAxisId = _currentAxis.Id;
        _axisJogStartTask = DispatchAxisCommandAsync(
            new JogAxisCommand(_axisJogAxisId, direction),
            direction == AxisJogDirection.Positive
                ? "Axis.ActionJogPositive"
                : "Axis.ActionJogNegative");
        NotifyAxisCommissioningChanged();
        return true;
    }

    internal Task EndAxisJogAsync()
    {
        if (!_axisJogInteractionActive || _axisJogAxisId is null || _axisJogStartTask is null)
        {
            return Task.CompletedTask;
        }

        _axisJogInteractionActive = false;
        var axisId = _axisJogAxisId;
        var startTask = _axisJogStartTask;
        _axisJogAxisId = null;
        _axisJogStartTask = null;
        NotifyAxisCommissioningChanged();
        return StopAxisJogAfterStartAsync(axisId, startTask);
    }

    public ICommand AddLayoutComponentCommand => _addLayoutComponentCommand ??=
        CreateRelayCommand(AddLayoutComponent, _ => IsSceneEditable && !_isApplyingProject);

    public ICommand DeleteLayoutComponentCommand => _deleteLayoutComponentCommand ??=
        CreateRelayCommand(
            _ => DeleteSelectedLayoutComponent(),
            _ => IsSceneEditable && !_isApplyingProject && Layout.SelectionCount == 1 && Layout.SelectedItem?.Component is not null);

    public ICommand SceneSelectionRequestedCommand => _sceneSelectionRequestedCommand ??=
        CreateRelayCommand(HandleSceneSelectionRequested);

    public ICommand SceneMoveRequestedCommand => _sceneMoveRequestedCommand ??=
        CreateRelayCommand(HandleSceneMoveRequested);

    public ICommand SceneMarqueeSelectionRequestedCommand => _sceneMarqueeSelectionRequestedCommand ??=
        CreateRelayCommand(HandleSceneMarqueeSelectionRequested);

    public ICommand SceneTransformRequestedCommand => _sceneTransformRequestedCommand ??=
        CreateRelayCommand(HandleSceneTransformRequested);

    public ICommand SceneLibraryComponentDropRequestedCommand => _sceneLibraryComponentDropRequestedCommand ??=
        CreateRelayCommand(HandleSceneLibraryComponentDropRequested);

    public ICommand NudgeLayoutComponentCommand => _nudgeLayoutComponentCommand ??=
        CreateRelayCommand(
            NudgeSelectedLayoutComponent,
            _ => IsSceneEditable && !_isApplyingProject && Layout.SelectedItem?.Component is not null);

    public ICommand AlignLayoutSelectionCommand => _alignLayoutSelectionCommand ??=
        CreateRelayCommand(
            AlignLayoutSelection,
            _ => IsSceneEditable && !_isApplyingProject && Layout.HasMultipleSelection);

    public ICommand ChangeLayoutLayerOrderCommand => _changeLayoutLayerOrderCommand ??=
        CreateRelayCommand(
            ChangeLayoutLayerOrder,
            parameter => IsSceneEditable &&
                !_isApplyingProject &&
                parameter is string value &&
                Enum.TryParse(value, out LayoutLayerOrder order) &&
                Layout.CanChangeSelectionLayerOrder(order));

    public ICommand UndoLayoutEditCommand => _undoLayoutEditCommand ??= CreateRelayCommand(
        _ => UndoLayoutEdit(),
        _ => IsSceneEditable && !_isApplyingProject && _layoutEditHistory.CanUndo);

    public ICommand RedoLayoutEditCommand => _redoLayoutEditCommand ??= CreateRelayCommand(
        _ => RedoLayoutEdit(),
        _ => IsSceneEditable && !_isApplyingProject && _layoutEditHistory.CanRedo);

    public ICommand CopyLayoutSelectionCommand => _copyLayoutSelectionCommand ??= CreateRelayCommand(
        _ => CopyLayoutSelection(),
        _ => IsSceneEditable && !_isApplyingProject && Layout.HasSelection && Layout.Definition is not null);

    public ICommand DuplicateLayoutSelectionCommand => _duplicateLayoutSelectionCommand ??= CreateRelayCommand(
        _ => DuplicateLayoutSelection(),
        _ => IsSceneEditable && !_isApplyingProject && Layout.HasSelection && Layout.Definition is not null);

    public ICommand PasteLayoutSelectionCommand => _pasteLayoutSelectionCommand ??= CreateRelayCommand(
        _ => PasteLayoutSelection(),
        _ => IsSceneEditable && !_isApplyingProject && _layoutClipboard.HasContent && Layout.Definition is not null);

    public ICommand PreviousDryRunPlaybackStepCommand => _previousDryRunPlaybackStepCommand ??=
        CreateRelayCommand(_ => MoveDryRunPlayback(-1), _ => _isDryRunPlaybackActive && _dryRunPlaybackIndex > 0);

    public ICommand NextDryRunPlaybackStepCommand => _nextDryRunPlaybackStepCommand ??=
        CreateRelayCommand(
            _ => MoveDryRunPlayback(1),
            _ => _isDryRunPlaybackActive && _dryRunPlaybackIndex + 1 < _dryRunPlaybackSteps.Length);

    public ICommand ExitDryRunPlaybackCommand => _exitDryRunPlaybackCommand ??=
        CreateRelayCommand(_ => ExitDryRunPlayback(), _ => _isDryRunPlaybackActive);

    public ICommand ReturnToProcessPlanCommand => _returnToProcessPlanCommand ??=
        CreateRelayCommand(_ => ReturnToProcessPlan(), _ => CanReturnToProcessPlan());

    public ICommand PreviousProcessPlanReviewStepCommand => _previousProcessPlanReviewStepCommand ??=
        CreateRelayCommand(_ => MoveProcessPlanReview(-1), _ => CanMoveProcessPlanReview(-1));

    public ICommand NextProcessPlanReviewStepCommand => _nextProcessPlanReviewStepCommand ??=
        CreateRelayCommand(_ => MoveProcessPlanReview(1), _ => CanMoveProcessPlanReview(1));

    public ICommand ExitCommand => _exitCommand ??= CreateRelayCommand(_ => Application.Current.Shutdown());

    private void AddLayoutComponent(object? parameter)
    {
        if (parameter is not LayoutComponentKind kind)
        {
            return;
        }

        TryAddLayoutComponent(kind);
    }

    public bool TryAddLayoutComponent(
        LayoutComponentKind kind,
        double? worldX = null,
        double? worldY = null)
    {
        if (!IsSceneEditable || _isApplyingProject || worldX.HasValue != worldY.HasValue ||
            worldX is { } x && !double.IsFinite(x) ||
            worldY is { } y && !double.IsFinite(y))
        {
            return false;
        }

        var before = _layoutAuthoringState;

        var previousActiveLayoutId = _project.Simulation.ActiveLayoutId;
        var previousLayoutCount = _project.Layouts.Count;
        var previousAxisCount = _project.Axes.Count;
        var previousDeviceCount = _project.Devices.Count;
        var previousChannelCount = _project.Channels.Count;
        var layout = GetOrCreateActiveLayout();
        if (layout is null)
        {
            return false;
        }
        var previousComponentCount = layout.Components.Count;

        LayoutComponentDefinition? component = kind switch
        {
            LayoutComponentKind.MachineFrame => CreateMachineFrame(layout),
            LayoutComponentKind.LinearStage => CreateLinearStage(layout),
            LayoutComponentKind.RotaryStage => CreateRotaryStage(layout),
            LayoutComponentKind.DigitalSensor => CreateDigitalSensor(layout),
            LayoutComponentKind.PneumaticCylinder => CreatePneumaticCylinder(),
            LayoutComponentKind.Conveyor => CreateConveyor(),
            LayoutComponentKind.Workpiece => CreateWorkpiece(layout),
            _ => null
        };

        if (component is null)
        {
            RollBackLayoutAddition(
                layout,
                previousActiveLayoutId,
                previousLayoutCount,
                previousComponentCount,
                previousAxisCount,
                previousDeviceCount,
                previousChannelCount);
            return false;
        }

        if (worldX is { } dropX && worldY is { } dropY)
        {
            PlaceNewLayoutComponent(layout, component, dropX, dropY);
        }
        else if (UsesIndependentDefaultPlacement(component.Kind))
        {
            var position = FindNearestAvailableGridPosition(layout, component);
            PlaceNewLayoutComponent(layout, component, position.X, position.Y);
        }

        layout.Components.Add(component);
        var validation = new MachineProjectLayoutValidator().Validate(_project);
        if (!validation.IsValid)
        {
            RollBackLayoutAddition(
                layout,
                previousActiveLayoutId,
                previousLayoutCount,
                previousComponentCount,
                previousAxisCount,
                previousDeviceCount,
                previousChannelCount);
            var error = validation.Errors[0];
            StatusMessage = "Component was not added because its definition is invalid";
            Log("Layout", $"Add rejected · {error.Code}: {error.Message}");
            RefreshDefinitionPresentation(null);
            return false;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(component.Id);
        CommitLayoutMutation(before);
        StatusMessage = $"Added {component.Name}";
        Log("Layout", $"Added {component.Kind} '{component.Id}'");
        return true;
    }

    private void HandleSceneSelectionRequested(object? parameter)
    {
        if (parameter is not SceneSelectionRequest request)
        {
            return;
        }

        Layout.ExtendSelection(request.Item, request.Toggle);
    }

    private void HandleSceneMoveRequested(object? parameter)
    {
        if (parameter is not SceneMoveRequest request)
        {
            return;
        }

        switch (request.Action)
        {
            case SceneViewportMoveAction.Begin:
                Layout.BeginSelectionDrag();
                break;
            case SceneViewportMoveAction.Update:
                Layout.UpdateSelectionDrag(request.Delta.X, request.Delta.Y);
                break;
            case SceneViewportMoveAction.Commit:
                Layout.CompleteSelectionDrag();
                break;
            case SceneViewportMoveAction.Cancel:
                Layout.CancelSelectionDrag();
                break;
        }
    }

    private void HandleSceneMarqueeSelectionRequested(object? parameter)
    {
        if (parameter is not SceneMarqueeSelectionRequest request)
        {
            return;
        }

        Layout.SelectRegion(request.Items, request.Mode);
    }

    private void HandleSceneTransformRequested(object? parameter)
    {
        if (parameter is not SceneTransformRequest request)
        {
            return;
        }

        switch (request.Action)
        {
            case SceneViewportMoveAction.Begin:
                Layout.BeginSelectionTransform(request.Handle);
                break;
            case SceneViewportMoveAction.Update:
                Layout.UpdateSelectionTransform(
                    request.WorldPoint.X,
                    request.WorldPoint.Y,
                    request.PreserveAspectRatio);
                break;
            case SceneViewportMoveAction.Commit:
                Layout.CompleteSelectionTransform();
                break;
            case SceneViewportMoveAction.Cancel:
                Layout.CancelSelectionTransform();
                break;
        }
    }

    private void HandleSceneLibraryComponentDropRequested(object? parameter)
    {
        if (parameter is not SceneLibraryComponentDropRequest request)
        {
            return;
        }

        TryAddLayoutComponent(request.Kind, request.WorldPoint.X, request.WorldPoint.Y);
    }

    private void PlaceNewLayoutComponent(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component,
        double worldX,
        double worldY)
    {
        var x = SnapLayoutCoordinate(layout, worldX);
        var y = SnapLayoutCoordinate(layout, worldY);
        component.Transform.X = x;
        component.Transform.Y = y;

        if (component.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            var axis = _project.Axes.FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                component.BehaviorBindingId,
                StringComparison.Ordinal));
            if (axis is not null)
            {
                axis.Position = new Coordinate3D(x, y, axis.Position.Z);
            }
            return;
        }

        var device = _project.Devices.FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            component.BehaviorBindingId,
            StringComparison.Ordinal));
        if (device is not null)
        {
            device.MountPosition = new Coordinate3D(x, y, device.MountPosition.Z);
        }
    }

    private static double SnapLayoutCoordinate(MachineLayoutDefinition layout, double value) =>
        layout.SnapToGrid && double.IsFinite(layout.GridSize) && layout.GridSize > 0
            ? Math.Round(value / layout.GridSize, MidpointRounding.AwayFromZero) * layout.GridSize
            : value;

    private static bool UsesIndependentDefaultPlacement(LayoutComponentKind kind) =>
        kind is not LayoutComponentKind.MachineFrame and not LayoutComponentKind.Workpiece;

    private static (double X, double Y) FindNearestAvailableGridPosition(
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component)
    {
        var defaultX = SnapLayoutCoordinate(layout, component.Transform.X);
        var defaultY = SnapLayoutCoordinate(layout, component.Transform.Y);
        if (!layout.SnapToGrid || !double.IsFinite(layout.GridSize) || layout.GridSize <= 0)
        {
            return (defaultX, defaultY);
        }

        var obstacles = layout.Components
            .Where(existing => existing.Kind != LayoutComponentKind.MachineFrame)
            .ToArray();
        if (obstacles.Length == 0 || !OverlapsAny(component, defaultX, defaultY, obstacles))
        {
            return (defaultX, defaultY);
        }

        var maximumRadius = (int)Math.Ceiling(obstacles.Max(existing =>
            (Math.Abs(existing.Transform.Y - defaultY) +
             GetVerticalHalfExtent(component) +
             GetVerticalHalfExtent(existing)) / layout.GridSize)) + 1;

        for (var radius = 1; radius <= maximumRadius; radius++)
        {
            var offsets = Enumerable.Range(-radius, (radius * 2) + 1)
                .SelectMany(x => Enumerable.Range(-radius, (radius * 2) + 1)
                    .Where(y => Math.Max(Math.Abs(x), Math.Abs(y)) == radius)
                    .Select(y => (X: x, Y: y)))
                .OrderBy(offset => (offset.X * offset.X) + (offset.Y * offset.Y))
                .ThenBy(offset => offset.Y)
                .ThenBy(offset => offset.X);
            foreach (var offset in offsets)
            {
                var x = defaultX + (offset.X * layout.GridSize);
                var y = defaultY + (offset.Y * layout.GridSize);
                if (!OverlapsAny(component, x, y, obstacles))
                {
                    return (x, y);
                }
            }
        }

        return (defaultX, defaultY);
    }

    private static bool OverlapsAny(
        LayoutComponentDefinition component,
        double x,
        double y,
        IReadOnlyList<LayoutComponentDefinition> obstacles) =>
        obstacles.Any(existing =>
            Math.Abs(existing.Transform.X - x) <
                GetHorizontalHalfExtent(component) + GetHorizontalHalfExtent(existing) &&
            Math.Abs(existing.Transform.Y - y) <
                GetVerticalHalfExtent(component) + GetVerticalHalfExtent(existing));

    private static double GetHorizontalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Cos(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Sin(radians)) * component.Size.Height / 2d);
    }

    private static double GetVerticalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Sin(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Cos(radians)) * component.Size.Height / 2d);
    }

    private void RollBackLayoutAddition(
        MachineLayoutDefinition layout,
        string? previousActiveLayoutId,
        int previousLayoutCount,
        int previousComponentCount,
        int previousAxisCount,
        int previousDeviceCount,
        int previousChannelCount)
    {
        RemoveAddedItems(layout.Components, previousComponentCount);
        RemoveAddedItems(_project.Axes, previousAxisCount);
        RemoveAddedItems(_project.Devices, previousDeviceCount);
        RemoveAddedItems(_project.Channels, previousChannelCount);
        RemoveAddedItems(_project.Layouts, previousLayoutCount);
        _project.Simulation.ActiveLayoutId = previousActiveLayoutId;
    }

    private static void RemoveAddedItems<T>(List<T> items, int originalCount)
    {
        if (items.Count > originalCount)
        {
            items.RemoveRange(originalCount, items.Count - originalCount);
        }
    }

    private MachineLayoutDefinition? GetOrCreateActiveLayout()
    {
        var activeLayoutId = _project.Simulation.ActiveLayoutId;
        if (!string.IsNullOrWhiteSpace(activeLayoutId))
        {
            var active = _project.Layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, activeLayoutId, StringComparison.Ordinal));
            if (active is not null)
            {
                return active;
            }

            StatusMessage = $"Active layout '{activeLayoutId}' was not found";
            Log("Layout", "Select a valid active layout before adding components");
            return null;
        }

        if (_project.Layouts.Count == 1)
        {
            var existing = _project.Layouts[0];
            _project.Simulation.ActiveLayoutId = existing.Id;
            return existing;
        }

        if (_project.Layouts.Count > 1)
        {
            StatusMessage = "Select an active layout before adding components";
            Log("Layout", "simulation.activeLayoutId is required for projects with multiple layouts");
            return null;
        }

        var layout = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            GridSize = 10,
            SnapToGrid = true
        };
        _project.Layouts.Add(layout);
        _project.Simulation.ActiveLayoutId = layout.Id;
        return layout;
    }

    private LayoutComponentDefinition CreateMachineFrame(MachineLayoutDefinition layout)
    {
        var index = NextOrdinal("frame", AllLayoutComponentIds());
        return new LayoutComponentDefinition
        {
            Id = $"frame-{index}",
            Name = $"Machine Frame {index}",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 150 + ((index - 1) * 20), Y = 200 },
            Size = new Size2D { Width = 520, Height = 300 },
            ZIndex = -100
        };
    }

    private LayoutComponentDefinition CreateLinearStage(MachineLayoutDefinition layout)
        => CreateAxisStage(layout, AxisKind.Linear);

    private LayoutComponentDefinition CreateRotaryStage(MachineLayoutDefinition layout)
        => CreateAxisStage(layout, AxisKind.Rotary);

    private LayoutComponentDefinition CreateAxisStage(
        MachineLayoutDefinition layout,
        AxisKind axisKind)
    {
        var boundAxisIds = layout.Components
            .Where(item => item.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
            .Select(item => item.BehaviorBindingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var axis = _project.Axes.FirstOrDefault(item =>
            item.Kind == axisKind && !boundAxisIds.Contains(item.Id));
        if (axis is null)
        {
            var axisIndex = NextOrdinal("axis", _project.Axes.Select(item => item.Id));
            bool rotary = axisKind == AxisKind.Rotary;
            axis = new VirtualAxisDefinition
            {
                Id = $"axis-{axisIndex}",
                Name = rotary ? $"Rotation Axis {axisIndex}" : $"Transfer Axis {axisIndex}",
                Kind = axisKind,
                Unit = rotary ? "deg" : "mm",
                HomePosition = 0,
                SoftLimitMin = rotary ? -360 : 0,
                SoftLimitMax = rotary ? 360 : 300,
                MaxVelocity = rotary ? 240 : 180,
                MaxAcceleration = rotary ? 900 : 600,
                MaxDeceleration = rotary ? 900 : 600,
                FollowingErrorLimit = VirtualAxisDefinition.DefaultFollowingErrorLimit,
                Position = new Coordinate3D(40, 180 + ((axisIndex - 1) * 90), 0)
            };
            _project.Axes.Add(axis);
        }

        bool isRotary = axisKind == AxisKind.Rotary;
        string stagePrefix = isRotary ? "rotary-stage" : "stage";
        var stageIndex = NextOrdinal(stagePrefix, AllLayoutComponentIds());
        return new LayoutComponentDefinition
        {
            Id = $"{stagePrefix}-{stageIndex}",
            Name = isRotary ? $"Rotary Stage {stageIndex}" : $"Linear Stage {stageIndex}",
            Kind = isRotary ? LayoutComponentKind.RotaryStage : LayoutComponentKind.LinearStage,
            Transform = new Transform2D
            {
                X = 40,
                Y = 180 + ((stageIndex - 1) * 90)
            },
            Size = isRotary
                ? new Size2D { Width = 72, Height = 72 }
                : new Size2D { Width = 84, Height = 48 },
            ZIndex = 20,
            BehaviorBindingId = axis.Id
        };
    }

    private LayoutComponentDefinition? CreateDigitalSensor(MachineLayoutDefinition layout)
    {
        var target = Layout.SelectedItem?.Component is
            { Kind: LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage or LayoutComponentKind.Workpiece } selected
            ? selected
            : layout.Components.FirstOrDefault(item => item.Kind == LayoutComponentKind.Workpiece)
              ?? layout.Components.FirstOrDefault(item => item.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage);
        if (target is null)
        {
            StatusMessage = "Add a Workpiece or Stage before adding a Digital Sensor";
            Log("Layout", "Digital Sensor requires a Workpiece or Stage target");
            return null;
        }

        var sensorIndex = NextSensorOrdinal();
        var componentId = $"sensor-{sensorIndex}";
        var channelId = $"di.{componentId}";
        var deviceId = $"device.{componentId}";

        _project.Channels.Add(new ChannelDefinition
        {
            Id = channelId,
            Name = $"Stage Sensor {sensorIndex}",
            Kind = ChannelKind.DigitalInput,
            InitialValue = 0
        });
        _project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Stage Sensor {sensorIndex}",
            Kind = DeviceKind.Sensor,
            MountPosition = new Coordinate3D(target.Transform.X + 180, target.Transform.Y, 0),
            ChannelIds = { channelId },
            Sensor = new DigitalSensorDefinition
            {
                OutputChannelId = channelId,
                TargetComponentId = target.Id,
                OnDelayMilliseconds = 0,
                OffDelayMilliseconds = 0
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Digital Sensor {sensorIndex}",
            Kind = LayoutComponentKind.DigitalSensor,
            Transform = new Transform2D { X = target.Transform.X + 180, Y = target.Transform.Y },
            Size = new Size2D { Width = 18, Height = 84 },
            ZIndex = 30,
            BehaviorBindingId = deviceId
        };
    }

    private LayoutComponentDefinition CreateConveyor()
    {
        var conveyorIndex = NextConveyorOrdinal();
        var componentId = $"conveyor-{conveyorIndex}";
        var deviceId = $"device.{componentId}";
        var runChannelId = $"do.{componentId}.run";
        var reverseChannelId = $"do.{componentId}.reverse";
        var y = 260 + ((conveyorIndex - 1) * 110);

        _project.Channels.Add(new ChannelDefinition
        {
            Id = runChannelId,
            Name = $"Conveyor {conveyorIndex} Run",
            Kind = ChannelKind.DigitalOutput,
            InitialValue = 0
        });
        _project.Channels.Add(new ChannelDefinition
        {
            Id = reverseChannelId,
            Name = $"Conveyor {conveyorIndex} Reverse",
            Kind = ChannelKind.DigitalOutput,
            InitialValue = 0
        });
        _project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Conveyor {conveyorIndex}",
            Kind = DeviceKind.Conveyor,
            MountPosition = new Coordinate3D(220, y, 0),
            ChannelIds = { runChannelId, reverseChannelId },
            Conveyor = new ConveyorDefinition
            {
                RunCommandChannelId = runChannelId,
                ReverseCommandChannelId = reverseChannelId,
                SpeedUnitsPerSecond = 120
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Conveyor {conveyorIndex}",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D { X = 220, Y = y },
            Size = new Size2D { Width = 360, Height = 80 },
            ZIndex = 10,
            BehaviorBindingId = deviceId
        };
    }

    private LayoutComponentDefinition? CreateWorkpiece(MachineLayoutDefinition layout)
    {
        var conveyor = layout.Components.FirstOrDefault(item => item.Kind == LayoutComponentKind.Conveyor);
        if (conveyor is null)
        {
            StatusMessage = "Add a Conveyor before adding a Workpiece";
            Log("Layout", "Workpiece requires an explicit Conveyor carrier");
            return null;
        }

        var workpieceIndex = NextWorkpieceOrdinal();
        var componentId = $"workpiece-{workpieceIndex}";
        var deviceId = $"device.{componentId}";
        double radians = conveyor.Transform.RotationDegrees * Math.PI / 180d;
        double initialOffset = -((conveyor.Size.Width - 42) / 2d) + 20;
        double x = conveyor.Transform.X + (initialOffset * Math.Cos(radians));
        double y = conveyor.Transform.Y + (initialOffset * Math.Sin(radians));

        _project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Workpiece {workpieceIndex}",
            Kind = DeviceKind.Workpiece,
            MountPosition = new Coordinate3D(x, y, 0),
            Workpiece = new WorkpieceDefinition
            {
                Type = "Generic Part",
                ConveyorComponentId = conveyor.Id,
                InspectionState = WorkpieceInspectionState.Pending
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Workpiece {workpieceIndex}",
            Kind = LayoutComponentKind.Workpiece,
            Transform = new Transform2D
            {
                X = x,
                Y = y,
                RotationDegrees = conveyor.Transform.RotationDegrees
            },
            Size = new Size2D { Width = 42, Height = 42 },
            ZIndex = 20,
            BehaviorBindingId = deviceId
        };
    }

    private LayoutComponentDefinition CreatePneumaticCylinder()
    {
        var cylinderIndex = NextCylinderOrdinal();
        var componentId = $"cylinder-{cylinderIndex}";
        var deviceId = $"device.{componentId}";
        var commandChannelId = $"do.{componentId}.extend";
        var extendedChannelId = $"di.{componentId}.extended";
        var retractedChannelId = $"di.{componentId}.retracted";
        var y = 110 + ((cylinderIndex - 1) * 70);

        _project.Channels.Add(new ChannelDefinition
        {
            Id = commandChannelId,
            Name = $"Cylinder {cylinderIndex} Extend Command",
            Kind = ChannelKind.DigitalOutput,
            InitialValue = 0
        });
        _project.Channels.Add(new ChannelDefinition
        {
            Id = extendedChannelId,
            Name = $"Cylinder {cylinderIndex} Extended",
            Kind = ChannelKind.DigitalInput,
            InitialValue = 0
        });
        _project.Channels.Add(new ChannelDefinition
        {
            Id = retractedChannelId,
            Name = $"Cylinder {cylinderIndex} Retracted",
            Kind = ChannelKind.DigitalInput,
            InitialValue = 1
        });
        _project.Devices.Add(new DeviceDefinition
        {
            Id = deviceId,
            Name = $"Pneumatic Cylinder {cylinderIndex}",
            Kind = DeviceKind.Cylinder,
            MountPosition = new Coordinate3D(360, y, 0),
            ChannelIds = { commandChannelId, extendedChannelId, retractedChannelId },
            Cylinder = new PneumaticCylinderDefinition
            {
                ExtendCommandChannelId = commandChannelId,
                ExtendedSensorChannelId = extendedChannelId,
                RetractedSensorChannelId = retractedChannelId,
                ExtendDurationMilliseconds = 300,
                RetractDurationMilliseconds = 250,
                ExtendedSensorDelayMilliseconds = 10,
                RetractedSensorDelayMilliseconds = 10,
                Stroke = 80
            }
        });

        return new LayoutComponentDefinition
        {
            Id = componentId,
            Name = $"Pneumatic Cylinder {cylinderIndex}",
            Kind = LayoutComponentKind.PneumaticCylinder,
            Transform = new Transform2D { X = 360, Y = y },
            Size = new Size2D { Width = 96, Height = 36 },
            ZIndex = 25,
            BehaviorBindingId = deviceId
        };
    }

    private void DeleteSelectedLayoutComponent()
    {
        var before = _layoutAuthoringState;
        var component = Layout.SelectedItem?.Component;
        var layout = Layout.Definition;
        if (component is null || layout is null)
        {
            return;
        }

        var dependentSensorComponent = _project.Layouts
            .SelectMany(definition => definition.Components)
            .Where(candidate => candidate.Kind == LayoutComponentKind.DigitalSensor)
            .Select(candidate => new
            {
                Component = candidate,
                Device = _project.Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, candidate.BehaviorBindingId, StringComparison.Ordinal))
            })
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Device?.Sensor?.TargetComponentId,
                    component.Id,
                    StringComparison.Ordinal));
        if (dependentSensorComponent is not null)
        {
            StatusMessage = $"Remove sensor '{dependentSensorComponent.Component.Name}' before removing {component.Name}";
            return;
        }

        var dependentWorkpiece = _project.Layouts
            .SelectMany(definition => definition.Components)
            .Where(candidate => candidate.Kind == LayoutComponentKind.Workpiece)
            .Select(candidate => new
            {
                Component = candidate,
                Device = _project.Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, candidate.BehaviorBindingId, StringComparison.Ordinal))
            })
            .FirstOrDefault(candidate => string.Equals(
                candidate.Device?.Workpiece?.ConveyorComponentId,
                component.Id,
                StringComparison.Ordinal));
        if (dependentWorkpiece is not null)
        {
            StatusMessage = $"Remove workpiece '{dependentWorkpiece.Component.Name}' before removing {component.Name}";
            return;
        }

        layout.Components.Remove(component);

        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        CommitLayoutMutation(before);
        StatusMessage = component.Kind is LayoutComponentKind.DigitalSensor or LayoutComponentKind.PneumaticCylinder
            ? $"Removed {component.Name}; its device and channel definitions were retained"
            : $"Removed {component.Name}";
        Log("Layout", $"Removed {component.Kind} '{component.Id}' without cascading into project definitions");
    }

    private void NudgeSelectedLayoutComponent(object? parameter)
    {
        if (parameter is not string direction)
        {
            return;
        }

        Layout.NudgeSelection(direction);
    }

    private void AlignLayoutSelection(object? parameter)
    {
        if (parameter is not string value ||
            !Enum.TryParse(value, out LayoutSelectionAlignment alignment))
        {
            return;
        }

        if (Layout.AlignSelection(alignment))
        {
            StatusMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "Status.LayoutAligned",
                    "선택 장비를 {0} 기준으로 정렬했습니다.",
                    "Aligned selected components to {0}."),
                OpenVisionLanguageService.T($"Alignment.{alignment}"));
        }
    }

    private void ChangeLayoutLayerOrder(object? parameter)
    {
        if (parameter is not string value ||
            !Enum.TryParse(value, out LayoutLayerOrder order) ||
            !Layout.ChangeSelectionLayerOrder(order))
        {
            return;
        }

        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Status.LayerOrderChanged",
                "선택 장비의 레이어 순서를 {0}(으)로 변경했습니다.",
                "Changed selected equipment layer order to {0}."),
            OpenVisionLanguageService.T($"LayerOrder.{order}"));
    }

    private void UndoLayoutEdit()
    {
        if (_layoutEditHistory.TryUndo(out var state) && state is not null)
        {
            RestoreLayoutAuthoringState(state, "Undid layout edit");
        }
    }

    private void RedoLayoutEdit()
    {
        if (_layoutEditHistory.TryRedo(out var state) && state is not null)
        {
            RestoreLayoutAuthoringState(state, "Redid layout edit");
        }
    }

    private void CopyLayoutSelection()
    {
        var definition = Layout.Definition;
        var componentIds = Layout.SelectedItems
            .Where(item => item.Component is not null)
            .Select(item => item.Id)
            .ToArray();
        if (definition is null || componentIds.Length == 0)
        {
            return;
        }

        var copiedCount = _layoutClipboard.Copy(_project, definition, componentIds);
        StatusMessage = $"Copied {copiedCount} layout component(s)";
        InvalidateCommands();
    }

    private void DuplicateLayoutSelection()
    {
        CopyLayoutSelection();
        PasteLayoutSelection();
    }

    private void PasteLayoutSelection()
    {
        if (!_layoutClipboard.HasContent || Layout.Definition is not { } targetLayout)
        {
            return;
        }

        var before = _layoutAuthoringState;
        var result = _layoutClipboard.Paste(_project, targetLayout);
        if (!result.IsSuccess)
        {
            _layoutEditHistory.Restore(_project, before);
            RefreshDefinitionPresentation(null);
            Layout.SelectMany(before.SelectedComponentIds, before.PrimaryComponentId);
            _layoutAuthoringState = before;
            StatusMessage = "Copied components were not pasted because their definitions are invalid";
            if (result.Error is { } error)
            {
                Log("Layout", $"Paste rejected · {error.Code}: {error.Message}");
            }
            return;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        Layout.SelectMany(result.ComponentIds, result.ComponentIds[^1]);
        CommitLayoutMutation(before);
        StatusMessage = $"Pasted {result.ComponentIds.Count} layout component(s)";
        Log("Layout", $"Pasted {result.ComponentIds.Count} component(s) with cloned behavior bindings");
    }

    private LayoutAuthoringState CaptureLayoutAuthoringState() => _layoutEditHistory.Capture(
        _project,
        Layout.SelectedItems.Select(item => item.Id),
        Layout.SelectedItem?.Id);

    private void CommitLayoutMutation(LayoutAuthoringState before)
    {
        var after = CaptureLayoutAuthoringState();
        _layoutEditHistory.Record(before, after);
        _layoutAuthoringState = after;
        InvalidateCommands();
    }

    private void RestoreLayoutAuthoringState(LayoutAuthoringState state, string status)
    {
        _isRestoringLayoutHistory = true;
        try
        {
            _layoutEditHistory.Restore(_project, state);
            RefreshDefinitionPresentation(null);
            Layout.SelectMany(state.SelectedComponentIds, state.PrimaryComponentId);
            _layoutAuthoringState = state;
            MarkProjectChanged();
            UpdateRunToolAvailability();
            StatusMessage = status;
            Log("Layout", status);
        }
        finally
        {
            _isRestoringLayoutHistory = false;
            InvalidateCommands();
        }
    }

    private void RefreshDefinitionPresentation(string? selectedComponentId)
    {
        ProjectTree.LoadProject(_project);
        Layout.Load(_project);
        if (selectedComponentId is not null)
        {
            Layout.Select(selectedComponentId);
        }
        RecipeConnections.Load(_project, Layout.SelectedItem?.Id);
        SequenceEditor.RefreshAuthoringTargets();
        Properties.Show(Layout.SelectedItem?.Component);
        OnPropertyChanged(nameof(AxisCountText));
        OnPropertyChanged(nameof(LayoutComponentCountText));
        OnPropertyChanged(nameof(CameraCountText));
        OnPropertyChanged(nameof(HasAuthoredLayout));
        OnPropertyChanged(nameof(SelectionStatusText));
        InvalidateCommands();
    }

    private static int NextOrdinal(string prefix, IEnumerable<string> ids)
    {
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (existing.Contains($"{prefix}-{ordinal}"))
        {
            ordinal++;
        }
        return ordinal;
    }

    private IEnumerable<string> AllLayoutComponentIds() =>
        _project.Layouts.SelectMany(layout => layout.Components).Select(component => component.Id);

    private int NextSensorOrdinal()
    {
        var componentIds = AllLayoutComponentIds().ToHashSet(StringComparer.Ordinal);
        var deviceIds = _project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var channelIds = _project.Channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"sensor-{ordinal}")
               || deviceIds.Contains($"device.sensor-{ordinal}")
               || channelIds.Contains($"di.sensor-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private int NextCylinderOrdinal()
    {
        var componentIds = AllLayoutComponentIds().ToHashSet(StringComparer.Ordinal);
        var deviceIds = _project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var channelIds = _project.Channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"cylinder-{ordinal}")
               || deviceIds.Contains($"device.cylinder-{ordinal}")
               || channelIds.Contains($"do.cylinder-{ordinal}.extend")
               || channelIds.Contains($"di.cylinder-{ordinal}.extended")
               || channelIds.Contains($"di.cylinder-{ordinal}.retracted"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private int NextConveyorOrdinal()
    {
        var componentIds = AllLayoutComponentIds().ToHashSet(StringComparer.Ordinal);
        var deviceIds = _project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var channelIds = _project.Channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"conveyor-{ordinal}")
               || deviceIds.Contains($"device.conveyor-{ordinal}")
               || channelIds.Contains($"do.conveyor-{ordinal}.run")
               || channelIds.Contains($"do.conveyor-{ordinal}.reverse"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private int NextWorkpieceOrdinal()
    {
        var componentIds = AllLayoutComponentIds().ToHashSet(StringComparer.Ordinal);
        var deviceIds = _project.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (componentIds.Contains($"workpiece-{ordinal}")
               || deviceIds.Contains($"device.workpiece-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }

    private async Task StartAndConsumeRuntimeAsync(SimulationRuntimeConfiguration initialRuntime)
    {
        try
        {
            await _engine.StartAsync(_runtimeCancellation.Token);
            var snapshotTask = ConsumeSnapshotsAsync();
            var eventTask = ConsumeEventsAsync();
            var configuration = await _engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(initialRuntime),
                _runtimeCancellation.Token);
            if (!configuration.IsAccepted)
            {
                await DispatchAsync(() =>
                    Log("Runtime", $"Initial configuration rejected · {configuration.Detail}"));
            }

            if (configuration.IsAccepted)
            {
                await DispatchAsync(() =>
                {
                    ApplyMonitorSnapshot(_engine.CurrentSnapshot);
                    RestoreBatchArtifacts();
                });
            }

            await Task.WhenAll(snapshotTask, eventTask);
        }
        catch (OperationCanceledException) when (_runtimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await DispatchAsync(() => HandleCommandException(exception));
        }
    }

    private async Task ConsumeSnapshotsAsync()
    {
        var monitorStopwatch = Stopwatch.StartNew();
        await foreach (var snapshot in _engine.SnapshotReader.ReadAllAsync(_runtimeCancellation.Token))
        {
            SceneSnapshots.Publish(snapshot);
            if (snapshot.RunMode == SimulationRunMode.RealTime
                && monitorStopwatch.Elapsed < MonitorRefreshInterval)
            {
                continue;
            }

            monitorStopwatch.Restart();
            await DispatchAsync(() => ApplyMonitorSnapshot(snapshot));
        }
    }

    private async Task ConsumeEventsAsync()
    {
        await foreach (var runtimeEvent in _engine.EventReader.ReadAllAsync(_runtimeCancellation.Token))
        {
            await DispatchAsync(() => LogRuntimeEvent(runtimeEvent));
        }
    }

    private async Task<bool> ApplyProjectAsync(MachineProjectDocument project)
    {
        if (_isApplyingProject)
        {
            return false;
        }

        _isApplyingProject = true;
        UpdateRunToolAvailability();
        InvalidateCommands();
        try
        {
            SimulationRuntimeConfiguration runtime;
            try
            {
                runtime = BuildRuntimeConfiguration(project);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                Log("Project", $"Project rejected · {exception.Message}");
                return false;
            }

            var result = await _engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(runtime));
            if (!result.IsAccepted)
            {
                Log("Project", $"Project rejected · {result.ErrorCode}: {result.Detail}");
                return false;
            }

            _project = project;
            _axisTargetAxisId = null;
            _latestBatchResult = null;
            _acceptedBatchBaseline = null;
            _batchWasCanceled = false;
            _batchCompletedRuns = 0;
            _batchArtifactState = BatchArtifactState.None;
            _latestCommissioningResult = null;
            _acceptedCommissioningBaseline = null;
            _commissioningHistory = DeterministicMultiAxisCommissioningResultHistory.Empty(project.Id);
            _selectedCommissioningHistoryEntry = null;
            _commissioningBaselineComparison = null;
            _isCommissioningValidationRunning = false;
            _commissioningCompletedRuns = 0;
            _commissioningArtifactState = BatchArtifactState.None;
            ClearVisionEvidence();
            ApplyProjectPresentation(project);
            _layoutEditHistory.Clear();
            _layoutClipboard.Clear();
            _layoutAuthoringState = CaptureLayoutAuthoringState();
            RaiseBatchPresentationChanged();
            RaiseCommissioningValidationChanged(invalidateCommands: false);
            _runtimeDefinitionDirty = false;
            UpdateRunToolAvailability();
            IsRunning = false;
            IsDesignMode = true;
            ApplyMonitorSnapshot(_engine.CurrentSnapshot);
            AcceptCurrentProjectAsSaved();
            return true;
        }
        finally
        {
            _isApplyingProject = false;
            UpdateRunToolAvailability();
            InvalidateCommands();
        }
    }

    private async Task<bool> EnsureRuntimeDefinitionAppliedAsync()
    {
        if (!_runtimeDefinitionDirty)
        {
            return true;
        }

        SimulationRuntimeConfiguration runtime;
        try
        {
            runtime = BuildRuntimeConfiguration(_project);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            StatusMessage = "Machine definition is invalid";
            Log("Project", $"Simulation build rejected · {exception.Message}");
            return false;
        }

        var result = await _engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(runtime));
        if (!result.IsAccepted)
        {
            StatusMessage = "Machine definition is invalid";
            Log("Project", $"Simulation build rejected · {result.ErrorCode}: {result.Detail}");
            return false;
        }

        _runtimeDefinitionDirty = false;
        UpdateRunToolAvailability();
        ApplyMonitorSnapshot(_engine.CurrentSnapshot);
        Log("Runtime", "Authored machine rebuilt for Simulation ON");
        return true;
    }

    private void ApplyProjectPresentation(MachineProjectDocument project)
    {
        ExitDryRunPlayback();
        ClearProcessPlanReturnContext();
        if (!project.Devices.Any(device =>
                device.Kind == DeviceKind.Camera
                && string.Equals(device.Id, _selectedCameraId, StringComparison.Ordinal)))
        {
            _selectedCameraId = project.Devices.FirstOrDefault(device =>
                device.Kind == DeviceKind.Camera)?.Id;
        }
        EnsureSelectedCameraRecipe();
        CameraImageSourceEditor.Load(project, _currentProjectPath, _selectedCameraId);
        AxisDriveTuningEditor = null;
        ProjectTree.LoadProject(project);
        Layout.Load(project);
        RecipeConnections.Load(project, Layout.SelectedItem?.Id);
        SequenceEditor.Load(project);
        SimulationWorkspace.LoadProjectScenario(project.Simulation);
        MultiAxisCommissioningRecipe.Load(project);
        Properties.ShowNode(null);
        RefreshProjectIdentity();
        StatusMessage = "Ready";
        OnPropertyChanged(nameof(ProjectStatusText));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(AxisCountText));
        OnPropertyChanged(nameof(LayoutComponentCountText));
        OnPropertyChanged(nameof(CameraCountText));
        OnPropertyChanged(nameof(HasVirtualCamera));
        OnPropertyChanged(nameof(VirtualCameras));
        OnPropertyChanged(nameof(SelectedVirtualCamera));
        OnPropertyChanged(nameof(SelectedCameraId));
        OnPropertyChanged(nameof(CurrentCameraRecipes));
        OnPropertyChanged(nameof(SelectedCameraRecipe));
        OnPropertyChanged(nameof(HasEmbeddedSequence));
        OnPropertyChanged(nameof(HasAutomaticRun));
        OnPropertyChanged(nameof(HasAuthoredLayout));
        OnPropertyChanged(nameof(HasCycleStartInput));
        OnPropertyChanged(nameof(ControlOwnerHelpText));
        OnPropertyChanged(nameof(SceneTitleText));
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
        OnPropertyChanged(nameof(CurrentCameraName));
        OnPropertyChanged(nameof(CurrentCameraStateText));
        OnPropertyChanged(nameof(CurrentCameraResultText));
        OnPropertyChanged(nameof(CurrentCameraFrameText));
        NotifyCameraCommissioningChanged(invalidateCommands: false);
        NotifyMultiAxisCommissioningRecipeChanged();
        InvalidateCommands();
    }

    private static SimulationRuntimeConfiguration BuildRuntimeConfiguration(
        MachineProjectDocument project)
    {
        var result = new MachineProjectRuntimeCompiler(SimulationFixedStep).Compile(project);
        if (result.IsSuccess)
        {
            return result.Configuration!;
        }

        var errors = string.Join(
            "; ",
            result.Errors.Select(error =>
                $"{error.Code}({error.TargetId ?? "project"}): {error.Message}"));
        throw new InvalidDataException(errors);
    }

    private void ApplyMonitorSnapshot(SimulationSnapshot snapshot)
    {
        _simulationTime = snapshot.SimulationTime;
        _tickIndex = snapshot.TickIndex;
        _controlOwner = snapshot.ControlOwner;
        IsRunning = snapshot.RunMode is SimulationRunMode.RealTime or SimulationRunMode.FastForward;

        if (Layout.SelectedItem?.Component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            _currentAxis = snapshot.Axes.FirstOrDefault(axis =>
                axis.Id == Layout.SelectedItem.BehaviorBindingId);
        }
        else
        {
            var selectedTreeAxisId = ProjectTree.SelectedNode is
                { Kind: global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.Axis } selected
                ? selected.Id
                : null;
            _currentAxis = snapshot.Axes.FirstOrDefault(axis => axis.Id == selectedTreeAxisId)
                ?? snapshot.Axes.FirstOrDefault();
        }
        SynchronizeAxisTargetInput();
        var projectCameraId = _project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera)?.Id;
        var preferredCameraId = _selectedCameraId ?? projectCameraId;
        _currentCamera = snapshot.Cameras.FirstOrDefault(camera => camera.Id == preferredCameraId)
            ?? snapshot.Cameras.FirstOrDefault();
        _currentSequence = GetActiveSequenceSnapshot(snapshot);
        _automaticRun = snapshot.AutomaticRun;
        _conditionScenario = snapshot.ConditionScenario;
        MultiAxisCommissioningRecipe.ApplyAxisSnapshots(snapshot.Axes);
        _isSynchronizingSimulationWorkspace = true;
        try
        {
            SimulationWorkspace.EnsureScenarioTarget(
                snapshot.Axes.Select(axis => axis.Id)
                    .Concat(snapshot.LayoutComponents.Select(component => component.Id)));
            SimulationWorkspace.UpdateFinalEquipmentTargets(
                snapshot.Axes.Select(axis => axis.Id)
                    .Concat(snapshot.LayoutComponents.Select(component => component.Id)));
            SimulationWorkspace.EnsureScheduledFaultTarget(
                new SimulationFaultTargetCatalog()
                    .GetTargets(snapshot, SimulationWorkspace.ScheduledFaultKind)
                    .Select(target => target.Id));
            SimulationWorkspace.EnsureRecoverySequence(
                snapshot.Sequences.Select(sequence => sequence.SequenceId));
        }
        finally
        {
            _isSynchronizingSimulationWorkspace = false;
        }
        _cycleStartInput = ReadSignal(snapshot, CycleStartInputId);
        _cycleActiveOutput = ReadSignal(snapshot, CycleActiveOutputId);
        _cycleDoneOutput = ReadSignal(snapshot, CycleDoneOutputId);
        DigitalIo.ApplySnapshot(snapshot);
        FaultManager.ApplySnapshot(snapshot);
        if (_activeVisionEvidenceRecorder is not null)
        {
            TryCompleteVisionEvidence(snapshot);
        }
        NotifyProjectAndRuntimeChanged();
    }

    private void NotifyProjectAndRuntimeChanged()
    {
        OnPropertyChanged(nameof(SimulationStatusText));
        OnPropertyChanged(nameof(TickStatusText));
        OnPropertyChanged(nameof(CurrentAxisName));
        OnPropertyChanged(nameof(CurrentAxisStateText));
        OnPropertyChanged(nameof(CurrentAxisPositionText));
        OnPropertyChanged(nameof(CurrentAxisVelocityText));
        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        NotifySensorCommissioningChanged(invalidateCommands: false);
        NotifyCylinderCommissioningChanged(invalidateCommands: false);
        NotifyConveyorCommissioningChanged(invalidateCommands: false);
        OnPropertyChanged(nameof(CurrentCameraName));
        OnPropertyChanged(nameof(CurrentCameraStateText));
        OnPropertyChanged(nameof(CurrentCameraResultText));
        OnPropertyChanged(nameof(CurrentCameraFrameText));
        NotifyCameraCommissioningChanged(invalidateCommands: false);
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStateText));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
        OnPropertyChanged(nameof(AutomaticRunStateText));
        OnPropertyChanged(nameof(ConditionScenario));
        OnPropertyChanged(nameof(ConditionScenarioTargets));
        OnPropertyChanged(nameof(ScheduledFaultTargets));
        OnPropertyChanged(nameof(RecoverySequences));
        OnPropertyChanged(nameof(ConditionScenarioStateText));
        OnPropertyChanged(nameof(ConditionScenarioProgressText));
        OnPropertyChanged(nameof(ConditionScenarioHealthText));
        OnPropertyChanged(nameof(CompletedCycleCountText));
        OnPropertyChanged(nameof(CycleStartSignalText));
        OnPropertyChanged(nameof(CycleActiveSignalText));
        OnPropertyChanged(nameof(CycleDoneSignalText));
        OnPropertyChanged(nameof(ControlOwnerText));
        OnPropertyChanged(nameof(SceneControlText));
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanStopTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
        NotifyMultiAxisCommissioningRecipeChanged(invalidateCommands: false);
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(RunStatusText));
        InvalidateCommands();
    }

    private bool CanRun()
    {
        if (_isApplyingProject || IsValidationBusy || IsRunning)
        {
            return false;
        }

        if (_runtimeDefinitionDirty)
        {
            return _project.Axes.Count > 0 || _project.Sequences.Count > 0;
        }

        if (HasAutomaticRun)
        {
            var automaticRun = _engine.CurrentSnapshot.AutomaticRun;
            var sequenceStatus = GetActiveSequenceSnapshot()?.Status;
            return automaticRun.IsConfigured
                && (automaticRun.IsActive || sequenceStatus == SequenceExecutionStatus.Ready);
        }

        if (!HasEmbeddedSequence)
        {
            return _project.Axes.Count > 0;
        }

        var status = GetActiveSequenceSnapshot()?.Status;
        return status is SequenceExecutionStatus.Ready or SequenceExecutionStatus.Running;
    }

    private bool CanStep()
    {
        if (_isApplyingProject || IsValidationBusy || !IsRunMode || IsRunning || _runtimeDefinitionDirty)
        {
            return false;
        }

        if (_controlOwner == SimulationControlOwner.Manual)
        {
            return HasAuthoredLayout || _project.Axes.Count > 0 || HasVirtualCamera;
        }

        if (!HasEmbeddedSequence)
        {
            return _project.Axes.Count > 0;
        }

        if (HasAutomaticRun)
        {
            return _engine.CurrentSnapshot.AutomaticRun.IsActive;
        }

        var status = GetActiveSequenceSnapshot()?.Status;
        return status is SequenceExecutionStatus.Ready or SequenceExecutionStatus.Running;
    }

    private async Task<bool> EnsureActiveSequenceStartedAsync()
    {
        if (!HasEmbeddedSequence)
        {
            return true;
        }

        var sequenceId = ActiveSequenceId;
        var sequence = GetActiveSequenceSnapshot();
        if (sequenceId is null || sequence is null)
        {
            Log("Sequence", "Active sequence is not available in the runtime snapshot.");
            return false;
        }

        if (sequence.Status == SequenceExecutionStatus.Running)
        {
            return true;
        }

        if (sequence.Status != SequenceExecutionStatus.Ready)
        {
            Log("Sequence", $"Sequence cannot start from {sequence.Status}.");
            return false;
        }

        var result = await _engine.EnqueueCommandAsync(new StartSequenceCommand(sequenceId));
        if (result.IsAccepted)
        {
            return true;
        }

        Log("Sequence", $"Start rejected · {result.ErrorCode}: {result.Detail}");
        return false;
    }

    private async Task StartTestScenarioAsync()
    {
        if (!await EnsureRuntimeDefinitionAppliedAsync())
        {
            return;
        }

        IsDesignMode = false;
        var targetId = SimulationWorkspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioTargetRequired");
            return;
        }

        var profile = SimulationWorkspace.BuildEngineProfile(targetId);
        if (profile.FaultRecovery is not null)
        {
            if (!await StartScheduledFaultScenarioAsync(profile))
            {
                return;
            }

            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStarted");
            return;
        }

        var command = new StartConditionScenarioCommand(profile);
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStartRejected");
            Log("Scenario", $"Start rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _testScenarioOwnsRun = false;
        StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStarted");
        Log("Scenario", $"Test scenario started · {ShortCommandId(command)}");
    }

    private async Task StopTestScenarioAsync()
    {
        var command = new StopConditionScenarioCommand();
        var result = await _engine.EnqueueCommandAsync(command);
        if (!result.IsAccepted)
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStopRejected");
            Log("Scenario", $"Stop rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        if (_testScenarioOwnsRun)
        {
            var pause = await _engine.EnqueueCommandAsync(new PauseCommand());
            if (!pause.IsAccepted)
            {
                StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStopRejected");
                Log("Scenario", $"Pause after stop rejected: {pause.ErrorCode}: {pause.Detail}");
                return;
            }

            _testScenarioOwnsRun = false;
            IsRunning = false;
        }

        StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStopped");
        Log("Scenario", $"Test scenario stopped · {ShortCommandId(command)}");
    }

    private async Task<bool> StartScheduledFaultScenarioAsync(
        DeterministicConditionScenarioProfile profile)
    {
        var reset = await _engine.EnqueueCommandAsync(new ResetCommand());
        if (!reset.IsAccepted)
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStartRejected");
            Log("Scenario", $"Reset rejected: {reset.ErrorCode}: {reset.Detail}");
            return false;
        }

        var startScenario = new StartConditionScenarioCommand(profile);
        var scenarioResult = await _engine.EnqueueCommandAsync(startScenario);
        if (!scenarioResult.IsAccepted)
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStartRejected");
            Log("Scenario", $"Start rejected: {scenarioResult.ErrorCode}: {scenarioResult.Detail}");
            return false;
        }

        if (_project.Simulation.AutomaticRun is not null)
        {
            var automaticResult = await _engine.EnqueueCommandAsync(new StartAutomaticRunCommand());
            if (!automaticResult.IsAccepted)
            {
                await _engine.EnqueueCommandAsync(new StopConditionScenarioCommand());
                StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStartRejected");
                Log(
                    "Scenario",
                    $"Automatic run rejected: {automaticResult.ErrorCode}: {automaticResult.Detail}");
                return false;
            }
        }
        else if (profile.FaultRecovery?.RestartSequenceId is { } recoverySequenceId)
        {
            var sequenceResult = await _engine.EnqueueCommandAsync(
                new StartSequenceCommand(recoverySequenceId));
            if (!sequenceResult.IsAccepted)
            {
                await _engine.EnqueueCommandAsync(new StopConditionScenarioCommand());
                StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStartRejected");
                Log(
                    "Scenario",
                    $"Recovery sequence rejected: {sequenceResult.ErrorCode}: {sequenceResult.Detail}");
                return false;
            }
        }

        var playResult = await _engine.EnqueueCommandAsync(new PlayCommand());
        if (!playResult.IsAccepted)
        {
            await _engine.EnqueueCommandAsync(new StopConditionScenarioCommand());
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioStartRejected");
            Log("Scenario", $"Play rejected: {playResult.ErrorCode}: {playResult.Detail}");
            return false;
        }

        IsRunning = true;
        _testScenarioOwnsRun = true;
        IsDesignMode = false;
        Log("Scenario", $"Scheduled fault scenario started · {ShortCommandId(startScenario)}");
        return true;
    }

    private async Task ReplayTestScenarioAsync()
    {
        if (!await EnsureRuntimeDefinitionAppliedAsync())
        {
            return;
        }

        var targetId = SimulationWorkspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioTargetRequired");
            return;
        }

        var profile = SimulationWorkspace.BuildEngineProfile(targetId);
        if (profile.FaultRecovery is not null)
        {
            if (!await StartScheduledFaultScenarioAsync(profile))
            {
                return;
            }

            IsDesignMode = false;
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioReplayed");
            return;
        }

        var resetCommand = new ResetCommand();
        var resetResult = await _engine.EnqueueCommandAsync(resetCommand);
        if (!resetResult.IsAccepted)
        {
            Log("Scenario", $"Replay reset rejected · {resetResult.ErrorCode}: {resetResult.Detail}");
            return;
        }

        var startCommand = new StartConditionScenarioCommand(profile);
        var startResult = await _engine.EnqueueCommandAsync(startCommand);
        if (!startResult.IsAccepted)
        {
            Log("Scenario", $"Replay start rejected · {startResult.ErrorCode}: {startResult.Detail}");
            return;
        }

        _testScenarioOwnsRun = false;
        IsDesignMode = false;
        StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioReplayed");
        Log("Scenario", $"Test scenario replayed · {ShortCommandId(startCommand)}");
    }

    private async Task RunScenarioBatchAsync()
    {
        if (!await EnsureRuntimeDefinitionAppliedAsync())
        {
            return;
        }

        var targetId = SimulationWorkspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            StatusMessage = OpenVisionLanguageService.T("Simulation.ScenarioTargetRequired");
            return;
        }

        if (IsRunning)
        {
            var pauseResult = await _engine.EnqueueCommandAsync(new PauseCommand());
            if (!pauseResult.IsAccepted)
            {
                StatusMessage = OpenVisionLanguageService.T("Simulation.BatchPauseRejected");
                Log("Batch", $"Batch pause rejected · {pauseResult.ErrorCode}: {pauseResult.Detail}");
                return;
            }

            IsRunning = false;
            Log("Batch", "Main runtime paused before sequential batch");
        }

        SimulationWorkspace.SaveProjectScenario(_project.Simulation);
        var runtime = BuildRuntimeConfiguration(_project);
        var profile = SimulationWorkspace.BuildEngineProfile(targetId);
        var projectJson = _projectStore.SerializeForEvidence(_project);
        var projectPath = _currentProjectPath
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"unsaved-{_project.Id}.ovmachine"));
        var repetitionCount = SimulationWorkspace.BatchRepetitionCount;
        var definition = new DeterministicSimulationBatchDefinition(
            BatchId(_project.Id, profile),
            repetitionCount,
            BuildIdentity.Current);

        _batchCancellation?.Dispose();
        _batchCancellation = new CancellationTokenSource();
        _batchWasCanceled = false;
        _batchCompletedRuns = 0;
        _latestBatchResult = null;
        SetBatchRunning(true);
        StatusMessage = OpenVisionLanguageService.T("Simulation.BatchStarted");
        Log("Batch", $"Sequential batch started · {repetitionCount} run(s)");

        try
        {
            var batchRunner = new DeterministicSimulationBatchRunner();
            _latestBatchResult = await batchRunner.RunAsync(
                definition,
                async (runIndex, cancellationToken) =>
                {
                    await UpdateBatchProgressAsync(runIndex - 1);
                    var result = await RunBatchRepetitionAsync(
                        runtime,
                        profile,
                        projectPath,
                        projectJson,
                        cancellationToken);
                    await UpdateBatchProgressAsync(runIndex);
                    return result;
                },
                _acceptedBatchBaseline,
                _batchCancellation.Token);

            StatusMessage = _latestBatchResult.IsSuccess
                ? OpenVisionLanguageService.T("Simulation.BatchPassedStatus")
                : OpenVisionLanguageService.T("Simulation.BatchMismatchStatus");
            Log(
                "Batch",
                _latestBatchResult.IsSuccess
                    ? $"Sequential batch passed · {_latestBatchResult.CompletedRuns} run(s) · {ShortHash(_latestBatchResult.EvidenceHash)}"
                    : FormatBatchMismatchLog(_latestBatchResult.FirstMismatch));
            PersistBatchArtifacts();
        }
        catch (OperationCanceledException) when (_batchCancellation.IsCancellationRequested)
        {
            _batchWasCanceled = true;
            StatusMessage = OpenVisionLanguageService.T("Simulation.BatchCanceled");
            Log("Batch", $"Sequential batch canceled after {_batchCompletedRuns} completed run(s)");
        }
        finally
        {
            SetBatchRunning(false);
            _batchCancellation.Dispose();
            _batchCancellation = null;
            RaiseBatchPresentationChanged();
        }
    }

    private async Task<DeterministicSimulationRunResultPackage> RunBatchRepetitionAsync(
        SimulationRuntimeConfiguration runtime,
        DeterministicConditionScenarioProfile profile,
        string projectPath,
        string projectJson,
        CancellationToken cancellationToken)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = SimulationFixedStep });
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configured = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(runtime),
                cancellationToken).ConfigureAwait(false);
            if (!configured.IsAccepted)
            {
                throw new InvalidOperationException(
                    $"Batch runtime configuration was rejected: {configured.ErrorCode}: {configured.Detail}");
            }

            if (runtime.AutomaticRun is not null)
            {
                var automaticStarted = await engine.EnqueueCommandAsync(
                    new StartAutomaticRunCommand(beginRealTime: false),
                    cancellationToken).ConfigureAwait(false);
                if (!automaticStarted.IsAccepted)
                {
                    throw new InvalidOperationException(
                        $"Batch automatic run was rejected: {automaticStarted.ErrorCode}: {automaticStarted.Detail}");
                }
            }
            else if (profile.FaultRecovery?.RestartSequenceId is { } recoverySequenceId)
            {
                var sequenceStarted = await engine.EnqueueCommandAsync(
                    new StartSequenceCommand(recoverySequenceId),
                    cancellationToken).ConfigureAwait(false);
                if (!sequenceStarted.IsAccepted)
                {
                    throw new InvalidOperationException(
                        $"Batch recovery sequence was rejected: {sequenceStarted.ErrorCode}: {sequenceStarted.Detail}");
                }
            }

            var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(
                engine,
                profile,
                cancellationToken).ConfigureAwait(false);
            return DeterministicSimulationRunResultPackage.FromReplay(
                _project.Id,
                _project.Name,
                projectPath,
                projectJson,
                SimulationFixedStep,
                profile,
                replay);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task UpdateBatchProgressAsync(int completedRuns)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _batchCompletedRuns = completedRuns;
            RaiseBatchPresentationChanged();
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            _batchCompletedRuns = completedRuns;
            RaiseBatchPresentationChanged();
        });
    }

    private void AcceptBatchBaseline()
    {
        var firstRun = _latestBatchResult?.Runs.FirstOrDefault();
        if (firstRun is null || !_latestBatchResult!.IsComplete || !_latestBatchResult.IsSuccess)
        {
            return;
        }

        _acceptedBatchBaseline = firstRun.Result;
        StatusMessage = OpenVisionLanguageService.T("Simulation.BatchBaselineAcceptedStatus");
        Log("Batch", $"Accepted baseline · {ShortHash(_acceptedBatchBaseline.EvidenceHash)}");
        PersistBatchArtifacts();
        RaiseBatchPresentationChanged();
    }

    private void ClearBatchBaseline()
    {
        _acceptedBatchBaseline = null;
        if (_currentProjectPath is not null)
        {
            var baselinePath = BaselineArtifactPath(_currentProjectPath);
            try
            {
                if (File.Exists(baselinePath))
                {
                    File.Delete(baselinePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _batchArtifactState = BatchArtifactState.SaveFailed;
                Log("Batch", $"Baseline reset failed · {exception.Message}");
                RaiseBatchPresentationChanged();
                return;
            }
        }

        _batchArtifactState = _latestBatchResult is null
            ? BatchArtifactState.None
            : _currentProjectPath is null
                ? BatchArtifactState.MemoryOnly
                : BatchArtifactState.Saved;
        StatusMessage = OpenVisionLanguageService.T("Simulation.BatchBaselineClearedStatus");
        Log("Batch", "Accepted baseline cleared");
        RaiseBatchPresentationChanged();
    }

    private void PersistBatchArtifacts()
    {
        if (_latestBatchResult is null && _acceptedBatchBaseline is null)
        {
            _batchArtifactState = BatchArtifactState.None;
            RaiseBatchPresentationChanged();
            return;
        }

        if (_currentProjectPath is null)
        {
            _batchArtifactState = BatchArtifactState.MemoryOnly;
            RaiseBatchPresentationChanged();
            return;
        }

        var targetId = SimulationWorkspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _batchArtifactState = BatchArtifactState.StaleRejected;
            RaiseBatchPresentationChanged();
            return;
        }

        var profile = SimulationWorkspace.BuildEngineProfile(targetId);
        var projectJson = _projectStore.SerializeForEvidence(_project);
        var buildIdentity = BuildIdentity.Current;
        var batchId = BatchId(_project.Id, profile);
        var saved = false;
        try
        {
            if (_latestBatchResult is not null
                && _latestBatchResult.IsForContext(
                    batchId,
                    buildIdentity,
                    SimulationWorkspace.BatchRepetitionCount,
                    _project.Id,
                    projectJson,
                    SimulationFixedStep,
                    profile))
            {
                DeterministicSimulationBatchResultPackage.SaveToJson(
                    _latestBatchResult,
                    ResultArtifactPath(_currentProjectPath));
                saved = true;
            }

            if (_acceptedBatchBaseline is not null
                && _acceptedBatchBaseline.IsForContext(
                    _project.Id,
                    projectJson,
                    SimulationFixedStep,
                    profile))
            {
                DeterministicSimulationRunResultPackage.SaveToJson(
                    _acceptedBatchBaseline,
                    BaselineArtifactPath(_currentProjectPath));
                saved = true;
            }

            _batchArtifactState = saved
                ? BatchArtifactState.Saved
                : BatchArtifactState.StaleRejected;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _batchArtifactState = BatchArtifactState.SaveFailed;
            Log("Batch", $"Evidence save failed · {exception.Message}");
        }

        RaiseBatchPresentationChanged();
    }

    private void RestoreBatchArtifacts()
    {
        if (_currentProjectPath is null)
        {
            _batchArtifactState = BatchArtifactState.None;
            RaiseBatchPresentationChanged();
            return;
        }

        var resultPath = ResultArtifactPath(_currentProjectPath);
        var baselinePath = BaselineArtifactPath(_currentProjectPath);
        var hasResultFile = File.Exists(resultPath);
        var hasBaselineFile = File.Exists(baselinePath);
        if (!hasResultFile && !hasBaselineFile)
        {
            _batchArtifactState = BatchArtifactState.None;
            RaiseBatchPresentationChanged();
            return;
        }

        var targetId = SimulationWorkspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _batchArtifactState = BatchArtifactState.StaleRejected;
            RaiseBatchPresentationChanged();
            return;
        }

        var profile = SimulationWorkspace.BuildEngineProfile(targetId);
        var projectJson = _projectStore.SerializeForEvidence(_project);
        var buildIdentity = BuildIdentity.Current;
        var batchId = BatchId(_project.Id, profile);
        var rejected = false;
        var restored = false;

        var result = DeterministicSimulationBatchResultPackage.LoadFromJson(resultPath);
        if (result is not null
            && result.IsForContext(
                batchId,
                buildIdentity,
                SimulationWorkspace.BatchRepetitionCount,
                _project.Id,
                projectJson,
                SimulationFixedStep,
                profile))
        {
            _latestBatchResult = result;
            _batchCompletedRuns = result.CompletedRuns;
            restored = true;
        }
        else if (hasResultFile)
        {
            rejected = true;
        }

        var baseline = DeterministicSimulationRunResultPackage.LoadFromJson(baselinePath);
        if (baseline is not null
            && baseline.IsForContext(
                _project.Id,
                projectJson,
                SimulationFixedStep,
                profile))
        {
            _acceptedBatchBaseline = baseline;
            restored = true;
        }
        else if (hasBaselineFile)
        {
            rejected = true;
        }

        _batchArtifactState = rejected
            ? BatchArtifactState.StaleRejected
            : restored
                ? BatchArtifactState.Restored
                : BatchArtifactState.None;
        if (rejected)
        {
            Log("Batch", "Saved evidence rejected because project or scenario context changed");
        }
        else if (restored)
        {
            Log("Batch", "Saved batch result and baseline restored");
        }

        RaiseBatchPresentationChanged();
    }

    private void RelinkBatchProjectPath(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        if (_acceptedBatchBaseline is not null)
        {
            _acceptedBatchBaseline = _acceptedBatchBaseline with { ProjectPath = fullPath };
        }

        if (_latestBatchResult is not null)
        {
            _latestBatchResult = _latestBatchResult with
            {
                Runs = _latestBatchResult.Runs
                    .Select(run => run with
                    {
                        Result = run.Result with { ProjectPath = fullPath }
                    })
                    .ToImmutableArray()
            };
        }
    }

    private static string ResultArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.batch-result.json";

    private static string BaselineArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.batch-baseline.json";

    private static string BatchId(
        string projectId,
        DeterministicConditionScenarioProfile profile) =>
        $"{projectId}:{profile.ScenarioId}";

    private void TryCompleteVisionEvidence(SimulationSnapshot snapshot)
    {
        var recorder = _activeVisionEvidenceRecorder;
        if (recorder is null || !recorder.CanComplete(snapshot))
        {
            return;
        }

        try
        {
            var package = recorder.Complete(snapshot);
            _visionEvidenceComparison = _latestVisionEvidence?.CompareTo(package);
            _latestVisionEvidence = package;
            _activeVisionEvidenceRecorder = null;
            _visionEvidenceArtifactState = BatchArtifactState.MemoryOnly;
            PersistVisionEvidence();
            Log(
                "Vision",
                $"Execution evidence completed · {package.ShortEvidenceHash}" +
                (_visionEvidenceComparison is null
                    ? string.Empty
                    : _visionEvidenceComparison.IsMatch
                        ? " · repeat match"
                        : $" · {_visionEvidenceComparison.MismatchCode}"));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            _activeVisionEvidenceRecorder = null;
            _visionEvidenceArtifactState = BatchArtifactState.SaveFailed;
            Log("Vision", $"Execution evidence failed · {exception.Message}");
        }

        NotifyCameraCommissioningChanged(invalidateCommands: false);
    }

    private void RestoreVisionEvidence()
    {
        _activeVisionEvidenceRecorder = null;
        _latestVisionEvidence = null;
        _visionEvidenceComparison = null;
        _visionEvidenceArtifactState = BatchArtifactState.None;
        if (string.IsNullOrWhiteSpace(_currentProjectPath))
        {
            NotifyCameraCommissioningChanged(invalidateCommands: false);
            return;
        }

        var path = VisionEvidenceArtifactPath(_currentProjectPath);
        var package = DeterministicVisionExecutionEvidencePackage.LoadFromJson(path);
        if (package is null)
        {
            _visionEvidenceArtifactState = File.Exists(path)
                ? BatchArtifactState.StaleRejected
                : BatchArtifactState.None;
        }
        else
        {
            _latestVisionEvidence = package;
            _visionEvidenceArtifactState = package.IsForContext(
                _project.Id,
                _projectStore.SerializeForEvidence(_project),
                BuildIdentity.Current,
                _selectedCameraId,
                _selectedCameraRecipe)
                ? BatchArtifactState.Restored
                : BatchArtifactState.StaleRejected;
        }

        Log(
            "Vision",
            _visionEvidenceArtifactState == BatchArtifactState.Restored
                ? "Saved execution evidence restored"
                : _visionEvidenceArtifactState == BatchArtifactState.StaleRejected
                    ? "Saved execution evidence rejected because project, build, camera, or recipe context changed"
                    : "No saved execution evidence found");
        NotifyCameraCommissioningChanged(invalidateCommands: false);
    }

    private void PersistVisionEvidence()
    {
        if (_latestVisionEvidence is null || string.IsNullOrWhiteSpace(_currentProjectPath))
        {
            return;
        }

        if (!_latestVisionEvidence.IsForContext(
                _project.Id,
                _projectStore.SerializeForEvidence(_project),
                BuildIdentity.Current,
                _selectedCameraId,
                _selectedCameraRecipe))
        {
            _visionEvidenceArtifactState = BatchArtifactState.StaleRejected;
            NotifyCameraCommissioningChanged(invalidateCommands: false);
            return;
        }

        try
        {
            DeterministicVisionExecutionEvidencePackage.SaveToJson(
                _latestVisionEvidence,
                VisionEvidenceArtifactPath(_currentProjectPath));
            _visionEvidenceArtifactState = BatchArtifactState.Saved;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _visionEvidenceArtifactState = BatchArtifactState.SaveFailed;
            Log("Vision", $"Execution evidence save failed · {exception.Message}");
        }

        NotifyCameraCommissioningChanged(invalidateCommands: false);
    }

    private void RefreshVisionEvidenceContext()
    {
        if (_activeVisionEvidenceRecorder is not null)
        {
            NotifyCameraCommissioningChanged(invalidateCommands: false);
            return;
        }

        _visionEvidenceArtifactState = _latestVisionEvidence switch
        {
            null when _visionEvidenceArtifactState == BatchArtifactState.StaleRejected =>
                BatchArtifactState.StaleRejected,
            null => BatchArtifactState.None,
            { } package when package.IsForContext(
                _project.Id,
                _projectStore.SerializeForEvidence(_project),
                BuildIdentity.Current,
                _selectedCameraId,
                _selectedCameraRecipe) => _visionEvidenceArtifactState switch
                {
                    BatchArtifactState.Restored => BatchArtifactState.Restored,
                    BatchArtifactState.SaveFailed => BatchArtifactState.SaveFailed,
                    BatchArtifactState.MemoryOnly => BatchArtifactState.MemoryOnly,
                    _ => BatchArtifactState.Saved
                },
            _ => BatchArtifactState.StaleRejected
        };
        NotifyCameraCommissioningChanged(invalidateCommands: false);
    }

    private void RelinkVisionEvidenceProjectPath(string projectPath)
    {
        if (_latestVisionEvidence is not null)
        {
            _latestVisionEvidence = _latestVisionEvidence with
            {
                ProjectPath = Path.GetFullPath(projectPath)
            };
        }
    }

    private void ClearVisionEvidence()
    {
        _activeVisionEvidenceRecorder = null;
        _latestVisionEvidence = null;
        _visionEvidenceComparison = null;
        _visionEvidenceArtifactState = BatchArtifactState.None;
        NotifyCameraCommissioningChanged(invalidateCommands: false);
    }

    private static string VisionEvidenceArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.vision-result.json";

    private void NavigateToBatchMismatch()
    {
        var mismatch = _latestBatchResult?.FirstMismatch;
        if (mismatch is null)
        {
            return;
        }

        Layout.Select(mismatch.TargetId);
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.BatchMismatchNavigationStatus"),
            mismatch.TargetId,
            mismatch.ObservedTickIndex);
        Log(
            "Batch",
            $"First mismatch selected · {mismatch.EvidenceKind} · {mismatch.TargetId} · Tick {mismatch.ObservedTickIndex}");
    }

    private void SetBatchRunning(bool value)
    {
        if (_isBatchRunning == value)
        {
            return;
        }

        _isBatchRunning = value;
        OnPropertyChanged(nameof(IsBatchRunning));
        OnPropertyChanged(nameof(IsScenarioConfigurationEnabled));
        RaiseBatchPresentationChanged();
        InvalidateCommands();
    }

    private string ProjectDisplayName => _currentProjectPath is null
        ? _project.Name
        : Path.GetFileNameWithoutExtension(_currentProjectPath);

    private void RefreshProjectIdentity()
    {
        Title = $"OpenVisionLab Machine Studio · {ProjectDisplayName}{(HasUnsavedChanges ? " *" : string.Empty)}";
        OnPropertyChanged(nameof(ProjectStatusText));
        OnPropertyChanged(nameof(SceneTitleText));
    }

    private void MarkProjectChanged(bool requiresRuntimeRebuild = true)
    {
        if (requiresRuntimeRebuild)
        {
            _runtimeDefinitionDirty = true;
        }

        RefreshProjectDirtyState();
    }

    private void RefreshProjectDirtyState() =>
        HasUnsavedChanges = !string.Equals(
            _savedProjectEvidence,
            _projectStore.SerializeForEvidence(_project),
            StringComparison.Ordinal);

    private void AcceptCurrentProjectAsSaved()
    {
        _savedProjectEvidence = _projectStore.SerializeForEvidence(_project);
        HasUnsavedChanges = false;
    }

    internal async Task<bool> TryResolveUnsavedChangesAsync()
    {
        await CommitFocusedEditorAsync();
        RefreshProjectDirtyState();
        if (!HasUnsavedChanges)
        {
            return true;
        }

        return UnsavedProjectPrompt() switch
        {
            UnsavedProjectDecision.Save => await TrySaveCurrentProjectAsync(),
            UnsavedProjectDecision.Discard => true,
            _ => false
        };
    }

    private static UnsavedProjectDecision ShowUnsavedProjectPrompt()
    {
        var result = WpfMessageDialog.Show(
            Application.Current?.MainWindow,
            CreateUnsavedProjectDialogOptions());
        return result switch
        {
            WpfMessageDialogResult.Yes => UnsavedProjectDecision.Save,
            WpfMessageDialogResult.No => UnsavedProjectDecision.Discard,
            _ => UnsavedProjectDecision.Cancel
        };
    }

    internal static WpfMessageDialogOptions CreateUnsavedProjectDialogOptions() => new()
    {
        Title = OpenVisionLanguageService.T(
            "Project.UnsavedTitle",
            "저장하지 않은 프로젝트",
            "Unsaved project"),
        Message = OpenVisionLanguageService.T(
            "Project.UnsavedMessage",
            "현재 프로젝트의 변경 내용을 저장하시겠습니까?",
            "Save changes to the current project?"),
        Kind = WpfMessageDialogKind.Question,
        DefaultResult = WpfMessageDialogResult.Yes,
        PrimaryButtonText = OpenVisionLanguageService.T("Project.Save", "저장", "Save"),
        SecondaryButtonText = OpenVisionLanguageService.T(
            "Project.DontSave",
            "저장 안 함",
            "Don't save"),
        TertiaryButtonText = OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel")
    };

    private static void ShowProjectSaveFailure(string details)
    {
        WpfMessageDialog.Show(
            Application.Current?.MainWindow,
            new WpfMessageDialogOptions
            {
                Title = OpenVisionLanguageService.T(
                    "Project.SaveFailedTitle",
                    "프로젝트 저장 실패",
                    "Project save failed"),
                Message = string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T(
                        "Project.SaveFailedMessage",
                        "프로젝트 파일을 안전하게 저장하지 못했습니다.{0}{0}{1}",
                        "The project file could not be saved safely.{0}{0}{1}"),
                    Environment.NewLine,
                    details),
                Kind = WpfMessageDialogKind.Warning,
                DefaultResult = WpfMessageDialogResult.OK,
                PrimaryButtonText = OpenVisionLanguageService.T(
                    "MessageBox.OK",
                    "확인",
                    "OK")
            });
    }

    private void RaiseBatchPresentationChanged()
    {
        OnPropertyChanged(nameof(BatchCompletedRuns));
        OnPropertyChanged(nameof(BatchStatusText));
        OnPropertyChanged(nameof(BatchResultText));
        OnPropertyChanged(nameof(BatchBaselineText));
        OnPropertyChanged(nameof(BatchArtifactStatusText));
        OnPropertyChanged(nameof(BatchAssertionOutcomes));
        OnPropertyChanged(nameof(HasBatchAssertionOutcomes));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
        OnPropertyChanged(nameof(CanAcceptBatchBaseline));
        OnPropertyChanged(nameof(CanClearBatchBaseline));
        OnPropertyChanged(nameof(CanNavigateToBatchMismatch));
        InvalidateCommands();
    }

    private static string FormatBatchMismatchLog(DeterministicSimulationBatchMismatch? mismatch) =>
        mismatch is null
            ? "Sequential batch evidence mismatch"
            : $"First mismatch · run {mismatch.RunIndex} · {mismatch.EvidenceKind} · {mismatch.TargetId} · Tick {mismatch.ObservedTickIndex}";

    private ScenarioAssertionOutcomePresentation CreateAssertionOutcomePresentation(
        DeterministicScenarioAssertionOutcome outcome)
    {
        string status = OpenVisionLanguageService.T(
            outcome.IsPassed
                ? "Simulation.AssertionPassed"
                : "Simulation.AssertionFailed");
        string summary = outcome.Kind switch
        {
            DeterministicScenarioAssertionKind.AutomaticCycleCompleted => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.AssertionOutcomeCycle"),
                outcome.ActualValue,
                outcome.ExpectedValue,
                outcome.ObservedTickIndex),
            DeterministicScenarioAssertionKind.NoActiveFaults => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.AssertionOutcomeFaults"),
                outcome.ActualValue,
                outcome.ObservedTickIndex),
            DeterministicScenarioAssertionKind.FinalEquipmentState => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.AssertionOutcomeEquipment"),
                ConditionScenarioTargets.FirstOrDefault(target =>
                    string.Equals(target.Id, outcome.TargetId, StringComparison.Ordinal))?.Name
                    ?? outcome.TargetId,
                outcome.ActualValue,
                outcome.ExpectedValue,
                outcome.ObservedTickIndex),
            _ => outcome.Detail
        };
        return new ScenarioAssertionOutcomePresentation(outcome.IsPassed, status, summary);
    }

    private async Task PauseForDesignAsync()
    {
        try
        {
            var command = new PauseCommand();
            var result = await _engine.EnqueueCommandAsync(command);
            if (result.IsAccepted)
            {
                IsRunning = false;
                Log("Simulation", "Paused before entering Design mode");
            }
        }
        catch (OperationCanceledException) when (_runtimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleCommandException(exception);
        }
    }

    private void OnProjectTreePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ProjectTree.SelectedNode))
        {
            return;
        }

        var node = ProjectTree.SelectedNode;
        if (node?.Kind == global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.Device
            && _project.Devices.Any(device =>
                device.Kind == DeviceKind.Camera
                && string.Equals(device.Id, node.Id, StringComparison.Ordinal)))
        {
            SelectVirtualCamera(node.Id);
        }
        if (node?.Kind == global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.LayoutComponent)
        {
            Layout.Select(node.Id);
        }
        else if (Layout.SelectedItem is not null)
        {
            Layout.SelectedItem = null;
        }
        if (node?.Model is SequenceDefinition sequence)
        {
            SequenceEditor.SelectSequence(sequence.Id);
        }
        else if (node?.Model is SequenceStepDefinition step)
        {
            SequenceEditor.SelectStep(step.Id);
        }
        Properties.ShowNode(node);
        AxisDriveTuningEditor = node?.Model is VirtualAxisDefinition axis
            ? new AxisDriveTuningEditorViewModel(axis, OnAxisDefinitionChanged)
            : null;
        OnPropertyChanged(nameof(IsMultiAxisCommissioningRecipeSelection));
        StatusMessage = node is null ? "Ready" : $"Selected {node.DisplayName}";
        OnPropertyChanged(nameof(SelectionStatusText));
        if (node?.Kind == global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.Axis)
        {
            ApplyMonitorSnapshot(SceneSnapshots.Latest ?? _engine.CurrentSnapshot);
        }
        else
        {
            NotifyManualCommissioningChanged(invalidateCommands: false);
            NotifyAxisCommissioningChanged(invalidateCommands: false);
            OnPropertyChanged(nameof(SelectedEquipmentStatus));
            InvalidateCommands();
        }
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(Layout.SelectedItem) and
            not nameof(Layout.SelectionCount))
        {
            return;
        }

        Properties.Show(Layout.SelectedItem?.Component);
        if (Layout.SelectedItem is not null)
        {
            AxisDriveTuningEditor = null;
        }
        if (_layoutAuthoringState is not null && !_isRestoringLayoutHistory)
        {
            _layoutAuthoringState = _layoutAuthoringState with
            {
                SelectedComponentIds = Layout.SelectedItems.Select(item => item.Id).ToArray(),
                PrimaryComponentId = Layout.SelectedItem?.Id
            };
        }
        StatusMessage = Layout.SelectedItem is null
            ? "Ready"
            : Layout.SelectionCount > 1
                ? $"Selected {Layout.SelectionCount} components; reference {Layout.SelectedItem.Name}"
                : $"Selected {Layout.SelectedItem.Name}";
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(HasSelectedEquipment));
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        RecipeConnections.SynchronizeSelection(Layout.SelectedItem?.Id);
        var snapshot = SceneSnapshots.Latest ?? _engine.CurrentSnapshot;
        if (Layout.SelectedItem?.Component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            _currentAxis = snapshot.Axes.FirstOrDefault(axis =>
                axis.Id == Layout.SelectedItem.BehaviorBindingId);
            SynchronizeAxisTargetInput();
        }
        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        NotifySensorCommissioningChanged(invalidateCommands: false);
        NotifyCylinderCommissioningChanged(invalidateCommands: false);
        NotifyConveyorCommissioningChanged(invalidateCommands: false);
        InvalidateCommands();
    }

    private void OnLayoutDefinitionChanged(object? sender, EventArgs args)
    {
        ExitDryRunPlayback();
        if (!_isRestoringLayoutHistory)
        {
            CommitLayoutMutation(_layoutAuthoringState);
        }
        MarkProjectChanged();
        UpdateRunToolAvailability();
        RecipeConnections.Load(_project, Layout.SelectedItem?.Id);
        Properties.Show(Layout.SelectedItem?.Component);
        StatusMessage = "Layout changed; Simulation ON will rebuild the runtime";
        InvalidateCommands();
    }

    private void OnAxisDefinitionChanged()
    {
        ExitDryRunPlayback();
        MarkProjectChanged();
        if (_latestCommissioningResult is not null)
        {
            _commissioningArtifactState = BatchArtifactState.StaleRejected;
            _commissioningBaselineComparison = null;
            RaiseCommissioningValidationChanged();
        }
        UpdateRunToolAvailability();
        Properties.Show(AxisDriveTuningEditor is null
            ? null
            : _project.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, AxisDriveTuningEditor.Id, StringComparison.Ordinal)));
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        MultiAxisCommissioningRecipe.ApplyAxisSnapshots(
            (SceneSnapshots.Latest ?? _engine.CurrentSnapshot).Axes);
        StatusMessage = "Axis tuning changed; Simulation ON will validate and rebuild the runtime";
        InvalidateCommands();
    }

    private void OnMultiAxisCommissioningRecipeChanged()
    {
        MarkProjectChanged(requiresRuntimeRebuild: false);
        Properties.ShowNode(ProjectTree.SelectedNode);
        if (_latestCommissioningResult is not null)
        {
            _commissioningArtifactState = BatchArtifactState.StaleRejected;
        }
        _commissioningBaselineComparison = null;
        NotifyMultiAxisCommissioningRecipeChanged();
        StatusMessage = "Multi-axis commissioning recipe changed; save the project to retain it";
    }

    private void OnSequenceDefinitionChanged(
        object? sender,
        SequenceEditorChangedEventArgs args)
    {
        ExitDryRunPlayback();
        MarkProjectChanged();
        UpdateRunToolAvailability();
        if (args.StructureChanged)
        {
            ProjectTree.LoadProject(_project);
        }
        RecipeConnections.RefreshDefinitionPreservingProcessBlockPlan(Layout.SelectedItem?.Id);

        StatusMessage = "Sequence changed; Simulation ON will validate and rebuild the runtime";
        OnPropertyChanged(nameof(HasEmbeddedSequence));
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
        InvalidateCommands();
    }

    private string? ActiveSequenceId =>
        _project.Simulation.AutomaticRun?.SequenceId
        ?? _project.Sequences.FirstOrDefault()?.Id;

    private SequenceExecutionSnapshot? GetActiveSequenceSnapshot() =>
        GetActiveSequenceSnapshot(_engine.CurrentSnapshot);

    private SequenceExecutionSnapshot? GetActiveSequenceSnapshot(SimulationSnapshot snapshot)
    {
        var sequenceId = ActiveSequenceId;
        return sequenceId is null
            ? null
            : snapshot.Sequences.FirstOrDefault(sequence =>
                string.Equals(sequence.SequenceId, sequenceId, StringComparison.Ordinal));
    }

    private string ResolveSequenceName(string? sequenceId)
    {
        if (sequenceId is null)
        {
            return HasEmbeddedSequence
                ? LocalizeSequenceName(_project.Sequences[0])
                : OpenVisionLanguageService.T(
                    "Shell.NoSequenceConfigured",
                    "시퀀스가 설정되지 않았습니다",
                    "No sequence configured");
        }

        var sequence = _project.Sequences.FirstOrDefault(item => item.Id == sequenceId);
        return sequence is null ? sequenceId : LocalizeSequenceName(sequence);
    }

    private string ResolveStepName(string? sequenceId, string? stepId)
    {
        if (stepId is null)
        {
            return _currentSequence?.Status == SequenceExecutionStatus.Completed
                ? OpenVisionLanguageService.T("Sequence.Complete", "완료", "Complete")
                : OpenVisionLanguageService.T("Shell.Unavailable");
        }

        var sequence = _project.Sequences.FirstOrDefault(item => item.Id == sequenceId);
        var step = sequence?.Steps.FirstOrDefault(item => item.Id == stepId);
        return step is null || sequence is null
            ? stepId
            : OpenVisionLanguageService.TUserText(
                "sequence",
                $"{sequence.Id}.step.{step.Id}.name",
                step.Name);
    }

    private static string LocalizeSequenceName(SequenceDefinition sequence) =>
        OpenVisionLanguageService.TUserText("sequence", $"{sequence.Id}.name", sequence.Name);

    private static bool? ReadSignal(SimulationSnapshot snapshot, string id) =>
        snapshot.Signals.FirstOrDefault(signal => signal.Id == id)?.Value;

    private DeviceDefinition? CurrentCameraDefinition => _project.Devices.FirstOrDefault(device =>
        device.Kind == DeviceKind.Camera
        && string.Equals(
            device.Id,
            _selectedCameraId ?? _currentCamera?.Id,
            StringComparison.Ordinal));

    private bool HasUsableCameraImageSource =>
        !string.IsNullOrWhiteSpace(_currentProjectPath)
        && !string.IsNullOrWhiteSpace(_selectedCameraRecipe)
        && CurrentCameraDefinition?.Camera?.SingleImageSource is
        {
            SourceRelativePath.Length: > 0,
            Width: > 0,
            Height: > 0,
            PixelFormat.Length: > 0
        };

    private IReadOnlyList<string> GetCameraRecipes(string? cameraId) =>
        string.IsNullOrWhiteSpace(cameraId)
            ? []
            : _project.Sequences
                .SelectMany(sequence => sequence.Steps)
                .Where(step => step.Action == SequenceStepAction.TriggerCamera
                    && string.Equals(step.TargetId, cameraId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(step.Parameter))
                .Select(step => step.Parameter.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(recipe => recipe, StringComparer.Ordinal)
                .ToArray();

    private void SelectVirtualCamera(string? cameraId)
    {
        if (cameraId is null && HasVirtualCamera)
        {
            return;
        }

        if (cameraId is not null && !_project.Devices.Any(device =>
                device.Kind == DeviceKind.Camera
                && string.Equals(device.Id, cameraId, StringComparison.Ordinal)))
        {
            return;
        }

        if (string.Equals(_selectedCameraId, cameraId, StringComparison.Ordinal))
        {
            return;
        }

        _selectedCameraId = cameraId;
        EnsureSelectedCameraRecipe();
        CameraImageSourceEditor.SelectCamera(cameraId);
        OnPropertyChanged(nameof(SelectedCameraId));
        OnPropertyChanged(nameof(SelectedVirtualCamera));
        OnPropertyChanged(nameof(CurrentCameraRecipes));
        RefreshVisionEvidenceContext();
        ApplyMonitorSnapshot(SceneSnapshots.Latest ?? _engine.CurrentSnapshot);
    }

    private void EnsureSelectedCameraRecipe()
    {
        var recipes = GetCameraRecipes(_selectedCameraId);
        var next = recipes.Contains(_selectedCameraRecipe, StringComparer.Ordinal)
            ? _selectedCameraRecipe
            : recipes.FirstOrDefault();
        if (string.Equals(_selectedCameraRecipe, next, StringComparison.Ordinal))
        {
            return;
        }

        _selectedCameraRecipe = next;
        OnPropertyChanged(nameof(SelectedCameraRecipe));
    }

    private VirtualAxisDefinition? CurrentAxisDefinition => _currentAxis is null
        ? null
        : _project.Axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, _currentAxis.Id, StringComparison.Ordinal));

    private LayoutComponentSnapshot? CurrentCylinderSnapshot
    {
        get
        {
            var selected = Layout.SelectedItem;
            if (selected is null
                || selected.Component?.Kind != LayoutComponentKind.PneumaticCylinder)
            {
                return null;
            }

            return PresentationSnapshot.LayoutComponents
                .FirstOrDefault(component =>
                    string.Equals(component.Id, selected.Id, StringComparison.Ordinal)
                    && component.Kind == LayoutComponentKind.PneumaticCylinder);
        }
    }

    private LayoutComponentSnapshot? CurrentSensorSnapshot
    {
        get
        {
            var selected = Layout.SelectedItem;
            if (selected is null || selected.Component?.Kind != LayoutComponentKind.DigitalSensor)
            {
                return null;
            }

            return PresentationSnapshot.LayoutComponents
                .FirstOrDefault(component =>
                    string.Equals(component.Id, selected.Id, StringComparison.Ordinal)
                    && component.Kind == LayoutComponentKind.DigitalSensor);
        }
    }

    private string? CurrentSensorOutputChannelId => CurrentSensorSnapshot?.SensorOutputChannelId;

    private DigitalSignalSnapshot? CurrentSensorSignal => CurrentSensorOutputChannelId is { } channelId
        ? PresentationSnapshot.Signals.FirstOrDefault(signal =>
            string.Equals(signal.Id, channelId, StringComparison.Ordinal))
        : null;

    internal DigitalSignalSnapshot? CurrentSelectedSensorSignal => CurrentSensorSignal;

    private LayoutComponentSnapshot? CurrentConveyorSnapshot
    {
        get
        {
            var selected = Layout.SelectedItem;
            if (selected is null || selected.Component?.Kind != LayoutComponentKind.Conveyor)
            {
                return null;
            }

            return PresentationSnapshot.LayoutComponents
                .FirstOrDefault(component =>
                    string.Equals(component.Id, selected.Id, StringComparison.Ordinal)
                    && component.Kind == LayoutComponentKind.Conveyor);
        }
    }

    private string CurrentAxisUnit => string.IsNullOrWhiteSpace(CurrentAxisDefinition?.Unit)
        ? "mm"
        : CurrentAxisDefinition.Unit;

    private void SynchronizeAxisTargetInput()
    {
        if (_currentAxis is null || string.Equals(_axisTargetAxisId, _currentAxis.Id, StringComparison.Ordinal))
        {
            return;
        }

        _axisTargetAxisId = _currentAxis.Id;
        _axisTargetPositionText = _currentAxis.Position.ToString("F3", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(AxisTargetPositionText));
    }

    private bool TryGetAxisTargetPosition(out double target)
    {
        target = default;
        if (_currentAxis is null || CurrentAxisDefinition is null || !TryParseAxisTargetPosition(out target))
        {
            return false;
        }

        var minimum = CurrentAxisDefinition?.SoftLimitMin ?? 0;
        var maximum = CurrentAxisDefinition?.SoftLimitMax ?? 300;
        return target >= minimum && target <= maximum;
    }

    private bool TryParseAxisTargetPosition(out double target) =>
        (double.TryParse(_axisTargetPositionText, NumberStyles.Float, CultureInfo.CurrentCulture, out target)
         || double.TryParse(_axisTargetPositionText, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
        && double.IsFinite(target);

    private bool TryGetAxisRelativeDistance(out double distance) =>
        (double.TryParse(_axisRelativeDistanceText, NumberStyles.Float, CultureInfo.CurrentCulture, out distance)
         || double.TryParse(_axisRelativeDistanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
        && double.IsFinite(distance)
        && distance != 0;

    private bool TryGetAxisCommandVelocity(out double velocity)
    {
        velocity = default;
        return CurrentAxisDefinition is not null
            && TryParseAxisCommandVelocity(out velocity)
            && velocity != 0
            && Math.Abs(velocity) <= CurrentAxisDefinition.MaxVelocity;
    }

    private bool TryParseAxisCommandVelocity(out double velocity) =>
        (double.TryParse(_axisCommandVelocityText, NumberStyles.Float, CultureInfo.CurrentCulture, out velocity)
         || double.TryParse(_axisCommandVelocityText, NumberStyles.Float, CultureInfo.InvariantCulture, out velocity))
        && double.IsFinite(velocity);

    private bool CanUseManualAxis => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && IsRunning
        && _controlOwner == SimulationControlOwner.Manual
        && HasSelectedAxisStage
        && !IsCurrentAxisInterlocked
        && _currentAxis is not null;

    private async Task StartManualEquipmentControlAsync()
    {
        SimulationCommandResult? result = Layout.SelectedItem?.Component?.Kind switch
        {
            LayoutComponentKind.LinearStage => await DispatchAxisCommandAsync(
                new StartManualControlCommand(),
                "Axis.ActionStartManual"),
            LayoutComponentKind.RotaryStage => await DispatchAxisCommandAsync(
                new StartManualControlCommand(),
                "Axis.ActionStartManual"),
            LayoutComponentKind.DigitalSensor => await DispatchSensorCommandAsync(
                new StartManualControlCommand(),
                "Sensor.ActionStartManual"),
            LayoutComponentKind.PneumaticCylinder => await DispatchCylinderCommandAsync(
                new StartManualControlCommand(),
                "Cylinder.ActionStartManual"),
            LayoutComponentKind.Conveyor => await DispatchConveyorCommandAsync(
                new StartManualControlCommand(),
                "Conveyor.ActionStartManual"),
            _ => null
        };

        if (result?.IsAccepted == true)
        {
            IsRunning = true;
        }
    }

    private async Task StartManualCameraControlAsync()
    {
        var result = await DispatchCameraCommandAsync(
            new StartManualControlCommand(),
            "Camera.ActionStartManual");
        if (result.IsAccepted)
        {
            IsRunning = true;
        }
    }

    private async Task TriggerSelectedCameraAsync()
    {
        if (!CanTriggerCamera
            || CurrentCameraDefinition?.Camera is not
                { SingleImageSource: { } sourceDefinition } cameraDefinition
            || _currentCamera is not { } camera
            || string.IsNullOrWhiteSpace(_selectedCameraRecipe)
            || string.IsNullOrWhiteSpace(_currentProjectPath))
        {
            return;
        }

        var snapshot = _engine.CurrentSnapshot;
        var acquisitionId = string.Concat(
            camera.Id,
            "/frame/",
            (camera.AcquisitionOrdinal + 1).ToString("D8", CultureInfo.InvariantCulture));
        VirtualFrameDescriptor frame;
        try
        {
            var context = new VirtualAcquisitionContext(
                acquisitionId,
                camera.Id,
                _selectedCameraRecipe,
                snapshot.TickIndex,
                snapshot.SimulationTime,
                _project.Simulation.Seed,
                snapshot.Axes.ToDictionary(axis => axis.Id, axis => axis.Position, StringComparer.Ordinal));
            var source = new ProjectRelativeSingleImageSource(
                Path.GetDirectoryName(_currentProjectPath)!,
                sourceDefinition.SourceRelativePath,
                sourceDefinition.Width,
                sourceDefinition.Height,
                sourceDefinition.PixelFormat);
            frame = await source.AcquireAsync(context, _runtimeCancellation.Token);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            StatusMessage = OpenVisionLanguageService.T("Camera.StatusRejected");
            Log("Camera", string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Camera.SourceRejected"),
                exception.Message));
            return;
        }

        VisionRunResult inspectionResult;
        try
        {
            var judgment = cameraDefinition.PlaceholderDecision switch
            {
                PlaceholderInspectionDecision.Pass => VisionJudgment.OK,
                PlaceholderInspectionDecision.Fail => VisionJudgment.NG,
                _ => throw new InvalidOperationException("Camera inspection judgment is invalid.")
            };
            IVisionInspectionRunner runner = new DeterministicMockVisionInspectionRunner(
                new Dictionary<string, VisionJudgment>(StringComparer.Ordinal)
                {
                    [_selectedCameraRecipe] = judgment
                });
            inspectionResult = await runner.RunAsync(
                new VisionRecipeReference(
                    _selectedCameraRecipe,
                    $"recipes/{_selectedCameraRecipe}.ovrecipe"),
                frame,
                _runtimeCancellation.Token);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            StatusMessage = OpenVisionLanguageService.T("Camera.StatusRejected");
            Log("Camera", string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Camera.InspectionRejected"),
                exception.Message));
            return;
        }

        var current = _engine.CurrentSnapshot;
        var currentCamera = current.Cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, camera.Id, StringComparison.Ordinal));
        if (current.RunMode != SimulationRunMode.Paused
            || current.ControlOwner != SimulationControlOwner.Manual
            || current.TickIndex != snapshot.TickIndex
            || current.SimulationTime != snapshot.SimulationTime
            || currentCamera?.AcquisitionOrdinal != camera.AcquisitionOrdinal
            || currentCamera.State != camera.State)
        {
            StatusMessage = OpenVisionLanguageService.T("Camera.StatusRejected");
            Log("Camera", OpenVisionLanguageService.T("Camera.ContextChanged"));
            return;
        }

        var evidence = new VirtualCameraFrameEvidence(
            frame.FrameId,
            frame.SourceRelativePath,
            frame.ContentSha256,
            frame.ContentLength,
            frame.Width,
            frame.Height,
            frame.PixelFormat);
        var inspectionEvidence = new VirtualCameraInspectionEvidence(
            inspectionResult.InspectionId,
            inspectionResult.AcquisitionId,
            inspectionResult.CameraId,
            inspectionResult.RecipeId,
            inspectionResult.FrameId,
            inspectionResult.Judgment switch
            {
                VisionJudgment.OK => PlaceholderInspectionDecision.Pass,
                VisionJudgment.NG => PlaceholderInspectionDecision.Fail,
                _ => throw new InvalidOperationException(
                    $"Unsupported manual inspection judgment: {inspectionResult.Judgment}.")
            },
            inspectionResult.Message,
            inspectionResult.Metrics);
        var command = new TriggerVirtualCameraCommand(
            camera.Id,
            _selectedCameraRecipe,
            evidence,
            inspectionEvidence);
        _activeVisionEvidenceRecorder = new DeterministicVisionExecutionRecorder(
            _project.Id,
            _project.Name,
            _currentProjectPath,
            _projectStore.SerializeForEvidence(_project),
            BuildIdentity.Current,
            SimulationFixedStep,
            snapshot.TickIndex,
            command.CommandId,
            camera.Id,
            _selectedCameraRecipe,
            acquisitionId,
            frame.FrameId,
            inspectionResult.InspectionId);
        NotifyCameraCommissioningChanged(invalidateCommands: false);
        var result = await DispatchCameraCommandAsync(command, "Camera.ActionTrigger");
        if (result.IsAccepted)
        {
            ApplyMonitorSnapshot(_engine.CurrentSnapshot);
        }
        else
        {
            _activeVisionEvidenceRecorder = null;
            RefreshVisionEvidenceContext();
        }
    }

    private bool CanUseManualCylinder => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && _controlOwner == SimulationControlOwner.Manual
        && !IsCurrentCylinderInterlocked
        && CurrentCylinderSnapshot is not null;

    private bool CanUseManualSensor => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && _controlOwner == SimulationControlOwner.Manual
        && !IsCurrentSensorFaulted
        && CurrentSensorSnapshot is not null;

    private bool CanUseManualConveyor => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && _controlOwner == SimulationControlOwner.Manual
        && CurrentConveyorSnapshot is not null;

    private async Task<SimulationCommandResult> DispatchAxisCommandAsync(
        SimulationCommand command,
        string actionKey)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        var action = OpenVisionLanguageService.T(actionKey);
        var message = result.IsAccepted
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Axis.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Axis.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail);
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                result.IsAccepted ? "Axis.StatusAccepted" : "Axis.StatusRejected"),
            action);
        Log("Motion", message);
        return result;
    }

    private async Task<SimulationCommandResult> DispatchCameraCommandAsync(
        SimulationCommand command,
        string actionKey)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        var action = OpenVisionLanguageService.T(actionKey);
        StatusMessage = OpenVisionLanguageService.T(
            result.IsAccepted ? "Camera.StatusAccepted" : "Camera.StatusRejected");
        Log("Camera", result.IsAccepted
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Camera.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Camera.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail));
        return result;
    }

    private async Task RunMultiAxisCommissioningRecipeAsync()
    {
        if (!CanRunMultiAxisCommissioningRecipe)
        {
            return;
        }

        if (_engine.CurrentSnapshot.RunMode != SimulationRunMode.Paused)
        {
            var pause = await _engine.EnqueueCommandAsync(new PauseCommand());
            if (!pause.IsAccepted)
            {
                Log("Motion", $"Recipe preparation rejected · {pause.ErrorCode}: {pause.Detail}");
                return;
            }

            IsRunning = false;
        }

        var manual = await DispatchAxisCommandAsync(
            new StartManualControlCommand(),
            "Axis.ActionStartManual");
        if (!manual.IsAccepted)
        {
            return;
        }

        var move = new MoveAxesAbsoluteCommand(
            MultiAxisCommissioningRecipe.Targets.Select(target =>
                new AxisMoveTarget(target.AxisId, target.TargetPosition)));
        var moveResult = await DispatchAxisCommandAsync(move, "Axis.ActionRunRecipe");
        if (!moveResult.IsAccepted)
        {
            return;
        }

        IsRunning = true;
    }

    private Task StopMultiAxisCommissioningRecipeAsync() => DispatchAxisCommandAsync(
        new StopAxesCommand(MultiAxisCommissioningRecipe.Targets.Select(target => target.AxisId)),
        "Axis.ActionStopRecipe");

    private async Task ValidateMultiAxisCommissioningRecipeAsync()
    {
        if (!CanValidateMultiAxisCommissioningRecipe
            || _project.MultiAxisCommissioningRecipe is not { } recipe)
        {
            return;
        }

        SimulationRuntimeConfiguration runtime;
        try
        {
            runtime = BuildRuntimeConfiguration(_project);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            StatusMessage = OpenVisionLanguageService.T("Axis.RecipeValidationRejected");
            Log("Motion", $"Commissioning validation rejected · {exception.Message}");
            return;
        }

        var projectJson = _projectStore.SerializeForEvidence(_project);
        var projectPath = _currentProjectPath
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"unsaved-{_project.Id}.ovmachine"));
        _latestCommissioningResult = null;
        _commissioningCompletedRuns = 0;
        SetCommissioningValidationRunning(true);
        StatusMessage = OpenVisionLanguageService.T("Axis.RecipeValidationStarted");
        Log("Motion", $"Commissioning repeat validation started · {recipe.ValidationRepetitions} run(s)");
        try
        {
            _latestCommissioningResult = await new DeterministicMultiAxisCommissioningRunner().RunAsync(
                runtime,
                _project.Id,
                _project.Name,
                projectPath,
                projectJson,
                recipe,
                SimulationFixedStep,
                UpdateCommissioningProgressAsync);
            _commissioningHistory = _commissioningHistory.Append(
                _latestCommissioningResult,
                DateTimeOffset.UtcNow);
            SelectedCommissioningHistoryEntry = _commissioningHistory.Entries[^1];
            _commissioningBaselineComparison = _acceptedCommissioningBaseline?.CompareTo(
                _latestCommissioningResult);
            StatusMessage = _latestCommissioningResult.IsSuccess
                ? OpenVisionLanguageService.T("Axis.RecipeValidationPassedStatus")
                : OpenVisionLanguageService.T("Axis.RecipeValidationMismatchStatus");
            Log(
                "Motion",
                _latestCommissioningResult.IsSuccess
                    ? $"Commissioning repeat validation passed · {_latestCommissioningResult.CompletedRuns} run(s) · {ShortHash(_latestCommissioningResult.EvidenceHash)}"
                    : $"Commissioning repeat validation mismatch · run {_latestCommissioningResult.FirstMismatch?.RunIndex} · {_latestCommissioningResult.FirstMismatch?.EvidenceKind} · Tick {_latestCommissioningResult.FirstMismatch?.TickIndex}");
            PersistCommissioningResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            StatusMessage = OpenVisionLanguageService.T("Axis.RecipeValidationRejected");
            Log("Motion", $"Commissioning repeat validation rejected · {exception.Message}");
        }
        finally
        {
            SetCommissioningValidationRunning(false);
        }
    }

    private async Task UpdateCommissioningProgressAsync(int completedRuns)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _commissioningCompletedRuns = completedRuns;
            RaiseCommissioningValidationChanged();
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            _commissioningCompletedRuns = completedRuns;
            RaiseCommissioningValidationChanged();
        });
    }

    private void PersistCommissioningResult()
    {
        if (_latestCommissioningResult is null)
        {
            _commissioningArtifactState = BatchArtifactState.None;
        }
        else if (_currentProjectPath is null)
        {
            _commissioningArtifactState = BatchArtifactState.MemoryOnly;
        }
        else if (_project.MultiAxisCommissioningRecipe is not { } recipe
            || !_latestCommissioningResult.IsForContext(
                _project.Id,
                _projectStore.SerializeForEvidence(_project),
                SimulationFixedStep,
                recipe))
        {
            _commissioningArtifactState = BatchArtifactState.StaleRejected;
        }
        else
        {
            try
            {
                DeterministicMultiAxisCommissioningResultPackage.SaveToJson(
                    _latestCommissioningResult,
                    CommissioningResultArtifactPath(_currentProjectPath));
                DeterministicMultiAxisCommissioningResultHistory.SaveToJson(
                    _commissioningHistory,
                    CommissioningHistoryArtifactPath(_currentProjectPath));
                if (_acceptedCommissioningBaseline is not null)
                {
                    DeterministicMultiAxisCommissioningBaseline.SaveToJson(
                        _acceptedCommissioningBaseline,
                        CommissioningBaselineArtifactPath(_currentProjectPath));
                }
                _commissioningArtifactState = BatchArtifactState.Saved;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _commissioningArtifactState = BatchArtifactState.SaveFailed;
                Log("Motion", $"Commissioning evidence save failed · {exception.Message}");
            }
        }
        RaiseCommissioningValidationChanged();
    }

    private void RestoreCommissioningResult()
    {
        _latestCommissioningResult = null;
        _acceptedCommissioningBaseline = null;
        _commissioningHistory = DeterministicMultiAxisCommissioningResultHistory.Empty(_project.Id);
        _selectedCommissioningHistoryEntry = null;
        _commissioningBaselineComparison = null;
        _commissioningCompletedRuns = 0;
        if (_currentProjectPath is null)
        {
            _commissioningArtifactState = BatchArtifactState.None;
            RaiseCommissioningValidationChanged();
            return;
        }

        var path = CommissioningResultArtifactPath(_currentProjectPath);
        var history = DeterministicMultiAxisCommissioningResultHistory.LoadFromJson(
            CommissioningHistoryArtifactPath(_currentProjectPath));
        if (history is { } restoredHistory
            && restoredHistory.HasValidEvidenceHash()
            && string.Equals(restoredHistory.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            _commissioningHistory = restoredHistory;
            _selectedCommissioningHistoryEntry = restoredHistory.Entries.LastOrDefault();
        }
        var baseline = DeterministicMultiAxisCommissioningBaseline.LoadFromJson(
            CommissioningBaselineArtifactPath(_currentProjectPath));
        if (baseline?.HasValidEvidenceHash() == true
            && string.Equals(baseline.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            _acceptedCommissioningBaseline = baseline;
        }
        if (!File.Exists(path))
        {
            _commissioningArtifactState = _commissioningHistory.Entries.IsDefaultOrEmpty
                && _acceptedCommissioningBaseline is null
                    ? BatchArtifactState.None
                    : BatchArtifactState.Restored;
            RaiseCommissioningValidationChanged();
            return;
        }

        var result = DeterministicMultiAxisCommissioningResultPackage.LoadFromJson(path);
        if (result is not null
            && _project.MultiAxisCommissioningRecipe is { } recipe
            && result.IsForContext(
                _project.Id,
                _projectStore.SerializeForEvidence(_project),
                SimulationFixedStep,
                recipe))
        {
            _latestCommissioningResult = result;
            _commissioningCompletedRuns = result.CompletedRuns;
            _commissioningBaselineComparison = _acceptedCommissioningBaseline?.CompareTo(result);
            _commissioningArtifactState = BatchArtifactState.Restored;
            Log("Motion", "Saved commissioning validation result restored");
        }
        else
        {
            _commissioningArtifactState = BatchArtifactState.StaleRejected;
            Log("Motion", "Saved commissioning validation result rejected because project or recipe changed");
        }
        RaiseCommissioningValidationChanged();
    }

    private void RelinkCommissioningProjectPath(string projectPath)
    {
        if (_latestCommissioningResult is not null)
        {
            _latestCommissioningResult = _latestCommissioningResult with
            {
                ProjectPath = Path.GetFullPath(projectPath)
            };
        }
    }

    private static string CommissioningResultArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.commissioning-result.json";

    private static string CommissioningHistoryArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.commissioning-history.json";

    private static string CommissioningBaselineArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.commissioning-baseline.json";

    private void AcceptCommissioningBaseline()
    {
        var baseline = SelectedCommissioningHistoryEntry?.Reference;
        if (baseline is null || !baseline.HasValidEvidenceHash())
        {
            return;
        }

        _acceptedCommissioningBaseline = baseline;
        _commissioningBaselineComparison = baseline.CompareTo(_latestCommissioningResult);
        StatusMessage = OpenVisionLanguageService.T("Axis.RecipeBaselineAcceptedStatus");
        Log("Motion", $"Commissioning baseline accepted · {ShortHash(baseline.EvidenceHash)}");
        PersistCommissioningResult();
    }

    private void ClearCommissioningBaseline()
    {
        _acceptedCommissioningBaseline = null;
        _commissioningBaselineComparison = null;
        if (_currentProjectPath is not null)
        {
            try
            {
                File.Delete(CommissioningBaselineArtifactPath(_currentProjectPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _commissioningArtifactState = BatchArtifactState.SaveFailed;
                Log("Motion", $"Commissioning baseline clear failed · {exception.Message}");
            }
        }
        StatusMessage = OpenVisionLanguageService.T("Axis.RecipeBaselineClearedStatus");
        RaiseCommissioningValidationChanged();
    }

    private void NavigateToCommissioningMismatch()
    {
        var mismatch = _commissioningBaselineComparison?.FirstMismatch;
        if (mismatch is null || string.IsNullOrWhiteSpace(mismatch.TargetId))
        {
            return;
        }

        var stage = Layout.Items.FirstOrDefault(item =>
            string.Equals(item.BehaviorBindingId, mismatch.TargetId, StringComparison.Ordinal)
            || string.Equals(item.Id, mismatch.TargetId, StringComparison.Ordinal));
        if (stage is not null)
        {
            Layout.Select(stage.Id);
        }
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.RecipeMismatchNavigationStatus"),
            mismatch.TargetId,
            mismatch.TickIndex);
        Log("Motion", $"Commissioning mismatch selected · {mismatch.TargetId} · Tick {mismatch.TickIndex}");
    }

    private void SetCommissioningValidationRunning(bool value)
    {
        _isCommissioningValidationRunning = value;
        RaiseCommissioningValidationChanged();
    }

    private void RaiseCommissioningValidationChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(IsCommissioningValidationRunning));
        OnPropertyChanged(nameof(IsCommissioningValidationConfigurationEnabled));
        OnPropertyChanged(nameof(CanValidateMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(CommissioningValidationStatusText));
        OnPropertyChanged(nameof(CommissioningValidationResultText));
        OnPropertyChanged(nameof(CommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CommissioningResultHistoryEntries));
        OnPropertyChanged(nameof(SelectedCommissioningHistoryEntry));
        OnPropertyChanged(nameof(CommissioningHistoryStatusText));
        OnPropertyChanged(nameof(CommissioningBaselineStatusText));
        OnPropertyChanged(nameof(CanAcceptCommissioningBaseline));
        OnPropertyChanged(nameof(CanClearCommissioningBaseline));
        OnPropertyChanged(nameof(CanNavigateToCommissioningMismatch));
        OnPropertyChanged(nameof(IsScenarioConfigurationEnabled));
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanStopTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void OnSimulationWorkspacePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SimulationWorkspaceViewModel.ScheduledFaultKind))
        {
            OnPropertyChanged(nameof(ScheduledFaultTargets));
            SimulationWorkspace.EnsureScheduledFaultTarget(
                ScheduledFaultTargets.Select(target => target.Id));
        }
        if (!_isApplyingProject && !_isSynchronizingSimulationWorkspace && e.PropertyName is
            nameof(SimulationWorkspaceViewModel.SelectedScenarioProfile) or
            nameof(SimulationWorkspaceViewModel.ScenarioSeed) or
            nameof(SimulationWorkspaceViewModel.ScenarioDurationCycles) or
            nameof(SimulationWorkspaceViewModel.ScenarioTargetId) or
            nameof(SimulationWorkspaceViewModel.BatchRepetitionCount) or
            nameof(SimulationWorkspaceViewModel.IsScheduledFaultEnabled) or
            nameof(SimulationWorkspaceViewModel.ScheduledFaultKind) or
            nameof(SimulationWorkspaceViewModel.ScheduledFaultTargetId) or
            nameof(SimulationWorkspaceViewModel.ScheduledFaultForcedValue) or
            nameof(SimulationWorkspaceViewModel.ScheduledFaultInjectTick) or
            nameof(SimulationWorkspaceViewModel.ScheduledFaultHoldTicks) or
            nameof(SimulationWorkspaceViewModel.RestartSequenceAfterFault) or
            nameof(SimulationWorkspaceViewModel.RecoverySequenceId) or
            nameof(SimulationWorkspaceViewModel.RequireAutomaticCycleCompleted) or
            nameof(SimulationWorkspaceViewModel.MinimumCompletedCycles) or
            nameof(SimulationWorkspaceViewModel.RequireNoActiveFaults) or
            nameof(SimulationWorkspaceViewModel.RequireFinalEquipmentState) or
            nameof(SimulationWorkspaceViewModel.FinalEquipmentTargetId) or
            nameof(SimulationWorkspaceViewModel.FinalEquipmentExpectedState))
        {
            SimulationWorkspace.SaveProjectScenario(_project.Simulation);
            MarkProjectChanged(requiresRuntimeRebuild: false);
        }
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
        InvalidateCommands();
    }

    private void OpenConnectionSequenceStep(string sequenceId, string stepId)
    {
        ClearProcessPlanReturnContext();
        TryOpenConnectionSequenceStep(sequenceId, stepId);
    }

    private bool TryOpenConnectionSequenceStep(string sequenceId, string stepId)
    {
        SequenceEditor.SelectStep(sequenceId, stepId);
        if (SequenceEditor.SelectedStep is null)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.OpenStepRejectedStatus");
            return false;
        }

        SelectedDocumentTabIndex = 2;
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.OpenStepStatus"),
            SequenceEditor.SelectedStep.DisplayName);
        return true;
    }

    private void OpenProcessBlockSequenceStep(string sequenceId, string stepId)
    {
        ClearProcessPlanReturnContext();
        if (!TryOpenConnectionSequenceStep(sequenceId, stepId))
        {
            return;
        }

        _processPlanReviewSteps = RecipeConnections.VisibleProcessBlockItems
            .Where(item => item.CanOpenSequenceStep)
            .Select(item => (SequenceId: item.SequenceId!, item.StepId))
            .ToArray();
        _processPlanReviewIndex = Array.FindIndex(
            _processPlanReviewSteps,
            item => string.Equals(item.SequenceId, sequenceId, StringComparison.Ordinal)
                    && string.Equals(item.StepId, stepId, StringComparison.Ordinal));
        if (_processPlanReviewIndex < 0)
        {
            _processPlanReviewSteps = [(sequenceId, stepId)];
            _processPlanReviewIndex = 0;
        }
        _processPlanReturnStepId = stepId;
        RaiseProcessPlanReviewChanged();
    }

    private bool CanReturnToProcessPlan() => RecipeConnections.IsEditable
        && _processPlanReturnStepId is { } stepId
        && RecipeConnections.IsProcessBlockPreviewVisible
        && RecipeConnections.ProcessBlockItems.Any(item => string.Equals(
            item.StepId,
            stepId,
            StringComparison.Ordinal));

    private bool CanMoveProcessPlanReview(int offset)
    {
        var targetIndex = _processPlanReviewIndex + offset;
        if (!CanReturnToProcessPlan()
            || targetIndex < 0
            || targetIndex >= _processPlanReviewSteps.Length)
        {
            return false;
        }

        var target = _processPlanReviewSteps[targetIndex];
        return RecipeConnections.ProcessBlockItems.Any(item =>
            item.CanOpenSequenceStep
            && string.Equals(item.SequenceId, target.SequenceId, StringComparison.Ordinal)
            && string.Equals(item.StepId, target.StepId, StringComparison.Ordinal));
    }

    private void MoveProcessPlanReview(int offset)
    {
        if (!CanMoveProcessPlanReview(offset))
        {
            return;
        }

        var targetIndex = _processPlanReviewIndex + offset;
        var target = _processPlanReviewSteps[targetIndex];
        if (!TryOpenConnectionSequenceStep(target.SequenceId, target.StepId))
        {
            return;
        }

        _processPlanReviewIndex = targetIndex;
        _processPlanReturnStepId = target.StepId;
        RaiseProcessPlanReviewChanged();
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessPlanReviewStatus"),
            _processPlanReviewIndex + 1,
            _processPlanReviewSteps.Length,
            SequenceEditor.SelectedStep!.DisplayName);
    }

    private void ReturnToProcessPlan()
    {
        if (_processPlanReturnStepId is not { } stepId)
        {
            return;
        }

        SelectedDocumentTabIndex = 1;
        var item = RecipeConnections.SelectProcessBlockStep(stepId);
        if (item is null)
        {
            ClearProcessPlanReturnContext();
            return;
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ReturnToProcessPlanStatus"),
            item.StepText);
    }

    private void OnProcessBlockPreviewClosed(object? sender, EventArgs args) =>
        ClearProcessPlanReturnContext();

    private void ClearProcessPlanReturnContext()
    {
        if (_processPlanReturnStepId is null)
        {
            return;
        }

        _processPlanReturnStepId = null;
        _processPlanReviewSteps = [];
        _processPlanReviewIndex = -1;
        RaiseProcessPlanReviewChanged();
    }

    private void RaiseProcessPlanReviewChanged()
    {
        OnPropertyChanged(nameof(HasProcessPlanReturnContext));
        OnPropertyChanged(nameof(ProcessPlanReturnStepId));
        OnPropertyChanged(nameof(ProcessPlanReviewPositionText));
        RaiseCanExecuteChanged(_returnToProcessPlanCommand);
        RaiseCanExecuteChanged(_previousProcessPlanReviewStepCommand);
        RaiseCanExecuteChanged(_nextProcessPlanReviewStepCommand);
    }

    private void ShowConnectionDryRunStep(RecipeDryRunStepPresentation step)
    {
        var steps = RecipeConnections.RecipeDryRunTimeline.ToArray();
        var index = Array.IndexOf(steps, step);
        if (index < 0)
        {
            return;
        }

        _dryRunPlaybackSteps = steps;
        _dryRunPlaybackIndex = index;
        _dryRunPlaybackSnapshots.Publish(step.BoundarySnapshot);
        _isDryRunPlaybackActive = true;
        Layout.IsEditable = false;
        SelectedDocumentTabIndex = 0;
        RaiseDryRunPlaybackChanged();
        StatusMessage = OpenVisionLanguageService.T("Connections.DryRunPlaybackStatus");
    }

    private void MoveDryRunPlayback(int offset)
    {
        var index = _dryRunPlaybackIndex + offset;
        if (!_isDryRunPlaybackActive || index < 0 || index >= _dryRunPlaybackSteps.Length)
        {
            return;
        }

        _dryRunPlaybackIndex = index;
        var step = _dryRunPlaybackSteps[index];
        RecipeConnections.SelectedRecipeDryRunStep = step;
        _dryRunPlaybackSnapshots.Publish(step.BoundarySnapshot);
        RaiseDryRunPlaybackChanged();
    }

    private void ExitDryRunPlayback()
    {
        if (!_isDryRunPlaybackActive)
        {
            return;
        }

        _isDryRunPlaybackActive = false;
        _dryRunPlaybackSteps = [];
        _dryRunPlaybackIndex = -1;
        Layout.IsEditable = IsDesignMode;
        RaiseDryRunPlaybackChanged();
    }

    private void RaiseDryRunPlaybackChanged()
    {
        OnPropertyChanged(nameof(IsDryRunPlaybackActive));
        OnPropertyChanged(nameof(IsSceneEditable));
        OnPropertyChanged(nameof(SceneSnapshotSource));
        OnPropertyChanged(nameof(DryRunPlaybackTitleText));
        OnPropertyChanged(nameof(DryRunPlaybackDetailText));
        OnPropertyChanged(nameof(HasDryRunPlaybackCheckpoint));
        OnPropertyChanged(nameof(HasDryRunPlaybackMismatch));
        OnPropertyChanged(nameof(DryRunPlaybackCheckpointText));
        OnPropertyChanged(nameof(HasDryRunPlaybackLoadLock));
        OnPropertyChanged(nameof(IsDryRunPlaybackLoadLockFault));
        OnPropertyChanged(nameof(DryRunPlaybackLoadLockText));
        OnPropertyChanged(nameof(HasDryRunPlaybackWaferHandler));
        OnPropertyChanged(nameof(IsDryRunPlaybackWaferHandlerFault));
        OnPropertyChanged(nameof(DryRunPlaybackWaferHandlerText));
        OnPropertyChanged(nameof(HasDryRunPlaybackInspectionSorter));
        OnPropertyChanged(nameof(IsDryRunPlaybackInspectionSorterFault));
        OnPropertyChanged(nameof(DryRunPlaybackInspectionSorterText));
        OnPropertyChanged(nameof(HasDryRunPlaybackInspectionHandoff));
        OnPropertyChanged(nameof(IsDryRunPlaybackInspectionHandoffFault));
        OnPropertyChanged(nameof(DryRunPlaybackInspectionHandoffText));
        OnPropertyChanged(nameof(HasDryRunPlaybackOhtHandoff));
        OnPropertyChanged(nameof(IsDryRunPlaybackOhtHandoffFault));
        OnPropertyChanged(nameof(DryRunPlaybackOhtHandoffText));
        OnPropertyChanged(nameof(HasDryRunPlaybackPrealigner));
        OnPropertyChanged(nameof(IsDryRunPlaybackPrealignerFault));
        OnPropertyChanged(nameof(DryRunPlaybackPrealignerText));
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        InvalidateCommands();
    }

    private int ApplyConnectionStationSkeleton(SemiconductorStationSetupDefinition setup)
    {
        ExitDryRunPlayback();
        var result = _stationSkeletonTemplate.Apply(_project, setup);
        if (!result.Changed)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.StationSkeletonNoChangesStatus");
            return 0;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        SequenceEditor.RefreshAuthoringTargets();
        _layoutEditHistory.Clear();
        _layoutAuthoringState = CaptureLayoutAuthoringState();
        StatusMessage = result.AppliedCount > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Connections.StationSkeletonAppliedStatus"),
                result.AppliedCount)
            : OpenVisionLanguageService.T("Connections.StationSetupAppliedStatus");
        Log("Project", $"Applied semiconductor station setup · {result.AppliedCount} missing role(s)");
        InvalidateCommands();
        return Math.Max(1, result.AppliedCount);
    }

    private int ApplyConnectionLoadLockSetup(LoadLockDefinition setup)
    {
        ExitDryRunPlayback();
        var loadLocks = _project.Devices
            .Where(device => device.Kind == DeviceKind.LoadLock)
            .ToArray();
        if (loadLocks.Length > 1)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.LoadLockSetupMultipleError");
            return 0;
        }

        var device = loadLocks.SingleOrDefault();
        var channelIds = new[]
        {
            setup.EvacuateCommandChannelId,
            setup.VentCommandChannelId,
            setup.VacuumReadySensorChannelId,
            setup.AtmosphereReadySensorChannelId
        };
        var current = device?.LoadLock;
        var changed = current is null
            || !string.Equals(current.OuterDoorComponentId, setup.OuterDoorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.InnerDoorComponentId, setup.InnerDoorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.EvacuateCommandChannelId, setup.EvacuateCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.VentCommandChannelId, setup.VentCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.VacuumReadySensorChannelId, setup.VacuumReadySensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.AtmosphereReadySensorChannelId, setup.AtmosphereReadySensorChannelId, StringComparison.Ordinal)
            || current.PumpDownDurationMilliseconds != setup.PumpDownDurationMilliseconds
            || current.VentDurationMilliseconds != setup.VentDurationMilliseconds
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.LoadLockSetupNoChangesStatus");
            return 0;
        }

        if (device is null)
        {
            var ordinal = NextOrdinal("load-lock", _project.Devices.Select(item => item.Id));
            device = new DeviceDefinition
            {
                Id = $"load-lock-{ordinal}",
                Name = $"Load Lock Chamber {ordinal}",
                Kind = DeviceKind.LoadLock,
                MountPosition = new Coordinate3D(0, 0, 0)
            };
            _project.Devices.Add(device);
        }
        device.ChannelIds = [.. channelIds];
        device.LoadLock = new LoadLockDefinition
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

        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        SequenceEditor.RefreshAuthoringTargets();
        _layoutEditHistory.Clear();
        _layoutAuthoringState = CaptureLayoutAuthoringState();
        StatusMessage = OpenVisionLanguageService.T("Connections.LoadLockSetupAppliedStatus");
        Log("Project", $"Applied load-lock setup · {device.Id}");
        InvalidateCommands();
        return 1;
    }

    private int ApplyConnectionWaferHandlerSetup(WaferHandlerDefinition setup)
    {
        ExitDryRunPlayback();
        var devices = _project.Devices.Where(device => device.Kind == DeviceKind.Handler).ToArray();
        if (devices.Length > 1)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.WaferHandlerSetupMultipleError");
            return 0;
        }
        var channelIds = new[] { setup.SourcePresentSensorChannelId, setup.GateOpenSensorChannelId, setup.PickCommandChannelId, setup.PlaceCommandChannelId, setup.HoldingFeedbackChannelId, setup.PlacedFeedbackChannelId };
        var device = devices.SingleOrDefault();
        if (device is not null && device.WaferHandler is not null && device.WaferHandler.HorizontalAxisId == setup.HorizontalAxisId && device.WaferHandler.VerticalAxisId == setup.VerticalAxisId && device.WaferHandler.WorkpieceComponentId == setup.WorkpieceComponentId && device.WaferHandler.SourcePresentSensorChannelId == setup.SourcePresentSensorChannelId && device.WaferHandler.GateOpenSensorChannelId == setup.GateOpenSensorChannelId && device.WaferHandler.PickCommandChannelId == setup.PickCommandChannelId && device.WaferHandler.PlaceCommandChannelId == setup.PlaceCommandChannelId && device.WaferHandler.HoldingFeedbackChannelId == setup.HoldingFeedbackChannelId && device.WaferHandler.PlacedFeedbackChannelId == setup.PlacedFeedbackChannelId && device.WaferHandler.PickHorizontalPosition == setup.PickHorizontalPosition && device.WaferHandler.PickVerticalPosition == setup.PickVerticalPosition && device.WaferHandler.PlaceHorizontalPosition == setup.PlaceHorizontalPosition && device.WaferHandler.PlaceVerticalPosition == setup.PlaceVerticalPosition && device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal))
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.WaferHandlerSetupNoChangesStatus");
            return 0;
        }
        if (device is null)
        {
            var ordinal = NextOrdinal("wafer-handler", _project.Devices.Select(item => item.Id));
            device = new DeviceDefinition { Id = $"wafer-handler-{ordinal}", Name = $"Wafer Handler {ordinal}", Kind = DeviceKind.Handler, MountPosition = new Coordinate3D(0, 0, 0) };
            _project.Devices.Add(device);
        }
        device.ChannelIds = [.. channelIds];
        device.WaferHandler = new WaferHandlerDefinition { HorizontalAxisId = setup.HorizontalAxisId, VerticalAxisId = setup.VerticalAxisId, WorkpieceComponentId = setup.WorkpieceComponentId, SourcePresentSensorChannelId = setup.SourcePresentSensorChannelId, GateOpenSensorChannelId = setup.GateOpenSensorChannelId, PickCommandChannelId = setup.PickCommandChannelId, PlaceCommandChannelId = setup.PlaceCommandChannelId, HoldingFeedbackChannelId = setup.HoldingFeedbackChannelId, PlacedFeedbackChannelId = setup.PlacedFeedbackChannelId, PickHorizontalPosition = setup.PickHorizontalPosition, PickVerticalPosition = setup.PickVerticalPosition, PlaceHorizontalPosition = setup.PlaceHorizontalPosition, PlaceVerticalPosition = setup.PlaceVerticalPosition };
        MarkProjectChanged(); UpdateRunToolAvailability(); RefreshDefinitionPresentation(null); SequenceEditor.RefreshAuthoringTargets(); _layoutEditHistory.Clear(); _layoutAuthoringState = CaptureLayoutAuthoringState(); StatusMessage = OpenVisionLanguageService.T("Connections.WaferHandlerSetupAppliedStatus"); Log("Project", $"Applied wafer-handler setup · {device.Id}"); InvalidateCommands(); return 1;
    }

    private int ApplyConnectionPrealignerSetup(PrealignerDefinition setup)
    {
        ExitDryRunPlayback();
        var devices = _project.Devices.Where(device => device.Kind == DeviceKind.Prealigner).ToArray();
        if (devices.Length > 1)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.PrealignerSetupMultipleError");
            return 0;
        }
        var channelIds = new[] { setup.WaferPresentSensorChannelId, setup.AlignmentAcceptedCommandChannelId, setup.AlignmentReadyFeedbackChannelId, setup.AlignmentCompleteFeedbackChannelId };
        var device = devices.SingleOrDefault();
        if (device is not null && device.Prealigner is not null && device.Prealigner.RotaryStageComponentId == setup.RotaryStageComponentId && device.Prealigner.ClampCylinderComponentId == setup.ClampCylinderComponentId && device.Prealigner.WaferPresentSensorChannelId == setup.WaferPresentSensorChannelId && device.Prealigner.AlignmentAcceptedCommandChannelId == setup.AlignmentAcceptedCommandChannelId && device.Prealigner.AlignmentReadyFeedbackChannelId == setup.AlignmentReadyFeedbackChannelId && device.Prealigner.AlignmentCompleteFeedbackChannelId == setup.AlignmentCompleteFeedbackChannelId && device.Prealigner.AlignmentTargetDegrees == setup.AlignmentTargetDegrees && device.Prealigner.AlignmentToleranceDegrees == setup.AlignmentToleranceDegrees)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.PrealignerSetupNoChangesStatus");
            return 0;
        }
        if (device is null)
        {
            var ordinal = NextOrdinal("prealigner", _project.Devices.Select(item => item.Id));
            device = new DeviceDefinition { Id = $"prealigner-{ordinal}", Name = $"Pre-aligner {ordinal}", Kind = DeviceKind.Prealigner, MountPosition = new Coordinate3D(0, 0, 0) };
            _project.Devices.Add(device);
        }
        device.ChannelIds = [.. channelIds];
        device.Prealigner = new PrealignerDefinition { RotaryStageComponentId = setup.RotaryStageComponentId, ClampCylinderComponentId = setup.ClampCylinderComponentId, WaferPresentSensorChannelId = setup.WaferPresentSensorChannelId, AlignmentAcceptedCommandChannelId = setup.AlignmentAcceptedCommandChannelId, AlignmentReadyFeedbackChannelId = setup.AlignmentReadyFeedbackChannelId, AlignmentCompleteFeedbackChannelId = setup.AlignmentCompleteFeedbackChannelId, AlignmentTargetDegrees = setup.AlignmentTargetDegrees, AlignmentToleranceDegrees = setup.AlignmentToleranceDegrees };
        MarkProjectChanged(); UpdateRunToolAvailability(); RefreshDefinitionPresentation(null); SequenceEditor.RefreshAuthoringTargets(); _layoutEditHistory.Clear(); _layoutAuthoringState = CaptureLayoutAuthoringState(); StatusMessage = OpenVisionLanguageService.T("Connections.PrealignerSetupAppliedStatus"); Log("Project", $"Applied pre-aligner setup · {device.Id}"); InvalidateCommands(); return 1;
    }

    private int ApplyConnectionInspectionHandoffSetup(InspectionHandoffDefinition setup)
    {
        ExitDryRunPlayback();
        var devices = _project.Devices.Where(device => device.Kind == DeviceKind.Inspection).ToArray();
        if (devices.Length > 1) { StatusMessage = OpenVisionLanguageService.T("Connections.InspectionHandoffSetupMultipleError"); return 0; }
        var channelIds = new[] { setup.InspectionPositionSensorChannelId, setup.ResultAcceptedCommandChannelId, setup.InspectionReadyFeedbackChannelId, setup.InspectionCompleteFeedbackChannelId };
        var device = devices.SingleOrDefault();
        var current = device?.InspectionHandoff;
        if (device is not null && current is not null && current.CameraId == setup.CameraId && current.InspectionPositionSensorChannelId == setup.InspectionPositionSensorChannelId && current.ResultAcceptedCommandChannelId == setup.ResultAcceptedCommandChannelId && current.InspectionReadyFeedbackChannelId == setup.InspectionReadyFeedbackChannelId && current.InspectionCompleteFeedbackChannelId == setup.InspectionCompleteFeedbackChannelId && device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal)) { StatusMessage = OpenVisionLanguageService.T("Connections.InspectionHandoffSetupNoChangesStatus"); return 0; }
        if (device is null) { var ordinal = NextOrdinal("inspection-handoff", _project.Devices.Select(item => item.Id)); device = new DeviceDefinition { Id = $"inspection-handoff-{ordinal}", Name = $"Inspection Handoff {ordinal}", Kind = DeviceKind.Inspection, MountPosition = new Coordinate3D(0, 0, 0) }; _project.Devices.Add(device); }
        device.ChannelIds = [.. channelIds]; device.InspectionHandoff = new InspectionHandoffDefinition { CameraId = setup.CameraId, InspectionPositionSensorChannelId = setup.InspectionPositionSensorChannelId, ResultAcceptedCommandChannelId = setup.ResultAcceptedCommandChannelId, InspectionReadyFeedbackChannelId = setup.InspectionReadyFeedbackChannelId, InspectionCompleteFeedbackChannelId = setup.InspectionCompleteFeedbackChannelId };
        MarkProjectChanged(); UpdateRunToolAvailability(); RefreshDefinitionPresentation(null); SequenceEditor.RefreshAuthoringTargets(); _layoutEditHistory.Clear(); _layoutAuthoringState = CaptureLayoutAuthoringState(); StatusMessage = OpenVisionLanguageService.T("Connections.InspectionHandoffSetupAppliedStatus"); Log("Project", $"Applied inspection-handoff setup · {device.Id}"); InvalidateCommands(); return 1;
    }

    private int ApplyConnectionInspectionSortRouterSetup(InspectionSortRouterDefinition setup)
    {
        ExitDryRunPlayback();
        var devices = _project.Devices.Where(device => device.Kind == DeviceKind.Sorter).ToArray();
        if (devices.Length > 1) { StatusMessage = OpenVisionLanguageService.T("Connections.InspectionSortSetupMultipleError"); return 0; }
        var channelIds = new[] { setup.PassRoutedFeedbackChannelId, setup.NgRoutedFeedbackChannelId };
        var device = devices.SingleOrDefault();
        var current = device?.InspectionSortRouter;
        if (device is not null && current is not null && current.CameraId == setup.CameraId && current.PassConveyorComponentId == setup.PassConveyorComponentId && current.NgConveyorComponentId == setup.NgConveyorComponentId && current.PassRoutedFeedbackChannelId == setup.PassRoutedFeedbackChannelId && current.NgRoutedFeedbackChannelId == setup.NgRoutedFeedbackChannelId && device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal)) { StatusMessage = OpenVisionLanguageService.T("Connections.InspectionSortSetupNoChangesStatus"); return 0; }
        if (device is null) { var ordinal = NextOrdinal("inspection-sorter", _project.Devices.Select(item => item.Id)); device = new DeviceDefinition { Id = $"inspection-sorter-{ordinal}", Name = $"Inspection Sorter {ordinal}", Kind = DeviceKind.Sorter, MountPosition = new Coordinate3D(0, 0, 0) }; _project.Devices.Add(device); }
        device.ChannelIds = [.. channelIds]; device.InspectionSortRouter = new InspectionSortRouterDefinition { CameraId = setup.CameraId, PassConveyorComponentId = setup.PassConveyorComponentId, NgConveyorComponentId = setup.NgConveyorComponentId, PassRoutedFeedbackChannelId = setup.PassRoutedFeedbackChannelId, NgRoutedFeedbackChannelId = setup.NgRoutedFeedbackChannelId };
        MarkProjectChanged(); UpdateRunToolAvailability(); RefreshDefinitionPresentation(null); SequenceEditor.RefreshAuthoringTargets(); _layoutEditHistory.Clear(); _layoutAuthoringState = CaptureLayoutAuthoringState(); StatusMessage = OpenVisionLanguageService.T("Connections.InspectionSortSetupAppliedStatus"); Log("Project", $"Applied inspection-sort setup · {device.Id}"); InvalidateCommands(); return 1;
    }

    private int ApplyConnectionOhtHandoffSetup(OhtHandoffDefinition setup)
    {
        ExitDryRunPlayback();
        var devices = _project.Devices.Where(device => device.Kind == DeviceKind.Oht).ToArray();
        if (devices.Length > 1) { StatusMessage = OpenVisionLanguageService.T("Connections.OhtSetupMultipleError"); return 0; }
        var channelIds = new[] { setup.RouteAvailableSensorChannelId, setup.VehicleDockedSensorChannelId, setup.LoadPortReadySensorChannelId, setup.CarrierReceivedSensorChannelId, setup.HandoffReadyFeedbackChannelId, setup.CarrierTransferredFeedbackChannelId };
        var device = devices.SingleOrDefault();
        var current = device?.OhtHandoff;
        if (device is not null && current is not null && current.TransportConveyorComponentId == setup.TransportConveyorComponentId && current.RouteAvailableSensorChannelId == setup.RouteAvailableSensorChannelId && current.VehicleDockedSensorChannelId == setup.VehicleDockedSensorChannelId && current.LoadPortReadySensorChannelId == setup.LoadPortReadySensorChannelId && current.CarrierReceivedSensorChannelId == setup.CarrierReceivedSensorChannelId && current.HandoffReadyFeedbackChannelId == setup.HandoffReadyFeedbackChannelId && current.CarrierTransferredFeedbackChannelId == setup.CarrierTransferredFeedbackChannelId && device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal)) { StatusMessage = OpenVisionLanguageService.T("Connections.OhtSetupNoChangesStatus"); return 0; }
        if (device is null) { var ordinal = NextOrdinal("oht-handoff", _project.Devices.Select(item => item.Id)); device = new DeviceDefinition { Id = $"oht-handoff-{ordinal}", Name = $"OHT Handoff {ordinal}", Kind = DeviceKind.Oht, MountPosition = new Coordinate3D(0, 0, 0) }; _project.Devices.Add(device); }
        device.ChannelIds = [.. channelIds]; device.OhtHandoff = new OhtHandoffDefinition { TransportConveyorComponentId = setup.TransportConveyorComponentId, RouteAvailableSensorChannelId = setup.RouteAvailableSensorChannelId, VehicleDockedSensorChannelId = setup.VehicleDockedSensorChannelId, LoadPortReadySensorChannelId = setup.LoadPortReadySensorChannelId, CarrierReceivedSensorChannelId = setup.CarrierReceivedSensorChannelId, HandoffReadyFeedbackChannelId = setup.HandoffReadyFeedbackChannelId, CarrierTransferredFeedbackChannelId = setup.CarrierTransferredFeedbackChannelId };
        MarkProjectChanged(); UpdateRunToolAvailability(); RefreshDefinitionPresentation(null); SequenceEditor.RefreshAuthoringTargets(); _layoutEditHistory.Clear(); _layoutAuthoringState = CaptureLayoutAuthoringState(); StatusMessage = OpenVisionLanguageService.T("Connections.OhtSetupAppliedStatus"); Log("Project", $"Applied OHT setup · {device.Id}"); InvalidateCommands(); return 1;
    }

    private int ApplyConnectionProcessBlock(IReadOnlyList<SemiconductorProcessBlockKind> kinds)
    {
        ExitDryRunPlayback();
        var result = _processBlockComposer.Apply(_project, kinds);
        if (!result.Changed)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.ProcessBlockEditNoChangesStatus");
            return 0;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        SequenceEditor.Load(_project);
        _layoutEditHistory.Clear();
        _layoutAuthoringState = CaptureLayoutAuthoringState();
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessBlockEditAppliedStatus"),
            kinds.Count,
            result.AddedConnectionCount,
            result.AddedStepCount,
            result.RemovedStepCount);
        Log("Sequence", $"Applied semiconductor process plan · {kinds.Count} block(s) · {result.AddedConnectionCount} connection role(s) · {result.AddedStepCount} step(s) added · {result.RemovedStepCount} managed step(s) removed");
        InvalidateCommands();
        return result.AddedConnectionCount + result.AddedStepCount + result.RemovedStepCount;
    }

    private int ApplyConnectionProcessBlockTimeouts(
        SemiconductorManagedTimeoutAdjustmentPreview preview)
    {
        ExitDryRunPlayback();
        var result = _processBlockComposer.ApplyTimeoutAdjustment(_project, preview);
        if (!result.Changed)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.ProcessBlockTimeoutRejectedStatus");
            return 0;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        SequenceEditor.Load(_project);
        RecipeConnections.RefreshDefinitionPreservingProcessBlockPlan(Layout.SelectedItem?.Id);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessBlockTimeoutAppliedStatus"),
            result.AppliedStepCount,
            preview.ProposedTimeoutMs);
        Log(
            "Sequence",
            $"Applied managed timeout adjustment · {result.AppliedStepCount} step(s) · {preview.ProposedTimeoutMs} ms");
        InvalidateCommands();
        return result.AppliedStepCount;
    }

    private string? AddConnectionSequenceStep(string targetId)
    {
        string? stepId = SequenceEditor.TryAddStepForTarget(targetId);
        if (stepId is null)
        {
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Connections.AddStepRejectedStatus"),
                SequenceEditor.StructuralEditStatus);
            return null;
        }

        SelectedDocumentTabIndex = 2;
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.AddStepStatus"),
            targetId);
        Log("Sequence", $"Added connection target step · {stepId} · {targetId}");
        return stepId;
    }

    private void OnConnectionCheckpointTemplateApplied(int appliedCount)
    {
        ExitDryRunPlayback();
        if (appliedCount <= 0)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.CheckpointTemplateNoChangesStatus");
            return;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        SequenceEditor.RefreshAuthoringTargets();
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.CheckpointTemplateAppliedStatus"),
            appliedCount);
        Log("Sequence", $"Applied representative recipe checkpoints · {appliedCount}");
        InvalidateCommands();
    }

    private string? ValidateConnectionSimulationReadiness()
    {
        try
        {
            BuildRuntimeConfiguration(_project);
            StatusMessage = OpenVisionLanguageService.T("Connections.ReadinessPassedStatus");
            Log("Project", "Simulation readiness validation passed without applying or running the runtime");
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.ReadinessFailedStatus");
            Log("Project", $"Simulation readiness validation rejected · {exception.Message}");
            return exception.Message;
        }
    }

    private async Task<SequenceStepPreviewResult> RunConnectionSequenceStepPreviewAsync(
        string sequenceId,
        string stepId,
        string componentId)
    {
        var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(
            _project,
            sequenceId,
            stepId,
            componentId);
        StatusMessage = OpenVisionLanguageService.T(result.IsCompleted
            ? "Connections.PreviewCompletedStatus"
            : "Connections.PreviewStoppedStatus");
        Log(
            "Simulation",
            $"Isolated connection step preview · {sequenceId}/{stepId} · {result.Outcome} · {result.ExecutedTicks}/{result.MaximumTicks} ticks");
        return result;
    }

    private async Task<RecipeDryRunResult> RunConnectionRecipeDryRunAsync(string sequenceId)
    {
        var result = await new DeterministicRecipeDryRunRunner().RunAsync(_project, sequenceId);
        StatusMessage = OpenVisionLanguageService.T(result.IsCompleted
            ? "Connections.DryRunCompletedStatus"
            : "Connections.DryRunStoppedStatus");
        Log(
            "Simulation",
            $"Isolated recipe dry run · {sequenceId} · {result.Outcome} · {result.ExecutedTicks}/{result.MaximumTicks} ticks · {result.Timeline.Count} steps");
        return result;
    }

    private async Task StopAxisJogAfterStartAsync(
        string axisId,
        Task<SimulationCommandResult> startTask)
    {
        var startResult = await startTask;
        if (startResult.IsAccepted)
        {
            await DispatchAxisCommandAsync(
                new StopAxisCommand(axisId),
                "Axis.ActionStop");
        }
    }

    private Task StopAxisMotionAsync()
    {
        if (_axisJogInteractionActive)
        {
            return EndAxisJogAsync();
        }

        return _currentAxis is null
            ? Task.CompletedTask
            : DispatchAxisCommandAsync(
                new StopAxisCommand(_currentAxis.Id),
                "Axis.ActionStop");
    }

    private Task SetCylinderCommandAsync(bool extend)
    {
        var cylinder = CurrentCylinderSnapshot;
        return cylinder is null
            ? Task.CompletedTask
            : DispatchCylinderCommandAsync(
                new SetCylinderCommand(cylinder.Id, extend),
                extend ? "Cylinder.ActionExtend" : "Cylinder.ActionRetract");
    }

    private Task SetSensorForceAsync(bool? forcedValue)
    {
        var sensor = CurrentSensorSnapshot;
        return sensor is null
            ? Task.CompletedTask
            : DispatchSensorCommandAsync(
                new SetDigitalSensorForceCommand(sensor.Id, forcedValue),
                forcedValue switch
                {
                    true => "Sensor.ActionForceOn",
                    false => "Sensor.ActionForceOff",
                    null => "Sensor.ActionClearForce"
                });
    }

    private async Task<SimulationCommandResult> DispatchSensorCommandAsync(
        SimulationCommand command,
        string actionKey)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        var action = OpenVisionLanguageService.T(actionKey);
        var message = result.IsAccepted
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Sensor.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Sensor.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail);
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                result.IsAccepted ? "Sensor.StatusAccepted" : "Sensor.StatusRejected"),
            action);
        Log("Sensor", message);
        return result;
    }

    private async Task<SimulationCommandResult> DispatchCylinderCommandAsync(
        SimulationCommand command,
        string actionKey)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        var action = OpenVisionLanguageService.T(actionKey);
        var message = result.IsAccepted
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Cylinder.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Cylinder.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail);
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                result.IsAccepted ? "Cylinder.StatusAccepted" : "Cylinder.StatusRejected"),
            action);
        Log("Cylinder", message);
        return result;
    }

    private Task SetConveyorCommandAsync(bool running, ConveyorDirection direction)
    {
        var conveyor = CurrentConveyorSnapshot;
        return conveyor is null
            ? Task.CompletedTask
            : DispatchConveyorCommandAsync(
                new SetConveyorCommand(conveyor.Id, running, direction),
                running
                    ? direction == ConveyorDirection.Forward
                        ? "Conveyor.ActionRunForward"
                        : "Conveyor.ActionRunReverse"
                    : "Conveyor.ActionStop");
    }

    private async Task<SimulationCommandResult> DispatchConveyorCommandAsync(
        SimulationCommand command,
        string actionKey)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        var action = OpenVisionLanguageService.T(actionKey);
        var message = result.IsAccepted
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Conveyor.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Conveyor.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail);
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                result.IsAccepted ? "Conveyor.StatusAccepted" : "Conveyor.StatusRejected"),
            action);
        Log("Conveyor", message);
        return result;
    }

    private void NotifyAxisCommissioningChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(CurrentAxisHomeText));
        OnPropertyChanged(nameof(CurrentAxisLimitsText));
        OnPropertyChanged(nameof(CurrentAxisFollowingErrorText));
        OnPropertyChanged(nameof(CurrentAxisDriveTuningText));
        OnPropertyChanged(nameof(IsCurrentAxisDriveAlarmActive));
        OnPropertyChanged(nameof(CurrentAxisDriveAlarmText));
        OnPropertyChanged(nameof(CurrentAxisUnitText));
        OnPropertyChanged(nameof(CurrentAxisVelocityUnitText));
        OnPropertyChanged(nameof(IsAxisTargetPositionValid));
        OnPropertyChanged(nameof(HasAxisTargetPositionError));
        OnPropertyChanged(nameof(AxisTargetPositionValidationText));
        OnPropertyChanged(nameof(IsAxisRelativeDistanceValid));
        OnPropertyChanged(nameof(HasAxisRelativeDistanceError));
        OnPropertyChanged(nameof(AxisRelativeDistanceValidationText));
        OnPropertyChanged(nameof(IsAxisCommandVelocityValid));
        OnPropertyChanged(nameof(HasAxisCommandVelocityError));
        OnPropertyChanged(nameof(AxisCommandVelocityValidationText));
        OnPropertyChanged(nameof(IsCurrentAxisInterlocked));
        OnPropertyChanged(nameof(CurrentAxisInterlockText));
        OnPropertyChanged(nameof(AxisCommissioningHintText));
        OnPropertyChanged(nameof(CanMoveAxisAbsolute));
        OnPropertyChanged(nameof(CanMoveAxisRelative));
        OnPropertyChanged(nameof(CanMoveAxisVelocity));
        OnPropertyChanged(nameof(CanJogAxis));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifyMultiAxisCommissioningRecipeChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(IsMultiAxisCommissioningRecipeSelection));
        OnPropertyChanged(nameof(HasMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(CanRunMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(CanStopMultiAxisCommissioningRecipe));
        RaiseCommissioningValidationChanged(invalidateCommands);
    }

    private void NotifyCylinderCommissioningChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(HasSelectedPneumaticCylinder));
        OnPropertyChanged(nameof(IsCurrentCylinderInterlocked));
        OnPropertyChanged(nameof(CurrentCylinderInterlockText));
        OnPropertyChanged(nameof(CylinderCommissioningHintText));
        OnPropertyChanged(nameof(CanExtendCylinder));
        OnPropertyChanged(nameof(CanRetractCylinder));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifySensorCommissioningChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(HasSelectedDigitalSensor));
        OnPropertyChanged(nameof(IsCurrentSensorFaulted));
        OnPropertyChanged(nameof(IsCurrentSensorManuallyForced));
        OnPropertyChanged(nameof(CurrentSensorForceText));
        OnPropertyChanged(nameof(SensorCommissioningHintText));
        OnPropertyChanged(nameof(CanForceSensorOn));
        OnPropertyChanged(nameof(CanForceSensorOff));
        OnPropertyChanged(nameof(CanClearSensorForce));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifyConveyorCommissioningChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(HasSelectedConveyor));
        OnPropertyChanged(nameof(ConveyorCommissioningHintText));
        OnPropertyChanged(nameof(CanRunConveyorForward));
        OnPropertyChanged(nameof(CanRunConveyorReverse));
        OnPropertyChanged(nameof(CanStopConveyor));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifyCameraCommissioningChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(CurrentCameraExposureTicksText));
        OnPropertyChanged(nameof(CurrentCameraTransferTicksText));
        OnPropertyChanged(nameof(CurrentCameraSourceText));
        OnPropertyChanged(nameof(CurrentCameraFrameHashText));
        if (HasVirtualCamera)
        {
            OnPropertyChanged(nameof(CurrentCameraEvidenceDetailsText));
        }
        OnPropertyChanged(nameof(CameraCommissioningHintText));
        OnPropertyChanged(nameof(CanStartManualCameraControl));
        OnPropertyChanged(nameof(CanTriggerCamera));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void OnCameraImageSourceApplied(string cameraId, string detail)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            StatusMessage = OpenVisionLanguageService.T("Camera.SourceMustBeProjectOwned");
            Log("Camera", $"Image source selection rejected · {detail}");
            return;
        }

        MarkProjectChanged(requiresRuntimeRebuild: false);
        StatusMessage = OpenVisionLanguageService.T("Camera.SourceAppliedSave");
        Log("Camera", $"Image source applied · {cameraId} · {detail}");
        RefreshVisionEvidenceContext();
        NotifyCameraCommissioningChanged();
    }

    private void NotifyManualCommissioningChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(HasSelectedAxisStage));
        OnPropertyChanged(nameof(HasSelectedManualEquipment));
        OnPropertyChanged(nameof(CanStartManualEquipmentControl));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifyModeDependentCommandsChanged()
    {
        if (HasSelectedManualEquipment)
        {
            OnPropertyChanged(nameof(CanStartManualEquipmentControl));
        }
        if (HasSelectedAxisDefinition || HasSelectedAxisStage)
        {
            OnPropertyChanged(nameof(AxisCommissioningHintText));
            OnPropertyChanged(nameof(CanMoveAxisAbsolute));
            OnPropertyChanged(nameof(CanMoveAxisRelative));
            OnPropertyChanged(nameof(CanMoveAxisVelocity));
            OnPropertyChanged(nameof(CanJogAxis));
        }
        if (HasSelectedDigitalSensor)
        {
            OnPropertyChanged(nameof(SensorCommissioningHintText));
            OnPropertyChanged(nameof(CanForceSensorOn));
            OnPropertyChanged(nameof(CanForceSensorOff));
            OnPropertyChanged(nameof(CanClearSensorForce));
        }
        if (HasSelectedPneumaticCylinder)
        {
            OnPropertyChanged(nameof(CylinderCommissioningHintText));
            OnPropertyChanged(nameof(CanExtendCylinder));
            OnPropertyChanged(nameof(CanRetractCylinder));
        }
        if (HasSelectedConveyor)
        {
            OnPropertyChanged(nameof(ConveyorCommissioningHintText));
            OnPropertyChanged(nameof(CanRunConveyorForward));
            OnPropertyChanged(nameof(CanRunConveyorReverse));
            OnPropertyChanged(nameof(CanStopConveyor));
        }
        if (HasVirtualCamera)
        {
            OnPropertyChanged(nameof(CameraCommissioningHintText));
            OnPropertyChanged(nameof(CanStartManualCameraControl));
            OnPropertyChanged(nameof(CanTriggerCamera));
        }
        if (HasMultiAxisCommissioningRecipe)
        {
            OnPropertyChanged(nameof(CanRunMultiAxisCommissioningRecipe));
            OnPropertyChanged(nameof(CanStopMultiAxisCommissioningRecipe));
            OnPropertyChanged(nameof(IsCommissioningValidationConfigurationEnabled));
            OnPropertyChanged(nameof(CanValidateMultiAxisCommissioningRecipe));
        }
        OnPropertyChanged(nameof(IsScenarioConfigurationEnabled));
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanStopTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
    }

    private void UpdateRunToolAvailability()
    {
        var isEnabled = IsRunMode && !_isApplyingProject && !_runtimeDefinitionDirty;
        DigitalIo.SetEnabled(isEnabled, invalidateCommands: false);
        FaultManager.SetEnabled(isEnabled, invalidateCommands: false);
    }

    private async Task<SimulationCommandResult> DispatchDigitalIoCommandAsync(
        SimulationCommand command)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        string actionKey = command switch
        {
            StartManualControlCommand => "Io.ActionStartManual",
            SetVirtualInputForceCommand { ForcedValue: true } => "Io.ActionForceOn",
            SetVirtualInputForceCommand { ForcedValue: false } => "Io.ActionForceOff",
            SetVirtualInputForceCommand => "Io.ActionClearForce",
            _ => "Io.ActionCommand"
        };
        var action = OpenVisionLanguageService.T(actionKey);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                result.IsAccepted ? "Io.StatusAccepted" : "Io.StatusRejected"),
            action);
        Log("I/O", result.IsAccepted
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Io.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Io.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail));
        return result;
    }

    private async Task<SimulationCommandResult> DispatchFaultCommandAsync(
        SimulationCommand command)
    {
        var result = await _engine.EnqueueCommandAsync(command);
        var isInject = command is InjectSimulationFaultCommand;
        var action = OpenVisionLanguageService.T(isInject ? "Fault.ActionInject" : "Fault.ActionClear");
        if (!result.IsAccepted)
        {
            StatusMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Fault.StatusRejected"),
                action);
            Log("Fault", string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Fault.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail));
            return result;
        }

        StatusMessage = OpenVisionLanguageService.T(
            isInject ? "Fault.InjectAcceptedStatus" : "Fault.ClearAcceptedStatus");
        Log("Fault", string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Fault.CommandAccepted"),
            action,
            ShortCommandId(command)));
        return result;
    }

    private void Log(string category, string message) =>
        AppendLog(_simulationTime, category, message);

    private void LogRuntimeEvent(SimulationEvent runtimeEvent)
    {
        _activeVisionEvidenceRecorder?.RecordEvent(runtimeEvent);
        TryCompleteVisionEvidence(_engine.CurrentSnapshot);
        AppendLog(runtimeEvent.SimulationTime, runtimeEvent.Category, runtimeEvent.Message);
    }

    private void AppendLog(TimeSpan time, string category, string message)
    {
        var localizedCategory = OpenVisionLanguageService.T(
            $"Runtime.Category.{category}",
            category,
            category);
        var line = $"[{time:hh\\:mm\\:ss\\.fff}] {localizedCategory} · {SimulationLogEntry.LocalizeMessage(message)}";
        LogMessages.Add(line);
        _logger.Log(line);
    }

    private static string FormatSignal(bool? value) => value switch
    {
        true => OpenVisionLanguageService.T("Shell.SignalOn"),
        false => OpenVisionLanguageService.T("Shell.SignalOff"),
        null => OpenVisionLanguageService.T("Shell.NotConfigured")
    };

    private static string LocalizeRuntimeState(string state) =>
        OpenVisionLanguageService.T($"Equipment.State.{state}", state, state);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ExitDryRunPlayback();
        var language = OpenVisionLanguageService.CurrentLanguage;
        var option = LanguageOptions.FirstOrDefault(item => item.Language == language);
        if (option is not null && !ReferenceEquals(_selectedLanguageOption, option))
        {
            _selectedLanguageOption = option;
            OnPropertyChanged(nameof(SelectedLanguageOption));
        }

        foreach (var propertyName in LocalizedPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }

        Properties.RefreshLocalization();
        CameraImageSourceEditor.RefreshLocalization();
        AxisDriveTuningEditor?.RefreshLocalization();
        MultiAxisCommissioningRecipe.RefreshLocalization();
        SemiconductorRecipes.RefreshLocalization();
        RecipeConnections.RefreshLocalization();
        Layout.RefreshLocalization();
        DigitalIo.RefreshLocalization();
        FaultManager.RefreshLocalization();
        SequenceEditor.RefreshLocalization();
    }

    private static readonly string[] LocalizedPropertyNames =
    [
        nameof(ModeText), nameof(StateText), nameof(LeftPanelHeaderText), nameof(RightPanelHeaderText),
        nameof(ProjectStatusText), nameof(SelectionStatusText),
        nameof(SimulationStatusText), nameof(TickStatusText), nameof(FixedStepStatusText),
        nameof(RunStatusText), nameof(ControlOwnerHelpText), nameof(ControlOwnerText),
        nameof(SceneControlText), nameof(CurrentAxisName), nameof(CurrentAxisStateText),
        nameof(CurrentAxisHomeText), nameof(CurrentAxisLimitsText), nameof(CurrentAxisUnitText),
        nameof(CurrentAxisFollowingErrorText), nameof(CurrentAxisDriveTuningText),
        nameof(CurrentAxisDriveAlarmText),
        nameof(CurrentAxisVelocityUnitText), nameof(AxisTargetPositionValidationText),
        nameof(AxisRelativeDistanceValidationText), nameof(AxisCommandVelocityValidationText),
        nameof(IsCurrentAxisInterlocked),
        nameof(CurrentAxisInterlockText), nameof(AxisCommissioningHintText),
        nameof(CurrentSensorForceText), nameof(SensorCommissioningHintText),
        nameof(CurrentCylinderInterlockText), nameof(CylinderCommissioningHintText),
        nameof(ConveyorCommissioningHintText),
        nameof(CurrentCameraName), nameof(CurrentCameraStateText), nameof(CurrentCameraResultText),
        nameof(CameraCommissioningHintText), nameof(VisionEvidenceStatusText),
        nameof(VisionEvidenceComparisonText), nameof(CurrentCameraEvidenceDetailsText),
        nameof(CurrentSequenceName), nameof(CurrentSequenceStateText), nameof(CurrentSequenceStepText),
        nameof(AutomaticRunStateText),
        nameof(ConditionScenarioStateText), nameof(ConditionScenarioProgressText),
        nameof(ConditionScenarioHealthText),
        nameof(BatchStatusText), nameof(BatchResultText), nameof(BatchBaselineText),
        nameof(BatchArtifactStatusText), nameof(BatchAssertionOutcomes),
        nameof(SelectedEquipmentStatus), nameof(ProcessPlanReviewPositionText)
    ];

    private AsyncRelayCommand CreateAsyncCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null) =>
        new(
            execute,
            canExecute,
            HandleCommandException,
            useCommandManagerRequery: false);

    private static RelayCommand CreateRelayCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null) =>
        new(execute, canExecute, useCommandManagerRequery: false);

    private void InvalidateCommands(bool includeCommandManager = true)
    {
        RaiseCanExecuteChanged(_newProjectCommand);
        RaiseCanExecuteChanged(_openProjectCommand);
        RaiseCanExecuteChanged(_saveProjectCommand);
        RaiseCanExecuteChanged(_runCommand);
        RaiseCanExecuteChanged(_pauseCommand);
        RaiseCanExecuteChanged(_stepCommand);
        RaiseCanExecuteChanged(_resetCommand);
        RaiseCanExecuteChanged(_startTestScenarioCommand);
        RaiseCanExecuteChanged(_stopTestScenarioCommand);
        RaiseCanExecuteChanged(_replayTestScenarioCommand);
        RaiseCanExecuteChanged(_runScenarioBatchCommand);
        RaiseCanExecuteChanged(_cancelScenarioBatchCommand);
        RaiseCanExecuteChanged(_acceptBatchBaselineCommand);
        RaiseCanExecuteChanged(_clearBatchBaselineCommand);
        RaiseCanExecuteChanged(_navigateToBatchMismatchCommand);
        RaiseCanExecuteChanged(_cycleStartCommand);
        RaiseCanExecuteChanged(_startManualEquipmentControlCommand);
        RaiseCanExecuteChanged(_startManualCameraControlCommand);
        RaiseCanExecuteChanged(_triggerCameraCommand);
        RaiseCanExecuteChanged(_moveAxisAbsoluteCommand);
        RaiseCanExecuteChanged(_moveAxisRelativeCommand);
        RaiseCanExecuteChanged(_moveAxisVelocityCommand);
        RaiseCanExecuteChanged(_beginAxisJogNegativeCommand);
        RaiseCanExecuteChanged(_beginAxisJogPositiveCommand);
        RaiseCanExecuteChanged(_endAxisJogCommand);
        RaiseCanExecuteChanged(_homeAxisCommand);
        RaiseCanExecuteChanged(_stopAxisMotionCommand);
        RaiseCanExecuteChanged(_runMultiAxisCommissioningRecipeCommand);
        RaiseCanExecuteChanged(_stopMultiAxisCommissioningRecipeCommand);
        RaiseCanExecuteChanged(_validateMultiAxisCommissioningRecipeCommand);
        RaiseCanExecuteChanged(_acceptCommissioningBaselineCommand);
        RaiseCanExecuteChanged(_clearCommissioningBaselineCommand);
        RaiseCanExecuteChanged(_navigateToCommissioningMismatchCommand);
        RaiseCanExecuteChanged(_forceSensorOnCommand);
        RaiseCanExecuteChanged(_forceSensorOffCommand);
        RaiseCanExecuteChanged(_clearSensorForceCommand);
        RaiseCanExecuteChanged(_extendCylinderCommand);
        RaiseCanExecuteChanged(_retractCylinderCommand);
        RaiseCanExecuteChanged(_runConveyorForwardCommand);
        RaiseCanExecuteChanged(_runConveyorReverseCommand);
        RaiseCanExecuteChanged(_stopConveyorCommand);
        RaiseCanExecuteChanged(_addLayoutComponentCommand);
        RaiseCanExecuteChanged(_deleteLayoutComponentCommand);
        RaiseCanExecuteChanged(_nudgeLayoutComponentCommand);
        RaiseCanExecuteChanged(_alignLayoutSelectionCommand);
        RaiseCanExecuteChanged(_changeLayoutLayerOrderCommand);
        RaiseCanExecuteChanged(_undoLayoutEditCommand);
        RaiseCanExecuteChanged(_redoLayoutEditCommand);
        RaiseCanExecuteChanged(_copyLayoutSelectionCommand);
        RaiseCanExecuteChanged(_duplicateLayoutSelectionCommand);
        RaiseCanExecuteChanged(_pasteLayoutSelectionCommand);
        RaiseCanExecuteChanged(_previousDryRunPlaybackStepCommand);
        RaiseCanExecuteChanged(_nextDryRunPlaybackStepCommand);
        RaiseCanExecuteChanged(_exitDryRunPlaybackCommand);
        RaiseCanExecuteChanged(_returnToProcessPlanCommand);
        RaiseCanExecuteChanged(_previousProcessPlanReviewStepCommand);
        RaiseCanExecuteChanged(_nextProcessPlanReviewStepCommand);

        if (includeCommandManager)
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void InvalidateModeCommands()
    {
        RaiseCanExecuteChanged(_runCommand);
        RaiseCanExecuteChanged(_pauseCommand);
        RaiseCanExecuteChanged(_stepCommand);
        RaiseCanExecuteChanged(_resetCommand);
        RaiseCanExecuteChanged(_startTestScenarioCommand);
        RaiseCanExecuteChanged(_stopTestScenarioCommand);
        RaiseCanExecuteChanged(_replayTestScenarioCommand);
        RaiseCanExecuteChanged(_runScenarioBatchCommand);
        RaiseCanExecuteChanged(_cycleStartCommand);
        RaiseCanExecuteChanged(_moveAxisAbsoluteCommand);
        RaiseCanExecuteChanged(_moveAxisRelativeCommand);
        RaiseCanExecuteChanged(_moveAxisVelocityCommand);
        RaiseCanExecuteChanged(_beginAxisJogNegativeCommand);
        RaiseCanExecuteChanged(_beginAxisJogPositiveCommand);
        RaiseCanExecuteChanged(_endAxisJogCommand);
        RaiseCanExecuteChanged(_homeAxisCommand);
        RaiseCanExecuteChanged(_stopAxisMotionCommand);
        RaiseCanExecuteChanged(_addLayoutComponentCommand);
        RaiseCanExecuteChanged(_deleteLayoutComponentCommand);
        RaiseCanExecuteChanged(_nudgeLayoutComponentCommand);
        RaiseCanExecuteChanged(_alignLayoutSelectionCommand);
        RaiseCanExecuteChanged(_changeLayoutLayerOrderCommand);
        RaiseCanExecuteChanged(_undoLayoutEditCommand);
        RaiseCanExecuteChanged(_redoLayoutEditCommand);
        RaiseCanExecuteChanged(_copyLayoutSelectionCommand);
        RaiseCanExecuteChanged(_pasteLayoutSelectionCommand);
        RaiseCanExecuteChanged(_returnToProcessPlanCommand);
        RaiseCanExecuteChanged(_previousProcessPlanReviewStepCommand);
        RaiseCanExecuteChanged(_nextProcessPlanReviewStepCommand);
    }

    private static void RaiseCanExecuteChanged(ICommand? command)
    {
        switch (command)
        {
            case AsyncRelayCommand asyncCommand:
                asyncCommand.RaiseCanExecuteChanged();
                break;
            case RelayCommand relayCommand:
                relayCommand.RaiseCanExecuteChanged();
                break;
        }
    }

    private void HandleCommandException(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        StatusMessage = "Command failed";
        Log("Error", exception.Message);
    }

    private static string ShortCommandId(SimulationCommand command) =>
        $"CMD-{command.CommandId[..8].ToUpperInvariant()}";

    private static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];

    private static async Task DispatchAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        ProjectTree.PropertyChanged -= OnProjectTreePropertyChanged;
        Layout.PropertyChanged -= OnLayoutPropertyChanged;
        SimulationWorkspace.PropertyChanged -= OnSimulationWorkspacePropertyChanged;
        RecipeConnections.ProcessBlockPreviewClosed -= OnProcessBlockPreviewClosed;
        Layout.DefinitionChanged -= OnLayoutDefinitionChanged;
        SequenceEditor.DefinitionChanged -= OnSequenceDefinitionChanged;
        _runtimeCancellation.Cancel();
        _batchCancellation?.Cancel();
        _engine.Dispose();
        SimulationWorkspace.Dispose();
        if (_runtimeTask.IsFaulted)
        {
            Trace.TraceError(_runtimeTask.Exception?.ToString());
        }
        _runtimeCancellation.Dispose();
        _batchCancellation?.Dispose();
    }
}
