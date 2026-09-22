using OpenVisionLab;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the Simulation run-control transaction. The shell supplies a current
/// state snapshot and presentation callbacks; this type owns engine command
/// ordering and cross-command serialization without WPF coupling.
/// </summary>
internal sealed class SimulationRunControlWorkflow : IDisposable
{
    private readonly ISimulationEngine _engine;
    private readonly TimeSpan _simulationFixedStep;
    private readonly Func<SimulationRunControlState> _getState;
    private readonly Func<Task<bool>> _ensureRuntimeDefinitionApplied;
    private readonly Func<CancellationToken, Task<bool>>? _prepareAutomaticExternalInspection;
    private readonly Action<bool> _setDesignMode;
    private readonly Action<bool> _setRunning;
    private readonly Action<SimulationSnapshot> _applySnapshot;
    private readonly Action _cancelVisionCapture;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;
    private readonly Action _notifyCommandsChanged;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _operationsIdle = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycleGate = new();
    private int _isBusy;
    private int _activeOperations;
    private int _operationGeneration;
    private bool _disposeRequested;
    private bool _executionGateDisposed;

    internal SimulationRunControlWorkflow(
        ISimulationEngine engine,
        TimeSpan simulationFixedStep,
        Func<SimulationRunControlState> getState,
        Func<Task<bool>> ensureRuntimeDefinitionApplied,
        Action<bool> setDesignMode,
        Action<bool> setRunning,
        Action<SimulationSnapshot> applySnapshot,
        Action cancelVisionCapture,
        Action<string> setStatus,
        Action<string, string> log,
        Action notifyCommandsChanged,
        Func<CancellationToken, Task<bool>>? prepareAutomaticExternalInspection = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _simulationFixedStep = simulationFixedStep;
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _ensureRuntimeDefinitionApplied = ensureRuntimeDefinitionApplied
            ?? throw new ArgumentNullException(nameof(ensureRuntimeDefinitionApplied));
        _prepareAutomaticExternalInspection = prepareAutomaticExternalInspection;
        _setDesignMode = setDesignMode ?? throw new ArgumentNullException(nameof(setDesignMode));
        _setRunning = setRunning ?? throw new ArgumentNullException(nameof(setRunning));
        _applySnapshot = applySnapshot ?? throw new ArgumentNullException(nameof(applySnapshot));
        _cancelVisionCapture = cancelVisionCapture
            ?? throw new ArgumentNullException(nameof(cancelVisionCapture));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _notifyCommandsChanged = notifyCommandsChanged
            ?? throw new ArgumentNullException(nameof(notifyCommandsChanged));
    }

    internal bool IsBusy => Volatile.Read(ref _isBusy) != 0;

