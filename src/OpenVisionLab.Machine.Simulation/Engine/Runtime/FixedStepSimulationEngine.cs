using System.Collections.Immutable;
using System.Globalization;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
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

public sealed class FixedStepSimulationEngine : ISimulationEngine, ISimulationEventJournalSource
{
    private readonly SimulationSettings _settings;
    private readonly Channel<SimulationCommand> _commandChannel;
    private readonly SimulationEventPublisher _eventPublisher;
    private readonly LatestSnapshotStore _snapshotStore;
    private readonly SimulationEngineLifecycle _lifecycle;
    private readonly SimulationPhysicalRuntimeTick _physicalRuntimeTick;
    private readonly SimulationRuntimeState _runtimeState;
    private readonly DeterministicSimulationCommandTraceStore _commandTraceStore;
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
    private readonly Action<SimulationEngineFaultPoint>? _faultInjector;
    private SimulationCommand? _currentCommand;
    private string? _operationContext;
    private bool _disposed;

    private SimulationClock Clock => _runtimeState.Clock;
    private List<ServoAxisComponent> Axes => _runtimeState.Axes;
    private List<DeterministicVirtualCamera> Cameras => _runtimeState.Cameras;
    private SimulationSequenceRuntime SequenceRuntime => _runtimeState.SequenceRuntime;
    private SimulationConditionScenarioRuntime ConditionScenarioRuntime => _runtimeState.ConditionScenarioRuntime;
    private SimulationAutomaticRunRuntime AutomaticRunRuntime => _runtimeState.AutomaticRunRuntime;
    private SimulationFaultRuntime FaultRuntime => _runtimeState.FaultRuntime;
    private DeterministicSignalHub SignalHub => _runtimeState.SignalHub;
    private DeterministicMachineLayout? MachineLayout => _runtimeState.MachineLayout;
    private DeterministicPickPlaceWorkpiece? PickPlaceWorkpiece => _runtimeState.PickPlaceWorkpiece;
    private double _timeScale
    {
        get => _runtimeState.TimeScale;
        set => _runtimeState.TimeScale = value;
    }
    private SimulationRunMode _runMode
    {
        get => _runtimeState.RunMode;
        set => _runtimeState.RunMode = value;
    }
    private SimulationControlOwner _controlOwner
    {
        get => _runtimeState.ControlOwner;
        set => _runtimeState.ControlOwner = value;
    }
    private string? _activeSequenceId
    {
        get => _runtimeState.ActiveSequenceId;
        set => _runtimeState.ActiveSequenceId = value;
    }
    private int _pendingSteps
    {
        get => _runtimeState.PendingSteps;
        set => _runtimeState.PendingSteps = value;
    }
    private long TickIndex => _runtimeState.TickIndex;
    private long CommandBoundaryTick => _runtimeState.CommandBoundaryTick;
    private TimeSpan CommandBoundaryTime => _runtimeState.CommandBoundaryTime;
    private string? ProjectId => _runtimeState.ProjectId;
    private long RuntimeGeneration => _runtimeState.RuntimeGeneration;

    public FixedStepSimulationEngine(SimulationSettings settings)
        : this(settings, null)
    {
    }

