using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Owns the mutable deterministic simulation state used by the engine.
///
/// The engine still decides command and tick order. This owner keeps the
/// runtime aggregate together so configuration, reset, snapshot, identity,
/// and clock transitions cannot accidentally update only part of that state.
/// </summary>
internal sealed class SimulationRuntimeState
{
    private readonly SimulationClock _clock;
    private readonly SimulationRuntimeConfigurationBuilder _configurationBuilder;
    private readonly List<ServoAxisComponent> _axes = new();
    private readonly List<DeterministicVirtualCamera> _cameras = new();
    private readonly SimulationSequenceRuntime _sequenceRuntime = new();
    private readonly SimulationConditionScenarioRuntime _conditionScenarioRuntime = new();
    private readonly SimulationAutomaticRunRuntime _automaticRunRuntime = new();
    private readonly SimulationFaultRuntime _faultRuntime = new();
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
    private string? _projectId;
    private long _runtimeGeneration;
    private string? _resetRetrySequenceId;
    private bool _automaticExternalInspectionRearmRequired;
    private readonly Dictionary<string, VirtualCameraExternalSource> _automaticExternalSources =
        new(StringComparer.Ordinal);
    private string? _automaticExternalSequenceId;
    private bool _automaticExternalInspectionEnabled;
    private bool _automaticExternalRequestPublished;
    private bool _automaticExternalResumeRealTime;
    private TimeSpan _automaticExternalInspectionWaitElapsed;
    private TimeSpan? _automaticExternalInspectionSimulationTimeout;
    private AutomaticExternalInspectionClosure? _automaticExternalInspectionClosure;

    internal SimulationRuntimeState(TimeSpan fixedStep, double timeScale)
    {
        _clock = new SimulationClock(fixedStep);
        _configurationBuilder = new SimulationRuntimeConfigurationBuilder(fixedStep);
        _timeScale = timeScale;
        _signalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
    }

    internal SimulationClock Clock => _clock;
    internal TimeSpan FixedStep => _clock.FixedStep;
    internal TimeSpan SimulationTime => _clock.Time;
    internal double TimeScale
    {
        get => _timeScale;
        set => _timeScale = value;
    }

    internal List<ServoAxisComponent> Axes => _axes;
    internal List<DeterministicVirtualCamera> Cameras => _cameras;
    internal SimulationSequenceRuntime SequenceRuntime => _sequenceRuntime;
    internal SimulationConditionScenarioRuntime ConditionScenarioRuntime => _conditionScenarioRuntime;
    internal SimulationAutomaticRunRuntime AutomaticRunRuntime => _automaticRunRuntime;
    internal SimulationFaultRuntime FaultRuntime => _faultRuntime;
    internal DeterministicSignalHub SignalHub => _signalHub;
    internal DeterministicMachineLayout? MachineLayout => _machineLayout;
    internal DeterministicPickPlaceWorkpiece? PickPlaceWorkpiece => _pickPlaceWorkpiece;

    internal SimulationRunMode RunMode
    {
        get => _runMode;
        set => _runMode = value;
    }

    internal SimulationControlOwner ControlOwner
    {
        get => _controlOwner;
        set => _controlOwner = value;
    }

    internal string? ActiveSequenceId
    {
        get => _activeSequenceId;
        set => _activeSequenceId = value;
    }

    internal int PendingSteps
    {
        get => _pendingSteps;
        set => _pendingSteps = value;
    }

    internal long TickIndex => _tickIndex;
    internal long CommandBoundaryTick => _commandBoundaryTick;
    internal TimeSpan CommandBoundaryTime => _commandBoundaryTime;
    internal string? ProjectId => _projectId;
    internal long RuntimeGeneration => _runtimeGeneration;
    internal string? ResetRetrySequenceId => _resetRetrySequenceId;
    internal bool AutomaticExternalInspectionRearmRequired =>
        _automaticExternalInspectionRearmRequired;
    internal bool AutomaticExternalInspectionEnabled => _automaticExternalInspectionEnabled;
    internal bool AutomaticExternalRequestPublished => _automaticExternalRequestPublished;
    internal bool AutomaticExternalResumeRealTime => _automaticExternalResumeRealTime;
    internal bool IsAutomaticExternalInspectionWaiting =>
        _automaticExternalInspectionEnabled && _automaticExternalRequestPublished;
    internal TimeSpan AutomaticExternalInspectionWaitElapsed => _automaticExternalInspectionWaitElapsed;
    internal TimeSpan? AutomaticExternalInspectionSimulationTimeout =>
        _automaticExternalInspectionSimulationTimeout;
    internal IReadOnlyDictionary<string, VirtualCameraExternalSource> AutomaticExternalSources =>
        _automaticExternalSources;

    internal long NextTickIndex => _tickIndex + 1;
    internal TimeSpan NextSimulationTime => _clock.Time + _clock.FixedStep;

