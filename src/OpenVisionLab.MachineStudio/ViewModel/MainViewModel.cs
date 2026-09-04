using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
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
using OpenVisionLab.Machine.Infrastructure.Integration;
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

public sealed class MainViewModel : ViewModelBase, IDisposable
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
        nameof(UnifiedCommissioningEvidenceStatusText),
        nameof(SimulationCommandTraceStatusText),
        nameof(SelectedEquipmentStatus), nameof(ProcessPlanReviewPositionText)
    ];

    #endregion

    #region Dependencies

    private readonly ProjectDocumentStore _projectStore = new();
    private readonly ProjectDocumentSession _projectSession;
    private readonly ProjectOpenWorkflow _projectOpenWorkflow;
    private readonly ProjectSaveWorkflow _projectSaveWorkflow;
    private readonly SemiconductorRecipeCopyWorkflow _semiconductorRecipeCopyWorkflow;
    private readonly VirtualCameraInspectionTemplate _virtualCameraInspectionTemplate = new();
    private readonly RecipeConnectionProjectApplier _recipeConnectionProjectApplier = new();
    private readonly MachineIntegrationRequestWorkflow _integrationRequestWorkflow;
    private readonly RuntimeObservabilityPresenter _runtimeObservabilityPresenter;
    private readonly ISimulationEngine _engine;
    private readonly RuntimeDefinitionApplicationWorkflow _runtimeDefinitionApplicationWorkflow;
    private readonly ProjectRuntimeApplicationWorkflow _projectRuntimeApplicationWorkflow;
    private readonly SimulationRuntimeLoop _runtimeLoop;
    private readonly SimulationRuntimeResourceOwner _runtimeResources;
    private readonly SimulationRuntimeShutdownWorkflow _runtimeShutdownWorkflow;
    private readonly EquipmentCommandDispatcher _equipmentCommandDispatcher;
    private readonly ManualControlCommandWorkflow _manualControlCommandWorkflow;
    private readonly SimulationRunControlWorkflow _simulationRunControlWorkflow;
    private readonly SimulationCommandPresentationDispatcher _simulationCommandPresentationDispatcher;
    private readonly SimulationRuntimeProjectionCoordinator _runtimeProjectionCoordinator;
    private readonly ProjectSelectionSynchronizationWorkflow _selectionSynchronization;
    private readonly LayoutSelectionCommandWorkflow _layoutSelectionCommands;
    private readonly ManualCameraTriggerRequestFactory _manualCameraTriggerRequestFactory = new();
    private readonly MultiAxisCommissioningExecutionWorkflow _multiAxisCommissioningExecutionWorkflow;
    private readonly SimulationScenarioExecutionCoordinator _simulationScenarioExecutionCoordinator;
    private readonly ManualEquipmentPresentation _manualEquipmentPresentation;
    private readonly CameraCommissioningPresentation _cameraCommissioningPresentation;
    private readonly CameraSelectionWorkflow _cameraSelection;
    private readonly RecipeConnectionSimulationWorkflow _recipeConnectionSimulationWorkflow;
    private readonly RecipeConnectionSetupWorkflow _recipeConnectionSetupWorkflow;
    private readonly LayoutComponentAuthoringService _layoutComponentAuthoringService = new();
    private readonly LayoutAuthoringHistoryViewModel _layoutAuthoringHistory;
    private readonly LayoutAuthoringMutationWorkflow _layoutAuthoringMutationWorkflow;
    private readonly ProcessPlanReviewViewModel _processPlanReview;
    private readonly SimulationCommandTraceViewModel _simulationCommandTrace;
    private readonly ProjectFileDialogHost _projectFileDialogHost = new();
    private readonly SimulationEvidenceFileDialogHost _simulationEvidenceFileDialogHost = new();
    private readonly MainMessageDialogHost _mainMessageDialogHost = new();
    private readonly MainWpfInteractionHost _mainWpfInteractionHost = new();
    private readonly MultiAxisCommissioningViewModel _multiAxisCommissioning;
    private SimulationScenarioBatchViewModel? _scenarioBatch;
    private readonly VisionExecutionEvidenceViewModel _visionExecutionEvidence;
    private readonly UnifiedCommissioningEvidenceViewModel _unifiedCommissioningEvidence;
    private readonly ManualCameraTriggerWorkflow _manualCameraTriggerWorkflow;
    private readonly string? _startupSamplePath;

    #endregion

    #region State

    private string _title = "OpenVisionLab Machine Studio";
    private string _statusMessage = "Ready";
    private bool _isRunning;
    private bool _isDesignMode = true;
    private bool _isCompactLayout;
    private string? _integrationContextKey;
    private bool _isApplyingProject;
    private bool _isStartupChoiceVisible;
    private int _selectedLeftToolTabIndex;
    private bool _runtimeDefinitionDirty;
    private bool _disposed;
    private OpenVisionLanguageOption _selectedLanguageOption;
    private int _selectedDocumentTabIndex;

    #endregion

    #region Command Backing Fields

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
    private ICommand? _abortSequenceCommand;
    private ICommand? _retrySequenceCommand;
    private ICommand? _stepCommand;
    private ICommand? _resetCommand;
    private ICommand? _startTestScenarioCommand;
    private ICommand? _stopTestScenarioCommand;
    private ICommand? _replayTestScenarioCommand;
    private ICommand? _exportSimulationEvidenceCommand;
    private ICommand? _importSimulationEvidenceCommand;
    private ICommand? _exportUnifiedCommissioningEvidenceCommand;
    private ICommand? _importUnifiedCommissioningEvidenceCommand;
    private ICommand? _cycleStartCommand;
    private ICommand? _startManualEquipmentControlCommand;
    private ICommand? _startManualCameraControlCommand;
    private ICommand? _triggerCameraCommand;
    private ICommand? _runMultiAxisCommissioningRecipeCommand;
    private ICommand? _stopMultiAxisCommissioningRecipeCommand;
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
    private ICommand? _exitCommand;

    #endregion

    #endregion

    #region Constructors

    public MainViewModel(
        MachineProjectDocument? initialProject = null,
        string? initialProjectPath = null,
        string? startupSamplePath = null,
        string? integrationSettingsPath = null)
    {
        OpenVisionLanguageService.Load();
        UnsavedProjectPrompt = _mainMessageDialogHost.ShowUnsavedProjectPrompt;
        ProjectOpenFailurePresenter = _mainMessageDialogHost.ShowProjectOpenFailure;
        _selectedLanguageOption = OpenVisionLanguageService.LanguageOptions
            .First(option => option.Language == OpenVisionLanguageService.CurrentLanguage);
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;
        _projectSession = new(
            _projectStore,
            initialProject ?? new MachineProjectDocument { Name = "Untitled" },
            initialProjectPath);
        _integrationRequestWorkflow = new();
        _startupSamplePath = string.IsNullOrWhiteSpace(startupSamplePath)
            ? null
            : Path.GetFullPath(startupSamplePath);
        _isStartupChoiceVisible = initialProject is null && _startupSamplePath is not null;
        _projectOpenWorkflow = new(
            _projectStore,
            TryResolveUnsavedChangesAsync,
            ApplyOpenedProjectAsync,
            HandleProjectOpenFailure);
        _semiconductorRecipeCopyWorkflow = new(
            _projectStore,
            () => OpenVisionLanguageService.T("Gallery.TemplateOverwriteRejected"));
        var initialRuntime = BuildRuntimeConfiguration(CurrentProject);
        _engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = SimulationFixedStep });
        _runtimeDefinitionApplicationWorkflow = new(
            _engine,
            SimulationFixedStep);
        _projectRuntimeApplicationWorkflow = new(
            _runtimeDefinitionApplicationWorkflow,
            OnProjectRuntimeApplicationStateChanged,
            OnProjectRuntimeApplicationRejected,
            CompleteProjectRuntimeApplication);
        _equipmentCommandDispatcher = new(
            _engine,
            status => StatusMessage = status,
            Log);
        _simulationCommandPresentationDispatcher = new(
            _engine,
            status => StatusMessage = status,
            Log);
        _multiAxisCommissioningExecutionWorkflow = new(
            _engine,
            _equipmentCommandDispatcher);
        _manualEquipmentPresentation = new();
        _cameraCommissioningPresentation = new();
        _recipeConnectionSetupWorkflow = new(
            _recipeConnectionProjectApplier,
            () => CurrentProject,
            ExitDryRunPlayback,
            CompleteConnectionSetupMutation,
            CompleteConnectionProcessBlockMutation,
            status => StatusMessage = status,
            Log);
        _recipeConnectionSimulationWorkflow = new(
            () => CurrentProject,
            project =>
            {
                BuildRuntimeConfiguration(project);
            },
            status => StatusMessage = status,
            Log);
        AxisCommissioning = new AxisCommissioningViewModel(
            _equipmentCommandDispatcher.DispatchAxisCommandAsync,
            HandleCommandException);

        ProjectTree = new ProjectTreeViewModel();
        Properties = new PropertiesViewModel();
        Layout = new MachineLayoutViewModel();
        _manualControlCommandWorkflow = new(
            _equipmentCommandDispatcher,
            _manualEquipmentPresentation,
            () => Layout.SelectedItem?.Component?.Kind,
            () => IsRunning = true);
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
            _recipeConnectionSimulationWorkflow.ValidateSimulationReadiness,
            _recipeConnectionSimulationWorkflow.RunSequenceStepPreviewAsync,
            _recipeConnectionSimulationWorkflow.RunRecipeDryRunAsync,
            ShowConnectionDryRunStep,
            ApplyConnectionVirtualCameraWorkflow,
            _recipeConnectionSetupWorkflow.ApplyStationSkeleton,
            _recipeConnectionSetupWorkflow.ApplyLoadLockSetup,
            _recipeConnectionSetupWorkflow.ApplyWaferHandlerSetup,
            _recipeConnectionSetupWorkflow.ApplyPrealignerSetup,
            _recipeConnectionSetupWorkflow.ApplyInspectionHandoffSetup,
            _recipeConnectionSetupWorkflow.ApplyInspectionSortRouterSetup,
            _recipeConnectionSetupWorkflow.ApplyOhtHandoffSetup,
            _recipeConnectionSetupWorkflow.ApplyProcessBlocks,
            ApplyConnectionProcessBlockTimeouts,
            OnConnectionCheckpointTemplateApplied,
            OpenProcessBlockSequenceStep);
        SequenceEditor = new SequenceEditorViewModel();
        _processPlanReview = new ProcessPlanReviewViewModel(
            () => RecipeConnections.IsEditable,
            () => RecipeConnections.ProcessBlocks.IsProcessBlockPreviewVisible,
            () => RecipeConnections.ProcessBlocks.VisibleProcessBlockItems,
            () => RecipeConnections.ProcessBlocks.ProcessBlockItems,
            TryOpenConnectionSequenceStepForReview,
            stepId => RecipeConnections.ProcessBlocks.SelectProcessBlockStep(stepId)?.StepText,
            tabIndex => SelectedDocumentTabIndex = tabIndex,
            status => StatusMessage = status);
        _processPlanReview.PropertyChanged += OnProcessPlanReviewPropertyChanged;
        _simulationCommandTrace = new SimulationCommandTraceViewModel(
            () => IsRunMode
                  && !_isApplyingProject
                  && !IsValidationBusy
                  && !IsRunning
                  && !_runtimeDefinitionDirty,
            () => _engine as FixedStepSimulationEngine,
            ApplyMonitorSnapshot,
            ResetUnifiedCommissioningEvidenceForTraceCapture,
            RaiseUnifiedCommissioningEvidencePresentationChanged,
            status => StatusMessage = status,
            message => Log("Simulation", message),
            ExportSimulationCommandTraceWithDialog,
            ReplaySimulationCommandTraceWithDialogAsync,
            HandleCommandException);
        _simulationCommandTrace.PropertyChanged += OnSimulationCommandTracePropertyChanged;
        SimulationWorkspace = new SimulationWorkspaceViewModel();
        _simulationScenarioExecutionCoordinator = new(
            new SimulationScenarioWorkflow(command => _engine.EnqueueCommandAsync(command)),
            SimulationWorkspace,
            () => CurrentProject,
            EnsureRuntimeDefinitionAppliedAsync,
            value => IsDesignMode = value,
            value => IsRunning = value,
            status => StatusMessage = status,
            Log);
        MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeEditorViewModel(
            OnMultiAxisCommissioningRecipeChanged);
        _multiAxisCommissioning = new MultiAxisCommissioningViewModel(
            MultiAxisCommissioningRecipe,
            () => IsRunMode
                  && !_isApplyingProject
                  && !IsScenarioBatchRunning
                  && !IsRunning
                  && !_runtimeDefinitionDirty
                  && MultiAxisCommissioningRecipe.IsValid
                  && MultiAxisCommissioningRecipe.Targets.All(target =>
                      target.RuntimeState != AxisState.Moving),
            () => IsScenarioBatchRunning,
            () => CurrentProject,
            () => CurrentProjectPath,
            () => _projectSession.SerializeForEvidence(),
            () => BuildRuntimeConfiguration(CurrentProject),
            SimulationFixedStep,
            _mainWpfInteractionHost.DispatchAsync,
            status => StatusMessage = status,
            message => Log("Motion", message),
            NavigateToCommissioningMismatch,
            OnMultiAxisCommissioningPresentationChanged,
            HandleCommandException);
        _scenarioBatch = new SimulationScenarioBatchViewModel(
            SimulationWorkspace,
            () => IsRunMode
                  && !_isApplyingProject
                  && !_multiAxisCommissioning.IsValidationRunning
                  && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
                  && SimulationWorkspace.IsScheduledFaultConfigurationValid
                  && SimulationWorkspace.IsAssertionConfigurationValid,
            () => IsRunMode
                  && !_isApplyingProject
                  && !_multiAxisCommissioning.IsValidationRunning,
            () => IsRunMode
                  && !_isApplyingProject
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
            () => _projectSession.SerializeForEvidence(),
            SimulationFixedStep,
            _mainWpfInteractionHost.DispatchBatchProgressAsync,
            () => ConditionScenarioTargets,
            ResetUnifiedCommissioningEvidenceForTraceCapture,
            status => StatusMessage = status,
            message => Log("Batch", message),
            NavigateToBatchMismatch,
            OnScenarioBatchPresentationChanged,
            HandleCommandException);
        _visionExecutionEvidence = new VisionExecutionEvidenceViewModel(
            CreateVisionEvidenceContext,
            message => Log("Vision", message),
            NotifyCameraCommissioningChanged);
        _manualCameraTriggerWorkflow = new(
            () => _engine.CurrentSnapshot,
            _equipmentCommandDispatcher.DispatchCameraCommandAsync,
            _visionExecutionEvidence,
            ApplyMonitorSnapshot);
        _projectSaveWorkflow = new(
            _projectStore,
            () => CurrentProject,
            project => SimulationWorkspace.SaveProjectScenario(project.Simulation),
            _scenarioBatch!.PersistForProjectPath,
            _multiAxisCommissioning.PersistForProjectPath,
            _visionExecutionEvidence.PersistForProjectPath);
        _unifiedCommissioningEvidence = new UnifiedCommissioningEvidenceViewModel(
            CanExportUnifiedCommissioningEvidenceCore,
            CanImportUnifiedCommissioningEvidenceCore,
            CreateSimulationEvidenceForUnifiedCommissioning,
            CreateCommandTraceForUnifiedCommissioning,
            GetCurrentUnifiedCommissioningVisionEvidence,
            CreateUnifiedCommissioningEvidenceContext,
            ApplyImportedUnifiedCommissioningArtifacts,
            status => StatusMessage = status,
            message => Log("Batch", message),
            RaiseUnifiedCommissioningEvidencePresentationChanged);
        CameraImageSourceEditor = new CameraImageSourceEditorViewModel(OnCameraImageSourceApplied);
        SceneSnapshots = new SceneSnapshotStore();
        _cameraSelection = new(
            () => CurrentProject,
            cameraId => CameraImageSourceEditor.SelectCamera(cameraId),
            () => _visionExecutionEvidence.RefreshContext(),
            () => ApplyMonitorSnapshot(SceneSnapshots.Latest ?? _engine.CurrentSnapshot),
            OnPropertyChanged,
            () => NotifyCameraCommissioningChanged());
        Integration = new MachineIntegrationViewModel(
            CreateTwoDIntegrationRequest,
            CanBuildTwoDIntegrationRequest,
            () => CurrentProject.Id,
            integrationSettingsPath);
        SemiconductorRecipes = new SemiconductorRecipeGalleryViewModel(
            CreateSemiconductorRecipeCopyAsync);
        DryRunPlayback = new RecipeDryRunPlaybackViewModel(
            () => IsDesignMode,
            isEditable => Layout.IsEditable = isEditable,
            tabIndex => SelectedDocumentTabIndex = tabIndex,
            step => RecipeConnections.DryRun.SelectedRecipeDryRunStep = step,
            status => StatusMessage = status);
        DryRunPlayback.PropertyChanged += OnDryRunPlaybackPropertyChanged;
        DigitalIo = new DigitalIoCommissioningViewModel(
            _simulationCommandPresentationDispatcher.DispatchDigitalIoAsync);
        FaultManager = new FaultManagerViewModel(
            _simulationCommandPresentationDispatcher.DispatchFaultAsync);
        RuntimeDebugger = new RuntimeDebuggerViewModel(DispatchRuntimeDebuggerCommandAsync);
        _runtimeObservabilityPresenter = new(
            LogMessageRetentionLimit,
            OperationalDiagnosticRetentionLimit,
            _visionExecutionEvidence,
            RuntimeDebugger);
        LogMessages = _runtimeObservabilityPresenter.LogMessages;
        _layoutAuthoringHistory = new LayoutAuthoringHistoryViewModel(
            Layout,
            () => CurrentProject,
            () => IsSceneEditable,
            () => _isApplyingProject,
            () => MarkProjectChanged(),
            UpdateRunToolAvailability,
            RefreshDefinitionPresentation,
            () => InvalidateCommands(),
            status => StatusMessage = status,
            Log,
            OnLayoutDefinitionChanged);
        _layoutAuthoringMutationWorkflow = new(
            Layout,
            _layoutComponentAuthoringService,
            _layoutAuthoringHistory,
            () => CurrentProject,
            () => IsSceneEditable,
            () => _isApplyingProject,
            () => MarkProjectChanged(),
            UpdateRunToolAvailability,
            RefreshDefinitionPresentation,
            status => StatusMessage = status,
            Log);
        _layoutSelectionCommands = new(
            Layout,
            status => StatusMessage = status);
        _runtimeProjectionCoordinator = new(
            MultiAxisCommissioningRecipe,
            SimulationWorkspace,
            DigitalIo,
            FaultManager,
            RuntimeDebugger,
            _visionExecutionEvidence,
            value => IsRunning = value,
            RefreshManualEquipmentProjection,
            RefreshCameraCommissioningProjection);
        _selectionSynchronization = new(
            ProjectTree,
            Layout,
            Properties,
            RecipeConnections,
            SequenceEditor,
            _cameraSelection,
            () => CurrentProject,
            () => SceneSnapshots.Latest ?? _engine.CurrentSnapshot,
            snapshot => _runtimeProjectionCoordinator.UpdateSelectedAxis(
                snapshot,
                CreateRuntimeProjectionSelection()),
            OnProjectTreeSelectionPresentationChanged,
            OnLayoutSelectionPresentationChanged,
            OnAxisDefinitionChanged,
            OnAnalogChannelDefinitionChanged,
            status => StatusMessage = status);
        _selectionSynchronization.PropertyChanged += OnSelectionSynchronizationPropertyChanged;
        _simulationRunControlWorkflow = new(
            _engine,
            SimulationFixedStep,
            () => new SimulationRunControlState(
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
                ActiveSequenceId),
            EnsureRuntimeDefinitionAppliedAsync,
            value => IsDesignMode = value,
            value => IsRunning = value,
            ApplyMonitorSnapshot,
            _visionExecutionEvidence.CancelCapture,
            status => StatusMessage = status,
            Log,
            () => InvalidateCommands());
        SimulationWorkspace.PropertyChanged += OnSimulationWorkspacePropertyChanged;
        RecipeConnections.ProcessBlocks.ProcessBlockPreviewClosed += OnProcessBlockPreviewClosed;
        SequenceEditor.DefinitionChanged += OnSequenceDefinitionChanged;

        _isApplyingProject = true;
        try
        {
            ApplyProjectPresentation(CurrentProject);
            RuntimeDebugger.LoadProject(CurrentProject, resetSession: true);
            _layoutAuthoringHistory.Reset();
            _multiAxisCommissioning.Restore();
            _visionExecutionEvidence.Restore();
            var initialSnapshot = _engine.CurrentSnapshot;
            SceneSnapshots.Publish(initialSnapshot);
            ApplyMonitorSnapshot(initialSnapshot);
        }
        finally
        {
            _isApplyingProject = false;
        }
        AcceptCurrentProjectAsSaved();
        Log("System", "Deterministic machine runtime ready · fixed step 5 ms");

        _runtimeLoop = new SimulationRuntimeLoop(
            _engine,
            _mainWpfInteractionHost.DispatchAsync,
            snapshot => SceneSnapshots.Publish(snapshot),
            ApplyMonitorSnapshot,
            () =>
            {
                ApplyMonitorSnapshot(_engine.CurrentSnapshot);
                _scenarioBatch!.Restore();
            },
            detail => Log("Runtime", $"Initial configuration rejected · {detail}"),
            runtimeEvent => _runtimeObservabilityPresenter.RecordRuntimeEvent(
                runtimeEvent,
                _engine.CurrentSnapshot),
            _runtimeObservabilityPresenter.RecordEngineTermination,
            HandleCommandException);
        _runtimeResources = new(
            _engine,
            _runtimeLoop,
            SimulationWorkspace,
            _scenarioBatch,
            _multiAxisCommissioning);
        _runtimeShutdownWorkflow = new(
            _engine,
            _runtimeLoop,
            _runtimeResources,
            _simulationRunControlWorkflow,
            RecordShutdownDiagnostic);
        _runtimeLoop.Start(initialRuntime);
    }

    #endregion

    #region Properties

    private bool IsScenarioBatchRunning => _scenarioBatch?.IsBatchRunning == true;
    private bool IsValidationBusy => IsScenarioBatchRunning || _multiAxisCommissioning.IsValidationRunning;
    private SimulationRuntimeSnapshotProjection RuntimeProjection => _runtimeProjectionCoordinator.CurrentProjection;
    private MachineProjectDocument CurrentProject => _projectSession.Project;
    private string ProjectDisplayName => _projectSession.DisplayName;
    private string? ActiveSequenceId => CurrentProject.Simulation.AutomaticRun?.SequenceId
        ?? CurrentProject.Sequences.FirstOrDefault()?.Id;
    private VirtualAxisDefinition? CurrentAxisDefinition => RuntimeProjection.CurrentAxis is null
        ? null
        : CurrentProject.Axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, RuntimeProjection.CurrentAxis.Id, StringComparison.Ordinal));
    private DeviceDefinition? CurrentCameraDefinition =>
        _cameraSelection.GetSelectedDefinition(RuntimeProjection.CurrentCamera?.Id);

    internal Func<UnsavedProjectDecision> UnsavedProjectPrompt { get; set; }
    internal Action<string> ProjectOpenFailurePresenter { get; set; }
    internal string? CurrentProjectPath => _projectSession.CurrentPath;

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
            RefreshManualEquipmentProjection();
            RefreshCameraCommissioningProjection();
            NotifyModeDependentCommandsChanged();
            InvalidateModeCommands();
            SequenceEditor.InvalidateCommands();
            DigitalIo.InvalidateCommands();
            FaultManager.InvalidateCommands();
            RuntimeDebugger.InvalidateCommands();

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
        => _projectSession.HasUnsavedChanges;

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        set => SetProperty(ref _isCompactLayout, value);
    }

    public ProjectTreeViewModel ProjectTree { get; }
    public PropertiesViewModel Properties { get; }
    public AxisDriveTuningEditorViewModel? AxisDriveTuningEditor => _selectionSynchronization.AxisDriveTuningEditor;
    public AnalogIoAuthoringViewModel? AnalogIoAuthoring => _selectionSynchronization.AnalogIoAuthoring;

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

    public bool HasProcessPlanReturnContext => _processPlanReview.HasReturnContext;
    public string? ProcessPlanReturnStepId => _processPlanReview.ReturnStepId;
    public string ProcessPlanReviewPositionText => _processPlanReview.ReviewPositionText;
    public bool HasSelectedAxisDefinition => AxisDriveTuningEditor is not null;
    public bool HasSelectedAnalogChannel => AnalogIoAuthoring is not null;
    public AxisCommissioningViewModel AxisCommissioning { get; }
    public MachineLayoutViewModel Layout { get; }
    public RecipeConnectionWorkbenchViewModel RecipeConnections { get; }
    public SequenceEditorViewModel SequenceEditor { get; }
    public SimulationWorkspaceViewModel SimulationWorkspace { get; }
    public MultiAxisCommissioningRecipeEditorViewModel MultiAxisCommissioningRecipe { get; }
    public CameraImageSourceEditorViewModel CameraImageSourceEditor { get; }
    public MachineIntegrationViewModel Integration { get; }
    public SemiconductorRecipeGalleryViewModel SemiconductorRecipes { get; }
    public RecipeDryRunPlaybackViewModel DryRunPlayback { get; }
    public SceneSnapshotStore SceneSnapshots { get; }
    public SceneSnapshotStore SceneSnapshotSource => DryRunPlayback.IsActive
        ? DryRunPlayback.PlaybackSnapshots
        : SceneSnapshots;
    private SimulationSnapshot PresentationSnapshot =>
        SceneSnapshotSource.Latest ?? SceneSnapshots.Latest ?? _engine.CurrentSnapshot;
    public bool IsSceneEditable => IsDesignMode && !DryRunPlayback.IsActive;
    public bool IsDryRunPlaybackActive => DryRunPlayback.IsActive;
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
    public IReadOnlyList<OpenVisionLanguageOption> LanguageOptions => OpenVisionLanguageService.LanguageOptions;

    public DeterministicConditionScenarioSnapshot ConditionScenario => RuntimeProjection.ConditionScenario;
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
    public bool CanStartTestScenario => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !RuntimeProjection.ConditionScenario.IsActive
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanStopTestScenario => IsRunMode && !IsValidationBusy && RuntimeProjection.ConditionScenario.IsActive;
    public bool CanReplayTestScenario => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanAbortSequence => _simulationRunControlWorkflow.CanAbortSequence();
    public bool CanRetrySequence => _simulationRunControlWorkflow.CanRetrySequence();
    public bool IsBatchRunning => _scenarioBatch?.IsBatchRunning == true;
    public bool IsScenarioConfigurationEnabled =>
        _scenarioBatch?.IsScenarioConfigurationEnabled ?? !IsValidationBusy;
    public int BatchCompletedRuns => _scenarioBatch?.BatchCompletedRuns ?? 0;
    public bool CanRunScenarioBatch => _scenarioBatch?.CanRunScenarioBatch == true;
    public bool CanAcceptBatchBaseline => _scenarioBatch?.CanAcceptBatchBaseline == true;
    public bool CanClearBatchBaseline => _scenarioBatch?.CanClearBatchBaseline == true;
    public bool CanNavigateToBatchMismatch => _scenarioBatch?.CanNavigateToBatchMismatch == true;
    public bool CanExportSimulationEvidence => _scenarioBatch?.CanExportEvidence == true;
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
            CurrentProject);

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
        RuntimeProjection.SimulationTime);
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
    public string CameraCountText => CurrentProject.Devices.Count(device => device.Kind == DeviceKind.Camera)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasVirtualCamera => CurrentProject.Devices.Any(device => device.Kind == DeviceKind.Camera);
    public IReadOnlyList<DeviceDefinition> VirtualCameras => CurrentProject.Devices
        .Where(device => device.Kind == DeviceKind.Camera)
        .ToArray();
    public DeviceDefinition? SelectedVirtualCamera
    {
        get => _cameraSelection.SelectedVirtualCamera;
        set => _cameraSelection.SelectVirtualCamera(value?.Id);
    }
    public string? SelectedCameraId
    {
        get => _cameraSelection.SelectedCameraId;
        set => _cameraSelection.SelectVirtualCamera(value);
    }
    public IReadOnlyList<string> CurrentCameraRecipes => _cameraSelection.CurrentCameraRecipes;
    public string? SelectedCameraRecipe
    {
        get => _cameraSelection.SelectedCameraRecipe;
        set => _cameraSelection.SelectCameraRecipe(value);
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
    public bool CanStartManualEquipmentControl => IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !IsRunning
        && !RuntimeProjection.AutomaticRun.IsActive
        && RuntimeProjection.CurrentSequence?.Status != SequenceExecutionStatus.Running
        && (Layout.SelectedItem?.Component?.Kind switch
        {
            LayoutComponentKind.LinearStage => AxisCommissioning.HasCurrentAxis
                && !AxisCommissioning.IsCurrentAxisInterlocked,
            LayoutComponentKind.RotaryStage => AxisCommissioning.HasCurrentAxis
                && !AxisCommissioning.IsCurrentAxisInterlocked,
            LayoutComponentKind.DigitalSensor => _manualEquipmentPresentation.HasSelectedDigitalSensor
                && !_manualEquipmentPresentation.IsCurrentSensorFaulted,
            LayoutComponentKind.PneumaticCylinder => _manualEquipmentPresentation.HasSelectedPneumaticCylinder
                && !_manualEquipmentPresentation.IsCurrentCylinderInterlocked,
            LayoutComponentKind.Conveyor => _manualEquipmentPresentation.HasSelectedConveyor,
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
    public bool HasSelectedDigitalSensor => _manualEquipmentPresentation.HasSelectedDigitalSensor;
    public bool IsCurrentSensorFaulted => _manualEquipmentPresentation.IsCurrentSensorFaulted;
    public bool IsCurrentSensorManuallyForced => _manualEquipmentPresentation.IsCurrentSensorManuallyForced;
    public string CurrentSensorForceText => _manualEquipmentPresentation.CurrentSensorForceText;
    public string SensorCommissioningHintText => _manualEquipmentPresentation.SensorCommissioningHintText;
    public bool CanForceSensorOn => _manualEquipmentPresentation.CanForceSensorOn;
    public bool CanForceSensorOff => _manualEquipmentPresentation.CanForceSensorOff;
    public bool CanClearSensorForce => _manualEquipmentPresentation.CanClearSensorForce;
    public bool HasSelectedPneumaticCylinder => _manualEquipmentPresentation.HasSelectedPneumaticCylinder;
    public bool IsCurrentCylinderInterlocked => _manualEquipmentPresentation.IsCurrentCylinderInterlocked;
    public string CurrentCylinderInterlockText => _manualEquipmentPresentation.CurrentCylinderInterlockText;
    public string CylinderCommissioningHintText => _manualEquipmentPresentation.CylinderCommissioningHintText;
    public bool CanExtendCylinder => _manualEquipmentPresentation.CanExtendCylinder;
    public bool CanRetractCylinder => _manualEquipmentPresentation.CanRetractCylinder;
    public bool HasSelectedConveyor => _manualEquipmentPresentation.HasSelectedConveyor;
    public string ConveyorCommissioningHintText => _manualEquipmentPresentation.ConveyorCommissioningHintText;
    public bool CanRunConveyorForward => _manualEquipmentPresentation.CanRunConveyorForward;
    public bool CanRunConveyorReverse => _manualEquipmentPresentation.CanRunConveyorReverse;
    public bool CanStopConveyor => _manualEquipmentPresentation.CanStopConveyor;
    internal DigitalSignalSnapshot? CurrentSelectedSensorSignal =>
        _manualEquipmentPresentation.CurrentSelectedSensorSignal;
    public string CurrentCameraName => _cameraCommissioningPresentation.CurrentCameraName;
    public string CurrentCameraStateText => _cameraCommissioningPresentation.CurrentCameraStateText;
    public string CurrentCameraResultText => _cameraCommissioningPresentation.CurrentCameraResultText;
    public string CurrentCameraFrameText => _cameraCommissioningPresentation.CurrentCameraFrameText;
    public string CurrentCameraExposureTicksText =>
        _cameraCommissioningPresentation.CurrentCameraExposureTicksText;
    public string CurrentCameraTransferTicksText =>
        _cameraCommissioningPresentation.CurrentCameraTransferTicksText;
    public string CurrentCameraSourceText => _cameraCommissioningPresentation.CurrentCameraSourceText;
    public string CurrentCameraFrameHashText => _cameraCommissioningPresentation.CurrentCameraFrameHashText;
    public string CurrentCameraInspectionIdText =>
        _cameraCommissioningPresentation.CurrentCameraInspectionIdText;
    public string CurrentCameraInspectionMessageText =>
        _cameraCommissioningPresentation.CurrentCameraInspectionMessageText;
    public string CurrentCameraInspectionMetricsText =>
        _cameraCommissioningPresentation.CurrentCameraInspectionMetricsText;
    public string CurrentVisionEvidenceHashText => _visionExecutionEvidence.EvidenceHashText;
    public string VisionEvidenceStatusText => _visionExecutionEvidence.StatusText;
    public string VisionEvidenceComparisonText => _visionExecutionEvidence.ComparisonText;
    public string CurrentCameraEvidenceDetailsText => string.Join(
        Environment.NewLine,
        $"{OpenVisionLanguageService.T("Camera.InspectionId")}: {CurrentCameraInspectionIdText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMessage")}: {CurrentCameraInspectionMessageText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMetrics")}: {CurrentCameraInspectionMetricsText}",
        $"{OpenVisionLanguageService.T("Camera.ExecutionEvidence")}: {CurrentVisionEvidenceHashText}",
        VisionEvidenceStatusText,
        VisionEvidenceComparisonText);
    internal DeterministicVisionExecutionEvidencePackage? LatestVisionEvidence =>
        _visionExecutionEvidence.LatestEvidence;
    internal DeterministicVisionExecutionComparison? VisionEvidenceComparison =>
        _visionExecutionEvidence.Comparison;
    public string CameraCommissioningHintText =>
        _cameraCommissioningPresentation.CameraCommissioningHintText;
    public bool CanStartManualCameraControl =>
        _cameraCommissioningPresentation.CanStartManualCameraControl;
    public bool CanTriggerCamera => _cameraCommissioningPresentation.CanTriggerCamera;
    public string CurrentSequenceName => ResolveSequenceName(RuntimeProjection.CurrentSequence?.SequenceId);
    public string CurrentSequenceStateText => RuntimeProjection.CurrentSequence is null
        ? OpenVisionLanguageService.T("Shell.NotConfigured")
        : LocalizeRuntimeState(RuntimeProjection.CurrentSequence.Status.ToString());
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
        if (_projectFileDialogHost.SelectProjectToOpen() is not { } path)
        {
            return;
        }

        await OpenProjectReplacingCurrentAsync(path);
    }, _ => !_isApplyingProject && !IsValidationBusy);

    public ICommand SaveProjectCommand => _saveProjectCommand ??= CreateAsyncCommand(async _ =>
    {
        await TrySaveCurrentProjectAsync();
    }, _ => !_isApplyingProject && !IsValidationBusy && !string.IsNullOrWhiteSpace(CurrentProject.Name));

    public ICommand SaveProjectAsCommand => _saveProjectAsCommand ??= CreateAsyncCommand(
        async _ => await TrySaveCurrentProjectAsync(saveAs: true),
        _ => !_isApplyingProject && !IsValidationBusy && !string.IsNullOrWhiteSpace(CurrentProject.Name));

    public ICommand RunCommand => _runCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.RunAsync(),
        _ => _simulationRunControlWorkflow.CanRun());

    public ICommand PauseCommand => _pauseCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.PauseAsync(),
        _ => _simulationRunControlWorkflow.CanPause());

    public ICommand StopCommand => PauseCommand;

    public ICommand AbortSequenceCommand => _abortSequenceCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.AbortSequenceAsync(),
        _ => _simulationRunControlWorkflow.CanAbortSequence());

    public ICommand RetrySequenceCommand => _retrySequenceCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.RetrySequenceAsync(),
        _ => _simulationRunControlWorkflow.CanRetrySequence());

    public ICommand StepCommand => _stepCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.StepAsync(),
        _ => _simulationRunControlWorkflow.CanStep());

    public ICommand ResetCommand => _resetCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.ResetAsync(),
        _ => _simulationRunControlWorkflow.CanReset());

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

    public ICommand StartSimulationCommandTraceCaptureCommand =>
        _simulationCommandTrace.StartCaptureCommand;

    public ICommand ExportSimulationCommandTraceCommand =>
        _simulationCommandTrace.ExportCommand;

    public ICommand ReplaySimulationCommandTraceCommand =>
        _simulationCommandTrace.ReplayCommand;

    public ICommand CycleStartCommand => _cycleStartCommand ??= CreateAsyncCommand(
        async _ => await _simulationRunControlWorkflow.CycleStartAsync(),
        _ => _simulationRunControlWorkflow.CanCycleStart());

    public ICommand StartManualEquipmentControlCommand => _startManualEquipmentControlCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.StartEquipmentControlAsync(),
            _ => CanStartManualEquipmentControl);

    public ICommand StartManualCameraControlCommand => _startManualCameraControlCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.StartCameraControlAsync(),
            _ => CanStartManualCameraControl);

    public ICommand TriggerCameraCommand => _triggerCameraCommand ??= CreateAsyncCommand(
        async _ => await TriggerSelectedCameraAsync(),
        _ => CanTriggerCamera);

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

    public ICommand ForceSensorOnCommand => _forceSensorOnCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetSensorForceAsync(true),
            _ => CanForceSensorOn);

    public ICommand ForceSensorOffCommand => _forceSensorOffCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetSensorForceAsync(false),
            _ => CanForceSensorOff);

    public ICommand ClearSensorForceCommand => _clearSensorForceCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetSensorForceAsync(null),
            _ => CanClearSensorForce);

    public ICommand ExtendCylinderCommand => _extendCylinderCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetCylinderAsync(extend: true),
            _ => CanExtendCylinder);

    public ICommand RetractCylinderCommand => _retractCylinderCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetCylinderAsync(extend: false),
            _ => CanRetractCylinder);

    public ICommand RunConveyorForwardCommand => _runConveyorForwardCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetConveyorAsync(
                true,
                ConveyorDirection.Forward),
            _ => CanRunConveyorForward);

    public ICommand RunConveyorReverseCommand => _runConveyorReverseCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.SetConveyorAsync(
                true,
                ConveyorDirection.Reverse),
            _ => CanRunConveyorReverse);

    public ICommand StopConveyorCommand => _stopConveyorCommand ??=
        CreateAsyncCommand(
            async _ => await _manualControlCommandWorkflow.StopConveyorAsync(),
            _ => CanStopConveyor);

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
            _layoutSelectionCommands.Nudge,
            _ => IsSceneEditable && !_isApplyingProject && Layout.SelectedItem?.Component is not null);

    public ICommand AlignLayoutSelectionCommand => _alignLayoutSelectionCommand ??=
        CreateRelayCommand(
            _layoutSelectionCommands.Align,
            _ => IsSceneEditable && !_isApplyingProject && Layout.HasMultipleSelection);

    public ICommand ChangeLayoutLayerOrderCommand => _changeLayoutLayerOrderCommand ??=
        CreateRelayCommand(
            _layoutSelectionCommands.ChangeLayerOrder,
            parameter => IsSceneEditable &&
                !_isApplyingProject &&
                parameter is string value &&
                Enum.TryParse(value, out LayoutLayerOrder order) &&
                Layout.CanChangeSelectionLayerOrder(order));

    public ICommand UndoLayoutEditCommand => _layoutAuthoringHistory.UndoCommand;

    public ICommand RedoLayoutEditCommand => _layoutAuthoringHistory.RedoCommand;

    public ICommand CopyLayoutSelectionCommand => _layoutAuthoringHistory.CopyCommand;

    public ICommand DuplicateLayoutSelectionCommand => _layoutAuthoringHistory.DuplicateCommand;

    public ICommand PasteLayoutSelectionCommand => _layoutAuthoringHistory.PasteCommand;

    public ICommand PreviousDryRunPlaybackStepCommand => DryRunPlayback.PreviousStepCommand;

    public ICommand NextDryRunPlaybackStepCommand => DryRunPlayback.NextStepCommand;

    public ICommand ExitDryRunPlaybackCommand => DryRunPlayback.ExitCommand;

    public ICommand ReturnToProcessPlanCommand => _processPlanReview.ReturnToProcessPlanCommand;

    public ICommand PreviousProcessPlanReviewStepCommand => _processPlanReview.PreviousStepCommand;

    public ICommand NextProcessPlanReviewStepCommand => _processPlanReview.NextStepCommand;

    public ICommand ExitCommand => _exitCommand ??= CreateRelayCommand(_ => _mainWpfInteractionHost.ShutdownApplication());

    #endregion

    #region Project File Operations

    internal Task<bool> OpenProjectAsync(string path) => _projectOpenWorkflow.OpenAsync(path);

    internal Task<bool> OpenProjectReplacingCurrentAsync(string path) =>
        _projectOpenWorkflow.OpenAsync(path, replaceCurrent: true);

    private async Task<bool> ApplyOpenedProjectAsync(MachineProjectDocument project, string path)
    {
        if (!await ApplyProjectAsync(project))
        {
            return false;
        }

        _projectSession.SetCurrentPath(path);
        RefreshProjectIdentity();
        CameraImageSourceEditor.Load(CurrentProject, CurrentProjectPath, _cameraSelection.SelectedCameraId);
        _scenarioBatch!.Restore();
        _multiAxisCommissioning.Restore();
        _visionExecutionEvidence.Restore();
        IsStartupChoiceVisible = false;
        Log("Project", $"Opened {project.Name}");
        return true;
    }

    internal async Task<bool> CreateNewProjectAsync()
    {
        if (!await TryResolveUnsavedChangesAsync()
            || !await ApplyProjectAsync(new MachineProjectDocument { Name = "Untitled" }))
        {
            return false;
        }

        _projectSession.SetCurrentPath(null);
        RefreshProjectIdentity();
        CameraImageSourceEditor.SetProjectPath(null, isSaved: true);
        _visionExecutionEvidence.Clear();
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

        _projectSession.SetCurrentPath(null);
        CameraImageSourceEditor.SetProjectPath(null, isSaved: true);
        IsStartupChoiceVisible = false;
        SelectedLeftToolTabIndex = 0;
        StatusMessage = OpenVisionLanguageService.T("Scene.SampleOpenedStatus");
        Log("Project", $"Opened bundled sample · {project.Name}");
        InvalidateCommands();
    }

    internal async Task SaveProjectAsync(string path)
    {
        await _mainWpfInteractionHost.CommitFocusedEditorAsync();
        _projectSession.SetCurrentPath(await _projectSaveWorkflow.SaveAsync(path));
        RefreshProjectIdentity();
        CameraImageSourceEditor.SetProjectPath(CurrentProjectPath, isSaved: true);
        NotifyCameraCommissioningChanged();
        AcceptCurrentProjectAsSaved();
        Log("Project", $"Saved {CurrentProject.Name}");
    }

    private async Task<bool> CreateSemiconductorRecipeCopyAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            destinationPath = _projectFileDialogHost.SelectRecipeCopyDestination(recipe.FileName);
            if (destinationPath is null)
            {
                return false;
            }
        }

        if (!await TryResolveUnsavedChangesAsync())
        {
            return false;
        }

        var copyPath = await _semiconductorRecipeCopyWorkflow.CopyAsync(
            recipe.SourcePath,
            destinationPath);
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
            if (!saveAs && CurrentProjectPath is not null)
            {
                await SaveProjectAsync(CurrentProjectPath);
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
            _mainMessageDialogHost.ShowProjectSaveFailure(exception.Message);
            return false;
        }
    }

    private async Task<bool> TrySaveProjectAsAsync()
    {
        await _mainWpfInteractionHost.CommitFocusedEditorAsync();
        if (_projectFileDialogHost.SelectProjectSaveAs(CurrentProject.Name) is not { } path)
        {
            return false;
        }

        await SaveProjectAsync(path);
        return true;
    }

    #endregion

    #region Layout Authoring And Scene Interaction

    private void AddLayoutComponent(object? parameter)
    {
        if (parameter is LayoutComponentKind kind)
        {
            TryAddLayoutComponent(kind);
        }
    }

    public bool TryAddLayoutComponent(
        LayoutComponentKind kind,
        double? worldX = null,
        double? worldY = null) =>
        _layoutAuthoringMutationWorkflow.TryAdd(kind, worldX, worldY);

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


    private void DeleteSelectedLayoutComponent() =>
        _layoutAuthoringMutationWorkflow.TryRemoveSelected();

    private void RefreshDefinitionPresentation(string? selectedComponentId)
    {
        _selectionSynchronization.ClearAnalogEditor();
        ProjectTree.LoadProject(CurrentProject);
        Layout.Load(CurrentProject);
        if (selectedComponentId is not null)
        {
            Layout.Select(selectedComponentId);
        }
        RecipeConnections.Load(CurrentProject, Layout.SelectedItem?.Id);
        SequenceEditor.RefreshAuthoringTargets();
        Properties.Show(Layout.SelectedItem?.Component);
        OnPropertyChanged(nameof(AxisCountText));
        OnPropertyChanged(nameof(LayoutComponentCountText));
        OnPropertyChanged(nameof(CameraCountText));
        OnPropertyChanged(nameof(HasAuthoredLayout));
        OnPropertyChanged(nameof(SelectionStatusText));
        InvalidateCommands();
    }

    #endregion

    #region Project Application And Runtime Definition

    private Task<bool> ApplyProjectAsync(MachineProjectDocument project) =>
        _projectRuntimeApplicationWorkflow.ApplyAsync(project);

    private void OnProjectRuntimeApplicationStateChanged(bool isApplying)
    {
        _isApplyingProject = isApplying;
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        UpdateRunToolAvailability();
        InvalidateCommands();
    }

    private void OnProjectRuntimeApplicationRejected(RuntimeDefinitionApplicationResult result)
    {
        if (result.Outcome == RuntimeDefinitionApplicationOutcome.CompilationRejected)
        {
            Log("Project", $"Project rejected · {result.CompilationDetail}");
        }
        else if (result.CommandResult is { } commandResult)
        {
            Log("Project", $"Project rejected · {commandResult.ErrorCode}: {commandResult.Detail}");
        }
    }

    private void CompleteProjectRuntimeApplication(MachineProjectDocument project)
    {
        _projectSession.ReplaceProject(project);
        _unifiedCommissioningEvidence.Reset();
        _scenarioBatch!.Reset();
        _simulationCommandTrace.Reset();
        _multiAxisCommissioning.Reset();
        _visionExecutionEvidence.Clear();
        ApplyProjectPresentation(project);
        RuntimeDebugger.LoadProject(project, resetSession: true);
        _layoutAuthoringHistory.Reset();
        _runtimeDefinitionDirty = false;
        UpdateRunToolAvailability();
        IsRunning = false;
        IsDesignMode = true;
        ApplyMonitorSnapshot(_engine.CurrentSnapshot);
        AcceptCurrentProjectAsSaved();
    }

    private async Task<bool> EnsureRuntimeDefinitionAppliedAsync()
    {
        if (!_runtimeDefinitionDirty)
        {
            return true;
        }

        var result = await _runtimeDefinitionApplicationWorkflow.ApplyAsync(CurrentProject);
        if (!result.IsAccepted)
        {
            StatusMessage = "Machine definition is invalid";
            if (result.Outcome == RuntimeDefinitionApplicationOutcome.CompilationRejected)
            {
                Log("Project", $"Simulation build rejected · {result.CompilationDetail}");
            }
            else if (result.CommandResult is { } commandResult)
            {
                Log("Project", $"Simulation build rejected · {commandResult.ErrorCode}: {commandResult.Detail}");
            }
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
        _processPlanReview.Clear();
        _cameraSelection.EnsureSelectionFor(project);
        CameraImageSourceEditor.Load(project, CurrentProjectPath, _cameraSelection.SelectedCameraId);
        _selectionSynchronization.ClearEditors();
        ProjectTree.LoadProject(project);
        Layout.Load(project);
        RecipeConnections.Load(project, Layout.SelectedItem?.Id);
        SequenceEditor.Load(project);
        SimulationWorkspace.LoadProjectScenario(project.Simulation);
        MultiAxisCommissioningRecipe.Load(project);
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
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

    private static SimulationRuntimeConfiguration BuildRuntimeConfiguration(MachineProjectDocument project)
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

    #endregion

    #region Runtime Projection

    private void RefreshManualEquipmentProjection(SimulationSnapshot? snapshot = null)
    {
        var selected = Layout.SelectedItem;
        _manualEquipmentPresentation.ApplyProjection(
            new ManualEquipmentProjection(
                snapshot ?? PresentationSnapshot,
                selected?.Id,
                selected?.Component?.Kind,
                IsRunMode,
                _isApplyingProject,
                IsValidationBusy,
                _runtimeDefinitionDirty,
                IsRunning,
                RuntimeProjection.ControlOwner,
                RuntimeProjection.AutomaticRun.IsActive,
                RuntimeProjection.CurrentSequence?.Status));
    }

    private void RefreshCameraCommissioningProjection()
    {
        var cameraDefinition = CurrentCameraDefinition;
        var fallbackCameraName = CurrentProject.Devices
            .FirstOrDefault(device => device.Kind == DeviceKind.Camera)
            ?.Name;
        _cameraCommissioningPresentation.ApplyProjection(
            new CameraCommissioningProjection(
                RuntimeProjection.CurrentCamera,
                cameraDefinition is not null,
                fallbackCameraName,
                cameraDefinition?.Camera?.SingleImageSource,
                CurrentProjectPath,
                _cameraSelection.SelectedCameraRecipe,
                _engine.CurrentSnapshot.RunMode,
                IsRunMode,
                _isApplyingProject,
                IsValidationBusy,
                _runtimeDefinitionDirty,
                IsRunning,
                RuntimeProjection.ControlOwner,
                RuntimeProjection.AutomaticRun.IsActive,
                RuntimeProjection.CurrentSequence?.Status));
    }

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
            _cameraSelection.SelectedCameraId ?? projectCameraId,
            SimulationWorkspace.ScheduledFaultKind,
            ActiveSequenceId);
    }

    private void ApplyMonitorSnapshot(SimulationSnapshot snapshot)
    {
        _runtimeProjectionCoordinator.Apply(
            snapshot,
            CreateRuntimeProjectionSelection());
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
        OnPropertyChanged(nameof(RunStatusText));
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
        && _engine is FixedStepSimulationEngine traceEngine
        && traceEngine.CommandTrace.Length > 0;

    private bool CanImportUnifiedCommissioningEvidenceCore() => CanImportSimulationEvidence
        && !_visionExecutionEvidence.IsCapturing;

    private DeterministicSimulationEvidenceExchangePackage?
        CreateSimulationEvidenceForUnifiedCommissioning() =>
        _scenarioBatch?.LatestBatchResult is { } batchResult
            ? DeterministicSimulationEvidenceExchangePackage.Create(
                batchResult,
                _scenarioBatch.AcceptedBatchBaseline)
            : null;

    private DeterministicSimulationCommandTracePackage? CreateCommandTraceForUnifiedCommissioning() =>
        _engine is FixedStepSimulationEngine traceEngine
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
                _projectSession.SerializeForEvidence(),
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
        _visionExecutionEvidence.SetImportedEvidence(visionEvidence);
    }

    private VisionEvidenceContext CreateVisionEvidenceContext() =>
        new(
            CurrentProject.Id,
            _projectSession.SerializeForEvidence(),
            BuildIdentity.Current,
            CurrentProjectPath,
            _cameraSelection.SelectedCameraId,
            _cameraSelection.SelectedCameraRecipe);

    private DeterministicVisionExecutionEvidencePackage? GetCurrentUnifiedCommissioningVisionEvidence() =>
        _visionExecutionEvidence.GetCurrentEvidence();

    #endregion

    #region Scenario Presentation

    private void NavigateToBatchMismatch(DeterministicSimulationBatchMismatch mismatch)
    {
        Layout.Select(mismatch.TargetId);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.BatchMismatchNavigationStatus"),
            mismatch.TargetId,
            mismatch.ObservedTickIndex);
        Log(
            "Batch",
            $"First mismatch selected · {mismatch.EvidenceKind} · {mismatch.TargetId} · Tick {mismatch.ObservedTickIndex}");
    }

    private void OnScenarioBatchPresentationChanged(bool invalidateCommands)
    {
        OnPropertyChanged(nameof(IsBatchRunning));
        OnPropertyChanged(nameof(IsScenarioConfigurationEnabled));
        OnPropertyChanged(nameof(CanValidateMultiAxisCommissioningRecipe));
        OnPropertyChanged(nameof(IsCommissioningValidationConfigurationEnabled));
        OnPropertyChanged(nameof(BatchCompletedRuns));
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
        OnPropertyChanged(nameof(CanImportSimulationEvidence));
        OnPropertyChanged(nameof(CanStartTestScenario));
        OnPropertyChanged(nameof(CanStopTestScenario));
        OnPropertyChanged(nameof(CanReplayTestScenario));
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

        NotifyAxisCommissioningChanged(invalidateCommands: false);
        RefreshProjectDirtyState();
    }

    private void RefreshProjectDirtyState()
    {
        if (_projectSession.RefreshDirtyState())
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshProjectIdentity();
        }
    }

    private void AcceptCurrentProjectAsSaved()
    {
        if (_projectSession.AcceptAsSaved())
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshProjectIdentity();
        }
    }

    internal async Task<bool> TryResolveUnsavedChangesAsync()
    {
        await _mainWpfInteractionHost.CommitFocusedEditorAsync();
        SimulationWorkspace.SaveProjectScenario(CurrentProject.Simulation);
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

    private void HandleProjectOpenFailure(Exception exception)
    {
        StatusMessage = OpenVisionLanguageService.T(
            "Project.OpenFailedStatus",
            "프로젝트를 열지 못했습니다",
            "The project could not be opened");
        Log("Project", $"Open failed · {exception.Message}");
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
    }

    private void ResetUnifiedCommissioningEvidenceForTraceCapture()
        => _unifiedCommissioningEvidence.Reset();

    #endregion

    #region Scenario Runtime Coordination

    private async Task<bool> PauseRuntimeForScenarioBatchAsync()
    {
        try
        {
            var command = new PauseCommand();
            var result = await _engine.EnqueueCommandAsync(command);
            if (!result.IsAccepted)
            {
                Log("Batch", $"Main runtime pause rejected · {result.ErrorCode}: {result.Detail}");
                return false;
            }

            IsRunning = false;
            Log("Batch", $"Main runtime paused before sequential batch · {ShortCommandId(command)}");
            return true;
        }
        catch (OperationCanceledException) when (_runtimeLoop.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
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
        catch (OperationCanceledException) when (_runtimeLoop.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleCommandException(exception);
        }
    }

    #endregion

    #region Selection Synchronization And Sequence Presentation

    private void OnProjectTreeSelectionPresentationChanged(bool isAxisSelection)
    {
        OnPropertyChanged(nameof(IsMultiAxisCommissioningRecipeSelection));
        OnPropertyChanged(nameof(SelectionStatusText));
        if (isAxisSelection)
        {
            ApplyMonitorSnapshot(SceneSnapshots.Latest ?? _engine.CurrentSnapshot);
            return;
        }

        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        InvalidateCommands();
    }

    private void OnLayoutSelectionPresentationChanged()
    {
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(HasSelectedEquipment));
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
        RefreshManualEquipmentProjection(SceneSnapshots.Latest ?? _engine.CurrentSnapshot);
        RefreshCameraCommissioningProjection();
        NotifyManualCommissioningChanged(invalidateCommands: false);
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        NotifySensorCommissioningChanged(invalidateCommands: false);
        NotifyCylinderCommissioningChanged(invalidateCommands: false);
        NotifyConveyorCommissioningChanged(invalidateCommands: false);
        InvalidateCommands();
    }

    private void OnSelectionSynchronizationPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
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
        ExitDryRunPlayback();
        RecipeConnections.Load(CurrentProject, Layout.SelectedItem?.Id);
        Properties.Show(Layout.SelectedItem?.Component);
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
        StatusMessage = "Layout changed; Simulation ON will rebuild the runtime";
    }

    private void OnAxisDefinitionChanged()
    {
        ExitDryRunPlayback();
        MarkProjectChanged();
        _multiAxisCommissioning.InvalidateContextIfResult();
        UpdateRunToolAvailability();
        Properties.Show(AxisDriveTuningEditor is null
            ? null
            : CurrentProject.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, AxisDriveTuningEditor.Id, StringComparison.Ordinal)));
        NotifyAxisCommissioningChanged(invalidateCommands: false);
        MultiAxisCommissioningRecipe.ApplyAxisSnapshots(
            (SceneSnapshots.Latest ?? _engine.CurrentSnapshot).Axes);
        _multiAxisCommissioning.NotifyRuntimeChanged(invalidateCommands: false);
        StatusMessage = "Axis tuning changed; Simulation ON will validate and rebuild the runtime";
        InvalidateCommands();
    }

    private void OnAnalogChannelDefinitionChanged()
    {
        ExitDryRunPlayback();
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
        MarkProjectChanged(requiresRuntimeRebuild: false);
        Properties.ShowNode(ProjectTree.SelectedNode);
        NotifyMultiAxisCommissioningRecipeChanged(recipeChanged: true);
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
            ProjectTree.LoadProject(CurrentProject);
        }
        RecipeConnections.RefreshDefinitionPreservingProcessBlockPlan(Layout.SelectedItem?.Id);
        RuntimeDebugger.LoadProject(CurrentProject, resetSession: false);

        StatusMessage = "Sequence changed; Simulation ON will validate and rebuild the runtime";
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

    private void RefreshIntegrationContext()
    {
        var frame = RuntimeProjection.CurrentCamera?.Result?.FrameEvidence ??
            RuntimeProjection.CurrentCamera?.FrameEvidence;
        var contextKey = string.Join(
            "\u001F",
            CurrentProject.Id,
            CurrentProjectPath ?? string.Empty,
            _cameraSelection.SelectedCameraId ?? string.Empty,
            _cameraSelection.SelectedCameraRecipe ?? string.Empty,
            RuntimeProjection.CurrentCamera?.State.ToString() ?? string.Empty,
            RuntimeProjection.CurrentCamera?.CurrentAcquisitionId ?? string.Empty,
            frame?.FrameId ?? string.Empty,
            frame?.ContentSha256 ?? string.Empty,
            frame?.SourceRelativePath ?? string.Empty);
        if (string.Equals(_integrationContextKey, contextKey, StringComparison.Ordinal))
        {
            return;
        }

        _integrationContextKey = contextKey;
        Integration.RefreshContext();
    }

    #endregion

    #region Integration And Camera

    private bool CanBuildTwoDIntegrationRequest(
        string recipePath,
        IntegrationApplicationIdentity consumer) =>
        CreateTwoDIntegrationRequest(recipePath, consumer) is not null;

    private MachineInspectionHandoffRequest? CreateTwoDIntegrationRequest(
        string recipePath,
        IntegrationApplicationIdentity consumer)
        => _integrationRequestWorkflow.TryCreate(
            new MachineIntegrationRequestContext(
                BuildIdentity.IsExactCommit,
                CurrentProject.Id,
                CurrentProject.Schema,
                CurrentProject.Sequences,
                CurrentProjectPath,
                _cameraSelection.SelectedCameraId,
                _cameraSelection.SelectedCameraRecipe,
            RuntimeProjection.CurrentCamera,
                CurrentCameraDefinition?.Camera?.SingleImageSource,
                BuildIdentity.IntegrationIdentity,
                consumer),
            recipePath);

    private ManualCameraTriggerRequest? CreateManualCameraTriggerRequest()
    {
        var cameraDefinition = CurrentCameraDefinition?.Camera;
        var sourceDefinition = cameraDefinition?.SingleImageSource;
        return _manualCameraTriggerRequestFactory.TryCreate(
            new ManualCameraTriggerRequestInput(
                CanTriggerCamera,
                CurrentProject.Id,
                CurrentProject.Name,
                CurrentProjectPath,
                _projectSession.SerializeForEvidence(),
                BuildIdentity.Current,
                SimulationFixedStep,
                _engine.CurrentSnapshot,
                RuntimeProjection.CurrentCamera,
                _cameraSelection.SelectedCameraRecipe,
                cameraDefinition,
                sourceDefinition,
                CurrentProject.Simulation.Seed));
    }

    private async Task TriggerSelectedCameraAsync()
    {
        var request = CreateManualCameraTriggerRequest();
        if (request is null)
        {
            return;
        }

        var result = await _manualCameraTriggerWorkflow.ExecuteAsync(
            request,
            _runtimeLoop.CancellationToken);
        PresentManualCameraTriggerFailure(result);
    }

    private void PresentManualCameraTriggerFailure(ManualCameraTriggerResult result)
    {
        switch (result.Outcome)
        {
            case ManualCameraTriggerOutcome.SourceRejected:
                StatusMessage = OpenVisionLanguageService.T("Camera.StatusRejected");
                Log("Camera", string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Camera.SourceRejected"),
                    result.Detail ?? string.Empty));
                break;
            case ManualCameraTriggerOutcome.InspectionRejected:
                StatusMessage = OpenVisionLanguageService.T("Camera.StatusRejected");
                Log("Camera", string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Camera.InspectionRejected"),
                    result.Detail ?? string.Empty));
                break;
            case ManualCameraTriggerOutcome.ContextChanged:
                StatusMessage = OpenVisionLanguageService.T("Camera.StatusRejected");
                Log("Camera", OpenVisionLanguageService.T("Camera.ContextChanged"));
                break;
        }
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

        var result = await _multiAxisCommissioningExecutionWorkflow.ExecuteAsync(
            MultiAxisCommissioningRecipe.Targets.Select(target =>
                new AxisMoveTarget(target.AxisId, target.TargetPosition)));

        if (result.PausedBeforeExecution)
        {
            IsRunning = false;
        }

        if (result.Outcome == MultiAxisCommissioningExecutionOutcome.PauseRejected
            && result.RejectedCommand is not null)
        {
            var pause = result.RejectedCommand;
            Log("Motion", $"Recipe preparation rejected · {pause.ErrorCode}: {pause.Detail}");
            return;
        }

        if (result.IsAccepted)
        {
            IsRunning = true;
        }
    }

    private Task StopMultiAxisCommissioningRecipeAsync() => _equipmentCommandDispatcher.DispatchAxisCommandAsync(
        new StopAxesCommand(MultiAxisCommissioningRecipe.Targets.Select(target => target.AxisId)),
        "Axis.ActionStopRecipe");

    private void NavigateToCommissioningMismatch(DeterministicCommissioningMismatch mismatch)
    {
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

    #endregion

    #region Runtime Workspace

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

    private void OpenConnectionSequenceStep(string sequenceId, string stepId)
    {
        _processPlanReview.Clear();
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

    private void OpenProcessBlockSequenceStep(string sequenceId, string stepId) =>
        _processPlanReview.OpenProcessBlockSequenceStep(sequenceId, stepId);

    private string? TryOpenConnectionSequenceStepForReview(string sequenceId, string stepId) =>
        TryOpenConnectionSequenceStep(sequenceId, stepId)
            ? SequenceEditor.SelectedStep?.DisplayName
            : null;

    private void OnProcessBlockPreviewClosed(object? sender, EventArgs args) =>
        _processPlanReview.Clear();

    private void OnProcessPlanReviewPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
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

    private void ShowConnectionDryRunStep(RecipeDryRunStepPresentation step)
    {
        DryRunPlayback.Show(step, RecipeConnections.DryRun.Timeline);
    }

    private void ExitDryRunPlayback()
    {
        DryRunPlayback.Exit();
    }

    private void OnDryRunPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsDryRunPlaybackActive));
        OnPropertyChanged(nameof(IsSceneEditable));
        OnPropertyChanged(nameof(SceneSnapshotSource));
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

    private int ApplyConnectionProcessBlockTimeouts(
        SemiconductorManagedTimeoutAdjustmentPreview preview)
    {
        ExitDryRunPlayback();
        var result = _recipeConnectionProjectApplier.ApplyProcessBlockTimeouts(CurrentProject, preview);
        if (!result.Changed)
        {
            StatusMessage = OpenVisionLanguageService.T("Connections.ProcessBlockTimeoutRejectedStatus");
            return 0;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        SequenceEditor.Load(CurrentProject);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessBlockTimeoutAppliedStatus"),
            result.AppliedStepCount,
            preview.ProposedTimeoutMs);
        RecipeConnections.RefreshDefinitionPreservingProcessBlockPlan(Layout.SelectedItem?.Id);
        Log(
            "Sequence",
            $"Applied managed timeout adjustment · {result.AppliedStepCount} step(s) · {preview.ProposedTimeoutMs} ms");
        InvalidateCommands();
        return result.ChangeCount;
    }

    private void CompleteConnectionSetupMutation()
    {
        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        SequenceEditor.RefreshAuthoringTargets();
        _layoutAuthoringHistory.Reset();
        InvalidateCommands();
    }

    private void CompleteConnectionProcessBlockMutation()
    {
        MarkProjectChanged();
        UpdateRunToolAvailability();
        RefreshDefinitionPresentation(null);
        SequenceEditor.Load(CurrentProject);
        _layoutAuthoringHistory.Reset();
        InvalidateCommands();
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

    #endregion

    #region Commissioning Presentation

    private void NotifyAxisCommissioningChanged(bool invalidateCommands = true)
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
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private void OnMultiAxisCommissioningPresentationChanged(bool invalidateCommands)
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
        RefreshCameraCommissioningProjection();
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
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
        RefreshIntegrationContext();
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
        _visionExecutionEvidence.RefreshContext();
        NotifyCameraCommissioningChanged();
    }

    private void NotifyManualCommissioningChanged(bool invalidateCommands = true)
    {
        RefreshManualEquipmentProjection();
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
        if (HasSelectedManualEquipment)
        {
            OnPropertyChanged(nameof(CanStartManualEquipmentControl));
        }
        if (HasSelectedAxisDefinition || HasSelectedAxisStage)
        {
            NotifyAxisCommissioningChanged(invalidateCommands: false);
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
            _multiAxisCommissioning.NotifyRuntimeChanged(invalidateCommands: false);
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
        RuntimeDebugger.SetEnabled(isEnabled, invalidateCommands: false);
    }

    #endregion

    #region Virtual Camera Workflow

    private bool ApplyConnectionVirtualCameraWorkflow()
    {
        ExitDryRunPlayback();
        var result = _virtualCameraInspectionTemplate.Apply(CurrentProject);
        if (!result.Created)
        {
            return false;
        }

        MarkProjectChanged();
        UpdateRunToolAvailability();
        ApplyProjectPresentation(CurrentProject);
        RuntimeDebugger.LoadProject(CurrentProject, resetSession: false);
        _layoutAuthoringHistory.Reset();
        _cameraSelection.SelectVirtualCamera(result.CameraId);
        OpenConnectionSequenceStep(result.SequenceId, result.TriggerStepId);
        StatusMessage = OpenVisionLanguageService.T("Connections.VirtualCameraWorkflowCreatedStatus");
        Log(
            "Project",
            $"Created virtual-camera inspection workflow · {result.CameraId} · {result.SequenceId}");
        InvalidateCommands();
        return true;
    }

    #endregion

    #region Runtime Command And Logging

    private async Task<SimulationCommandResult> DispatchRuntimeDebuggerCommandAsync(
        SimulationCommand command)
        => await _simulationCommandPresentationDispatcher.DispatchRuntimeDebuggerAsync(
            command,
            () => ApplyMonitorSnapshot(_engine.CurrentSnapshot));

    private void Log(string category, string message) =>
        AppendLog(RuntimeProjection.SimulationTime, category, message);

    internal void AppendLog(TimeSpan time, string category, string message)
    {
        _runtimeObservabilityPresenter.Append(
            time,
            category,
            message,
            RuntimeProjection.TickIndex);
    }

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
        _visionExecutionEvidence.RefreshLocalization();
        AxisCommissioning.RefreshLocalization();
        AxisDriveTuningEditor?.RefreshLocalization();
        MultiAxisCommissioningRecipe.RefreshLocalization();
        _multiAxisCommissioning.RefreshLocalization();
        _scenarioBatch?.RefreshLocalization();
        SemiconductorRecipes.RefreshLocalization();
        RecipeConnections.RefreshLocalization();
        Layout.RefreshLocalization();
        Integration.RefreshLocalization();
        DigitalIo.RefreshLocalization();
        AnalogIoAuthoring?.RefreshLocalization();
        FaultManager.RefreshLocalization();
        RuntimeDebugger.RefreshLocalization();
        SequenceEditor.RefreshLocalization();
    }

    #endregion

    #region Command Infrastructure

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
        RaiseCanExecuteChanged(_abortSequenceCommand);
        RaiseCanExecuteChanged(_retrySequenceCommand);
        RaiseCanExecuteChanged(_stepCommand);
        RaiseCanExecuteChanged(_resetCommand);
        RaiseCanExecuteChanged(_startTestScenarioCommand);
        RaiseCanExecuteChanged(_stopTestScenarioCommand);
        RaiseCanExecuteChanged(_replayTestScenarioCommand);
        RaiseCanExecuteChanged(_exportSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_importSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_exportUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_importUnifiedCommissioningEvidenceCommand);
        _simulationCommandTrace.InvalidateCommands();
        _multiAxisCommissioning.InvalidateCommands();
        _scenarioBatch?.InvalidateCommands();
        RaiseCanExecuteChanged(_cycleStartCommand);
        RaiseCanExecuteChanged(_startManualEquipmentControlCommand);
        RaiseCanExecuteChanged(_startManualCameraControlCommand);
        RaiseCanExecuteChanged(_triggerCameraCommand);
        AxisCommissioning.InvalidateCommands();
        RaiseCanExecuteChanged(_runMultiAxisCommissioningRecipeCommand);
        RaiseCanExecuteChanged(_stopMultiAxisCommissioningRecipeCommand);
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
        _layoutAuthoringHistory.InvalidateCommands();
        DryRunPlayback.InvalidateCommands();
        _processPlanReview.InvalidateCommands();
        RuntimeDebugger.InvalidateCommands();

        if (includeCommandManager)
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void InvalidateModeCommands()
    {
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
        RaiseCanExecuteChanged(_importSimulationEvidenceCommand);
        RaiseCanExecuteChanged(_exportUnifiedCommissioningEvidenceCommand);
        RaiseCanExecuteChanged(_importUnifiedCommissioningEvidenceCommand);
        _simulationCommandTrace.InvalidateCommands();
        _multiAxisCommissioning.InvalidateCommands();
        _scenarioBatch?.InvalidateCommands();
        RaiseCanExecuteChanged(_cycleStartCommand);
        AxisCommissioning.InvalidateCommands();
        RaiseCanExecuteChanged(_addLayoutComponentCommand);
        RaiseCanExecuteChanged(_deleteLayoutComponentCommand);
        RaiseCanExecuteChanged(_nudgeLayoutComponentCommand);
        RaiseCanExecuteChanged(_alignLayoutSelectionCommand);
        RaiseCanExecuteChanged(_changeLayoutLayerOrderCommand);
        _layoutAuthoringHistory.InvalidateCommands();
        _processPlanReview.InvalidateCommands();
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
        if (_disposed || _runtimeShutdownWorkflow.IsShutdownRequested)
        {
            return;
        }

        StatusMessage = "Command failed";
        Log("Error", exception.Message);
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
        CancellationToken cancellationToken = default) =>
        _runtimeShutdownWorkflow.ShutdownAsync(
            timeout ?? RuntimeShutdownTimeout,
            cancellationToken);

    private void RecordShutdownDiagnostic(SimulationRuntimeShutdownDiagnostic diagnostic) =>
        RecordShutdownDiagnostic(
            diagnostic.Kind,
            diagnostic.Severity,
            diagnostic.Message,
            diagnostic.Stage,
            diagnostic.Termination,
            diagnostic.Exception);

    private void RecordShutdownDiagnostic(
        SimulationOperationalDiagnosticKind kind,
        SimulationLogSeverity severity,
        string message,
        string stage,
        SimulationEngineTerminationResult? termination = null,
        Exception? exception = null)
    {
        _runtimeObservabilityPresenter.RecordShutdownDiagnostic(
            kind,
            severity,
            message,
            stage,
            RuntimeProjection.TickIndex,
            RuntimeProjection.SimulationTime,
            termination,
            exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        _selectionSynchronization.PropertyChanged -= OnSelectionSynchronizationPropertyChanged;
        _selectionSynchronization.Dispose();
        SimulationWorkspace.PropertyChanged -= OnSimulationWorkspacePropertyChanged;
        RecipeConnections.ProcessBlocks.ProcessBlockPreviewClosed -= OnProcessBlockPreviewClosed;
        _processPlanReview.PropertyChanged -= OnProcessPlanReviewPropertyChanged;
        _simulationCommandTrace.PropertyChanged -= OnSimulationCommandTracePropertyChanged;
        _layoutAuthoringHistory.Dispose();
        SequenceEditor.DefinitionChanged -= OnSequenceDefinitionChanged;
        DryRunPlayback.PropertyChanged -= OnDryRunPlaybackPropertyChanged;
        Integration.Dispose();

        var shutdownTask = ShutdownAsync(cancellationToken: CancellationToken.None);
        _runtimeShutdownWorkflow.CompleteDisposeAfterShutdown(shutdownTask);
    }

    #endregion
}
