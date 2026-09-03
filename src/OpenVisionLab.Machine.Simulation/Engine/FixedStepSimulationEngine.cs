using System.Globalization;
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
    private readonly List<DeterministicVirtualCamera> _cameras = new();
    private readonly Dictionary<string, DeterministicSequenceExecutor> _sequenceExecutors =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompiledSequence> _compiledSequences =
        new(StringComparer.Ordinal);
    private readonly Dictionary<SimulationFaultKey, SimulationFaultSnapshot> _activeFaults = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _lifecycleLock = new();
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
    private bool _disposed;

    public FixedStepSimulationEngine(SimulationSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (settings.FixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "FixedStep must be positive.");
        }

        _clock = new SimulationClock(settings.FixedStep);
        _commandChannel = Channel.CreateUnbounded<SimulationCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _eventChannel = Channel.CreateUnbounded<SimulationEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _snapshotStore = new LatestSnapshotStore();
        _signalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
        _latestSnapshot = CreateSnapshot();
    }

    public SimulationSnapshot CurrentSnapshot => Volatile.Read(ref _latestSnapshot);
    public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotStore.Reader;
    public ChannelReader<SimulationEvent> EventReader => _eventChannel.Reader;

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

            _lifecycleState = EngineLifecycleState.Running;
            _runTask = Task.Run(() => RunLoop(_stopCts.Token));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? runTask;
        lock (_lifecycleLock)
        {
            if (_lifecycleState is EngineLifecycleState.Created or EngineLifecycleState.Running)
            {
                _lifecycleState = EngineLifecycleState.Stopping;
                _commandChannel.Writer.TryComplete();
                _stopCts.Cancel();
            }

            runTask = _runTask;
            if (runTask is null)
            {
                _snapshotStore.Complete();
                _eventChannel.Writer.TryComplete();
                _lifecycleState = EngineLifecycleState.Stopped;
            }
        }

        if (runTask is not null)
        {
            await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
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
                _ => SimulationCommandErrorCode.EngineStopped
            };
        }

        if (lifecycleError.HasValue)
        {
            return CompleteLifecycleRejection(command, lifecycleError.Value);
        }

        if (!_commandChannel.Writer.TryWrite(command))
        {
            return CompleteLifecycleRejection(command, SimulationCommandErrorCode.EngineStopped);
        }

        return await command.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timing = new SimulationRunLoopTiming(_settings.FixedStep, _settings.MaxCatchUpTicks);
        timing.Reset(stopwatch.Elapsed);

        try
        {
            PublishSnapshot();
            while (!cancellationToken.IsCancellationRequested)
            {
                var wasPaused = _runMode == SimulationRunMode.Paused;
                var commandResults = new List<(SimulationCommand Command, SimulationCommandResult Result)>();
                while (_commandChannel.Reader.TryRead(out var command))
                {
                    commandResults.Add((command, ApplyCommand(command)));
                }

                if (wasPaused && _runMode != SimulationRunMode.Paused)
                {
                    timing.Reset(stopwatch.Elapsed);
                }

                if (_runMode == SimulationRunMode.Paused)
                {
                    if (commandResults.Count > 0)
                    {
                        PublishSnapshot();
                    }

                    CompleteCommands(commandResults);
                    timing.Reset(stopwatch.Elapsed);
                    await _commandChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var ticksToRun = 0;
                if (_runMode == SimulationRunMode.SingleStep)
                {
                    ticksToRun = Math.Max(1, _pendingSteps);
                    _pendingSteps = 0;
                    _runMode = SimulationRunMode.Paused;
                    timing.AlignToWallTime(stopwatch.Elapsed);
                }
                else if (_runMode == SimulationRunMode.FastForward)
                {
                    ticksToRun = _settings.MaxCatchUpTicks;
                }
                else
                {
                    ticksToRun = timing.CalculateRealTimeTicks(stopwatch.Elapsed, _settings.TimeScale);
                }

                for (var index = 0; index < ticksToRun; index++)
                {
                    Tick();
                }

                if (ticksToRun == 0 && commandResults.Count > 0)
                {
                    PublishSnapshot();
                }
                CompleteCommands(commandResults);

                if (_runMode == SimulationRunMode.RealTime && ticksToRun == 0)
                {
                    var delay = timing.CalculateRealTimeDelay(_settings.TimeScale);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (_lifecycleState == EngineLifecycleState.Running)
                {
                    _lifecycleState = EngineLifecycleState.Stopping;
                }
                _commandChannel.Writer.TryComplete();
            }

            while (_commandChannel.Reader.TryRead(out var command))
            {
                command.TryComplete(SimulationCommandResult.Rejected(
                    command,
                    _tickIndex,
                    _clock.Time,
                    SimulationCommandErrorCode.EngineStopped,
                    "The simulation engine stopped before applying the command."));
            }

            _snapshotStore.Complete();
            _eventChannel.Writer.TryComplete();
            lock (_lifecycleLock)
            {
                _lifecycleState = EngineLifecycleState.Stopped;
            }
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
                _pendingSteps = 0;
                _runMode = SimulationRunMode.RealTime;
                _controlOwner = _sequenceExecutors.Count > 0
                    ? SimulationControlOwner.EmbeddedSequence
                    : SimulationControlOwner.Manual;
                result = Accept(command, "Simulation entered RealTime mode.");
                break;

            case PauseCommand:
                _pendingSteps = 0;
                _runMode = SimulationRunMode.Paused;
                result = Accept(command, "Simulation paused.");
                break;

            case StepCommand:
                if (_runMode is SimulationRunMode.RealTime or SimulationRunMode.FastForward)
                {
                    result = Reject(
                        command,
                        SimulationCommandErrorCode.InvalidRunMode,
                        "Single-step is available only while paused.");
                    break;
                }

                _pendingSteps++;
                _runMode = SimulationRunMode.SingleStep;
                result = Accept(command, "One fixed tick was scheduled.");
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

            case SetVirtualInputCommand setInput:
                result = ApplyVirtualInput(command, setInput);
                break;

            case SetVirtualInputForceCommand setInputForce:
                result = ApplyVirtualInputForce(command, setInputForce);
                break;

            case SetDigitalSensorForceCommand setSensorForce:
                result = ApplyDigitalSensorForce(command, setSensorForce);
                break;

            case InjectSimulationFaultCommand injectFault:
                result = ApplyInjectFault(command, injectFault);
                break;

            case ClearSimulationFaultCommand clearFault:
                result = ApplyClearFault(command, clearFault);
                break;

            case StartConditionScenarioCommand startConditionScenario:
                result = ApplyStartConditionScenario(command, startConditionScenario.Profile);
                break;

            case StopConditionScenarioCommand:
                result = ApplyStopConditionScenario(command);
                break;

            case StartSequenceCommand startSequence:
                result = ApplyStartSequence(command, startSequence);
                break;

            case StartAutomaticRunCommand startAutomaticRun:
                result = ApplyStartAutomaticRun(startAutomaticRun);
                break;

            case StartManualControlCommand:
                result = ApplyStartManualControl(command);
                break;

            case TriggerVirtualCameraCommand triggerCamera:
                result = ApplyManualCameraTrigger(command, triggerCamera);
                break;

            case MoveAbsoluteCommand move:
                result = ApplyManualMove(command, move);
                break;

            case MoveAxesAbsoluteCommand move:
                result = ApplyManualGroupMove(command, move);
                break;

            case MoveRelativeCommand move:
                result = ApplyManualRelativeMove(command, move);
                break;

            case MoveVelocityCommand move:
                result = ApplyManualVelocityMove(command, move);
                break;

            case HomeAxisCommand home:
                result = ApplyManualHome(command, home);
                break;

            case JogAxisCommand jog:
                result = ApplyManualJog(command, jog);
                break;

            case StopAxisCommand stop:
                result = ApplyManualStop(command, stop);
                break;

            case StopAxesCommand stop:
                result = ApplyManualGroupStop(command, stop);
                break;

            case SetCylinderCommand setCylinder:
                result = ApplyManualCylinderCommand(command, setCylinder);
                break;

            case SetConveyorCommand setConveyor:
                result = ApplyManualConveyorCommand(command, setConveyor);
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
        return result;
    }

    private SimulationCommandResult ApplyStartConditionScenario(
        SimulationCommand command,
        DeterministicConditionScenarioProfile profile)
    {
        if (_conditionScenarioActive)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ConditionScenarioAlreadyActive,
                "A condition scenario is already active.");
        }

        var normalized = DeterministicConditionScenarioProfile.Normalize(profile);
        var validationErrors = DeterministicConditionScenarioProfile.Validate(normalized);
        if (validationErrors.Count > 0)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ConditionScenarioInvalid,
                string.Join(" ", validationErrors));
        }

        bool targetExists = _axes.Any(axis =>
                string.Equals(axis.Id, normalized.TargetId, StringComparison.Ordinal))
            || _machineLayout?.CaptureSnapshots().Any(component =>
                string.Equals(component.Id, normalized.TargetId, StringComparison.Ordinal)) == true;
        if (!targetExists)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
                $"Condition target '{normalized.TargetId}' was not found in the active runtime.");
        }

        var faultRecovery = normalized.FaultRecovery;
        if (faultRecovery is not null)
        {
            var targets = new SimulationFaultTargetCatalog().GetTargets(
                CreateSnapshot(),
                faultRecovery.FaultKind);
            if (!targets.Any(target => string.Equals(
                    target.Id,
                    faultRecovery.TargetId,
                    StringComparison.Ordinal)))
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
                    $"Condition fault target '{faultRecovery.TargetId}' was not found for " +
                    $"'{faultRecovery.FaultKind}' in the active runtime.");
            }

            if (faultRecovery.RestartSequenceId is not null
                && !_sequenceExecutors.ContainsKey(faultRecovery.RestartSequenceId))
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
                    $"Condition recovery sequence '{faultRecovery.RestartSequenceId}' was not found in the active runtime.");
            }

            var faultKey = new SimulationFaultKey(
                faultRecovery.FaultKind,
                faultRecovery.TargetId);
            if (_activeFaults.ContainsKey(faultKey))
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.ConditionScenarioInvalid,
                    $"{faultRecovery.FaultKind} is already active for '{faultRecovery.TargetId}'.");
            }
        }

        _conditionScenarioProfile = normalized;
        _conditionStateMachine = new DeterministicConditionStateMachine(normalized);
        _conditionScenarioExecutedTicks = 0;
        _conditionLastTransition = null;
        _conditionScheduledFaultActive = false;
        _conditionScheduledFaultInterruptedAutomaticRun = false;
        _conditionScenarioActive = normalized.DurationTicks > 0;
        EmitAtCommandBoundary(
            "Condition",
            "ConditionScenarioStarted",
            $"Condition scenario '{normalized.ScenarioId}' started for '{normalized.TargetId}' " +
            $"with seed {normalized.Seed}.",
            command.CommandId);
        if (!_conditionScenarioActive)
        {
            EmitAtCommandBoundary(
                "Condition",
                "ConditionScenarioCompleted",
                $"Condition scenario '{normalized.ScenarioId}' completed after 0 ticks.",
                command.CommandId);
        }

        return Accept(command, $"Condition scenario '{normalized.ScenarioId}' started.");
    }

    private SimulationCommandResult ApplyStopConditionScenario(SimulationCommand command)
    {
        if (!_conditionScenarioActive || _conditionScenarioProfile is null)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ConditionScenarioNotActive,
                "No condition scenario is active.");
        }

        ClearConditionScheduledFault(
            _commandBoundaryTick,
            _commandBoundaryTime,
            restartSequence: false,
            command.CommandId);
        _conditionScenarioActive = false;
        _conditionLastTransition = null;
        EmitAtCommandBoundary(
            "Condition",
            "ConditionScenarioStopped",
            $"Condition scenario '{_conditionScenarioProfile.ScenarioId}' stopped after " +
            $"{_conditionScenarioExecutedTicks} ticks.",
            command.CommandId);
        return Accept(command, $"Condition scenario '{_conditionScenarioProfile.ScenarioId}' stopped.");
    }

    private SimulationCommandResult ApplyRuntimeConfiguration(
        SimulationCommand command,
        SimulationRuntimeConfiguration configuration)
    {
        if (!TryCreateAxes(configuration.Axes, out var axes, out var axisError))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, axisError);
        }

        if (!TryCreateCameras(configuration.Cameras, out var cameras, out var cameraError))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, cameraError);
        }

        var hubResult = DeterministicSignalHub.Create(configuration.Channels);
        if (!hubResult.IsAccepted || hubResult.Hub is null)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.RuntimeConfigurationInvalid,
                $"Signal configuration failed: {hubResult.ErrorCode} ({hubResult.ChannelId ?? "n/a"}).");
        }

        var compiled = new Dictionary<string, CompiledSequence>(StringComparer.Ordinal);
        var executors = new Dictionary<string, DeterministicSequenceExecutor>(StringComparer.Ordinal);
        foreach (var sequence in configuration.Sequences)
        {
            if (sequence is null || string.IsNullOrWhiteSpace(sequence.Id))
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.RuntimeConfigurationInvalid,
                    "Every compiled sequence requires an id.");
            }

            if (!compiled.TryAdd(sequence.Id, sequence))
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.RuntimeConfigurationInvalid,
                    $"Sequence id '{sequence.Id}' is duplicated.");
            }
            executors.Add(sequence.Id, new DeterministicSequenceExecutor(sequence));
        }

        if (!TryValidateAutomaticRun(
                configuration.AutomaticRun,
                compiled,
                hubResult.Hub,
                out var repeatDelayTicks,
                out var automaticRunError))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.RuntimeConfigurationInvalid,
                automaticRunError);
        }

        if (!TryCreateMachineLayout(
                configuration.Layout,
                axes,
                cameras,
                hubResult.Hub,
                out var machineLayout,
                out var layoutError))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.RuntimeConfigurationInvalid,
                layoutError);
        }

        if (!TryCreatePickPlaceWorkpiece(
                configuration.PickPlaceWorkpiece,
                axes,
                hubResult.Hub,
                out var pickPlaceWorkpiece,
                out var workpieceError))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.RuntimeConfigurationInvalid,
                workpieceError);
        }

        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _clock.Reset();
        _tickIndex = 0;
        _axes.Clear();
        _axes.AddRange(axes);
        _cameras.Clear();
        _cameras.AddRange(cameras);
        _signalHub = hubResult.Hub;
        _machineLayout = machineLayout;
        _pickPlaceWorkpiece = pickPlaceWorkpiece;
        _activeFaults.Clear();
        ClearConditionScenarioState();
        _compiledSequences.Clear();
        foreach (var pair in compiled)
        {
            _compiledSequences.Add(pair.Key, pair.Value);
        }
        _sequenceExecutors.Clear();
        foreach (var pair in executors)
        {
            _sequenceExecutors.Add(pair.Key, pair.Value);
        }
        _automaticRunConfiguration = configuration.AutomaticRun;
        _automaticRunRepeatDelayTicks = repeatDelayTicks;
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
        if (!TryCreateAxes(configurations, out var axes, out var error))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, error);
        }

        var emptySignalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;

        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
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

    private SimulationCommandResult ApplyVirtualInput(
        SimulationCommand command,
        SetVirtualInputCommand setInput)
    {
        var write = _signalHub.SetDigitalInput(
            setInput.ChannelId,
            setInput.Value,
            SignalWriteOwner.Manual);
        if (!write.IsAccepted)
        {
            return Reject(
                command,
                write.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Input '{setInput.ChannelId}' write failed: {write.ErrorCode}.");
        }

        if (write.StateChanged)
        {
            Emit(
                "I/O",
                "DigitalInputChanged",
                $"{setInput.ChannelId} = {FormatSignal(setInput.Value)}.",
                command.CommandId);
        }
        return Accept(command, $"Input '{setInput.ChannelId}' set to {FormatSignal(setInput.Value)}.");
    }

    private SimulationCommandResult ApplyVirtualInputForce(
        SimulationCommand command,
        SetVirtualInputForceCommand setInputForce)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual input forcing is unavailable while owner is {_controlOwner}.");
        }

        if (_activeFaults.ContainsKey(
                new SimulationFaultKey(
                    SimulationFaultKind.StuckDigitalInput,
                    setInputForce.ChannelId)))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.SignalWriteRejected,
                $"Input '{setInputForce.ChannelId}' has an active stuck-input fault.");
        }

        DigitalInputOverrideResult inputOverride = _signalHub.SetDigitalInputOverride(
            setInputForce.ChannelId,
            setInputForce.ForcedValue);
        if (!inputOverride.IsAccepted)
        {
            return Reject(
                command,
                inputOverride.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Input '{setInputForce.ChannelId}' force failed: {inputOverride.ErrorCode}.");
        }

        string code = setInputForce.ForcedValue switch
        {
            true => "DigitalInputForceOnAccepted",
            false => "DigitalInputForceOffAccepted",
            null => "DigitalInputForceCleared"
        };
        string action = setInputForce.ForcedValue.HasValue
            ? $"forced {FormatSignal(setInputForce.ForcedValue.Value)}"
            : "force cleared";
        EmitAtCommandBoundary(
            "I/O",
            code,
            $"{setInputForce.ChannelId} {action}; effective = " +
            $"{FormatSignal(inputOverride.CurrentValue ?? false)}.",
            command.CommandId);
        return Accept(command, $"Input '{setInputForce.ChannelId}' {action}.");
    }

    private SimulationCommandResult ApplyDigitalSensorForce(
        SimulationCommand command,
        SetDigitalSensorForceCommand setSensorForce)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual sensor forcing is unavailable while owner is {_controlOwner}.");
        }

        if (_machineLayout is null
            || !_machineLayout.TryGetDigitalSensorOutputChannelId(
                setSensorForce.SensorId,
                out string? inputChannelId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.DigitalSensorNotFound,
                $"Digital sensor '{setSensorForce.SensorId}' was not found.");
        }

        if (_activeFaults.ContainsKey(
                new SimulationFaultKey(SimulationFaultKind.StuckDigitalInput, inputChannelId!)))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.DigitalSensorInterlocked,
                $"Digital sensor '{setSensorForce.SensorId}' has an active stuck-input fault.");
        }

        DigitalInputOverrideResult inputOverride = _signalHub.SetDigitalInputOverride(
            inputChannelId,
            setSensorForce.ForcedValue);
        if (!inputOverride.IsAccepted)
        {
            return Reject(
                command,
                inputOverride.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Digital sensor input '{inputChannelId}' force failed: {inputOverride.ErrorCode}.");
        }

        string code = setSensorForce.ForcedValue switch
        {
            true => "DigitalSensorForceOnAccepted",
            false => "DigitalSensorForceOffAccepted",
            null => "DigitalSensorForceCleared"
        };
        string action = setSensorForce.ForcedValue.HasValue
            ? $"forced {FormatSignal(setSensorForce.ForcedValue.Value)}"
            : "force cleared";
        EmitAtCommandBoundary(
            "Sensor",
            code,
            $"{setSensorForce.SensorId} {action}; {inputChannelId} effective = " +
            $"{FormatSignal(inputOverride.CurrentValue ?? false)}.",
            command.CommandId);
        return Accept(command, $"Digital sensor '{setSensorForce.SensorId}' {action}.");
    }

    private SimulationCommandResult ApplyInjectFault(
        SimulationCommand command,
        InjectSimulationFaultCommand injectFault)
    {
        if (string.IsNullOrWhiteSpace(injectFault.TargetId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.FaultParameterInvalid,
                "A fault target id is required.");
        }

        var key = new SimulationFaultKey(injectFault.Kind, injectFault.TargetId);
        if (_activeFaults.ContainsKey(key))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.FaultAlreadyActive,
                $"Fault '{injectFault.Kind}' is already active for '{injectFault.TargetId}'.");
        }

        switch (injectFault.Kind)
        {
            case SimulationFaultKind.StuckDigitalInput:
                if (!injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "StuckDigitalInput requires a forced Boolean value.");
                }

                if (_signalHub.CaptureSnapshot().TryGetSignal(
                        injectFault.TargetId,
                        out DigitalSignalSnapshot? inputSignal)
                    && inputSignal?.OverrideValue.HasValue == true)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultApplicationRejected,
                        $"Digital-input target '{injectFault.TargetId}' already has a manual force.");
                }

                DigitalInputOverrideResult inputOverride = _signalHub.SetDigitalInputOverride(
                    injectFault.TargetId,
                    injectFault.ForcedValue.Value);
                if (!inputOverride.IsAccepted)
                {
                    return Reject(
                        command,
                        inputOverride.ErrorCode is SignalHubErrorCode.ChannelNotFound
                            or SignalHubErrorCode.ChannelKindMismatch
                            ? SimulationCommandErrorCode.FaultTargetNotFound
                            : SimulationCommandErrorCode.FaultApplicationRejected,
                        $"Digital-input fault target '{injectFault.TargetId}' is unavailable: " +
                        $"{inputOverride.ErrorCode}.");
                }
                break;

            case SimulationFaultKind.CylinderTravelBlocked:
                if (injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "CylinderTravelBlocked does not accept a forced Boolean value.");
                }

                if (_machineLayout is null || !_machineLayout.ContainsCylinder(injectFault.TargetId))
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultTargetNotFound,
                        $"Cylinder fault target '{injectFault.TargetId}' was not found.");
                }
                break;

            case SimulationFaultKind.AxisMotionBlocked:
                if (injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "AxisMotionBlocked does not accept a forced Boolean value.");
                }

                var blockedAxis = _axes.FirstOrDefault(axis =>
                    string.Equals(axis.Id, injectFault.TargetId, StringComparison.Ordinal));
                if (blockedAxis is null)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultTargetNotFound,
                        $"Axis fault target '{injectFault.TargetId}' was not found.");
                }

                blockedAxis.SetMotionBlocked(true);
                break;

            case SimulationFaultKind.AxisFollowingError:
                if (injectFault.ForcedValue.HasValue)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultParameterInvalid,
                        "AxisFollowingError does not accept a forced Boolean value.");
                }

                var followingErrorAxis = _axes.FirstOrDefault(axis =>
                    string.Equals(axis.Id, injectFault.TargetId, StringComparison.Ordinal));
                if (followingErrorAxis is null)
                {
                    return Reject(
                        command,
                        SimulationCommandErrorCode.FaultTargetNotFound,
                        $"Axis fault target '{injectFault.TargetId}' was not found.");
                }

                followingErrorAxis.SetFollowingErrorInjected(true);
                break;

            default:
                return Reject(
                    command,
                    SimulationCommandErrorCode.FaultParameterInvalid,
                    $"Fault kind '{injectFault.Kind}' is unsupported.");
        }

        var snapshot = new SimulationFaultSnapshot(
            injectFault.Kind,
            injectFault.TargetId,
            injectFault.ForcedValue,
            _commandBoundaryTick,
            _commandBoundaryTime);
        _activeFaults.Add(key, snapshot);
        EmitAtCommandBoundary(
            "Fault",
            "FaultInjected",
            FormatFault(snapshot),
            command.CommandId);
        return Accept(command, $"Fault '{injectFault.Kind}' injected for '{injectFault.TargetId}'.");
    }

    private SimulationCommandResult ApplyClearFault(
        SimulationCommand command,
        ClearSimulationFaultCommand clearFault)
    {
        if (string.IsNullOrWhiteSpace(clearFault.TargetId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.FaultParameterInvalid,
                "A fault target id is required.");
        }

        var key = new SimulationFaultKey(clearFault.Kind, clearFault.TargetId);
        if (!_activeFaults.TryGetValue(key, out SimulationFaultSnapshot? activeFault))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.FaultNotActive,
                $"Fault '{clearFault.Kind}' is not active for '{clearFault.TargetId}'.");
        }

        if (clearFault.Kind == SimulationFaultKind.StuckDigitalInput)
        {
            DigitalInputOverrideResult inputOverride = _signalHub.SetDigitalInputOverride(
                clearFault.TargetId,
                null);
            if (!inputOverride.IsAccepted)
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.FaultApplicationRejected,
                    $"Digital-input override could not be cleared: {inputOverride.ErrorCode}.");
            }
        }
        else if (clearFault.Kind == SimulationFaultKind.AxisMotionBlocked)
        {
            var blockedAxis = _axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, clearFault.TargetId, StringComparison.Ordinal));
            if (blockedAxis is null)
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.FaultApplicationRejected,
                    $"Axis fault target '{clearFault.TargetId}' could not be recovered.");
            }

            blockedAxis.SetMotionBlocked(false);
        }
        else if (clearFault.Kind == SimulationFaultKind.AxisFollowingError)
        {
            var followingErrorAxis = _axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, clearFault.TargetId, StringComparison.Ordinal));
            if (followingErrorAxis is null)
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.FaultApplicationRejected,
                    $"Axis fault target '{clearFault.TargetId}' could not be recovered.");
            }

            var alarmWasActive = followingErrorAxis.DriveAlarmActive;
            followingErrorAxis.SetFollowingErrorInjected(false);
            if (alarmWasActive)
            {
                EmitAtCommandBoundary(
                    "Motion",
                    "AxisDriveAlarmCleared",
                    $"{followingErrorAxis.Id} drive alarm cleared; axis is stopped.",
                    command.CommandId);
            }
        }

        _activeFaults.Remove(key);
        EmitAtCommandBoundary(
            "Fault",
            "FaultCleared",
            $"Cleared {FormatFault(activeFault)}",
            command.CommandId);
        return Accept(command, $"Fault '{clearFault.Kind}' cleared for '{clearFault.TargetId}'.");
    }

    private SimulationCommandResult ApplyStartSequence(
        SimulationCommand command,
        StartSequenceCommand startSequence)
    {
        if (_automaticRunActive)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.SequenceStartRejected,
                "A configured automatic run is already active.");
        }

        if (!_sequenceExecutors.TryGetValue(startSequence.SequenceId, out var executor))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Sequence '{startSequence.SequenceId}' is not configured.");
        }

        if (_activeSequenceId is not null
            && _sequenceExecutors[_activeSequenceId].CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.SequenceStartRejected,
                $"Sequence '{_activeSequenceId}' is already running.");
        }

        if (executor.CaptureSnapshot().Status == SequenceExecutionStatus.Faulted)
        {
            executor.Reset();
        }

        var start = executor.Start();
        if (!start.IsSuccess)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.SequenceStartRejected,
                start.Error?.Message ?? "Sequence start was rejected.");
        }

        _activeSequenceId = startSequence.SequenceId;
        _controlOwner = SimulationControlOwner.EmbeddedSequence;
        Emit(
            "Sequence",
            "SequenceStarted",
            $"{startSequence.SequenceId} entered {start.CurrentStepId}.",
            command.CommandId);
        return Accept(command, $"Sequence '{startSequence.SequenceId}' started.");
    }

    private SimulationCommandResult ApplyStartAutomaticRun(StartAutomaticRunCommand command)
    {
        var configuration = _automaticRunConfiguration;
        if (configuration is null)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunNotConfigured,
                "Automatic run is not configured.");
        }

        if (_runMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.InvalidRunMode,
                "Automatic run can start only while the simulation is paused.");
        }

        if (_automaticRunActive)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                "Automatic run is already active.");
        }

        if (_activeSequenceId is not null
            && _sequenceExecutors[_activeSequenceId].CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                $"Sequence '{_activeSequenceId}' is already running.");
        }

        if (!_sequenceExecutors.TryGetValue(configuration.SequenceId, out var executor))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                $"Automatic sequence '{configuration.SequenceId}' is unavailable.");
        }

        if (executor.CaptureSnapshot().Status != SequenceExecutionStatus.Ready)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                $"Automatic sequence '{configuration.SequenceId}' is not Ready; reset is required.");
        }

        SignalWriteResult? inputWrite = null;
        if (configuration.StartInputId is not null)
        {
            inputWrite = _signalHub.SetDigitalInput(
                configuration.StartInputId,
                configuration.StartInputValue,
                SignalWriteOwner.Manual);
            if (!inputWrite.IsAccepted)
            {
                return Reject(
                    command,
                    SimulationCommandErrorCode.AutomaticRunStartRejected,
                    $"Automatic start input '{configuration.StartInputId}' failed: {inputWrite.ErrorCode}.");
            }
        }

        var start = executor.Start();
        if (!start.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Prevalidated automatic sequence '{configuration.SequenceId}' could not start.");
        }

        _activeSequenceId = configuration.SequenceId;
        _automaticRunActive = true;
        _automaticRunWaitingForRepeat = false;
        _automaticRunCompletedCycleCount = 0;
        _automaticRunRemainingDelayTicks = 0;
        _pendingSteps = 0;
        _runMode = command.BeginRealTime
            ? SimulationRunMode.RealTime
            : SimulationRunMode.Paused;
        _controlOwner = SimulationControlOwner.EmbeddedSequence;

        if (inputWrite is { StateChanged: true })
        {
            EmitAtCommandBoundary(
                "I/O",
                "DigitalInputChanged",
                $"{configuration.StartInputId} = {FormatSignal(configuration.StartInputValue)}.",
                command.CommandId);
        }
        EmitAtCommandBoundary(
            "Sequence",
            "SequenceStarted",
            $"{configuration.SequenceId} entered {start.CurrentStepId}.",
            command.CommandId);
        EmitAtCommandBoundary(
            "AutomaticRun",
            "AutomaticRunStarted",
            $"Automatic sequence '{configuration.SequenceId}' started.",
            command.CommandId);
        return Accept(command, $"Automatic sequence '{configuration.SequenceId}' started.");
    }

    private SimulationCommandResult ApplyManualMove(
        SimulationCommand command,
        MoveAbsoluteCommand move)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {_controlOwner}.");
        }

        var axis = _axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, move.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return Reject(command, SimulationCommandErrorCode.AxisNotFound, $"Axis '{move.AxisId}' was not found.");
        }

        var moveResult = axis.MoveAbsolute(move.TargetPosition);
        if (!moveResult.IsAccepted)
        {
            return Reject(command, MapAxisError(moveResult.ErrorCode), $"Axis move rejected: {moveResult.ErrorCode}.");
        }

        Emit(
            "Motion",
            "AxisMoveAccepted",
            $"{move.AxisId} target = {move.TargetPosition:F3}.",
            command.CommandId);
        return Accept(command, $"Axis '{move.AxisId}' move accepted.");
    }

    private SimulationCommandResult ApplyManualRelativeMove(
        SimulationCommand command,
        MoveRelativeCommand move)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {_controlOwner}.");
        }

        var axis = _axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, move.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return Reject(command, SimulationCommandErrorCode.AxisNotFound, $"Axis '{move.AxisId}' was not found.");
        }

        var moveResult = axis.MoveRelative(move.Distance);
        if (!moveResult.IsAccepted)
        {
            return Reject(
                command,
                MapAxisError(moveResult.ErrorCode),
                $"Axis relative move rejected: {moveResult.ErrorCode}.");
        }

        Emit(
            "Motion",
            "AxisRelativeMoveAccepted",
            $"{move.AxisId} distance = {move.Distance:F3}, target = {moveResult.RequestedTarget:F3}.",
            command.CommandId);
        return Accept(command, $"Axis '{move.AxisId}' relative move accepted.");
    }

    private SimulationCommandResult ApplyManualGroupMove(
        SimulationCommand command,
        MoveAxesAbsoluteCommand move)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {_controlOwner}.");
        }

        if (!TryResolveDistinctAxes(move.Targets.Select(target => target.AxisId), out var axes, out var error))
        {
            return Reject(command, error.ErrorCode, error.Detail);
        }

        for (var index = 0; index < axes.Count; index++)
        {
            var validation = axes[index].ValidateAbsoluteMove(move.Targets[index].TargetPosition);
            if (!validation.IsAccepted)
            {
                return Reject(
                    command,
                    MapAxisError(validation.ErrorCode),
                    $"Axis '{axes[index].Id}' group move rejected: {validation.ErrorCode}.");
            }
        }

        for (var index = 0; index < axes.Count; index++)
        {
            axes[index].MoveAbsolute(move.Targets[index].TargetPosition);
        }

        var targets = string.Join(
            ", ",
            move.Targets.Select(target => FormattableString.Invariant(
                $"{target.AxisId} = {target.TargetPosition:F3}")));
        EmitAtCommandBoundary(
            "Motion",
            "AxisGroupMoveAccepted",
            $"Targets: {targets}.",
            command.CommandId);
        return Accept(command, $"Coordinated move for {axes.Count} axes accepted.");
    }

    private SimulationCommandResult ApplyManualVelocityMove(
        SimulationCommand command,
        MoveVelocityCommand move)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {_controlOwner}.");
        }

        var axis = _axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, move.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return Reject(command, SimulationCommandErrorCode.AxisNotFound, $"Axis '{move.AxisId}' was not found.");
        }

        var moveResult = axis.MoveVelocity(move.Velocity);
        if (!moveResult.IsAccepted)
        {
            return Reject(
                command,
                MapAxisError(moveResult.ErrorCode),
                $"Axis velocity move rejected: {moveResult.ErrorCode}.");
        }

        Emit(
            "Motion",
            "AxisVelocityMoveAccepted",
            $"{move.AxisId} velocity = {move.Velocity:F3}, limit = {moveResult.RequestedTarget:F3}.",
            command.CommandId);
        return Accept(command, $"Axis '{move.AxisId}' velocity move accepted.");
    }

    private SimulationCommandResult ApplyStartManualControl(SimulationCommand command)
    {
        if (_runMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.InvalidRunMode,
                "Manual control can start only while the simulation is paused.");
        }

        if (_automaticRunActive || _sequenceExecutors.Values.Any(executor =>
                executor.CaptureSnapshot().Status == SequenceExecutionStatus.Running))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                "Reset the active automatic or sequence run before starting manual control.");
        }

        _pendingSteps = 0;
        _runMode = SimulationRunMode.RealTime;
        _controlOwner = SimulationControlOwner.Manual;
        EmitAtCommandBoundary(
            "Motion",
            "ManualControlStarted",
            "Manual commissioning control entered RealTime mode.",
            command.CommandId);
        return Accept(command, "Manual commissioning control started.");
    }

    private SimulationCommandResult ApplyManualCameraTrigger(
        SimulationCommand command,
        TriggerVirtualCameraCommand triggerCamera)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual camera acquisition is unavailable while owner is {_controlOwner}.");
        }

        if (_runMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.InvalidRunMode,
                "Manual camera acquisition can be triggered only while paused.");
        }

        var camera = _cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, triggerCamera.CameraId, StringComparison.Ordinal));
        if (camera is null)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.CameraNotFound,
                $"Virtual camera '{triggerCamera.CameraId}' was not found.");
        }

        var trigger = camera.Trigger(
            triggerCamera.RecipeId,
            triggerCamera.FrameEvidence,
            triggerCamera.InspectionEvidence);
        if (!trigger.IsAccepted || string.IsNullOrWhiteSpace(trigger.AcquisitionId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.CameraTriggerRejected,
                $"Virtual camera '{triggerCamera.CameraId}' trigger failed: {trigger.ErrorCode}.");
        }

        EmitAtCommandBoundary(
            "Camera",
            "CameraTriggered",
            $"{triggerCamera.CameraId} started {trigger.AcquisitionId} for recipe " +
            $"'{triggerCamera.RecipeId}' with frame SHA-256 " +
            $"{triggerCamera.FrameEvidence.ContentSha256}" +
            (triggerCamera.InspectionEvidence is null
                ? "."
                : $" and inspection {triggerCamera.InspectionEvidence.InspectionId}."),
            command.CommandId);
        return Accept(
            command,
            $"Virtual camera '{triggerCamera.CameraId}' started {trigger.AcquisitionId}.");
    }

    private SimulationCommandResult ApplyManualHome(
        SimulationCommand command,
        HomeAxisCommand home)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis homing is unavailable while owner is {_controlOwner}.");
        }

        var axis = _axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, home.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return Reject(command, SimulationCommandErrorCode.AxisNotFound, $"Axis '{home.AxisId}' was not found.");
        }

        var homeResult = axis.MoveAbsolute(axis.HomePosition);
        if (!homeResult.IsAccepted)
        {
            return Reject(command, MapAxisError(homeResult.ErrorCode), $"Axis home rejected: {homeResult.ErrorCode}.");
        }

        Emit(
            "Motion",
            "AxisHomeAccepted",
            $"{home.AxisId} home = {axis.HomePosition:F3}.",
            command.CommandId);
        return Accept(command, $"Axis '{home.AxisId}' home accepted.");
    }

    private SimulationCommandResult ApplyManualJog(
        SimulationCommand command,
        JogAxisCommand jog)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis jog is unavailable while owner is {_controlOwner}.");
        }

        if (!Enum.IsDefined(jog.Direction))
        {
            return Reject(command, SimulationCommandErrorCode.AxisTargetInvalid, "Axis jog direction is invalid.");
        }

        var axis = _axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, jog.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return Reject(command, SimulationCommandErrorCode.AxisNotFound, $"Axis '{jog.AxisId}' was not found.");
        }

        var positive = jog.Direction == AxisJogDirection.Positive;
        var jogResult = axis.Jog(positive);
        if (!jogResult.IsAccepted)
        {
            return Reject(command, MapAxisError(jogResult.ErrorCode), $"Axis jog rejected: {jogResult.ErrorCode}.");
        }

        Emit(
            "Motion",
            "AxisJogAccepted",
            $"{jog.AxisId} jog {jog.Direction} toward {jogResult.RequestedTarget:F3}.",
            command.CommandId);
        return Accept(command, $"Axis '{jog.AxisId}' jog {jog.Direction} accepted.");
    }

    private SimulationCommandResult ApplyManualStop(
        SimulationCommand command,
        StopAxisCommand stop)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis stop is unavailable while owner is {_controlOwner}.");
        }

        var axis = _axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, stop.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return Reject(command, SimulationCommandErrorCode.AxisNotFound, $"Axis '{stop.AxisId}' was not found.");
        }

        axis.Stop();
        Emit(
            "Motion",
            "AxisStopAccepted",
            $"{stop.AxisId} stopped at {axis.Position:F3}.",
            command.CommandId);
        return Accept(command, $"Axis '{stop.AxisId}' stopped.");
    }

    private SimulationCommandResult ApplyManualGroupStop(
        SimulationCommand command,
        StopAxesCommand stop)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis stop is unavailable while owner is {_controlOwner}.");
        }

        if (!TryResolveDistinctAxes(stop.AxisIds, out var axes, out var error))
        {
            return Reject(command, error.ErrorCode, error.Detail);
        }

        foreach (var axis in axes)
        {
            axis.Stop();
        }

        var positions = string.Join(
            ", ",
            axes.Select(axis => FormattableString.Invariant($"{axis.Id} = {axis.Position:F3}")));
        EmitAtCommandBoundary(
            "Motion",
            "AxisGroupStopAccepted",
            $"Stopped: {positions}.",
            command.CommandId);
        return Accept(command, $"Coordinated stop for {axes.Count} axes accepted.");
    }

    private SimulationCommandResult ApplyManualCylinderCommand(
        SimulationCommand command,
        SetCylinderCommand setCylinder)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual cylinder control is unavailable while owner is {_controlOwner}.");
        }

        if (_machineLayout is null
            || !_machineLayout.TryGetCylinderCommandChannelId(
                setCylinder.CylinderId,
                out string? outputChannelId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.CylinderNotFound,
                $"Cylinder '{setCylinder.CylinderId}' was not found.");
        }

        if (_activeFaults.Values.Any(fault =>
                fault.Kind == SimulationFaultKind.CylinderTravelBlocked
                && string.Equals(fault.TargetId, setCylinder.CylinderId, StringComparison.Ordinal)))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.CylinderInterlocked,
                $"Cylinder '{setCylinder.CylinderId}' travel is blocked by an active fault.");
        }

        SignalWriteResult write = _signalHub.SetDigitalOutput(
            outputChannelId,
            setCylinder.Extend,
            SignalWriteOwner.Manual);
        if (!write.IsAccepted)
        {
            return Reject(
                command,
                write.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Cylinder output '{outputChannelId}' write rejected: {write.ErrorCode}.");
        }

        string action = setCylinder.Extend ? "extend" : "retract";
        EmitAtCommandBoundary(
            "Cylinder",
            setCylinder.Extend ? "CylinderExtendAccepted" : "CylinderRetractAccepted",
            $"{setCylinder.CylinderId} {action} command wrote {outputChannelId} = " +
            $"{FormatSignal(setCylinder.Extend)}.",
            command.CommandId);
        return Accept(command, $"Cylinder '{setCylinder.CylinderId}' {action} command accepted.");
    }

    private SimulationCommandResult ApplyManualConveyorCommand(
        SimulationCommand command,
        SetConveyorCommand setConveyor)
    {
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual conveyor control is unavailable while owner is {_controlOwner}.");
        }

        if (!Enum.IsDefined(setConveyor.Direction))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ConveyorCommandInvalid,
                "Conveyor direction is invalid.");
        }

        if (_machineLayout is null
            || !_machineLayout.TryGetConveyorCommandChannelIds(
                setConveyor.ConveyorId,
                out string? runChannelId,
                out string? reverseChannelId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ConveyorNotFound,
                $"Conveyor '{setConveyor.ConveyorId}' was not found.");
        }

        SignalWriteResult reverseWrite = _signalHub.SetDigitalOutput(
            reverseChannelId,
            setConveyor.Direction == ConveyorDirection.Reverse,
            SignalWriteOwner.Manual);
        SignalWriteResult runWrite = _signalHub.SetDigitalOutput(
            runChannelId,
            setConveyor.Running,
            SignalWriteOwner.Manual);
        if (!reverseWrite.IsAccepted || !runWrite.IsAccepted)
        {
            SignalWriteResult rejected = !reverseWrite.IsAccepted ? reverseWrite : runWrite;
            return Reject(
                command,
                rejected.ErrorCode == SignalHubErrorCode.ChannelNotFound
                    ? SimulationCommandErrorCode.SignalNotFound
                    : SimulationCommandErrorCode.SignalWriteRejected,
                $"Conveyor output '{rejected.ChannelId}' write rejected: {rejected.ErrorCode}.");
        }

        string action = setConveyor.Running ? $"run {setConveyor.Direction}" : "stop";
        EmitAtCommandBoundary(
            "Conveyor",
            setConveyor.Running ? "ConveyorRunAccepted" : "ConveyorStopAccepted",
            $"{setConveyor.ConveyorId} {action}; {runChannelId} = " +
            $"{FormatSignal(setConveyor.Running)}, {reverseChannelId} = " +
            $"{FormatSignal(setConveyor.Direction == ConveyorDirection.Reverse)}.",
            command.CommandId);
        return Accept(command, $"Conveyor '{setConveyor.ConveyorId}' {action} command accepted.");
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
            var context = new SimulationSequenceRuntimeContext(this, eventTick, eventTime);
            var execution = executor.Tick(_settings.FixedStep, context);
            if (execution.Transitioned)
            {
                Emit(
                    "Sequence",
                    "SequenceStepTransition",
                    $"{_activeSequenceId}: {execution.PreviousStepId} -> {execution.CurrentStepId}.",
                    tickIndex: eventTick,
                    simulationTime: eventTime);
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
        _conditionStateMachine.Advance(
            scenarioTick,
            out _conditionLastTransition);
        _conditionScenarioExecutedTicks++;

        if (_conditionLastTransition is not null)
        {
            Emit(
                "Condition",
                "ConditionStateChanged",
                $"{_conditionLastTransition.TargetId}: {_conditionLastTransition.From} -> " +
                $"{_conditionLastTransition.To} at scenario tick {_conditionLastTransition.TickIndex}.",
                tickIndex: eventTick,
                simulationTime: eventTime);
        }

        if (_conditionScenarioExecutedTicks >= _conditionScenarioProfile.DurationTicks)
        {
            _conditionScenarioActive = false;
            Emit(
                "Condition",
                "ConditionScenarioCompleted",
                $"Condition scenario '{_conditionScenarioProfile.ScenarioId}' completed after " +
                $"{_conditionScenarioExecutedTicks} ticks.",
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
            var injection = new InjectSimulationFaultCommand(
                schedule.FaultKind,
                schedule.TargetId,
                schedule.ForcedValue);
            var result = ApplyInjectFault(injection, injection);
            if (!result.IsAccepted)
            {
                _conditionScenarioActive = false;
                Emit(
                    "Condition",
                    "ConditionFaultScheduleRejected",
                    $"Scheduled {schedule.FaultKind} injection for '{schedule.TargetId}' was rejected: " +
                    $"{result.ErrorCode}: {result.Detail}",
                    injection.CommandId,
                    eventTick,
                    eventTime);
                return;
            }

            _conditionScheduledFaultActive = true;
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
        var clear = new ClearSimulationFaultCommand(schedule.FaultKind, schedule.TargetId);
        var result = ApplyClearFault(clear, clear);
        if (!result.IsAccepted)
        {
            Emit(
                "Condition",
                "ConditionFaultClearRejected",
                $"Scheduled {schedule.FaultKind} clear for '{schedule.TargetId}' was rejected: " +
                $"{result.ErrorCode}: {result.Detail}",
                commandId ?? clear.CommandId,
                eventTick,
                eventTime);
            return;
        }

        _conditionScheduledFaultActive = false;
        bool resumeAutomaticRun = _conditionScheduledFaultInterruptedAutomaticRun;
        _conditionScheduledFaultInterruptedAutomaticRun = false;
        if (!restartSequence || schedule.RestartSequenceId is null)
        {
            return;
        }

        var executor = _sequenceExecutors[schedule.RestartSequenceId];
        if (executor.CaptureSnapshot().Status != SequenceExecutionStatus.Faulted)
        {
            return;
        }

        executor.Reset();
        var start = executor.Start();
        if (!start.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Prevalidated recovery sequence '{schedule.RestartSequenceId}' could not restart.");
        }

        _activeSequenceId = schedule.RestartSequenceId;
        _controlOwner = SimulationControlOwner.EmbeddedSequence;
        Emit(
            "Sequence",
            "SequenceStarted",
            $"{schedule.RestartSequenceId} entered {start.CurrentStepId}; restarted by condition scenario recovery.",
            commandId,
            eventTick,
            eventTime);
        if (resumeAutomaticRun)
        {
            _automaticRunActive = true;
            _automaticRunWaitingForRepeat = false;
            _automaticRunRemainingDelayTicks = 0;
            Emit(
                "AutomaticRun",
                "AutomaticRunRecovered",
                $"Automatic sequence '{schedule.RestartSequenceId}' resumed after scheduled fault recovery.",
                commandId,
                eventTick,
                eventTime);
        }
    }

    private void AdvanceAutomaticRunRepeat(long eventTick, TimeSpan eventTime)
    {
        if (!_automaticRunActive || !_automaticRunWaitingForRepeat)
        {
            return;
        }

        if (_automaticRunRemainingDelayTicks > 0)
        {
            _automaticRunRemainingDelayTicks--;
        }

        if (_automaticRunRemainingDelayTicks > 0)
        {
            return;
        }

        var configuration = _automaticRunConfiguration;
        if (configuration is null
            || !_sequenceExecutors.TryGetValue(configuration.SequenceId, out var executor))
        {
            FaultAutomaticRun(
                eventTick,
                eventTime,
                "The configured automatic sequence is unavailable during repeat.");
            return;
        }

        executor.Reset();
        var start = executor.Start();
        if (!start.IsSuccess)
        {
            FaultAutomaticRun(
                eventTick,
                eventTime,
                start.Error?.Message ?? "The automatic sequence could not restart.");
            return;
        }

        _activeSequenceId = configuration.SequenceId;
        _automaticRunWaitingForRepeat = false;
        Emit(
            "AutomaticRun",
            "AutomaticRunCycleRestarted",
            $"Automatic cycle {_automaticRunCompletedCycleCount + 1} entered {start.CurrentStepId}.",
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private void CompleteAutomaticRunCycle(long eventTick, TimeSpan eventTime)
    {
        var configuration = _automaticRunConfiguration;
        if (!_automaticRunActive
            || configuration is null
            || !string.Equals(_activeSequenceId, configuration.SequenceId, StringComparison.Ordinal))
        {
            return;
        }

        _automaticRunCompletedCycleCount++;
        Emit(
            "AutomaticRun",
            "AutomaticRunCycleCompleted",
            $"Automatic cycle {_automaticRunCompletedCycleCount} completed.",
            tickIndex: eventTick,
            simulationTime: eventTime);

        if (configuration.Repeat)
        {
            _automaticRunWaitingForRepeat = true;
            _automaticRunRemainingDelayTicks = _automaticRunRepeatDelayTicks;
            return;
        }

        _automaticRunActive = false;
        _automaticRunWaitingForRepeat = false;
        _automaticRunRemainingDelayTicks = 0;
        Emit(
            "AutomaticRun",
            "AutomaticRunCompleted",
            $"Automatic run completed after {_automaticRunCompletedCycleCount} cycle(s).",
            tickIndex: eventTick,
            simulationTime: eventTime);
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
            _settings.TimeScale,
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
                : _machineLayout.CapturePrealignerSnapshots());
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

    private void Emit(
        string category,
        string code,
        string message,
        string? commandId = null,
        long? tickIndex = null,
        TimeSpan? simulationTime = null)
    {
        _eventChannel.Writer.TryWrite(new SimulationEvent(
            ++_eventIndex,
            tickIndex ?? _tickIndex,
            simulationTime ?? _clock.Time,
            category,
            code,
            message,
            commandId));
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

    private SimulationCommandResult CompleteLifecycleRejection(
        SimulationCommand command,
        SimulationCommandErrorCode errorCode)
    {
        var snapshot = CurrentSnapshot;
        var detail = errorCode == SimulationCommandErrorCode.EngineNotStarted
            ? "The simulation engine has not started."
            : "The simulation engine is stopped.";
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

    private static void CompleteCommands(
        IEnumerable<(SimulationCommand Command, SimulationCommandResult Result)> commandResults)
    {
        foreach (var (command, result) in commandResults)
        {
            command.TryComplete(result);
        }
    }

    private bool TryResolveDistinctAxes(
        IEnumerable<string> axisIds,
        out IReadOnlyList<ServoAxisComponent> axes,
        out (SimulationCommandErrorCode ErrorCode, string Detail) error)
    {
        var candidates = new List<ServoAxisComponent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var axisId in axisIds)
        {
            if (string.IsNullOrWhiteSpace(axisId))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = (SimulationCommandErrorCode.AxisGroupInvalid, "Every coordinated axis requires an id.");
                return false;
            }

            if (!ids.Add(axisId))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = (SimulationCommandErrorCode.AxisGroupInvalid, $"Axis id '{axisId}' is duplicated.");
                return false;
            }

            var axis = _axes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, axisId, StringComparison.Ordinal));
            if (axis is null)
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = (SimulationCommandErrorCode.AxisNotFound, $"Axis '{axisId}' was not found.");
                return false;
            }

            candidates.Add(axis);
        }

        if (candidates.Count == 0)
        {
            axes = Array.Empty<ServoAxisComponent>();
            error = (SimulationCommandErrorCode.AxisGroupInvalid, "At least one coordinated axis is required.");
            return false;
        }

        axes = candidates;
        error = (SimulationCommandErrorCode.None, string.Empty);
        return true;
    }

    private static bool TryCreateAxes(
        IEnumerable<AxisConfiguration> configurations,
        out IReadOnlyList<ServoAxisComponent> axes,
        out string error)
    {
        var candidates = new List<ServoAxisComponent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in configurations)
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Id))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = "Every axis requires an id.";
                return false;
            }

            if (!ids.Add(configuration.Id))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = $"Axis id '{configuration.Id}' is duplicated.";
                return false;
            }

            if (!double.IsFinite(configuration.MinimumPosition)
                || !double.IsFinite(configuration.MaximumPosition)
                || !double.IsFinite(configuration.HomePosition)
                || configuration.MinimumPosition > configuration.MaximumPosition
                || configuration.HomePosition < configuration.MinimumPosition
                || configuration.HomePosition > configuration.MaximumPosition
                || !double.IsFinite(configuration.MaximumVelocity)
                || configuration.MaximumVelocity <= 0
                || !double.IsFinite(configuration.Acceleration)
                || configuration.Acceleration <= 0
                || !double.IsFinite(configuration.Deceleration)
                || configuration.Deceleration <= 0)
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = $"Axis '{configuration.Id}' has invalid limits or motion parameters.";
                return false;
            }

            candidates.Add(new ServoAxisComponent(CloneAxis(configuration)));
        }

        axes = candidates;
        error = string.Empty;
        return true;
    }

    private static bool TryCreateMachineLayout(
        MachineLayoutRuntimeConfiguration? configuration,
        IReadOnlyList<ServoAxisComponent> axes,
        IReadOnlyList<DeterministicVirtualCamera> cameras,
        DeterministicSignalHub signalHub,
        out DeterministicMachineLayout? machineLayout,
        out string error)
    {
        machineLayout = null;
        error = string.Empty;
        if (configuration is null)
        {
            return true;
        }

        var axisIds = axes.Select(axis => axis.Id).ToHashSet(StringComparer.Ordinal);
        var missingStageAxis = configuration.Components
            .OfType<AxisBoundStageRuntimeConfiguration>()
            .FirstOrDefault(stage => !axisIds.Contains(stage.AxisId));
        if (missingStageAxis is not null)
        {
            error = $"Layout stage '{missingStageAxis.Id}' axis '{missingStageAxis.AxisId}' was not configured.";
            return false;
        }

        var missingHandlerAxis = configuration.WaferHandlers.FirstOrDefault(handler =>
            !axisIds.Contains(handler.HorizontalAxisId) || !axisIds.Contains(handler.VerticalAxisId));
        if (missingHandlerAxis is not null)
        {
            error = $"Wafer-handler '{missingHandlerAxis.Id}' references an axis that was not configured.";
            return false;
        }

        var missingPrealignerAxis = configuration.Prealigners.FirstOrDefault(prealigner =>
            !axisIds.Contains(prealigner.RotaryAxisId));
        if (missingPrealignerAxis is not null)
        {
            error = $"Pre-aligner '{missingPrealignerAxis.Id}' rotary axis '{missingPrealignerAxis.RotaryAxisId}' was not configured.";
            return false;
        }

        var cameraIds = cameras.Select(camera => camera.Id).ToHashSet(StringComparer.Ordinal);
        var missingSorterCamera = configuration.InspectionSortRouters.FirstOrDefault(sorter =>
            !cameraIds.Contains(sorter.CameraId));
        if (missingSorterCamera is not null)
        {
            error = $"Inspection sorter '{missingSorterCamera.Id}' camera '{missingSorterCamera.CameraId}' was not configured.";
            return false;
        }

        var missingHandoffCamera = configuration.InspectionHandoffs.FirstOrDefault(handoff =>
            !cameraIds.Contains(handoff.CameraId));
        if (missingHandoffCamera is not null)
        {
            error = $"Inspection handoff '{missingHandoffCamera.Id}' camera '{missingHandoffCamera.CameraId}' was not configured.";
            return false;
        }

        try
        {
            machineLayout = new DeterministicMachineLayout(configuration, signalHub);
            machineLayout.Reset();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = $"Layout configuration failed: {exception.Message}";
            machineLayout = null;
            return false;
        }
    }

    private static bool TryCreatePickPlaceWorkpiece(
        PickPlaceWorkpieceRuntimeConfiguration? configuration,
        IReadOnlyList<ServoAxisComponent> axes,
        DeterministicSignalHub signalHub,
        out DeterministicPickPlaceWorkpiece? workpiece,
        out string error)
    {
        workpiece = null;
        error = string.Empty;
        if (configuration is null)
        {
            return true;
        }

        ServoAxisComponent? xAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, configuration.XAxisId, StringComparison.Ordinal));
        ServoAxisComponent? yAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, configuration.YAxisId, StringComparison.Ordinal));
        SignalReadResult gripper = signalHub.ReadDigitalSignal(configuration.GripperSignalId);
        if (string.IsNullOrWhiteSpace(configuration.Id) ||
            string.IsNullOrWhiteSpace(configuration.Name) ||
            xAxis is null ||
            yAxis is null ||
            ReferenceEquals(xAxis, yAxis) ||
            !gripper.IsAccepted ||
            gripper.Kind != ChannelKind.DigitalOutput ||
            !double.IsFinite(configuration.PickX) ||
            !double.IsFinite(configuration.PickY) ||
            configuration.PickX < xAxis.MinimumPosition ||
            configuration.PickX > xAxis.MaximumPosition ||
            configuration.PickY < yAxis.MinimumPosition ||
            configuration.PickY > yAxis.MaximumPosition)
        {
            error = "Pick-and-Place workpiece configuration is invalid.";
            return false;
        }

        workpiece = new DeterministicPickPlaceWorkpiece(configuration);
        return true;
    }

    private bool TryValidateAutomaticRun(
        AutomaticRunConfiguration? configuration,
        IReadOnlyDictionary<string, CompiledSequence> compiledSequences,
        DeterministicSignalHub signalHub,
        out int repeatDelayTicks,
        out string error)
    {
        repeatDelayTicks = 0;
        error = string.Empty;
        if (configuration is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(configuration.SequenceId))
        {
            error = "Automatic run requires a sequence id.";
            return false;
        }

        if (!compiledSequences.ContainsKey(configuration.SequenceId))
        {
            error = $"Automatic sequence '{configuration.SequenceId}' is not configured.";
            return false;
        }

        if (configuration.StartInputId is not null)
        {
            if (string.IsNullOrWhiteSpace(configuration.StartInputId))
            {
                error = "Automatic start input id cannot be blank.";
                return false;
            }

            var input = signalHub.ReadDigitalSignal(configuration.StartInputId);
            if (!input.IsAccepted)
            {
                error = $"Automatic start input '{configuration.StartInputId}' is not configured.";
                return false;
            }

            if (input.Kind != ChannelKind.DigitalInput)
            {
                error = $"Automatic start input '{configuration.StartInputId}' must be a digital input.";
                return false;
            }
        }

        if (configuration.RepeatDelayMilliseconds < 0)
        {
            error = "Automatic repeat delay cannot be negative.";
            return false;
        }

        if (!configuration.Repeat && configuration.RepeatDelayMilliseconds != 0)
        {
            error = "Automatic repeat delay must be zero when repeat is disabled.";
            return false;
        }

        var repeatDelay = TimeSpan.FromMilliseconds(configuration.RepeatDelayMilliseconds);
        if (repeatDelay.Ticks % _settings.FixedStep.Ticks != 0)
        {
            error = "Automatic repeat delay must be an exact multiple of the simulation fixed step.";
            return false;
        }

        var tickCount = repeatDelay.Ticks / _settings.FixedStep.Ticks;
        if (tickCount > int.MaxValue)
        {
            error = "Automatic repeat delay exceeds the supported fixed-tick range.";
            return false;
        }

        repeatDelayTicks = (int)tickCount;
        return true;
    }

    private static bool TryCreateCameras(
        IEnumerable<VirtualCameraConfiguration> configurations,
        out IReadOnlyList<DeterministicVirtualCamera> cameras,
        out string error)
    {
        var candidates = new List<DeterministicVirtualCamera>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in configurations)
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Id))
            {
                cameras = Array.Empty<DeterministicVirtualCamera>();
                error = "Every virtual camera requires an id.";
                return false;
            }

            if (!ids.Add(configuration.Id))
            {
                cameras = Array.Empty<DeterministicVirtualCamera>();
                error = $"Virtual camera id '{configuration.Id}' is duplicated.";
                return false;
            }

            candidates.Add(new DeterministicVirtualCamera(configuration));
        }

        cameras = candidates;
        error = string.Empty;
        return true;
    }

    private static AxisConfiguration CloneAxis(AxisConfiguration source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            MinimumPosition = source.MinimumPosition,
            MaximumPosition = source.MaximumPosition,
            HomePosition = source.HomePosition,
            MaximumVelocity = source.MaximumVelocity,
            Acceleration = source.Acceleration,
            Deceleration = source.Deceleration,
            FollowingErrorLimit = source.FollowingErrorLimit
        };

    private static SimulationCommandErrorCode MapAxisError(AxisCommandErrorCode errorCode) =>
        errorCode switch
        {
            AxisCommandErrorCode.InvalidTarget => SimulationCommandErrorCode.AxisTargetInvalid,
            AxisCommandErrorCode.TargetOutOfRange => SimulationCommandErrorCode.AxisTargetOutOfRange,
            AxisCommandErrorCode.InvalidVelocity => SimulationCommandErrorCode.AxisVelocityInvalid,
            AxisCommandErrorCode.VelocityOutOfRange => SimulationCommandErrorCode.AxisVelocityOutOfRange,
            AxisCommandErrorCode.AxisBusy => SimulationCommandErrorCode.AxisBusy,
            AxisCommandErrorCode.AxisInterlocked => SimulationCommandErrorCode.AxisInterlocked,
            _ => SimulationCommandErrorCode.AxisTargetInvalid
        };

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";

    private static string FormatFault(SimulationFaultSnapshot fault) =>
        fault.ForcedValue.HasValue
            ? $"{fault.Kind} on '{fault.TargetId}' forced to {FormatSignal(fault.ForcedValue.Value)}."
            : $"{fault.Kind} on '{fault.TargetId}'.";

    private static string FormatConveyorState(bool isRunning, ConveyorDirection direction) =>
        isRunning ? $"RUNNING {direction}" : $"STOPPED {direction}";

    private enum EngineLifecycleState
    {
        Created,
        Running,
        Stopping,
        Stopped
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        _stopCts.Dispose();
    }

    private sealed class SimulationSequenceRuntimeContext : ISequenceRuntimeContext
    {
        private readonly FixedStepSimulationEngine _engine;
        private readonly long _eventTick;
        private readonly TimeSpan _eventTime;

        public SimulationSequenceRuntimeContext(
            FixedStepSimulationEngine engine,
            long eventTick,
            TimeSpan eventTime)
        {
            _engine = engine;
            _eventTick = eventTick;
            _eventTime = eventTime;
        }

        public SequenceSignalReadResult ReadSignal(string signalId)
        {
            var read = _engine._signalHub.ReadDigitalSignal(signalId);
            return read.IsAccepted && read.Value.HasValue
                ? SequenceSignalReadResult.Success(read.Value.Value)
                : SequenceSignalReadResult.Failure(
                    read.ErrorCode == SignalHubErrorCode.ChannelNotFound
                        ? SequenceContextErrorCode.TargetNotFound
                        : SequenceContextErrorCode.InvalidTargetKind,
                    $"Signal '{signalId}' read failed: {read.ErrorCode}.");
        }

        public SequenceContextOperationResult SetSignal(string signalId, bool value)
        {
            var write = _engine._signalHub.SetDigitalOutput(
                signalId,
                value,
                SignalWriteOwner.EmbeddedSequence);
            if (!write.IsAccepted)
            {
                return SequenceContextOperationResult.Failure(
                    write.ErrorCode == SignalHubErrorCode.ChannelNotFound
                        ? SequenceContextErrorCode.TargetNotFound
                        : SequenceContextErrorCode.Rejected,
                    $"Signal '{signalId}' write failed: {write.ErrorCode}.");
            }

            if (write.StateChanged)
            {
                _engine.Emit(
                    "I/O",
                    "DigitalOutputChanged",
                    $"{signalId} = {FormatSignal(value)}.",
                    tickIndex: _eventTick,
                    simulationTime: _eventTime);
            }
            return SequenceContextOperationResult.Success();
        }

        public SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition)
        {
            var axis = _engine._axes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, axisId, StringComparison.Ordinal));
            if (axis is null)
            {
                return SequenceContextOperationResult.Failure(
                    SequenceContextErrorCode.TargetNotFound,
                    $"Axis '{axisId}' was not found.");
            }

            var move = axis.MoveAbsolute(targetPosition);
            if (!move.IsAccepted)
            {
                return SequenceContextOperationResult.Failure(
                    SequenceContextErrorCode.Rejected,
                    $"Axis '{axisId}' move failed: {move.ErrorCode}.");
            }

            _engine.Emit(
                "Motion",
                "SequenceAxisMoveAccepted",
                $"{axisId} target = {targetPosition:F3}.",
                tickIndex: _eventTick,
                simulationTime: _eventTime);
            return SequenceContextOperationResult.Success();
        }

        public SequenceAxisMotionReadResult ReadAxisMotionState(string axisId)
        {
            var axis = _engine._axes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, axisId, StringComparison.Ordinal));
            if (axis is null)
            {
                return SequenceAxisMotionReadResult.Failure(
                    SequenceContextErrorCode.TargetNotFound,
                    $"Axis '{axisId}' was not found.");
            }

            return axis.State switch
            {
                AxisState.Moving => SequenceAxisMotionReadResult.Success(SequenceAxisMotionState.Moving),
                AxisState.Idle => SequenceAxisMotionReadResult.Success(SequenceAxisMotionState.Completed),
                _ => SequenceAxisMotionReadResult.Success(SequenceAxisMotionState.Faulted)
            };
        }

        public SequenceCameraTriggerResult TriggerCamera(string cameraId, string recipeId)
        {
            var camera = _engine._cameras.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cameraId, StringComparison.Ordinal));
            if (camera is null)
            {
                return SequenceCameraTriggerResult.Failure(
                    SequenceContextErrorCode.TargetNotFound,
                    $"Virtual camera '{cameraId}' was not found.");
            }

            var trigger = camera.Trigger(recipeId);
            if (!trigger.IsAccepted || string.IsNullOrWhiteSpace(trigger.AcquisitionId))
            {
                var contextCode = trigger.ErrorCode switch
                {
                    VirtualCameraTriggerErrorCode.CameraFaulted => SequenceContextErrorCode.Faulted,
                    _ => SequenceContextErrorCode.Rejected
                };
                return SequenceCameraTriggerResult.Failure(
                    contextCode,
                    $"Virtual camera '{cameraId}' trigger failed: {trigger.ErrorCode}.");
            }

            _engine.Emit(
                "Camera",
                "CameraTriggered",
                $"{cameraId} started {trigger.AcquisitionId} for recipe '{recipeId}'.",
                tickIndex: _eventTick,
                simulationTime: _eventTime);
            return SequenceCameraTriggerResult.Success(trigger.AcquisitionId);
        }

        public SequenceVisionResultReadResult ReadVisionResult(
            string cameraId,
            string acquisitionId)
        {
            var camera = _engine._cameras.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cameraId, StringComparison.Ordinal));
            if (camera is null)
            {
                return SequenceVisionResultReadResult.Failure(
                    SequenceContextErrorCode.TargetNotFound,
                    $"Virtual camera '{cameraId}' was not found.");
            }

            var snapshot = camera.CaptureSnapshot();
            if (snapshot.CurrentAcquisitionId is null)
            {
                return SequenceVisionResultReadResult.Success(SequenceVisionResultState.NotTriggered);
            }

            if (!string.Equals(snapshot.CurrentAcquisitionId, acquisitionId, StringComparison.Ordinal))
            {
                return SequenceVisionResultReadResult.Failure(
                    SequenceContextErrorCode.Unavailable,
                    $"Virtual camera '{cameraId}' no longer owns acquisition '{acquisitionId}'.");
            }

            return snapshot.State switch
            {
                VirtualCameraState.Idle =>
                    SequenceVisionResultReadResult.Success(SequenceVisionResultState.NotTriggered),
                VirtualCameraState.Exposing or VirtualCameraState.Transferring =>
                    SequenceVisionResultReadResult.Success(SequenceVisionResultState.Pending),
                VirtualCameraState.Faulted =>
                    SequenceVisionResultReadResult.Success(SequenceVisionResultState.Faulted),
                VirtualCameraState.FrameReady => ReadCompletedVisionResult(snapshot, acquisitionId),
                _ => SequenceVisionResultReadResult.Failure(
                    SequenceContextErrorCode.Unavailable,
                    $"Virtual camera '{cameraId}' returned an unsupported state '{snapshot.State}'.")
            };
        }

        private static SequenceVisionResultReadResult ReadCompletedVisionResult(
            VirtualCameraSnapshot snapshot,
            string acquisitionId)
        {
            if (snapshot.Result is null
                || !string.Equals(snapshot.Result.AcquisitionId, acquisitionId, StringComparison.Ordinal))
            {
                return SequenceVisionResultReadResult.Failure(
                    SequenceContextErrorCode.Unavailable,
                    $"Virtual camera '{snapshot.Id}' has no result for acquisition '{acquisitionId}'.");
            }

            return SequenceVisionResultReadResult.Success(
                snapshot.Result.Decision == PlaceholderInspectionDecision.Pass
                    ? SequenceVisionResultState.Passed
                    : SequenceVisionResultState.Failed);
        }
    }
}