    internal void AddAxis(ServoAxisComponent axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (_axes.Any(existing => string.Equals(existing.Id, axis.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Axis id '{axis.Id}' is duplicated.", nameof(axis));
        }

        _axes.Add(axis);
    }

    internal bool TryApplyRuntimeConfiguration(
        SimulationRuntimeConfiguration configuration,
        string? projectId,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!_configurationBuilder.TryBuild(
                configuration,
                out var candidate,
                out error))
        {
            return false;
        }

        SimulationRuntimeConfigurationBuildResult runtime = candidate!;
        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _sequenceRuntime.DebugState.ClearPendingSemanticStep();
        _sequenceRuntime.DebugState.SetPause(SequenceDebugPauseReason.None, null);
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
        ClearAutomaticExternalInspection(clearSources: true);
        _automaticExternalInspectionClosure = null;
        _resetRetrySequenceId = null;
        _automaticExternalInspectionRearmRequired = false;
        _sequenceRuntime.Configure(runtime.CompiledSequences, runtime.SequenceExecutors);
        _automaticRunRuntime.Configure(
            configuration.AutomaticRun,
            runtime.AutomaticRunRepeatDelayTicks);
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;
        _projectId = projectId;
        _runtimeGeneration++;
        error = string.Empty;
        return true;
    }

    internal bool TryApplyAxisConfiguration(
        IReadOnlyList<AxisConfiguration> configurations,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if (!_configurationBuilder.TryCreateAxes(configurations, out var axes, out error))
        {
            return false;
        }

        _runMode = SimulationRunMode.Paused;
        _pendingSteps = 0;
        _sequenceRuntime.DebugState.ClearPendingSemanticStep();
        _sequenceRuntime.DebugState.SetPause(SequenceDebugPauseReason.None, null);
        _clock.Reset();
        _tickIndex = 0;
        _axes.Clear();
        _axes.AddRange(axes);
        _cameras.Clear();
        _signalHub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
        _machineLayout = null;
        _pickPlaceWorkpiece = null;
        _faultRuntime.Clear();
        _conditionScenarioRuntime.Clear();
        ClearAutomaticExternalInspection(clearSources: true);
        _automaticExternalInspectionClosure = null;
        _resetRetrySequenceId = null;
        _automaticExternalInspectionRearmRequired = false;
        _sequenceRuntime.ClearConfiguration();
        _automaticRunRuntime.Configure(null, repeatDelayTicks: 0);
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;
        _projectId = null;
        _runtimeGeneration++;
        return true;
    }

    internal void Reset()
    {
        var resetRetrySequenceId = _resetRetrySequenceId;
        if (resetRetrySequenceId is null
            && _activeSequenceId is { } activeSequenceId
            && _sequenceRuntime.SequenceExecutors.TryGetValue(activeSequenceId, out var activeExecutor)
            && activeExecutor.CaptureSnapshot().Status == SequenceExecutionStatus.Faulted)
        {
            resetRetrySequenceId = activeSequenceId;
        }

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
        ClearAutomaticExternalInspection(clearSources: true);
        _automaticExternalInspectionClosure = null;
        _resetRetrySequenceId = resetRetrySequenceId;
        _automaticExternalInspectionRearmRequired = false;
        _activeSequenceId = null;
        _controlOwner = SimulationControlOwner.Definition;
        _runtimeGeneration++;
    }

    internal void SetCommandBoundary()
    {
        _commandBoundaryTick = _tickIndex;
        _commandBoundaryTime = _clock.Time;
    }

    internal void SetCommandBoundary(long tickIndex, TimeSpan simulationTime)
    {
        _commandBoundaryTick = tickIndex;
        _commandBoundaryTime = simulationTime;
    }

    internal bool Matches(SimulationRuntimeIdentity expected) =>
        expected.RuntimeGeneration == _runtimeGeneration
        && string.Equals(expected.ProjectId, _projectId, StringComparison.Ordinal);

    internal bool TryArmAutomaticExternalInspection(
        string sequenceId,
        IReadOnlyDictionary<string, VirtualCameraExternalSource> sources,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(sequenceId))
        {
            error = "Automatic external inspection requires a sequence id.";
            return false;
        }

        if (sources.Count == 0)
        {
            error = "Automatic external inspection requires at least one camera source.";
            return false;
        }

        foreach (var cameraId in sources.Keys)
        {
            if (!_cameras.Any(camera => string.Equals(camera.Id, cameraId, StringComparison.Ordinal)))
            {
                error = $"Virtual camera '{cameraId}' was not found.";
                return false;
            }
        }

        _automaticExternalSources.Clear();
        foreach (var (cameraId, source) in sources)
        {
            _automaticExternalSources.Add(cameraId, source);
        }

        _automaticExternalSequenceId = sequenceId;
        _automaticExternalInspectionEnabled = false;
        _automaticExternalRequestPublished = false;
        _automaticExternalResumeRealTime = false;
        _automaticExternalInspectionWaitElapsed = TimeSpan.Zero;
        _automaticExternalInspectionSimulationTimeout = null;
        _automaticExternalInspectionClosure = null;
        _automaticExternalInspectionRearmRequired = false;
        error = string.Empty;
        return true;
    }