    internal FixedStepSimulationEngine(
        SimulationSettings settings,
        Action<SimulationEngineFaultPoint>? faultInjector,
        Action<SimulationCommand>? commandAdmissionObserver = null)
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
        if (settings.CommandTraceEntryCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "CommandTraceEntryCapacity must be positive.");
        }
        if (settings.AutomaticExternalInspectionWallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "AutomaticExternalInspectionWallTimeout must be positive.");
        }
        if (settings.CanonicalEventJournalCapacity is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "CanonicalEventJournalCapacity must be positive when configured.");
        }

        _commandTraceStore = new(settings.CommandTraceEntryCapacity);
        _runtimeState = new SimulationRuntimeState(settings.FixedStep, settings.TimeScale);
        _commandChannel = Channel.CreateBounded<SimulationCommand>(
            new BoundedChannelOptions(settings.CommandQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _eventPublisher = new SimulationEventPublisher(
            settings.EventBufferCapacity,
            settings.CanonicalEventJournalCapacity ?? settings.EventBufferCapacity);
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
            () => CurrentSnapshot,
            commandAdmissionObserver);
    }

    public SimulationSnapshot CurrentSnapshot => _snapshotStore.Current;
    public TimeSpan FixedStep => _settings.FixedStep;
    public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotStore.Reader;
    public ChannelReader<SimulationEvent> EventReader => _eventPublisher.Reader;
    public SimulationEventJournalSnapshot EventJournal => _eventPublisher.JournalSnapshot;
    public Task<SimulationEngineTerminationResult> Termination => _lifecycle.Termination;

    public IAsyncEnumerable<SimulationEvent> ReadCanonicalEventsAsync(
        CancellationToken cancellationToken = default) =>
        _eventPublisher.ReadJournalAsync(cancellationToken);

    public ImmutableArray<DeterministicSimulationCommandTraceEntry> CommandTrace => _commandTraceStore.Snapshot();

    public int CommandTraceCount => _commandTraceStore.Count;

    public int CommandTraceCapacity => _commandTraceStore.Capacity;

    public bool CommandTraceIsComplete => _commandTraceStore.IsComplete;

    public long CommandTraceDroppedEntryCount => _commandTraceStore.DroppedEntryCount;

    public DeterministicSimulationCommandTracePackage CreateCommandTracePackage()
    {
        if (!CommandTraceIsComplete)
        {
            throw new InvalidOperationException(
                $"The command trace is incomplete; {CommandTraceDroppedEntryCount} entries were dropped.");
        }

        return _commandTraceStore.CreatePackage(FixedStep);
    }

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

        _runtimeState.AddAxis(axis);
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
        // Canonical events include numeric messages. Keep this worker's async
        // context deterministic without changing the caller's UI culture.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timing = new SimulationRunLoopTiming(_settings.FixedStep, _settings.MaxCatchUpTicks);
        timing.Reset(stopwatch.Elapsed);
        var pendingCommands = new List<PendingSimulationCommand>();
        TimeSpan? automaticExternalWaitStartedAt = null;
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

                if (!_runtimeState.IsAutomaticExternalInspectionWaiting)
                {
                    automaticExternalWaitStartedAt = null;
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
                    if (_runtimeState.IsAutomaticExternalInspectionWaiting)
                    {
                        automaticExternalWaitStartedAt ??= stopwatch.Elapsed;
                        var wake = await WaitForAutomaticExternalInspectionAsync(
                                stopwatch,
                                automaticExternalWaitStartedAt.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (wake == AutomaticExternalInspectionWaitWake.WallTimeout)
                        {
                            FailAutomaticExternalInspectionTimeout(
                                AutomaticExternalInspectionClosureReason.WallTimeout,
                                _settings.AutomaticExternalInspectionWallTimeout);
                            PublishSnapshotAfterWait();
                        }
                        else if (wake == AutomaticExternalInspectionWaitWake.LogicalTick)
                        {
                            var logicalTimeout = _runtimeState.AdvanceAutomaticExternalInspectionWait(
                                _settings.FixedStep);
                            if (logicalTimeout)
                            {
                                FailAutomaticExternalInspectionTimeout(
                                    AutomaticExternalInspectionClosureReason.SimulationTimeout,
                                    _runtimeState.AutomaticExternalInspectionSimulationTimeout
                                        ?? _settings.FixedStep);
                            }

                            PublishSnapshotAfterWait();
                        }
                    }
                    else
                    {
                        await _commandChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    }
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
                    ticksToRun = Math.Min(_settings.MaxCatchUpTicks, _pendingSteps);
                }
                else
                {
                    ticksToRun = timing.CalculateRealTimeTicks(stopwatch.Elapsed, _timeScale);
                }

                var fastForwardBatch = _runMode == SimulationRunMode.FastForward;
                var ticksExecuted = 0;
                for (var index = 0; index < ticksToRun; index++)
                {
                    _operationContext = "Tick";
                    InjectFault(SimulationEngineFaultPoint.BeforeTick);
                    Tick();
                    InjectFault(SimulationEngineFaultPoint.AfterTick);
                    ticksExecuted++;
                    if (stopTickBatchWhenPaused && _runMode == SimulationRunMode.Paused)
                    {
                        break;
                    }
                }

                if (fastForwardBatch && ticksExecuted > 0)
                {
                    _pendingSteps = Math.Max(0, _pendingSteps - ticksExecuted);
                    if (_pendingSteps == 0 && _runMode == SimulationRunMode.FastForward)
                    {
                        _runMode = SimulationRunMode.Paused;
                        Emit(
                            "Runtime",
                            "FastForwardCompleted",
                            "Finite FastForward tick budget completed; Simulation is paused.",
                            tickIndex: TickIndex,
                            simulationTime: Clock.Time);
                        PublishSnapshot();
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

    private async Task<AutomaticExternalInspectionWaitWake> WaitForAutomaticExternalInspectionAsync(
        System.Diagnostics.Stopwatch stopwatch,
        TimeSpan waitStartedAt,
        CancellationToken cancellationToken)
    {
        var wallTimeout = _settings.AutomaticExternalInspectionWallTimeout;
        var wallElapsed = stopwatch.Elapsed - waitStartedAt;
        if (wallElapsed >= wallTimeout)
        {
            return AutomaticExternalInspectionWaitWake.WallTimeout;
        }

        var wallRemaining = wallTimeout - wallElapsed;
        var delay = wallRemaining < _settings.FixedStep
            ? wallRemaining
            : _settings.FixedStep;
        using var wakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var commandTask = _commandChannel.Reader.WaitToReadAsync(wakeCancellation.Token).AsTask();
        var timerTask = Task.Delay(delay, wakeCancellation.Token);
        var completed = await Task.WhenAny(commandTask, timerTask).ConfigureAwait(false);
        var wallTimedOut = stopwatch.Elapsed - waitStartedAt >= wallTimeout;
        var commandReady = commandTask.IsCompletedSuccessfully && commandTask.Result;
        wakeCancellation.Cancel();
        try
        {
            await Task.WhenAll(commandTask, timerTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (wallTimedOut)
        {
            return AutomaticExternalInspectionWaitWake.WallTimeout;
        }

        return ResolveAutomaticExternalInspectionWaitWake(
            wallTimedOut,
            completed == commandTask,
            commandReady);
    }

    private void PublishSnapshotAfterWait()
    {
        _operationContext = "SnapshotPublication";
        InjectFault(SimulationEngineFaultPoint.BeforeSnapshotPublication);
        PublishSnapshot();
        InjectFault(SimulationEngineFaultPoint.AfterSnapshotPublication);
    }

    private void FailAutomaticExternalInspectionTimeout(
        AutomaticExternalInspectionClosureReason reason,
        TimeSpan timeout)
    {
        var camera = Cameras.FirstOrDefault(candidate =>
            candidate.State == VirtualCameraState.AwaitingExternalResult);
        var cameraSnapshot = camera?.CaptureSnapshot();
        var sequenceId = _activeSequenceId ?? string.Empty;
        var stepId = CurrentSequenceStepId() ?? string.Empty;
        var waitElapsed = _runtimeState.AutomaticExternalInspectionWaitElapsed;
        if (cameraSnapshot is not null
            && !string.IsNullOrWhiteSpace(sequenceId)
            && !string.IsNullOrWhiteSpace(stepId)
            && !string.IsNullOrWhiteSpace(cameraSnapshot.CurrentAcquisitionId)
            && !string.IsNullOrWhiteSpace(cameraSnapshot.FrameEvidence?.FrameId))
        {
            _runtimeState.RecordAutomaticExternalInspectionClosure(
                new AutomaticExternalInspectionClosure(
                    sequenceId,
                    stepId,
                    cameraSnapshot.Id,
                    cameraSnapshot.CurrentAcquisitionId!,
                    cameraSnapshot.FrameEvidence!.FrameId,
                    reason,
                    waitElapsed,
                    timeout));
        }

        if (camera is not null)
        {
            camera.Fault();
        }

        if (!string.IsNullOrWhiteSpace(sequenceId)
            && SequenceRuntime.SequenceExecutors.TryGetValue(sequenceId, out var executor)
            && executor.CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            var aborted = executor.Abort();
            SequenceRuntime.DebugState.ClearPendingSemanticStep();
            SequenceRuntime.DebugState.SetPause(
                SequenceDebugPauseReason.SequenceAborted,
                aborted.CurrentStepId);
        }

        AutomaticRunRuntime.MarkFaulted();
        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _controlOwner = SimulationControlOwner.Definition;
        _runtimeState.ClearAutomaticExternalInspection(clearSources: true);

        var timeoutKind = reason == AutomaticExternalInspectionClosureReason.WallTimeout
            ? "WallClock"
            : "SimulationClock";
        var timeoutMessage =
            $"Automatic external inspection timed out: timeoutKind={timeoutKind}; " +
            $"sequence={sequenceId}; step={stepId}; camera={cameraSnapshot?.Id ?? "<none>"}; " +
            $"acquisition={cameraSnapshot?.CurrentAcquisitionId ?? "<none>"}; " +
            $"elapsedMs={FormatMilliseconds(waitElapsed)}; limitMs={FormatMilliseconds(timeout)}.";
        Emit(
            "Vision",
            "AutomaticExternalInspectionTimedOut",
            timeoutMessage,
            tickIndex: TickIndex,
            simulationTime: Clock.Time);
        Emit(
            "AutomaticRun",
            "AutomaticExternalInspectionFailedClosed",
            timeoutMessage + " The Sequence was aborted; automatic retry is disabled.",
            tickIndex: TickIndex,
            simulationTime: Clock.Time);
        Emit(
            "AutomaticRun",
            "AutomaticRunAborted",
            $"Automatic sequence '{sequenceId}' was aborted after the external inspection timeout.",
            tickIndex: TickIndex,
            simulationTime: Clock.Time);
    }

    private void CloseAutomaticExternalInspectionAfterAbort()
    {
        if (!_runtimeState.AutomaticExternalInspectionEnabled)
        {
            return;
        }

        var camera = _runtimeState.IsAutomaticExternalInspectionWaiting
            ? Cameras.FirstOrDefault(candidate =>
                candidate.State == VirtualCameraState.AwaitingExternalResult)
            : null;
        var snapshot = camera?.CaptureSnapshot();
        var sequenceId = _activeSequenceId ?? string.Empty;
        var stepId = CurrentSequenceStepId() ?? string.Empty;
        if (_runtimeState.IsAutomaticExternalInspectionWaiting
            && snapshot is not null
            && !string.IsNullOrWhiteSpace(sequenceId)
            && !string.IsNullOrWhiteSpace(stepId)
            && !string.IsNullOrWhiteSpace(snapshot.CurrentAcquisitionId)
            && !string.IsNullOrWhiteSpace(snapshot.FrameEvidence?.FrameId))
        {
            _runtimeState.RecordAutomaticExternalInspectionClosure(
                new AutomaticExternalInspectionClosure(
                    sequenceId,
                    stepId,
                    snapshot.Id,
                    snapshot.CurrentAcquisitionId!,
                    snapshot.FrameEvidence!.FrameId,
                    AutomaticExternalInspectionClosureReason.Aborted,
                    _runtimeState.AutomaticExternalInspectionWaitElapsed,
                    _runtimeState.AutomaticExternalInspectionSimulationTimeout));
        }

        foreach (var pendingCamera in Cameras.Where(candidate =>
                     candidate.State == VirtualCameraState.AwaitingExternalResult))
        {
            pendingCamera.Fault();
        }

        _runtimeState.ClearAutomaticExternalInspection(clearSources: true);
    }

    private void FailAutomaticExternalInspectionConfiguration(
        long eventTick,
        TimeSpan eventTime,
        string cameraId,
        string detail)
    {
        foreach (var camera in Cameras.Where(candidate =>
                     candidate.State == VirtualCameraState.AwaitingExternalResult))
        {
            camera.Fault();
        }

        var sequenceId = _activeSequenceId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sequenceId)
            && SequenceRuntime.SequenceExecutors.TryGetValue(sequenceId, out var executor)
            && executor.CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            var aborted = executor.Abort();
            SequenceRuntime.DebugState.ClearPendingSemanticStep();
            SequenceRuntime.DebugState.SetPause(
                SequenceDebugPauseReason.SequenceAborted,
                aborted.CurrentStepId);
        }

        AutomaticRunRuntime.MarkFaulted();
        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _controlOwner = SimulationControlOwner.Definition;
        _runtimeState.ClearAutomaticExternalInspection(clearSources: true);
        Emit(
            "Vision",
            "AutomaticExternalInspectionFailedClosed",
            $"Automatic external inspection for camera '{cameraId}' failed closed: {detail}",
            tickIndex: eventTick,
            simulationTime: eventTime);
        Emit(
            "AutomaticRun",
            "AutomaticExternalInspectionFailedClosed",
            $"Automatic sequence '{sequenceId}' was aborted because the external inspection timeout " +
            $"could not be established: {detail}",
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private static string FormatMilliseconds(TimeSpan value) =>
        value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

    internal static AutomaticExternalInspectionWaitWake ResolveAutomaticExternalInspectionWaitWake(
        bool wallTimedOut,
        bool commandTaskCompleted,
        bool commandReady) => wallTimedOut
            ? AutomaticExternalInspectionWaitWake.WallTimeout
            : commandTaskCompleted || commandReady
                ? AutomaticExternalInspectionWaitWake.Command
                : AutomaticExternalInspectionWaitWake.LogicalTick;

    internal enum AutomaticExternalInspectionWaitWake
    {
        Command,
        LogicalTick,
        WallTimeout
    }

    private SimulationCommandResult ApplyCommand(SimulationCommand command)
    {
        _runtimeState.SetCommandBoundary();
        SimulationCommandResult result;
        switch (command)
        {
            // Check owned state at application time, before any handler mutates it.
            // CurrentSnapshot may still describe the previous configuration here.
            case { ExpectedRuntime: { } expected } when !_runtimeState.Matches(expected):
                result = Reject(
                    command,
                    SimulationCommandErrorCode.RuntimeIdentityMismatch,
                    $"Command expects project '{expected.ProjectId ?? "<none>"}' generation {expected.RuntimeGeneration}, " +
                    $"but current runtime is project '{ProjectId ?? "<none>"}' generation {RuntimeGeneration}.");
                break;

            case PlayCommand:
            case PauseCommand:
            case FastForwardCommand:
            case StepCommand:
            case StepSequenceCommand:
            case SetSequenceBreakpointCommand:
                result = ApplyRunControlCommand(command);
                break;

            case ResetCommand:
                ResetRuntime();
                result = Accept(command, "Runtime state reset to authored initial values.");
                if (ConditionScenarioRuntime.Profile is not null)
                {
                    EmitAtCommandBoundary(
                        "Condition",
                        "ConditionScenarioReset",
                        $"Condition scenario '{ConditionScenarioRuntime.Profile.ScenarioId}' reset to " +
                        $"{ConditionScenarioRuntime.Profile.InitialState} and stopped.",
                        command.CommandId);
                }
                EmitAtCommandBoundary(
                    "Runtime",
                    "RuntimeReset",
                    "Axes, I/O, workpiece, faults, cameras, sequence, condition scenario, clock, and tick index reset.",
                    command.CommandId);
                break;

            case ConfigureRuntimeCommand configureRuntime:
                result = ApplyRuntimeConfiguration(command, configureRuntime);
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

            case ArmAutomaticExternalInspectionCommand armAutomaticExternalInspection:
                result = ApplyArmAutomaticExternalInspection(armAutomaticExternalInspection);
                break;

            case ApplyExternalInspectionResultCommand externalInspectionResult:
                result = ApplyExternalInspectionResult(externalInspectionResult);
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
        if (!_commandTraceStore.Capture(command, result)
            && _commandTraceStore.DroppedEntryCount == 1)
        {
            EmitAtCommandBoundary(
                "Diagnostics",
                "CommandTraceOverflow",
                $"Command trace capacity {_commandTraceStore.Capacity} was reached; " +
                "subsequent command boundaries are not retained and trace evidence is incomplete.",
                command.CommandId);
        }
        return result;
    }

    private SimulationCommandResult ApplyRunControlCommand(SimulationCommand command)
    {
        if (_runtimeState.AutomaticExternalInspectionEnabled
            && _runtimeState.AutomaticExternalRequestPublished
            && command is PlayCommand or FastForwardCommand or StepCommand or StepSequenceCommand)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.InvalidRunMode,
                "Automatic external inspection is waiting for a Result; apply the Result or abort the Sequence first.");
        }

        var outcome = _runControlCommandHandler.Apply(
            command,
            new SimulationRunControlContext(
                _runMode,
                _pendingSteps,
                _activeSequenceId,
                CurrentSequenceStepId(),
                SequenceRuntime.CompiledSequences,
                SequenceRuntime.SequenceExecutors,
                SequenceRuntime.DebugState,
                CommandBoundaryTick,
                CommandBoundaryTime));
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
        if (stepId is not null && SequenceRuntime.DebugState.IsBreakpoint(sequenceId, stepId))
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

        if (SequenceRuntime.DebugState.IsSemanticStepBoundary(execution, rootSequenceId))
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
        var sequenceId = SequenceRuntime.DebugState.GetActiveSemanticStepSequenceId(_activeSequenceId);
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
        SequenceRuntime.DebugState.ClearPendingSemanticStep();
        SequenceRuntime.DebugState.SetPause(reason, stepId);
        Emit(
            "Sequence",
            eventCode,
            message,
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private string? CurrentSequenceStepId() => SequenceRuntime.CurrentStepId(_activeSequenceId);

    private void ClearSequenceDebugConfiguration() => SequenceRuntime.DebugState.Clear();

    private SimulationCommandResult ApplyStopConditionScenario(SimulationCommand command)
    {
        var outcome = _conditionScenarioStopHandler.Apply(
            command,
            new SimulationConditionScenarioStopContext(
                ConditionScenarioRuntime.IsActive,
                ConditionScenarioRuntime.Profile,
                ConditionScenarioRuntime.ExecutedTicks,
                CreateConditionScheduledFaultRecoveryContext(
                    restartSequence: false,
                    command.CommandId),
                _conditionScheduledFaultRecoveryHandler));
        if (outcome.State is { } state)
        {
            ConditionScenarioRuntime.ApplyStopState(state);
            _activeSequenceId = state.RecoveryState.ActiveSequenceId;
            _controlOwner = state.RecoveryState.ControlOwner;
            AutomaticRunRuntime.ApplyRecoveryState(state.RecoveryState);
        }

        foreach (var operationEvent in outcome.Events ?? Array.Empty<SimulationConditionScenarioStopEvent>())
        {
            Emit(
                operationEvent.Category,
                operationEvent.Code,
                operationEvent.Message,
                operationEvent.CommandId,
                CommandBoundaryTick,
                CommandBoundaryTime);
        }

        return outcome.Result;
    }

    private SimulationCommandResult ApplyRuntimeConfiguration(
        SimulationCommand command,
        ConfigureRuntimeCommand configureRuntime)
    {
        var configuration = configureRuntime.Configuration;
        if (!_runtimeState.TryApplyRuntimeConfiguration(
                configuration,
                configureRuntime.ProjectId,
                out var configurationError))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, configurationError);
        }

        var configurationSummary =
            $"Configured {Axes.Count} axis/axes, {configuration.Channels.Count} signal(s), " +
            $"{Cameras.Count} camera(s), {SequenceRuntime.SequenceExecutors.Count} sequence(s), and " +
            $"{configuration.Layout?.Components.Count ?? 0} layout component(s).";
        if (PickPlaceWorkpiece is not null)
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
        if (!_runtimeState.TryApplyAxisConfiguration(configurations, out var error))
        {
            return Reject(command, SimulationCommandErrorCode.RuntimeConfigurationInvalid, error);
        }

        EmitAtCommandBoundary(
            "Runtime",
            "AxesConfigured",
            $"Configured {Axes.Count} axis/axes; I/O, camera, and sequence runtime were cleared.",
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
                AutomaticRunRuntime.IsActive,
                Axes,
                Cameras,
                SequenceRuntime.SequenceExecutors,
                SignalHub,
                MachineLayout,
                FaultRuntime,
                CommandBoundaryTick,
                CommandBoundaryTime,
                FormatSignal,
                ProjectId,
                RuntimeGeneration));
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

    private SimulationCommandResult ApplyExternalInspectionResult(
        ApplyExternalInspectionResultCommand command)
    {
        if (_runMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.InvalidRunMode,
                "An external inspection Result can be applied only while Simulation is paused.");
        }

        var expected = command.ExpectedCorrelation;
        if (!command.AcknowledgementAccepted
            || !command.MessageChain.IsExactlyCorrelated
            || expected != command.ResultCorrelation
            || expected.ConsumerBuild != command.AcknowledgementProducer
            || expected.ConsumerBuild != command.ResultProducer
            || !string.Equals(expected.ProjectId, ProjectId, StringComparison.Ordinal))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.ExternalInspectionCorrelationMismatch,
                "The external inspection message chain, correlation, or consumer build does not match exactly.");
        }

        if (_runtimeState.TryGetAutomaticExternalInspectionClosure(
                expected.SequenceId,
                expected.CameraId,
                expected.AcquisitionId,
                expected.FrameId,
                out var closure))
        {
            var timeoutKind = closure.Reason switch
            {
                AutomaticExternalInspectionClosureReason.WallTimeout => "WallClock",
                AutomaticExternalInspectionClosureReason.SimulationTimeout => "SimulationClock",
                _ => "RunClosed"
            };
            var closureDetail =
                $"Late external Result {command.MessageChain.ResultMessageId:D} was quarantined: " +
                $"reason={closure.Reason}; timeoutKind={timeoutKind}; " +
                $"sequence={closure.SequenceId}; step={closure.StepId}; " +
                $"camera={closure.CameraId}; acquisition={closure.AcquisitionId}; " +
                "no runtime mutation was performed.";
            EmitAtCommandBoundary(
                "Vision",
                "AutomaticExternalInspectionLateResultQuarantined",
                closureDetail,
                command.CommandId);
            return Reject(
                command,
                SimulationCommandErrorCode.ExternalInspectionNotPending,
                closureDetail);
        }

        var camera = Cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, expected.CameraId, StringComparison.Ordinal));
        if (camera is null)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.CameraNotFound,
                $"Virtual camera '{expected.CameraId}' was not found.");
        }

        var decision = command.Status == ExternalInspectionResultStatus.Completed
            ? command.Outcome switch
            {
                ExternalInspectionOutcome.Pass => PlaceholderInspectionDecision.Pass,
                ExternalInspectionOutcome.Ng => PlaceholderInspectionDecision.Fail,
                _ => (PlaceholderInspectionDecision?)null
            }
            : null;
        var evidence = new VirtualCameraExternalResultEvidence(
            command.MessageChain.HandoffTransactionId,
            command.MessageChain.HandoffMessageId,
            command.MessageChain.AcknowledgementMessageId,
            command.MessageChain.ResultMessageId,
            command.MessageChain.ResultDocumentSha256,
            expected,
            command.Status,
            command.Outcome,
            command.RunId,
            decision);
        var admission = camera.ApplyExternalResult(evidence);
        if (!admission.IsAccepted)
        {
            var errorCode = admission.ErrorCode switch
            {
                VirtualCameraExternalResultAdmissionErrorCode.ConflictingDuplicate =>
                    SimulationCommandErrorCode.ExternalInspectionConflictingDuplicate,
                VirtualCameraExternalResultAdmissionErrorCode.AcquisitionMismatch or
                    VirtualCameraExternalResultAdmissionErrorCode.FrameMismatch =>
                    SimulationCommandErrorCode.ExternalInspectionAcquisitionMismatch,
                _ => SimulationCommandErrorCode.ExternalInspectionNotPending
            };
            return Reject(
                command,
                errorCode,
                $"Virtual camera '{expected.CameraId}' rejected the external Result: {admission.ErrorCode}.");
        }

        var detail = admission.IsIdempotent
            ? $"External Result {command.MessageChain.ResultMessageId:D} was already applied; no state changed."
            : admission.IsTerminalFailure
                ? $"External Result {command.MessageChain.ResultMessageId:D} entered fail-closed camera state; Reset is required."
                : $"External Result {command.MessageChain.ResultMessageId:D} applied to acquisition '{expected.AcquisitionId}'.";
        EmitAtCommandBoundary(
            "Vision",
            admission.IsIdempotent
                ? "ExternalVisionResultReplayIgnored"
                : admission.IsTerminalFailure
                    ? "ExternalVisionResultFailedClosed"
                    : "ExternalVisionResultApplied",
            detail,
            command.CommandId);
        if (!admission.IsIdempotent
            && _runtimeState.AutomaticExternalInspectionEnabled
            && AutomaticRunRuntime.IsActive
            && !admission.IsTerminalFailure)
        {
            _runtimeState.ClearAutomaticExternalRequestPublished();
            _runMode = _runtimeState.AutomaticExternalResumeRealTime
                ? SimulationRunMode.RealTime
                : SimulationRunMode.Paused;
            EmitAtCommandBoundary(
                "AutomaticRun",
                _runMode == SimulationRunMode.RealTime
                    ? "AutomaticExternalInspectionResumed"
                    : "AutomaticExternalInspectionApplied",
                _runMode == SimulationRunMode.RealTime
                    ? "The automatic Sequence resumed after the external Result was applied."
                    : "The external Result was applied; the automatic Sequence remains paused.",
                command.CommandId);
        }
        else if (!admission.IsIdempotent
            && admission.IsTerminalFailure
            && _runtimeState.AutomaticExternalInspectionEnabled
            && AutomaticRunRuntime.IsActive)
        {
            if (command.Status == ExternalInspectionResultStatus.Failed
                && command.Outcome == ExternalInspectionOutcome.ExecutionError)
            {
                _runtimeState.MarkExternalInspectionFailureForRetry(expected.SequenceId);
            }

            _runtimeState.ClearAutomaticExternalRequestPublished();
            EmitAtCommandBoundary(
                "AutomaticRun",
                "AutomaticExternalInspectionFailedClosed",
                "The external Result failed closed; abort or reset is required before automatic continuation.",
                command.CommandId);
        }
        return Accept(command, detail);
    }

    private SimulationCommandResult ApplyArmAutomaticExternalInspection(
        ArmAutomaticExternalInspectionCommand command)
    {
        if (_runMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.InvalidRunMode,
                "Automatic external inspection can be armed only while the simulation is paused.");
        }

        var isAutomaticExternalInspectionRearm =
            _runtimeState.AutomaticExternalInspectionRearmRequired;
        if (AutomaticRunRuntime.IsActive
            && !isAutomaticExternalInspectionRearm)
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                "Automatic external inspection cannot be armed while an automatic run is active.");
        }

        if (!SequenceRuntime.SequenceExecutors.ContainsKey(command.SequenceId))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Automatic sequence '{command.SequenceId}' is not configured.");
        }

        if (!_runtimeState.TryArmAutomaticExternalInspection(
            command.SequenceId,
            command.Sources,
            out var error))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                error);
        }

        if (isAutomaticExternalInspectionRearm)
        {
            _runtimeState.BeginAutomaticExternalInspection(
                command.SequenceId,
                resumeRealTime: true);
        }

        EmitAtCommandBoundary(
            "Vision",
            "AutomaticExternalInspectionArmed",
            $"Automatic external inspection sources were armed for sequence '{command.SequenceId}'.",
            command.CommandId);
        return Accept(command, "Automatic external inspection sources armed.");
    }

    private SimulationCommandResult ApplyFaultCommand(SimulationCommand command)
    {
        var outcome = _faultCommandHandler.Apply(
            command,
            new SimulationFaultCommandContext(
                Axes,
                SignalHub,
                MachineLayout,
                FaultRuntime,
                CommandBoundaryTick,
                CommandBoundaryTime));
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
                ConditionScenarioRuntime.IsActive,
                CreateSnapshot(),
                SequenceRuntime.SequenceExecutors,
                FaultRuntime,
                CommandBoundaryTick,
                CommandBoundaryTime));
        if (outcome.State is { } state)
        {
            ConditionScenarioRuntime.ApplyStartState(state);
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
                    AutomaticRunRuntime.IsActive,
                    AutomaticRunRuntime.WaitingForRepeat,
                    AutomaticRunRuntime.RemainingDelayTicks,
                    ConditionScenarioRuntime.ScheduledFaultInterruptedAutomaticRun),
                SequenceRuntime.SequenceExecutors,
                FaultRuntime,
                SequenceRuntime.DebugState,
                CommandBoundaryTick,
                CommandBoundaryTime,
                _runtimeState.ResetRetrySequenceId,
                AutomaticRunRuntime.Configuration is not null));
        if (outcome.State is { } state)
        {
            _runMode = state.RunMode;
            _controlOwner = state.ControlOwner;
            _pendingSteps = state.PendingSteps;
            _activeSequenceId = state.ActiveSequenceId;
            AutomaticRunRuntime.ApplySequenceState(state);
            ConditionScenarioRuntime.SetAutomaticRunInterruption(
                state.ConditionScheduledFaultInterruptedAutomaticRun);
            if (command is RetrySequenceCommand retrySequence
                && outcome.Result.IsAccepted
                && string.Equals(
                    _runtimeState.ResetRetrySequenceId,
                    retrySequence.SequenceId,
                    StringComparison.Ordinal))
            {
                _runtimeState.ClearResetRetrySequence();
                if (state.AutomaticRunActive)
                {
                    _runtimeState.MarkAutomaticExternalInspectionRearmRequired();
                }
            }
            if (command is AbortSequenceCommand
                && _runtimeState.AutomaticExternalInspectionEnabled)
            {
                CloseAutomaticExternalInspectionAfterAbort();
            }
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
        if (command is StartAutomaticRunCommand startCommand
            && startCommand.WaitForExternalResult
            && !_runtimeState.HasArmedAutomaticExternalInspection(
                AutomaticRunRuntime.Configuration?.SequenceId ?? string.Empty))
        {
            return Reject(
                command,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                "Automatic external inspection must be armed with preflighted frame sources before the run starts.");
        }

        var outcome = _automaticRunCommandHandler.Apply(
            command,
            new SimulationAutomaticRunCommandContext(
                AutomaticRunRuntime.Configuration,
                AutomaticRunRuntime.CreateCommandState(
                    _runMode,
                    _controlOwner,
                    _pendingSteps,
                    _activeSequenceId),
                SignalHub,
                SequenceRuntime.SequenceExecutors,
                CommandBoundaryTick,
                CommandBoundaryTime));
        if (outcome.State is { } state)
        {
            _runMode = state.RunMode;
            _controlOwner = state.ControlOwner;
            _pendingSteps = state.PendingSteps;
            _activeSequenceId = state.ActiveSequenceId;
            AutomaticRunRuntime.ApplyCommandState(state);
            if (command is StartAutomaticRunCommand acceptedStartCommand
                && acceptedStartCommand.WaitForExternalResult
                && outcome.Result.IsAccepted)
            {
                _runtimeState.BeginAutomaticExternalInspection(
                    state.ActiveSequenceId
                        ?? throw new InvalidOperationException(
                            "An accepted automatic external run must have an active sequence."),
                    acceptedStartCommand.BeginRealTime);
            }
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
        var eventTick = _runtimeState.NextTickIndex;
        var eventTime = _runtimeState.NextSimulationTime;

        AdvanceConditionScenario(eventTick, eventTime);

        IReadOnlySet<string>? blockedCylinderIds = MachineLayout is null
            ? null
            : FaultRuntime.Values
                    .Where(fault => fault.Kind == SimulationFaultKind.CylinderTravelBlocked)
                .Select(fault => fault.TargetId)
                .ToHashSet(StringComparer.Ordinal);
        _physicalRuntimeTick.Advance(
            new SimulationPhysicalRuntimeTickContext(
                _settings.FixedStep,
                eventTick,
                eventTime,
                Axes,
                MachineLayout,
                blockedCylinderIds,
                Cameras));

        var pausedForAutomaticExternalInspection =
            PauseForAutomaticExternalInspectionIfNeeded(eventTick, eventTime);

        AdvanceAutomaticRunRepeat(eventTick, eventTime);

        if (!pausedForAutomaticExternalInspection
            && _activeSequenceId is not null
            && SequenceRuntime.SequenceExecutors.TryGetValue(_activeSequenceId, out var executor)
            && executor.CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            var context = new DeterministicSequenceRuntimeContext(
                SignalHub,
                Axes,
                Cameras,
                eventTick,
                eventTime,
                EmitSequenceRuntimeEvent,
                _runtimeState.AutomaticExternalInspectionEnabled,
                _runtimeState.AutomaticExternalSources);
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

        _runtimeState.AdvanceTick();
        PublishSnapshot();
    }

    private bool PauseForAutomaticExternalInspectionIfNeeded(
        long eventTick,
        TimeSpan eventTime)
    {
        if (!_runtimeState.AutomaticExternalInspectionEnabled
            || !AutomaticRunRuntime.IsActive
            || _runMode == SimulationRunMode.Paused
            || _runtimeState.AutomaticExternalRequestPublished)
        {
            return false;
        }

        var camera = Cameras.FirstOrDefault(candidate =>
            candidate.State == VirtualCameraState.AwaitingExternalResult);
        if (camera is null)
        {
            return false;
        }

        _runMode = SimulationRunMode.Paused;
        if (!SequenceRuntime.TryGetCurrentVisionWaitTimeout(
                _activeSequenceId,
                out var sequenceId,
                out var stepId,
                out var simulationTimeout))
        {
            FailAutomaticExternalInspectionConfiguration(
                eventTick,
                eventTime,
                camera.Id,
                "The active automatic Sequence is not waiting on a positively timed WaitVisionResult step.");
            return true;
        }

        _runtimeState.MarkAutomaticExternalRequestPublished(simulationTimeout);
        Emit(
            "Vision",
            "AutomaticExternalInspectionRequestReady",
            $"Automatic external inspection is waiting for {camera.Id} frame " +
            $"{camera.CaptureSnapshot().CurrentAcquisitionId}; sequence={sequenceId}; " +
            $"step={stepId}; simulationTimeoutMs={FormatMilliseconds(simulationTimeout)}.",
            tickIndex: eventTick,
            simulationTime: eventTime);
        return true;
    }

    private void AdvanceConditionScenario(long eventTick, TimeSpan eventTime)
    {
        if (!ConditionScenarioRuntime.IsActive)
        {
            return;
        }

        var scenarioTick = ConditionScenarioRuntime.ExecutedTicks;
        AdvanceConditionScheduledFault(scenarioTick, eventTick, eventTime);
        foreach (var operationEvent in ConditionScenarioRuntime.Advance())
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
        var schedule = ConditionScenarioRuntime.Profile?.FaultRecovery;
        if (schedule is null)
        {
            return;
        }

        if (scenarioTick == schedule.InjectTick)
        {
            _runtimeState.SetCommandBoundary(eventTick, eventTime);
            var outcome = _conditionScheduledFaultInjectionHandler.Apply(
                new SimulationConditionScheduledFaultInjectionContext(
                    schedule,
                    scenarioTick,
                    Axes,
                    SignalHub,
                    MachineLayout,
                    FaultRuntime,
                    _faultCommandHandler,
                    CommandBoundaryTick,
                    CommandBoundaryTime));
            ConditionScenarioRuntime.ApplyScheduledFaultInjectionOutcome(outcome);
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

        if (ConditionScenarioRuntime.ScheduledFaultActive
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
        var schedule = ConditionScenarioRuntime.Profile?.FaultRecovery;
        if (!ConditionScenarioRuntime.ScheduledFaultActive || schedule is null)
        {
            return;
        }

        _runtimeState.SetCommandBoundary(eventTick, eventTime);
        var outcome = _conditionScheduledFaultRecoveryHandler.Apply(
            CreateConditionScheduledFaultRecoveryContext(restartSequence, commandId));
        if (outcome.State is { } state)
        {
            ConditionScenarioRuntime.ApplyScheduledFaultRecoveryState(
                state.ScheduledFaultActive,
                state.InterruptedAutomaticRun);
            _activeSequenceId = state.ActiveSequenceId;
            _controlOwner = state.ControlOwner;
            AutomaticRunRuntime.ApplyRecoveryState(state);
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
            ConditionScenarioRuntime.Profile?.FaultRecovery,
            restartSequence,
            commandId,
            new SimulationConditionScheduledFaultRecoveryState(
                ConditionScenarioRuntime.ScheduledFaultActive,
                ConditionScenarioRuntime.ScheduledFaultInterruptedAutomaticRun,
                _activeSequenceId,
                _controlOwner,
                AutomaticRunRuntime.IsActive,
                AutomaticRunRuntime.WaitingForRepeat,
                AutomaticRunRuntime.RemainingDelayTicks),
            Axes,
            SignalHub,
            MachineLayout,
            FaultRuntime,
            SequenceRuntime.SequenceExecutors,
            _faultCommandHandler,
            CommandBoundaryTick,
            CommandBoundaryTime);

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
        AutomaticRunRuntime.CreateCycleContext(_activeSequenceId, SequenceRuntime.SequenceExecutors);

    private void ApplyAutomaticRunCycleOutcome(
        SimulationAutomaticRunCycleOutcome outcome,
        long eventTick,
        TimeSpan eventTime)
    {
        if (outcome.State is { } state)
        {
            _activeSequenceId = state.ActiveSequenceId;
            AutomaticRunRuntime.ApplyCycleState(state);
            if (!state.AutomaticRunActive)
            {
                _runtimeState.ClearAutomaticExternalInspection(clearSources: true);
            }
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
        if (!AutomaticRunRuntime.IsActive)
        {
            return;
        }

        ConditionScenarioRuntime.CaptureAutomaticRunInterruption(_activeSequenceId);

        AutomaticRunRuntime.MarkFaulted();
        _runtimeState.ClearAutomaticExternalInspection(clearSources: true);
        Emit(
            "AutomaticRun",
            "AutomaticRunFaulted",
            detail ?? "The automatic sequence faulted.",
            tickIndex: eventTick,
            simulationTime: eventTime);
    }

    private void ResetRuntime()
    {
        _runtimeState.Reset();
    }

    private SimulationSnapshot CreateSnapshot() => _runtimeState.CreateSnapshot();

    private void AdvancePickPlaceWorkpiece(long eventTick, TimeSpan eventTime)
    {
        var result = _runtimeState.AdvancePickPlaceWorkpiece();
        if (result is null)
        {
            return;
        }

        var (transition, workpieceId) = result.Value;
        var code = transition.CurrentState == PickPlaceWorkpieceState.Attached
            ? "WorkpieceAttached"
            : "WorkpiecePlaced";
        Emit(
            "Workpiece",
            code,
            FormattableString.Invariant(
                $"{workpieceId}: {transition.PreviousState} -> {transition.CurrentState} at X {transition.X:F3}, Y {transition.Y:F3}."),
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
            tickIndex ?? TickIndex,
            simulationTime ?? Clock.Time,
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
            CommandBoundaryTick,
            CommandBoundaryTime);
    }

    private SimulationEngineTerminationResult CreateTerminationResult(
        SimulationEngineTerminationOutcome outcome,
        Exception? exception,
        string? currentCommandId,
        string? operation) =>
        new(
            outcome,
            TickIndex,
            Clock.Time,
            exception,
            currentCommandId,
            operation);

    private void InjectFault(SimulationEngineFaultPoint faultPoint) =>
        _faultInjector?.Invoke(faultPoint);

    private SimulationCommandResult Accept(SimulationCommand command, string detail) =>
        SimulationCommandResult.Accepted(command, CommandBoundaryTick, CommandBoundaryTime, detail);

    private SimulationCommandResult Reject(
        SimulationCommand command,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        SimulationCommandResult.Rejected(
            command,
            CommandBoundaryTick,
            CommandBoundaryTime,
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
