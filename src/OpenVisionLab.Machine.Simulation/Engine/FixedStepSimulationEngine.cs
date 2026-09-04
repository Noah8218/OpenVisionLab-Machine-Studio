using System.Globalization;
using System.Collections.Immutable;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Engine;

public sealed class FixedStepSimulationEngine : ISimulationEngine
{
    private readonly SimulationSettings _settings;
    private readonly SimulationClock _clock;
    private readonly Channel<SimulationCommand> _commandChannel;
    private readonly Channel<SimulationEvent> _eventChannel;
    private readonly LatestSnapshotStore _snapshotStore;
    private readonly List<ServoAxisComponent> _axes = new();
    private readonly DeterministicSimulationCommandTraceStore _commandTraceStore = new();
    private readonly List<DeterministicVirtualCamera> _cameras = new();
    private readonly Dictionary<string, DeterministicSequenceExecutor> _sequenceExecutors =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompiledSequence> _compiledSequences =
        new(StringComparer.Ordinal);
    private readonly DeterministicSequenceDebugState _sequenceDebugState = new();
    private readonly SimulationRuntimeConfigurationBuilder _runtimeConfigurationBuilder;
    private readonly SimulationManualControlCommandHandler _manualControlCommandHandler = new();
    private readonly SimulationFaultCommandHandler _faultCommandHandler = new();
    private readonly SimulationConditionScheduledFaultRecoveryHandler _conditionScheduledFaultRecoveryHandler = new();
    private readonly SimulationConditionScheduledFaultInjectionHandler _conditionScheduledFaultInjectionHandler = new();
    private readonly SimulationConditionScenarioProgressHandler _conditionScenarioProgressHandler = new();
    private readonly SimulationConditionScenarioStopHandler _conditionScenarioStopHandler = new();
    private readonly SimulationConditionScenarioCommandHandler _conditionScenarioCommandHandler = new();
    private readonly SimulationAutomaticRunCommandHandler _automaticRunCommandHandler = new();
    private readonly SimulationAutomaticRunCycleHandler _automaticRunCycleHandler = new();
    private readonly SimulationSequenceCommandHandler _sequenceCommandHandler = new();
    private readonly SimulationRunControlCommandHandler _runControlCommandHandler = new();
    private readonly Dictionary<SimulationFaultKey, SimulationFaultSnapshot> _activeFaults = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource<SimulationEngineTerminationResult> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycleLock = new();
    private readonly Action<SimulationEngineFaultPoint>? _faultInjector;
    private double _timeScale;
    private DeterministicSignalHub _signalHub;
    private DeterministicMachineLayout? _machineLayout;
    private DeterministicPickPlaceWorkpiece? _pickPlaceWorkpiece;
    private SimulationSnapshot _latestSnapshot;
    private Task? _runTask;
    private SimulationRunMode _runMode = SimulationRunMode.Paused;
    private SimulationControlOwner _controlOwner = SimulationControlOwner.Definition;
    private string? _activeSequenceId;
    private AutomaticRunConfiguration? _automaticRunConfiguration;
    private DeterministicConditionScenarioProfile? _conditionScenarioProfile;
    private DeterministicConditionStateMachine? _conditionStateMachine;
    private bool _automaticRunActive;
    private bool _conditionScenarioActive;
    private bool _conditionScheduledFaultActive;
    private bool _conditionScheduledFaultInterruptedAutomaticRun;
    private bool _automaticRunWaitingForRepeat;
    private long _automaticRunCompletedCycleCount;
    private int _automaticRunRemainingDelayTicks;
    private int _automaticRunRepeatDelayTicks;
    private int _pendingSteps;
    private long _conditionScenarioExecutedTicks;
    private DeterministicConditionTransition? _conditionLastTransition;
    private long _tickIndex;
    private long _eventIndex;
    private long _commandBoundaryTick;
    private TimeSpan _commandBoundaryTime;
    private EngineLifecycleState _lifecycleState = EngineLifecycleState.Created;
    private SimulationEngineTerminationOutcome _requestedTermination =
        SimulationEngineTerminationOutcome.Normal;
    private SimulationEngineTerminationResult? _terminalResult;
    private CancellationTokenRegistration _startCancellationRegistration;
    private SimulationCommand? _currentCommand;
    private string? _operationContext;
    private bool _disposed;

    public FixedStepSimulationEngine(SimulationSettings settings)
        : this(settings, null)
    {
    }