    internal void MarkExternalInspectionFailureForRetry(string sequenceId)
    {
        if (!string.IsNullOrWhiteSpace(sequenceId))
        {
            _resetRetrySequenceId = sequenceId;
        }
    }

    internal void ClearResetRetrySequence() => _resetRetrySequenceId = null;

    internal void MarkAutomaticExternalInspectionRearmRequired() =>
        _automaticExternalInspectionRearmRequired = true;

    internal bool HasArmedAutomaticExternalInspection(string sequenceId) =>
        string.Equals(_automaticExternalSequenceId, sequenceId, StringComparison.Ordinal)
        && _automaticExternalSources.Count > 0;

    internal void BeginAutomaticExternalInspection(string sequenceId, bool resumeRealTime)
    {
        if (!HasArmedAutomaticExternalInspection(sequenceId))
        {
            throw new InvalidOperationException(
                $"Automatic external inspection for sequence '{sequenceId}' was not armed.");
        }

        _automaticExternalInspectionEnabled = true;
        _automaticExternalRequestPublished = false;
        _automaticExternalResumeRealTime = resumeRealTime;
        _automaticExternalInspectionWaitElapsed = TimeSpan.Zero;
        _automaticExternalInspectionSimulationTimeout = null;
    }

    internal void MarkAutomaticExternalRequestPublished(TimeSpan simulationTimeout)
    {
        if (simulationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationTimeout));
        }

        _automaticExternalInspectionWaitElapsed = TimeSpan.Zero;
        _automaticExternalInspectionSimulationTimeout = simulationTimeout;
        _automaticExternalRequestPublished = true;
    }

    internal void ClearAutomaticExternalRequestPublished()
    {
        _automaticExternalRequestPublished = false;
        _automaticExternalInspectionWaitElapsed = TimeSpan.Zero;
        _automaticExternalInspectionSimulationTimeout = null;
    }

    internal void ClearAutomaticExternalInspection(bool clearSources = false)
    {
        _automaticExternalInspectionEnabled = false;
        _automaticExternalRequestPublished = false;
        _automaticExternalResumeRealTime = false;
        _automaticExternalInspectionWaitElapsed = TimeSpan.Zero;
        _automaticExternalInspectionSimulationTimeout = null;
        _automaticExternalInspectionRearmRequired = false;
        if (clearSources)
        {
            _automaticExternalSequenceId = null;
            _automaticExternalSources.Clear();
        }
    }

    internal bool AdvanceAutomaticExternalInspectionWait(TimeSpan elapsed)
    {
        if (!IsAutomaticExternalInspectionWaiting || elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        _automaticExternalInspectionWaitElapsed += elapsed;
        return _automaticExternalInspectionSimulationTimeout is { } timeout
            && _automaticExternalInspectionWaitElapsed >= timeout;
    }

    internal void RecordAutomaticExternalInspectionClosure(
        AutomaticExternalInspectionClosure closure)
    {
        ArgumentNullException.ThrowIfNull(closure);
        _automaticExternalInspectionClosure = closure;
    }

    internal bool TryGetAutomaticExternalInspectionClosure(
        string sequenceId,
        string cameraId,
        string acquisitionId,
        string frameId,
        out AutomaticExternalInspectionClosure closure)
    {
        if (_automaticExternalInspectionClosure is { } candidate
            // The automatic runner can use a generated sequence alias while
            // the published integration context keeps the authored sequence.
            // Camera + acquisition + frame are the stable identity of the
            // closed request, so do not discard a late Result solely because
            // those descriptive sequence ids differ.
            && string.Equals(candidate.CameraId, cameraId, StringComparison.Ordinal)
            && string.Equals(candidate.AcquisitionId, acquisitionId, StringComparison.Ordinal)
            && string.Equals(candidate.FrameId, frameId, StringComparison.Ordinal))
        {
            closure = candidate;
            return true;
        }

        closure = null!;
        return false;
    }

    internal void AdvanceTick()
    {
        _clock.Advance();
        _tickIndex++;
    }

    internal SimulationSnapshot CreateSnapshot() =>
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
                _sequenceRuntime.DebugState.CreateSnapshot(),
                _projectId,
                _runtimeGeneration,
                _resetRetrySequenceId));

    internal (PickPlaceWorkpieceTransition Transition, string WorkpieceId)?
        AdvancePickPlaceWorkpiece()
    {
        if (_pickPlaceWorkpiece is null)
        {
            return null;
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
        var transition = _pickPlaceWorkpiece.Tick(x, y, gripper.Value == true);
        return transition is null
            ? null
            : (transition, _pickPlaceWorkpiece.CaptureSnapshot().Id);
    }
}

internal enum AutomaticExternalInspectionClosureReason
{
    WallTimeout,
    SimulationTimeout,
    Aborted
}

internal sealed record AutomaticExternalInspectionClosure(
    string SequenceId,
    string StepId,
    string CameraId,
    string AcquisitionId,
    string FrameId,
    AutomaticExternalInspectionClosureReason Reason,
    TimeSpan WaitElapsed,
    TimeSpan? Timeout);
