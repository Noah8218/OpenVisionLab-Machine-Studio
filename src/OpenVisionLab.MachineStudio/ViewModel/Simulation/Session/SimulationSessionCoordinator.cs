using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Runtime composition contract for one simulation session.
/// </summary>
internal sealed record SimulationSessionStartup
{
    internal required string ProjectId { get; init; }
    internal required SimulationRuntimeConfiguration InitialRuntime { get; init; }
    internal required Func<SimulationRunControlState> GetRunControlState { get; init; }
    internal required Func<Task<bool>> EnsureRuntimeDefinitionApplied { get; init; }
    internal required Action<bool> SetDesignMode { get; init; }
    internal required Action<bool> SetRunning { get; init; }
    internal required Action<SimulationSnapshot> ApplySnapshot { get; init; }
    internal Func<SimulationSnapshot, bool>? CanApplySnapshot { get; init; }
    internal required Action CancelVisionCapture { get; init; }
    internal required Action<string> SetStatus { get; init; }
    internal required Action<string, string> Log { get; init; }
    internal required Action NotifyCommandsChanged { get; init; }
    internal required Func<Action, Task> Dispatch { get; init; }
    internal Func<Action, Task>? DispatchAfterDispose { get; init; }
    internal required Action<SimulationSnapshot> PublishSnapshot { get; init; }
    internal required Action OnInitialRuntimeApplied { get; init; }
    internal required Action<string> OnInitialConfigurationRejected { get; init; }
    internal required Action<SimulationEvent> OnRuntimeEvent { get; init; }
    internal required Action<SimulationEngineTerminationResult> OnTerminated { get; init; }
    internal required Action<Exception> OnUnhandledException { get; init; }
    internal Action<SimulationEvent>? OnCanonicalEvent { get; init; }
    internal Action<SimulationEventJournalSnapshot>? OnCanonicalJournalCompleted { get; init; }
    internal Func<CancellationToken, Task<bool>>? PrepareAutomaticExternalInspection { get; init; }
    internal required SimulationWorkspaceViewModel Workspace { get; init; }
    internal SimulationScenarioBatchViewModel? ScenarioBatch { get; init; }
    internal MultiAxisCommissioningViewModel? MultiAxisCommissioning { get; init; }
    internal required Func<TimeSpan, Task<ProjectSaveParticipantResult>> ObserveProjectSave { get; init; }
    internal required Func<TimeSpan, Task<CameraAcquisitionParticipantResult>> ObserveCameraAcquisition { get; init; }
    internal required Func<TimeSpan, Task<SimulationScenarioBatchParticipantResult>> ObserveScenarioBatch { get; init; }
    internal required Func<TimeSpan, Task<MultiAxisCommissioningParticipantResult>> ObserveCommissioningValidation { get; init; }
    internal required Func<TimeSpan, Task<MachineIntegrationParticipantResult>> ObserveIntegration { get; init; }
    internal required Func<Task<bool>> ResolveUnsavedChanges { get; init; }
    internal required Action<bool> SetCloseAdmission { get; init; }
    internal required Action<SimulationRuntimeShutdownDiagnostic> RecordShutdownDiagnostic { get; init; }
}