    internal FixedStepSimulationEngine(
        SimulationSettings settings,
        Action<SimulationEngineFaultPoint>? faultInjector)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _faultInjector = faultInjector;
        if (settings.FixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "FixedStep must be positive.");
        }
        if (!double.IsFinite(settings.TimeScale) || settings.TimeScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "TimeScale must be finite and positive.");
        }
        if (settings.CommandQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "CommandQueueCapacity must be positive.");
        }
        if (settings.EventBufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "EventBufferCapacity must be positive.");
        }

        _timeScale = settings.TimeScale;
        _clock = new SimulationClock(settings.FixedStep);
        _runtimeConfigurationBuilder = new SimulationRuntimeConfigurationBuilder(settings.FixedStep);
        _commandChannel = Channel.CreateBounded<SimulationCommand>(
            new BoundedChannelOptions(settings.CommandQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _eventChannel = Channel.CreateBounded<SimulationEvent>(
            new BoundedChannelOptions(settings.EventBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        _snapshotStore = new LatestSnapshotStore();
        _signalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
        _latestSnapshot = CreateSnapshot();
    }

    public SimulationSnapshot CurrentSnapshot => Volatile.Read(ref _latestSnapshot);
    public TimeSpan FixedStep => _settings.FixedStep;
    public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotStore.Reader;
    public ChannelReader<SimulationEvent> EventReader => _eventChannel.Reader;
    public Task<SimulationEngineTerminationResult> Termination => _termination.Task;

    public ImmutableArray<DeterministicSimulationCommandTraceEntry> CommandTrace => _commandTraceStore.Snapshot();

    public DeterministicSimulationCommandTracePackage CreateCommandTracePackage() =>
        _commandTraceStore.CreatePackage(FixedStep);

    public void ClearCommandTrace()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _commandTraceStore.Clear();
    }

    public void AddAxis(ServoAxisComponent axis)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(axis);
        if (_runTask is not null)
        {
            throw new InvalidOperationException("Axes cannot be added directly after the engine starts.");
        }

        if (_axes.Any(existing => string.Equals(existing.Id, axis.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Axis id '{axis.Id}' is duplicated.", nameof(axis));
        }

        _axes.Add(axis);
        Volatile.Write(ref _latestSnapshot, CreateSnapshot());
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleLock)
        {
            if (_lifecycleState == EngineLifecycleState.Running)
            {
                return Task.CompletedTask;
            }

            if (_lifecycleState != EngineLifecycleState.Created)
            {
                throw new InvalidOperationException("A stopped simulation engine cannot be restarted.");
            }

            _requestedTermination = SimulationEngineTerminationOutcome.Normal;
            _lifecycleState = EngineLifecycleState.Running;
            _runTask = Task.Run(() => RunLoop(_stopCts.Token));
            if (cancellationToken.CanBeCanceled)
            {
                _startCancellationRegistration = cancellationToken.Register(
                    static state => ((FixedStepSimulationEngine)state!).RequestCancellation(),
                    this);
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleState == EngineLifecycleState.Created)
            {
                _requestedTermination = SimulationEngineTerminationOutcome.Stopped;
                _terminalResult = CreateTerminationResult(
                    SimulationEngineTerminationOutcome.Stopped,
                    exception: null,
                    _currentCommand?.CommandId,
                    _operationContext);
                _lifecycleState = EngineLifecycleState.Stopped;
                _commandChannel.Writer.TryComplete();
                _snapshotStore.Complete();
                _eventChannel.Writer.TryComplete();
                _termination.TrySetResult(_terminalResult);
            }
            else if (_lifecycleState == EngineLifecycleState.Running)
            {
                _requestedTermination = SimulationEngineTerminationOutcome.Stopped;
                _lifecycleState = EngineLifecycleState.Stopping;
                _commandChannel.Writer.TryComplete();
                _stopCts.Cancel();
            }
        }

        await _termination.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimulationCommandResult> EnqueueCommandAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        SimulationCommandErrorCode? lifecycleError;
        lock (_lifecycleLock)
        {
            lifecycleError = _lifecycleState switch
            {
                EngineLifecycleState.Created => SimulationCommandErrorCode.EngineNotStarted,
                EngineLifecycleState.Running => null,
                EngineLifecycleState.Faulted => SimulationCommandErrorCode.EngineFaulted,
                _ => SimulationCommandErrorCode.EngineStopped
            };
        }

        if (lifecycleError.HasValue)
        {
            return CompleteLifecycleRejection(command, lifecycleError.Value);
        }

        try
        {
            await _commandChannel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return CompleteLifecycleRejection(command, GetClosedChannelError());
        }

        return await command.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timing = new SimulationRunLoopTiming(_settings.FixedStep, _settings.MaxCatchUpTicks);
        timing.Reset(stopwatch.Elapsed);
        var pendingCommands = new List<PendingCommand>();
        SimulationEngineTerminationResult? termination = null;

        try
        {
            _operationContext = "SnapshotPublication";
            InjectFault(SimulationEngineFaultPoint.BeforeSnapshotPublication);
            PublishSnapshot();
            InjectFault(SimulationEngineFaultPoint.AfterSnapshotPublication);
            _operationContext = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var wasPaused = _runMode == SimulationRunMode.Paused;
                while (_commandChannel.Reader.TryRead(out var command))
                {
                    _currentCommand = command;
                    _operationContext = "ApplyCommand";
                    var pendingCommand = new PendingCommand(command);
                    pendingCommands.Add(pendingCommand);
                    InjectFault(SimulationEngineFaultPoint.BeforeCommandApplication);
                    pendingCommand.Result = ApplyCommand(command);
                    InjectFault(SimulationEngineFaultPoint.AfterCommandApplication);
                    _currentCommand = null;
                    _operationContext = null;
                }

                if (wasPaused && _runMode != SimulationRunMode.Paused)
                {
                    timing.Reset(stopwatch.Elapsed);
                }

                if (_runMode == SimulationRunMode.Paused)
                {
                    if (pendingCommands.Count > 0)
                    {
                        _operationContext = "SnapshotPublication";
                        InjectFault(SimulationEngineFaultPoint.BeforeSnapshotPublication);
                        PublishSnapshot();
                        InjectFault(SimulationEngineFaultPoint.AfterSnapshotPublication);
                    }

                    _operationContext = "CommandCompletion";
                    CompleteCommands(pendingCommands);
                    pendingCommands.Clear();
                    _operationContext = null;
                    timing.Reset(stopwatch.Elapsed);
                    _operationContext = "CommandWait";
                    await _commandChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    _operationContext = null;
                    continue;
                }

                var ticksToRun = 0;
                var stopTickBatchWhenPaused = _runMode != SimulationRunMode.SingleStep;
                if (_runMode == SimulationRunMode.SingleStep)
                {
                    ticksToRun = Math.Max(1, _pendingSteps);
                    _pendingSteps = 0;
                    _runMode = SimulationRunMode.Paused;
                    timing.AlignToWallTime(stopwatch.Elapsed);
                }
                else if (_runMode == SimulationRunMode.SequenceStep)
                {
                    ticksToRun = _settings.MaxCatchUpTicks;
                }
                else if (_runMode == SimulationRunMode.FastForward)
                {
                    ticksToRun = _settings.MaxCatchUpTicks;
                }
                else
                {
                    ticksToRun = timing.CalculateRealTimeTicks(stopwatch.Elapsed, _timeScale);
                }

                for (var index = 0; index < ticksToRun; index++)
                {
                    _operationContext = "Tick";
                    InjectFault(SimulationEngineFaultPoint.BeforeTick);
                    Tick();
                    InjectFault(SimulationEngineFaultPoint.AfterTick);
                    if (stopTickBatchWhenPaused && _runMode == SimulationRunMode.Paused)
                    {
                        break;
                    }
                }

                if (ticksToRun == 0 && pendingCommands.Count > 0)
                {
                    _operationContext = "SnapshotPublication";
                    InjectFault(SimulationEngineFaultPoint.BeforeSnapshotPublication);
                    PublishSnapshot();
                    InjectFault(SimulationEngineFaultPoint.AfterSnapshotPublication);
                }
                _operationContext = "CommandCompletion";
                CompleteCommands(pendingCommands);
                pendingCommands.Clear();
                _operationContext = null;

                if (_runMode == SimulationRunMode.RealTime && ticksToRun == 0)
                {
                    var delay = timing.CalculateRealTimeDelay(_timeScale);
                    _operationContext = "RealTimeDelay";
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    _operationContext = null;
                }
            }

            termination = CreateTerminationResult(
                GetRequestedTermination(),
                exception: null,
                _currentCommand?.CommandId,
                _operationContext);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            termination = CreateTerminationResult(
                GetRequestedTermination(),
                exception: null,
                _currentCommand?.CommandId,
                _operationContext);
        }
        catch (Exception exception)
        {
            termination = CreateTerminationResult(
                SimulationEngineTerminationOutcome.Faulted,
                exception,
                _currentCommand?.CommandId,
                _operationContext);
        }
        finally
        {
            FinalizeRun(
                termination ?? CreateTerminationResult(
                    SimulationEngineTerminationOutcome.Faulted,
                    new InvalidOperationException("The simulation engine terminated without a result."),
                    _currentCommand?.CommandId,
                    _operationContext),
                pendingCommands);
        }
    }

    private SimulationCommandResult ApplyCommand(SimulationCommand command)
    {
        _commandBoundaryTick = _tickIndex;
        _commandBoundaryTime = _clock.Time;
        SimulationCommandResult result;
        switch (command)
        {
            case PlayCommand:
            case PauseCommand:
            case StepCommand:
            case StepSequenceCommand:
            case SetSequenceBreakpointCommand:
                result = ApplyRunControlCommand(command);
                break;

            case ResetCommand:
                ResetRuntime();
                result = Accept(command, "Runtime state reset to authored initial values.");
                if (_conditionScenarioProfile is not null)
                {
                    EmitAtCommandBoundary(
                        "Condition",
                        "ConditionScenarioReset",
                        $"Condition scenario '{_conditionScenarioProfile.ScenarioId}' reset to " +
                        $"{_conditionScenarioProfile.InitialState} and stopped.",
                        command.CommandId);
                }
                EmitAtCommandBoundary(
                    "Runtime",
                    "RuntimeReset",
                    "Axes, I/O, workpiece, faults, cameras, sequence, condition scenario, clock, and tick index reset.",
                    command.CommandId);
                break;

            case ConfigureRuntimeCommand configureRuntime:
                result = ApplyRuntimeConfiguration(command, configureRuntime.Configuration);
                break;

            case ConfigureAxesCommand configureAxes:
                result = ApplyAxisConfiguration(command, configureAxes.Axes);
                break;

            case InjectSimulationFaultCommand:
            case ClearSimulationFaultCommand:
                result = ApplyFaultCommand(command);
                break;

            case StartConditionScenarioCommand:
                result = ApplyConditionScenarioCommand(command);
                break;

            case StopConditionScenarioCommand:
                result = ApplyStopConditionScenario(command);
                break;

            case StartSequenceCommand:
            case AbortSequenceCommand:
            case RetrySequenceCommand:
                result = ApplySequenceCommand(command);
                break;

            case StartAutomaticRunCommand:
                result = ApplyAutomaticRunCommand(command);
                break;

            case StartManualControlCommand:
            case TriggerVirtualCameraCommand:
            case MoveAbsoluteCommand:
            case MoveAxesAbsoluteCommand:
            case MoveRelativeCommand:
            case MoveVelocityCommand:
            case HomeAxisCommand:
            case JogAxisCommand:
            case StopAxisCommand:
            case StopAxesCommand:
            case SetCylinderCommand:
            case SetConveyorCommand:
            case SetVirtualInputCommand:
            case SetVirtualInputForceCommand:
            case SetDigitalSensorForceCommand:
                result = ApplyManualControlCommand(command);
                break;

            default:
                result = Reject(
                    command,
                    SimulationCommandErrorCode.UnsupportedCommand,
                    $"Command '{command.GetType().Name}' is not supported.");
                break;
        }

        EmitAtCommandBoundary(
            "Command",
            result.IsAccepted ? "CommandAccepted" : "CommandRejected",
            result.Detail ?? command.GetType().Name,
            command.CommandId);
        _commandTraceStore.Capture(command, result);
        return result;
    }

    private SimulationCommandResult ApplyRunControlCommand(SimulationCommand command)
    {
        var outcome = _runControlCommandHandler.Apply(
            command,
            new SimulationRunControlContext(
                _runMode,
                _pendingSteps,
                _activeSequenceId,
                CurrentSequenceStepId(),
                _compiledSequences,
                _sequenceExecutors,
                _sequenceDebugState,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.RunMode.HasValue)
        {
            _runMode = outcome.RunMode.Value;
        }
        if (outcome.ControlOwner.HasValue)
        {
            _controlOwner = outcome.ControlOwner.Value;
        }
        if (outcome.PendingSteps.HasValue)
        {
            _pendingSteps = outcome.PendingSteps.Value;
        }
        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationRunControlEvent>())
        {
            EmitAtCommandBoundary(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                command.CommandId);
        }

        return outcome.Result;
    }

    private void PauseAtSequenceDebugBoundary(
        SequenceExecutionResult execution,
        long eventTick,
        TimeSpan eventTime)
    {
        var rootSequenceId = execution.Snapshot.SequenceId;
        var sequenceId = execution.CurrentSequenceId
            ?? execution.Snapshot.ActiveSequenceId
            ?? rootSequenceId;
        var stepId = execution.CurrentStepId;
        if (stepId is not null && _sequenceDebugState.IsBreakpoint(sequenceId, stepId))
        {
            PauseForSequenceDebug(
                SequenceDebugPauseReason.Breakpoint,
                sequenceId,
                stepId,
                "SequenceBreakpointHit",
                $"{sequenceId} paused before {stepId} executes.",
                eventTick,
                eventTime);
            return;
        }

        if (_sequenceDebugState.IsSemanticStepBoundary(execution, rootSequenceId))
        {
            PauseForSequenceDebug(
                SequenceDebugPauseReason.SemanticStep,
                sequenceId,
                stepId,
                "SequenceSemanticStepPaused",
                $"{sequenceId} advanced one semantic step and paused at {stepId}.",
                eventTick,
                eventTime);
        }
    }

    private void PauseCompletedSemanticStep(
        SequenceDebugPauseReason reason,
        string? stepId,
        long eventTick,
        TimeSpan eventTime)
    {
        var sequenceId = _sequenceDebugState.GetActiveSemanticStepSequenceId(_activeSequenceId);
        if (sequenceId is null)
        {
            return;
        }

        PauseForSequenceDebug(
            reason,
            sequenceId,
            stepId,
            reason == SequenceDebugPauseReason.SequenceCompleted
                ? "SequenceSemanticStepCompleted"
                : "SequenceSemanticStepFaulted",
            reason == SequenceDebugPauseReason.SequenceCompleted
                ? $"{sequenceId} completed and paused."
                : $"{sequenceId} faulted and paused.",
            eventTick,
            eventTime);
    }

    private void PauseForSequenceDebug(
        SequenceDebugPauseReason reason,
        string sequenceId,
        string? stepId,
        string eventCode,
        string message,
        long eventTick,
        TimeSpan eventTime)
    {
        _runMode = SimulationRunMode.Paused;
        _sequenceDebugState.ClearPendingSemanticStep();
        _sequenceDebugState.SetPause(reason, stepId);
        Emit(
            "Sequence",
            eventCode,
            message,
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private string? CurrentSequenceStepId() =>
        _activeSequenceId is not null
        && _sequenceExecutors.TryGetValue(_activeSequenceId, out var executor)
            ? executor.CaptureSnapshot().CurrentStepId
            : null;

    private void ClearSequenceDebugConfiguration() => _sequenceDebugState.Clear();

    private SimulationCommandResult ApplyStopConditionScenario(SimulationCommand command)
    {
        var outcome = _conditionScenarioStopHandler.Apply(
            command,
            new SimulationConditionScenarioStopContext(
                _conditionScenarioActive,
                _conditionScenarioProfile,
                _conditionScenarioExecutedTicks,
                CreateConditionScheduledFaultRecoveryContext(
                    restartSequence: false,
                    command.CommandId),
                _conditionScheduledFaultRecoveryHandler));
        if (outcome.State is { } state)
        {
            _conditionScenarioActive = state.ScenarioActive;
            _conditionLastTransition = state.LastTransition;
            _conditionScheduledFaultActive = state.RecoveryState.ScheduledFaultActive;
            _conditionScheduledFaultInterruptedAutomaticRun = state.RecoveryState.InterruptedAutomaticRun;
            _activeSequenceId = state.RecoveryState.ActiveSequenceId;
            _controlOwner = state.RecoveryState.ControlOwner;
            _automaticRunActive = state.RecoveryState.AutomaticRunActive;
            _automaticRunWaitingForRepeat = state.RecoveryState.AutomaticRunWaitingForRepeat;
            _automaticRunRemainingDelayTicks = state.RecoveryState.AutomaticRunRemainingDelayTicks;
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationConditionScenarioStopEvent>())
        {
            Emit(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                operationEvent.CommandId,
                _commandBoundaryTick,
                _commandBoundaryTime);
        }

        return outcome.Result;
    }

    private SimulationCommandResult ApplyRuntimeConfiguration(
        SimulationCommand command,
        SimulationRuntimeConfiguration configuration)
    {
        if (!_runtimeConfigurationBuilder.TryBuild(
                configuration,
                out var candidate,
                out var configurationError))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, configurationError);
        }

        SimulationRuntimeConfigurationBuildResult runtime = candidate!;
        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        ClearSequenceDebugConfiguration();
        _clock.Reset();
        _tickIndex = 0;
        _axes.Clear();
        _axes.AddRange(runtime.Axes);
        _cameras.Clear();
        _cameras.AddRange(runtime.Cameras);
        _signalHub = runtime.SignalHub;
        _machineLayout = runtime.MachineLayout;
        _pickPlaceWorkpiece = runtime.PickPlaceWorkpiece;
        if (configuration.TimeScale.HasValue)
        {
            _timeScale = configuration.TimeScale.Value;
        }
        _activeFaults.Clear();
        ClearConditionScenarioState();
        _compiledSequences.Clear();
        foreach (var pair in runtime.CompiledSequences)
        {
            _compiledSequences.Add(pair.Key, pair.Value);
        }
        _sequenceExecutors.Clear();
        foreach (var pair in runtime.SequenceExecutors)
        {
            _sequenceExecutors.Add(pair.Key, pair.Value);
        }
        _automaticRunConfiguration = configuration.AutomaticRun;
        _automaticRunRepeatDelayTicks = runtime.AutomaticRunRepeatDelayTicks;
        ResetAutomaticRunState();
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;

        var configurationSummary =
            $"Configured {_axes.Count} axis/axes, {configuration.Channels.Count} signal(s), " +
            $"{_cameras.Count} camera(s), {_sequenceExecutors.Count} sequence(s), and " +
            $"{configuration.Layout?.Components.Count ?? 0} layout component(s).";
        if (_pickPlaceWorkpiece is not null)
        {
            configurationSummary += " Configured 1 Pick-and-Place workpiece.";
        }

        EmitAtCommandBoundary(
            "Runtime",
            "RuntimeConfigured",
            configurationSummary,
            command.CommandId);
        return Accept(command, "Runtime configuration applied atomically.");
    }

    private SimulationCommandResult ApplyAxisConfiguration(
        SimulationCommand command,
        IReadOnlyList<AxisConfiguration> configurations)
    {
        if (!_runtimeConfigurationBuilder.TryCreateAxes(configurations, out var axes, out var error))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, error);
        }

        var emptySignalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;

        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        ClearSequenceDebugConfiguration();
        _clock.Reset();
        _tickIndex = 0;
        _axes.Clear();
        _axes.AddRange(axes);
        _cameras.Clear();
        _signalHub = emptySignalHub;
        _machineLayout = null;
        _pickPlaceWorkpiece = null;
        _activeFaults.Clear();
        ClearConditionScenarioState();
        _compiledSequences.Clear();
        _sequenceExecutors.Clear();
        _automaticRunConfiguration = null;
        _automaticRunRepeatDelayTicks = 0;
        ResetAutomaticRunState();
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;
        EmitAtCommandBoundary(
            "Runtime",
            "AxesConfigured",
            $"Configured {_axes.Count} axis/axes; I/O, camera, and sequence runtime were cleared.",
            command.CommandId);
        return Accept(command, "Axis configuration replaced.");
    }

    private SimulationCommandResult ApplyManualControlCommand(SimulationCommand command)
    {
        var outcome = _manualControlCommandHandler.Apply(
            command,
            new SimulationManualControlContext(
                _runMode,
                _controlOwner,
                _automaticRunActive,
                _axes,
                _cameras,
                _sequenceExecutors,
                _signalHub,
                _machineLayout,
                _activeFaults,
                _commandBoundaryTick,
                _commandBoundaryTime,
                FormatSignal));
        if (outcome.RunMode.HasValue)
        {
            _runMode = outcome.RunMode.Value;
        }
        if (outcome.ControlOwner.HasValue)
        {
            _controlOwner = outcome.ControlOwner.Value;
        }
        if (outcome.PendingSteps.HasValue)
        {
            _pendingSteps = outcome.PendingSteps.Value;
        }
        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationManualControlEvent>())
        {
            EmitAtCommandBoundary(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                command.CommandId);
        }

        return outcome.Result;
    }

    private SimulationCommandResult ApplyFaultCommand(SimulationCommand command)
    {
        var outcome = _faultCommandHandler.Apply(
            command,
            new SimulationFaultCommandContext(
                _axes,
                _signalHub,
                _machineLayout,
                _activeFaults,
                _commandBoundaryTick,
                _commandBoundaryTime));
        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationFaultCommandEvent>())
        {
            EmitAtCommandBoundary(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                command.CommandId);
        }

        return outcome.Result;
    }

    private SimulationCommandResult ApplyConditionScenarioCommand(SimulationCommand command)
    {
        var outcome = _conditionScenarioCommandHandler.Apply(
            command,
            new SimulationConditionScenarioCommandContext(
                _conditionScenarioActive,
                CreateSnapshot(),
                _sequenceExecutors,
                _activeFaults,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.State is { } state)
        {
            _conditionScenarioProfile = state.Profile;
            _conditionStateMachine = state.StateMachine;
            _conditionScenarioExecutedTicks = 0;
            _conditionLastTransition = null;
            _conditionScheduledFaultActive = false;
            _conditionScheduledFaultInterruptedAutomaticRun = false;
            _conditionScenarioActive = state.IsActive;
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationConditionScenarioCommandEvent>())
        {
            EmitAtCommandBoundary(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                command.CommandId);
        }

        return outcome.Result;
    }

    private SimulationCommandResult ApplySequenceCommand(SimulationCommand command)
    {
        var outcome = _sequenceCommandHandler.Apply(
            command,
            new SimulationSequenceCommandContext(
                new SimulationSequenceCommandState(
                    _runMode,
                    _controlOwner,
                    _pendingSteps,
                    _activeSequenceId,
                    _automaticRunActive,
                    _automaticRunWaitingForRepeat,
                    _automaticRunRemainingDelayTicks,
                    _conditionScheduledFaultInterruptedAutomaticRun),
                _sequenceExecutors,
                _activeFaults,
                _sequenceDebugState,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.State is { } state)
        {
            _runMode = state.RunMode;
            _controlOwner = state.ControlOwner;
            _pendingSteps = state.PendingSteps;
            _activeSequenceId = state.ActiveSequenceId;
            _automaticRunActive = state.AutomaticRunActive;
            _automaticRunWaitingForRepeat = state.AutomaticRunWaitingForRepeat;
            _automaticRunRemainingDelayTicks = state.AutomaticRunRemainingDelayTicks;
            _conditionScheduledFaultInterruptedAutomaticRun =
                state.ConditionScheduledFaultInterruptedAutomaticRun;
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationSequenceCommandEvent>())
        {
            EmitAtCommandBoundary(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                command.CommandId);
        }

        return outcome.Result;
    }

    private SimulationCommandResult ApplyAutomaticRunCommand(SimulationCommand command)
    {
        var outcome = _automaticRunCommandHandler.Apply(
            command,
            new SimulationAutomaticRunCommandContext(
                _automaticRunConfiguration,
                new SimulationAutomaticRunCommandState(
                    _runMode,
                    _controlOwner,
                    _pendingSteps,
                    _activeSequenceId,
                    _automaticRunActive,
                    _automaticRunWaitingForRepeat,
                    _automaticRunCompletedCycleCount,
                    _automaticRunRemainingDelayTicks),
                _signalHub,
                _sequenceExecutors,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.State is { } state)
        {
            _runMode = state.RunMode;
            _controlOwner = state.ControlOwner;
            _pendingSteps = state.PendingSteps;
            _activeSequenceId = state.ActiveSequenceId;
            _automaticRunActive = state.AutomaticRunActive;
            _automaticRunWaitingForRepeat = state.AutomaticRunWaitingForRepeat;
            _automaticRunCompletedCycleCount = state.AutomaticRunCompletedCycleCount;
            _automaticRunRemainingDelayTicks = state.AutomaticRunRemainingDelayTicks;
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationAutomaticRunCommandEvent>())
        {
            EmitAtCommandBoundary(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                command.CommandId);
        }

        return outcome.Result;
    }

    private void Tick()
    {
        var eventTick = _tickIndex + 1;
        var eventTime = _clock.Time + _settings.FixedStep;

        AdvanceConditionScenario(eventTick, eventTime);

        foreach (var axis in _axes)
        {
            var previousState = axis.State;
            var driveAlarmWasActive = axis.DriveAlarmActive;
            axis.Tick(_settings.FixedStep);
            if (!driveAlarmWasActive && axis.DriveAlarmActive)
            {
                Emit(
                    "Motion",
                    "AxisDriveAlarmActivated",
                    $"{axis.Id} following error {axis.FollowingError:F3} exceeded " +
                    $"limit {axis.FollowingErrorLimit:F3}.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }
            if (previousState == AxisState.Moving && axis.State == AxisState.Idle)
            {
                Emit(
                    "Motion",
                    "AxisTargetReached",
                    $"{axis.Id} reached {axis.Position:F3}.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }
        }

        if (_machineLayout is not null)
        {
            var axisSnapshots = _axes.ToDictionary(
                axis => axis.Id,
                axis => axis.CreateSnapshot(),
                StringComparer.Ordinal);
            HashSet<string> blockedCylinderIds = _activeFaults.Values
                .Where(fault => fault.Kind == SimulationFaultKind.CylinderTravelBlocked)
                .Select(fault => fault.TargetId)
                .ToHashSet(StringComparer.Ordinal);
            var cameraSnapshots = _cameras.ToDictionary(
                camera => camera.Id,
                camera => camera.CaptureSnapshot(),
                StringComparer.Ordinal);
            var layoutTick = _machineLayout.Tick(
                axisSnapshots,
                blockedCylinderIds,
                cameraSnapshots);
            foreach (var transition in layoutTick.Transitions)
            {
                Emit(
                    "Sensor",
                    transition.Kind == MachineLayoutTransitionKind.SensorActivated
                        ? "SensorActivated"
                        : "SensorDeactivated",
                    $"{transition.ComponentId} wrote {transition.OutputChannelId} = " +
                    $"{FormatSignal(transition.CurrentValue)}.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }

            foreach (var transition in layoutTick.CylinderStateTransitions)
            {
                Emit(
                    "Cylinder",
                    "CylinderStateChanged",
                    $"{transition.ComponentId}: {transition.PreviousState} -> " +
                    $"{transition.CurrentState} ({transition.MotionProgress:P0}).",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }

            foreach (var transition in layoutTick.CylinderFeedbackTransitions)
            {
                Emit(
                    "Cylinder",
                    "CylinderFeedbackChanged",
                    $"{transition.ComponentId} wrote {transition.ChannelId} = " +
                    $"{FormatSignal(transition.CurrentValue)}.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }

            foreach (var transition in layoutTick.ConveyorStateTransitions)
            {
                Emit(
                    "Conveyor",
                    "ConveyorStateChanged",
                    $"{transition.ComponentId}: " +
                    $"{FormatConveyorState(transition.PreviousRunning, transition.PreviousDirection)} -> " +
                    $"{FormatConveyorState(transition.CurrentRunning, transition.CurrentDirection)} " +
                    $"at {transition.SpeedUnitsPerSecond:F3} units/s.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }
        }

        foreach (var camera in _cameras)
        {
            var cameraTick = camera.Tick();
            if (cameraTick.Transition == VirtualCameraTickTransition.ExposureCompleted)
            {
                Emit(
                    "Camera",
                    "CameraExposureCompleted",
                    $"{camera.Id} exposure completed for {cameraTick.Snapshot.CurrentAcquisitionId}; transfer started.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }
            else if (cameraTick.Transition == VirtualCameraTickTransition.FrameReady)
            {
                var acquisition = cameraTick.CompletedAcquisition!;
                Emit(
                    "Camera",
                    "CameraFrameReady",
                    $"{camera.Id} frame {acquisition.AcquisitionId} is ready for recipe " +
                    $"'{acquisition.RecipeId}'" +
                    (acquisition.FrameEvidence is null
                        ? "."
                        : $"; SHA-256 {acquisition.FrameEvidence.ContentSha256}."),
                    tickIndex: eventTick,
                    simulationTime: eventTime);
                Emit(
                    "Vision",
                    "VisionResultReady",
                    acquisition.InspectionEvidence is { } inspection
                        ? $"{acquisition.AcquisitionId} inspection {inspection.InspectionId} result = " +
                          $"{inspection.Decision.ToString().ToUpperInvariant()}; " +
                          $"metrics {FormatInspectionMetrics(inspection.Metrics)}."
                        : $"{acquisition.AcquisitionId} placeholder result = " +
                          $"{acquisition.Decision.ToString().ToUpperInvariant()}.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
            }
        }

        AdvanceAutomaticRunRepeat(eventTick, eventTime);

        if (_activeSequenceId is not null
            && _sequenceExecutors.TryGetValue(_activeSequenceId, out var executor)
            && executor.CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            var context = new DeterministicSequenceRuntimeContext(
                _signalHub,
                _axes,
                _cameras,
                eventTick,
                eventTime,
                EmitSequenceRuntimeEvent);
            var execution = executor.Tick(_settings.FixedStep, context);
            if (execution.Transitioned)
            {
                var previousSequenceId = execution.PreviousSequenceId ?? _activeSequenceId;
                var currentSequenceId = execution.CurrentSequenceId ?? _activeSequenceId;
                var transitionMessage = string.Equals(
                        previousSequenceId,
                        currentSequenceId,
                        StringComparison.Ordinal)
                    ? $"{currentSequenceId}: {execution.PreviousStepId} -> {execution.CurrentStepId}."
                    : $"{previousSequenceId}:{execution.PreviousStepId} -> "
                      + $"{currentSequenceId}:{execution.CurrentStepId}.";
                Emit(
                    "Sequence",
                    "SequenceStepTransition",
                    transitionMessage,
                    tickIndex: eventTick,
                    simulationTime: eventTime);
                PauseAtSequenceDebugBoundary(execution, eventTick, eventTime);
            }

            if (execution.Snapshot.Status == SequenceExecutionStatus.Completed)
            {
                Emit(
                    "Sequence",
                    "SequenceCompleted",
                    $"{_activeSequenceId} completed.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
                CompleteAutomaticRunCycle(eventTick, eventTime);
                PauseCompletedSemanticStep(
                    SequenceDebugPauseReason.SequenceCompleted,
                    execution.Snapshot.CurrentStepId,
                    eventTick,
                    eventTime);
            }
            else if (execution.Snapshot.Status == SequenceExecutionStatus.Faulted)
            {
                Emit(
                    "Sequence",
                    "SequenceFaulted",
                    execution.Error?.Message ?? $"{_activeSequenceId} faulted.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
                FaultAutomaticRun(eventTick, eventTime);
                PauseCompletedSemanticStep(
                    SequenceDebugPauseReason.SequenceFaulted,
                    execution.Snapshot.CurrentStepId,
                    eventTick,
                    eventTime);
            }
        }

        AdvancePickPlaceWorkpiece(eventTick, eventTime);

        _clock.Advance();
        _tickIndex = eventTick;
        PublishSnapshot();
    }

    private static string FormatInspectionMetrics(IReadOnlyDictionary<string, double> metrics) =>
        metrics.Count == 0
            ? "none"
            : string.Join(
                ", ",
                metrics.Select(metric => string.Concat(
                    metric.Key,
                    "=",
                    metric.Value.ToString("R", CultureInfo.InvariantCulture))));

    private void AdvanceConditionScenario(long eventTick, TimeSpan eventTime)
    {
        if (!_conditionScenarioActive ||
            _conditionScenarioProfile is null ||
            _conditionStateMachine is null)
        {
            return;
        }

        var scenarioTick = _conditionScenarioExecutedTicks;
        AdvanceConditionScheduledFault(scenarioTick, eventTick, eventTime);
        var outcome = _conditionScenarioProgressHandler.Apply(
            new SimulationConditionScenarioProgressContext(
                _conditionScenarioProfile,
                _conditionStateMachine,
                _conditionScenarioActive,
                scenarioTick));
        _conditionScenarioExecutedTicks = outcome.State.ExecutedTicks;
        _conditionLastTransition = outcome.State.LastTransition;
        _conditionScenarioActive = outcome.State.IsActive;
        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationConditionScenarioProgressEvent>())
        {
            Emit(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                tickIndex: eventTick,
                simulationTime: eventTime);
        }
    }

    private void AdvanceConditionScheduledFault(
        long scenarioTick,
        long eventTick,
        TimeSpan eventTime)
    {
        var schedule = _conditionScenarioProfile?.FaultRecovery;
        if (schedule is null)
        {
            return;
        }

        if (scenarioTick == schedule.InjectTick)
        {
            _commandBoundaryTick = eventTick;
            _commandBoundaryTime = eventTime;
            var outcome = _conditionScheduledFaultInjectionHandler.Apply(
                new SimulationConditionScheduledFaultInjectionContext(
                    schedule,
                    scenarioTick,
                    _axes,
                    _signalHub,
                    _machineLayout,
                    _activeFaults,
                    _faultCommandHandler,
                    _commandBoundaryTick,
                    _commandBoundaryTime));
            if (outcome.ScheduledFaultActive is { } scheduledFaultActive)
            {
                _conditionScheduledFaultActive = scheduledFaultActive;
            }
            if (outcome.ConditionScenarioActive is { } conditionScenarioActive)
            {
                _conditionScenarioActive = conditionScenarioActive;
            }
            foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationConditionScheduledFaultInjectionEvent>())
            {
                Emit(
                    operationEvent.Category,
                    operationEvent.Code,
                    operationEvent.Message,
                    operationEvent.CommandId,
                    eventTick,
                    eventTime);
            }

            return;
        }

        if (_conditionScheduledFaultActive
            && scenarioTick == schedule.InjectTick + schedule.HoldTicks)
        {
            ClearConditionScheduledFault(eventTick, eventTime, restartSequence: true);
        }
    }

    private void ClearConditionScheduledFault(
        long eventTick,
        TimeSpan eventTime,
        bool restartSequence,
        string? commandId = null)
    {
        var schedule = _conditionScenarioProfile?.FaultRecovery;
        if (!_conditionScheduledFaultActive || schedule is null)
        {
            return;
        }

        _commandBoundaryTick = eventTick;
        _commandBoundaryTime = eventTime;
        var outcome = _conditionScheduledFaultRecoveryHandler.Apply(
            CreateConditionScheduledFaultRecoveryContext(restartSequence, commandId));
        if (outcome.State is { } state)
        {
            _conditionScheduledFaultActive = state.ScheduledFaultActive;
            _conditionScheduledFaultInterruptedAutomaticRun = state.InterruptedAutomaticRun;
            _activeSequenceId = state.ActiveSequenceId;
            _controlOwner = state.ControlOwner;
            _automaticRunActive = state.AutomaticRunActive;
            _automaticRunWaitingForRepeat = state.AutomaticRunWaitingForRepeat;
            _automaticRunRemainingDelayTicks = state.AutomaticRunRemainingDelayTicks;
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationConditionScheduledFaultRecoveryEvent>())
        {
            Emit(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                operationEvent.CommandId,
                eventTick,
                eventTime);
        }
    }

    private SimulationConditionScheduledFaultRecoveryContext CreateConditionScheduledFaultRecoveryContext(
        bool restartSequence,
        string? commandId) =>
        new(
            _conditionScenarioProfile?.FaultRecovery,
            restartSequence,
            commandId,
            new SimulationConditionScheduledFaultRecoveryState(
                _conditionScheduledFaultActive,
                _conditionScheduledFaultInterruptedAutomaticRun,
                _activeSequenceId,
                _controlOwner,
                _automaticRunActive,
                _automaticRunWaitingForRepeat,
                _automaticRunRemainingDelayTicks),
            _axes,
            _signalHub,
            _machineLayout,
            _activeFaults,
            _sequenceExecutors,
            _faultCommandHandler,
            _commandBoundaryTick,
            _commandBoundaryTime);

    private void AdvanceAutomaticRunRepeat(long eventTick, TimeSpan eventTime)
    {
        var outcome = _automaticRunCycleHandler.AdvanceRepeat(CreateAutomaticRunCycleContext());
        ApplyAutomaticRunCycleOutcome(outcome, eventTick, eventTime);
        if (outcome.FaultDetail is not null)
        {
            FaultAutomaticRun(eventTick, eventTime, outcome.FaultDetail);
        }
    }

    private void CompleteAutomaticRunCycle(long eventTick, TimeSpan eventTime)
    {
        var outcome = _automaticRunCycleHandler.Complete(CreateAutomaticRunCycleContext());
        ApplyAutomaticRunCycleOutcome(outcome, eventTick, eventTime);
    }

    private SimulationAutomaticRunCycleContext CreateAutomaticRunCycleContext() =>
        new(
            _automaticRunConfiguration,
            new SimulationAutomaticRunCycleState(
                _activeSequenceId,
                _automaticRunActive,
                _automaticRunWaitingForRepeat,
                _automaticRunCompletedCycleCount,
                _automaticRunRemainingDelayTicks),
            _sequenceExecutors,
            _automaticRunRepeatDelayTicks);

    private void ApplyAutomaticRunCycleOutcome(
        SimulationAutomaticRunCycleOutcome outcome,
        long eventTick,
        TimeSpan eventTime)
    {
        if (outcome.State is { } state)
        {
            _activeSequenceId = state.ActiveSequenceId;
            _automaticRunActive = state.AutomaticRunActive;
            _automaticRunWaitingForRepeat = state.AutomaticRunWaitingForRepeat;
            _automaticRunCompletedCycleCount = state.AutomaticRunCompletedCycleCount;
            _automaticRunRemainingDelayTicks = state.AutomaticRunRemainingDelayTicks;
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationAutomaticRunCycleEvent>())
        {
            Emit(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                tickIndex: eventTick,
                simulationTime: eventTime);
        }
    }

    private void FaultAutomaticRun(
        long eventTick,
        TimeSpan eventTime,
        string? detail = null)
    {
        if (!_automaticRunActive)
        {
            return;
        }

        var recovery = _conditionScenarioProfile?.FaultRecovery;
        _conditionScheduledFaultInterruptedAutomaticRun =
            _conditionScheduledFaultActive
            && recovery?.RestartSequenceId is not null
            && string.Equals(
                _activeSequenceId,
                recovery.RestartSequenceId,
                StringComparison.Ordinal);

        _automaticRunActive = false;
        _automaticRunWaitingForRepeat = false;
        _automaticRunRemainingDelayTicks = 0;
        Emit(
            "AutomaticRun",
            "AutomaticRunFaulted",
            detail ?? "The automatic sequence faulted.",
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private void ResetAutomaticRunState()
    {
        _automaticRunActive = false;
        _automaticRunWaitingForRepeat = false;
        _automaticRunCompletedCycleCount = 0;
        _automaticRunRemainingDelayTicks = 0;
    }

    private void ResetRuntime()
    {
        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _sequenceDebugState.ClearPendingSemanticStep();
        _sequenceDebugState.SetPause(SequenceDebugPauseReason.None, null);
        _clock.Reset();
        _tickIndex = 0;
        foreach (var axis in _axes)
        {
            axis.Reset();
        }
        foreach (var camera in _cameras)
        {
            camera.Reset();
        }
        _activeFaults.Clear();
        _signalHub.Reset();
        _machineLayout?.Reset();
        _pickPlaceWorkpiece?.Reset();
        foreach (var executor in _sequenceExecutors.Values)
        {
            executor.Reset();
        }
        ResetAutomaticRunState();
        ResetConditionScenarioState();
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;
    }

    private SimulationSnapshot CreateSnapshot()
    {
        var signals = _signalHub.CaptureSnapshot();
        return new SimulationSnapshot(
            _clock.Time,
            _tickIndex,
            _runMode,
            _controlOwner,
            _timeScale,
            _axes.Select(axis => axis.CreateSnapshot()),
            signals.Revision,
            signals.Signals,
            _sequenceExecutors
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.CaptureSnapshot()),
            _cameras
                .OrderBy(camera => camera.Id, StringComparer.Ordinal)
                .Select(camera => camera.CaptureSnapshot()),
            new AutomaticRunSnapshot(
                _automaticRunConfiguration is not null,
                _automaticRunActive,
                _automaticRunWaitingForRepeat,
                _automaticRunCompletedCycleCount,
                _automaticRunRemainingDelayTicks),
            _machineLayout is null
                ? Array.Empty<LayoutComponentSnapshot>()
                : _machineLayout.CaptureSnapshots(),
            _activeFaults.Values,
            CreateConditionScenarioSnapshot(),
            _pickPlaceWorkpiece is null
                ? Array.Empty<PickPlaceWorkpieceSnapshot>()
                : new[] { _pickPlaceWorkpiece.CaptureSnapshot() },
            _machineLayout is null
                ? Array.Empty<LoadLockSnapshot>()
                : _machineLayout.CaptureLoadLockSnapshots(),
            _machineLayout is null
                ? Array.Empty<WaferHandlerSnapshot>()
                : _machineLayout.CaptureWaferHandlerSnapshots(),
            _machineLayout is null
                ? Array.Empty<InspectionSortRouterSnapshot>()
                : _machineLayout.CaptureInspectionSortRouterSnapshots(),
            _machineLayout is null
                ? Array.Empty<InspectionHandoffSnapshot>()
                : _machineLayout.CaptureInspectionHandoffSnapshots(),
            _machineLayout is null
                ? Array.Empty<OhtHandoffSnapshot>()
                : _machineLayout.CaptureOhtHandoffSnapshots(),
            _machineLayout is null
                ? Array.Empty<PrealignerSnapshot>()
                : _machineLayout.CapturePrealignerSnapshots(),
             _sequenceDebugState.CreateSnapshot(),
             analogSignals: signals.AnalogSignals);
    }

    private void AdvancePickPlaceWorkpiece(long eventTick, TimeSpan eventTime)
    {
        if (_pickPlaceWorkpiece is null)
        {
            return;
        }

        var x = _axes.Single(axis => string.Equals(
            axis.Id,
            _pickPlaceWorkpiece.XAxisId,
            StringComparison.Ordinal)).Position;
        var y = _axes.Single(axis => string.Equals(
            axis.Id,
            _pickPlaceWorkpiece.YAxisId,
            StringComparison.Ordinal)).Position;
        var gripper = _signalHub.ReadDigitalSignal(_pickPlaceWorkpiece.GripperSignalId);
        PickPlaceWorkpieceTransition? transition = _pickPlaceWorkpiece.Tick(x, y, gripper.Value == true);
        if (transition is null)
        {
            return;
        }

        var code = transition.CurrentState == PickPlaceWorkpieceState.Attached
            ? "WorkpieceAttached"
            : "WorkpiecePlaced";
        Emit(
            "Workpiece",
            code,
            FormattableString.Invariant(
                $"{_pickPlaceWorkpiece.CaptureSnapshot().Id}: {transition.PreviousState} -> {transition.CurrentState} at X {transition.X:F3}, Y {transition.Y:F3}."),
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private DeterministicConditionScenarioSnapshot CreateConditionScenarioSnapshot()
    {
        if (_conditionScenarioProfile is null || _conditionStateMachine is null)
        {
            return DeterministicConditionScenarioSnapshot.NotConfigured;
        }

        return new DeterministicConditionScenarioSnapshot(
            true,
            _conditionScenarioActive,
            _conditionScenarioProfile.ScenarioId,
            _conditionScenarioProfile.TargetId,
            _conditionScenarioProfile.Seed,
            _conditionScenarioProfile.DurationTicks,
            _conditionScenarioExecutedTicks,
            _conditionScenarioProfile.InitialState,
            _conditionStateMachine.State,
            _conditionStateMachine.HealthScore,
            _conditionLastTransition);
    }

    private void ResetConditionScenarioState()
    {
        _conditionScenarioActive = false;
        _conditionScheduledFaultActive = false;
        _conditionScheduledFaultInterruptedAutomaticRun = false;
        _conditionScenarioExecutedTicks = 0;
        _conditionLastTransition = null;
        _conditionStateMachine = _conditionScenarioProfile is null
            ? null
            : new DeterministicConditionStateMachine(_conditionScenarioProfile);
    }

    private void ClearConditionScenarioState()
    {
        _conditionScenarioProfile = null;
        _conditionStateMachine = null;
        _conditionScenarioActive = false;
        _conditionScheduledFaultActive = false;
        _conditionScheduledFaultInterruptedAutomaticRun = false;
        _conditionScenarioExecutedTicks = 0;
        _conditionLastTransition = null;
    }

    private void PublishSnapshot()
    {
        var snapshot = CreateSnapshot();
        Volatile.Write(ref _latestSnapshot, snapshot);
        _snapshotStore.Writer.TryWrite(snapshot);
    }

    private void EmitSequenceRuntimeEvent(
        string category,
        string code,
        string message,
        long tickIndex,
        TimeSpan simulationTime) =>
        Emit(category, code, message, tickIndex: tickIndex, simulationTime: simulationTime);

    private void Emit(
        string category,
        string code,
        string message,
        string? commandId = null,
        long? tickIndex = null,
        TimeSpan? simulationTime = null)
    {
        var previousOperation = _operationContext;
        _operationContext = "EventPublication";
        InjectFault(SimulationEngineFaultPoint.BeforeEventPublication);
        _eventChannel.Writer.TryWrite(new SimulationEvent(
            ++_eventIndex,
            tickIndex ?? _tickIndex,
            simulationTime ?? _clock.Time,
            category,
            code,
            message,
            commandId));
        _operationContext = previousOperation;
    }

    private void EmitAtCommandBoundary(
        string category,
        string code,
        string message,
        string commandId)
    {
        Emit(
            category,
            code,
            message,
            commandId,
            _commandBoundaryTick,
            _commandBoundaryTime);
    }

    private void RequestCancellation()
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleState != EngineLifecycleState.Running)
            {
                return;
            }

            _requestedTermination = SimulationEngineTerminationOutcome.Cancelled;
            _lifecycleState = EngineLifecycleState.Stopping;
            _commandChannel.Writer.TryComplete();
            _stopCts.Cancel();
        }
    }

    private void FinalizeRun(
        SimulationEngineTerminationResult termination,
        IReadOnlyCollection<PendingCommand> pendingCommands)
    {
        lock (_lifecycleLock)
        {
            _terminalResult = termination;
            _lifecycleState = termination.Outcome == SimulationEngineTerminationOutcome.Faulted
                ? EngineLifecycleState.Faulted
                : EngineLifecycleState.Stopped;
            _commandChannel.Writer.TryComplete();
        }

        CompletePendingCommands(pendingCommands, termination);
        while (_commandChannel.Reader.TryRead(out var command))
        {
            command.TryComplete(CreateTerminalCommandResult(command, termination));
        }

        _snapshotStore.Complete();
        _eventChannel.Writer.TryComplete();
        _startCancellationRegistration.Dispose();
        _startCancellationRegistration = default;
        _termination.TrySetResult(termination);
    }

    private SimulationEngineTerminationOutcome GetRequestedTermination()
    {
        lock (_lifecycleLock)
        {
            return _requestedTermination;
        }
    }

    private SimulationEngineTerminationResult CreateTerminationResult(
        SimulationEngineTerminationOutcome outcome,
        Exception? exception,
        string? currentCommandId,
        string? operation) =>
        new(
            outcome,
            _tickIndex,
            _clock.Time,
            exception,
            currentCommandId,
            operation);

    private SimulationCommandErrorCode GetClosedChannelError()
    {
        lock (_lifecycleLock)
        {
            return _lifecycleState == EngineLifecycleState.Faulted
                ? SimulationCommandErrorCode.EngineFaulted
                : SimulationCommandErrorCode.EngineStopped;
        }
    }

    private SimulationCommandResult CreateTerminalCommandResult(
        SimulationCommand command,
        SimulationEngineTerminationResult termination) =>
        SimulationCommandResult.Rejected(
            command,
            termination.TickIndex,
            termination.SimulationTime,
            termination.Outcome == SimulationEngineTerminationOutcome.Faulted
                ? SimulationCommandErrorCode.EngineFaulted
                : SimulationCommandErrorCode.EngineStopped,
            CreateTerminationDetail(termination));

    private static void CompletePendingCommands(
        IEnumerable<PendingCommand> pendingCommands,
        SimulationEngineTerminationResult termination)
    {
        foreach (var pendingCommand in pendingCommands)
        {
            pendingCommand.Command.TryComplete(
                SimulationCommandResult.Rejected(
                    pendingCommand.Command,
                    termination.TickIndex,
                    termination.SimulationTime,
                    termination.Outcome == SimulationEngineTerminationOutcome.Faulted
                        ? SimulationCommandErrorCode.EngineFaulted
                        : SimulationCommandErrorCode.EngineStopped,
                    CreateTerminationDetail(termination)));
        }
    }

    private static string CreateTerminationDetail(SimulationEngineTerminationResult termination)
    {
        if (termination.Outcome == SimulationEngineTerminationOutcome.Faulted)
        {
            var context = string.Join(
                ", ",
                new[]
                {
                    termination.Operation is null ? null : $"operation={termination.Operation}",
                    termination.CurrentCommandId is null ? null : $"command={termination.CurrentCommandId}",
                    $"tick={termination.TickIndex}",
                    $"time={termination.SimulationTime}"
                }.Where(value => value is not null));
            return $"The simulation engine faulted ({context}): " +
                (termination.Exception?.Message ?? "Unknown simulation engine failure.");
        }

        return termination.Outcome == SimulationEngineTerminationOutcome.Cancelled
            ? "The simulation engine was cancelled before applying the command."
            : "The simulation engine stopped before applying the command.";
    }

    private void InjectFault(SimulationEngineFaultPoint faultPoint) =>
        _faultInjector?.Invoke(faultPoint);

    private SimulationCommandResult CompleteLifecycleRejection(
        SimulationCommand command,
        SimulationCommandErrorCode errorCode)
    {
        var snapshot = CurrentSnapshot;
        SimulationEngineTerminationResult? terminalResult;
        lock (_lifecycleLock)
        {
            terminalResult = _terminalResult;
        }

        var detail = errorCode switch
        {
            SimulationCommandErrorCode.EngineNotStarted => "The simulation engine has not started.",
            SimulationCommandErrorCode.EngineFaulted when terminalResult is not null =>
                CreateTerminationDetail(terminalResult),
            SimulationCommandErrorCode.EngineFaulted => "The simulation engine faulted.",
            _ => "The simulation engine is stopped."
        };
        var result = SimulationCommandResult.Rejected(
            command,
            snapshot.TickIndex,
            snapshot.SimulationTime,
            errorCode,
            detail);
        command.TryComplete(result);
        return result;
    }

    private SimulationCommandResult Accept(SimulationCommand command, string detail) =>
        SimulationCommandResult.Accepted(command, _commandBoundaryTick, _commandBoundaryTime, detail);

    private SimulationCommandResult Reject(
        SimulationCommand command,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        SimulationCommandResult.Rejected(
            command,
            _commandBoundaryTick,
            _commandBoundaryTime,
            errorCode,
            detail);

    private static void CompleteCommands(IEnumerable<PendingCommand> pendingCommands)
    {
        foreach (var pendingCommand in pendingCommands)
        {
            if (pendingCommand.Result is null)
            {
                throw new InvalidOperationException(
                    $"Command '{pendingCommand.Command.CommandId}' has no simulation result.");
            }

            pendingCommand.Command.TryComplete(pendingCommand.Result);
        }
    }

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";

    private static string FormatConveyorState(bool isRunning, ConveyorDirection direction) =>
        isRunning ? $"RUNNING {direction}" : $"STOPPED {direction}";

    private enum EngineLifecycleState
    {
        Created,
        Running,
        Stopping,
        Stopped,
        Faulted
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        _startCancellationRegistration.Dispose();
        _startCancellationRegistration = default;
        _stopCts.Dispose();
    }

    private sealed class PendingCommand(SimulationCommand command)
    {
        public SimulationCommand Command { get; } = command;
        public SimulationCommandResult? Result { get; set; }
    }

}
