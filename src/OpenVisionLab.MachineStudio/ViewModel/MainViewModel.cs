using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Diagnostics;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Persistence.History;
using OpenVisionLab.Machine.Simulation.History;
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
using OpenVisionLab.Machine.Vision.Models;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class MainViewModel : ViewModelBase, IDisposable, IShellCloseHost
{
    #region Fields

    #region Constants

    private const string CycleStartInputId = "di.cycle-start";
    private const string CycleActiveOutputId = "do.cycle-active";
    private const string CycleDoneOutputId = "do.cycle-done";
    private const int SimulationFixedStepMilliseconds = 5;
    internal const int LogMessageRetentionLimit = 1000;
    internal const int OperationalDiagnosticRetentionLimit = 1000;
    internal static readonly TimeSpan RuntimeShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SimulationFixedStep = TimeSpan.FromMilliseconds(SimulationFixedStepMilliseconds);

    #endregion

    #region Presentation Metadata

    private static readonly string[] LocalizedPropertyNames =
    [
        nameof(ModeText), nameof(ModeTransitionStatusText), nameof(StateText), nameof(SimulationEngineStateText), nameof(CurrentRunStatusText), nameof(SimulationObservationUnavailableText), nameof(RunStateCaptionText), nameof(RunTargetText), nameof(RunTargetSummaryText),
        nameof(LeftPanelHeaderText), nameof(RightPanelHeaderText), nameof(SequenceNavigationItems),
        nameof(ProjectStatusText), nameof(ProjectSaveStateText), nameof(ProjectRevisionText), nameof(SelectionStatusText), nameof(EquipmentSelectionStatusText), nameof(NewProjectNameValidationText),
        nameof(StationUnitValidationText), nameof(StationUnitRemovalHintText), nameof(StationUnitStationChoices), nameof(SelectedStationChoiceId),
        nameof(EquipmentComponentLibraryUnits), nameof(EquipmentComponentLibraryItems), nameof(EquipmentComponentLibraryUnitValidationText),
        nameof(SimulationStatusText), nameof(SimulationSecondsStatusText), nameof(TickStatusText), nameof(FixedStepStatusText),
        nameof(EquipmentVerdictStatusText), nameof(EquipmentTwoDStatusText), nameof(EquipmentThreeDStatusText), nameof(EquipmentAiStatusText), nameof(EquipmentExternalInspectionWaitText),
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
        nameof(CurrentCameraSourceModeText),
        nameof(CameraCommissioningHintText), nameof(VisionEvidenceStatusText),
        nameof(VisionEvidenceComparisonText), nameof(CurrentCameraEvidenceDetailsText),
        nameof(CurrentSequenceName), nameof(CurrentSequenceStateText), nameof(CurrentSequenceStepText),
        nameof(RecipeExecutionStatusText),
        nameof(AutomaticRunStateText),
        nameof(ConditionScenarioStateText), nameof(ConditionScenarioProgressText),
        nameof(ConditionScenarioHealthText),
        nameof(BatchStatusText), nameof(BatchResultText), nameof(BatchBaselineText),
        nameof(BatchArtifactStatusText), nameof(BatchAssertionOutcomes),
        nameof(UnifiedCommissioningEvidenceStatusText),
        nameof(SimulationCommandTraceStatusText),
        nameof(SelectedEquipmentStatus), nameof(ProcessPlanReviewPositionText)
    ];

    #endregion

    #region Dependencies

    private readonly ProjectLifecycleCoordinator _projectLifecycle;
    private readonly ProjectDiagnosticsViewModel _projectDiagnostics;
    private readonly SupportDiagnosticBundleViewModel _supportDiagnostics;
    private readonly SimulationSessionCoordinator _simulationSession;
    private readonly RuntimeObservabilityJournal _runtimeObservabilityJournal;
    private readonly RuntimeObservabilityPresenter _runtimeObservabilityPresenter;
    private readonly RuntimeDefinitionApplicationWorkflow _runtimeDefinitionApplicationWorkflow;
    private readonly ProjectRuntimeApplicationWorkflow _projectRuntimeApplicationWorkflow;
    private readonly EquipmentCommandDispatcher _equipmentCommandDispatcher;
    private readonly SimulationCommandPresentationDispatcher _simulationCommandPresentationDispatcher;
    private readonly SimulationRuntimeProjectionCoordinator _runtimeProjectionCoordinator;
    private readonly ProjectSelectionSynchronizationWorkflow _selectionSynchronization;
    private readonly MultiAxisCommissioningExecutionWorkflow _multiAxisCommissioningExecutionWorkflow;
    private readonly SimulationScenarioExecutionCoordinator _simulationScenarioExecutionCoordinator;
    private readonly ManualEquipmentCommissioningViewModel _manualEquipment;
    private readonly CameraCommissioningViewModel _camera;
    private readonly ShellNavigationViewModel _shellNavigation;
    private readonly LayoutAuthoringWorkspace _layoutAuthoring;
    private readonly RecipeAuthoringWorkspace _recipeAuthoring;
    private readonly SimulationCommandTraceViewModel _simulationCommandTrace;
    private readonly ProjectFileDialogHost _projectFileDialogHost = new();
    private readonly SimulationEvidenceFileDialogHost _simulationEvidenceFileDialogHost = new();
    private readonly MainMessageDialogHost _mainMessageDialogHost = new();
    private readonly MainWpfInteractionHost _mainWpfInteractionHost = new();
    private readonly MultiAxisCommissioningViewModel _multiAxisCommissioning;
    private SimulationScenarioBatchViewModel? _scenarioBatch;
    private readonly UnifiedCommissioningEvidenceViewModel _unifiedCommissioningEvidence;
    private readonly Action _disposeShellResourcesCallback;

    #endregion

    #region State

    private string _title = "OpenVisionLab Machine Studio";
    private string _statusMessage = "Ready";
    private bool _isRunning;
    private bool _isDesignMode = true;
    private bool _isModeTransitioning;
    private bool _pendingDesignMode;
    private int _modeTransitionGeneration;
    private bool _isApplyingProject;
    private bool _isApplyingHistoricalDraft;
    private bool _isResolvingPlacementDraft;
    private bool _runtimeDefinitionDirty;
    private bool _isManualControlEntryReady;
    private bool _sessionCloseRequested;
    private bool _disposed;
    private int _automaticExternalPublishInFlight;
    private bool _isStationUnitEditorOpen;
    private bool _isEquipmentComponentLibraryDialogOpen;
    private bool _isNewProjectNameDialogOpen;
    private bool _isNewProjectNameConfirming;
    private bool _newProjectNameValidationAttempted;
    private string _newProjectNameDraft = string.Empty;
    private string _equipmentComponentLibraryDialogStatusText = string.Empty;
    private string? _equipmentComponentLibraryUnitId;
    private bool _isCreatingNewStation;
    private string _stationNameDraft = string.Empty;
    private string _unitNameDraft = string.Empty;
    private string? _selectedStationId;
    private bool _isBulkMoveEditorOpen;
    private bool _isEquipmentSinglePartCheckOpen;
    private bool _isEquipmentDriveTabOpen;
    private string _bulkMoveXText = "0";
    private string _bulkMoveZText = "0";

    #endregion

    #region Command Backing Fields

    private ICommand? _newProjectCommand;
    private ICommand? _confirmNewProjectNameCommand;
    private ICommand? _cancelNewProjectNameCommand;
    private ICommand? _openProjectCommand;
    private ICommand? _navigateRuntimeErrorCommand;
    private ICommand? _showCameraEquipmentLocationCommand;
    private ICommand? _openEquipmentCameraSettingsCommand;
    private AsyncRelayCommand? _equipmentInspectorManualCommand;
    private bool _equipmentInspectorManualBusy;
    private string _inspectorTeachingName = string.Empty;
    private ICommand? _saveProjectCommand;
    private ICommand? _saveProjectAsCommand;
    private ICommand? _openStationUnitEditorCommand;
    private ICommand? _addStationUnitCommand;
    private ICommand? _cancelStationUnitEditorCommand;
    private ICommand? _removeEquipmentUnitCommand;
    private ICommand? _removeSelectedStationCommand;
    private ICommand? _openBulkMoveEditorCommand;
    private ICommand? _applyBulkMoveCommand;
    private ICommand? _cancelBulkMoveCommand;
    private ICommand? _openEquipmentPropertiesCommand;
    private ICommand? _openEquipmentDriveTabCommand;
    private ICommand? _openEquipmentSinglePartCheckCommand;
    private ICommand? _openEquipmentSequenceReferencesCommand;
    private ICommand? _openCameraAcquisitionStepCommand;
    private ICommand? _openExperimentCameraInputCommand;
    private ICommand? _openSimulationObservationStepCommand;
    private ICommand? _openStepAxisSettingsCommand;
    private ICommand? _addEquipmentLayoutComponentCommand;
    private ICommand? _openEquipmentComponentLibraryDialogCommand;
    private ICommand? _closeEquipmentComponentLibraryDialogCommand;
    private ICommand? _addEquipmentComponentFromLibraryCommand;
    private ICommand? _showEquipmentOverviewCommand;
    private ICommand? _showEquipmentUnitCommand;
    private ICommand? _showEquipmentStationCommand;
    private ICommand? _setSimulationSpeedCommand;
    private ICommand? _runCommand;
    private ICommand? _pauseCommand;
    private ICommand? _abortSequenceCommand;
    private ICommand? _retrySequenceCommand;
    private ICommand? _stepCommand;
    private ICommand? _resetCommand;
    private ICommand? _startTestScenarioCommand;
    private ICommand? _stopTestScenarioCommand;
    private ICommand? _replayTestScenarioCommand;
    private ICommand? _exportSimulationEvidenceCommand;
    private ICommand? _exportSimulationReportCommand;
    private ICommand? _importSimulationEvidenceCommand;
    private ICommand? _exportUnifiedCommissioningEvidenceCommand;
    private ICommand? _importUnifiedCommissioningEvidenceCommand;
    private ICommand? _exportSupportDiagnosticsCommand;
    private ICommand? _cycleStartCommand;
    private ICommand? _runMultiAxisCommissioningRecipeCommand;
    private ICommand? _stopMultiAxisCommissioningRecipeCommand;
    private ICommand? _exitCommand;

    #endregion

    #endregion

    #region Constructors

    public MainViewModel(
        MachineProjectDocument? initialProject = null,
        string? initialProjectPath = null,
        string? startupSamplePath = null,
        string? integrationSettingsPath = null,
        TimeSpan? automaticExternalInspectionWallTimeout = null,
        string? largeLayoutSamplePath = null)
    {
        OpenVisionLanguageService.Load();
        UnsavedProjectPrompt = _mainMessageDialogHost.ShowUnsavedProjectPrompt;
        PlacementDraftPrompt = _mainMessageDialogHost.ShowPlacementDraftPrompt;
        ProjectOpenFailurePresenter = _mainMessageDialogHost.ShowProjectOpenFailure;
        LayoutComponentRemovalPrompt = _mainMessageDialogHost.ConfirmLayoutComponentRemoval;
        EquipmentUnitRemovalPrompt = _mainMessageDialogHost.ConfirmEquipmentUnitRemoval;
        EquipmentUnitRemovalBlockedPresenter = _mainMessageDialogHost.ShowEquipmentUnitRemovalBlocked;
        EquipmentStationRemovalPrompt = _mainMessageDialogHost.ConfirmEquipmentStationRemoval;
        _simulationSession = new(
            SimulationFixedStep,
            automaticExternalInspectionWallTimeout);
        _runtimeObservabilityJournal = new(
            LogMessageRetentionLimit,
            OperationalDiagnosticRetentionLimit,
            getRuntimeCoordinates: () => (RuntimeProjection.SimulationTime, RuntimeProjection.TickIndex));
        _projectLifecycle = new(
            initialProject ?? new MachineProjectDocument { Name = "Untitled" },
            initialProjectPath,
            startupSamplePath,
            ApplyProjectAsync,
            OnProjectTransitionCompleted,
            OnProjectSaveCompleted,
            _mainWpfInteractionHost.CommitFocusedEditorAsync,
            project => SimulationWorkspace!.SaveProjectScenario(project.Simulation),
            path => _scenarioBatch!.PersistForProjectPath(path),
            path => _multiAxisCommissioning!.PersistForProjectPath(path),
            path => _camera!.PersistEvidenceForProjectPath(path),
            () => UnsavedProjectPrompt(),
            HandleProjectOpenFailure,
            HandleProjectSaveFailure,
            name => _projectFileDialogHost.SelectProjectSaveAs(name),
            fileName => _projectFileDialogHost.SelectRecipeCopyDestination(fileName),
            () => OpenVisionLanguageService.T("Gallery.TemplateOverwriteRejected"),
            largeLayoutSamplePath);
        var initialRuntime = BuildRuntimeConfiguration(CurrentProject, CurrentProjectPath);
        _shellNavigation = new(
            initialProject is null,
            () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsValidationBusy,
            () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsValidationBusy &&
                  _projectLifecycle.HasStartupSample,
            OpenBundledSampleAsync,
            OnBlankLayoutStarted,
            HandleCommandException,
            canOpenLargeLayoutSample: () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsValidationBusy &&
                                            _projectLifecycle.HasLargeLayoutSample,
            openLargeLayoutSampleAsync: OpenLargeLayoutSampleAsync,
            resolvePendingPlacementDraft: TryResolvePendingPlacementDraft,
            canOpenTestScenario: () => !_disposed && !_sessionCloseRequested && !_isApplyingProject
                && !IsNewProjectNameDialogOpen && !IsEquipmentComponentLibraryDialogOpen && SemiconductorRecipes is { IsOpen: false },
            canOpenRecipeConnections: () => !_disposed && !_sessionCloseRequested && !_isApplyingProject
                && !IsNewProjectNameDialogOpen && !IsEquipmentComponentLibraryDialogOpen && !IsStationUnitEditorOpen
                && !IsBulkMoveEditorOpen && SemiconductorRecipes is { IsOpen: false },
            canOpenManualCommissioning: () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && IsRunMode && !IsModeTransitioning);
        _runtimeDefinitionApplicationWorkflow = new(
            _simulationSession.Engine,
            SimulationFixedStep, () => CurrentProjectPath, BuildIdentity.Current);
        _projectRuntimeApplicationWorkflow = new(
            _runtimeDefinitionApplicationWorkflow,
            OnProjectRuntimeApplicationStateChanged,
            OnProjectRuntimeApplicationRejected,
            CompleteProjectRuntimeApplication);
        _equipmentCommandDispatcher = new(
            _simulationSession.Engine,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            _runtimeObservabilityJournal.Log);
        _simulationCommandPresentationDispatcher = new(
            _simulationSession.Engine,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            _runtimeObservabilityJournal.Log,
            () => !_disposed
                  && !_sessionCloseRequested
                  && !_isApplyingProject
                  && IsRunMode);
        _multiAxisCommissioningExecutionWorkflow = new(
            _simulationSession.Engine,
            _equipmentCommandDispatcher);
        AxisCommissioning = new AxisCommissioningViewModel(
            _equipmentCommandDispatcher.DispatchAxisCommandAsync,
            HandleCommandException);
        AxisCommissioning.PropertyChanged += OnAxisCommissioningInputChanged;

        ProjectTree = new ProjectTreeViewModel();
        ProjectTree.ResolvePendingPlacementDraft = TryResolvePendingPlacementDraft;
        Properties = new PropertiesViewModel();
        Layout = new MachineLayoutViewModel(TryResolvePendingPlacementDraft);
        _manualEquipment = new(
            _equipmentCommandDispatcher,
            CreateManualEquipmentProjection,
            () => Layout.SelectedItem?.Component?.Kind,
            () =>
            {
                ApplyDesignMode(false);
                IsRunning = true;
                if (Navigation.IsEquipmentWorkspace)
                {
                    IsEquipmentSinglePartCheckOpen = true;
                    IsEquipmentDriveTabOpen = false;
                    Navigation.IsInspectorOpen = true;
                }
                ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
            },
            HandleCommandException,
            PrepareSelectedManualControlAsync,
            () =>
            {
                IsRunning = false;
                ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
            });
        _manualEquipment.PropertyChanged += OnManualEquipmentPropertyChanged;
        SequenceEditor = new SequenceEditorViewModel();
        SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftPrompt();
        SequenceEditor.CanSelectOverviewSequence = () => CanNavigateSequenceUnits && Navigation.IsSequenceWorkspace;
        SequenceEditor.SelectOverviewSequence = sequence =>
        {
            SelectSequenceNavigationItem(SequenceNavigationItems.FirstOrDefault(item => item.SequenceId == sequence.Id)?.Key);
            return ReferenceEquals(SequenceEditor.SelectedSequence, sequence);
        };
        SequenceEditor.PropertyChanged += OnSequenceEditorPropertyChanged;
        _recipeAuthoring = new RecipeAuthoringWorkspace(
            Layout,
            SequenceEditor,
            () => CurrentProject,
            () => IsDesignMode,
            project => { BuildRuntimeConfiguration(project); },
            tabIndex =>
            {
                Navigation.SelectedDocumentTabIndex = tabIndex;
                if (Navigation.SelectedDocumentTabIndex == tabIndex) Navigation.CloseRecipeConnections();
            },
            () => MarkProjectChanged(),
            UpdateRunToolAvailability,
            () => RefreshDefinitionPresentation(null),
            () => _layoutAuthoring!.Reset(),
            RefreshVirtualCameraWorkflowPresentation,
            () => InvalidateCommands(),
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            _runtimeObservabilityJournal.Log,
            returnToConnections: () => Navigation.OpenRecipeConnectionsCommand.Execute(null));
        _recipeAuthoring.ProcessPlanReview.PropertyChanged += OnProcessPlanReviewPropertyChanged;
        _simulationCommandTrace = new SimulationCommandTraceViewModel(
            () => IsRunMode
                  && !_isApplyingProject
                  && !_sessionCloseRequested
                  && !IsValidationBusy
                  && !IsRunning
                  && !_runtimeDefinitionDirty,
            () => _simulationSession.Engine as FixedStepSimulationEngine,
            ApplyMonitorSnapshot,
            ResetUnifiedCommissioningEvidenceForTraceCapture,
            RaiseUnifiedCommissioningEvidencePresentationChanged,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            message => _runtimeObservabilityJournal.Log("Simulation", message),
            ExportSimulationCommandTraceWithDialog,
            ReplaySimulationCommandTraceWithDialogAsync,
            HandleCommandException);
        _simulationCommandTrace.PropertyChanged += OnSimulationCommandTracePropertyChanged;
        SimulationWorkspace = new SimulationWorkspaceViewModel();
        _simulationScenarioExecutionCoordinator = new(
            new SimulationScenarioWorkflow(command => _simulationSession.Engine.EnqueueCommandAsync(command)),
            SimulationWorkspace,
            () => CurrentProject,
            EnsureRuntimeDefinitionAppliedAsync,
            value =>
            {
                if (!_disposed)
                {
                    IsDesignMode = value;
                }
            },
            value =>
            {
                if (!_disposed)
                {
                    IsRunning = value;
                }
            },
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            _runtimeObservabilityJournal.Log);
        MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeEditorViewModel(
            OnMultiAxisCommissioningRecipeChanged);
        _multiAxisCommissioning = new MultiAxisCommissioningViewModel(
            MultiAxisCommissioningRecipe,
            () => IsRunMode
                  && !_isApplyingProject
                  && !_sessionCloseRequested
                  && !IsScenarioBatchRunning
                  && !IsRunning
                  && !_runtimeDefinitionDirty
                  && MultiAxisCommissioningRecipe.IsValid
                  && MultiAxisCommissioningRecipe.Targets.All(target =>
                      target.RuntimeState != AxisState.Moving),
            () => IsScenarioBatchRunning,
            () => CurrentProject,
            () => CurrentProjectPath,
            () => _projectLifecycle.SerializeForEvidence(),
            () => BuildRuntimeConfiguration(CurrentProject),
            SimulationFixedStep,
            _mainWpfInteractionHost.DispatchAsync,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            message => _runtimeObservabilityJournal.Log("Motion", message),
            NavigateToCommissioningMismatch,
            OnMultiAxisCommissioningPresentationChanged,
            HandleCommandException);
        _scenarioBatch = new SimulationScenarioBatchViewModel(
            SimulationWorkspace,
            () => IsRunMode
                  && !_isApplyingProject
                  && !_sessionCloseRequested
                  && !_multiAxisCommissioning.IsValidationRunning
                  && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
                  && SimulationWorkspace.IsScheduledFaultConfigurationValid
                  && SimulationWorkspace.IsAssertionConfigurationValid,
            () => IsRunMode
                  && !_isApplyingProject
                  && !_sessionCloseRequested
                  && !_multiAxisCommissioning.IsValidationRunning,
            () => IsRunMode
                  && !_isApplyingProject
                  && !_sessionCloseRequested
                  && !IsRunning
                  && !_multiAxisCommissioning.IsValidationRunning
                  && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
                  && SimulationWorkspace.IsScheduledFaultConfigurationValid
                  && SimulationWorkspace.IsAssertionConfigurationValid,
            () => _multiAxisCommissioning.IsValidationRunning,
            () => CurrentProject,
            () => CurrentProjectPath,
            EnsureRuntimeDefinitionAppliedAsync,
            () => IsRunning,
            PauseRuntimeForScenarioBatchAsync,
            project => SimulationWorkspace.SaveProjectScenario(project.Simulation),
            () => BuildRuntimeConfiguration(CurrentProject),
            () => _projectLifecycle.SerializeForEvidence(),
            SimulationFixedStep,
            _mainWpfInteractionHost.DispatchBatchProgressAsync,
            () => ConditionScenarioTargets,
            ResetUnifiedCommissioningEvidenceForTraceCapture,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            message => _runtimeObservabilityJournal.Log("Batch", message),
            NavigateToBatchMismatch,
            OnScenarioBatchPresentationChanged,
            HandleCommandException);
        _projectDiagnostics = new(
            new ProjectDocumentDiagnostics(),
            () => CurrentProject,
            () => CurrentProjectPath,
            () => HasUnsavedChanges,
            () => _isApplyingProject || IsValidationBusy || IsRunning || IsRunMode,
            preview => _projectLifecycle.ApplyProjectRecoveryAsync(preview),
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            });
        SceneSnapshots = new SceneSnapshotStore();
        _camera = new(
            () => CurrentProject,
            CreateCameraCommissioningProjection,
            CreateVisionEvidenceContext,
            () => new ManualCameraSessionIdentity(
                _projectLifecycle.SessionId,
                _projectLifecycle.Revision),
            CreateManualCameraTriggerRequestInput,
            () => _simulationSession.Engine.CurrentSnapshot,
            () => _simulationSession.RuntimeLoop.CancellationToken,
            _equipmentCommandDispatcher.DispatchCameraCommandAsync,
            ApplyMonitorSnapshot,
            () => ApplyMonitorSnapshot(SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot),
            _manualEquipment.StartManualCameraControlAsync,
            () =>
            {
                _layoutAuthoring!.Reset();
                MarkProjectChanged();
                SequenceEditor.RefreshAuthoringTargets();
            },
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            _runtimeObservabilityJournal.Log,
            RefreshIntegrationContext,
            OpenVisionLanguageService.T,
            HandleCommandException,
            canEditCapture: () => IsSceneEditable);
        _camera.PropertyChanged += OnCameraPropertyChanged;
        _unifiedCommissioningEvidence = new UnifiedCommissioningEvidenceViewModel(
            CanExportUnifiedCommissioningEvidenceCore,
            CanImportUnifiedCommissioningEvidenceCore,
            CreateSimulationEvidenceForUnifiedCommissioning,
            CreateCommandTraceForUnifiedCommissioning,
            GetCurrentUnifiedCommissioningVisionEvidence,
            CreateUnifiedCommissioningEvidenceContext,
            ApplyImportedUnifiedCommissioningArtifacts,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            message => _runtimeObservabilityJournal.Log("Batch", message),
            RaiseUnifiedCommissioningEvidencePresentationChanged);
        Integration = new MachineIntegrationViewModel(
            CaptureIntegrationRequestContext,
            () => BuildIdentity.IntegrationIdentity,
            () => CurrentProject.Id,
            integrationSettingsPath,
            _mainWpfInteractionHost.DispatchOnUiThreadAsync,
            HandleCommandException,
            () => _simulationSession.Engine.CurrentSnapshot,
            command => _simulationSession.Engine.EnqueueCommandAsync(command),
            () => _simulationSession.RunControl.AbortSequenceAsync(),
            () => _camera.SelectedVirtualCamera?.Camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D);
        Integration.PropertyChanged += OnIntegrationPropertyChanged;
        SemiconductorRecipes = new SemiconductorRecipeGalleryViewModel(
            CreateSemiconductorRecipeCopyAsync);
        DryRunPlayback.PropertyChanged += OnDryRunPlaybackPropertyChanged;
        DigitalIo = new DigitalIoCommissioningViewModel(
            _simulationCommandPresentationDispatcher.DispatchDigitalIoAsync);
        FaultManager = new FaultManagerViewModel(
            _simulationCommandPresentationDispatcher.DispatchFaultAsync);
        RuntimeDebugger = new RuntimeDebuggerViewModel(DispatchRuntimeDebuggerCommandAsync);
        RuntimeDebugger.Alarms.CollectionChanged += OnBottomAlertStateChanged;
        RecipeConnections.DryRun.PropertyChanged += OnBottomAlertStateChanged;
        _runtimeObservabilityPresenter = new(
            _runtimeObservabilityJournal,
            _camera.VisionEvidence,
            RuntimeDebugger);
        _supportDiagnostics = new(
            new SupportDiagnosticBundleBuilder(),
            CaptureSupportDiagnosticBundleRequest,
            () => _simulationEvidenceFileDialogHost.SelectSupportDiagnosticExport(ProjectDisplayName),
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            message => _runtimeObservabilityJournal.Log("SupportDiagnostics", message));
        LogMessages = _runtimeObservabilityPresenter.LogMessages;
        _layoutAuthoring = new LayoutAuthoringWorkspace(
            Layout,
            () => CurrentProject,
            ResolveLayoutAuthoringUnitId,
            () => IsSceneEditable,
            () => _isApplyingProject,
            () => !_disposed && !_sessionCloseRequested,
            () => MarkProjectChanged(),
            UpdateRunToolAvailability,
            RefreshDefinitionPresentation,
            () => InvalidateCommands(),
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            _runtimeObservabilityJournal.Log,
            OnLayoutDefinitionChanged,
            (components, impacts) => LayoutComponentRemovalPrompt(components, impacts),
            TryResolvePendingPlacementDraft,
            addedComponentId =>
            {
                if (!Navigation.IsRecipeConnectionsOpen) return;
                Navigation.SelectedDocumentTabIndex = 0;
                if (Navigation.SelectedDocumentTabIndex != 0) return;
                Layout.Select(addedComponentId);
                Navigation.CloseRecipeConnectionsCommand.Execute(null);
            });
        MaterialFlowSettings = new MaterialFlowSettingsViewModel(() => CurrentProject, ResolveLayoutAuthoringUnitId,
            () => IsSceneEditable && !_isApplyingProject && !_disposed,
            () => { MarkProjectChanged(); RefreshDefinitionPresentation(null); InvalidateCommands(); },
            change => _layoutAuthoring.TryApplyDefinitionChange(change));
        OpenMaterialFlowSetupCommand = CreateRelayCommand(_ =>
        {
            if (!IsSceneEditable || !TryResolvePendingPlacementDraft()) return;
            var unitId = IsEquipmentComponentLibraryDialogOpen ? EquipmentComponentLibraryUnitId : ResolveLayoutAuthoringUnitId();
            IsEquipmentComponentLibraryDialogOpen = false;
            if (unitId is not null) Layout.ShowUnit(unitId);
            MaterialFlowSettings.OpenCommand.Execute(null);
        }, _ => IsSceneEditable && Navigation.IsEquipmentWorkspace);
        RunHistory = new RunHistoryViewModel(new MachineRunRecordFileStore(GetRunHistoryDirectory()), () => CurrentProject,
            () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsValidationBusy && !IsRunning
                && !_simulationSession.IsShutdownRequested && !_simulationSession.Engine.CurrentSnapshot.AutomaticRun.IsActive
                && !_simulationSession.Engine.CurrentSnapshot.Sequences.Any(sequence => sequence.Status == SequenceExecutionStatus.Running),
            ApplyHistoricalRecipeDraftAsync,
            action => _mainWpfInteractionHost.DispatchOnUiThreadAsync(() => { action(); return Task.CompletedTask; }),
            record => Integration.CaptureRunInspections(record),
            message => { if (!_disposed) StatusMessage = message; _runtimeObservabilityJournal.Log("RunHistory", message); },
            openCameraSettings: OpenRunCaptureCameraSettings, canOpenCameraSettings: CanOpenRunCaptureCameraSettings);
        RunHistory.StartRecording(((ISimulationRunRecordSource)_simulationSession.Engine).RunRecordReader);
        RunHistory.LoadProject();
        _runtimeProjectionCoordinator = new(
            MultiAxisCommissioningRecipe,
            SimulationWorkspace,
            DigitalIo,
            FaultManager,
            RuntimeDebugger,
            _camera.VisionEvidence,
            value =>
            {
                if (!_disposed)
                {
                    IsRunning = value;
                }
            },
            snapshot => RefreshManualEquipmentProjection(snapshot),
            () =>
            {
                if (!_disposed)
                {
                    _camera.RefreshProjection();
                }
            });
        _selectionSynchronization = new(
            ProjectTree,
            Layout,
            Properties,
            RecipeConnections,
            SequenceEditor,
            _camera.Selection,
            () => CurrentProject,
            () => SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot,
            snapshot => _runtimeProjectionCoordinator.UpdateSelectedAxis(
                snapshot,
                CreateRuntimeProjectionSelection()),
            OnProjectTreeSelectionPresentationChanged,
            OnLayoutSelectionPresentationChanged,
            OnAxisDefinitionChanged,
            OnAnalogChannelDefinitionChanged,
            status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            }, canEditAxisDefinition: () => IsSceneEditable && !_disposed);
        _selectionSynchronization.PropertyChanged += OnSelectionSynchronizationPropertyChanged;
        Layout.PropertyChanged += OnEquipmentLibraryLayoutPropertyChanged;
        SimulationWorkspace.PropertyChanged += OnSimulationWorkspacePropertyChanged;
        SequenceEditor.DefinitionChanged += OnSequenceDefinitionChanged;

        _isApplyingProject = true;
        try
        {
            ApplyProjectPresentation(CurrentProject);
            RuntimeDebugger.LoadProject(CurrentProject, resetSession: true);
            _layoutAuthoring.Reset();
            _multiAxisCommissioning.Restore();
            _camera.RestoreEvidence();
            var initialSnapshot = _simulationSession.Engine.CurrentSnapshot;
            SceneSnapshots.Publish(initialSnapshot);
            ApplyMonitorSnapshot(initialSnapshot);
        }
        finally
        {
            _isApplyingProject = false;
        }
        if (initialProject is null)
        {
            SimulationWorkspace.SaveProjectScenario(CurrentProject.Simulation);
        }
        AcceptCurrentProjectAsSaved();
        _runtimeObservabilityJournal.Log("System", "Deterministic machine runtime ready · fixed step 5 ms");

        _disposeShellResourcesCallback = DisposeShellResources;
        _simulationSession.Start(new SimulationSessionStartup
        {
            ProjectId = CurrentProject.Id,
            InitialRuntime = initialRuntime,
            GetRunControlState = () => new SimulationRunControlState(
                _isApplyingProject,
                IsValidationBusy,
                IsRunMode,
                IsRunning,
                _runtimeDefinitionDirty,
                HasAutomaticRun,
                RuntimeProjection.AutomaticRun.IsConfigured,
                RuntimeProjection.AutomaticRun.IsActive,
                HasEmbeddedSequence,
                CurrentProject.Axes.Count > 0,
                HasAuthoredLayout,
                HasVirtualCamera,
                HasCycleStartInput,
                RuntimeProjection.CycleStartInput == true,
                FaultManager.HasActiveFaults,
                RuntimeProjection.ControlOwner,
                RuntimeProjection.CurrentSequence?.Status,
                ActiveSequenceId)
            {
                ResetRetrySequenceId = _simulationSession.Engine.CurrentSnapshot.ResetRetrySequenceId,
                AutomaticExternalInspectionEnabled = Integration.Setup.WaitForExternalResult,
                IsManualControlEntryPending = _manualEquipment.IsStartingManualControl,
                AutomaticExternalInspectionWaiting = Integration.Setup.WaitForExternalResult
                    && _simulationSession.Engine.CurrentSnapshot.AutomaticRun.IsActive
                    && _simulationSession.Engine.CurrentSnapshot.Cameras.Any(camera =>
                        camera.State == VirtualCameraState.AwaitingExternalResult)
            },
            EnsureRuntimeDefinitionApplied = EnsureRuntimeDefinitionAppliedAsync,
            PrepareAutomaticExternalInspection = PrepareAutomaticExternalInspectionAsync,
            SetDesignMode = value =>
            {
                if (!_disposed)
                {
                    IsDesignMode = value;
                }
            },
            SetRunning = value =>
            {
                if (!_disposed)
                {
                    IsRunning = value;
                }
            },
            ApplySnapshot = snapshot =>
            {
                if (!_disposed)
                {
                    ApplyMonitorSnapshot(snapshot);
                }
            },
            CancelVisionCapture = () =>
            {
                if (!_disposed)
                {
                    CancelManualCameraPreparation();
                }
            },
            SetStatus = status =>
            {
                if (!_disposed)
                {
                    StatusMessage = status;
                }
            },
            Log = (category, message) =>
            {
                if (!_disposed)
                {
                    _runtimeObservabilityJournal.Log(category, message);
                }
            },
            NotifyCommandsChanged = () =>
            {
                if (!_disposed)
                {
                    if (_isManualControlEntryReady) RefreshManualEquipmentProjection(invalidateCommands: false);
                    InvalidateCommands();
                }
            },
            Dispatch = action =>
            {
                if (_disposed && action != _disposeShellResourcesCallback)
                {
                    return Task.CompletedTask;
                }

                return _mainWpfInteractionHost.DispatchAsync(() =>
                {
                    if (!_disposed || action == _disposeShellResourcesCallback)
                    {
                        action();
                    }
                });
            },
            DispatchAfterDispose = action => _mainWpfInteractionHost.DispatchAsync(action),
            PublishSnapshot = snapshot =>
            {
                if (!_disposed)
                {
                    SceneSnapshots.Publish(snapshot);
                }
            },
            CanApplySnapshot = CanApplyRuntimeSnapshot,
            OnInitialRuntimeApplied = () =>
            {
                if (_disposed)
                {
                    return;
                }

                ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
                _scenarioBatch!.Restore();
            },
            OnInitialConfigurationRejected = detail =>
            {
                if (_disposed)
                {
                    return;
                }

                _runtimeObservabilityJournal.Log(
                    "Runtime",
                    $"Initial configuration rejected · {detail}");
                _supportDiagnostics.Refresh();
            },
            OnRuntimeEvent = runtimeEvent =>
            {
                if (_disposed)
                {
                    return;
                }

                _runtimeObservabilityPresenter.RecordRuntimeEventPresentation(runtimeEvent);
                _supportDiagnostics.Refresh();
                if (runtimeEvent.Code == "AutomaticExternalInspectionRequestReady")
                {
                    _ = PublishAutomaticExternalInspectionAsync();
                }
            },
            OnTerminated = termination =>
            {
                if (!_disposed)
                {
                    _runtimeObservabilityPresenter.RecordEngineTermination(termination);
                    _supportDiagnostics.Refresh();
                }
            },
            OnUnhandledException = HandleCommandException,
            OnCanonicalEvent = runtimeEvent => _runtimeObservabilityPresenter.RecordCanonicalRuntimeEvent(
                runtimeEvent,
                _simulationSession.Engine.CurrentSnapshot),
            OnCanonicalJournalCompleted = journal =>
            {
                if (!_disposed)
                {
                    _runtimeObservabilityPresenter.RecordCanonicalEventJournalCompleted(journal);
                    _supportDiagnostics.Refresh();
                }
            },
            Workspace = SimulationWorkspace,
            ScenarioBatch = _scenarioBatch,
            MultiAxisCommissioning = _multiAxisCommissioning,
            ObserveProjectSave = _projectLifecycle.ObserveSaveAsync,
            ObserveCameraAcquisition = _camera.ObserveAsync,
            ObserveScenarioBatch = _scenarioBatch.ObserveAsync,
            ObserveCommissioningValidation = _multiAxisCommissioning.ObserveAsync,
            ObserveIntegration = Integration.ObserveAsync,
            ResolveUnsavedChanges = async () =>
            {
                if (_disposed)
                {
                    return false;
                }

                var resolved = await TryResolveUnsavedChangesAsync();
                return !_disposed && resolved;
            },
            SetCloseAdmission = isRequested =>
            {
                if (_disposed && !isRequested)
                {
                    return;
                }

                SetSessionCloseAdmission(isRequested);
            },
            RecordShutdownDiagnostic = diagnostic =>
            {
                if (!_disposed)
                {
                    _runtimeObservabilityPresenter.RecordShutdownDiagnostic(diagnostic);
                }
            }
        });
        _isManualControlEntryReady = true;
        RefreshManualEquipmentProjection();
        _shellNavigation.PropertyChanged += OnShellNavigationPropertyChanged;
        _shellNavigation.LanguageChanged += OnLanguageChanged;
    }

    #endregion

    #region Properties

    private bool IsScenarioBatchRunning => _scenarioBatch?.IsBatchRunning == true;
    private bool IsValidationBusy => IsScenarioBatchRunning || _multiAxisCommissioning.IsValidationRunning;
    private SimulationRuntimeSnapshotProjection RuntimeProjection => _runtimeProjectionCoordinator.CurrentProjection;
    private MachineProjectDocument CurrentProject => _projectLifecycle.CurrentProject;
    private string ProjectDisplayName => _projectLifecycle.DisplayName;
    private string? ActiveSequenceId => CurrentProject.Simulation.AutomaticRun?.SequenceId
        ?? CurrentProject.Sequences.FirstOrDefault()?.Id;
    private VirtualAxisDefinition? CurrentAxisDefinition => RuntimeProjection.CurrentAxis is null
        ? null
        : CurrentProject.Axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, RuntimeProjection.CurrentAxis.Id, StringComparison.Ordinal));
    private DeviceDefinition? CurrentCameraDefinition =>
        _camera.GetSelectedDefinition(RuntimeProjection.CurrentCamera?.Id);

    internal Func<UnsavedProjectDecision> UnsavedProjectPrompt { get; set; }
    internal Func<PlacementDraftDecision> PlacementDraftPrompt { get; set; }
    internal Action<string> ProjectOpenFailurePresenter { get; set; }
    internal Func<IReadOnlyList<LayoutComponentDefinition>, IReadOnlyList<LayoutComponentRemovalImpact>, bool> LayoutComponentRemovalPrompt { get; set; }
    internal Func<MachineStationDefinition, MachineUnitDefinition, bool> EquipmentUnitRemovalPrompt { get; set; }
    internal Action<string> EquipmentUnitRemovalBlockedPresenter { get; set; }
    internal Func<MachineStationDefinition, bool> EquipmentStationRemovalPrompt { get; set; }
    internal string? CurrentProjectPath => _projectLifecycle.CurrentPath;
    internal bool IsSessionCloseRequested => _sessionCloseRequested;

    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(RecipeTitleText));
        }
    }

    public string RecipeTitleText => IsStartupChoiceVisible ? "Machine Studio" : ProjectDisplayName;
    public string RecipeIdentityText => IsStartupChoiceVisible ? string.Empty : $"{ProjectDisplayName} ({CurrentProject.Id})";

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
            OnPropertyChanged(nameof(SimulationSpeedText));
        OnPropertyChanged(nameof(SimulationEngineStateText));
        OnPropertyChanged(nameof(CurrentRunStatusText));
        OnPropertyChanged(nameof(RunStateCaptionText));
            OnPropertyChanged(nameof(HasRecipeExecutionStatus));
            OnPropertyChanged(nameof(RecipeExecutionStatusText));
            OnPropertyChanged(nameof(RunStatusText));
            NotifyManualCommissioningChanged(invalidateCommands: false);
            NotifyAxisCommissioningChanged(invalidateCommands: false);
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
            if (_disposed)
            {
                return;
            }

            if (_isManualControlEntryReady && _manualEquipment.IsStartingManualControl)
            {
                OnPropertyChanged(nameof(IsDesignMode));
                OnPropertyChanged(nameof(IsRunMode));
                return;
            }

            if (_isModeTransitioning)
            {
                _pendingDesignMode = value;
                OnPropertyChanged(nameof(ModeText));
                OnPropertyChanged(nameof(ModeTransitionStatusText));
                StatusMessage = GetModeTransitionStatusText();
                InvalidateCommands();
                return;
            }

            if (value == _isDesignMode)
            {
                return;
            }

            if (value && IsRunMode)
            {
                BeginDesignModeTransition();
                return;
            }

            ApplyDesignMode(value);
        }
    }

    public bool IsModeTransitioning => _isModeTransitioning;

    public bool IsRunMode
    {
        get => !_isDesignMode;
        set => IsDesignMode = !value;
    }

    public bool HasUnsavedChanges
        => _projectLifecycle.HasUnsavedChanges;

    public ShellNavigationViewModel Navigation => _shellNavigation;

    public bool IsCompactLayout
    {
        get => Navigation.IsCompactLayout;
        set => Navigation.IsCompactLayout = value;
    }

    public ProjectTreeViewModel ProjectTree { get; }
    public ProjectDiagnosticsViewModel ProjectDiagnostics => _projectDiagnostics;
    public SupportDiagnosticBundleViewModel SupportDiagnostics => _supportDiagnostics;
    public PropertiesViewModel Properties { get; }
    public AxisDriveTuningEditorViewModel? AxisDriveTuningEditor => _selectionSynchronization.AxisDriveTuningEditor;
    public AnalogIoAuthoringViewModel? AnalogIoAuthoring => _selectionSynchronization.AnalogIoAuthoring;

    public int SelectedDocumentTabIndex
    {
        get => Navigation.SelectedDocumentTabIndex;
        set => Navigation.SelectedDocumentTabIndex = value;
    }

    public int SelectedLeftToolTabIndex
    {
        get => Navigation.SelectedLeftToolTabIndex;
        set => Navigation.SelectedLeftToolTabIndex = value;
    }

    public bool IsStartupChoiceVisible => Navigation.IsStartupChoiceVisible;

    public bool IsNewProjectNameDialogOpen
    {
        get => _isNewProjectNameDialogOpen;
        private set
        {
            if (!SetProperty(ref _isNewProjectNameDialogOpen, value)) return;
            RaiseCanExecuteChanged(_newProjectCommand);
            RaiseCanExecuteChanged(_confirmNewProjectNameCommand);
            RaiseCanExecuteChanged(_cancelNewProjectNameCommand);
        }
    }

    public bool IsNewProjectNameEntryEnabled => !_isNewProjectNameConfirming;

    private bool IsNewProjectNameConfirming
    {
        get => _isNewProjectNameConfirming;
        set
        {
            if (!SetProperty(ref _isNewProjectNameConfirming, value)) return;
            OnPropertyChanged(nameof(IsNewProjectNameEntryEnabled));
            RaiseCanExecuteChanged(_confirmNewProjectNameCommand);
            RaiseCanExecuteChanged(_cancelNewProjectNameCommand);
        }
    }

    public string NewProjectNameDraft
    {
        get => _newProjectNameDraft;
        set
        {
            if (!SetProperty(ref _newProjectNameDraft, value ?? string.Empty)) return;
            if (_newProjectNameValidationAttempted)
            {
                OnPropertyChanged(nameof(NewProjectNameValidationText));
                OnPropertyChanged(nameof(HasNewProjectNameValidationError));
            }
        }
    }

    public bool HasNewProjectNameValidationError => _newProjectNameValidationAttempted
        && !ProjectLifecycleCoordinator.TryNormalizeNewProjectName(NewProjectNameDraft, out _);

    public string NewProjectNameValidationText => !HasNewProjectNameValidationError
        ? string.Empty
        : string.IsNullOrWhiteSpace(NewProjectNameDraft)
            ? OpenVisionLanguageService.T("Project.NewRecipeNameRequired")
            : OpenVisionLanguageService.T("Project.NewRecipeNameTooLong");

    public bool IsStationUnitEditorOpen
    {
        get => _isStationUnitEditorOpen;
        private set
        {
            if (!SetProperty(ref _isStationUnitEditorOpen, value)) return;
            OnPropertyChanged(nameof(StationUnitValidationText));
            OnPropertyChanged(nameof(StationUnitRemovalHintText));
            OnPropertyChanged(nameof(HasSelectedStationChildren));
            RaiseCanExecuteChanged(_addStationUnitCommand);
            RaiseCanExecuteChanged(_cancelStationUnitEditorCommand);
            RaiseCanExecuteChanged(_removeEquipmentUnitCommand);
            RaiseCanExecuteChanged(_removeSelectedStationCommand);
            RaiseCanExecuteChanged(_openStationUnitEditorCommand);
            RaiseCanExecuteChanged(_openBulkMoveEditorCommand);
            RaiseCanExecuteChanged(_openEquipmentCameraSettingsCommand);
        RaiseCanExecuteChanged(_openEquipmentPropertiesCommand);
            RaiseCanExecuteChanged(_openEquipmentDriveTabCommand);
            RaiseCanExecuteChanged(_openEquipmentSinglePartCheckCommand);
            RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
            RaiseCanExecuteChanged(_addEquipmentLayoutComponentCommand);
            RaiseCanExecuteChanged(_removeEquipmentUnitCommand);
        }
    }

    public bool IsEquipmentComponentLibraryDialogOpen
    {
        get => _isEquipmentComponentLibraryDialogOpen;
        private set
        {
            if (!SetProperty(ref _isEquipmentComponentLibraryDialogOpen, value)) return;
            if (!value)
            {
                EquipmentComponentLibraryDialogStatusText = string.Empty;
                EquipmentComponentLibraryUnitId = null;
            }
            OnPropertyChanged(nameof(EquipmentComponentLibraryUnitValidationText));
            RaiseCanExecuteChanged(_openEquipmentComponentLibraryDialogCommand);
            RaiseCanExecuteChanged(_closeEquipmentComponentLibraryDialogCommand);
            RaiseCanExecuteChanged(_addEquipmentComponentFromLibraryCommand);
        }
    }

    public string EquipmentComponentLibraryDialogStatusText
    {
        get => _equipmentComponentLibraryDialogStatusText;
        private set => SetProperty(ref _equipmentComponentLibraryDialogStatusText, value ?? string.Empty);
    }

    public IReadOnlyList<KeyValuePair<string, string>> EquipmentComponentLibraryUnits
    {
        get
        {
            var duplicateNames = ProjectStations.SelectMany(station => station.Units)
                .GroupBy(unit => unit.Name, StringComparer.CurrentCultureIgnoreCase).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            return ProjectStations.SelectMany(station => station.Units.Select(unit => new KeyValuePair<string, string>(unit.Id,
                duplicateNames.Contains(unit.Name) ? $"{station.Name} / {unit.Name} [{unit.Id}]" : unit.Name))).ToArray();
        }
    }

    public string? EquipmentComponentLibraryUnitId
    {
        get => _equipmentComponentLibraryUnitId;
        set
        {
            if (!SetProperty(ref _equipmentComponentLibraryUnitId, value)) return;
            EquipmentComponentLibraryDialogStatusText = string.Empty;
            OnPropertyChanged(nameof(EquipmentComponentLibraryUnitValidationText));
            RaiseCanExecuteChanged(_addEquipmentComponentFromLibraryCommand);
        }
    }

    public string EquipmentComponentLibraryUnitValidationText => IsEquipmentComponentLibraryDialogOpen
        && !EquipmentComponentLibraryUnits.Any(unit => unit.Key == EquipmentComponentLibraryUnitId)
            ? OpenVisionLanguageService.T("Equipment.LibraryTargetRequired") : string.Empty;

    public IReadOnlyList<ComponentLibraryItem> EquipmentComponentLibraryItems
    {
        get
        {
            var items = Layout.LibraryItems.Where(item => item.Kind != LayoutComponentKind.Camera).Concat(new[]
            {
                new ComponentLibraryItem(LayoutComponentKind.Camera, OpenVisionLanguageService.T("Equipment.LibraryCamera2D"),
                    OpenVisionLanguageService.T("Layout.Library.Category.Vision"), OpenVisionLanguageService.T("Equipment.LibraryCamera2DHint"), NumericalCameraInputKind.Image2D),
                new ComponentLibraryItem(LayoutComponentKind.Camera, OpenVisionLanguageService.T("Equipment.LibraryCamera3D"),
                    OpenVisionLanguageService.T("Layout.Library.Category.Vision"), OpenVisionLanguageService.T("Equipment.LibraryCamera3DHint"), NumericalCameraInputKind.HeightMap3D)
            });
            var search = Layout.LibrarySearchText.Trim();
            return items.Where(item => search.Length == 0 || (item.Name + " " + item.Description + " " + item.Category)
                .Contains(search, StringComparison.CurrentCultureIgnoreCase)).OrderBy(item => item.Kind switch
                {
                    LayoutComponentKind.Conveyor => 0,
                    LayoutComponentKind.FeedPosition => 0,
                    LayoutComponentKind.ExitPosition => 0,
                    LayoutComponentKind.DigitalSensor => 1,
                    LayoutComponentKind.LinearStage => 2,
                    LayoutComponentKind.PneumaticCylinder => 3,
                    LayoutComponentKind.Camera => 4,
                    LayoutComponentKind.DigitalIo => 5,
                    _ => 6
                }).ToArray();
        }
    }

    public bool EquipmentComponentLibraryHasNoMatches => EquipmentComponentLibraryItems.Count == 0;

    public bool IsBulkMoveEditorOpen
    {
        get => _isBulkMoveEditorOpen;
        private set
        {
            if (!SetProperty(ref _isBulkMoveEditorOpen, value)) return;
            RaiseCanExecuteChanged(_openBulkMoveEditorCommand);
            RaiseCanExecuteChanged(_applyBulkMoveCommand);
            RaiseCanExecuteChanged(_cancelBulkMoveCommand);
            RaiseCanExecuteChanged(_removeEquipmentUnitCommand);
            RaiseCanExecuteChanged(_removeSelectedStationCommand);
            RaiseCanExecuteChanged(_openStationUnitEditorCommand);
            RaiseCanExecuteChanged(_openEquipmentCameraSettingsCommand);
        RaiseCanExecuteChanged(_openEquipmentPropertiesCommand);
            RaiseCanExecuteChanged(_openEquipmentDriveTabCommand);
            RaiseCanExecuteChanged(_openEquipmentSinglePartCheckCommand);
            RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
            RaiseCanExecuteChanged(_addEquipmentLayoutComponentCommand);
        }
    }

    public bool IsEquipmentSinglePartCheckOpen
    {
        get => _isEquipmentSinglePartCheckOpen;
        private set
        {
            if (!SetProperty(ref _isEquipmentSinglePartCheckOpen, value)) return;
            OnPropertyChanged(nameof(RightPanelHeaderText));
        }
    }

    public bool IsEquipmentDriveTabOpen
    {
        get => _isEquipmentDriveTabOpen;
        private set => SetProperty(ref _isEquipmentDriveTabOpen, value);
    }

    public string BulkMoveXText
    {
        get => _bulkMoveXText;
        set
        {
            if (!SetProperty(ref _bulkMoveXText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(BulkMoveValidationText));
            RaiseCanExecuteChanged(_applyBulkMoveCommand);
        }
    }

    public string BulkMoveZText
    {
        get => _bulkMoveZText;
        set
        {
            if (!SetProperty(ref _bulkMoveZText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(BulkMoveValidationText));
            RaiseCanExecuteChanged(_applyBulkMoveCommand);
        }
    }

    public string BulkMoveTitleText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.BulkMoveTitle"),
        Layout.SelectionCount);

    public string BulkMoveValidationText => TryGetBulkMoveOffsets(out _, out _)
        ? string.Empty
        : OpenVisionLanguageService.T("Equipment.BulkMoveInvalid");

    public bool IsCreatingNewStation
    {
        get => _isCreatingNewStation;
        set
        {
            if (!SetProperty(ref _isCreatingNewStation, value)) return;
            OnPropertyChanged(nameof(SelectedStationChoiceId));
            OnPropertyChanged(nameof(StationUnitValidationText));
            OnPropertyChanged(nameof(StationUnitRemovalHintText));
            OnPropertyChanged(nameof(HasSelectedStationChildren));
            RaiseCanExecuteChanged(_addStationUnitCommand);
            RaiseCanExecuteChanged(_removeSelectedStationCommand);
        }
    }

    public string StationNameDraft
    {
        get => _stationNameDraft;
        set
        {
            if (!SetProperty(ref _stationNameDraft, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(StationUnitValidationText));
            RaiseCanExecuteChanged(_addStationUnitCommand);
        }
    }

    public string UnitNameDraft
    {
        get => _unitNameDraft;
        set
        {
            if (!SetProperty(ref _unitNameDraft, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(StationUnitValidationText));
            RaiseCanExecuteChanged(_addStationUnitCommand);
        }
    }

    public string? SelectedStationId
    {
        get => _selectedStationId;
        set
        {
            if (!SetProperty(ref _selectedStationId, value)) return;
            OnPropertyChanged(nameof(SelectedStationChoiceId));
            OnPropertyChanged(nameof(StationUnitValidationText));
            OnPropertyChanged(nameof(StationUnitRemovalHintText));
            OnPropertyChanged(nameof(HasSelectedStationChildren));
            RaiseCanExecuteChanged(_addStationUnitCommand);
            RaiseCanExecuteChanged(_removeSelectedStationCommand);
        }
    }

    public IReadOnlyList<SequenceUnitNavigationItem> SequenceNavigationItems
    {
        get
        {
            var units = CurrentProject.Stations.SelectMany(station => station.Units).ToArray();
            var items = units.Select(unit =>
            {
                var sequence = CurrentProject.Sequences.FirstOrDefault(candidate => candidate.UnitId == unit.Id);
                return new SequenceUnitNavigationItem($"unit:{unit.Id}", unit.Name, unit.Id, unit.Id, sequence?.Id, sequence?.Steps.Count ?? 0);
            });
            return items.Concat(CurrentProject.Sequences.Where(sequence => string.IsNullOrEmpty(sequence.UnitId) || !units.Any(unit => unit.Id == sequence.UnitId))
                .Select(sequence => new SequenceUnitNavigationItem($"sequence:{sequence.Id}", sequence.Name, sequence.Id, null, sequence.Id, sequence.Steps.Count))).ToArray();
        }
    }

    public bool CanNavigateSequenceUnits => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsModeTransitioning && !SequenceEditor.IsStepTestBusy;

    public string? SelectedSequenceNavigationKey
    {
        get => SequenceEditor.SelectedSequence is { } sequence
            ? SequenceNavigationItems.FirstOrDefault(item => item.SequenceId == sequence.Id)?.Key
            : Layout.ActiveUnitId is { } unitId ? $"unit:{unitId}" : null;
        set => SelectSequenceNavigationItem(value);
    }

    public IReadOnlyList<MachineStationDefinition> ProjectStations => CurrentProject.Stations;
    public IReadOnlyList<VirtualAxisDefinition> ProjectAxes => CurrentProject.Axes;
    public IReadOnlyList<DeviceDefinition> ProjectDevices => CurrentProject.Devices;

    public IReadOnlyList<KeyValuePair<string, string>> StationUnitStationChoices => ProjectStations
        .Select(station => new KeyValuePair<string, string>(station.Id, station.Name))
        .Append(new KeyValuePair<string, string>(string.Empty, OpenVisionLanguageService.T("Equipment.StationUnitNewStationOption")))
        .ToArray();

    public string? SelectedStationChoiceId
    {
        get => IsCreatingNewStation ? string.Empty : SelectedStationId;
        set
        {
            if (value is null) return;
            if (value.Length > 0) SelectedStationId = value;
            IsCreatingNewStation = value.Length == 0;
        }
    }

    public string StationUnitValidationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UnitNameDraft) || UnitNameDraft.Trim().Length > 80)
            {
                return OpenVisionLanguageService.T("Equipment.StationUnitUnitNameRequired");
            }

            if (IsCreatingNewStation)
            {
                return string.IsNullOrWhiteSpace(StationNameDraft) || StationNameDraft.Trim().Length > 80
                    ? OpenVisionLanguageService.T("Equipment.StationUnitStationNameRequired")
                    : string.Empty;
            }

            return ProjectStations.Any(station => string.Equals(station.Id, SelectedStationId, StringComparison.Ordinal))
                ? string.Empty
                : OpenVisionLanguageService.T("Equipment.StationUnitStationRequired");
        }
    }

    public string StationUnitRemovalHintText
    {
        get
        {
            var station = SelectedExistingStation;
            return station is { Units.Count: > 0 }
                ? string.Format(CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Equipment.StationRemovalRequiresEmpty"), station.Units.Count)
                : string.Empty;
        }
    }

    public bool HasSelectedStationChildren => SelectedExistingStation is { Units.Count: > 0 };

    private MachineStationDefinition? SelectedExistingStation => !IsCreatingNewStation
        ? ProjectStations.FirstOrDefault(station => string.Equals(station.Id, SelectedStationId, StringComparison.Ordinal))
        : null;

    private bool CanRemoveSelectedStation => IsStationUnitEditorOpen
        && IsSceneEditable
        && (Navigation.IsEquipmentWorkspace || Navigation.IsSimulationWorkspace)
        && !IsCreatingNewStation
        && !IsBulkMoveEditorOpen
        && !_isApplyingProject
        && !_sessionCloseRequested
        && !_disposed
        && SelectedExistingStation is { Units.Count: 0 };

    public bool HasProcessPlanReturnContext => _recipeAuthoring.ProcessPlanReview.HasReturnContext;
    public string? ProcessPlanReturnStepId => _recipeAuthoring.ProcessPlanReview.ReturnStepId;
    public string ProcessPlanReviewPositionText => _recipeAuthoring.ProcessPlanReview.ReviewPositionText;
    public bool HasSelectedAxisDefinition => AxisDriveTuningEditor is not null;
    public bool HasSelectedAnalogChannel => AnalogIoAuthoring is not null;
    public AxisCommissioningViewModel AxisCommissioning { get; }
    public MachineLayoutViewModel Layout { get; }
    public LayoutAuthoringWorkspace LayoutAuthoring => _layoutAuthoring;
    public RecipeAuthoringWorkspace RecipeAuthoring => _recipeAuthoring;
    public RecipeConnectionWorkbenchViewModel RecipeConnections => _recipeAuthoring.Connections;
    public SequenceEditorViewModel SequenceEditor { get; }
    public SimulationWorkspaceViewModel SimulationWorkspace { get; }
    public MultiAxisCommissioningRecipeEditorViewModel MultiAxisCommissioningRecipe { get; }
    public ICommand EquipmentInspectorManualCommand => _equipmentInspectorManualCommand ??= new AsyncRelayCommand(
        parameter => ExecuteEquipmentInspectorManualAsync(parameter as string ?? string.Empty),
        parameter => CanExecuteEquipmentInspectorManual(parameter as string ?? string.Empty), HandleCommandException);
    public string EquipmentInspectorPositionText => AxisCommissioning.InspectorPositionText;
    public string EquipmentInspectorProductsText => string.Join(", ", PresentationSnapshot.LayoutComponents
        .Where(component => component.Kind == LayoutComponentKind.Workpiece && component.IsWorkpiecePresent != false)
        .Select(component => $"{component.WorkpieceInstanceId ?? component.Id} X{component.X:0}")) is { Length: > 0 } products
        ? products : OpenVisionLanguageService.T("R24.None");
    public IReadOnlyList<string> InspectorTeachingNames => CurrentAxisDefinition?.TeachingPositions?.Keys.ToArray() ?? [];
    public string InspectorTeachingName
    {
        get => InspectorTeachingNames.Contains(_inspectorTeachingName) ? _inspectorTeachingName : InspectorTeachingNames.FirstOrDefault() ?? string.Empty;
        set { if (SetProperty(ref _inspectorTeachingName, value ?? string.Empty)) _equipmentInspectorManualCommand?.RaiseCanExecuteChanged(); }
    }
    public string EquipmentInspectorManualStatusText
    {
        get
        {
            var component = Layout.SelectedItem?.Component;
            var current = PresentationSnapshot.LayoutComponents.FirstOrDefault(item => item.Id == component?.Id);
            if (component?.Kind == LayoutComponentKind.PneumaticCylinder)
            {
                var cylinder = CurrentProject.Devices.FirstOrDefault(device => device.Id == component.BehaviorBindingId)?.Cylinder;
                return $"{(current?.MotionProgress ?? 0) * (cylinder?.Stroke ?? 0):0.0} mm · " +
                    OpenVisionLanguageService.T("R24.Extended") + " " + InspectorSignal(cylinder?.ExtendedSensorChannelId) + " · " +
                    OpenVisionLanguageService.T("R24.Retracted") + " " + InspectorSignal(cylinder?.RetractedSensorChannelId);
            }
            var channelId = component?.Kind switch
            {
                LayoutComponentKind.Conveyor => CurrentProject.Devices.FirstOrDefault(device => device.Id == component.BehaviorBindingId)?.Conveyor?.RunCommandChannelId,
                LayoutComponentKind.DigitalSensor => CurrentProject.Devices.FirstOrDefault(device => device.Id == component.BehaviorBindingId)?.Sensor?.OutputChannelId,
                LayoutComponentKind.DigitalIo => component.BehaviorBindingId,
                _ => null
            };
            return InspectorSignal(channelId);
        }
    }

    private string InspectorSignal(string? channelId) => PresentationSnapshot.Signals.FirstOrDefault(signal => signal.Id == channelId) is { } signal ? signal.Value ? "ON" : "OFF" : "—";

    private bool CanExecuteEquipmentInspectorManual(string action)
    {
        if (_disposed || _sessionCloseRequested || !_isManualControlEntryReady || _equipmentInspectorManualBusy || _isApplyingProject || IsValidationBusy
            || _isModeTransitioning || Layout.SelectionCount != 1 || !Navigation.IsEquipmentWorkspace
            || RuntimeProjection.AutomaticRun.IsActive || RuntimeProjection.CurrentSequence?.Status == SequenceExecutionStatus.Running
            || IsRunning && RuntimeProjection.ControlOwner != SimulationControlOwner.Manual) return false;
        var component = Layout.SelectedItem?.Component;
        return action switch
        {
            "home" => component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage,
            "target" => component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage && AxisCommissioning.IsAxisTargetPositionValid,
            "jog-" or "jog+" => component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage && AxisCommissioning.IsAxisRelativeDistanceValid,
            "teach" => component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage && InspectorTeachingName.Length > 0,
            "ext" or "ret" => component?.Kind == LayoutComponentKind.PneumaticCylinder && !ManualEquipment.IsCurrentCylinderInterlocked,
            "run" or "halt" => component?.Kind == LayoutComponentKind.Conveyor,
            "capture" => component?.Kind == LayoutComponentKind.Camera,
            "ioOn" or "ioOff" => component?.Kind == LayoutComponentKind.DigitalIo && CurrentProject.Channels.Any(channel => channel.Id == component.BehaviorBindingId && channel.Kind == ChannelKind.DigitalOutput),
            "placeAtSensor" => component?.Kind == LayoutComponentKind.DigitalSensor && HasInspectorSample,
            "placeWp" => HasInspectorSample && (component?.Kind == LayoutComponentKind.FeedPosition || component?.Kind == LayoutComponentKind.Conveyor && FindInspectorFeedPosition(component.Id) is not null),
            "clearWp" => HasInspectorSample,
            _ => false
        };
    }

    private bool HasInspectorSample => CurrentProject.Layouts.SelectMany(layout => layout.Components).Any(component => component.Kind == LayoutComponentKind.Workpiece);
    private LayoutComponentDefinition? FindInspectorFeedPosition(string conveyorId) => CurrentProject.Layouts.SelectMany(layout => layout.Components)
        .FirstOrDefault(component => component.Kind == LayoutComponentKind.FeedPosition && component.MaterialConveyorId == conveyorId);

    private async Task ExecuteEquipmentInspectorManualAsync(string action)
    {
        if (!CanExecuteEquipmentInspectorManual(action)) return;
        var requested = Layout.SelectedItem!.Component!;
        var project = CurrentProject;
        _equipmentInspectorManualBusy = true;
        _equipmentInspectorManualCommand?.RaiseCanExecuteChanged();
        try
        {
            if (!await ManualEquipment.PrepareInspectorActionAsync() || !ReferenceEquals(project, CurrentProject)
                || !ReferenceEquals(requested, Layout.SelectedItem?.Component)) return;
            switch (action)
            {
                case "home": case "target": case "jog-": case "jog+":
                    await AxisCommissioning.ExecuteInspectorMotionAsync(action);
                    break;
                case "teach":
                    await ManualEquipment.PauseInspectorControlAsync();
                    if (CurrentAxisDefinition is { } axis && AxisCommissioning.InspectorCurrentPosition is { } position && InspectorTeachingName.Length > 0)
                    {
                        axis.TeachingPositions ??= new Dictionary<string, double>(StringComparer.Ordinal);
                        axis.TeachingPositions[InspectorTeachingName] = position;
                        MarkProjectChanged();
                        Layout.SelectedComponentEditor?.DiscardSettingsDraft();
                    }
                    break;
                case "capture":
                    await Camera.TriggerFromInspectorAsync();
                    break;
                case "ioOn": case "ioOff":
                    await _equipmentCommandDispatcher.DispatchInspectorCommandAsync(new SetManualOutputCommand(requested.BehaviorBindingId!, action == "ioOn"), "R24.Output");
                    break;
                case "placeWp": case "placeAtSensor": case "clearWp":
                    var placement = requested.Kind == LayoutComponentKind.Conveyor ? FindInspectorFeedPosition(requested.Id) : requested;
                    if (placement is not null)
                        await _equipmentCommandDispatcher.DispatchInspectorCommandAsync(new SetManualSampleCommand(placement.Transform.X, action == "placeAtSensor" ? CurrentProject.Devices.FirstOrDefault(device => device.Id == placement.BehaviorBindingId)?.Sensor?.DetectionCenterY ?? placement.Transform.Y : placement.Transform.Y, action == "clearWp"), "R24.SampleProduct");
                    break;
                default:
                    await ManualEquipment.ExecuteInspectorActionAsync(action);
                    break;
            }
            ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
        }
        finally
        {
            _equipmentInspectorManualBusy = false;
            RefreshEquipmentInspectorManualPresentation();
        }
    }

    private void RefreshEquipmentInspectorManualPresentation()
    {
        OnPropertyChanged(nameof(EquipmentInspectorPositionText));
        OnPropertyChanged(nameof(EquipmentInspectorProductsText));
        OnPropertyChanged(nameof(EquipmentInspectorManualStatusText));
        OnPropertyChanged(nameof(InspectorTeachingNames));
        OnPropertyChanged(nameof(InspectorTeachingName));
        _equipmentInspectorManualCommand?.RaiseCanExecuteChanged();
    }

    public ManualEquipmentCommissioningViewModel ManualEquipment => _manualEquipment;
    public CameraCommissioningViewModel Camera => _camera;
    // Compatibility for existing callers; the Camera inspector binds directly to Camera.
    public CameraImageSourceEditorViewModel CameraImageSourceEditor => _camera.ImageSourceEditor;
    public MachineIntegrationViewModel Integration { get; }
    public SemiconductorRecipeGalleryViewModel SemiconductorRecipes { get; }
    public RecipeDryRunPlaybackViewModel DryRunPlayback => _recipeAuthoring.Playback;
    public SceneSnapshotStore SceneSnapshots { get; }
    public EquipmentMaterialFlowViewModel EquipmentMaterialFlow { get; } = new();
    public MaterialFlowSettingsViewModel MaterialFlowSettings { get; }
    public RunHistoryViewModel RunHistory { get; }
    public SceneSnapshotStore SceneSnapshotSource => DryRunPlayback.IsActive
        ? DryRunPlayback.PlaybackSnapshots
        : SceneSnapshots;
    private SimulationSnapshot PresentationSnapshot =>
        SceneSnapshotSource.Latest ?? SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot;
    public bool IsSceneEditable => IsDesignMode && !DryRunPlayback.IsActive && _manualEquipment?.IsManualControlAdmissionLocked != true;
    private bool CanAddStationUnit => IsStationUnitEditorOpen
        && IsSceneEditable
        && Navigation.IsEquipmentWorkspace
        && !IsBulkMoveEditorOpen
        && !_isApplyingProject
        && !_sessionCloseRequested
        && !_disposed
        && string.IsNullOrEmpty(StationUnitValidationText);
    public bool IsDryRunPlaybackActive => DryRunPlayback.IsActive;
    public bool AreSimulationObservationsCurrent => !_runtimeDefinitionDirty && !DryRunPlayback.IsActive;
    public string SimulationObservationUnavailableText => OpenVisionLanguageService.T(DryRunPlayback.IsActive ? "SimObservation.Playback" : "SimObservation.PendingDefinition");
    public string DryRunPlaybackTitleText => DryRunPlayback.TitleText;
    public string DryRunPlaybackDetailText => DryRunPlayback.DetailText;
    public bool HasDryRunPlaybackCheckpoint => DryRunPlayback.HasCheckpoint;
    public bool HasDryRunPlaybackMismatch => DryRunPlayback.HasMismatch;
    public string DryRunPlaybackCheckpointText => DryRunPlayback.CheckpointText;
    public bool HasDryRunPlaybackLoadLock => DryRunPlayback.HasLoadLock;
    public bool IsDryRunPlaybackLoadLockFault => DryRunPlayback.IsLoadLockFault;
    public string DryRunPlaybackLoadLockText => DryRunPlayback.LoadLockText;
    public bool HasDryRunPlaybackWaferHandler => DryRunPlayback.HasWaferHandler;
    public bool IsDryRunPlaybackWaferHandlerFault => DryRunPlayback.IsWaferHandlerFault;
    public string DryRunPlaybackWaferHandlerText => DryRunPlayback.WaferHandlerText;
    public bool HasDryRunPlaybackInspectionSorter => DryRunPlayback.HasInspectionSorter;
    public bool IsDryRunPlaybackInspectionSorterFault => DryRunPlayback.IsInspectionSorterFault;
    public string DryRunPlaybackInspectionSorterText => DryRunPlayback.InspectionSorterText;
    public bool HasDryRunPlaybackInspectionHandoff => DryRunPlayback.HasInspectionHandoff;
    public bool IsDryRunPlaybackInspectionHandoffFault => DryRunPlayback.IsInspectionHandoffFault;
    public string DryRunPlaybackInspectionHandoffText => DryRunPlayback.InspectionHandoffText;
    public bool HasDryRunPlaybackOhtHandoff => DryRunPlayback.HasOhtHandoff;
    public bool IsDryRunPlaybackOhtHandoffFault => DryRunPlayback.IsOhtHandoffFault;
    public string DryRunPlaybackOhtHandoffText => DryRunPlayback.OhtHandoffText;
    public bool HasDryRunPlaybackPrealigner => DryRunPlayback.HasPrealigner;
    public bool IsDryRunPlaybackPrealignerFault => DryRunPlayback.IsPrealignerFault;
    public string DryRunPlaybackPrealignerText => DryRunPlayback.PrealignerText;
    public DigitalIoCommissioningViewModel DigitalIo { get; }
    public FaultManagerViewModel FaultManager { get; }
    public RuntimeDebuggerViewModel RuntimeDebugger { get; }
    public ReadOnlyObservableCollection<string> LogMessages { get; }
    public IReadOnlyList<SimulationOperationalDiagnostic> OperationalDiagnostics =>
        _runtimeObservabilityPresenter.OperationalDiagnostics;
    public IReadOnlyList<OpenVisionLanguageOption> LanguageOptions => Navigation.LanguageOptions;

    public DeterministicConditionScenarioSnapshot ConditionScenario => RuntimeProjection.ConditionScenario;
    public IReadOnlyList<SimulationScenarioTargetOption> ConditionScenarioTargets
    {
        get
        {
            var snapshot = SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot;
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
                SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot,
                SimulationWorkspace.ScheduledFaultKind)
            .Select(target => new SimulationScenarioTargetOption(target.Id, target.Name))
            .ToArray();
    public IReadOnlyList<SimulationScenarioTargetOption> RecoverySequences =>
        (SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot).Sequences
            .Select(sequence => new SimulationScenarioTargetOption(
                sequence.SequenceId,
                sequence.SequenceId))
            .OrderBy(sequence => sequence.Id, StringComparer.Ordinal)
            .ToArray();
    public string ConditionScenarioStateText => !RuntimeProjection.ConditionScenario.IsConfigured
        ? OpenVisionLanguageService.T("Simulation.ScenarioNotConfigured")
        : OpenVisionLanguageService.T(
            $"Simulation.ConditionState.{RuntimeProjection.ConditionScenario.State}",
            RuntimeProjection.ConditionScenario.State.ToString(),
            RuntimeProjection.ConditionScenario.State.ToString());
    public string ConditionScenarioProgressText => !RuntimeProjection.ConditionScenario.IsConfigured
        ? OpenVisionLanguageService.T("Simulation.ScenarioNoProgress")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.ScenarioProgress"),
            RuntimeProjection.ConditionScenario.ExecutedTicks,
            RuntimeProjection.ConditionScenario.DurationTicks);
    public string ConditionScenarioHealthText => !RuntimeProjection.ConditionScenario.IsConfigured
        ? OpenVisionLanguageService.T("Simulation.ScenarioNoHealth")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.ScenarioHealth"),
            RuntimeProjection.ConditionScenario.HealthScore);
    public bool CanStartTestScenario => !_disposed
        && !_isModeTransitioning
        && IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !RuntimeProjection.ConditionScenario.IsActive
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanStopTestScenario => !_disposed
        && !_isModeTransitioning
        && IsRunMode
        && !IsValidationBusy
        && RuntimeProjection.ConditionScenario.IsActive;
    public bool CanReplayTestScenario => !_disposed
        && !_isModeTransitioning
        && IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanAbortSequence => _simulationSession.RunControl.CanAbortSequence();
    public bool CanRetrySequence => _simulationSession.RunControl.CanRetrySequence();
    public bool IsBatchRunning => _scenarioBatch?.IsBatchRunning == true;
    public bool IsBatchCancellationRequested => _scenarioBatch?.IsBatchCancellationRequested == true;
    public bool IsScenarioConfigurationEnabled =>
        _scenarioBatch?.IsScenarioConfigurationEnabled ?? !IsValidationBusy;
    public int BatchCompletedRuns => _scenarioBatch?.BatchCompletedRuns ?? 0;
    public bool CanRunScenarioBatch => _scenarioBatch?.CanRunScenarioBatch == true;
    public bool CanAcceptBatchBaseline => _scenarioBatch?.CanAcceptBatchBaseline == true;
    public bool CanClearBatchBaseline => _scenarioBatch?.CanClearBatchBaseline == true;
    public bool CanNavigateToBatchMismatch => _scenarioBatch?.CanNavigateToBatchMismatch == true;
    public bool CanExportSimulationEvidence => _scenarioBatch?.CanExportEvidence == true;
    public bool CanExportSimulationReport => _scenarioBatch?.CanExportReport == true;
    public bool CanImportSimulationEvidence => _scenarioBatch?.CanImportEvidence == true;
    public bool CanExportUnifiedCommissioningEvidence => _unifiedCommissioningEvidence.CanExport;
    public bool CanImportUnifiedCommissioningEvidence => _unifiedCommissioningEvidence.CanImport;
    public string UnifiedCommissioningEvidenceStatusText => _unifiedCommissioningEvidence.StatusText;
    public bool CanStartSimulationCommandTraceCapture => _simulationCommandTrace.CanStartCapture;
    public bool CanExportSimulationCommandTrace => _simulationCommandTrace.CanExportTrace;
    public bool CanReplaySimulationCommandTrace => _simulationCommandTrace.CanReplayTrace;
    public int SimulationCommandTraceEntryCount => _simulationCommandTrace.EntryCount;
    public string SimulationCommandTraceStatusText => _simulationCommandTrace.StatusText;
    internal bool LastSimulationCommandTraceReplaySucceeded => _simulationCommandTrace.LastReplaySucceeded;
    internal DeterministicUnifiedCommissioningEvidencePackage? LatestUnifiedCommissioningEvidence =>
        _unifiedCommissioningEvidence.LatestEvidence;
    public string BatchStatusText => _scenarioBatch?.BatchStatusText ?? string.Empty;
    public string BatchResultText => _scenarioBatch?.BatchResultText ?? string.Empty;
    public string BatchBaselineText => _scenarioBatch?.BatchBaselineText ?? string.Empty;
    public string BatchArtifactStatusText => _scenarioBatch?.BatchArtifactStatusText ?? string.Empty;
    public IReadOnlyList<ScenarioAssertionOutcomePresentation> BatchAssertionOutcomes =>
        _scenarioBatch?.BatchAssertionOutcomes ?? Array.Empty<ScenarioAssertionOutcomePresentation>();
    public bool HasBatchAssertionOutcomes => _scenarioBatch?.HasBatchAssertionOutcomes == true;
    internal DeterministicSimulationBatchResultPackage? LatestBatchResult => _scenarioBatch?.LatestBatchResult;
    internal bool HasAcceptedBatchBaseline => _scenarioBatch?.HasAcceptedBatchBaseline == true;
    internal bool BatchWasCanceled => _scenarioBatch?.BatchWasCanceled == true;
    internal bool HasRestoredBatchArtifacts => _scenarioBatch?.HasRestoredBatchArtifacts == true;
    internal bool RejectedStaleBatchArtifacts => _scenarioBatch?.RejectedStaleBatchArtifacts == true;

    public OpenVisionLanguageOption SelectedLanguageOption
    {
        get => Navigation.SelectedLanguageOption;
        set => Navigation.SelectedLanguageOption = value;
    }

    public bool HasSelectedEquipment => Layout.SelectedItem?.Component is not null;
    public bool HasSelectedAxisStage => Layout.SelectedItem?.Component?.Kind is
        LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage;
    public bool HasSelectedManualEquipment => _manualEquipment.HasSelectedManualEquipment;
    public EquipmentStatusPresentation? SelectedEquipmentStatus => Layout.SelectedItem is null
        ? null
        : EquipmentStatusPresentation.Create(
            Layout.SelectedItem,
            PresentationSnapshot,
            CurrentProject);

    public string ModeText => IsModeTransitioning
        ? ModeTransitionStatusText
        : IsDesignMode
        ? OpenVisionLanguageService.T("Shell.Design")
        : OpenVisionLanguageService.T("Shell.Run");
    public string ModeTransitionStatusText => _isModeTransitioning
        ? GetModeTransitionStatusText()
        : string.Empty;
    public string StateText => IsModeTransitioning ? ModeTransitionStatusText
        : IsRunning ? OpenVisionLanguageService.T("Shell.Running")
        : IsDesignMode ? OpenVisionLanguageService.T("Shell.Editing")
        : PresentationSnapshot.SequenceDebug.PauseReason switch
        {
            SequenceDebugPauseReason.SequenceCompleted => OpenVisionLanguageService.T("Equipment.State.Completed"),
            SequenceDebugPauseReason.SequenceFaulted => OpenVisionLanguageService.T("Equipment.State.Error"),
            SequenceDebugPauseReason.SequenceAborted => OpenVisionLanguageService.T("Equipment.State.Aborted"),
            SequenceDebugPauseReason.None when PresentationSnapshot.AutomaticRun.IsActive
                && PresentationSnapshot.Cameras.Any(camera => camera.State == VirtualCameraState.AwaitingExternalResult)
                => OpenVisionLanguageService.T("Equipment.State.AwaitingExternalResult"),
            _ => OpenVisionLanguageService.T(RuntimeDebugger.IsPausedForContinuation ? "Shell.Paused" : "Equipment.State.Ready")
        };
    public string SimulationEngineStateText => IsModeTransitioning ? ModeTransitionStatusText
        : IsDesignMode ? OpenVisionLanguageService.T("Shell.Editing")
        : IsRunning ? OpenVisionLanguageService.T("Shell.Running")
        : OpenVisionLanguageService.T(RuntimeProjection.ControlOwner == SimulationControlOwner.Definition && RuntimeProjection.TickIndex == 0
            ? "Equipment.State.Ready" : "Shell.Paused");
    public string CurrentRunStatusText => ((ISimulationRunRecordSource)_simulationSession.Engine).ActiveRunId is { } runId
        ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Bottom.Run"), runId, SimulationEngineStateText)
        : SimulationEngineStateText;
    public bool HasBottomReadinessAlert => RecipeConnections.DryRun.IsReadinessStale || RecipeConnections.DryRun.ReadinessPassed == false;
    public int BottomAlarmCount => RuntimeDebugger.Alarms.Count + (HasBottomReadinessAlert ? 1 : 0);
    public string RunStateCaptionText => IsDesignMode && !IsModeTransitioning
        ? OpenVisionLanguageService.T("R24.Editing") : SimulationEngineStateText;
    public string RunTargetSummaryText
    {
        get
        {
            var manual = (IsRunMode && !_runtimeDefinitionDirty && RuntimeProjection.ControlOwner == SimulationControlOwner.Manual)
                || !CurrentProject.Sequences.Any(sequence => sequence.Id == ActiveSequenceId);
            var scope = OpenVisionLanguageService.T(manual ? "R24.ManualRunScope" : "R24.WholeEquipment");
            var feed = !CurrentProject.MaterialFlow.Enabled ? OpenVisionLanguageService.T("R24.NoFeed")
                : CurrentProject.MaterialFlow.TotalFeedCount is { } count
                    ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.FeedCount"), count)
                    : OpenVisionLanguageService.T("R24.UnlimitedFeed");
            // Named R24 deviation conditions have no product configuration owner yet; fault-scenario IDs are a separate role.
            var condition = OpenVisionLanguageService.T("R24.ConditionNotSet");
            return string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.RunSummary"), scope, feed, condition);
        }
    }
    public string RunTargetText
    {
        get
        {
            if (IsRunMode && !_runtimeDefinitionDirty && RuntimeProjection.ControlOwner == SimulationControlOwner.Manual)
                return OpenVisionLanguageService.T("Shell.ManualRunTarget");

            var sequence = CurrentProject.Sequences.FirstOrDefault(candidate => candidate.Id == ActiveSequenceId);
            if (sequence is null) return OpenVisionLanguageService.T("Shell.ManualRunTarget");
            var target = $"{sequence.Name} / {sequence.Id}";
            var automatic = CurrentProject.Simulation.AutomaticRun;
            var repeat = automatic?.Repeat == true
                ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.RepeatRunTarget"), automatic.RepeatDelayMilliseconds)
                : OpenVisionLanguageService.T("Shell.SingleRunTarget");
            if (sequence.UnitId is not null)
            {
                var units = CurrentProject.Stations.SelectMany(station => station.Units)
                    .Where(unit => CurrentProject.Sequences.Any(candidate => candidate.UnitId == unit.Id)).ToArray();
                target = string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.UnitSequencesRunTarget"), units.Length);
                var counts = units.Select(unit => unit.RepeatCount).Distinct().ToArray();
                var unitRepeat = counts.Length == 1
                    ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.UnitRepeatRunTarget"), counts[0])
                    : OpenVisionLanguageService.T("Shell.MixedUnitRepeatRunTarget");
                repeat = CurrentProject.MaterialFlow.Enabled && CurrentProject.MaterialFlow.TotalFeedCount is { } total
                    ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("MaterialFlow.TotalSupplyRunTarget"), total)
                    : unitRepeat + " · " + repeat;
            }
            return string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.RunTarget"), target, repeat);
        }
    }
    public string LeftPanelHeaderText => OpenVisionLanguageService.T("Shell.Project");
    public string RightPanelHeaderText => IsEquipmentSinglePartCheckOpen
        ? OpenVisionLanguageService.T("Equipment.SinglePartCheck")
        : IsRunMode
        ? OpenVisionLanguageService.T("Shell.Runtime")
        : OpenVisionLanguageService.T("Shell.Properties");
    public string ProjectStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(HasUnsavedChanges
            ? "Shell.ProjectStatusUnsaved"
            : "Shell.ProjectStatus"),
        ProjectDisplayName);
    public bool HasSavedProjectFile => !string.IsNullOrWhiteSpace(CurrentProjectPath);
    public string ProjectSaveStateText => OpenVisionLanguageService.T(string.IsNullOrWhiteSpace(CurrentProjectPath)
        ? "Shell.SaveStateNotSaved"
        : HasUnsavedChanges ? "Shell.SaveStateAppliedUnsaved" : "Shell.SaveStateSaved");
    public string ProjectRevisionText => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.ProjectRevision"), _projectLifecycle.Revision);
    public string SimulationSpeedText => $"×{_simulationSession.Engine.CurrentSnapshot.TimeScale:0.##}";
    public string EquipmentVerdictStatusText
    {
        get
        {
            // Count completed exits from one live snapshot, never camera results or playback history.
            var exits = _simulationSession.Engine.CurrentSnapshot.UnitMaterialExits;
            int ok = exits.Count(exit => exit.Verdict == "OK");
            int ng = exits.Count(exit => exit.Verdict == "NG");
            return string.Create(CultureInfo.InvariantCulture, $"OK {ok} · NG {ng}");
        }
    }
    public bool IsEquipmentExternalInspectionWaiting
    {
        get
        {
            var snapshot = _simulationSession.Engine.CurrentSnapshot;
            // The active runtime owns the wait, even if a later setup draft changes its future mode.
            return snapshot.AutomaticRun.IsActive && snapshot.Cameras.Any(camera => camera.State == VirtualCameraState.AwaitingExternalResult);
        }
    }
    public string EquipmentExternalInspectionWaitText => OpenVisionLanguageService.T("R24.Status.ExternalWait", "대기 중 — 시뮬레이션 정지", "Waiting — simulation stopped");
    // Configured consumer identity and past receipts do not establish a live Studio connection.
    public string EquipmentTwoDStatusText => OpenVisionLanguageService.T("R24.Status.TwoDUnverified", "2D Studio: 미확인", "2D Studio: Unverified");
    public string EquipmentThreeDStatusText => OpenVisionLanguageService.T("R24.Status.ThreeDUnverified", "3D Studio: 미확인", "3D Studio: Unverified");
    public string EquipmentAiStatusText => OpenVisionLanguageService.T("R24.Status.AiDisconnected", "AI 생성: 미연결", "AI generation: Not connected");
    public string EquipmentFooterStatusText => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.FooterStatus"), _projectLifecycle.Revision, Layout.SelectionCount);
    public string EquipmentSelectionStatusText
    {
        get
        {
            string name = Layout.SelectedItem is { } item
                ? Layout.SelectionCount > 1
                    ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Status.SelectionMultiple", "{0}개 · {1}", "{0} items · {1}"), Layout.SelectionCount, item.Name)
                    : item.Kind == LayoutItemKind.Workpiece ? OpenVisionLanguageService.T("R24.Status.ProductModel", "제품 모델", "Product model") : item.Name
                : ProjectTree.SelectedNode is { } node ? node.DisplayName
                : Layout.ActiveUnitId is not null || Layout.ActiveStationId is not null ? Layout.SceneTitleText
                : OpenVisionLanguageService.T("R24.Status.NoSelection", "없음", "None");
            return string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.Status.Selection", "선택: {0}", "Selection: {0}"), name);
        }
    }
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
            ? Layout.ActiveUnitId is not null || Layout.ActiveStationId is not null
                ? Layout.EquipmentScopeContextText
                : OpenVisionLanguageService.T("Shell.SelectionNone")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Shell.SelectionStatus"),
                ProjectTree.SelectedNode.DisplayName);
    public string SimulationStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Shell.SimulationStatus"),
        RuntimeProjection.SimulationTime);
    public string SimulationSecondsStatusText => string.Format(CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("R24.Status.SimulationTime", "시뮬레이션 {0:F2} s", "Simulation {0:F2} s"), RuntimeProjection.SimulationTime.TotalSeconds);
    public string TickStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Shell.TickStatus"),
        RuntimeProjection.TickIndex);
    public string FixedStepStatusText => OpenVisionLanguageService.T("Shell.FixedStep");
    public string RunStatusText => StateText;
    public string AxisCountText => CurrentProject.Axes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string LayoutComponentCountText => CurrentProject.Layouts
        .SelectMany(layout => layout.Components)
        .Count()
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string CameraCountText => _camera.CameraCountText;
    public bool HasVirtualCamera => _camera.HasVirtualCamera;
    public IReadOnlyList<DeviceDefinition> VirtualCameras => _camera.VirtualCameras;
    public DeviceDefinition? SelectedVirtualCamera
    {
        get => _camera.SelectedVirtualCamera;
        set => _camera.SelectedCameraId = value?.Id;
    }
    public string? SelectedCameraId
    {
        get => _camera.SelectedCameraId;
        set => _camera.SelectedCameraId = value;
    }
    public IReadOnlyList<string> CurrentCameraRecipes => _camera.CurrentCameraRecipes;
    public string? SelectedCameraRecipe
    {
        get => _camera.SelectedCameraRecipe;
        set => _camera.SelectedCameraRecipe = value;
    }
    public bool HasEmbeddedSequence => CurrentProject.Sequences.Count > 0;
    public bool HasAutomaticRun => CurrentProject.Simulation.AutomaticRun is not null;
    public bool HasAuthoredLayout => CurrentProject.Layouts.Count > 0;
    public bool HasCycleStartInput => CurrentProject.Channels.Any(channel =>
        string.Equals(channel.Id, CycleStartInputId, StringComparison.Ordinal)
        && channel.Kind == global::OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput);
    public string ControlOwnerHelpText => HasAutomaticRun
        ? OpenVisionLanguageService.T("Shell.ControlOwnerAutomatic")
        : HasEmbeddedSequence
        ? OpenVisionLanguageService.T("Shell.ControlOwnerSequence")
        : OpenVisionLanguageService.T("Shell.ControlOwnerManual");
    public string ControlOwnerText => IsRunMode
        ? OpenVisionLanguageService.T(
            $"Shell.ControlOwnerLabel.{RuntimeProjection.ControlOwner}",
            RuntimeProjection.ControlOwner.ToString(),
            RuntimeProjection.ControlOwner.ToString())
        : OpenVisionLanguageService.T("Shell.Definition");
    public string SceneTitleText => string.IsNullOrWhiteSpace(ProjectDisplayName)
        ? "UNTITLED MACHINE"
        : ProjectDisplayName.ToUpperInvariant();
    public string SceneControlText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Shell.SceneControl"),
        ControlOwnerText);
    public string CurrentAxisName => AxisCommissioning.CurrentAxisName;
    public string CurrentAxisStateText => AxisCommissioning.CurrentAxisStateText;
    public string CurrentAxisPositionText => AxisCommissioning.CurrentAxisPositionText;
    public string CurrentAxisVelocityText => AxisCommissioning.CurrentAxisVelocityText;
    public string CurrentAxisHomeText => AxisCommissioning.CurrentAxisHomeText;
    public string CurrentAxisLimitsText => AxisCommissioning.CurrentAxisLimitsText;
    public string CurrentAxisFollowingErrorText => AxisCommissioning.CurrentAxisFollowingErrorText;
    public string CurrentAxisDriveTuningText => AxisCommissioning.CurrentAxisDriveTuningText;
    public bool IsCurrentAxisDriveAlarmActive => AxisCommissioning.IsCurrentAxisDriveAlarmActive;
    public string CurrentAxisDriveAlarmText => AxisCommissioning.CurrentAxisDriveAlarmText;
    public string CurrentAxisUnitText => AxisCommissioning.CurrentAxisUnitText;
    public string CurrentAxisVelocityUnitText => AxisCommissioning.CurrentAxisVelocityUnitText;
    public string AxisTargetPositionText
    {
        get => AxisCommissioning.AxisTargetPositionText;
        set => AxisCommissioning.AxisTargetPositionText = value;
    }
    public bool IsAxisTargetPositionValid => AxisCommissioning.IsAxisTargetPositionValid;
    public bool HasAxisTargetPositionError => AxisCommissioning.HasAxisTargetPositionError;
    public string AxisTargetPositionValidationText => AxisCommissioning.AxisTargetPositionValidationText;
    public string AxisRelativeDistanceText
    {
        get => AxisCommissioning.AxisRelativeDistanceText;
        set => AxisCommissioning.AxisRelativeDistanceText = value;
    }
    public bool IsAxisRelativeDistanceValid => AxisCommissioning.IsAxisRelativeDistanceValid;
    public bool HasAxisRelativeDistanceError => AxisCommissioning.HasAxisRelativeDistanceError;
    public string AxisRelativeDistanceValidationText => AxisCommissioning.AxisRelativeDistanceValidationText;
    public string AxisCommandVelocityText
    {
        get => AxisCommissioning.AxisCommandVelocityText;
        set => AxisCommissioning.AxisCommandVelocityText = value;
    }
    public bool IsAxisCommandVelocityValid => AxisCommissioning.IsAxisCommandVelocityValid;
    public bool HasAxisCommandVelocityError => AxisCommissioning.HasAxisCommandVelocityError;
    public string AxisCommandVelocityValidationText => AxisCommissioning.AxisCommandVelocityValidationText;
    public bool IsCurrentAxisInterlocked => AxisCommissioning.IsCurrentAxisInterlocked;
    public string CurrentAxisInterlockText => AxisCommissioning.CurrentAxisInterlockText;
    public string AxisCommissioningHintText => AxisCommissioning.AxisCommissioningHintText;
    public bool CanStartManualEquipmentControl => _manualEquipment.CanStartManualEquipmentControl;
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
        && !RuntimeProjection.ConditionScenario.IsActive
        && !RuntimeProjection.AutomaticRun.IsActive
        && RuntimeProjection.CurrentSequence?.Status != SequenceExecutionStatus.Running
        && RuntimeProjection.ControlOwner != SimulationControlOwner.EmbeddedSequence
        && MultiAxisCommissioningRecipe.Targets.All(target =>
            target.RuntimeState != AxisState.Moving);
    public bool CanStopMultiAxisCommissioningRecipe => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && RuntimeProjection.ControlOwner == SimulationControlOwner.Manual
        && MultiAxisCommissioningRecipe.Targets.Any(target =>
            target.RuntimeState == AxisState.Moving);
    public bool IsCommissioningValidationRunning => _multiAxisCommissioning.IsValidationRunning;
    public bool IsCommissioningValidationConfigurationEnabled =>
        _multiAxisCommissioning.IsValidationConfigurationEnabled;
    public bool CanValidateMultiAxisCommissioningRecipe => _multiAxisCommissioning.CanValidate;
    public string CommissioningValidationStatusText => _multiAxisCommissioning.ValidationStatusText;
    public string CommissioningValidationResultText => _multiAxisCommissioning.ValidationResultText;
    public string CommissioningEvidenceStatusText => _multiAxisCommissioning.EvidenceStatusText;
    public IReadOnlyList<DeterministicCommissioningResultHistoryEntry> CommissioningResultHistoryEntries =>
        _multiAxisCommissioning.ResultHistoryEntries;
    public DeterministicCommissioningResultHistoryEntry? SelectedCommissioningHistoryEntry
    {
        get => _multiAxisCommissioning.SelectedHistoryEntry;
        set => _multiAxisCommissioning.SelectedHistoryEntry = value;
    }
    public bool CanAcceptCommissioningBaseline => _multiAxisCommissioning.CanAcceptBaseline;
    public bool CanClearCommissioningBaseline => _multiAxisCommissioning.CanClearBaseline;
    public bool CanNavigateToCommissioningMismatch => _multiAxisCommissioning.CanNavigateToMismatch;
    public string CommissioningHistoryStatusText => _multiAxisCommissioning.HistoryStatusText;
    public string CommissioningBaselineStatusText => _multiAxisCommissioning.BaselineStatusText;
    internal DeterministicMultiAxisCommissioningResultPackage? LatestCommissioningResult =>
        _multiAxisCommissioning.LatestResult;
    internal DeterministicMultiAxisCommissioningBaseline? AcceptedCommissioningBaseline =>
        _multiAxisCommissioning.AcceptedBaseline;
    internal DeterministicMultiAxisCommissioningResultHistory CommissioningResultHistory =>
        _multiAxisCommissioning.ResultHistory;
    internal DeterministicCommissioningBaselineComparison? CommissioningBaselineComparison =>
        _multiAxisCommissioning.BaselineComparison;
    internal bool HasRestoredCommissioningResult =>
        _multiAxisCommissioning.HasRestoredResult;
    internal bool RejectedStaleCommissioningResult =>
        _multiAxisCommissioning.RejectedStaleResult;
    public bool CanJogAxis => AxisCommissioning.CanJogAxis;
    public bool CanMoveAxisAbsolute => AxisCommissioning.CanMoveAxisAbsolute;
    public bool CanMoveAxisRelative => AxisCommissioning.CanMoveAxisRelative;
    public bool CanMoveAxisVelocity => AxisCommissioning.CanMoveAxisVelocity;
    public bool HasSelectedDigitalSensor => _manualEquipment.HasSelectedDigitalSensor;
    public bool IsCurrentSensorFaulted => _manualEquipment.IsCurrentSensorFaulted;
    public bool IsCurrentSensorManuallyForced => _manualEquipment.IsCurrentSensorManuallyForced;
    public string CurrentSensorForceText => _manualEquipment.CurrentSensorForceText;
    public string SensorCommissioningHintText => _manualEquipment.SensorCommissioningHintText;
    public bool CanForceSensorOn => _manualEquipment.CanForceSensorOn;
    public bool CanForceSensorOff => _manualEquipment.CanForceSensorOff;
    public bool CanClearSensorForce => _manualEquipment.CanClearSensorForce;
    public bool HasSelectedPneumaticCylinder => _manualEquipment.HasSelectedPneumaticCylinder;
    public bool IsCurrentCylinderInterlocked => _manualEquipment.IsCurrentCylinderInterlocked;
    public string CurrentCylinderInterlockText => _manualEquipment.CurrentCylinderInterlockText;
    public string CylinderCommissioningHintText => _manualEquipment.CylinderCommissioningHintText;
    public bool CanExtendCylinder => _manualEquipment.CanExtendCylinder;
    public bool CanRetractCylinder => _manualEquipment.CanRetractCylinder;
    public bool HasSelectedConveyor => _manualEquipment.HasSelectedConveyor;
    public string ConveyorCommissioningHintText => _manualEquipment.ConveyorCommissioningHintText;
    public bool CanRunConveyorForward => _manualEquipment.CanRunConveyorForward;
    public bool CanRunConveyorReverse => _manualEquipment.CanRunConveyorReverse;
    public bool CanStopConveyor => _manualEquipment.CanStopConveyor;
    internal DigitalSignalSnapshot? CurrentSelectedSensorSignal =>
        _manualEquipment.CurrentSelectedSensorSignal;
    public string CurrentCameraName => _camera.CurrentCameraName;
    public string CurrentCameraStateText => _camera.CurrentCameraStateText;
    public string CurrentCameraResultText => _camera.CurrentCameraResultText;
    public string CurrentCameraFrameText => _camera.CurrentCameraFrameText;
    public string CurrentCameraExposureTicksText => _camera.CurrentCameraExposureTicksText;
    public string CurrentCameraTransferTicksText => _camera.CurrentCameraTransferTicksText;
    public string CurrentCameraSourceText => _camera.CurrentCameraSourceText;
    public string CurrentCameraSourceModeText => _camera.CurrentCameraSourceModeText;
    public string? CurrentCameraImagePath => _camera.CurrentCameraImagePath;
    public bool HasCurrentCameraImage => _camera.HasCurrentCameraImage;
    public string CurrentCameraFrameHashText => _camera.CurrentCameraFrameHashText;
    public string CurrentCameraInspectionIdText => _camera.CurrentCameraInspectionIdText;
    public string CurrentCameraInspectionMessageText => _camera.CurrentCameraInspectionMessageText;
    public string CurrentCameraInspectionMetricsText => _camera.CurrentCameraInspectionMetricsText;
    public string CurrentVisionEvidenceHashText => _camera.CurrentVisionEvidenceHashText;
    public string VisionEvidenceStatusText => _camera.VisionEvidenceStatusText;
    public string VisionEvidenceComparisonText => _camera.VisionEvidenceComparisonText;
    public string CurrentCameraEvidenceDetailsText => _camera.CurrentCameraEvidenceDetailsText;
    internal DeterministicVisionExecutionEvidencePackage? LatestVisionEvidence =>
        _camera.LatestVisionEvidence;
    internal DeterministicVisionExecutionComparison? VisionEvidenceComparison =>
        _camera.VisionEvidenceComparison;
    public string CameraCommissioningHintText => _camera.CameraCommissioningHintText;
    public bool CanStartManualCameraControl => _camera.CanStartManualCameraControl;
    public bool CanTriggerCamera => _camera.CanTriggerCamera;
    public string CurrentSequenceName => ResolveSequenceName(RuntimeProjection.CurrentSequence?.SequenceId);
    public string CurrentSequenceStateText => RuntimeProjection.CurrentSequence is null
        ? OpenVisionLanguageService.T("Shell.NotConfigured")
        : LocalizeRuntimeState(RuntimeProjection.CurrentSequence.Status.ToString());
    public bool HasRecipeExecutionStatus => IsRunMode && !IsModeTransitioning
        && RuntimeProjection.ControlOwner != SimulationControlOwner.Manual && RuntimeProjection.RecipeStatus is not null;
    public string RecipeExecutionStatusText => !HasRecipeExecutionStatus ? string.Empty
        : RuntimeProjection.RecipeStatus == SequenceExecutionStatus.Completed && RuntimeProjection.AutomaticRun.IsWaitingForRepeat
            ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.RecipeRepeatWaitingProgress"),
                RuntimeProjection.CompletedRecipeSequenceCount, RuntimeProjection.RecipeSequences.Count)
            : string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.RecipeExecutionProgress"),
                LocalizeRuntimeState(RuntimeProjection.RecipeStatus!.Value.ToString()),
                RuntimeProjection.CompletedRecipeSequenceCount, RuntimeProjection.RecipeSequences.Count);
    public string CurrentSequenceStepText => ResolveStepName(
        RuntimeProjection.CurrentSequence?.SequenceId,
        RuntimeProjection.CurrentSequence?.CurrentStepId);
    public string AutomaticRunStateText => !RuntimeProjection.AutomaticRun.IsConfigured
        ? OpenVisionLanguageService.T("Shell.AutomaticRunNotConfigured")
        : RuntimeProjection.AutomaticRun.IsWaitingForRepeat
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Shell.AutomaticRunWaiting"),
                RuntimeProjection.AutomaticRun.RemainingDelayTicks)
            : RuntimeProjection.AutomaticRun.IsActive
                ? OpenVisionLanguageService.T("Shell.AutomaticRunRunning")
                : OpenVisionLanguageService.T("Shell.AutomaticRunReady");
    public string CompletedCycleCountText => RuntimeProjection.AutomaticRun.CompletedCycleCount
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string CycleStartSignalText => FormatSignal(RuntimeProjection.CycleStartInput);
    public string CycleActiveSignalText => FormatSignal(RuntimeProjection.CycleActiveOutput);
    public string CycleDoneSignalText => FormatSignal(RuntimeProjection.CycleDoneOutput);

    #endregion

    #region Commands

    public ICommand StartBlankLayoutCommand => Navigation.StartBlankLayoutCommand;

    public ICommand OpenBundledSampleCommand => Navigation.OpenBundledSampleCommand;

    public ICommand OpenLargeLayoutSampleCommand => Navigation.OpenLargeLayoutSampleCommand;

    public ICommand NewProjectCommand => _newProjectCommand ??= CreateRelayCommand(
        _ => OpenNewProjectNameDialog(),
        _ => !_isApplyingProject && !IsValidationBusy && !IsNewProjectNameDialogOpen);

    public ICommand ConfirmNewProjectNameCommand => _confirmNewProjectNameCommand ??= CreateAsyncCommand(
        async _ => await ConfirmNewProjectNameAsync(),
        _ => IsNewProjectNameDialogOpen && !IsNewProjectNameConfirming && !_isApplyingProject && !IsValidationBusy);

    public ICommand CancelNewProjectNameCommand => _cancelNewProjectNameCommand ??= CreateRelayCommand(
        _ => { if (!IsNewProjectNameConfirming) CloseNewProjectNameDialog(); },
        _ => IsNewProjectNameDialogOpen && !IsNewProjectNameConfirming);

    public ICommand OpenProjectCommand => _openProjectCommand ??= CreateAsyncCommand(async _ =>
    {
        if (_projectFileDialogHost.SelectProjectToOpen() is not { } path)
        {
            return;
        }

        await OpenProjectReplacingCurrentAsync(path);
    }, _ => !_isApplyingProject && !IsValidationBusy);

    public ICommand SaveProjectCommand => _saveProjectCommand ??= CreateAsyncCommand(async _ =>
    {
        await TrySaveCurrentProjectAsync();
    }, _ => !IsStartupChoiceVisible && !_isApplyingProject && !IsValidationBusy && !string.IsNullOrWhiteSpace(CurrentProject.Name));

    public ICommand SaveProjectAsCommand => _saveProjectAsCommand ??= CreateAsyncCommand(
        async _ => await TrySaveCurrentProjectAsync(saveAs: true),
        _ => !IsStartupChoiceVisible && !_isApplyingProject && !IsValidationBusy && !string.IsNullOrWhiteSpace(CurrentProject.Name));

    public ICommand SetSimulationSpeedCommand => _setSimulationSpeedCommand ??= CreateAsyncCommand(
        async parameter =>
        {
            if (double.TryParse(Convert.ToString(parameter, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                && speed is 1 or 2 or 4 or 8) await _simulationSession.RunControl.SetTimeScaleAsync(speed);
        },
        _ => !IsStartupChoiceVisible && !_isModeTransitioning && _simulationSession.RunControl.CanSetTimeScale());

    public ICommand RunCommand => _runCommand ??= CreateAsyncCommand(
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.RunAsync();
        },
        _ => !IsStartupChoiceVisible && !_isModeTransitioning && _simulationSession.RunControl.CanRun());

    public ICommand PauseCommand => _pauseCommand ??= CreateAsyncCommand(
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.PauseAsync();
        },
        _ => !_isModeTransitioning && _simulationSession.RunControl.CanPause());

    public ICommand StopCommand => PauseCommand;

    public ICommand AbortSequenceCommand => _abortSequenceCommand ??= CreateAsyncCommand(
        async _ => await _simulationSession.RunControl.AbortSequenceAsync(),
        _ => _simulationSession.RunControl.CanAbortSequence());

    public ICommand RetrySequenceCommand => _retrySequenceCommand ??= CreateAsyncCommand(
        async _ => await _simulationSession.RunControl.RetrySequenceAsync(),
        _ => _simulationSession.RunControl.CanRetrySequence());

    public ICommand StepCommand => _stepCommand ??= CreateAsyncCommand(
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.StepAsync();
        },
        _ => !IsStartupChoiceVisible && _simulationSession.RunControl.CanStep());

    public ICommand ResetCommand => _resetCommand ??= CreateAsyncCommand(
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.ResetAsync();
        },
        _ => !IsStartupChoiceVisible && _simulationSession.RunControl.CanReset());

    public ICommand StartTestScenarioCommand => _startTestScenarioCommand ??= CreateAsyncCommand(
        async _ => await _simulationScenarioExecutionCoordinator.StartAsync(),
        _ => CanStartTestScenario);

    public ICommand StopTestScenarioCommand => _stopTestScenarioCommand ??= CreateAsyncCommand(
        async _ => await _simulationScenarioExecutionCoordinator.StopAsync(),
        _ => CanStopTestScenario);

    public ICommand ReplayTestScenarioCommand => _replayTestScenarioCommand ??= CreateAsyncCommand(
        async _ => await _simulationScenarioExecutionCoordinator.ReplayAsync(),
        _ => CanReplayTestScenario);

    public ICommand RunScenarioBatchCommand => _scenarioBatch!.RunCommand;

    public ICommand CancelScenarioBatchCommand => _scenarioBatch!.CancelCommand;

    public ICommand AcceptBatchBaselineCommand => _scenarioBatch!.AcceptBaselineCommand;

    public ICommand ClearBatchBaselineCommand => _scenarioBatch!.ClearBaselineCommand;

    public ICommand NavigateToBatchMismatchCommand => _scenarioBatch!.NavigateToMismatchCommand;

    public ICommand ExportSimulationEvidenceCommand => _exportSimulationEvidenceCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is string path && !string.IsNullOrWhiteSpace(path))
            {
                TryExportSimulationEvidence(path);
            }
            else
            {
                ExportSimulationEvidenceWithDialog();
            }
        },
        _ => CanExportSimulationEvidence);

    public ICommand ExportSimulationReportCommand => _exportSimulationReportCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is string path && !string.IsNullOrWhiteSpace(path))
            {
                TryExportSimulationReport(path);
            }
            else
            {
                ExportSimulationReportWithDialog();
            }
        },
        _ => CanExportSimulationReport);

    public ICommand ImportSimulationEvidenceCommand => _importSimulationEvidenceCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is string path && !string.IsNullOrWhiteSpace(path))
            {
                TryImportSimulationEvidence(path);
            }
            else
            {
                ImportSimulationEvidenceWithDialog();
            }
        },
        _ => CanImportSimulationEvidence);

    public ICommand ExportUnifiedCommissioningEvidenceCommand =>
        _exportUnifiedCommissioningEvidenceCommand ??= CreateRelayCommand(
            parameter =>
            {
                if (parameter is string path && !string.IsNullOrWhiteSpace(path))
                {
                    TryExportUnifiedCommissioningEvidence(path);
                }
                else
                {
                    ExportUnifiedCommissioningEvidenceWithDialog();
                }
            },
            _ => CanExportUnifiedCommissioningEvidence);

    public ICommand ImportUnifiedCommissioningEvidenceCommand =>
        _importUnifiedCommissioningEvidenceCommand ??= CreateRelayCommand(
            parameter =>
            {
                if (parameter is string path && !string.IsNullOrWhiteSpace(path))
                {
                    TryImportUnifiedCommissioningEvidence(path);
                }
                else
                {
                    ImportUnifiedCommissioningEvidenceWithDialog();
                }
            },
            _ => CanImportUnifiedCommissioningEvidence);

    public ICommand ExportSupportDiagnosticsCommand =>
        _exportSupportDiagnosticsCommand ??= _supportDiagnostics.ExportCommand;

    public ICommand StartSimulationCommandTraceCaptureCommand =>
        _simulationCommandTrace.StartCaptureCommand;

    public ICommand ExportSimulationCommandTraceCommand =>
        _simulationCommandTrace.ExportCommand;

    public ICommand ReplaySimulationCommandTraceCommand =>
        _simulationCommandTrace.ReplayCommand;

    public ICommand CycleStartCommand => _cycleStartCommand ??= CreateAsyncCommand(
        async _ => await _simulationSession.RunControl.CycleStartAsync(),
        _ => _simulationSession.RunControl.CanCycleStart());

    public ICommand StartManualEquipmentControlCommand =>
        _manualEquipment.StartManualEquipmentControlCommand;

    public ICommand StartManualCameraControlCommand => _camera.StartManualCameraControlCommand;

    public ICommand TriggerCameraCommand => _camera.TriggerCameraCommand;

    public ICommand OpenEquipmentCameraSettingsCommand => _openEquipmentCameraSettingsCommand ??= CreateRelayCommand(_ =>
    {
        if (!TryResolvePendingPlacementDraft()) return;
        Navigation.IsInspectionWorkspace = true;
        Navigation.SelectedInspectionSettingsTabIndex = 0;
        Navigation.IsInspectorOpen = true;
    }, _ => Navigation.IsEquipmentWorkspace && Layout.SelectedItem?.Component?.Kind == LayoutComponentKind.Camera);

    public ICommand ShowCameraEquipmentLocationCommand => _showCameraEquipmentLocationCommand ??= CreateRelayCommand(_ => ShowCameraEquipmentLocation(),
        _ => !_disposed && !IsModeTransitioning && Layout.Items.Any(item => item.Component?.Kind == LayoutComponentKind.Camera && item.BehaviorBindingId == _camera.SelectedCameraId));

    public ICommand MoveAxisAbsoluteCommand => AxisCommissioning.MoveAxisAbsoluteCommand;
    public ICommand MoveAxisRelativeCommand => AxisCommissioning.MoveAxisRelativeCommand;
    public ICommand MoveAxisVelocityCommand => AxisCommissioning.MoveAxisVelocityCommand;
    public ICommand BeginAxisJogNegativeCommand => AxisCommissioning.BeginAxisJogNegativeCommand;
    public ICommand BeginAxisJogPositiveCommand => AxisCommissioning.BeginAxisJogPositiveCommand;
    public ICommand EndAxisJogCommand => AxisCommissioning.EndAxisJogCommand;
    public ICommand HomeAxisCommand => AxisCommissioning.HomeAxisCommand;
    public ICommand StopAxisMotionCommand => AxisCommissioning.StopAxisMotionCommand;

    public ICommand RunMultiAxisCommissioningRecipeCommand =>
        _runMultiAxisCommissioningRecipeCommand ??= CreateAsyncCommand(
            async _ => await RunMultiAxisCommissioningRecipeAsync(),
            _ => CanRunMultiAxisCommissioningRecipe);

    public ICommand StopMultiAxisCommissioningRecipeCommand =>
        _stopMultiAxisCommissioningRecipeCommand ??= CreateAsyncCommand(
            async _ => await StopMultiAxisCommissioningRecipeAsync(),
            _ => CanStopMultiAxisCommissioningRecipe);

    public ICommand ValidateMultiAxisCommissioningRecipeCommand =>
        _multiAxisCommissioning.ValidateCommand;

    public ICommand AcceptCommissioningBaselineCommand =>
        _multiAxisCommissioning.AcceptBaselineCommand;

    public ICommand ClearCommissioningBaselineCommand =>
        _multiAxisCommissioning.ClearBaselineCommand;

    public ICommand NavigateToCommissioningMismatchCommand =>
        _multiAxisCommissioning.NavigateToMismatchCommand;

    public ICommand NavigateRuntimeErrorCommand => _navigateRuntimeErrorCommand ??= CreateRelayCommand(NavigateRuntimeError, CanNavigateRuntimeError);

    public ICommand ForceSensorOnCommand => _manualEquipment.ForceSensorOnCommand;

    public ICommand ForceSensorOffCommand => _manualEquipment.ForceSensorOffCommand;

    public ICommand ClearSensorForceCommand => _manualEquipment.ClearSensorForceCommand;

    public ICommand ExtendCylinderCommand => _manualEquipment.ExtendCylinderCommand;

    public ICommand RetractCylinderCommand => _manualEquipment.RetractCylinderCommand;

    public ICommand RunConveyorForwardCommand => _manualEquipment.RunConveyorForwardCommand;

    public ICommand RunConveyorReverseCommand => _manualEquipment.RunConveyorReverseCommand;

    public ICommand StopConveyorCommand => _manualEquipment.StopConveyorCommand;

    public ICommand AddLayoutComponentCommand => _layoutAuthoring.AddLayoutComponentCommand;

    public ICommand AddEquipmentLayoutComponentCommand => _addEquipmentLayoutComponentCommand ??= CreateRelayCommand(
        AddEquipmentLayoutComponent,
        _ => IsSceneEditable && Navigation.IsEquipmentWorkspace && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen);

    public ICommand OpenEquipmentComponentLibraryDialogCommand => _openEquipmentComponentLibraryDialogCommand ??= CreateRelayCommand(
        _ => OpenEquipmentComponentLibraryDialog(),
        _ => IsSceneEditable && Navigation.IsEquipmentWorkspace && !IsEquipmentComponentLibraryDialogOpen
            && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen);

    public ICommand CloseEquipmentComponentLibraryDialogCommand => _closeEquipmentComponentLibraryDialogCommand ??= CreateRelayCommand(
        _ => IsEquipmentComponentLibraryDialogOpen = false,
        _ => IsEquipmentComponentLibraryDialogOpen);

    public ICommand OpenMaterialFlowSetupCommand { get; private set; } = null!;

    public ICommand AddEquipmentComponentFromLibraryCommand => _addEquipmentComponentFromLibraryCommand ??= CreateRelayCommand(
        AddEquipmentComponentFromLibrary,
        parameter => CanAddEquipmentComponentFromLibrary(parameter, out _));

    public ICommand ShowEquipmentOverviewCommand => _showEquipmentOverviewCommand ??= CreateRelayCommand(
        _ =>
        {
            if (!TryResolvePendingPlacementDraft()) return;
            _selectionSynchronization.ClearSelection();
            Layout.ShowOverview();
            Properties.Show(CurrentProject);
            Navigation.IsInspectorOpen = true;
            IsBulkMoveEditorOpen = false;
            OnPropertyChanged(nameof(SelectionStatusText));
            OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        });

    public ICommand ShowEquipmentStationCommand => _showEquipmentStationCommand ??= CreateRelayCommand(
        parameter =>
        {
            var station = CurrentProject.Stations.FirstOrDefault(candidate => candidate.Id == parameter as string);
            if (station is null || !TryResolvePendingPlacementDraft()) return;
            _selectionSynchronization.ClearSelection();
            Layout.ShowStation(station.Id);
            Properties.Show(station);
            Navigation.IsInspectorOpen = true;
            IsBulkMoveEditorOpen = false;
            OnPropertyChanged(nameof(SelectionStatusText));
            OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        });

    public ICommand ShowEquipmentUnitCommand => _showEquipmentUnitCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is not string unitId || string.IsNullOrWhiteSpace(unitId))
            {
                return;
            }

            var unit = CurrentProject.Stations.SelectMany(station => station.Units).FirstOrDefault(candidate => candidate.Id == unitId);
            if (unit is null || !TryResolvePendingPlacementDraft()) return;
            _selectionSynchronization.ClearSelection();
            Layout.ShowUnit(unitId);
            Properties.Show(unit);
            Navigation.IsInspectorOpen = true;
            IsBulkMoveEditorOpen = false;
            OnPropertyChanged(nameof(SelectionStatusText));
            OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        });

    public ICommand OpenStationUnitEditorCommand => _openStationUnitEditorCommand ??= CreateRelayCommand(
        _ => OpenStationUnitEditor(),
        _ => IsSceneEditable
            && (Navigation.IsEquipmentWorkspace || Navigation.IsSimulationWorkspace)
            && !IsStationUnitEditorOpen
            && !IsBulkMoveEditorOpen);

    public ICommand AddStationUnitCommand => _addStationUnitCommand ??= CreateRelayCommand(
        _ => AddStationUnit(),
        _ => CanAddStationUnit);

    public ICommand CancelStationUnitEditorCommand => _cancelStationUnitEditorCommand ??= CreateRelayCommand(
        _ => CancelStationUnitEditor(),
        _ => IsStationUnitEditorOpen);

    public ICommand RemoveEquipmentUnitCommand => _removeEquipmentUnitCommand ??= CreateRelayCommand(
        RemoveEquipmentUnit,
        CanRemoveEquipmentUnit);

    public ICommand RemoveSelectedStationCommand => _removeSelectedStationCommand ??= CreateRelayCommand(
        _ => RemoveSelectedStation(),
        _ => CanRemoveSelectedStation);

    public ICommand OpenBulkMoveEditorCommand => _openBulkMoveEditorCommand ??= CreateRelayCommand(
        _ => OpenBulkMoveEditor(),
        _ => IsSceneEditable
            && Navigation.IsEquipmentWorkspace
            && Layout.HasSelection
            && !IsStationUnitEditorOpen
            && !IsBulkMoveEditorOpen);

    public ICommand OpenEquipmentSequenceReferencesCommand => _openEquipmentSequenceReferencesCommand ??= CreateRelayCommand(
        _ =>
        {
            if (!TryResolvePendingPlacementDraft()) return;
            var reference = Layout.EquipmentSelectionStepReferences.FirstOrDefault();
            if (reference == default) return;
            SequenceEditor.SelectStep(reference.SequenceId, reference.StepId);
            if (SequenceEditor.SelectedSequence?.Id != reference.SequenceId || SequenceEditor.SelectedStep?.Id != reference.StepId) return;
            Navigation.IsSimulationWorkspace = true;
            Navigation.SelectedExecutionTabIndex = 1;
        },
        _ => (Navigation.IsEquipmentWorkspace || Navigation.IsSequenceWorkspace) && Layout.EquipmentSelectionStepReferences.Count > 0 && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen);

    public ICommand OpenExperimentCameraInputCommand => _openExperimentCameraInputCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is not CameraExecutionInputItem input || !CanOpenExperimentCameraInput(input) || !TryResolvePendingPlacementDraft()) return;
            if (!CanOpenExperimentCameraInput(input)) return;
            Camera.SelectCameraCommand.Execute(input.Id);
            if (Camera.SelectedCameraId == input.Id) Navigation.IsInspectionWorkspace = true;
        },
        parameter => parameter is CameraExecutionInputItem input && CanOpenExperimentCameraInput(input));

    public ICommand OpenCameraAcquisitionStepCommand => _openCameraAcquisitionStepCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is not CameraSequenceStepReference reference || !OpenCameraAcquisitionStepCommand.CanExecute(reference) || !TryResolvePendingPlacementDraft()) return;
            if (!Camera.CurrentAcquisitionSteps.Contains(reference)) return;
            SequenceEditor.SelectStep(reference.SequenceId, reference.StepId);
            if (SequenceEditor.SelectedSequence?.Id == reference.SequenceId && SequenceEditor.SelectedStep?.Id == reference.StepId)
                Navigation.OpenSequenceEditorCommand.Execute(null);
        },
        parameter => Navigation.IsInspectionWorkspace && !Navigation.IsModalOverlayOpen && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen
            && parameter is CameraSequenceStepReference reference && Camera.CurrentAcquisitionSteps.Contains(reference));

    public ICommand OpenSimulationObservationStepCommand => _openSimulationObservationStepCommand ??= CreateRelayCommand(
        parameter =>
        {
            if (parameter is not SimulationSequenceObservation observation || !CanOpenSimulationObservationStep(observation) || !TryResolvePendingPlacementDraft()) return;
            if (!CanOpenSimulationObservationStep(observation)) return;
            SequenceEditor.SelectStep(observation.SequenceId, observation.StepId!);
            if (SequenceEditor.SelectedSequence?.Id == observation.SequenceId && SequenceEditor.SelectedStep?.Id == observation.StepId)
                Navigation.OpenSequenceEditorCommand.Execute(null);
        },
        parameter => parameter is SimulationSequenceObservation observation && CanOpenSimulationObservationStep(observation));

    public ICommand OpenStepAxisSettingsCommand => _openStepAxisSettingsCommand ??= CreateRelayCommand(
        _ =>
        {
            if (!CanOpenStepAxisSettings() || _selectionSynchronization.FindStepAxisSettingsTarget() is not { } target) return;
            var project = CurrentProject;
            var sequence = SequenceEditor.SelectedSequence;
            string? stepId = SequenceEditor.SelectedStep?.Id;
            string? axisId = SequenceEditor.SelectedStepDraft?.TargetId;
            if (!TryResolvePendingPlacementDraft()) return;
            if (!ReferenceEquals(project, CurrentProject) || !ReferenceEquals(sequence, SequenceEditor.SelectedSequence)
                || stepId != SequenceEditor.SelectedStep?.Id || axisId != SequenceEditor.SelectedStepDraft?.TargetId || !CanOpenStepAxisSettings()
                || !ReferenceEquals(target, _selectionSynchronization.FindStepAxisSettingsTarget())) return;
            _selectionSynchronization.SelectSequenceStepTarget();
            if (!ReferenceEquals(Layout.SelectedItem, target)) return;
            Navigation.OpenEquipmentWorkspaceCommand.Execute(null);
            if (Navigation.IsEquipmentWorkspace && ReferenceEquals(Layout.SelectedItem, target)) OpenEquipmentDriveTabCommand.Execute(null);
        },
        _ => CanOpenStepAxisSettings());

    public ICommand OpenEquipmentPropertiesCommand => _openEquipmentPropertiesCommand ??= CreateRelayCommand(
        _ =>
        {
            if (!TryResolvePendingPlacementDraft()) return;
            IsEquipmentSinglePartCheckOpen = false;
            IsEquipmentDriveTabOpen = false;
            Navigation.IsInspectorOpen = true;
        },
        _ => !_isModeTransitioning && !_isApplyingProject
            && Navigation.IsEquipmentWorkspace
            && Layout.HasSelection
            && !IsStationUnitEditorOpen
            && !IsBulkMoveEditorOpen);

    public ICommand OpenEquipmentDriveTabCommand => _openEquipmentDriveTabCommand ??= CreateRelayCommand(
        _ =>
        {
            if (!TryResolvePendingPlacementDraft()) return;
            IsEquipmentSinglePartCheckOpen = false;
            IsEquipmentDriveTabOpen = true;
            Navigation.IsInspectorOpen = true;
        },
        _ => !_isModeTransitioning && !_isApplyingProject
            && Navigation.IsEquipmentWorkspace
            && Layout.HasSelection
            && !IsStationUnitEditorOpen
            && !IsBulkMoveEditorOpen);

    public ICommand OpenEquipmentSinglePartCheckCommand => _openEquipmentSinglePartCheckCommand ??= CreateRelayCommand(
        _ =>
        {
            if (!TryResolvePendingPlacementDraft()) return;
            AxisCommissioning.ResetInspectorInputs();
            IsEquipmentSinglePartCheckOpen = true;
            IsEquipmentDriveTabOpen = false;
            Navigation.IsInspectorOpen = true;
        },
        _ => !_isModeTransitioning && !_isApplyingProject
            && Navigation.IsEquipmentWorkspace
            && Layout.SelectionCount == 1
            && !IsStationUnitEditorOpen
            && !IsBulkMoveEditorOpen);

    public ICommand ApplyBulkMoveCommand => _applyBulkMoveCommand ??= CreateRelayCommand(
        _ => ApplyBulkMove(),
        _ => IsBulkMoveEditorOpen
            && IsSceneEditable
            && Layout.HasSelection
            && TryGetBulkMoveOffsets(out var ignoredX, out var ignoredZ));

    public ICommand CancelBulkMoveCommand => _cancelBulkMoveCommand ??= CreateRelayCommand(
        _ => IsBulkMoveEditorOpen = false,
        _ => IsBulkMoveEditorOpen);

    public ICommand DeleteLayoutComponentCommand => _layoutAuthoring.DeleteLayoutComponentCommand;

    public ICommand SceneSelectionRequestedCommand => _layoutAuthoring.SceneSelectionRequestedCommand;

    public ICommand SceneMoveRequestedCommand => _layoutAuthoring.SceneMoveRequestedCommand;

    public ICommand SceneMarqueeSelectionRequestedCommand => _layoutAuthoring.SceneMarqueeSelectionRequestedCommand;

    public ICommand SceneTransformRequestedCommand => _layoutAuthoring.SceneTransformRequestedCommand;

    public ICommand SceneLibraryComponentDropRequestedCommand => _layoutAuthoring.SceneLibraryComponentDropRequestedCommand;

    public ICommand NudgeLayoutComponentCommand => _layoutAuthoring.NudgeLayoutComponentCommand;

    public ICommand AlignLayoutSelectionCommand => _layoutAuthoring.AlignLayoutSelectionCommand;

    public ICommand ChangeLayoutLayerOrderCommand => _layoutAuthoring.ChangeLayoutLayerOrderCommand;

    public ICommand UndoLayoutEditCommand => _layoutAuthoring.UndoLayoutEditCommand;

    public ICommand RedoLayoutEditCommand => _layoutAuthoring.RedoLayoutEditCommand;

    public ICommand CopyLayoutSelectionCommand => _layoutAuthoring.CopyLayoutSelectionCommand;

    public ICommand DuplicateLayoutSelectionCommand => _layoutAuthoring.DuplicateLayoutSelectionCommand;

    public ICommand PasteLayoutSelectionCommand => _layoutAuthoring.PasteLayoutSelectionCommand;

    public ICommand PreviousDryRunPlaybackStepCommand => DryRunPlayback.PreviousStepCommand;

    public ICommand NextDryRunPlaybackStepCommand => DryRunPlayback.NextStepCommand;

    public ICommand ExitDryRunPlaybackCommand => DryRunPlayback.ExitCommand;

    public ICommand ReturnToProcessPlanCommand => _recipeAuthoring.ProcessPlanReview.ReturnToProcessPlanCommand;

    public ICommand PreviousProcessPlanReviewStepCommand => _recipeAuthoring.ProcessPlanReview.PreviousStepCommand;

    public ICommand NextProcessPlanReviewStepCommand => _recipeAuthoring.ProcessPlanReview.NextStepCommand;

    public ICommand ExitCommand => _exitCommand ??= CreateRelayCommand(_ => _mainWpfInteractionHost.RequestApplicationClose());

    #endregion

    #region Project File Operations

    private bool TryResolvePendingPlacementDraft()
    {
        if (SequenceEditor is not null && !SequenceEditor.ResolvePendingStepDraft()) return false;
        var editor = Layout?.SelectedComponentEditor;
        if (editor is null || !editor.HasPendingInspectorDraft) return true;
        if (_isResolvingPlacementDraft) return false;

        _isResolvingPlacementDraft = true;
        try
        {
            switch (PlacementDraftPrompt())
            {
                case PlacementDraftDecision.Apply:
                    return editor.TryApplyInspectorDraft();
                case PlacementDraftDecision.Discard:
                    editor.DiscardInspectorDraft();
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _isResolvingPlacementDraft = false;
        }
    }

    internal Task<bool> OpenProjectAsync(string path) => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.OpenProjectAsync(path)
        : Task.FromResult(false);

    internal Task<bool> OpenProjectReplacingCurrentAsync(string path) => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.OpenProjectReplacingCurrentAsync(path)
        : Task.FromResult(false);

    internal Task<bool> CreateNewProjectAsync() => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.CreateNewProjectAsync()
        : Task.FromResult(false);

    internal Task<bool> CreateNewProjectAsync(string? name)
    {
        if (!ProjectLifecycleCoordinator.TryNormalizeNewProjectName(name, out var normalizedName)
            || !TryResolvePendingPlacementDraft())
        {
            return Task.FromResult(false);
        }

        return _projectLifecycle.CreateNewProjectAsync(normalizedName);
    }

    internal async Task<bool> ConfirmNewProjectNameAsync()
    {
        if (!IsNewProjectNameDialogOpen || IsNewProjectNameConfirming || _disposed || _isApplyingProject || IsValidationBusy)
        {
            return false;
        }

        _newProjectNameValidationAttempted = true;
        OnPropertyChanged(nameof(NewProjectNameValidationText));
        OnPropertyChanged(nameof(HasNewProjectNameValidationError));
        if (!ProjectLifecycleCoordinator.TryNormalizeNewProjectName(NewProjectNameDraft, out var name))
        {
            return false;
        }

        IsNewProjectNameConfirming = true;
        try
        {
            if (!await CreateNewProjectAsync(name)) return false;
            CloseNewProjectNameDialog();
            return true;
        }
        finally { IsNewProjectNameConfirming = false; }
    }

    private void OpenNewProjectNameDialog()
    {
        _newProjectNameValidationAttempted = false;
        OnPropertyChanged(nameof(NewProjectNameValidationText));
        OnPropertyChanged(nameof(HasNewProjectNameValidationError));
        NewProjectNameDraft = OpenVisionLanguageService.T("Project.NewRecipeDefaultName");
        IsNewProjectNameDialogOpen = true;
    }

    private void CloseNewProjectNameDialog()
    {
        _newProjectNameValidationAttempted = false;
        IsNewProjectNameDialogOpen = false;
        OnPropertyChanged(nameof(NewProjectNameValidationText));
        OnPropertyChanged(nameof(HasNewProjectNameValidationError));
    }

    private void OnBlankLayoutStarted()
    {
        if (_disposed)
        {
            return;
        }

        StatusMessage = OpenVisionLanguageService.T("Scene.BlankLayoutReadyStatus");
        InvalidateCommands();
    }

    private Task OpenBundledSampleAsync() => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.OpenBundledSampleAsync()
        : Task.CompletedTask;

    private Task OpenLargeLayoutSampleAsync() => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.OpenLargeLayoutSampleAsync()
        : Task.CompletedTask;

    internal Task SaveProjectAsync(string path) => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.SaveProjectAsync(path)
        : Task.FromException(new OperationCanceledException("Placement changes remain unapplied."));

    private Task<bool> CreateSemiconductorRecipeCopyAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath) => CreateSemiconductorRecipeCopyCoreAsync(recipe, destinationPath);

    private Task<bool> TrySaveCurrentProjectAsync(bool saveAs = false) => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.TrySaveCurrentProjectAsync(saveAs)
        : Task.FromResult(false);

    private async Task<bool> CreateSemiconductorRecipeCopyCoreAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath)
    {
        if (!TryResolvePendingPlacementDraft()) return false;
        var copied = await _projectLifecycle.CreateSemiconductorRecipeCopyAsync(recipe, destinationPath);
        if (!copied || _disposed)
        {
            return false;
        }

        Navigation.EnterEquipmentAuthoring();
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.CopySucceeded"),
            recipe.DisplayName);
        _runtimeObservabilityJournal.Log("Project", $"Created semiconductor recipe copy · {recipe.DisplayName}");
        return true;
    }

    #endregion

    #region Layout Authoring And Scene Interaction

    private void OpenStationUnitEditor()
    {
        if (!IsSceneEditable
            || (!Navigation.IsEquipmentWorkspace && !Navigation.IsSimulationWorkspace)
            || !TryResolvePendingPlacementDraft())
        {
            return;
        }

        IsEquipmentSinglePartCheckOpen = false;
        var firstStation = CurrentProject.Stations.FirstOrDefault();
        IsCreatingNewStation = firstStation is null;
        SelectedStationId = firstStation?.Id;
        StationNameDraft = OpenVisionLanguageService.T(
            "Equipment.StationUnitDefaultStation",
            "새 스테이션",
            "New station");
        UnitNameDraft = OpenVisionLanguageService.T(
            "Equipment.StationUnitDefaultUnit",
            "새 유닛",
            "New unit");
        OnPropertyChanged(nameof(ProjectStations));
        OnPropertyChanged(nameof(StationUnitStationChoices));
        OnPropertyChanged(nameof(SelectedStationChoiceId));
        IsStationUnitEditorOpen = true;
    }

    private void OpenEquipmentComponentLibraryDialog()
    {
        if (!IsSceneEditable || !Navigation.IsEquipmentWorkspace || IsEquipmentComponentLibraryDialogOpen
            || IsStationUnitEditorOpen || IsBulkMoveEditorOpen)
        {
            return;
        }

        if (EquipmentComponentLibraryUnits.Count == 0)
        {
            OpenStationUnitEditor();
            return;
        }

        if (!TryResolvePendingPlacementDraft()) return;
        Layout.LibrarySearchText = string.Empty;
        EquipmentComponentLibraryDialogStatusText = string.Empty;
        OnPropertyChanged(nameof(EquipmentComponentLibraryUnits));
        EquipmentComponentLibraryUnitId = ResolveActiveEquipmentUnitId() ?? EquipmentComponentLibraryUnits[0].Key;
        IsEquipmentComponentLibraryDialogOpen = true;
    }

    private bool CanAddEquipmentComponentFromLibrary(object? parameter, out ComponentLibraryItem? item)
    {
        item = parameter as ComponentLibraryItem ?? (parameter is LayoutComponentKind kind
            ? EquipmentComponentLibraryItems.FirstOrDefault(candidate => candidate.Kind == kind) : null);
        return IsEquipmentComponentLibraryDialogOpen && IsSceneEditable && Navigation.IsEquipmentWorkspace
            && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen && item is not null
            && EquipmentComponentLibraryUnits.Any(unit => unit.Key == EquipmentComponentLibraryUnitId)
            && EquipmentComponentLibraryItems.Contains(item) && AddEquipmentLayoutComponentCommand.CanExecute(item.Kind);
    }

    private void OnEquipmentLibraryLayoutPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MachineLayoutViewModel.UnitOverviewItems))
        {
            OnPropertyChanged(nameof(SequenceNavigationItems));
            OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
        }
        if (args.PropertyName == nameof(MachineLayoutViewModel.ActiveUnitId)) OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
        if (args.PropertyName != nameof(MachineLayoutViewModel.FilteredLibraryItems)) return;
        OnPropertyChanged(nameof(EquipmentComponentLibraryItems));
        OnPropertyChanged(nameof(EquipmentComponentLibraryHasNoMatches));
        RaiseCanExecuteChanged(_addEquipmentComponentFromLibraryCommand);
    }

    private void AddEquipmentComponentFromLibrary(object? parameter)
    {
        if (!CanAddEquipmentComponentFromLibrary(parameter, out var item)) return;
        var unitId = EquipmentComponentLibraryUnitId!;
        if (_layoutAuthoring.TryAddComponent(item!.Kind, cameraInputKind: item.CameraInputKind))
        {
            Layout.ShowUnit(unitId);
            IsEquipmentComponentLibraryDialogOpen = false;
            OpenEquipmentPropertiesCommand.Execute(null);
            if (item.Kind is LayoutComponentKind.FeedPosition or LayoutComponentKind.ExitPosition)
                MaterialFlowSettings.OpenCommand.Execute(null);
        }
        else
        {
            EquipmentComponentLibraryDialogStatusText = StatusMessage;
        }
    }

    private void AddEquipmentLayoutComponent(object? parameter)
    {
        if (!TryResolvePendingPlacementDraft()) return;
        if (ResolveActiveEquipmentUnitId() is null)
        {
            StatusMessage = OpenVisionLanguageService.T("Equipment.LibraryUnitRequired");
            OpenStationUnitEditor();
            return;
        }

        if (AddLayoutComponentCommand.CanExecute(parameter))
        {
            AddLayoutComponentCommand.Execute(parameter);
        }
    }

    private void AddStationUnit()
    {
        if (!CanAddStationUnit)
        {
            return;
        }

        MachineStationDefinition station;
        if (IsCreatingNewStation)
        {
            station = new MachineStationDefinition
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = StationNameDraft.Trim()
            };
            CurrentProject.Stations.Add(station);
        }
        else
        {
            var existingStation = CurrentProject.Stations.FirstOrDefault(item =>
                string.Equals(item.Id, SelectedStationId, StringComparison.Ordinal));
            if (existingStation is null)
            {
                OnPropertyChanged(nameof(StationUnitValidationText));
                RaiseCanExecuteChanged(_addStationUnitCommand);
                return;
            }

            station = existingStation;
        }

        var unit = new MachineUnitDefinition
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = UnitNameDraft.Trim(),
            SlotName = UnitNameDraft.Trim() + " 자리",
            BaseOrigin = LayoutComponentPlacementService.CreateUnitBaseOrigin(CurrentProject.Stations.Sum(item => item.Units.Count))
        };
        station.Units.Add(unit);

        MarkProjectChanged(requiresRuntimeRebuild: false);
        RefreshDefinitionPresentation(null);
        OnPropertyChanged(nameof(ProjectStations));
        OnPropertyChanged(nameof(StationUnitStationChoices));
        OnPropertyChanged(nameof(SelectedStationChoiceId));
        ProjectTree.SelectedNode = ProjectTree.Roots
            .SelectMany(root => root.Children)
            .Where(group => group.Kind == TreeNodeKind.Stations)
            .SelectMany(group => group.Children)
            .SelectMany(stationNode => stationNode.Children)
            .FirstOrDefault(unitNode => string.Equals(unitNode.Id, unit.Id, StringComparison.Ordinal));
        IsStationUnitEditorOpen = false;
        if (Navigation.IsEquipmentWorkspace)
        {
            Properties.Show(unit);
            Navigation.IsInspectorOpen = true;
        }
        StatusMessage = OpenVisionLanguageService.T("Equipment.StationUnitAddedStatus");
        InvalidateCommands();
    }

    private void CancelStationUnitEditor() => IsStationUnitEditorOpen = false;

    private bool CanRemoveEquipmentUnit(object? parameter) => IsSceneEditable
        && (Navigation.IsEquipmentWorkspace || Navigation.IsSimulationWorkspace)
        && !IsStationUnitEditorOpen
        && !IsEquipmentComponentLibraryDialogOpen
        && !IsBulkMoveEditorOpen
        && !_isApplyingProject
        && !_sessionCloseRequested
        && !_disposed
        && parameter is string unitId
        && !string.IsNullOrWhiteSpace(unitId)
        && FindStationAndUnit(unitId) is not null;

    private void RemoveEquipmentUnit(object? parameter)
    {
        if (parameter is not string unitId || !CanRemoveEquipmentUnit(unitId)
            || FindStationAndUnit(unitId) is not { } target)
        {
            return;
        }

        if (!TryResolvePendingPlacementDraft())
        {
            return;
        }

        var assignedComponentCount = CurrentProject.Layouts
            .SelectMany(layout => layout.Components)
            .Count(component => string.Equals(component.UnitId, target.Unit.Id, StringComparison.Ordinal));
        if (assignedComponentCount > 0)
        {
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Equipment.UnitRemovalBlocked"), target.Unit.Name, assignedComponentCount);
            EquipmentUnitRemovalBlockedPresenter(StatusMessage);
            return;
        }

        if (!EquipmentUnitRemovalPrompt(target.Station, target.Unit))
        {
            return;
        }

        target.Station.Units.Remove(target.Unit);
        OnPropertyChanged(nameof(StationUnitRemovalHintText));
        OnPropertyChanged(nameof(HasSelectedStationChildren));
        RaiseCanExecuteChanged(_removeSelectedStationCommand);
        MarkProjectChanged(requiresRuntimeRebuild: false);
        RefreshDefinitionPresentation(null);
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Equipment.UnitRemovedStatus"), target.Unit.Name);
    }

    private void RemoveSelectedStation()
    {
        var station = SelectedExistingStation;
        if (!CanRemoveSelectedStation || station is null)
        {
            return;
        }

        if (!TryResolvePendingPlacementDraft())
        {
            return;
        }

        if (station.Units.Count > 0)
        {
            OnPropertyChanged(nameof(StationUnitRemovalHintText));
            return;
        }

        if (!EquipmentStationRemovalPrompt(station))
        {
            return;
        }

        CurrentProject.Stations.Remove(station);
        OnPropertyChanged(nameof(ProjectStations));
        OnPropertyChanged(nameof(StationUnitStationChoices));
        OnPropertyChanged(nameof(SelectedStationChoiceId));
        SelectedStationId = null;
        IsCreatingNewStation = true;
        StationNameDraft = OpenVisionLanguageService.T("Equipment.StationUnitDefaultStation");
        MarkProjectChanged(requiresRuntimeRebuild: false);
        RefreshDefinitionPresentation(null);
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Equipment.StationRemovedStatus"), station.Name);
    }

    private (MachineStationDefinition Station, MachineUnitDefinition Unit)? FindStationAndUnit(string unitId)
    {
        foreach (var station in CurrentProject.Stations)
        {
            var unit = station.Units.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, unitId, StringComparison.Ordinal));
            if (unit is not null)
            {
                return (station, unit);
            }
        }

        return null;
    }

    private void OpenBulkMoveEditor()
    {
        if (!TryResolvePendingPlacementDraft()) return;
        IsEquipmentSinglePartCheckOpen = false;
        BulkMoveXText = "0";
        BulkMoveZText = "0";
        IsBulkMoveEditorOpen = true;
    }

    private void ApplyBulkMove()
    {
        if (TryGetBulkMoveOffsets(out var deltaX, out var deltaZ))
        {
            _layoutAuthoring.TryMoveSelectionBy(deltaX, deltaZ);
        }
        IsBulkMoveEditorOpen = false;
    }

    private bool TryGetBulkMoveOffsets(out double deltaX, out double deltaZ)
    {
        var hasX = double.TryParse(BulkMoveXText, NumberStyles.Float, CultureInfo.CurrentCulture, out deltaX);
        var hasZ = double.TryParse(BulkMoveZText, NumberStyles.Float, CultureInfo.CurrentCulture, out deltaZ);
        return hasX && hasZ && double.IsFinite(deltaX) && double.IsFinite(deltaZ);
    }

    private string? ResolveLayoutAuthoringUnitId() => Navigation.IsEquipmentWorkspace
        ? IsEquipmentComponentLibraryDialogOpen ? EquipmentComponentLibraryUnitId : ResolveActiveEquipmentUnitId()
        : ProjectTree.SelectedNode is { Kind: TreeNodeKind.Unit, Model: MachineUnitDefinition unit }
            ? unit.Id
            : null;

    private string? ResolveActiveEquipmentUnitId()
    {
        var activeUnitId = Layout.ActiveUnitId;
        return activeUnitId is not null && CurrentProject.Stations
            .SelectMany(station => station.Units)
            .Any(unit => string.Equals(unit.Id, activeUnitId, StringComparison.Ordinal))
                ? activeUnitId
                : null;
    }

    public bool TryAddLayoutComponent(
        LayoutComponentKind kind,
        double? worldX = null,
        double? worldY = null) =>
        _layoutAuthoring.TryAddComponent(kind, worldX, worldY);

    private void RefreshDefinitionPresentation(string? selectedComponentId)
    {
        _selectionSynchronization.ClearAnalogEditor();
        ProjectTree.LoadProject(CurrentProject);
        Layout.Load(CurrentProject);
        EquipmentMaterialFlow.Update(CurrentProject, Layout.Items, PresentationSnapshot);
        if (selectedComponentId is not null)
        {
            Layout.Select(selectedComponentId);
        }
        RecipeConnections.Load(CurrentProject, Layout.SelectedItem?.Id);
        SequenceEditor.RefreshAuthoringTargets();
        Camera.CaptureEditor.RefreshAuthoringTargets();
        SequenceEditor.ReloadUnitRepeatDraft();
        Properties.Show(Layout.SelectedItem?.Component);
        OnPropertyChanged(nameof(AxisCountText));
        OnPropertyChanged(nameof(LayoutComponentCountText));
        OnPropertyChanged(nameof(CameraCountText));
        OnPropertyChanged(nameof(HasAuthoredLayout));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        OnPropertyChanged(nameof(RunTargetText));
        OnPropertyChanged(nameof(RunTargetSummaryText));
        InvalidateCommands();
    }

    #endregion

    #region Project Application And Runtime Definition

    private Task<bool> ApplyProjectAsync(MachineProjectDocument project) =>
        _projectRuntimeApplicationWorkflow.ApplyAsync(project);

    private async Task<bool> ApplyHistoricalRecipeDraftAsync(MachineProjectDocument draft)
    {
        if (draft.Id != CurrentProject.Id || !TryResolvePendingPlacementDraft()
            || !await _projectLifecycle.TryResolveUnsavedChangesAsync()) return false;
        if (_disposed || _sessionCloseRequested || draft.Id != CurrentProject.Id) return false;
        _isApplyingHistoricalDraft = true;
        try
        {
            if (!await ApplyProjectAsync(draft)) return false;
            MarkProjectChanged(requiresRuntimeRebuild: false);
            StatusMessage = OpenVisionLanguageService.T("RunHistory.DraftApplied");
            return true;
        }
        finally { _isApplyingHistoricalDraft = false; }
    }

    private static string GetRunHistoryDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR");
        if (!string.IsNullOrWhiteSpace(directory)) return Path.GetFullPath(directory);
        var smokeDirectory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_SMOKE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(smokeDirectory) ? Path.Combine(Path.GetFullPath(smokeDirectory), "RunHistory")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVisionLab", "MachineStudio", "RunHistory");
    }

    private void OnProjectRuntimeApplicationStateChanged(bool isApplying)
    {
        if (_disposed)
        {
            return;
        }

        if (isApplying)
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            _simulationScenarioExecutionCoordinator.InvalidatePendingExecution();
            _simulationSession.RunControl.InvalidatePendingExecution();
            _camera.InvalidatePreparation();
        }

        _isApplyingProject = isApplying;
        OnPropertyChanged(nameof(CanNavigateSequenceUnits));
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        UpdateRunToolAvailability();
        InvalidateCommands();
    }

    private void OnProjectRuntimeApplicationRejected(RuntimeDefinitionApplicationResult result)
    {
        if (_disposed)
        {
            return;
        }

        if (result.Outcome == RuntimeDefinitionApplicationOutcome.CompilationRejected)
        {
            _runtimeObservabilityJournal.Log("Project", $"Project rejected · {result.CompilationDetail}");
        }
        else if (result.CommandResult is { } commandResult)
        {
            _runtimeObservabilityJournal.Log("Project", $"Project rejected · {commandResult.ErrorCode}: {commandResult.Detail}");
        }

        StatusMessage = OpenVisionLanguageService.T("Project.OpenFailedStatus");
        string detail = result.Outcome == RuntimeDefinitionApplicationOutcome.CompilationRejected
            ? result.CompilationDetail ?? string.Empty
            : $"{result.CommandResult?.ErrorCode}: {result.CommandResult?.Detail}";
        ProjectOpenFailurePresenter(detail);
    }

    private void CompleteProjectRuntimeApplication(MachineProjectDocument project)
    {
        if (_disposed)
        {
            return;
        }

        _camera.InvalidatePreparation();
        if (project.Id != CurrentProject.Id) Navigation.ClearResultSettingsReturn();
        _projectLifecycle.ReplaceProject(project);
        _unifiedCommissioningEvidence.Reset();
        _scenarioBatch!.Reset();
        _simulationCommandTrace.Reset();
        _multiAxisCommissioning.Reset();
        _camera.ClearEvidence();
        ApplyProjectPresentation(project);
        RuntimeDebugger.LoadProject(project, resetSession: true);
        _layoutAuthoring.Reset();
        _runtimeDefinitionDirty = false;
        UpdateRunToolAvailability();
        IsRunning = false;
        ApplyDesignMode(true);
        var currentSnapshot = _simulationSession.Engine.CurrentSnapshot;
        SceneSnapshots.Publish(currentSnapshot);
        ApplyMonitorSnapshot(currentSnapshot);
        SimulationWorkspace.AcceptRuntimeProjectionDefaults();
        if (!_isApplyingHistoricalDraft) AcceptCurrentProjectAsSaved();
    }

    private async Task<bool> EnsureRuntimeDefinitionAppliedAsync()
    {
        if (_disposed)
        {
            return false;
        }

        if (!_runtimeDefinitionDirty)
        {
            return true;
        }

        var result = await _runtimeDefinitionApplicationWorkflow.ApplyAsync(CurrentProject);
        if (_disposed)
        {
            return false;
        }

        if (!result.IsAccepted)
        {
            StatusMessage = "Machine definition is invalid";
            if (result.Outcome == RuntimeDefinitionApplicationOutcome.CompilationRejected)
            {
                _runtimeObservabilityJournal.Log("Project", $"Simulation build rejected · {result.CompilationDetail}");
            }
            else if (result.CommandResult is { } commandResult)
            {
                _runtimeObservabilityJournal.Log("Project", $"Simulation build rejected · {commandResult.ErrorCode}: {commandResult.Detail}");
            }
            return false;
        }

        _runtimeDefinitionDirty = false;
        UpdateRunToolAvailability();
        ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
        _runtimeObservabilityJournal.Log("Runtime", "Authored machine rebuilt for Simulation ON");
        return true;
    }

    private void ApplyProjectPresentation(MachineProjectDocument project)
    {
        RunHistory?.LoadProject();
        IsStationUnitEditorOpen = false;
        IsEquipmentComponentLibraryDialogOpen = false;
        _recipeAuthoring.ResetPresentation();
        _camera.LoadProject(project, CurrentProjectPath);
        OnPropertyChanged(nameof(ProjectAxes));
        OnPropertyChanged(nameof(ProjectDevices));
        _selectionSynchronization.ClearEditors();
        ProjectTree.LoadProject(project);
        Layout.Load(project);
        RecipeConnections.Load(project, Layout.SelectedItem?.Id);
        RecipeConnections.SetProjectRevision(_projectLifecycle.Revision);
        SequenceEditor.Load(project);
        OnPropertyChanged(nameof(SequenceNavigationItems));
        OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
        SimulationWorkspace.LoadProjectScenario(project.Simulation);
        MultiAxisCommissioningRecipe.Load(project);
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
        Properties.ShowNode(null);
        RefreshProjectIdentity();
        StatusMessage = "Ready";
        OnPropertyChanged(nameof(ProjectStatusText));
        OnPropertyChanged(nameof(ProjectRevisionText));
        OnPropertyChanged(nameof(EquipmentFooterStatusText));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        OnPropertyChanged(nameof(AxisCountText));
        OnPropertyChanged(nameof(LayoutComponentCountText));
        OnPropertyChanged(nameof(HasEmbeddedSequence));
        OnPropertyChanged(nameof(HasAutomaticRun));
        OnPropertyChanged(nameof(RunTargetText));
        OnPropertyChanged(nameof(RunTargetSummaryText));
        OnPropertyChanged(nameof(HasAuthoredLayout));
        OnPropertyChanged(nameof(HasCycleStartInput));
        OnPropertyChanged(nameof(ControlOwnerHelpText));
        OnPropertyChanged(nameof(SceneTitleText));
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
        NotifyMultiAxisCommissioningRecipeChanged();
        InvalidateCommands();
    }

    private static SimulationRuntimeConfiguration BuildRuntimeConfiguration(MachineProjectDocument project, string? projectPath = null)
    {
        var result = new MachineProjectRuntimeCompiler(SimulationFixedStep).Compile(project, projectPath, BuildIdentity.Current);
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

    #endregion

    #region Runtime Projection

    private void RefreshManualEquipmentProjection(
        SimulationSnapshot? snapshot = null,
        bool invalidateCommands = true)
    {
        _manualEquipment.RefreshProjection(snapshot, invalidateCommands);
        RefreshEquipmentInspectorManualPresentation();
    }

    private async Task<bool> PrepareSelectedManualControlAsync()
    {
        if (!_isManualControlEntryReady || _disposed || _sessionCloseRequested || _isModeTransitioning
            || _isApplyingProject || IsValidationBusy || _simulationSession.RunControl.IsBusy
            || IsRunning || RuntimeProjection.AutomaticRun.IsActive
            || RuntimeProjection.CurrentSequence?.Status == SequenceExecutionStatus.Running
            || Layout.SelectionCount != 1 || !Navigation.IsEquipmentWorkspace
            || IsStationUnitEditorOpen || IsBulkMoveEditorOpen) return false;
        if (!TryResolvePendingPlacementDraft()) return false;
        var project = CurrentProject;
        _manualEquipment.BeginRuntimePreparation();
        return await EnsureRuntimeDefinitionAppliedAsync() && ReferenceEquals(project, CurrentProject);
    }

    private ManualEquipmentProjection CreateManualEquipmentProjection(SimulationSnapshot? snapshot = null) =>
        new(
            snapshot ?? PresentationSnapshot,
            Layout.SelectedItem?.Id,
            Layout.SelectedItem?.Component?.Kind,
            IsRunMode,
            _isApplyingProject,
            IsValidationBusy,
            _runtimeDefinitionDirty,
            IsRunning,
            RuntimeProjection.ControlOwner,
            RuntimeProjection.AutomaticRun.IsActive,
            RuntimeProjection.CurrentSequence?.Status,
            RuntimeProjection.CurrentAxis is not null,
            RuntimeProjection.CurrentAxis?.State == AxisState.Error,
            HasCurrentCamera: RuntimeProjection.CurrentCamera is not null,
            IsRunControlBusy: !_isManualControlEntryReady || _simulationSession.RunControl.IsBusy,
            IsModeTransitioning: _isModeTransitioning,
            IsSelectionEligible: Layout.SelectionCount == 1 && Navigation.IsEquipmentWorkspace
                && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen);

    private CameraCommissioningProjection CreateCameraCommissioningProjection()
    {
        var cameraDefinition = CurrentCameraDefinition;
        var fallbackCameraName = CurrentProject.Devices
            .FirstOrDefault(device => device.Kind == DeviceKind.Camera)
            ?.Name;
        return new(
            RuntimeProjection.CurrentCamera,
            cameraDefinition is not null,
            fallbackCameraName,
            cameraDefinition?.Camera?.SingleImageSource,
            CurrentProjectPath,
            _camera.SelectedCameraRecipe,
            SimulationFixedStep,
            _simulationSession.Engine.CurrentSnapshot.RunMode,
            IsRunMode,
            _isApplyingProject,
            IsValidationBusy,
            _runtimeDefinitionDirty,
            IsRunning,
            RuntimeProjection.ControlOwner,
            RuntimeProjection.AutomaticRun.IsActive,
            RuntimeProjection.CurrentSequence?.Status,
            cameraDefinition?.Camera?.NumericalInput);
    }

    private void RefreshCameraCommissioningProjection(bool invalidateCommands = true) =>
        _camera.RefreshProjection(invalidateCommands);

    private SimulationRuntimeProjectionSelection CreateRuntimeProjectionSelection()
    {
        var selectedLayout = Layout.SelectedItem;
        var selectedTreeAxisId = ProjectTree.SelectedNode is
            { Kind: global::OpenVisionLab.MachineStudio.Model.TreeNodeKind.Axis } selected
            ? selected.Id
            : null;
        var projectCameraId = CurrentProject.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera)?.Id;

        return new(
            selectedLayout?.Component?.Kind,
            selectedLayout?.BehaviorBindingId,
            selectedTreeAxisId,
            _camera.SelectedCameraId ?? projectCameraId,
            SimulationWorkspace.ScheduledFaultKind,
            ActiveSequenceId);
    }

    private void ApplyMonitorSnapshot(SimulationSnapshot snapshot)
    {
        EquipmentMaterialFlow.Update(CurrentProject, Layout.Items, DryRunPlayback.IsActive ? PresentationSnapshot : snapshot);
        _runtimeProjectionCoordinator.Apply(
            snapshot,
            CreateRuntimeProjectionSelection());
        var activeSequence = IsRunMode && RuntimeProjection.CurrentSequence?.Status == SequenceExecutionStatus.Running
            ? RuntimeProjection.CurrentSequence
            : null;
        SequenceEditor.ApplyRuntimeStep(activeSequence?.ActiveSequenceId ?? activeSequence?.SequenceId,
            activeSequence?.CurrentStepId);
        Integration.RefreshRuntimeCommandState();
        NotifyProjectAndRuntimeChanged();
    }

    private bool CanApplyRuntimeSnapshot(SimulationSnapshot snapshot) =>
        !_disposed
        && !_sessionCloseRequested
        && !_isApplyingProject
        && IsCurrentRuntimeSnapshot(snapshot);

    private bool IsCurrentRuntimeSnapshot(SimulationSnapshot snapshot)
    {
        var current = _simulationSession.Engine.CurrentSnapshot;
        return snapshot.RuntimeGeneration == current.RuntimeGeneration
            && string.Equals(snapshot.ProjectId, current.ProjectId, StringComparison.Ordinal);
    }

    private bool CanOpenStepAxisSettings() => CanNavigateSequenceUnits && IsSceneEditable && SequenceEditor.IsStepDraftEditable
        && Navigation.IsSequenceWorkspace && !Navigation.IsModalOverlayOpen && !IsValidationBusy
        && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen && !IsEquipmentComponentLibraryDialogOpen
        && _selectionSynchronization.FindStepAxisSettingsTarget() is not null;

    private bool CanOpenRunCaptureCameraSettings(RunCaptureRecord capture) => CanNavigateSequenceUnits && IsSceneEditable
        && Navigation.IsResultsWorkspace && !Navigation.IsModalOverlayOpen && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen
        && !IsEquipmentComponentLibraryDialogOpen && Camera.SelectCameraCommand.CanExecute(capture.CameraId)
        && CurrentProject.Devices.Any(device => device.Kind == DeviceKind.Camera && device.Id == capture.CameraId);

    private void OpenRunCaptureCameraSettings(RunCaptureRecord capture)
    {
        if (!RunHistory.OpenCameraSettingsCommand.CanExecute(capture) || !TryResolvePendingPlacementDraft()) return;
        if (!RunHistory.OpenCameraSettingsCommand.CanExecute(capture) || !Navigation.OpenResultCameraSettings()) return;
        Camera.SelectCameraCommand.Execute(capture.CameraId);
    }

    private bool CanOpenExperimentCameraInput(CameraExecutionInputItem input) =>
        Navigation.IsExperimentWorkspace && !Navigation.IsModalOverlayOpen && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen
        && !_isApplyingProject && !_isModeTransitioning && AreSimulationObservationsCurrent
        && input.ProjectId == CurrentProject.Id && input.RuntimeGeneration == _simulationSession.Engine.CurrentSnapshot.RuntimeGeneration
        && Camera.CurrentCameraInputs.Contains(input);

    private bool CanOpenSimulationObservationStep(SimulationSequenceObservation observation) =>
        Navigation.IsSimulationOverviewWorkspace && !Navigation.IsModalOverlayOpen && !IsStationUnitEditorOpen && !IsBulkMoveEditorOpen
        && !_isApplyingProject && !_isModeTransitioning && AreSimulationObservationsCurrent && observation.CanOpenStep
        && observation.ProjectId == CurrentProject.Id && observation.Matches(_simulationSession.Engine.CurrentSnapshot)
        && CurrentProject.Sequences.Any(sequence => sequence.Id == observation.SequenceId && sequence.Steps.Any(step => step.Id == observation.StepId));

    private void NotifyProjectAndRuntimeChanged()
    {
        OnPropertyChanged(nameof(AreSimulationObservationsCurrent));
        OnPropertyChanged(nameof(SimulationStatusText));
        OnPropertyChanged(nameof(SimulationSecondsStatusText));
        OnPropertyChanged(nameof(EquipmentVerdictStatusText));
        OnPropertyChanged(nameof(IsEquipmentExternalInspectionWaiting));
        OnPropertyChanged(nameof(TickStatusText));
        OnPropertyChanged(nameof(CurrentAxisName));
        OnPropertyChanged(nameof(CurrentAxisStateText));
        OnPropertyChanged(nameof(CurrentAxisPositionText));
        OnPropertyChanged(nameof(CurrentAxisVelocityText));
        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        _camera.RefreshProjection(invalidateCommands: false);
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStateText));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
        OnPropertyChanged(nameof(CanAbortSequence));
        OnPropertyChanged(nameof(CanRetrySequence));
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
        _simulationCommandTrace.NotifyRuntimeChanged();
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanExportUnifiedCommissioningEvidence));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SimulationSpeedText));
        OnPropertyChanged(nameof(SimulationEngineStateText));
        OnPropertyChanged(nameof(CurrentRunStatusText));
        OnPropertyChanged(nameof(RunStateCaptionText));
        OnPropertyChanged(nameof(HasRecipeExecutionStatus));
        OnPropertyChanged(nameof(RecipeExecutionStatusText));
        OnPropertyChanged(nameof(RunStatusText));
        OnPropertyChanged(nameof(RunTargetText));
        OnPropertyChanged(nameof(RunTargetSummaryText));
        _supportDiagnostics.Refresh();
        InvalidateCommands();
    }

    #endregion

    #region Evidence And Batch Results

    private void ExportSimulationEvidenceWithDialog()
    {
        if (_simulationEvidenceFileDialogHost.SelectSimulationEvidenceExport(ProjectDisplayName)
            is { } path)
        {
            TryExportSimulationEvidence(path);
        }
    }

    private void ImportSimulationEvidenceWithDialog()
    {
        if (_simulationEvidenceFileDialogHost.SelectSimulationEvidenceImport()
            is { } path)
        {
            TryImportSimulationEvidence(path);
        }
    }

    private void ExportSimulationReportWithDialog()
    {
        if (_simulationEvidenceFileDialogHost.SelectSimulationReportExport(ProjectDisplayName)
            is { } path)
        {
            TryExportSimulationReport(path);
        }
    }

    private void ExportSimulationCommandTraceWithDialog()
    {
        if (_simulationEvidenceFileDialogHost.SelectCommandTraceExport(ProjectDisplayName)
            is { } path)
        {
            _simulationCommandTrace.TryExport(path);
        }
    }

    private Task ReplaySimulationCommandTraceWithDialogAsync()
    {
        var path = _simulationEvidenceFileDialogHost.SelectCommandTraceReplay();
        return path is not null
            ? _simulationCommandTrace.TryReplayAsync(path)
            : Task.CompletedTask;
    }

    internal bool TryExportSimulationCommandTrace(string path) =>
        _simulationCommandTrace.TryExport(path);

    internal Task<bool> TryReplaySimulationCommandTraceAsync(string path) =>
        _simulationCommandTrace.TryReplayAsync(path);

    internal bool TryExportSimulationEvidence(string path) =>
        _scenarioBatch!.TryExportEvidence(path);

    internal bool TryExportSimulationReport(string path) =>
        _scenarioBatch!.TryExportReport(path);

    internal bool TryImportSimulationEvidence(string path) =>
        _scenarioBatch!.TryImportEvidence(path);

    private void ExportUnifiedCommissioningEvidenceWithDialog()
    {
        if (_simulationEvidenceFileDialogHost.SelectUnifiedEvidenceExport(ProjectDisplayName)
            is { } path)
        {
            TryExportUnifiedCommissioningEvidence(path);
        }
    }

    private void ImportUnifiedCommissioningEvidenceWithDialog()
    {
        if (_simulationEvidenceFileDialogHost.SelectUnifiedEvidenceImport()
            is { } path)
        {
            TryImportUnifiedCommissioningEvidence(path);
        }
    }

    internal bool TryExportUnifiedCommissioningEvidence(string path) =>
        _unifiedCommissioningEvidence.TryExport(path);

    internal bool TryImportUnifiedCommissioningEvidence(string path) =>
        _unifiedCommissioningEvidence.TryImport(path);

    private bool CanExportUnifiedCommissioningEvidenceCore() => CanExportSimulationEvidence
        && _simulationCommandTrace.IsCaptureStarted
        && _simulationSession.Engine is FixedStepSimulationEngine
        {
            CommandTraceCount: > 0,
            CommandTraceIsComplete: true
        };

    private bool CanImportUnifiedCommissioningEvidenceCore() => CanImportSimulationEvidence
        && !_camera.VisionEvidence.IsCapturing;

    private DeterministicSimulationEvidenceExchangePackage?
        CreateSimulationEvidenceForUnifiedCommissioning() =>
        _scenarioBatch?.LatestBatchResult is { } batchResult
            ? DeterministicSimulationEvidenceExchangePackage.Create(
                batchResult,
                _scenarioBatch.AcceptedBatchBaseline)
            : null;

    private DeterministicSimulationCommandTracePackage? CreateCommandTraceForUnifiedCommissioning() =>
        _simulationSession.Engine is FixedStepSimulationEngine
        {
            CommandTraceIsComplete: true
        } traceEngine
            ? traceEngine.CreateCommandTracePackage()
            : null;

    private UnifiedCommissioningEvidenceContext? CreateUnifiedCommissioningEvidenceContext()
    {
        var targetId = SimulationWorkspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        try
        {
            return new UnifiedCommissioningEvidenceContext(
                CurrentProject.Id,
                _projectLifecycle.SerializeForEvidence(),
                SimulationFixedStep,
                SimulationWorkspace.BuildEngineProfile(targetId),
                BuildIdentity.Current,
                CurrentProjectPath
                    ?? Path.Combine(AppContext.BaseDirectory, $"unsaved-{CurrentProject.Id}.ovmachine"));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ApplyImportedUnifiedCommissioningArtifacts(
        DeterministicSimulationBatchResultPackage batchResult,
        DeterministicSimulationRunResultPackage? acceptedBaseline,
        DeterministicVisionExecutionEvidencePackage? visionEvidence)
    {
        _scenarioBatch!.SetImportedPackages(batchResult, acceptedBaseline);
        _camera.SetImportedEvidence(visionEvidence);
    }

    private VisionEvidenceContext CreateVisionEvidenceContext() =>
        new(
            CurrentProject.Id,
            _projectLifecycle.SerializeForEvidence(),
            BuildIdentity.Current,
            CurrentProjectPath,
            _camera.SelectedCameraId,
            _camera.SelectedCameraRecipe,
            ValidateCurrentVisionFrameSource);

    private bool ValidateCurrentVisionFrameSource(string expectedFrameHash)
    {
        var projectPath = CurrentProjectPath;
        var source = CurrentCameraDefinition?.Camera?.SingleImageSource;
        if (string.IsNullOrWhiteSpace(projectPath) || source is null)
        {
            return false;
        }

        try
        {
            var projectRoot = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            return new ProjectRelativeSingleImageSource(
                projectRoot,
                source.SourceRelativePath,
                source.Width,
                source.Height,
                source.PixelFormat).MatchesContentHash(expectedFrameHash);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidDataException)
        {
            return false;
        }
    }

    private DeterministicVisionExecutionEvidencePackage? GetCurrentUnifiedCommissioningVisionEvidence() =>
        _camera.GetCurrentEvidence();

    #endregion

    #region Scenario Presentation

    private void NavigateToBatchMismatch(DeterministicSimulationBatchMismatch mismatch)
    {
        if (_disposed)
        {
            return;
        }

        Layout.Select(mismatch.TargetId);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.BatchMismatchNavigationStatus"),
            mismatch.TargetId,
            mismatch.ObservedTickIndex);
        _runtimeObservabilityJournal.Log(
            "Batch",
            $"First mismatch selected · {mismatch.EvidenceKind} · {mismatch.TargetId} · Tick {mismatch.ObservedTickIndex}");
    }

    private void OnScenarioBatchPresentationChanged(bool invalidateCommands)
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsBatchRunning));
        OnPropertyChanged(nameof(IsScenarioConfigurationEnabled));
        OnPropertyChanged(nameof(CanValidateMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(IsCommissioningValidationConfigurationEnabled));
        OnPropertyChanged(nameof(BatchCompletedRuns));
        OnPropertyChanged(nameof(IsBatchCancellationRequested));
        OnPropertyChanged(nameof(BatchStatusText));
        OnPropertyChanged(nameof(BatchResultText));
        OnPropertyChanged(nameof(BatchBaselineText));
        OnPropertyChanged(nameof(BatchArtifactStatusText));
        OnPropertyChanged(nameof(BatchAssertionOutcomes));
        OnPropertyChanged(nameof(HasBatchAssertionOutcomes));
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanExportUnifiedCommissioningEvidence));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
        OnPropertyChanged(nameof(CanAcceptBatchBaseline));
        OnPropertyChanged(nameof(CanClearBatchBaseline));
        OnPropertyChanged(nameof(CanNavigateToBatchMismatch));
        OnPropertyChanged(nameof(CanExportSimulationEvidence));
        OnPropertyChanged(nameof(CanExportSimulationReport));
        OnPropertyChanged(nameof(CanImportSimulationEvidence));
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanStopTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
        _supportDiagnostics.Refresh();
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    #endregion

    #region Project Identity And Persistence

    private void RefreshProjectIdentity()
    {
        Title = IsStartupChoiceVisible ? "OpenVisionLab Machine Studio" : $"OpenVisionLab Machine Studio · {ProjectDisplayName}{(HasUnsavedChanges ? " *" : string.Empty)}";
        OnPropertyChanged(nameof(RecipeIdentityText));
        OnPropertyChanged(nameof(ProjectStatusText));
        OnPropertyChanged(nameof(ProjectSaveStateText));
        OnPropertyChanged(nameof(HasSavedProjectFile));
        OnPropertyChanged(nameof(ProjectRevisionText));
        OnPropertyChanged(nameof(EquipmentFooterStatusText));
        OnPropertyChanged(nameof(SceneTitleText));
    }

    private void InvalidateManualCameraPreparation()
        => _camera.InvalidatePreparation();

    private void CancelManualCameraPreparation()
        => _camera.CancelPreparation();

    private void MarkProjectChanged(bool requiresRuntimeRebuild = true)
    {
        _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
        InvalidateManualCameraPreparation();
        _projectLifecycle.MarkChanged();
        RecipeConnections.SetProjectRevision(_projectLifecycle.Revision);
        EquipmentMaterialFlow.Update(CurrentProject, Layout.Items, PresentationSnapshot);
        OnPropertyChanged(nameof(ProjectRevisionText));
        OnPropertyChanged(nameof(EquipmentFooterStatusText));
        _camera.VisionEvidence.RefreshContext();
        if (requiresRuntimeRebuild)
        {
            _runtimeDefinitionDirty = true;
            OnPropertyChanged(nameof(AreSimulationObservationsCurrent));
            RaiseCanExecuteChanged(_openSimulationObservationStepCommand);
            RaiseCanExecuteChanged(_openExperimentCameraInputCommand);
        }

        NotifyAxisCommissioningChanged(invalidateCommands: false);
        RefreshProjectDirtyState();
        _projectDiagnostics.NotifyCurrentProjectChanged();
    }

    private void RefreshProjectDirtyState()
    {
        if (_projectLifecycle.RefreshDirtyState())
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshProjectIdentity();
        }
    }

    private void AcceptCurrentProjectAsSaved()
    {
        if (_projectLifecycle.AcceptAsSaved())
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshProjectIdentity();
        }
    }

    private void OnProjectTransitionCompleted(ProjectLifecycleTransition transition)
    {
        if (_disposed)
        {
            return;
        }

        switch (transition.Kind)
        {
            case ProjectLifecycleTransitionKind.ProjectOpened:
                RefreshProjectIdentity();
                _camera.LoadProject(transition.Project, transition.Path);
                _scenarioBatch!.Restore();
                _multiAxisCommissioning.Restore();
                _camera.RestoreEvidence();
                Navigation.HideStartupChoice();
                if (transition.LoadResult?.IsRecoveredFromBackup == true)
                {
                    StatusMessage = OpenVisionLanguageService.T(
                        "Project.RecoveredFromBackupStatus",
                        "백업 파일(.bak)에서 프로젝트를 열었습니다. 원본 파일은 변경하지 않았습니다. 다른 이름으로 저장(Ctrl+Shift+S)하여 복구 사본을 만드세요.",
                        "Project opened from its .bak backup. The primary file was not changed. Use Save As (Ctrl+Shift+S) to create a recovery copy.");
                    _runtimeObservabilityJournal.Log(
                        "Project",
                        $"Opened from backup · source={transition.LoadResult.SourcePath} · reason={transition.LoadResult.RecoveryReason}");
                }
                else
                {
                    _runtimeObservabilityJournal.Log("Project", $"Opened {transition.Project.Name}");
                }
                break;
            case ProjectLifecycleTransitionKind.NewProjectCreated:
                RefreshProjectIdentity();
                _camera.SetProjectPath(null, isSaved: true);
                _camera.ClearEvidence();
                Navigation.EnterEquipmentAuthoring();
                _runtimeObservabilityJournal.Log("Project", "Created new project");
                break;
            case ProjectLifecycleTransitionKind.BundledSampleOpened:
                RefreshProjectIdentity();
                _camera.SetProjectPath(null, isSaved: true);
                Navigation.HideStartupChoice();
                Navigation.SelectedLeftToolTabIndex = 0;
                StatusMessage = OpenVisionLanguageService.T("Scene.ExampleOpenedStoppedStatus");
                _runtimeObservabilityJournal.Log("Project", $"Opened bundled sample · {transition.Project.Name}");
                InvalidateCommands();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition.Kind), transition.Kind, null);
        }

        _projectDiagnostics.NotifyProjectLoaded(transition.LoadResult);
        _projectDiagnostics.Refresh();
    }

    private void OnProjectSaveCompleted(ProjectSaveLifecycleResult result)
    {
        if (_disposed)
        {
            return;
        }

        if (!result.Applied)
        {
            RefreshProjectDirtyState();
            RefreshProjectIdentity();
            _projectDiagnostics.Refresh();
            _runtimeObservabilityJournal.Log("Project", $"Save completed for a stale document revision · {result.Receipt.Revision}");
            return;
        }

        RefreshProjectIdentity();
        _camera.SetProjectPath(CurrentProjectPath, isSaved: true);
        _camera.CaptureEditor.NotifyProjectSaved();
        AcceptCurrentProjectAsSaved();
        _projectDiagnostics.NotifyProjectLoaded(null);
        _projectDiagnostics.Refresh();
        _runtimeObservabilityJournal.Log("Project", $"Saved {CurrentProject.Name}");
    }

    private void HandleProjectSaveFailure(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        StatusMessage = OpenVisionLanguageService.T(
            "Project.SaveFailedStatus",
            "프로젝트를 저장하지 못했습니다",
            "The project could not be saved");
        _runtimeObservabilityJournal.Log("Project", $"Save failed · {exception.Message}");
        _mainMessageDialogHost.ShowProjectSaveFailure(exception.Message);
    }

    internal Task<bool> TryResolveUnsavedChangesAsync() => TryResolvePendingPlacementDraft()
        ? _projectLifecycle.TryResolveUnsavedChangesAsync()
        : Task.FromResult(false);

    private void HandleProjectOpenFailure(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        StatusMessage = OpenVisionLanguageService.T(
            "Project.OpenFailedStatus",
            "프로젝트를 열지 못했습니다",
            "The project could not be opened");
        _runtimeObservabilityJournal.Log("Project", $"Open failed · {exception.Message}");
        ProjectOpenFailurePresenter(CreateProjectOpenFailureDetail(exception));
    }

    private static string CreateProjectOpenFailureDetail(Exception exception) => exception switch
    {
        ProjectDocumentLoadException
        {
            ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema,
            ProjectSchema: not null
        } loadException => string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Project.OpenFailedUnsupportedSchemaDetail",
                "프로젝트 스키마 '{0}'은(는) 지원되지 않습니다. 지원되는 최신 스키마는 '{1}'입니다.",
                "Project schema '{0}' is not supported. The latest supported schema is '{1}'."),
            loadException.ProjectSchema,
            MachineProjectDocument.CurrentSchema),
        ProjectDocumentLoadException or JsonException => OpenVisionLanguageService.T(
            "Project.OpenFailedInvalidFileDetail",
            "파일 내용이 올바른 Machine Studio 프로젝트가 아닙니다.",
            "The file content is not a valid Machine Studio project."),
        FileNotFoundException or DirectoryNotFoundException => OpenVisionLanguageService.T(
            "Project.OpenFailedNotFoundDetail",
            "프로젝트 파일을 찾을 수 없습니다.",
            "The project file could not be found."),
        UnauthorizedAccessException => OpenVisionLanguageService.T(
            "Project.OpenFailedAccessDetail",
            "프로젝트 파일을 읽을 권한이 없습니다.",
            "The project file cannot be read with the current permissions."),
        IOException => OpenVisionLanguageService.T(
            "Project.OpenFailedReadDetail",
            "프로젝트 파일을 읽는 동안 파일 시스템 오류가 발생했습니다.",
            "A file-system error occurred while reading the project file."),
        _ => throw new ArgumentOutOfRangeException(nameof(exception))
    };

    #endregion

    #region Evidence Presentation

    private void RaiseUnifiedCommissioningEvidencePresentationChanged()
    {
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanExportUnifiedCommissioningEvidence));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
        RaiseCanExecuteChanged(_exportUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_importUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_exportSupportDiagnosticsCommand);
    }

    private void ResetUnifiedCommissioningEvidenceForTraceCapture()
        => _unifiedCommissioningEvidence.Reset();

    #endregion

    #region Scenario Runtime Coordination

    private async Task<bool> PauseRuntimeForScenarioBatchAsync()
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            var command = new PauseCommand();
            var result = await _simulationSession.Engine.EnqueueCommandAsync(command);
            if (_disposed)
            {
                return false;
            }

            if (!result.IsAccepted)
            {
                _runtimeObservabilityJournal.Log("Batch", $"Main runtime pause rejected · {result.ErrorCode}: {result.Detail}");
                return false;
            }

            IsRunning = false;
            _runtimeObservabilityJournal.Log("Batch", $"Main runtime paused before sequential batch · {ShortCommandId(command)}");
            return true;
        }
        catch (OperationCanceledException) when (_simulationSession.RuntimeLoop.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void ApplyDesignMode(bool value)
    {
        if (!SetProperty(ref _isDesignMode, value, nameof(IsDesignMode)))
        {
            return;
        }

        _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
        OnPropertyChanged(nameof(IsRunMode));
        OnPropertyChanged(nameof(IsSceneEditable));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ControlOwnerText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SimulationSpeedText));
        OnPropertyChanged(nameof(SimulationEngineStateText));
        OnPropertyChanged(nameof(CurrentRunStatusText));
        OnPropertyChanged(nameof(RunStateCaptionText));
        OnPropertyChanged(nameof(HasRecipeExecutionStatus));
        OnPropertyChanged(nameof(RecipeExecutionStatusText));
        OnPropertyChanged(nameof(RunStatusText));
        OnPropertyChanged(nameof(RunTargetText));
        OnPropertyChanged(nameof(RunTargetSummaryText));
        OnPropertyChanged(nameof(SceneControlText));
        OnPropertyChanged(nameof(LeftPanelHeaderText));
        OnPropertyChanged(nameof(RightPanelHeaderText));
        if (!value)
        {
            _recipeAuthoring.ExitPlayback();
        }
        Layout.IsEditable = IsSceneEditable;
        RecipeConnections.IsEditable = value;
        SequenceEditor.IsEditable = value;
        if (value)
        {
            SequenceEditor.ApplyRuntimeStep(null, null);
        }
        UpdateRunToolAvailability();
        if (_manualEquipment.HasSelectedManualEquipment)
        {
            RefreshManualEquipmentProjection(invalidateCommands: false);
        }
        if (_camera.HasVirtualCamera)
        {
            RefreshCameraCommissioningProjection(invalidateCommands: false);
        }
        NotifyModeDependentCommandsChanged();
        InvalidateModeCommands();
        DigitalIo.InvalidateCommands();
        FaultManager.InvalidateCommands();
        RuntimeDebugger.InvalidateCommands();
    }

    private void BeginDesignModeTransition()
    {
        _isModeTransitioning = true;
        _pendingDesignMode = true;
        var generation = ++_modeTransitionGeneration;
        _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
        _simulationSession.RunControl.InvalidatePendingExecution();
        OnPropertyChanged(nameof(IsModeTransitioning));
        OnPropertyChanged(nameof(CanNavigateSequenceUnits));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ModeTransitionStatusText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SimulationSpeedText));
        OnPropertyChanged(nameof(SimulationEngineStateText));
        OnPropertyChanged(nameof(CurrentRunStatusText));
        OnPropertyChanged(nameof(RunStateCaptionText));
        OnPropertyChanged(nameof(HasRecipeExecutionStatus));
        OnPropertyChanged(nameof(RecipeExecutionStatusText));
        StatusMessage = GetModeTransitionStatusText();
        InvalidateCommands();
        _ = CompleteDesignModeTransitionAsync(generation);
    }

    private async Task CompleteDesignModeTransitionAsync(int generation)
    {
        var resetAccepted = false;
        var timedOut = false;
        Exception? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(RuntimeShutdownTimeout);
            resetAccepted = await _simulationSession.RunControl
                .ResetForDesignModeAsync(timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (_disposed || _simulationSession.RuntimeLoop.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            _simulationSession.RunControl.InvalidatePendingExecution();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (_disposed || generation != _modeTransitionGeneration)
        {
            return;
        }

        _isModeTransitioning = false;
        OnPropertyChanged(nameof(IsModeTransitioning));
        OnPropertyChanged(nameof(CanNavigateSequenceUnits));
        OnPropertyChanged(nameof(IsDesignMode));
        OnPropertyChanged(nameof(IsRunMode));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ModeTransitionStatusText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SimulationSpeedText));
        OnPropertyChanged(nameof(SimulationEngineStateText));
        OnPropertyChanged(nameof(CurrentRunStatusText));
        OnPropertyChanged(nameof(RunStateCaptionText));
        OnPropertyChanged(nameof(HasRecipeExecutionStatus));
        OnPropertyChanged(nameof(RecipeExecutionStatusText));

        if (failure is not null)
        {
            StatusMessage = OpenVisionLanguageService.T(
                "Shell.EditResetTransitionFailed",
                "설계 모드 전환 실패: 실행 종료·초기화 중 오류가 발생했습니다.",
                "Design mode transition failed: the reset operation raised an error.");
            _runtimeObservabilityJournal.Log(
                "Simulation",
                $"Design mode transition failed · {failure.GetType().Name}: {failure.Message}");
            InvalidateCommands();
            return;
        }

        if (!resetAccepted)
        {
            StatusMessage = timedOut
                ? OpenVisionLanguageService.T(
                    "Shell.EditResetTransitionTimeout",
                    "설계 모드 전환 실패: 시뮬레이션 종료·초기화 시간이 초과되었습니다.",
                    "Design mode transition failed: simulation reset timed out.")
                : OpenVisionLanguageService.T(
                    "Shell.EditResetTransitionRejected",
                    "설계 모드 전환 실패: 시뮬레이션 종료·초기화가 거부되었습니다.",
                    "Design mode transition failed: simulation reset was rejected.");
            InvalidateCommands();
            return;
        }

        if (_pendingDesignMode)
        {
            ApplyDesignMode(true);
            StatusMessage = OpenVisionLanguageService.T(
                "Shell.EditResetTransitionCompleted",
                "실행을 끝내고 초기화했습니다. 설정을 수정한 뒤 새로 시운전하세요.",
                "The run ended and was reset. Edit the settings before starting a new test.");
        }
        else
        {
            StatusMessage = OpenVisionLanguageService.T(
                "Shell.EditResetTransitionCanceled",
                "설계 모드 요청이 취소되었습니다. 실행은 종료·초기화된 상태입니다.",
                "The Design mode request was canceled. The run has ended and was reset.");
            InvalidateCommands();
        }
    }

    private string GetModeTransitionStatusText() => _pendingDesignMode
        ? OpenVisionLanguageService.T(
            "Shell.EditResetTransitionPending",
            "실행 종료·초기화 후 편집으로 전환 중",
            "Ending and resetting the run before editing")
        : OpenVisionLanguageService.T(
            "Shell.EditResetTransitionRunRequested",
            "설계 모드 요청이 취소되었습니다. 실행 종료·초기화를 완료하는 중입니다.",
            "Design mode request canceled; completing the run reset");

    #endregion

    #region Selection Synchronization And Sequence Presentation

    private void OnProjectTreeSelectionPresentationChanged(bool isAxisSelection)
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsMultiAxisCommissioningRecipeSelection));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        if (ProjectTree.SelectedNode?.Kind == TreeNodeKind.Unit)
        {
            Navigation.IsInspectorOpen = false;
        }
        if (isAxisSelection)
        {
            ApplyMonitorSnapshot(SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot);
            return;
        }

        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        InvalidateCommands();
    }

    private void OnLayoutSelectionPresentationChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (Navigation.IsEquipmentWorkspace && Layout.SelectionCount == 1) Navigation.IsInspectorOpen = true;
        OnPropertyChanged(nameof(ProjectRevisionText));
        OnPropertyChanged(nameof(EquipmentFooterStatusText));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(EquipmentSelectionStatusText));
        OnPropertyChanged(nameof(HasSelectedEquipment));
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        OnPropertyChanged(nameof(BulkMoveTitleText));
        if (Layout.SelectionCount != 1)
        {
            IsEquipmentSinglePartCheckOpen = false;
        }
        RaiseCanExecuteChanged(_openBulkMoveEditorCommand);
        RaiseCanExecuteChanged(_applyBulkMoveCommand);
        RaiseCanExecuteChanged(_openEquipmentCameraSettingsCommand);
        RaiseCanExecuteChanged(_openEquipmentPropertiesCommand);
        RaiseCanExecuteChanged(_openEquipmentDriveTabCommand);
        RaiseCanExecuteChanged(_openEquipmentSinglePartCheckCommand);
        RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
        RefreshManualEquipmentProjection(SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot);
        RefreshCameraCommissioningProjection();
        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        InvalidateCommands();
    }

    private void OnSelectionSynchronizationPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (args.PropertyName == nameof(ProjectSelectionSynchronizationWorkflow.AxisDriveTuningEditor))
        {
            OnPropertyChanged(nameof(AxisDriveTuningEditor));
            OnPropertyChanged(nameof(HasSelectedAxisDefinition));
        }
        else if (args.PropertyName == nameof(ProjectSelectionSynchronizationWorkflow.AnalogIoAuthoring))
        {
            OnPropertyChanged(nameof(AnalogIoAuthoring));
            OnPropertyChanged(nameof(HasSelectedAnalogChannel));
        }
    }

    private void OnLayoutDefinitionChanged()
    {
        if (_disposed)
        {
            return;
        }

        _recipeAuthoring.ExitPlayback();
        RecipeConnections.Load(CurrentProject, Layout.SelectedItem?.Id);
        SequenceEditor.RefreshAuthoringTargets();
        Properties.Show(Layout.SelectedItem?.Component);
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
        StatusMessage = "Layout changed; Simulation ON will rebuild the runtime";
    }

    private void OnAxisDefinitionChanged()
    {
        if (_disposed)
        {
            return;
        }

        _recipeAuthoring.ExitPlayback();
        MarkProjectChanged();
        _multiAxisCommissioning.InvalidateContextIfResult();
        UpdateRunToolAvailability();
        Properties.Show(AxisDriveTuningEditor is null
            ? null
            : CurrentProject.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, AxisDriveTuningEditor.Id, StringComparison.Ordinal)));
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        MultiAxisCommissioningRecipe.ApplyAxisSnapshots(
            (SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot).Axes);
        _multiAxisCommissioning.NotifyRuntimeChanged(invalidateCommands: false);
        StatusMessage = "Axis tuning changed; Simulation ON will validate and rebuild the runtime";
        InvalidateCommands();
    }

    private void OnAnalogChannelDefinitionChanged()
    {
        if (_disposed)
        {
            return;
        }

        _recipeAuthoring.ExitPlayback();
        MarkProjectChanged();
        UpdateRunToolAvailability();
        Properties.ShowNode(ProjectTree.SelectedNode);
        StatusMessage = OpenVisionLanguageService.T(
            "Io.AnalogAuthoringChangedStatus",
            "아날로그 InitialValue가 변경되었습니다. Simulation ON 전에 저장하세요.",
            "Analog InitialValue changed. Save before turning Simulation ON.");
        InvalidateCommands();
    }

    private void OnMultiAxisCommissioningRecipeChanged()
    {
        if (_disposed)
        {
            return;
        }

        MarkProjectChanged(requiresRuntimeRebuild: false);
        Properties.ShowNode(ProjectTree.SelectedNode);
        NotifyMultiAxisCommissioningRecipeChanged(recipeChanged: true);
        StatusMessage = "Multi-axis commissioning recipe changed; save the project to retain it";
    }

    private void OnSequenceEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || _isApplyingProject) return;
        if (args.PropertyName is nameof(SequenceEditorViewModel.SelectedSequence) or nameof(SequenceEditorViewModel.SelectedStep)
            or nameof(SequenceEditorViewModel.SelectedStepDraft) or nameof(SequenceEditorViewModel.IsStepDraftEditable)
            or nameof(SequenceEditorViewModel.HasPendingStepDraft)) RaiseCanExecuteChanged(_openStepAxisSettingsCommand);
        if (args.PropertyName == nameof(SequenceEditorViewModel.SelectedSequence)) OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
        if (args.PropertyName == nameof(SequenceEditorViewModel.IsStepTestBusy))
        {
            OnPropertyChanged(nameof(CanNavigateSequenceUnits));
            RunHistory?.InvalidateCommands();
        }
        if (args.PropertyName == nameof(SequenceEditorViewModel.SelectedStep) && Navigation.IsSequenceWorkspace && SequenceEditor.HasSelectedStep)
        {
            Navigation.IsInspectorOpen = true;
            _selectionSynchronization.SelectSequenceStepTarget();
        }
    }

    private void SelectSequenceNavigationItem(string? key)
    {
        if (key is null || key == SelectedSequenceNavigationKey) return;
        var item = SequenceNavigationItems.FirstOrDefault(candidate => candidate.Key == key);
        if (item is null || !CanNavigateSequenceUnits || !Navigation.IsSequenceWorkspace || !TryResolvePendingPlacementDraft())
        {
            OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
            return;
        }

        var sequence = CurrentProject.Sequences.FirstOrDefault(candidate => candidate.Id == item.SequenceId);
        _selectionSynchronization.ClearSelection();
        if (item.UnitId is { } unitId)
        {
            Layout.ShowUnit(unitId);
            Properties.Show(CurrentProject.Stations.SelectMany(station => station.Units).First(unit => unit.Id == unitId));
        }
        else
        {
            Layout.ShowOverview();
            Properties.Show(sequence);
        }
        SequenceEditor.SelectedSequence = sequence;
        Navigation.IsInspectorOpen = true;
        IsBulkMoveEditorOpen = false;
        OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(EquipmentSelectionStatusText));
    }

    private void OnSequenceDefinitionChanged(
        object? sender,
        SequenceEditorChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _recipeAuthoring.ExitPlayback();
        MarkProjectChanged();
        UpdateRunToolAvailability();
        if (args.StructureChanged)
        {
            ProjectTree.LoadProject(CurrentProject);
        }
        RecipeConnections.RefreshDefinitionPreservingProcessBlockPlan(Layout.SelectedItem?.Id);
        Layout.RefreshSequenceReferences();
        Camera.NotifyPresentationChanged();
        OnPropertyChanged(nameof(SequenceNavigationItems));
        OnPropertyChanged(nameof(SelectedSequenceNavigationKey));
        RuntimeDebugger.LoadProject(CurrentProject, resetSession: false);

        if (!_isApplyingProject && Navigation.IsSequenceWorkspace) _selectionSynchronization.SelectSequenceStepTarget();
        StatusMessage = "Sequence changed; Simulation ON will validate and rebuild the runtime";
        OnPropertyChanged(nameof(RunTargetText));
        OnPropertyChanged(nameof(RunTargetSummaryText));
        OnPropertyChanged(nameof(HasEmbeddedSequence));
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
        InvalidateCommands();
    }

    private string ResolveSequenceName(string? sequenceId)
    {
        if (sequenceId is null)
        {
            return HasEmbeddedSequence
                ? LocalizeSequenceName(CurrentProject.Sequences[0])
                : OpenVisionLanguageService.T(
                    "Shell.NoSequenceConfigured",
                    "시퀀스가 설정되지 않았습니다",
                    "No sequence configured");
        }

        var sequence = CurrentProject.Sequences.FirstOrDefault(item => item.Id == sequenceId);
        return sequence is null ? sequenceId : LocalizeSequenceName(sequence);
    }

    private string ResolveStepName(string? sequenceId, string? stepId)
    {
        if (stepId is null)
        {
        return RuntimeProjection.CurrentSequence?.Status == SequenceExecutionStatus.Completed
                ? OpenVisionLanguageService.T("Sequence.Complete", "완료", "Complete")
                : OpenVisionLanguageService.T("Shell.Unavailable");
        }

        var sequence = CurrentProject.Sequences.FirstOrDefault(item => item.Id == sequenceId);
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

    #endregion

    #region Integration Context

    private void RefreshIntegrationContext() => Integration.RefreshSourceContext();

    #endregion

    #region Integration And Camera

    private async Task<bool> PrepareAutomaticExternalInspectionAsync(
        CancellationToken cancellationToken)
    {
        var useThreeDHeightMap = Integration.Setup.UseThreeDHeightMap;
        var automaticRun = CurrentProject.Simulation.AutomaticRun;
        if (automaticRun is null
            || string.IsNullOrWhiteSpace(automaticRun.SequenceId))
        {
            return false;
        }

        var sequence = CurrentProject.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, automaticRun.SequenceId, StringComparison.Ordinal));
        var triggerSteps = sequence?.Steps
            .Where(step => step.Action == SequenceStepAction.TriggerCamera)
            .ToArray()
            ?? Array.Empty<SequenceStepDefinition>();
        if (sequence is null || triggerSteps.Length != 1)
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                $"Automatic external inspection requires exactly one direct TriggerCamera step in sequence '{automaticRun.SequenceId}'.");
            return false;
        }

        var trigger = triggerSteps[0];
        _camera.SelectedCameraId = trigger.TargetId;
        _camera.SelectedCameraRecipe = trigger.Parameter?.Trim() ?? string.Empty;
        if (_disposed || !Integration.CanPrepareAutomaticExternalInspection)
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                useThreeDHeightMap
                    ? "Automatic external inspection was not prepared because the saved 3D HeightMap setup is incomplete."
                    : "Automatic external inspection was not prepared because the saved 2D setup is incomplete.");
            return false;
        }

        if (useThreeDHeightMap
            && (!string.Equals(Integration.Setup.ThreeDSequenceId, sequence.Id, StringComparison.Ordinal)
                || !string.Equals(Integration.Setup.ThreeDStepId, trigger.Id, StringComparison.Ordinal)
                || !string.Equals(Integration.Setup.ThreeDDeviceId, trigger.TargetId, StringComparison.Ordinal)))
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                $"3D HeightMap setup binding does not match sequence '{sequence.Id}', step '{trigger.Id}', device '{trigger.TargetId}'.");
            return false;
        }

        var waitVision = sequence.Steps.FirstOrDefault(step =>
            step.Action == SequenceStepAction.WaitVisionResult
            && string.Equals(step.TargetId, trigger.TargetId, StringComparison.Ordinal));
        if (waitVision is null)
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                $"Sequence '{sequence.Id}' has no WaitVisionResult step for camera '{trigger.TargetId}'.");
            return false;
        }

        var projectPath = CurrentProjectPath;
        var cameraDefinition = CurrentProject.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera
            && string.Equals(device.Id, trigger.TargetId, StringComparison.Ordinal));
        var sourceDefinition = cameraDefinition?.Camera?.SingleImageSource;
        if (string.IsNullOrWhiteSpace(projectPath)
            || cameraDefinition?.Camera is not { } cameraSettings
            || (!useThreeDHeightMap && sourceDefinition is not { } && cameraSettings.NumericalInput is null)
            || string.IsNullOrWhiteSpace(trigger.Parameter))
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                "Automatic external inspection requires a saved project, camera source, and recipe.");
            return false;
        }

        var numericalSettings = cameraSettings.NumericalInput;
        if (numericalSettings is not null
            && (useThreeDHeightMap != (numericalSettings.InputKind == NumericalCameraInputKind.HeightMap3D)
                || numericalSettings.ConfirmedVersion != numericalSettings.Version
                || numericalSettings.RecipeName != trigger.Parameter.Trim()))
        {
            _runtimeObservabilityJournal.Log("Vision", "생성 입력 종류 또는 확인한 촬영 레시피가 외부 검사 설정과 일치하지 않습니다.");
            return false;
        }

        MachineIntegrationHeightMapSourceDefinition? heightMapSource = null;
        if (useThreeDHeightMap && numericalSettings is null
            && !Integration.Setup.TryGetThreeDHeightMapSource(projectPath, out heightMapSource))
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                "The saved 3D HeightMap source is missing, outside the project, or no longer matches its saved hash.");
            return false;
        }

        var snapshot = _simulationSession.Engine.CurrentSnapshot;
        var cameraSnapshot = snapshot.Cameras.FirstOrDefault(camera =>
            string.Equals(camera.Id, trigger.TargetId, StringComparison.Ordinal));
        if (cameraSnapshot is null
            || !string.Equals(snapshot.ProjectId, CurrentProject.Id, StringComparison.Ordinal))
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                "Automatic external inspection source preparation did not match the current runtime.");
            return false;
        }

        // The selected camera follows the designated automatic step so the
        // existing integration request owner can reuse its normal context.
        _camera.SelectedCameraId = trigger.TargetId;
        _camera.SelectedCameraRecipe = trigger.Parameter.Trim();
        VirtualCameraExternalSource? source = null;
        string sourceSha256;
        if (numericalSettings is not null)
        {
            sourceSha256 = "촬영 시 생성 · LU 수치 모의 원본";
        }
        else if (useThreeDHeightMap)
        {
            var configuredSource = heightMapSource!;
            source = new VirtualCameraExternalSource(
                configuredSource.SourceRelativePath,
                configuredSource.Evidence.ContentSha256,
                configuredSource.Evidence.ContentLength,
                configuredSource.Width,
                configuredSource.Height,
                configuredSource.PixelFormat);
            sourceSha256 = configuredSource.Evidence.ContentSha256;
        }
        else
        {
            var request = new VirtualCameraInspectionRequest(
                projectPath,
                trigger.TargetId,
                trigger.Parameter.Trim(),
                cameraSnapshot.AcquisitionOrdinal,
                cameraSettings.PlaceholderDecision,
                sourceDefinition!.SourceRelativePath,
                sourceDefinition.Width,
                sourceDefinition.Height,
                sourceDefinition.PixelFormat,
                snapshot.TickIndex,
                snapshot.SimulationTime,
                CurrentProject.Simulation.Seed,
                snapshot.Axes.ToDictionary(
                    axis => axis.Id,
                    axis => axis.Position,
                    StringComparer.Ordinal));

            VirtualFrameDescriptor frame = await _camera
                .AcquireFrameAsync(request, cancellationToken)
                .ConfigureAwait(true);
            source = new VirtualCameraExternalSource(
                frame.SourceRelativePath,
                frame.ContentSha256,
                frame.ContentLength,
                frame.Width,
                frame.Height,
                frame.PixelFormat);
            sourceSha256 = frame.ContentSha256;
        }
        var armCommand = new ArmAutomaticExternalInspectionCommand(
            new SimulationRuntimeIdentity(snapshot.ProjectId, snapshot.RuntimeGeneration),
            sequence.Id,
            source is null ? new Dictionary<string, VirtualCameraExternalSource>()
                : new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal) { [trigger.TargetId] = source },
            numericalSettings is null ? null : new[] { trigger.TargetId });
        var result = await _simulationSession.Engine
            .EnqueueCommandAsync(armCommand, cancellationToken)
            .ConfigureAwait(true);
        if (!result.IsAccepted)
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                $"Automatic external inspection arming was rejected · {result.ErrorCode}: {result.Detail}");
            return false;
        }

        _runtimeObservabilityJournal.Log(
            "Vision",
            $"Automatic external inspection source armed · modality={(useThreeDHeightMap ? "ThreeD/HeightMap" : "TwoD/Image")} · camera={trigger.TargetId} · SHA-256={sourceSha256}.");
        return true;
    }

    private async Task PublishAutomaticExternalInspectionAsync()
    {
        if (Interlocked.Exchange(ref _automaticExternalPublishInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            if (!await WaitForAutomaticExternalRequestSnapshotAsync().ConfigureAwait(true))
            {
                _runtimeObservabilityJournal.Log(
                    "Vision",
                    "Automatic external inspection request was not visible in the current runtime snapshot.");
                await _simulationSession.RunControl.AbortSequenceAsync();
                return;
            }

            var observation = await Integration
                .PublishAutomaticExternalInspectionAsync()
                .ConfigureAwait(true);
            if (observation.Outcome != MachineIntegrationParticipantOutcome.Completed)
            {
                _runtimeObservabilityJournal.Log(
                    "Vision",
                    $"Automatic external inspection Handoff was not published · {observation.Outcome}.");
                await _simulationSession.RunControl.AbortSequenceAsync();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _runtimeObservabilityJournal.Log(
                "Vision",
                $"Automatic external inspection Handoff failed · {exception.Message}");
            await _simulationSession.RunControl.AbortSequenceAsync();
        }
        finally
        {
            Volatile.Write(ref _automaticExternalPublishInFlight, 0);
        }
    }

    private async Task<bool> WaitForAutomaticExternalRequestSnapshotAsync()
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var snapshot = _simulationSession.Engine.CurrentSnapshot;
            if (snapshot.RunMode == SimulationRunMode.Paused
                && snapshot.AutomaticRun.IsActive
                && snapshot.Cameras.Any(camera =>
                    camera.State == VirtualCameraState.AwaitingExternalResult))
            {
                return true;
            }

            if (_disposed || _simulationSession.IsShutdownRequested)
            {
                return false;
            }

            await Task.Delay(1).ConfigureAwait(true);
        }

        return false;
    }

    private MachineIntegrationRequestContext CaptureIntegrationRequestContext()
    {
        var useThreeDHeightMap = Integration.Setup.UseThreeDHeightMap;
        var automaticSequenceId = _simulationSession.Engine.CurrentSnapshot.AutomaticRun.IsActive
            ? CurrentProject.Simulation.AutomaticRun?.SequenceId
            : null;
        MachineIntegrationHeightMapSourceDefinition? heightMapSource = null;
        MachineIntegrationArtifactEvidence? inspectionRecipeEvidence = null;
        if (useThreeDHeightMap && CurrentProjectPath is { } projectPath)
        {
            Integration.Setup.TryGetThreeDHeightMapSource(projectPath, out heightMapSource);
            Integration.Setup.TryGetThreeDInspectionRecipeEvidence(out inspectionRecipeEvidence);
        }

        var cameraSnapshot = RuntimeProjection.CurrentCamera;
        var sourceDefinition = CurrentCameraDefinition?.Camera?.SingleImageSource;
        if (cameraSnapshot?.NumericalInput is { IsExternalInspection: true } generated
            && cameraSnapshot.CurrentAcquisitionId == generated.Frame.FrameId)
        {
            var frame = generated.Frame;
            if (useThreeDHeightMap && frame.PixelFormat == "HeightMapFloat32LE")
                heightMapSource = Integration.Setup.CreateGeneratedHeightMapSource(frame);
            else if (!useThreeDHeightMap && frame.PixelFormat == "Gray8")
                sourceDefinition = new VirtualSingleImageSourceDefinition
                {
                    SourceRelativePath = frame.SourceRelativePath, Width = frame.Width, Height = frame.Height, PixelFormat = frame.PixelFormat
                };
        }

        return new(
            BuildIdentity.IsExactCommit,
            CurrentProject.Id,
            CurrentProject.Schema,
            CurrentProject.Sequences,
            CurrentProjectPath,
            _camera.SelectedCameraId,
            _camera.SelectedCameraRecipe,
            cameraSnapshot,
            sourceDefinition,
            heightMapSource,
            inspectionRecipeEvidence,
            useThreeDHeightMap ? Integration.Setup.ThreeDSequenceId : automaticSequenceId,
            useThreeDHeightMap ? Integration.Setup.ThreeDStepId : null,
            useThreeDHeightMap ? Integration.Setup.ThreeDDeviceId : null,
            _simulationSession.Engine.CurrentSnapshot.RuntimeGeneration);
    }

    private ManualCameraTriggerRequestInput CreateManualCameraTriggerRequestInput()
    {
        var cameraDefinition = CurrentCameraDefinition?.Camera;
        var sourceDefinition = cameraDefinition?.SingleImageSource;
        return new(
            CanTriggerCamera,
            _projectLifecycle.SessionId,
            _projectLifecycle.Revision,
            CurrentProject.Id,
            CurrentProject.Name,
            CurrentProjectPath,
            _projectLifecycle.SerializeForEvidence(),
            BuildIdentity.Current,
            SimulationFixedStep,
            _simulationSession.Engine.CurrentSnapshot,
            RuntimeProjection.CurrentCamera,
            _camera.SelectedCameraRecipe,
            cameraDefinition,
            sourceDefinition,
            CurrentProject.Simulation.Seed,
            Integration.Setup.WaitForExternalResult);
    }

    #endregion

    #region Axis Commissioning

    internal bool BeginAxisJog(AxisJogDirection direction) => AxisCommissioning.BeginAxisJog(direction);

    internal Task EndAxisJogAsync() => AxisCommissioning.EndAxisJogAsync();

    private async Task RunMultiAxisCommissioningRecipeAsync()
    {
        if (!CanRunMultiAxisCommissioningRecipe)
        {
            return;
        }

        var executionVersion = _multiAxisCommissioningExecutionWorkflow.ExecutionVersion;
        var sessionId = _projectLifecycle.SessionId;
        var revision = _projectLifecycle.Revision;
        var runtimeGeneration = _simulationSession.Engine.CurrentSnapshot.RuntimeGeneration;
        bool IsContextCurrent() => !_disposed && !_sessionCloseRequested && !_isApplyingProject
            && !_simulationSession.IsShutdownRequested && IsRunMode && !IsValidationBusy
            && _multiAxisCommissioningExecutionWorkflow.ExecutionVersion == executionVersion
            && _projectLifecycle.SessionId == sessionId && _projectLifecycle.Revision == revision
            && _simulationSession.Engine.CurrentSnapshot.RuntimeGeneration == runtimeGeneration;

        var result = await _multiAxisCommissioningExecutionWorkflow.ExecuteAsync(
            MultiAxisCommissioningRecipe.Targets.Select(target =>
                new AxisMoveTarget(target.AxisId, target.TargetPosition)), IsContextCurrent);
        if (result.Outcome == MultiAxisCommissioningExecutionOutcome.Interrupted || !IsContextCurrent())
        {
            return;
        }

        if (result.PausedBeforeExecution)
        {
            IsRunning = false;
        }

        if (result.Outcome == MultiAxisCommissioningExecutionOutcome.PauseRejected
            && result.RejectedCommand is not null)
        {
            var pause = result.RejectedCommand;
            _runtimeObservabilityJournal.Log("Motion", $"Recipe preparation rejected · {pause.ErrorCode}: {pause.Detail}");
            return;
        }

        if (result.IsAccepted)
        {
            IsRunning = true;
        }
    }

    private Task StopMultiAxisCommissioningRecipeAsync()
    {
        _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
        return _equipmentCommandDispatcher.DispatchAxisCommandAsync(
            new StopAxesCommand(MultiAxisCommissioningRecipe.Targets.Select(target => target.AxisId)),
            "Axis.ActionStopRecipe");
    }

    private void NavigateToCommissioningMismatch(DeterministicCommissioningMismatch mismatch)
    {
        if (_disposed)
        {
            return;
        }

        var stage = Layout.Items.FirstOrDefault(item =>
            string.Equals(item.BehaviorBindingId, mismatch.TargetId, StringComparison.Ordinal)
            || string.Equals(item.Id, mismatch.TargetId, StringComparison.Ordinal));
        if (stage is not null)
        {
            Layout.Select(stage.Id);
            Navigation.IsEquipmentWorkspace = true;
        }
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.RecipeMismatchNavigationStatus"),
            mismatch.TargetId,
            mismatch.TickIndex);
        _runtimeObservabilityJournal.Log("Motion", $"Commissioning mismatch selected · {mismatch.TargetId} · Tick {mismatch.TickIndex}");
    }

    private bool CanNavigateRuntimeError(object? parameter) => !_disposed && !IsRunning && !IsModeTransitioning
        && parameter is RuntimeSequenceErrorLocation location && location.ProjectId == CurrentProject.Id
        && CurrentProject.Sequences.Any(sequence => sequence.Id == location.Error.SequenceId
            && (location.Error.StepId is null || sequence.Steps.Any(step => step.Id == location.Error.StepId)));

    private void NavigateRuntimeError(object? parameter)
    {
        if (!CanNavigateRuntimeError(parameter) || parameter is not RuntimeSequenceErrorLocation location
            || !TryResolvePendingPlacementDraft()) return;
        var part = string.IsNullOrEmpty(location.TargetId) ? null : Layout.Items.FirstOrDefault(item =>
            item.Id == location.TargetId || item.BehaviorBindingId == location.TargetId);
        if (part is not null)
        {
            _selectionSynchronization.ClearSelection();
            if (string.IsNullOrEmpty(part.UnitId)) Layout.ShowOverview();
            else Layout.ShowUnit(part.UnitId);
            Layout.Select(part.Id);
        }
        Navigation.IsSimulationWorkspace = true;
        Navigation.SelectedExecutionTabIndex = 1;
        SequenceEditor.SelectSequence(location.Error.SequenceId);
        if (location.Error.StepId is not null) SequenceEditor.SelectStep(location.Error.SequenceId, location.Error.StepId);
        else SequenceEditor.SelectedStep = null;
        Navigation.IsInspectorOpen = true;
        StatusMessage = location.DisplayText;
    }

    #endregion

    #region Runtime Workspace

    private void OnSimulationWorkspacePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName == nameof(SimulationWorkspaceViewModel.ScheduledFaultKind))
        {
            OnPropertyChanged(nameof(ScheduledFaultTargets));
            SimulationWorkspace.EnsureScheduledFaultTarget(
                ScheduledFaultTargets.Select(target => target.Id));
        }
        if (!_isApplyingProject && !_runtimeProjectionCoordinator.IsApplyingProjection && e.PropertyName is
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
            SimulationWorkspace.SaveProjectScenario(CurrentProject.Simulation);
            MarkProjectChanged(requiresRuntimeRebuild: false);
        }
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
        InvalidateCommands();
    }

    #endregion

    #region Sequence Review

    private void OnProcessPlanReviewPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (args.PropertyName == nameof(ProcessPlanReviewViewModel.HasReturnContext))
        {
            OnPropertyChanged(nameof(HasProcessPlanReturnContext));
        }
        else if (args.PropertyName == nameof(ProcessPlanReviewViewModel.ReturnStepId))
        {
            OnPropertyChanged(nameof(ProcessPlanReturnStepId));
        }
        else if (args.PropertyName == nameof(ProcessPlanReviewViewModel.ReviewPositionText))
        {
            OnPropertyChanged(nameof(ProcessPlanReviewPositionText));
        }
    }

    private void OnSimulationCommandTracePropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        switch (args.PropertyName)
        {
            case nameof(SimulationCommandTraceViewModel.CanStartCapture):
                OnPropertyChanged(nameof(CanStartSimulationCommandTraceCapture));
                OnPropertyChanged(nameof(CanReplaySimulationCommandTrace));
                break;
            case nameof(SimulationCommandTraceViewModel.CanExportTrace):
                OnPropertyChanged(nameof(CanExportSimulationCommandTrace));
                break;
            case nameof(SimulationCommandTraceViewModel.EntryCount):
                OnPropertyChanged(nameof(SimulationCommandTraceEntryCount));
                OnPropertyChanged(nameof(CanExportUnifiedCommissioningEvidence));
                OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
                break;
            case nameof(SimulationCommandTraceViewModel.StatusText):
                OnPropertyChanged(nameof(SimulationCommandTraceStatusText));
                break;
            case nameof(SimulationCommandTraceViewModel.IsCaptureStarted):
                OnPropertyChanged(nameof(CanExportUnifiedCommissioningEvidence));
                OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
                break;
            case nameof(SimulationCommandTraceViewModel.LastReplaySucceeded):
                OnPropertyChanged(nameof(LastSimulationCommandTraceReplaySucceeded));
                break;
        }
    }

    #endregion

    #region Recipe Connection Authoring

    private void OnDryRunPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsDryRunPlaybackActive));
        OnPropertyChanged(nameof(AreSimulationObservationsCurrent));
        OnPropertyChanged(nameof(SimulationObservationUnavailableText));
        RaiseCanExecuteChanged(_openSimulationObservationStepCommand);
        OnPropertyChanged(nameof(IsSceneEditable));
        OnPropertyChanged(nameof(SceneSnapshotSource));
        EquipmentMaterialFlow.Update(CurrentProject, Layout.Items, PresentationSnapshot);
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
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
        if (e.PropertyName is nameof(RecipeDryRunPlaybackViewModel.IsActive)
            or nameof(RecipeDryRunPlaybackViewModel.CurrentStep))
        {
            InvalidateCommands();
        }
    }

    #endregion

    #region Commissioning Presentation

    private void NotifyAxisCommissioningChanged(
        bool invalidateCommands = true,
        bool modeOnly = false)
    {
        AxisCommissioning.ApplyProjection(
            new AxisCommissioningProjection(
                RuntimeProjection.CurrentAxis,
                CurrentAxisDefinition,
                HasSelectedAxisStage,
                IsRunMode,
                _isApplyingProject,
                IsValidationBusy,
                _runtimeDefinitionDirty,
                IsRunning,
                RuntimeProjection.ControlOwner,
                RuntimeProjection.AutomaticRun.IsActive,
                RuntimeProjection.CurrentSequence?.Status == SequenceExecutionStatus.Running),
            invalidateCommands);
        OnPropertyChanged(nameof(EquipmentInspectorPositionText));
        if (modeOnly)
        {
            OnPropertyChanged(nameof(CanMoveAxisAbsolute));
            OnPropertyChanged(nameof(CanMoveAxisRelative));
            OnPropertyChanged(nameof(CanMoveAxisVelocity));
            OnPropertyChanged(nameof(CanJogAxis));
        }
        else
        {
            OnPropertyChanged(nameof(CurrentAxisName));
            OnPropertyChanged(nameof(CurrentAxisStateText));
            OnPropertyChanged(nameof(CurrentAxisPositionText));
            OnPropertyChanged(nameof(CurrentAxisVelocityText));
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
        }
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void OnMultiAxisCommissioningPresentationChanged(bool invalidateCommands)
    {
        if (_disposed)
        {
            return;
        }

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
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifyMultiAxisCommissioningRecipeChanged(
        bool invalidateCommands = true,
        bool recipeChanged = false)
    {
        OnPropertyChanged(nameof(IsMultiAxisCommissioningRecipeSelection));
        OnPropertyChanged(nameof(HasMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(CanRunMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(CanStopMultiAxisCommissioningRecipe));
        if (recipeChanged)
        {
            _multiAxisCommissioning.NotifyRecipeChanged(invalidateCommands);
        }
        else
        {
            _multiAxisCommissioning.NotifyRuntimeChanged(invalidateCommands);
        }
    }

    private void NotifyCameraCommissioningChanged(bool invalidateCommands = true)
    {
        _camera.RefreshProjection(invalidateCommands: false);
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
        RefreshIntegrationContext();
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void NotifyManualCommissioningChanged(bool invalidateCommands = true)
    {
        RefreshManualEquipmentProjection(invalidateCommands: false);
        OnPropertyChanged(nameof(HasSelectedAxisStage));
        OnPropertyChanged(nameof(HasSelectedManualEquipment));
        OnPropertyChanged(nameof(CanStartManualEquipmentControl));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    #endregion

    #region Command Availability Presentation

    private void NotifyModeDependentCommandsChanged()
    {
        Navigation.InvalidateCommands();
        if (HasSelectedAxisDefinition || HasSelectedAxisStage)
        {
            NotifyAxisCommissioningChanged(invalidateCommands: false, modeOnly: true);
        }
        if (HasMultiAxisCommissioningRecipe)
        {
            OnPropertyChanged(nameof(CanRunMultiAxisCommissioningRecipe));
            OnPropertyChanged(nameof(CanStopMultiAxisCommissioningRecipe));
            OnPropertyChanged(nameof(CanValidateMultiAxisCommissioningRecipe));
        }
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
        RuntimeDebugger.SetEnabled(isEnabled, invalidateCommands: false);
    }

    #endregion

    #region Virtual Camera Workflow

    private void ShowCameraEquipmentLocation()
    {
        var part = Layout.Items.FirstOrDefault(item => item.Component?.Kind == LayoutComponentKind.Camera && item.BehaviorBindingId == _camera.SelectedCameraId);
        if (part is null || !TryResolvePendingPlacementDraft()) return;
        Navigation.IsEquipmentWorkspace = true;
        if (!Navigation.IsEquipmentWorkspace) return;
        _selectionSynchronization.ClearSelection();
        if (string.IsNullOrEmpty(part.UnitId)) Layout.ShowOverview();
        else Layout.ShowUnit(part.UnitId);
        Layout.Select(part.Id);
    }

    private void RefreshVirtualCameraWorkflowPresentation(string cameraId)
    {
        ApplyProjectPresentation(CurrentProject);
        RuntimeDebugger.LoadProject(CurrentProject, resetSession: false);
        _layoutAuthoring.Reset();
        _camera.SelectedCameraId = cameraId;
    }

    #endregion

    #region Runtime Command And Logging

    private async Task<SimulationCommandResult> DispatchRuntimeDebuggerCommandAsync(
        SimulationCommand command)
        => await _simulationCommandPresentationDispatcher.DispatchRuntimeDebuggerAsync(
            command,
            () => ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot));

    internal void AppendLog(TimeSpan time, string category, string message)
    {
        _runtimeObservabilityJournal.Append(time, category, message);
        _supportDiagnostics.Refresh();
    }

    private SupportDiagnosticBundleRequest CaptureSupportDiagnosticBundleRequest()
    {
        var errors = new List<SupportDiagnosticError>();
        var queue = new List<SupportDiagnosticQueueObservation>();
        var artifacts = new List<SupportDiagnosticArtifact>();

        foreach (var item in ProjectDiagnostics.Report?.Items
                     .Where(item => item.Severity == ProjectDocumentDiagnosticSeverity.Error)
                     ?? [])
        {
            errors.Add(new(
                item.Code.ToString(),
                item.Detail,
                item.RelatedPath));
        }

        foreach (var diagnostic in OperationalDiagnostics.TakeLast(50))
        {
            var detail = string.Join(
                " · ",
                new[]
                {
                    diagnostic.ExceptionType,
                    diagnostic.ExceptionMessage,
                    diagnostic.ExceptionDetail
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (diagnostic.Severity == SimulationLogSeverity.Alarm
                || !string.IsNullOrWhiteSpace(detail)
                || diagnostic.EventName.Contains("error", StringComparison.OrdinalIgnoreCase)
                || diagnostic.EventName.Contains("fail", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new(diagnostic.EventName, diagnostic.Message, detail));
            }

            if (IsQueueOrTimeoutDiagnostic(diagnostic))
            {
                queue.Add(new(
                    diagnostic.EventName,
                    diagnostic.Category ?? "runtime",
                    IsTimeoutDiagnostic(diagnostic),
                    null,
                    diagnostic.Message));
            }
        }

        foreach (var diagnostic in Integration.TransactionDiagnostics.TakeLast(50))
        {
            queue.Add(new(
                diagnostic.TransactionId is { } id
                    ? $"transaction-{id:N}"
                    : "transaction-unknown",
                diagnostic.State.ToString(),
                diagnostic.State == MachineIntegrationTransactionState.Invalid,
                null,
                $"artifacts={diagnostic.ArtifactCount}; bytes={diagnostic.MaterializedBytes}/{diagnostic.DeclaredBytes}"));
        }

        AddIntegrationReadError(errors, "transaction-diagnostics", Integration.TransactionDiagnosticReadError);
        AddIntegrationReadError(errors, "acknowledgement", Integration.AcknowledgementReadError);
        AddIntegrationReadError(errors, "result", Integration.ResultReadError);

        if (Integration.IsBusy)
        {
            queue.Add(new("integration", "busy"));
        }

        if (Integration.Setup.WaitForExternalResult)
        {
            queue.Add(new("external-result", "waiting-enabled"));
        }

        if (_scenarioBatch?.IsBatchRunning == true)
        {
            queue.Add(new(
                "scenario-batch",
                "running",
                BatchStatusText.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                    || BatchStatusText.Contains("시간 초과", StringComparison.Ordinal)));
        }

        if (CurrentProjectPath is not null)
        {
            artifacts.Add(new("project/current-project.ovmachine", "project"));
        }

        if (ProjectDiagnostics.Report is not null)
        {
            artifacts.Add(new("diagnostics/project-report.json", "diagnostic"));
        }

        if (OperationalDiagnostics.Count > 0)
        {
            artifacts.Add(new("diagnostics/runtime-journal.json", "diagnostic"));
        }

        if (Integration.TransactionDiagnostics.Count > 0)
        {
            artifacts.Add(new("integration/transaction-diagnostics.json", "diagnostic"));
        }

        foreach (var transaction in Integration.TransactionHistory.Take(20))
        {
            var transactionPath = $"integration/transactions/{transaction.Handoff.TransactionId:D}";
            artifacts.Add(new($"{transactionPath}/handoff.json", "integration"));
            if (transaction.HasAcknowledgement)
            {
                artifacts.Add(new($"{transactionPath}/ack.json", "integration"));
            }
            if (transaction.HasResult)
            {
                artifacts.Add(new($"{transactionPath}/result.json", "integration"));
            }
        }

        if (_camera.VisionEvidence.LatestEvidence is not null)
        {
            artifacts.Add(new("evidence/vision-result.json", "vision-evidence"));
        }

        if (_scenarioBatch?.LatestBatchResult is not null)
        {
            artifacts.Add(new("simulation/scenario-batch-result.json", "simulation-result"));
        }

        if (_unifiedCommissioningEvidence.LatestEvidence is not null)
        {
            artifacts.Add(new("simulation/unified-commissioning-evidence.json", "simulation-result"));
        }

        var sensitivePaths = new List<string>();
        AddSensitivePath(sensitivePaths, CurrentProjectPath);
        AddSensitivePath(sensitivePaths, Integration.Setup.ExchangeRoot);
        AddSensitivePath(sensitivePaths, Integration.Setup.InspectionRecipePath);
        var source = CurrentCameraDefinition?.Camera?.SingleImageSource;
        AddSensitivePath(sensitivePaths, source?.SourceRelativePath);
        if (CurrentProjectPath is { } projectPath
            && source is { SourceRelativePath.Length: > 0 }
            && Path.GetDirectoryName(projectPath) is { } projectDirectory)
        {
            AddSensitivePath(
                sensitivePaths,
                Path.Combine(projectDirectory, source.SourceRelativePath));
        }

        return new(
            BuildIdentity.Current,
            BuildIdentity.SourceCommit,
            BuildIdentity.SourceState,
            CurrentProject.Id,
            Integration.LatestRunId,
            errors,
            queue,
            artifacts,
            Replayable: false,
            ReplayLimitation: "원본/선택 이미지와 비밀 자료를 제외했으므로 이 사본만으로는 replay할 수 없습니다. / Replay is unavailable because original/selected images and secret material are excluded.",
            SensitivePaths: sensitivePaths);
    }

    private static void AddIntegrationReadError(
        ICollection<SupportDiagnosticError> errors,
        string code,
        string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            errors.Add(new(code, message));
        }
    }

    private static void AddSensitivePath(ICollection<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }

    private static bool IsQueueOrTimeoutDiagnostic(SimulationOperationalDiagnostic diagnostic) =>
        IsTimeoutDiagnostic(diagnostic)
        || diagnostic.EventName.Contains("queue", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Message.Contains("queue", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Message.Contains("대기", StringComparison.Ordinal)
        || diagnostic.Operation?.Contains("wait", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTimeoutDiagnostic(SimulationOperationalDiagnostic diagnostic) =>
        diagnostic.EventName.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Message.Contains("시간 초과", StringComparison.Ordinal)
        || diagnostic.Kind == SimulationOperationalDiagnosticKind.ShutdownTimedOut;

    #endregion

    #region Localization

    private static string FormatSignal(bool? value) => value switch
    {
        true => OpenVisionLanguageService.T("Shell.SignalOn"),
        false => OpenVisionLanguageService.T("Shell.SignalOff"),
        null => OpenVisionLanguageService.T("Shell.NotConfigured")
    };

    private static string LocalizeRuntimeState(string state) =>
        OpenVisionLanguageService.T($"Equipment.State.{state}", state, state);

    private void OnShellNavigationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (!_isApplyingProject && args.PropertyName == nameof(ShellNavigationViewModel.IsSequenceWorkspace) && Navigation.IsSequenceWorkspace)
            _selectionSynchronization.SelectSequenceStepTarget();
        if (args.PropertyName == nameof(ShellNavigationViewModel.IsSequenceWorkspace)) RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
        if (args.PropertyName is nameof(ShellNavigationViewModel.IsSequenceWorkspace) or nameof(ShellNavigationViewModel.IsModalOverlayOpen)) RaiseCanExecuteChanged(_openStepAxisSettingsCommand);
        if (args.PropertyName is nameof(ShellNavigationViewModel.IsResultsWorkspace) or nameof(ShellNavigationViewModel.IsModalOverlayOpen)) RunHistory?.InvalidateCommands();
        if (args.PropertyName is nameof(ShellNavigationViewModel.IsExperimentWorkspace) or nameof(ShellNavigationViewModel.IsModalOverlayOpen)) RaiseCanExecuteChanged(_openExperimentCameraInputCommand);
        if (args.PropertyName is nameof(ShellNavigationViewModel.IsSimulationOverviewWorkspace) or nameof(ShellNavigationViewModel.IsModalOverlayOpen)) RaiseCanExecuteChanged(_openSimulationObservationStepCommand);
        if (args.PropertyName is nameof(ShellNavigationViewModel.IsInspectionWorkspace) or nameof(ShellNavigationViewModel.IsModalOverlayOpen)) RaiseCanExecuteChanged(_openCameraAcquisitionStepCommand);

        if (args.PropertyName is nameof(ShellNavigationViewModel.SelectedWorkspaceIndex)
            or nameof(ShellNavigationViewModel.IsEquipmentWorkspace)
            or nameof(ShellNavigationViewModel.IsSimulationWorkspace))
        {
            if (!Navigation.IsEquipmentWorkspace)
            {
                IsEquipmentComponentLibraryDialogOpen = false;
            }

            if (!Navigation.IsEquipmentWorkspace && !Navigation.IsSimulationWorkspace)
            {
                IsStationUnitEditorOpen = false;
                IsBulkMoveEditorOpen = false;
                IsEquipmentSinglePartCheckOpen = false;
            }

            RaiseCanExecuteChanged(_openStationUnitEditorCommand);
            RaiseCanExecuteChanged(_addStationUnitCommand);
            RaiseCanExecuteChanged(_removeEquipmentUnitCommand);
            RaiseCanExecuteChanged(_removeSelectedStationCommand);
            RaiseCanExecuteChanged(_openBulkMoveEditorCommand);
            RaiseCanExecuteChanged(_applyBulkMoveCommand);
            RaiseCanExecuteChanged(_openEquipmentCameraSettingsCommand);
        RaiseCanExecuteChanged(_openEquipmentPropertiesCommand);
            RaiseCanExecuteChanged(_openEquipmentDriveTabCommand);
            RaiseCanExecuteChanged(_openEquipmentSinglePartCheckCommand);
            RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
            RaiseCanExecuteChanged(_addEquipmentLayoutComponentCommand);
            RaiseCanExecuteChanged(_openEquipmentComponentLibraryDialogCommand);
        }

        if (args.PropertyName == nameof(ShellNavigationViewModel.IsInspectorOpen)
            && !Navigation.IsInspectorOpen)
        {
            IsEquipmentSinglePartCheckOpen = false;
        }

        switch (args.PropertyName)
        {
            case nameof(ShellNavigationViewModel.IsCompactLayout):
                OnPropertyChanged(nameof(IsCompactLayout));
                break;
            case nameof(ShellNavigationViewModel.SelectedDocumentTabIndex):
                OnPropertyChanged(nameof(SelectedDocumentTabIndex));
                break;
            case nameof(ShellNavigationViewModel.SelectedLeftToolTabIndex):
                OnPropertyChanged(nameof(SelectedLeftToolTabIndex));
                break;
            case nameof(ShellNavigationViewModel.IsStartupChoiceVisible):
                OnPropertyChanged(nameof(IsStartupChoiceVisible));
                RefreshProjectIdentity();
                InvalidateCommands();
                break;
            case nameof(ShellNavigationViewModel.SelectedLanguageOption):
                OnPropertyChanged(nameof(SelectedLanguageOption));
                break;
        }
    }

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrEmpty(args.PropertyName))
        {
            return;
        }

        OnPropertyChanged(args.PropertyName);
        if (args.PropertyName == nameof(CameraCommissioningViewModel.SelectedCameraId)) RaiseCanExecuteChanged(_showCameraEquipmentLocationCommand);
        if (args.PropertyName is nameof(CameraCommissioningViewModel.SelectedCameraId) or nameof(CameraCommissioningViewModel.CurrentCameraInputs) or nameof(CameraCommissioningViewModel.SelectedExecutionInput)) RaiseCanExecuteChanged(_openExperimentCameraInputCommand);
        if (args.PropertyName is nameof(CameraCommissioningViewModel.SelectedCameraId) or nameof(CameraCommissioningViewModel.CurrentAcquisitionSteps)) RaiseCanExecuteChanged(_openCameraAcquisitionStepCommand);
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
        _supportDiagnostics.Refresh();
    }

    private void OnBottomAlertStateChanged(object? sender, EventArgs args)
    {
        if (_disposed || args is PropertyChangedEventArgs propertyArgs
            && !string.IsNullOrEmpty(propertyArgs.PropertyName)
            && propertyArgs.PropertyName is not (nameof(RecipeConnections.DryRun.ReadinessPassed) or nameof(RecipeConnections.DryRun.IsReadinessStale)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasBottomReadinessAlert));
        OnPropertyChanged(nameof(BottomAlarmCount));
    }

    private void OnIntegrationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _supportDiagnostics.Refresh();
    }

    private void OnAxisCommissioningInputChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_disposed && args.PropertyName is nameof(AxisCommissioningViewModel.IsAxisTargetPositionValid)
            or nameof(AxisCommissioningViewModel.IsAxisRelativeDistanceValid))
            _equipmentInspectorManualCommand?.RaiseCanExecuteChanged();
    }

    private void OnManualEquipmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrEmpty(args.PropertyName))
        {
            return;
        }

        OnPropertyChanged(args.PropertyName);
        if (args.PropertyName is nameof(ManualEquipmentCommissioningViewModel.IsStartingManualControl)
            or nameof(ManualEquipmentCommissioningViewModel.IsManualControlAdmissionLocked))
        {
            OnPropertyChanged(nameof(IsSceneEditable));
            Layout.IsEditable = IsSceneEditable;
            RecipeConnections.IsEditable = IsSceneEditable;
            SequenceEditor.IsEditable = IsSceneEditable;
            InvalidateCommands();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _recipeAuthoring.ExitPlayback();

        foreach (var propertyName in LocalizedPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }

        Properties.RefreshLocalization();
        _manualEquipment.RefreshLocalization();
        _camera.RefreshLocalization();
        AxisCommissioning.RefreshLocalization();
        AxisDriveTuningEditor?.RefreshLocalization();
        MultiAxisCommissioningRecipe.RefreshLocalization();
        _multiAxisCommissioning.RefreshLocalization();
        _scenarioBatch?.RefreshLocalization();
        SemiconductorRecipes.RefreshLocalization();
        RecipeConnections.RefreshLocalization();
        Layout.RefreshLocalization();
        MaterialFlowSettings.RefreshLocalization();
        RunHistory.RefreshLocalization();
        EquipmentMaterialFlow.Update(CurrentProject, Layout.Items, PresentationSnapshot);
        OnPropertyChanged(nameof(ProjectRevisionText));
        OnPropertyChanged(nameof(EquipmentFooterStatusText));
        Integration.RefreshLocalization();
        DigitalIo.RefreshLocalization();
        AnalogIoAuthoring?.RefreshLocalization();
        FaultManager.RefreshLocalization();
        RuntimeDebugger.RefreshLocalization();
        SequenceEditor.RefreshLocalization();
        _projectDiagnostics.RefreshLocalization();
        _supportDiagnostics.Refresh();
    }

    #endregion

    #region Command Infrastructure

    private AsyncRelayCommand CreateAsyncCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null)
    {
        return new(
            execute,
            parameter => !_disposed
                && !_sessionCloseRequested
                && (canExecute?.Invoke(parameter) ?? true),
            HandleCommandException,
            useCommandManagerRequery: false);
    }

    private RelayCommand CreateRelayCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null)
    {
        return new(
            execute,
            parameter => !_disposed
                && !_sessionCloseRequested
                && (canExecute?.Invoke(parameter) ?? true),
            useCommandManagerRequery: false);
    }

    private void InvalidateCommands(bool includeCommandManager = true)
    {
        Navigation.InvalidateCommands();
        MaterialFlowSettings?.InvalidateCommands();
        RunHistory?.InvalidateCommands();
        RaiseCanExecuteChanged(_newProjectCommand);
        RaiseCanExecuteChanged(_openCameraAcquisitionStepCommand);
        RaiseCanExecuteChanged(_openExperimentCameraInputCommand);
        RaiseCanExecuteChanged(_openSimulationObservationStepCommand);
        RaiseCanExecuteChanged(_openStepAxisSettingsCommand);
        RaiseCanExecuteChanged(_confirmNewProjectNameCommand);
        RaiseCanExecuteChanged(_cancelNewProjectNameCommand);
        RaiseCanExecuteChanged(_openProjectCommand);
        RaiseCanExecuteChanged(_saveProjectCommand);
        RaiseCanExecuteChanged(_saveProjectAsCommand);
        RaiseCanExecuteChanged(_openStationUnitEditorCommand);
        RaiseCanExecuteChanged(_addStationUnitCommand);
        RaiseCanExecuteChanged(_cancelStationUnitEditorCommand);
        RaiseCanExecuteChanged(_removeEquipmentUnitCommand);
        RaiseCanExecuteChanged(_removeSelectedStationCommand);
        RaiseCanExecuteChanged(_openBulkMoveEditorCommand);
        RaiseCanExecuteChanged(_applyBulkMoveCommand);
        RaiseCanExecuteChanged(_cancelBulkMoveCommand);
        RaiseCanExecuteChanged(_openEquipmentCameraSettingsCommand);
        RaiseCanExecuteChanged(_openEquipmentPropertiesCommand);
        RaiseCanExecuteChanged(_openEquipmentDriveTabCommand);
        RaiseCanExecuteChanged(_openEquipmentSinglePartCheckCommand);
        RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
        RaiseCanExecuteChanged(_addEquipmentLayoutComponentCommand);
        RaiseCanExecuteChanged(_openEquipmentComponentLibraryDialogCommand);
        RaiseCanExecuteChanged(_addEquipmentComponentFromLibraryCommand);
        RaiseCanExecuteChanged(_showEquipmentOverviewCommand);
        RaiseCanExecuteChanged(_showEquipmentUnitCommand);
        RaiseCanExecuteChanged(_showCameraEquipmentLocationCommand);
        RaiseCanExecuteChanged(_setSimulationSpeedCommand);
        RaiseCanExecuteChanged(_runCommand);
        RaiseCanExecuteChanged(_navigateRuntimeErrorCommand);
        RaiseCanExecuteChanged(_pauseCommand);
        RaiseCanExecuteChanged(_abortSequenceCommand);
        RaiseCanExecuteChanged(_retrySequenceCommand);
        RaiseCanExecuteChanged(_stepCommand);
        RaiseCanExecuteChanged(_resetCommand);
        RaiseCanExecuteChanged(_startTestScenarioCommand);
        RaiseCanExecuteChanged(_stopTestScenarioCommand);
        RaiseCanExecuteChanged(_replayTestScenarioCommand);
        RaiseCanExecuteChanged(_exportSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_exportSimulationReportCommand);
        RaiseCanExecuteChanged(_importSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_exportUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_importUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_exportSupportDiagnosticsCommand);
        _simulationCommandTrace.InvalidateCommands();
        _multiAxisCommissioning.InvalidateCommands();
        _scenarioBatch?.InvalidateCommands();
        RaiseCanExecuteChanged(_cycleStartCommand);
        _manualEquipment.InvalidateCommands();
        _camera.InvalidateCommands();
        AxisCommissioning.InvalidateCommands();
        RaiseCanExecuteChanged(_runMultiAxisCommissioningRecipeCommand);
        RaiseCanExecuteChanged(_stopMultiAxisCommissioningRecipeCommand);
        _layoutAuthoring.InvalidateCommands();
        _recipeAuthoring.InvalidateCommands();
        RuntimeDebugger.InvalidateCommands();
        _projectDiagnostics.InvalidateCommands();

        if (includeCommandManager)
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void InvalidateModeCommands()
    {
        MaterialFlowSettings.InvalidateCommands();
        RaiseCanExecuteChanged(_openStationUnitEditorCommand);
        RaiseCanExecuteChanged(_addStationUnitCommand);
        RaiseCanExecuteChanged(_removeEquipmentUnitCommand);
        RaiseCanExecuteChanged(_removeSelectedStationCommand);
        RaiseCanExecuteChanged(_openBulkMoveEditorCommand);
        RaiseCanExecuteChanged(_applyBulkMoveCommand);
        RaiseCanExecuteChanged(_openEquipmentCameraSettingsCommand);
        RaiseCanExecuteChanged(_openEquipmentPropertiesCommand);
        RaiseCanExecuteChanged(_openEquipmentDriveTabCommand);
        RaiseCanExecuteChanged(_openEquipmentSinglePartCheckCommand);
        RaiseCanExecuteChanged(_openEquipmentSequenceReferencesCommand);
        RaiseCanExecuteChanged(_addEquipmentLayoutComponentCommand);
        RaiseCanExecuteChanged(_openEquipmentComponentLibraryDialogCommand);
        RaiseCanExecuteChanged(_addEquipmentComponentFromLibraryCommand);
        RaiseCanExecuteChanged(_showCameraEquipmentLocationCommand);
        RaiseCanExecuteChanged(_setSimulationSpeedCommand);
        RaiseCanExecuteChanged(_runCommand);
        RaiseCanExecuteChanged(_pauseCommand);
        RaiseCanExecuteChanged(_abortSequenceCommand);
        RaiseCanExecuteChanged(_retrySequenceCommand);
        RaiseCanExecuteChanged(_stepCommand);
        RaiseCanExecuteChanged(_resetCommand);
        RaiseCanExecuteChanged(_startTestScenarioCommand);
        RaiseCanExecuteChanged(_stopTestScenarioCommand);
        RaiseCanExecuteChanged(_replayTestScenarioCommand);
        RaiseCanExecuteChanged(_exportSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_exportSimulationReportCommand);
        RaiseCanExecuteChanged(_importSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_exportUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_importUnifiedCommissioningEvidenceCommand);
        _simulationCommandTrace.InvalidateCommands();
        _multiAxisCommissioning.InvalidateCommands();
        _scenarioBatch?.InvalidateCommands();
        RaiseCanExecuteChanged(_cycleStartCommand);
        if (_manualEquipment.HasSelectedManualEquipment)
        {
            _manualEquipment.InvalidateCommands();
        }
        if (_camera.HasVirtualCamera)
        {
            _camera.InvalidateCommands();
        }
        if (HasSelectedAxisDefinition || HasSelectedAxisStage)
        {
            AxisCommissioning.InvalidateCommands();
        }
        _layoutAuthoring.InvalidateCommands();
        _recipeAuthoring.ProcessPlanReview.InvalidateCommands();
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
        if (_disposed || _sessionCloseRequested || _simulationSession.IsShutdownRequested)
        {
            return;
        }

        StatusMessage = "Command failed";
        var snapshot = _simulationSession.Engine.CurrentSnapshot;
        var detail = $"{exception.GetType().Name}: {exception.Message}";
        _runtimeObservabilityJournal.Append(
            snapshot.SimulationTime,
            "Error",
            detail,
            snapshot.TickIndex,
            new SimulationOperationalDiagnostic(
                DateTimeOffset.UtcNow,
                SimulationOperationalDiagnosticKind.RuntimeMessage,
                SimulationLogSeverity.Alarm,
                "MachineStudio",
                "CommandFailed",
                detail,
                snapshot.TickIndex,
                snapshot.SimulationTime,
                "Error",
                Operation: nameof(HandleCommandException),
                ExceptionType: exception.GetType().FullName,
                ExceptionMessage: exception.Message,
                ProjectId: CurrentProject.Id,
                SessionId: _projectLifecycle.SessionId,
                RuntimeGeneration: snapshot.RuntimeGeneration,
                ExceptionDetail: exception.ToString()));
        _supportDiagnostics.Refresh();
    }

    private static string ShortCommandId(SimulationCommand command) =>
        ShortCommandId(command.CommandId);

    private static string ShortCommandId(string commandId) =>
        $"CMD-{commandId[..8].ToUpperInvariant()}";

    private static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];

    #endregion

    #region Shutdown And Dispose

    internal Task<RuntimeShutdownResult> ShutdownAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
        return _simulationSession.ShutdownAsync(timeout ?? RuntimeShutdownTimeout, cancellationToken);
    }

    internal async Task<SimulationSessionCloseResult> RequestCloseAsync(TimeSpan? timeout = null)
    {
        var duration = timeout ?? RuntimeShutdownTimeout;
        var result = await _simulationSession.RequestCloseAsync(duration);
        if (!result.IsApproved) return result;
        try
        {
            await RunHistory.RecordingCompletion.WaitAsync(duration);
            if (RunHistory.HasPendingSaves) return new(SimulationSessionCloseOutcome.ShutdownIncomplete, result.Shutdown);
            return result;
        }
        catch (Exception exception)
        {
            return new(SimulationSessionCloseOutcome.ShutdownIncomplete, result.Shutdown, exception);
        }
    }

    async Task<bool> IShellCloseHost.RequestCloseAsync()
    {
        var result = await RequestCloseAsync().ConfigureAwait(true);
        if (!result.IsApproved)
        {
            PresentCloseResult(result);
        }

        return result.IsApproved;
    }

    internal void PresentCloseResult(SimulationSessionCloseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_disposed
            || result.IsApproved
            || result.Outcome == SimulationSessionCloseOutcome.UnsavedChangesRejected)
        {
            return;
        }

        var status = result.Outcome switch
        {
            SimulationSessionCloseOutcome.ShutdownIncomplete => OpenVisionLanguageService.T(
                "Shell.ShutdownIncomplete",
                "실행 증거가 완결되지 않아 종료를 취소했습니다.",
                "Close was cancelled because runtime evidence is incomplete."),
            SimulationSessionCloseOutcome.ShutdownTimedOut => OpenVisionLanguageService.T(
                "Shell.ShutdownTimedOut",
                "실행 종료 시간이 초과되어 창을 닫지 않았습니다.",
                "Close was cancelled because runtime shutdown timed out."),
            SimulationSessionCloseOutcome.ShutdownFaulted => OpenVisionLanguageService.T(
                "Shell.ShutdownFaulted",
                "실행 종료 중 오류가 발생해 창을 닫지 않았습니다.",
                "Close was cancelled because runtime shutdown failed."),
            _ => OpenVisionLanguageService.T(
                "Shell.CloseFailed",
                "종료를 완료하지 못했습니다.",
                "The session could not be closed.")
        };
        StatusMessage = status;
        var detail = result.Shutdown?.Stage is { } stage
            ? $"{status} · stage={stage}"
            : result.Exception is { } exception
                ? $"{status} · {exception.Message}"
                : status;
        _runtimeObservabilityJournal.Log("Runtime", detail);
    }

    internal void PresentCloseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (_disposed)
        {
            return;
        }

        StatusMessage = OpenVisionLanguageService.T(
            "Shell.CloseFailed",
            "종료를 완료하지 못했습니다.",
            "The session could not be closed.");
        _runtimeObservabilityJournal.Log("Runtime", $"Close failed · {exception.Message}");
    }

    void IShellCloseHost.PresentCloseFailure(Exception exception) => PresentCloseFailure(exception);

    private void SetSessionCloseAdmission(bool isRequested)
    {
        if (_sessionCloseRequested == isRequested)
        {
            return;
        }

        _sessionCloseRequested = isRequested;
        Integration.SetSessionCloseAdmission(isRequested);
        _manualEquipment.SetSessionCloseAdmission(isRequested);
        _camera.SetSessionCloseAdmission(isRequested);
        if (isRequested)
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            _scenarioBatch?.CancelBatch();
            _multiAxisCommissioning.CancelValidation();
        }

        OnPropertyChanged(nameof(IsSessionCloseRequested));
        OnPropertyChanged(nameof(CanNavigateSequenceUnits));
        InvalidateCommands();
    }

    private void DisposeShellResources()
    {
        RunHistory.Dispose();
        _manualEquipment.Dispose();
        _camera.Dispose();
        _selectionSynchronization.Dispose();
        _recipeAuthoring.Dispose();
        AxisCommissioning.Dispose();
        DigitalIo.Dispose();
        FaultManager.Dispose();
        RuntimeDebugger.Dispose();
        SemiconductorRecipes.Dispose();
        _layoutAuthoring.Dispose();
        Layout.Dispose();
        SequenceEditor.Dispose();
        _simulationCommandTrace.Dispose();
        _simulationScenarioExecutionCoordinator.Dispose();
        Integration.Dispose();
        _projectDiagnostics.Dispose();
        _supportDiagnostics.Dispose();
    }

    private void DetachShellEventHandlers()
    {
        _shellNavigation.PropertyChanged -= OnShellNavigationPropertyChanged;
        _shellNavigation.LanguageChanged -= OnLanguageChanged;
        _shellNavigation.Dispose();
        AxisCommissioning.PropertyChanged -= OnAxisCommissioningInputChanged;
        _manualEquipment.PropertyChanged -= OnManualEquipmentPropertyChanged;
        _camera.PropertyChanged -= OnCameraPropertyChanged;
        Integration.PropertyChanged -= OnIntegrationPropertyChanged;
        _selectionSynchronization.ClearEditors();
        _selectionSynchronization.PropertyChanged -= OnSelectionSynchronizationPropertyChanged;
        Layout.PropertyChanged -= OnEquipmentLibraryLayoutPropertyChanged;
        SimulationWorkspace.PropertyChanged -= OnSimulationWorkspacePropertyChanged;
        RuntimeDebugger.Alarms.CollectionChanged -= OnBottomAlertStateChanged;
        RecipeConnections.DryRun.PropertyChanged -= OnBottomAlertStateChanged;
        _recipeAuthoring.DetachEventHandlers();
        _recipeAuthoring.ProcessPlanReview.PropertyChanged -= OnProcessPlanReviewPropertyChanged;
        _simulationCommandTrace.PropertyChanged -= OnSimulationCommandTracePropertyChanged;
        SequenceEditor.PropertyChanged -= OnSequenceEditorPropertyChanged;
        SequenceEditor.DefinitionChanged -= OnSequenceDefinitionChanged;
        DryRunPlayback.PropertyChanged -= OnDryRunPlaybackPropertyChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Layout.IsEditable = false;
        _multiAxisCommissioningExecutionWorkflow.Dispose();
        DetachShellEventHandlers();
        _simulationSession.BeginDispose(RuntimeShutdownTimeout, _disposeShellResourcesCallback);
    }

    #endregion
}