/// <summary>
/// Owns the Engine-backed task, cancellation, close, and disposal lifetime of
/// one Simulation Session. Project and shell presentation remain callbacks.
/// </summary>
internal sealed class SimulationSessionCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _fixedStep;
    private readonly ISimulationEngine _engine;
    private SimulationRunControlWorkflow? _runControl;
    private SimulationRuntimeLoop? _runtimeLoop;
    private SimulationRuntimeResourceOwner? _runtimeResources;
    private SimulationRuntimeShutdownWorkflow? _runtimeShutdown;
    private SimulationSessionCloseWorkflow? _closeWorkflow;
    private Action<bool>? _setCloseAdmission;
    private bool _started;
    private bool _disposeRequested;

    internal SimulationSessionCoordinator(
        TimeSpan fixedStep,
        TimeSpan? automaticExternalInspectionWallTimeout = null)
    {
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStep));
        }

        _fixedStep = fixedStep;
        _engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = fixedStep,
            AutomaticExternalInspectionWallTimeout = automaticExternalInspectionWallTimeout
                ?? SimulationSettings.DefaultAutomaticExternalInspectionWallTimeout
        });
    }

    internal ISimulationEngine Engine => _engine;

    internal SimulationRuntimeLoop RuntimeLoop => _runtimeLoop
        ?? throw new InvalidOperationException("The simulation session has not started.");

    internal SimulationRunControlWorkflow RunControl => _runControl
        ?? throw new InvalidOperationException("The simulation session has not started.");

    internal bool IsShutdownRequested => _runtimeShutdown?.IsShutdownRequested == true;

    internal void Start(SimulationSessionStartup startup)
    {
        ArgumentNullException.ThrowIfNull(startup);

        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("The simulation session can start only once.");
            }

            if (_disposeRequested)
            {
                throw new ObjectDisposedException(nameof(SimulationSessionCoordinator));
            }

            _runControl = new(
                _engine,
                _fixedStep,
                startup.GetRunControlState,
                startup.EnsureRuntimeDefinitionApplied,
                startup.SetDesignMode,
                startup.SetRunning,
                startup.ApplySnapshot,
                startup.CancelVisionCapture,
                startup.SetStatus,
                startup.Log,
                startup.NotifyCommandsChanged,
                startup.PrepareAutomaticExternalInspection);
            _runtimeLoop = new(
                _engine,
                startup.Dispatch,
                startup.PublishSnapshot,
                startup.ApplySnapshot,
                startup.OnInitialRuntimeApplied,
                startup.OnInitialConfigurationRejected,
                startup.OnRuntimeEvent,
                startup.OnTerminated,
                startup.OnUnhandledException,
                startup.OnCanonicalEvent,
                startup.OnCanonicalJournalCompleted,
                startup.CanApplySnapshot);
            _runtimeResources = new(
                _engine,
                _runtimeLoop,
                startup.Workspace,
                startup.ScenarioBatch,
                startup.MultiAxisCommissioning);
            _runtimeShutdown = new(
                _engine,
                _runtimeLoop,
                _runtimeResources,
                _runControl,
                startup.RecordShutdownDiagnostic,
                startup.Dispatch,
                startup.DispatchAfterDispose);
            _closeWorkflow = new(
                startup.ResolveUnsavedChanges,
                startup.ObserveProjectSave,
                startup.ObserveCameraAcquisition,
                startup.ObserveScenarioBatch,
                startup.ObserveCommissioningValidation,
                (timeout, cancellationToken) => ShutdownAsync(timeout, cancellationToken),
                startup.SetCloseAdmission,
                startup.ObserveIntegration);
            _setCloseAdmission = startup.SetCloseAdmission;
            _started = true;
        }

        RuntimeLoop.Start(startup.InitialRuntime, startup.ProjectId);
    }

    internal Task<RuntimeShutdownResult> ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        RequireShutdownWorkflow().ShutdownAsync(timeout, cancellationToken);

    internal Task<SimulationSessionCloseResult> RequestCloseAsync(TimeSpan timeout) =>
        RequireCloseWorkflow().RequestCloseAsync(timeout);

    internal void BeginDispose(TimeSpan timeout, Action completeShellDispose)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ArgumentNullException.ThrowIfNull(completeShellDispose);

        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
        }

        (_setCloseAdmission ?? throw new InvalidOperationException(
            "The simulation session has not started."))(true);
        var shutdownTask = ShutdownAsync(timeout);
        RequireShutdownWorkflow().CompleteDisposeAfterShutdown(shutdownTask, completeShellDispose);
    }

    public void Dispose() => BeginDispose(TimeSpan.FromSeconds(5), static () => { });

    private SimulationRuntimeShutdownWorkflow RequireShutdownWorkflow() => _runtimeShutdown
        ?? throw new InvalidOperationException("The simulation session has not started.");

    private SimulationSessionCloseWorkflow RequireCloseWorkflow() => _closeWorkflow
        ?? throw new InvalidOperationException("The simulation session has not started.");

}
