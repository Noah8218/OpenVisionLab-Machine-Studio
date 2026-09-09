namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum SimulationSessionCloseOutcome
{
    Approved,
    UnsavedChangesRejected,
    ShutdownIncomplete,
    ShutdownFaulted,
    ShutdownTimedOut,
    Failed
}

internal sealed record SimulationSessionCloseResult(
    SimulationSessionCloseOutcome Outcome,
    RuntimeShutdownResult? Shutdown = null,
    Exception? Exception = null,
    ProjectSaveParticipantResult? Save = null,
    CameraAcquisitionParticipantResult? Camera = null,
    SimulationScenarioBatchParticipantResult? ScenarioBatch = null,
    MultiAxisCommissioningParticipantResult? CommissioningValidation = null,
    MachineIntegrationParticipantResult? Integration = null)
{
    public bool IsApproved => Outcome == SimulationSessionCloseOutcome.Approved;
    public bool IsRetryable => Outcome == SimulationSessionCloseOutcome.UnsavedChangesRejected
        || (Outcome == SimulationSessionCloseOutcome.Failed && Shutdown is null);
}

/// <summary>
/// Owns one shell-close admission transaction. Project decision policy and
/// runtime shutdown policy remain with their existing workflows; this type
/// only joins them and exposes a typed close result to the shell.
/// </summary>
internal sealed class SimulationSessionCloseWorkflow
{
    private readonly Func<Task<bool>> _resolveUnsavedChanges;
    private readonly Func<TimeSpan, Task<ProjectSaveParticipantResult>> _observeProjectSave;
    private readonly Func<TimeSpan, Task<CameraAcquisitionParticipantResult>> _observeCameraAcquisition;
    private readonly Func<TimeSpan, Task<SimulationScenarioBatchParticipantResult>> _observeScenarioBatch;
    private readonly Func<TimeSpan, Task<MultiAxisCommissioningParticipantResult>> _observeCommissioningValidation;
    private readonly Func<TimeSpan, Task<MachineIntegrationParticipantResult>> _observeIntegration;
    private readonly Func<TimeSpan, CancellationToken, Task<RuntimeShutdownResult>> _shutdown;
    private readonly Action<bool> _setCloseAdmission;
    private readonly object _gate = new();
    private Task<SimulationSessionCloseResult>? _closeTask;
    private bool _closeRequested;

    internal SimulationSessionCloseWorkflow(
        Func<Task<bool>> resolveUnsavedChanges,
        Func<TimeSpan, Task<ProjectSaveParticipantResult>> observeProjectSave,
        Func<TimeSpan, Task<CameraAcquisitionParticipantResult>> observeCameraAcquisition,
        Func<TimeSpan, Task<SimulationScenarioBatchParticipantResult>> observeScenarioBatch,
        Func<TimeSpan, Task<MultiAxisCommissioningParticipantResult>> observeCommissioningValidation,
        Func<TimeSpan, CancellationToken, Task<RuntimeShutdownResult>> shutdown,
        Action<bool> setCloseAdmission,
        Func<TimeSpan, Task<MachineIntegrationParticipantResult>>? observeIntegration = null)
    {
        _resolveUnsavedChanges = resolveUnsavedChanges
            ?? throw new ArgumentNullException(nameof(resolveUnsavedChanges));
        _observeProjectSave = observeProjectSave
            ?? throw new ArgumentNullException(nameof(observeProjectSave));
        _observeCameraAcquisition = observeCameraAcquisition
            ?? throw new ArgumentNullException(nameof(observeCameraAcquisition));
        _observeScenarioBatch = observeScenarioBatch
            ?? throw new ArgumentNullException(nameof(observeScenarioBatch));
        _observeCommissioningValidation = observeCommissioningValidation
            ?? throw new ArgumentNullException(nameof(observeCommissioningValidation));
        _observeIntegration = observeIntegration
            ?? (_ => Task.FromResult(new MachineIntegrationParticipantResult(
                MachineIntegrationParticipantOutcome.Idle)));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _setCloseAdmission = setCloseAdmission
            ?? throw new ArgumentNullException(nameof(setCloseAdmission));
    }

