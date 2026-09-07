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
    private readonly SimulationEventPublisher _eventPublisher;
    private readonly LatestSnapshotStore _snapshotStore;
    private readonly SimulationEngineLifecycle _lifecycle;
    private readonly SimulationPhysicalRuntimeTick _physicalRuntimeTick;
    private readonly List<ServoAxisComponent> _axes = new();
    private readonly DeterministicSimulationCommandTraceStore _commandTraceStore = new();
    private readonly List<DeterministicVirtualCamera> _cameras = new();
    private readonly SimulationSequenceRuntime _sequenceRuntime = new();
    private readonly SimulationRuntimeConfigurationBuilder _runtimeConfigurationBuilder;
    private readonly SimulationConditionScenarioRuntime _conditionScenarioRuntime = new();
    private readonly SimulationAutomaticRunRuntime _automaticRunRuntime = new();
    private readonly SimulationManualControlCommandHandler _manualControlCommandHandler = new();
    private readonly SimulationFaultCommandHandler _faultCommandHandler = new();
    private readonly SimulationConditionScheduledFaultRecoveryHandler _conditionScheduledFaultRecoveryHandler = new();
    private readonly SimulationConditionScheduledFaultInjectionHandler _conditionScheduledFaultInjectionHandler = new();
    private readonly SimulationConditionScenarioStopHandler _conditionScenarioStopHandler = new();
    private readonly SimulationConditionScenarioCommandHandler _conditionScenarioCommandHandler = new();
    private readonly SimulationAutomaticRunCommandHandler _automaticRunCommandHandler = new();
    private readonly SimulationAutomaticRunCycleHandler _automaticRunCycleHandler = new();
    private readonly SimulationSequenceCommandHandler _sequenceCommandHandler = new();
    private readonly SimulationRunControlCommandHandler _runControlCommandHandler = new();
    private readonly SimulationFaultRuntime _faultRuntime = new();
    private readonly Action<SimulationEngineFaultPoint>? _faultInjector;
    private double _timeScale;
    private DeterministicSignalHub _signalHub;
    private DeterministicMachineLayout? _machineLayout;
    private DeterministicPickPlaceWorkpiece? _pickPlaceWorkpiece;
    private SimulationRunMode _runMode = SimulationRunMode.Paused;
    private SimulationControlOwner _controlOwner = SimulationControlOwner.Definition;
    private string? _activeSequenceId;
    private int _pendingSteps;
    private long _tickIndex;
    private long _commandBoundaryTick;
    private TimeSpan _commandBoundaryTime;
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
        _eventPublisher = new SimulationEventPublisher(settings.EventBufferCapacity);
        _signalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
        _snapshotStore = new LatestSnapshotStore(CreateSnapshot());
        _physicalRuntimeTick = new SimulationPhysicalRuntimeTick(EmitPhysicalRuntimeEvent);
        _lifecycle = new SimulationEngineLifecycle(
            _commandChannel,
            _eventPublisher,
            _snapshotStore,
            (outcome, exception) => CreateTerminationResult(
                outcome,
                exception,
                _currentCommand?.CommandId,
                _operationContext),
            () => CurrentSnapshot);
    }

    public SimulationSnapshot CurrentSnapshot => _snapshotStore.Current;
    public TimeSpan FixedStep => _settings.FixedStep;
    public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotStore.Reader;
    public ChannelReader<SimulationEvent> EventReader => _eventPublisher.Reader;
    public Task<SimulationEngineTerminationResult> Termination => _lifecycle.Termination;

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
        if (_lifecycle.HasStarted)
        {
            throw new InvalidOperationException("Axes cannot be added directly after the engine starts.");
        }

        if (_axes.Any(existing => string.Equals(existing.Id, axis.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Axis id '{axis.Id}' is duplicated.", nameof(axis));
        }

        _axes.Add(axis);
        _snapshotStore.SetCurrent(CreateSnapshot());
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _lifecycle.StartAsync(RunLoop, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _lifecycle.StopAsync(cancellationToken);

    public Task<SimulationCommandResult> EnqueueCommandAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        return _lifecycle.EnqueueCommandAsync(command, cancellationToken);
    }

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timing = new SimulationRunLoopTiming(_settings.FixedStep, _settings.MaxCatchUpTicks);
        timing.Reset(stopwatch.Elapsed);
        var pendingCommands = new List<PendingSimulationCommand>();
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
                    var pendingCommand = new PendingSimulationCommand(command);
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
                    _lifecycle.CompleteAppliedCommands(pendingCommands);
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
                _lifecycle.CompleteAppliedCommands(pendingCommands);
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
                _lifecycle.GetRequestedTermination(),
                exception: null,
                _currentCommand?.CommandId,
                _operationContext);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            termination = CreateTerminationResult(
                _lifecycle.GetRequestedTermination(),
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
            _lifecycle.FinalizeRun(
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
                if (_conditionScenarioRuntime.Profile is not null)
                {
                    EmitAtCommandBoundary(
                        "Condition",
                        "ConditionScenarioReset",
                        $"Condition scenario '{_conditionScenarioRuntime.Profile.ScenarioId}' reset to " +
                        $"{_conditionScenarioRuntime.Profile.InitialState} and stopped.",
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
                _sequenceRuntime.CompiledSequences,
                _sequenceRuntime.SequenceExecutors,
                _sequenceRuntime.DebugState,
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
        if (stepId is not null && _sequenceRuntime.DebugState.IsBreakpoint(sequenceId, stepId))
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

        if (_sequenceRuntime.DebugState.IsSemanticStepBoundary(execution, rootSequenceId))
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
        var sequenceId = _sequenceRuntime.DebugState.GetActiveSemanticStepSequenceId(_activeSequenceId);
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
        _sequenceRuntime.DebugState.ClearPendingSemanticStep();
        _sequenceRuntime.DebugState.SetPause(reason, stepId);
        Emit(
            "Sequence",
            eventCode,
            message,
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private string? CurrentSequenceStepId() => _sequenceRuntime.CurrentStepId(_activeSequenceId);

    private void ClearSequenceDebugConfiguration() => _sequenceRuntime.DebugState.Clear();

    private SimulationCommandResult ApplyStopConditionScenario(SimulationCommand command)
    {
        var outcome = _conditionScenarioStopHandler.Apply(
            command,
            new SimulationConditionScenarioStopContext(
                _conditionScenarioRuntime.IsActive,
                _conditionScenarioRuntime.Profile,
                _conditionScenarioRuntime.ExecutedTicks,
                CreateConditionScheduledFaultRecoveryContext(
                    restartSequence: false,
                    command.CommandId),
                _conditionScheduledFaultRecoveryHandler));
        if (outcome.State is { } state)
        {
            _conditionScenarioRuntime.ApplyStopState(state);
            _activeSequenceId = state.RecoveryState.ActiveSequenceId;
            _controlOwner = state.RecoveryState.ControlOwner;
            _automaticRunRuntime.ApplyRecoveryState(state.RecoveryState);
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
        _faultRuntime.Clear();
        _conditionScenarioRuntime.Clear();
        _sequenceRuntime.Configure(runtime.CompiledSequences, runtime.SequenceExecutors);
        _automaticRunRuntime.Configure(
            configuration.AutomaticRun,
            runtime.AutomaticRunRepeatDelayTicks);
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;

        var configurationSummary =
            $"Configured {_axes.Count} axis/axes, {configuration.Channels.Count} signal(s), " +
            $"{_cameras.Count} camera(s), {_sequenceRuntime.SequenceExecutors.Count} sequence(s), and " +
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
        _faultRuntime.Clear();
        _conditionScenarioRuntime.Clear();
        _sequenceRuntime.ClearConfiguration();
        _automaticRunRuntime.Configure(null, repeatDelayTicks: 0);
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
                _automaticRunRuntime.IsActive,
                _axes,
                _cameras,
                _sequenceRuntime.SequenceExecutors,
                _signalHub,
                _machineLayout,
                _faultRuntime,
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
                _faultRuntime,
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
                _conditionScenarioRuntime.IsActive,
                CreateSnapshot(),
                _sequenceRuntime.SequenceExecutors,
                _faultRuntime,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.State is { } state)
        {
            _conditionScenarioRuntime.ApplyStartState(state);
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
                    _automaticRunRuntime.IsActive,
                    _automaticRunRuntime.WaitingForRepeat,
                    _automaticRunRuntime.RemainingDelayTicks,
                    _conditionScenarioRuntime.ScheduledFaultInterruptedAutomaticRun),
                _sequenceRuntime.SequenceExecutors,
                _faultRuntime,
                _sequenceRuntime.DebugState,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.State is { } state)
        {
            _runMode = state.RunMode;
            _controlOwner = state.ControlOwner;
            _pendingSteps = state.PendingSteps;
            _activeSequenceId = state.ActiveSequenceId;
            _automaticRunRuntime.ApplySequenceState(state);
            _conditionScenarioRuntime.SetAutomaticRunInterruption(
                state.ConditionScheduledFaultInterruptedAutomaticRun);
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
                _automaticRunRuntime.Configuration,
                _automaticRunRuntime.CreateCommandState(
                    _runMode,
                    _controlOwner,
                    _pendingSteps,
                    _activeSequenceId),
                _signalHub,
                _sequenceRuntime.SequenceExecutors,
                _commandBoundaryTick,
                _commandBoundaryTime));
        if (outcome.State is { } state)
        {
            _runMode = state.RunMode;
            _controlOwner = state.ControlOwner;
            _pendingSteps = state.PendingSteps;
            _activeSequenceId = state.ActiveSequenceId;
            _automaticRunRuntime.ApplyCommandState(state);
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

        IReadOnlySet<string>? blockedCylinderIds = _machineLayout is null
            ? null
            : _faultRuntime.Values
                    .Where(fault => fault.Kind == SimulationFaultKind.CylinderTravelBlocked)
                .Select(fault => fault.TargetId)
                .ToHashSet(StringComparer.Ordinal);
        _physicalRuntimeTick.Advance(
            new SimulationPhysicalRuntimeTickContext(
                _settings.FixedStep,
                eventTick,
                eventTime,
                _axes,
                _machineLayout,
                blockedCylinderIds,
                _cameras));

        AdvanceAutomaticRunRepeat(eventTick, eventTime);

        if (_activeSequenceId is not null
            && _sequenceRuntime.SequenceExecutors.TryGetValue(_activeSequenceId, out var executor)
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

    private void AdvanceConditionScenario(long eventTick, TimeSpan eventTime)
    {
        if (!_conditionScenarioRuntime.IsActive)
        {
            return;
        }

        var scenarioTick = _conditionScenarioRuntime.ExecutedTicks;
        AdvanceConditionScheduledFault(scenarioTick, eventTick, eventTime);
        foreach (var operationEvent in _conditionScenarioRuntime.Advance())
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
        var schedule = _conditionScenarioRuntime.Profile?.FaultRecovery;
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
                    _faultRuntime,
                    _faultCommandHandler,
                    _commandBoundaryTick,
                    _commandBoundaryTime));
            _conditionScenarioRuntime.ApplyScheduledFaultInjectionOutcome(outcome);
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

        if (_conditionScenarioRuntime.ScheduledFaultActive
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
        var schedule = _conditionScenarioRuntime.Profile?.FaultRecovery;
        if (!_conditionScenarioRuntime.ScheduledFaultActive || schedule is null)
        {
            return;
        }

        _commandBoundaryTick = eventTick;
        _commandBoundaryTime = eventTime;
        var outcome = _conditionScheduledFaultRecoveryHandler.Apply(
            CreateConditionScheduledFaultRecoveryContext(restartSequence, commandId));
        if (outcome.State is { } state)
        {
            _conditionScenarioRuntime.ApplyScheduledFaultRecoveryState(
                state.ScheduledFaultActive,
                state.InterruptedAutomaticRun);
            _activeSequenceId = state.ActiveSequenceId;
            _controlOwner = state.ControlOwner;
            _automaticRunRuntime.ApplyRecoveryState(state);
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
            _conditionScenarioRuntime.Profile?.FaultRecovery,
            restartSequence,
            commandId,
            new SimulationConditionScheduledFaultRecoveryState(
                _conditionScenarioRuntime.ScheduledFaultActive,
                _conditionScenarioRuntime.ScheduledFaultInterruptedAutomaticRun,
                _activeSequenceId,
                _controlOwner,
                _automaticRunRuntime.IsActive,
                _automaticRunRuntime.WaitingForRepeat,
                _automaticRunRuntime.RemainingDelayTicks),
            _axes,
            _signalHub,
            _machineLayout,
            _faultRuntime,
            _sequenceRuntime.SequenceExecutors,
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
        _automaticRunRuntime.CreateCycleContext(_activeSequenceId, _sequenceRuntime.SequenceExecutors);

    private void ApplyAutomaticRunCycleOutcome(
        SimulationAutomaticRunCycleOutcome outcome,
        long eventTick,
        TimeSpan eventTime)
    {
        if (outcome.State is { } state)
        {
            _activeSequenceId = state.ActiveSequenceId;
            _automaticRunRuntime.ApplyCycleState(state);
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
        if (!_automaticRunRuntime.IsActive)
        {
            return;
        }

        _conditionScenarioRuntime.CaptureAutomaticRunInterruption(_activeSequenceId);

        _automaticRunRuntime.MarkFaulted();
        Emit(
            "AutomaticRun",
            "AutomaticRunFaulted",
            detail ?? "The automatic sequence faulted.",
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private void ResetRuntime()
    {
        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _sequenceRuntime.DebugState.ClearPendingSemanticStep();
        _sequenceRuntime.DebugState.SetPause(SequenceDebugPauseReason.None, null);
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
        _faultRuntime.Clear();
        _signalHub.Reset();
        _machineLayout?.Reset();
        _pickPlaceWorkpiece?.Reset();
        _sequenceRuntime.ResetExecutors();
        _automaticRunRuntime.Reset();
        _conditionScenarioRuntime.Reset();
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;
    }

    private SimulationSnapshot CreateSnapshot() =>
        SimulationSnapshotFactory.Create(
            new SimulationSnapshotFactoryContext(
                _clock.Time,
                _tickIndex,
                _runMode,
                _controlOwner,
                _timeScale,
                _axes,
                _signalHub,
                _sequenceRuntime.SequenceExecutors,
                _cameras,
                new AutomaticRunSnapshot(
                    _automaticRunRuntime.Configuration is not null,
                    _automaticRunRuntime.IsActive,
                    _automaticRunRuntime.WaitingForRepeat,
                    _automaticRunRuntime.CompletedCycleCount,
                    _automaticRunRuntime.RemainingDelayTicks),
                _machineLayout,
                _faultRuntime.Values,
                _conditionScenarioRuntime.CreateSnapshot(),
                _pickPlaceWorkpiece,
                _sequenceRuntime.DebugState.CreateSnapshot()));

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

    private void PublishSnapshot()
    {
        var snapshot = CreateSnapshot();
        _snapshotStore.Publish(snapshot);
    }

    private void EmitSequenceRuntimeEvent(
        string category,
        string code,
        string message,
        long tickIndex,
        TimeSpan simulationTime) =>
        Emit(category, code, message, tickIndex: tickIndex, simulationTime: simulationTime);

    private void EmitPhysicalRuntimeEvent(
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
        _eventPublisher.TryPublish(
            tickIndex ?? _tickIndex,
            simulationTime ?? _clock.Time,
            category,
            code,
            message,
            commandId);
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

    private void InjectFault(SimulationEngineFaultPoint faultPoint) =>
        _faultInjector?.Invoke(faultPoint);

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

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lifecycle.Dispose();
        _disposed = true;
    }

}
