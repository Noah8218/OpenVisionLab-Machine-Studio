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
                _runtimeGeneration));

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