    internal bool IsCloseRequested
    {
        get
        {
            lock (_gate)
            {
                return _closeRequested;
            }
        }
    }

    internal async Task<SimulationSessionCloseResult> RequestCloseAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Close timeout must be positive.");
        }

        Task<SimulationSessionCloseResult> closeTask;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _closeRequested = true;
                _setCloseAdmission(true);
                _closeTask = ResolveCloseAsync(timeout);
            }

            closeTask = _closeTask!;
        }

        var result = await closeTask;
        if (result.IsRetryable)
        {
            var releaseAdmission = false;
            lock (_gate)
            {
                if (ReferenceEquals(_closeTask, closeTask))
                {
                    _closeTask = null;
                    _closeRequested = false;
                    releaseAdmission = true;
                }
            }

            if (releaseAdmission)
            {
                _setCloseAdmission(false);
            }
        }

        return result;
    }

    private async Task<SimulationSessionCloseResult> ResolveCloseAsync(TimeSpan timeout)
    {
        try
        {
            var saveTask = _observeProjectSave(timeout);
            var cameraTask = _observeCameraAcquisition(timeout);
            var scenarioBatchTask = _observeScenarioBatch(timeout);
            var commissioningValidationTask = _observeCommissioningValidation(timeout);
            var integrationTask = _observeIntegration(timeout);
            await Task.WhenAll(
                saveTask,
                cameraTask,
                scenarioBatchTask,
                commissioningValidationTask,
                integrationTask);
            var save = await saveTask;
            var camera = await cameraTask;
            var scenarioBatch = await scenarioBatchTask;
            var commissioningValidation = await commissioningValidationTask;
            var integration = await integrationTask;
            if (commissioningValidation.IsTimedOut)
            {
                return new(
                    SimulationSessionCloseOutcome.Failed,
                    Exception: commissioningValidation.Exception,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration);
            }

            if (scenarioBatch.IsTimedOut)
            {
                return new(
                    SimulationSessionCloseOutcome.Failed,
                    Exception: scenarioBatch.Exception,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration);
            }

            if (camera.IsTimedOut)
            {
                return new(
                    SimulationSessionCloseOutcome.Failed,
                    Exception: camera.Exception,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration);
            }

            if (save.IsTimedOut)
            {
                return new(
                    SimulationSessionCloseOutcome.Failed,
                    Exception: save.Exception,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration);
            }

            if (integration.IsTimedOut)
            {
                return new(
                    SimulationSessionCloseOutcome.Failed,
                    Exception: integration.Exception,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration);
            }

            if (!await _resolveUnsavedChanges())
            {
                return new(
                    SimulationSessionCloseOutcome.UnsavedChangesRejected,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration);
            }

            var shutdown = await _shutdown(timeout, CancellationToken.None);
            return shutdown.Outcome switch
            {
                RuntimeShutdownOutcome.Completed => new(
                    SimulationSessionCloseOutcome.Approved,
                    shutdown,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration),
                RuntimeShutdownOutcome.Incomplete => new(
                    SimulationSessionCloseOutcome.ShutdownIncomplete,
                    shutdown,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration),
                RuntimeShutdownOutcome.Faulted => new(
                    SimulationSessionCloseOutcome.ShutdownFaulted,
                    shutdown,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration),
                RuntimeShutdownOutcome.TimedOut => new(
                    SimulationSessionCloseOutcome.ShutdownTimedOut,
                    shutdown,
                    Save: save,
                    Camera: camera,
                    ScenarioBatch: scenarioBatch,
                    CommissioningValidation: commissioningValidation,
                    Integration: integration),
                _ => throw new ArgumentOutOfRangeException(nameof(shutdown.Outcome))
            };
        }
        catch (Exception exception)
        {
            return new(SimulationSessionCloseOutcome.Failed, Exception: exception);
        }
    }
}