    internal bool AreOperationsIdle
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _activeOperations == 0;
            }
        }
    }

    internal Task WaitForOperationsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_activeOperations == 0)
            {
                return Task.CompletedTask;
            }
        }

        return cancellationToken.CanBeCanceled
            ? _operationsIdle.Task.WaitAsync(cancellationToken)
            : _operationsIdle.Task;
    }

    internal bool CanRun()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanRun(_getState());
    }

    internal bool CanPause()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanPause(_getState());
    }

    internal bool CanAbortSequence()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanAbortSequence(_getState());
    }

    internal bool CanRetrySequence()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanRetrySequence(_getState());
    }

    internal bool CanStep()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanStep(_getState());
    }

    internal bool CanCycleStart()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanCycleStart(_getState());
    }

    internal bool CanReset()
    {
        return !IsBusy && SimulationRunControlAdmissionPolicy.CanReset(_getState());
    }

    internal Task RunAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(RunCoreAsync, cancellationToken);

    internal Task PauseAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(PauseCoreAsync, cancellationToken);

    internal Task<bool> PauseForDesignModeAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedResultAsync(PauseForDesignModeCoreAsync, cancellationToken);

    internal Task AbortSequenceAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(AbortSequenceCoreAsync, cancellationToken);

    internal Task RetrySequenceAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(RetrySequenceCoreAsync, cancellationToken);

    internal Task StepAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(StepCoreAsync, cancellationToken);

    internal Task ResetAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(ResetCoreAsync, cancellationToken);

    internal Task CycleStartAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(CycleStartCoreAsync, cancellationToken);

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        if (!await _ensureRuntimeDefinitionApplied())
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        if (!IsOperationCurrent(operationGeneration)
            || !SimulationRunControlAdmissionPolicy.CanRun(_getState()))
        {
            return;
        }

        _setDesignMode(false);
        var state = _getState();
        if (state.HasAutomaticRun)
        {
            if (state.AutomaticExternalInspectionEnabled)
            {
                if (_prepareAutomaticExternalInspection is null
                    || !await _prepareAutomaticExternalInspection(cancellationToken))
                {
                    _setStatus("Automatic external inspection could not be prepared.");
                    return;
                }

                state = _getState();
                if (!IsOperationCurrent(operationGeneration)
                    || !SimulationRunControlAdmissionPolicy.CanRun(state))
                {
                    return;
                }
            }

            if (state.AutomaticRunActive)
            {
                await DispatchPlayAsync(
                    "Automatic simulation running",
                    "Simulation resumed",
                    operationGeneration,
                    cancellationToken);
                return;
            }

            var automaticCommand = new StartAutomaticRunCommand(
                waitForExternalResult: state.AutomaticExternalInspectionEnabled);
            var automaticResult = await _engine.EnqueueCommandAsync(
                automaticCommand,
                cancellationToken);
            if (!IsOperationCurrent(operationGeneration))
            {
                return;
            }

            if (!automaticResult.IsAccepted)
            {
                _log(
                    "Simulation",
                    $"Automatic run rejected · {automaticResult.ErrorCode}: {automaticResult.Detail}");
                return;
            }

            _setRunning(true);
            _setStatus("Automatic simulation running");
            _log("Simulation", $"Simulation ON requested · {ShortCommandId(automaticCommand)}");
            return;
        }

        if (!await EnsureActiveSequenceStartedAsync(
                operationGeneration,
                cancellationToken))
        {
            return;
        }

        await DispatchPlayAsync(
            "Simulation running",
            "Run requested",
            operationGeneration,
            cancellationToken);
    }

    private async Task DispatchPlayAsync(
        string acceptedStatus,
        string acceptedLogPrefix,
        int operationGeneration,
        CancellationToken cancellationToken)
    {
        var command = new PlayCommand();
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _log("Simulation", $"Run rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _setRunning(true);
        _setStatus(acceptedStatus);
        _log("Simulation", $"{acceptedLogPrefix} · {ShortCommandId(command)}");
    }

    private async Task PauseCoreAsync(CancellationToken cancellationToken)
    {
        if (!SimulationRunControlAdmissionPolicy.CanPause(_getState()))
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var command = new PauseCommand();
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _log("Simulation", $"Pause rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _setRunning(false);
        _setStatus("Simulation paused");
        _log("Simulation", $"Pause requested · {ShortCommandId(command)}");
    }

    private async Task<bool> PauseForDesignModeCoreAsync(CancellationToken cancellationToken)
    {
        var state = _getState();
        if (!state.IsRunning)
        {
            return true;
        }

        if (!SimulationRunControlAdmissionPolicy.CanPause(state))
        {
            _log("Simulation", "Design mode pause rejected by the current run-control state.");
            return false;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var command = new PauseCommand();
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return false;
        }

        if (!result.IsAccepted)
        {
            _log("Simulation", $"Design mode pause rejected · {result.ErrorCode}: {result.Detail}");
            return false;
        }

        _setRunning(false);
        _setStatus("Simulation paused before entering Design mode");
        _log("Simulation", $"Design mode pause requested · {ShortCommandId(command)}");
        return true;
    }

    private async Task AbortSequenceCoreAsync(CancellationToken cancellationToken)
    {
        if (!SimulationRunControlAdmissionPolicy.CanAbortSequence(_getState()))
        {
            return;
        }

        var sequenceId = _getState().ActiveSequenceId;
        if (sequenceId is null)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var command = new AbortSequenceCommand(sequenceId);
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _setStatus(OpenVisionLanguageService.T(
                "Shell.SequenceAbortRejectedStatus",
                "시퀀스 중단이 거부되었습니다.",
                "Sequence abort was rejected."));
            _log("Sequence", $"Abort rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _applySnapshot(_engine.CurrentSnapshot);
        _setStatus(OpenVisionLanguageService.T(
            "Shell.SequenceAbortedStatus",
            "시퀀스가 중단되었습니다. Reset 후 다시 시작할 수 있습니다.",
            "The sequence was aborted. Reset is required before starting again."));
        _log("Sequence", $"Abort applied · {ShortCommandId(command)}");
    }

    private async Task RetrySequenceCoreAsync(CancellationToken cancellationToken)
    {
        if (!SimulationRunControlAdmissionPolicy.CanRetrySequence(_getState()))
        {
            return;
        }

        var state = _getState();
        var sequenceId = state.ResetRetrySequenceId ?? state.ActiveSequenceId;
        if (sequenceId is null)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var command = new RetrySequenceCommand(sequenceId);
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _setStatus(OpenVisionLanguageService.T(
                "Shell.SequenceRetryRejectedStatus",
                "시퀀스 재시도가 거부되었습니다.",
                "Sequence retry was rejected."));
            _log("Sequence", $"Retry rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _applySnapshot(_engine.CurrentSnapshot);
        _setStatus(OpenVisionLanguageService.T(
            "Shell.SequenceRetriedStatus",
            "시퀀스를 재시도했습니다. 자동 반복은 중지된 상태입니다.",
            "The sequence was retried from its entry step. Automatic continuation remains stopped."));
        _log("Sequence", $"Retry applied · {ShortCommandId(command)}");
    }

    private async Task StepCoreAsync(CancellationToken cancellationToken)
    {
        var state = _getState();
        if (!SimulationRunControlAdmissionPolicy.CanStep(state))
        {
            return;
        }

        if (state.ControlOwner != SimulationControlOwner.Manual
            && state.HasAutomaticRun
            && !state.AutomaticRunActive)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        if (state.ControlOwner != SimulationControlOwner.Manual
            && !state.HasAutomaticRun
            && !await EnsureActiveSequenceStartedAsync(
                operationGeneration,
                cancellationToken))
        {
            return;
        }

        var command = new StepCommand();
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _log("Simulation", $"Step rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _log(
            "Simulation",
            $"Single {_simulationFixedStep.TotalMilliseconds:G0} ms tick applied · {ShortCommandId(command)}");
    }

    private async Task ResetCoreAsync(CancellationToken cancellationToken)
    {
        var state = _getState();
        if (!SimulationRunControlAdmissionPolicy.CanReset(state))
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var command = new ResetCommand();
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _log("Simulation", $"Reset rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _setRunning(false);
        _cancelVisionCapture();
        _setStatus("Simulation reset");
        _log("Simulation", $"Reset applied · {ShortCommandId(command)}");
    }

    private async Task CycleStartCoreAsync(CancellationToken cancellationToken)
    {
        if (!SimulationRunControlAdmissionPolicy.CanCycleStart(_getState()))
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var command = new SetVirtualInputCommand("di.cycle-start", true);
        var result = await _engine.EnqueueCommandAsync(command, cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            _log("I/O", $"Cycle Start rejected · {result.ErrorCode}: {result.Detail}");
            return;
        }

        _setStatus("Cycle Start input applied");
        _log("I/O", $"Cycle Start input requested · {ShortCommandId(command)}");
    }

    private async Task<bool> EnsureActiveSequenceStartedAsync(
        int operationGeneration,
        CancellationToken cancellationToken)
    {
        var state = _getState();
        if (!state.HasEmbeddedSequence)
        {
            return true;
        }

        var sequence = state.ActiveSequenceStatus;
        if (state.ActiveSequenceId is null || sequence is null)
        {
            _log("Sequence", "Active sequence is not available in the runtime snapshot.");
            return false;
        }

        if (sequence == SequenceExecutionStatus.Running)
        {
            return true;
        }

        if (sequence != SequenceExecutionStatus.Ready)
        {
            _log("Sequence", $"Sequence cannot start from {sequence}.");
            return false;
        }

        var result = await _engine.EnqueueCommandAsync(
            new StartSequenceCommand(state.ActiveSequenceId),
            cancellationToken);
        if (!IsOperationCurrent(operationGeneration))
        {
            return false;
        }

        if (result.IsAccepted)
        {
            return true;
        }

        _log("Sequence", $"Start rejected · {result.ErrorCode}: {result.Detail}");
        return false;
    }

    private async Task ExecuteSerializedAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            await _executionGate.WaitAsync(cancellationToken);
            try
            {
                if (!CanEnterExecution())
                {
                    return;
                }

                SetBusy(true);
                await operation(cancellationToken);
            }
            finally
            {
                try
                {
                    SetBusy(false);
                }
                finally
                {
                    _executionGate.Release();
                }
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private bool CanEnterExecution()
    {
        lock (_lifecycleGate)
        {
            return !_disposeRequested;
        }
    }

    private bool IsOperationCurrent(int operationGeneration)
    {
        lock (_lifecycleGate)
        {
            return !_disposeRequested
                && operationGeneration == _operationGeneration;
        }
    }

    private async Task<bool> ExecuteSerializedResultAsync(
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            await _executionGate.WaitAsync(cancellationToken);
            try
            {
                if (!CanEnterExecution())
                {
                    return false;
                }

                SetBusy(true);
                return await operation(cancellationToken);
            }
            finally
            {
                try
                {
                    SetBusy(false);
                }
                finally
                {
                    _executionGate.Release();
                }
            }
        }
        finally
        {
            EndOperation();
        }
    }

    internal void InvalidatePendingExecution()
    {
        lock (_lifecycleGate)
        {
            if (!_disposeRequested)
            {
                Interlocked.Increment(ref _operationGeneration);
            }
        }
    }

    private void BeginOperation()
    {
        lock (_lifecycleGate)
        {
            if (_disposeRequested)
            {
                throw new ObjectDisposedException(nameof(SimulationRunControlWorkflow));
            }

            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        var disposeGate = false;
        var operationsIdle = false;
        lock (_lifecycleGate)
        {
            _activeOperations--;
            if (_disposeRequested && _activeOperations == 0 && !_executionGateDisposed)
            {
                _executionGateDisposed = true;
                disposeGate = true;
            }

            operationsIdle = _disposeRequested && _activeOperations == 0;
        }

        if (operationsIdle)
        {
            _operationsIdle.TrySetResult(true);
        }

        if (disposeGate)
        {
            _executionGate.Dispose();
        }
    }

    private void SetBusy(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _isBusy, next) != next)
        {
            _notifyCommandsChanged();
        }
    }

    private static string ShortCommandId(SimulationCommand command) =>
        $"CMD-{command.CommandId[..8].ToUpperInvariant()}";

    public void Dispose()
    {
        var disposeGate = false;
        lock (_lifecycleGate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            Interlocked.Increment(ref _operationGeneration);
            if (_activeOperations == 0)
            {
                _executionGateDisposed = true;
                disposeGate = true;
                _operationsIdle.TrySetResult(true);
            }
        }

        if (disposeGate)
        {
            _executionGate.Dispose();
        }
    }
}
