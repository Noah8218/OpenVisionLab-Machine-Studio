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
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
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
        nameof(CurrentCameraSourceModeText),
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

    private readonly ProjectLifecycleCoordinator _projectLifecycle;
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

    #endregion

    #region State

    private string _title = "OpenVisionLab Machine Studio";
    private string _statusMessage = "Ready";
    private bool _isRunning;
    private bool _isDesignMode = true;
    private bool _isApplyingProject;
    private bool _runtimeDefinitionDirty;
    private bool _sessionCloseRequested;
    private bool _disposed;

    #endregion

    #region Command Backing Fields

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
        string? integrationSettingsPath = null)
    {
        OpenVisionLanguageService.Load();
        UnsavedProjectPrompt = _mainMessageDialogHost.ShowUnsavedProjectPrompt;
        ProjectOpenFailurePresenter = _mainMessageDialogHost.ShowProjectOpenFailure;
        _simulationSession = new(SimulationFixedStep);
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
            () => OpenVisionLanguageService.T("Gallery.TemplateOverwriteRejected"));
        _shellNavigation = new(
            initialProject is null && _projectLifecycle.HasStartupSample,
            () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsValidationBusy,
            () => !_disposed && !_sessionCloseRequested && !_isApplyingProject && !IsValidationBusy &&
                  _projectLifecycle.HasStartupSample,
            OpenBundledSampleAsync,
            OnBlankLayoutStarted,
            HandleCommandException);
        _shellNavigation.PropertyChanged += OnShellNavigationPropertyChanged;
        _shellNavigation.LanguageChanged += OnLanguageChanged;
        var initialRuntime = BuildRuntimeConfiguration(CurrentProject);
        _runtimeDefinitionApplicationWorkflow = new(
            _simulationSession.Engine,
            SimulationFixedStep);
        _projectRuntimeApplicationWorkflow = new(
            _runtimeDefinitionApplicationWorkflow,
            OnProjectRuntimeApplicationStateChanged,
            OnProjectRuntimeApplicationRejected,
            CompleteProjectRuntimeApplication);
        _equipmentCommandDispatcher = new(
            _simulationSession.Engine,
            status => StatusMessage = status,
            _runtimeObservabilityJournal.Log);
        _simulationCommandPresentationDispatcher = new(
            _simulationSession.Engine,
            status => StatusMessage = status,
            _runtimeObservabilityJournal.Log);
        _multiAxisCommissioningExecutionWorkflow = new(
            _simulationSession.Engine,
            _equipmentCommandDispatcher);
        AxisCommissioning = new AxisCommissioningViewModel(
            _equipmentCommandDispatcher.DispatchAxisCommandAsync,
            HandleCommandException);

        ProjectTree = new ProjectTreeViewModel();
        Properties = new PropertiesViewModel();
        Layout = new MachineLayoutViewModel();
        _manualEquipment = new(
            _equipmentCommandDispatcher,
            CreateManualEquipmentProjection,
            () => Layout.SelectedItem?.Component?.Kind,
            () => IsRunning = true,
            HandleCommandException);
        _manualEquipment.PropertyChanged += OnManualEquipmentPropertyChanged;
        SequenceEditor = new SequenceEditorViewModel();
        _recipeAuthoring = new RecipeAuthoringWorkspace(
            Layout,
            SequenceEditor,
            () => CurrentProject,
            () => IsDesignMode,
            project => { BuildRuntimeConfiguration(project); },
            tabIndex => Navigation.SelectedDocumentTabIndex = tabIndex,
            () => MarkProjectChanged(),
            UpdateRunToolAvailability,
            () => RefreshDefinitionPresentation(null),
            () => _layoutAuthoring!.Reset(),
            RefreshVirtualCameraWorkflowPresentation,
            () => InvalidateCommands(),
            status => StatusMessage = status,
            _runtimeObservabilityJournal.Log);
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
            status => StatusMessage = status,
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
            value => IsDesignMode = value,
            value => IsRunning = value,
            status => StatusMessage = status,
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
            status => StatusMessage = status,
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
            status => StatusMessage = status,
            message => _runtimeObservabilityJournal.Log("Batch", message),
            NavigateToBatchMismatch,
            OnScenarioBatchPresentationChanged,
            HandleCommandException);
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
            () => MarkProjectChanged(requiresRuntimeRebuild: false),
            status => StatusMessage = status,
            _runtimeObservabilityJournal.Log,
            RefreshIntegrationContext,
            OpenVisionLanguageService.T,
            HandleCommandException);
        _camera.PropertyChanged += OnCameraPropertyChanged;
        _unifiedCommissioningEvidence = new UnifiedCommissioningEvidenceViewModel(
            CanExportUnifiedCommissioningEvidenceCore,
            CanImportUnifiedCommissioningEvidenceCore,
            CreateSimulationEvidenceForUnifiedCommissioning,
            CreateCommandTraceForUnifiedCommissioning,
            GetCurrentUnifiedCommissioningVisionEvidence,
            CreateUnifiedCommissioningEvidenceContext,
            ApplyImportedUnifiedCommissioningArtifacts,
            status => StatusMessage = status,
            message => _runtimeObservabilityJournal.Log("Batch", message),
            RaiseUnifiedCommissioningEvidencePresentationChanged);
        Integration = new MachineIntegrationViewModel(
            CaptureIntegrationRequestContext,
            () => BuildIdentity.IntegrationIdentity,
            () => CurrentProject.Id,
            integrationSettingsPath,
            _mainWpfInteractionHost.DispatchOnUiThreadAsync);
        SemiconductorRecipes = new SemiconductorRecipeGalleryViewModel(
            CreateSemiconductorRecipeCopyAsync);
        DryRunPlayback.PropertyChanged += OnDryRunPlaybackPropertyChanged;
        DigitalIo = new DigitalIoCommissioningViewModel(
            _simulationCommandPresentationDispatcher.DispatchDigitalIoAsync);
        FaultManager = new FaultManagerViewModel(
            _simulationCommandPresentationDispatcher.DispatchFaultAsync);
        RuntimeDebugger = new RuntimeDebuggerViewModel(DispatchRuntimeDebuggerCommandAsync);
        _runtimeObservabilityPresenter = new(
            _runtimeObservabilityJournal,
            _camera.VisionEvidence,
            RuntimeDebugger);
        LogMessages = _runtimeObservabilityPresenter.LogMessages;
        _layoutAuthoring = new LayoutAuthoringWorkspace(
            Layout,
            () => CurrentProject,
            () => IsSceneEditable,
            () => _isApplyingProject,
            () => !_disposed && !_sessionCloseRequested,
            () => MarkProjectChanged(),
            UpdateRunToolAvailability,
            RefreshDefinitionPresentation,
            () => InvalidateCommands(),
            status => StatusMessage = status,
            _runtimeObservabilityJournal.Log,
            OnLayoutDefinitionChanged);
        _runtimeProjectionCoordinator = new(
            MultiAxisCommissioningRecipe,
            SimulationWorkspace,
            DigitalIo,
            FaultManager,
            RuntimeDebugger,
            _camera.VisionEvidence,
            value => IsRunning = value,
            snapshot => RefreshManualEquipmentProjection(snapshot),
            () => _camera.RefreshProjection());
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
            status => StatusMessage = status);
        _selectionSynchronization.PropertyChanged += OnSelectionSynchronizationPropertyChanged;
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
        AcceptCurrentProjectAsSaved();
        _runtimeObservabilityJournal.Log("System", "Deterministic machine runtime ready · fixed step 5 ms");

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
                ActiveSequenceId),
            EnsureRuntimeDefinitionApplied = EnsureRuntimeDefinitionAppliedAsync,
            SetDesignMode = value => IsDesignMode = value,
            SetRunning = value => IsRunning = value,
            ApplySnapshot = ApplyMonitorSnapshot,
            CancelVisionCapture = CancelManualCameraPreparation,
            SetStatus = status => StatusMessage = status,
            Log = _runtimeObservabilityJournal.Log,
            NotifyCommandsChanged = () => InvalidateCommands(),
            Dispatch = _mainWpfInteractionHost.DispatchAsync,
            PublishSnapshot = snapshot => SceneSnapshots.Publish(snapshot),
            OnInitialRuntimeApplied = () =>
            {
                ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
                _scenarioBatch!.Restore();
            },
            OnInitialConfigurationRejected = detail => _runtimeObservabilityJournal.Log(
                "Runtime",
                $"Initial configuration rejected · {detail}"),
            OnRuntimeEvent = _runtimeObservabilityPresenter.RecordRuntimeEventPresentation,
            OnTerminated = _runtimeObservabilityPresenter.RecordEngineTermination,
            OnUnhandledException = HandleCommandException,
            OnCanonicalEvent = runtimeEvent => _runtimeObservabilityPresenter.RecordCanonicalRuntimeEvent(
                runtimeEvent,
                _simulationSession.Engine.CurrentSnapshot),
            OnCanonicalJournalCompleted = _runtimeObservabilityPresenter.RecordCanonicalEventJournalCompleted,
            Workspace = SimulationWorkspace,
            ScenarioBatch = _scenarioBatch,
            MultiAxisCommissioning = _multiAxisCommissioning,
            ObserveProjectSave = _projectLifecycle.ObserveSaveAsync,
            ObserveCameraAcquisition = _camera.ObserveAsync,
            ObserveScenarioBatch = _scenarioBatch.ObserveAsync,
            ObserveCommissioningValidation = _multiAxisCommissioning.ObserveAsync,
            ObserveIntegration = Integration.ObserveAsync,
            ResolveUnsavedChanges = TryResolveUnsavedChangesAsync,
            SetCloseAdmission = SetSessionCloseAdmission,
            RecordShutdownDiagnostic = _runtimeObservabilityPresenter.RecordShutdownDiagnostic
        });
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
    internal Action<string> ProjectOpenFailurePresenter { get; set; }
    internal string? CurrentProjectPath => _projectLifecycle.CurrentPath;
    internal bool IsSessionCloseRequested => _sessionCloseRequested;

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

            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            OnPropertyChanged(nameof(IsRunMode));
            OnPropertyChanged(nameof(IsSceneEditable));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(ControlOwnerText));
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
        => _projectLifecycle.HasUnsavedChanges;

    public ShellNavigationViewModel Navigation => _shellNavigation;

    public bool IsCompactLayout
    {
        get => Navigation.IsCompactLayout;
        set => Navigation.IsCompactLayout = value;
    }

    public ProjectTreeViewModel ProjectTree { get; }
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
    public ManualEquipmentCommissioningViewModel ManualEquipment => _manualEquipment;
    public CameraCommissioningViewModel Camera => _camera;
    // Compatibility for existing callers; the Camera inspector binds directly to Camera.
    public CameraImageSourceEditorViewModel CameraImageSourceEditor => _camera.ImageSourceEditor;
    public MachineIntegrationViewModel Integration { get; }
    public SemiconductorRecipeGalleryViewModel SemiconductorRecipes { get; }
    public RecipeDryRunPlaybackViewModel DryRunPlayback => _recipeAuthoring.Playback;
    public SceneSnapshotStore SceneSnapshots { get; }
    public SceneSnapshotStore SceneSnapshotSource => DryRunPlayback.IsActive
        ? DryRunPlayback.PlaybackSnapshots
        : SceneSnapshots;
    private SimulationSnapshot PresentationSnapshot =>
        SceneSnapshotSource.Latest ?? SceneSnapshots.Latest ?? _simulationSession.Engine.CurrentSnapshot;
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
        && IsRunMode
        && !_isApplyingProject
        && !IsValidationBusy
        && !_runtimeDefinitionDirty
        && !RuntimeProjection.ConditionScenario.IsActive
        && !string.IsNullOrWhiteSpace(SimulationWorkspace.ScenarioTargetId)
        && SimulationWorkspace.IsScheduledFaultConfigurationValid
        && SimulationWorkspace.IsAssertionConfigurationValid;
    public bool CanStopTestScenario => !_disposed
        && IsRunMode
        && !IsValidationBusy
        && RuntimeProjection.ConditionScenario.IsActive;
    public bool CanReplayTestScenario => !_disposed
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
    public string CurrentCameraFrameHashText => _camera.CurrentCameraFrameHashText;
    public string CurrentCameraInspectionIdText => _camera.CurrentCameraInspectionIdText;
    public string CurrentCameraInspectionMessageText => _camera.CurrentCameraInspectionMessageText;
    public string CurrentCameraInspectionMetricsText => _camera.CurrentCameraInspectionMetricsText;
    public string CurrentVisionEvidenceHashText => _camera.CurrentVisionEvidenceHashText;
    public string VisionEvidenceStatusText => _camera.VisionEvidenceStatusText;
    public string VisionEvidenceComparisonText => _camera.VisionEvidenceComparisonText;
    public string CurrentCameraEvidenceDetailsText => string.Join(
        Environment.NewLine,
        $"{OpenVisionLanguageService.T("Camera.InspectionId")}: {CurrentCameraInspectionIdText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMessage")}: {CurrentCameraInspectionMessageText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMetrics")}: {CurrentCameraInspectionMetricsText}",
        $"{OpenVisionLanguageService.T("Camera.ExecutionEvidence")}: {CurrentVisionEvidenceHashText}",
        VisionEvidenceStatusText,
        VisionEvidenceComparisonText);
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
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.RunAsync();
        },
        _ => _simulationSession.RunControl.CanRun());

    public ICommand PauseCommand => _pauseCommand ??= CreateAsyncCommand(
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.PauseAsync();
        },
        _ => _simulationSession.RunControl.CanPause());

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
        _ => _simulationSession.RunControl.CanStep());

    public ICommand ResetCommand => _resetCommand ??= CreateAsyncCommand(
        async _ =>
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            await _simulationSession.RunControl.ResetAsync();
        },
        _ => _simulationSession.RunControl.CanReset());

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
        async _ => await _simulationSession.RunControl.CycleStartAsync(),
        _ => _simulationSession.RunControl.CanCycleStart());

    public ICommand StartManualEquipmentControlCommand =>
        _manualEquipment.StartManualEquipmentControlCommand;

    public ICommand StartManualCameraControlCommand => _camera.StartManualCameraControlCommand;

    public ICommand TriggerCameraCommand => _camera.TriggerCameraCommand;

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

    public ICommand ForceSensorOnCommand => _manualEquipment.ForceSensorOnCommand;

    public ICommand ForceSensorOffCommand => _manualEquipment.ForceSensorOffCommand;

    public ICommand ClearSensorForceCommand => _manualEquipment.ClearSensorForceCommand;

    public ICommand ExtendCylinderCommand => _manualEquipment.ExtendCylinderCommand;

    public ICommand RetractCylinderCommand => _manualEquipment.RetractCylinderCommand;

    public ICommand RunConveyorForwardCommand => _manualEquipment.RunConveyorForwardCommand;

    public ICommand RunConveyorReverseCommand => _manualEquipment.RunConveyorReverseCommand;

    public ICommand StopConveyorCommand => _manualEquipment.StopConveyorCommand;

    public ICommand AddLayoutComponentCommand => _layoutAuthoring.AddLayoutComponentCommand;

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

    public ICommand ExitCommand => _exitCommand ??= CreateRelayCommand(_ => _mainWpfInteractionHost.ShutdownApplication());

    #endregion

    #region Project File Operations

    internal Task<bool> OpenProjectAsync(string path) =>
        _projectLifecycle.OpenProjectAsync(path);

    internal Task<bool> OpenProjectReplacingCurrentAsync(string path) =>
        _projectLifecycle.OpenProjectReplacingCurrentAsync(path);

    internal Task<bool> CreateNewProjectAsync() =>
        _projectLifecycle.CreateNewProjectAsync();

    private void OnBlankLayoutStarted()
    {
        StatusMessage = OpenVisionLanguageService.T("Scene.BlankLayoutReadyStatus");
        InvalidateCommands();
    }

    private Task OpenBundledSampleAsync() =>
        _projectLifecycle.OpenBundledSampleAsync();

    internal Task SaveProjectAsync(string path) =>
        _projectLifecycle.SaveProjectAsync(path);

    private Task<bool> CreateSemiconductorRecipeCopyAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath) => CreateSemiconductorRecipeCopyCoreAsync(recipe, destinationPath);

    private Task<bool> TrySaveCurrentProjectAsync(bool saveAs = false) =>
        _projectLifecycle.TrySaveCurrentProjectAsync(saveAs);

    private async Task<bool> CreateSemiconductorRecipeCopyCoreAsync(
        SemiconductorRecipeGalleryItemViewModel recipe,
        string? destinationPath)
    {
        var copied = await _projectLifecycle.CreateSemiconductorRecipeCopyAsync(recipe, destinationPath);
        if (!copied)
        {
            return false;
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.CopySucceeded"),
            recipe.DisplayName);
        _runtimeObservabilityJournal.Log("Project", $"Created semiconductor recipe copy · {recipe.DisplayName}");
        return true;
    }

    #endregion

    #region Layout Authoring And Scene Interaction

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
        if (isApplying)
        {
            _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
            _camera.InvalidatePreparation();
        }

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
            _runtimeObservabilityJournal.Log("Project", $"Project rejected · {result.CompilationDetail}");
        }
        else if (result.CommandResult is { } commandResult)
        {
            _runtimeObservabilityJournal.Log("Project", $"Project rejected · {commandResult.ErrorCode}: {commandResult.Detail}");
        }
    }

    private void CompleteProjectRuntimeApplication(MachineProjectDocument project)
    {
        _camera.InvalidatePreparation();
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
        IsDesignMode = true;
        ApplyMonitorSnapshot(_simulationSession.Engine.CurrentSnapshot);
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
        _recipeAuthoring.ResetPresentation();
        _camera.LoadProject(project, CurrentProjectPath);
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
        OnPropertyChanged(nameof(HasEmbeddedSequence));
        OnPropertyChanged(nameof(HasAutomaticRun));
        OnPropertyChanged(nameof(HasAuthoredLayout));
        OnPropertyChanged(nameof(HasCycleStartInput));
        OnPropertyChanged(nameof(ControlOwnerHelpText));
        OnPropertyChanged(nameof(SceneTitleText));
        OnPropertyChanged(nameof(CurrentSequenceName));
        OnPropertyChanged(nameof(CurrentSequenceStepText));
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

    private void RefreshManualEquipmentProjection(
        SimulationSnapshot? snapshot = null,
        bool invalidateCommands = true) =>
        _manualEquipment.RefreshProjection(snapshot, invalidateCommands);

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
            RuntimeProjection.CurrentAxis?.State == AxisState.Error);

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
            _simulationSession.Engine.CurrentSnapshot.RunMode,
            IsRunMode,
            _isApplyingProject,
            IsValidationBusy,
            _runtimeDefinitionDirty,
            IsRunning,
            RuntimeProjection.ControlOwner,
            RuntimeProjection.AutomaticRun.IsActive,
            RuntimeProjection.CurrentSequence?.Status);
    }

    private void RefreshCameraCommissioningProjection() => _camera.RefreshProjection();

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
        && _simulationSession.Engine is FixedStepSimulationEngine traceEngine
        && traceEngine.CommandTrace.Length > 0;

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
        _simulationSession.Engine is FixedStepSimulationEngine traceEngine
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
            _camera.SelectedCameraRecipe);

    private DeterministicVisionExecutionEvidencePackage? GetCurrentUnifiedCommissioningVisionEvidence() =>
        _camera.GetCurrentEvidence();

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
        _runtimeObservabilityJournal.Log(
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

    private void InvalidateManualCameraPreparation()
        => _camera.InvalidatePreparation();

    private void CancelManualCameraPreparation()
        => _camera.CancelPreparation();

    private void MarkProjectChanged(bool requiresRuntimeRebuild = true)
    {
        _multiAxisCommissioningExecutionWorkflow.InvalidatePendingExecution();
        InvalidateManualCameraPreparation();
        _projectLifecycle.MarkChanged();
        if (requiresRuntimeRebuild)
        {
            _runtimeDefinitionDirty = true;
        }

        NotifyAxisCommissioningChanged(invalidateCommands: false);
        RefreshProjectDirtyState();
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
        switch (transition.Kind)
        {
            case ProjectLifecycleTransitionKind.ProjectOpened:
                RefreshProjectIdentity();
                _camera.LoadProject(transition.Project, transition.Path);
                _scenarioBatch!.Restore();
                _multiAxisCommissioning.Restore();
                _camera.RestoreEvidence();
                Navigation.HideStartupChoice();
                _runtimeObservabilityJournal.Log("Project", $"Opened {transition.Project.Name}");
                break;
            case ProjectLifecycleTransitionKind.NewProjectCreated:
                RefreshProjectIdentity();
                _camera.SetProjectPath(null, isSaved: true);
                _camera.ClearEvidence();
                Navigation.HideStartupChoice();
                Navigation.SelectedLeftToolTabIndex = 1;
                _runtimeObservabilityJournal.Log("Project", "Created new project");
                break;
            case ProjectLifecycleTransitionKind.BundledSampleOpened:
                RefreshProjectIdentity();
                _camera.SetProjectPath(null, isSaved: true);
                Navigation.HideStartupChoice();
                Navigation.SelectedLeftToolTabIndex = 0;
                StatusMessage = OpenVisionLanguageService.T("Scene.SampleOpenedStatus");
                _runtimeObservabilityJournal.Log("Project", $"Opened bundled sample · {transition.Project.Name}");
                InvalidateCommands();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition.Kind), transition.Kind, null);
        }
    }

    private void OnProjectSaveCompleted(ProjectSaveLifecycleResult result)
    {
        if (!result.Applied)
        {
            RefreshProjectDirtyState();
            RefreshProjectIdentity();
            _runtimeObservabilityJournal.Log("Project", $"Save completed for a stale document revision · {result.Receipt.Revision}");
            return;
        }

        RefreshProjectIdentity();
        _camera.SetProjectPath(CurrentProjectPath, isSaved: true);
        AcceptCurrentProjectAsSaved();
        _runtimeObservabilityJournal.Log("Project", $"Saved {CurrentProject.Name}");
    }

    private void HandleProjectSaveFailure(Exception exception)
    {
        StatusMessage = OpenVisionLanguageService.T(
            "Project.SaveFailedStatus",
            "프로젝트를 저장하지 못했습니다",
            "The project could not be saved");
        _runtimeObservabilityJournal.Log("Project", $"Save failed · {exception.Message}");
        _mainMessageDialogHost.ShowProjectSaveFailure(exception.Message);
    }

    internal Task<bool> TryResolveUnsavedChangesAsync() =>
        _projectLifecycle.TryResolveUnsavedChangesAsync();

    private void HandleProjectOpenFailure(Exception exception)
    {
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
            var result = await _simulationSession.Engine.EnqueueCommandAsync(command);
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

    private async Task PauseForDesignAsync()
    {
        try
        {
            var command = new PauseCommand();
            var result = await _simulationSession.Engine.EnqueueCommandAsync(command);
            if (result.IsAccepted)
            {
                IsRunning = false;
                _runtimeObservabilityJournal.Log("Simulation", "Paused before entering Design mode");
            }
        }
        catch (OperationCanceledException) when (_simulationSession.RuntimeLoop.CancellationToken.IsCancellationRequested)
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
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(HasSelectedEquipment));
        OnPropertyChanged(nameof(SelectedEquipmentStatus));
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
        _recipeAuthoring.ExitPlayback();
        RecipeConnections.Load(CurrentProject, Layout.SelectedItem?.Id);
        Properties.Show(Layout.SelectedItem?.Component);
        RefreshManualEquipmentProjection();
        RefreshCameraCommissioningProjection();
        StatusMessage = "Layout changed; Simulation ON will rebuild the runtime";
    }

    private void OnAxisDefinitionChanged()
    {
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
        MarkProjectChanged(requiresRuntimeRebuild: false);
        Properties.ShowNode(ProjectTree.SelectedNode);
        NotifyMultiAxisCommissioningRecipeChanged(recipeChanged: true);
        StatusMessage = "Multi-axis commissioning recipe changed; save the project to retain it";
    }

    private void OnSequenceDefinitionChanged(
        object? sender,
        SequenceEditorChangedEventArgs args)
    {
        _recipeAuthoring.ExitPlayback();
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

    private void RefreshIntegrationContext() => Integration.RefreshSourceContext();

    #endregion

    #region Integration And Camera

    private MachineIntegrationRequestContext CaptureIntegrationRequestContext() => new(
        BuildIdentity.IsExactCommit,
        CurrentProject.Id,
        CurrentProject.Schema,
        CurrentProject.Sequences,
        CurrentProjectPath,
        _camera.SelectedCameraId,
        _camera.SelectedCameraRecipe,
        RuntimeProjection.CurrentCamera,
        CurrentCameraDefinition?.Camera?.SingleImageSource);

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
            CurrentProject.Simulation.Seed);
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
        _runtimeObservabilityJournal.Log("Motion", $"Commissioning mismatch selected · {mismatch.TargetId} · Tick {mismatch.TickIndex}");
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
        => _runtimeObservabilityJournal.Append(time, category, message);

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
                break;
            case nameof(ShellNavigationViewModel.SelectedLanguageOption):
                OnPropertyChanged(nameof(SelectedLanguageOption));
                break;
        }
    }

    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName))
        {
            return;
        }

        OnPropertyChanged(args.PropertyName);
        OnPropertyChanged(nameof(UnifiedCommissioningEvidenceStatusText));
        OnPropertyChanged(nameof(CanImportUnifiedCommissioningEvidence));
    }

    private void OnManualEquipmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName))
        {
            return;
        }

        OnPropertyChanged(args.PropertyName);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
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
        _manualEquipment.InvalidateCommands();
        _camera.InvalidateCommands();
        AxisCommissioning.InvalidateCommands();
        RaiseCanExecuteChanged(_runMultiAxisCommissioningRecipeCommand);
        RaiseCanExecuteChanged(_stopMultiAxisCommissioningRecipeCommand);
        _layoutAuthoring.InvalidateCommands();
        _recipeAuthoring.InvalidateCommands();
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
        _manualEquipment.InvalidateCommands();
        AxisCommissioning.InvalidateCommands();
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
        _runtimeObservabilityJournal.Log("Error", exception.Message);
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

    internal Task<SimulationSessionCloseResult> RequestCloseAsync(TimeSpan? timeout = null) =>
        _simulationSession.RequestCloseAsync(timeout ?? RuntimeShutdownTimeout);

    internal void PresentCloseResult(SimulationSessionCloseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsApproved || result.Outcome == SimulationSessionCloseOutcome.UnsavedChangesRejected)
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
        StatusMessage = OpenVisionLanguageService.T(
            "Shell.CloseFailed",
            "종료를 완료하지 못했습니다.",
            "The session could not be closed.");
        _runtimeObservabilityJournal.Log("Runtime", $"Close failed · {exception.Message}");
    }

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
        InvalidateCommands();
    }

    private void DisposeShellResources()
    {
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
    }

    private void DetachShellEventHandlers()
    {
        _shellNavigation.PropertyChanged -= OnShellNavigationPropertyChanged;
        _shellNavigation.LanguageChanged -= OnLanguageChanged;
        _shellNavigation.Dispose();
        _manualEquipment.PropertyChanged -= OnManualEquipmentPropertyChanged;
        _camera.PropertyChanged -= OnCameraPropertyChanged;
        _selectionSynchronization.PropertyChanged -= OnSelectionSynchronizationPropertyChanged;
        SimulationWorkspace.PropertyChanged -= OnSimulationWorkspacePropertyChanged;
        _recipeAuthoring.DetachEventHandlers();
        _recipeAuthoring.ProcessPlanReview.PropertyChanged -= OnProcessPlanReviewPropertyChanged;
        _simulationCommandTrace.PropertyChanged -= OnSimulationCommandTracePropertyChanged;
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
        _multiAxisCommissioningExecutionWorkflow.Dispose();
        DetachShellEventHandlers();
        _simulationSession.BeginDispose(RuntimeShutdownTimeout, DisposeShellResources);
    }

    #endregion
}
