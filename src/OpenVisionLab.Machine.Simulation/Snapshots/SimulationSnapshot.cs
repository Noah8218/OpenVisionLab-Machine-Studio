using System.Collections.Immutable;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Snapshots;

public sealed class SimulationSnapshot
{
    public TimeSpan SimulationTime { get; }
    public long TickIndex { get; }
    public SimulationRunMode RunMode { get; }
    public SimulationControlOwner ControlOwner { get; }
    public double TimeScale { get; }
    public IReadOnlyList<Axis.AxisSnapshot> Axes { get; }
    public long SignalRevision { get; }
    public IReadOnlyList<DigitalSignalSnapshot> Signals { get; }
    public IReadOnlyList<AnalogSignalSnapshot> AnalogSignals { get; }
    public IReadOnlyList<SequenceExecutionSnapshot> Sequences { get; }
    public IReadOnlyList<VirtualCameraSnapshot> Cameras { get; }
    public AutomaticRunSnapshot AutomaticRun { get; }
    public IReadOnlyList<LayoutComponentSnapshot> LayoutComponents { get; }
    public IReadOnlyList<SimulationFaultSnapshot> Faults { get; }
    public DeterministicConditionScenarioSnapshot ConditionScenario { get; }
    public IReadOnlyList<PickPlaceWorkpieceSnapshot> Workpieces { get; }
    public IReadOnlyList<LoadLockSnapshot> LoadLocks { get; }
    public IReadOnlyList<WaferHandlerSnapshot> WaferHandlers { get; }
    public IReadOnlyList<InspectionSortRouterSnapshot> InspectionSortRouters { get; }
    public IReadOnlyList<InspectionHandoffSnapshot> InspectionHandoffs { get; }
    public IReadOnlyList<OhtHandoffSnapshot> OhtHandoffs { get; }
    public IReadOnlyList<PrealignerSnapshot> Prealigners { get; }
    public SequenceDebugSnapshot SequenceDebug { get; }

    public SimulationSnapshot(
        TimeSpan simulationTime,
        long tickIndex,
        SimulationRunMode runMode,
        SimulationControlOwner controlOwner,
        double timeScale,
        IEnumerable<Axis.AxisSnapshot> axes,
        long signalRevision,
        IEnumerable<DigitalSignalSnapshot> signals,
        IEnumerable<SequenceExecutionSnapshot> sequences)
        : this(
            simulationTime,
            tickIndex,
            runMode,
            controlOwner,
            timeScale,
            axes,
            signalRevision,
            signals,
            sequences,
            Array.Empty<VirtualCameraSnapshot>(),
            AutomaticRunSnapshot.NotConfigured,
            Array.Empty<LayoutComponentSnapshot>())
    {
    }

    public SimulationSnapshot(
        TimeSpan simulationTime,
        long tickIndex,
        SimulationRunMode runMode,
        SimulationControlOwner controlOwner,
        double timeScale,
        IEnumerable<Axis.AxisSnapshot> axes,
        long signalRevision,
        IEnumerable<DigitalSignalSnapshot> signals,
        IEnumerable<SequenceExecutionSnapshot> sequences,
        IEnumerable<VirtualCameraSnapshot> cameras)
        : this(
            simulationTime,
            tickIndex,
            runMode,
            controlOwner,
            timeScale,
            axes,
            signalRevision,
            signals,
            sequences,
            cameras,
            AutomaticRunSnapshot.NotConfigured,
            Array.Empty<LayoutComponentSnapshot>())
    {
    }

    public SimulationSnapshot(
        TimeSpan simulationTime,
        long tickIndex,
        SimulationRunMode runMode,
        SimulationControlOwner controlOwner,
        double timeScale,
        IEnumerable<Axis.AxisSnapshot> axes,
        long signalRevision,
        IEnumerable<DigitalSignalSnapshot> signals,
        IEnumerable<SequenceExecutionSnapshot> sequences,
        IEnumerable<VirtualCameraSnapshot> cameras,
        AutomaticRunSnapshot automaticRun)
        : this(
            simulationTime,
            tickIndex,
            runMode,
            controlOwner,
            timeScale,
            axes,
            signalRevision,
            signals,
            sequences,
            cameras,
            automaticRun,
            Array.Empty<LayoutComponentSnapshot>())
    {
    }

    public SimulationSnapshot(
        TimeSpan simulationTime,
        long tickIndex,
        SimulationRunMode runMode,
        SimulationControlOwner controlOwner,
        double timeScale,
        IEnumerable<Axis.AxisSnapshot> axes,
        long signalRevision,
        IEnumerable<DigitalSignalSnapshot> signals,
        IEnumerable<SequenceExecutionSnapshot> sequences,
        IEnumerable<VirtualCameraSnapshot> cameras,
        AutomaticRunSnapshot automaticRun,
        IEnumerable<LayoutComponentSnapshot> layoutComponents,
        IEnumerable<SimulationFaultSnapshot>? faults = null,
        DeterministicConditionScenarioSnapshot? conditionScenario = null,
        IEnumerable<PickPlaceWorkpieceSnapshot>? workpieces = null,
        IEnumerable<LoadLockSnapshot>? loadLocks = null,
        IEnumerable<WaferHandlerSnapshot>? waferHandlers = null,
        IEnumerable<InspectionSortRouterSnapshot>? inspectionSortRouters = null,
        IEnumerable<InspectionHandoffSnapshot>? inspectionHandoffs = null,
        IEnumerable<OhtHandoffSnapshot>? ohtHandoffs = null,
        IEnumerable<PrealignerSnapshot>? prealigners = null,
        SequenceDebugSnapshot? sequenceDebug = null,
        IEnumerable<AnalogSignalSnapshot>? analogSignals = null)
    {
        SimulationTime = simulationTime;
        TickIndex = tickIndex;
        RunMode = runMode;
        ControlOwner = controlOwner;
        TimeScale = timeScale;
        Axes = axes.ToImmutableList();
        SignalRevision = signalRevision;
        Signals = signals.ToImmutableList();
        AnalogSignals = (analogSignals ?? Array.Empty<AnalogSignalSnapshot>()).ToImmutableList();
        Sequences = sequences.ToImmutableList();
        Cameras = cameras.ToImmutableList();
        AutomaticRun = automaticRun ?? throw new ArgumentNullException(nameof(automaticRun));
        LayoutComponents = layoutComponents.ToImmutableList();
        Faults = (faults ?? Array.Empty<SimulationFaultSnapshot>())
            .OrderBy(fault => fault.Kind)
            .ThenBy(fault => fault.TargetId, StringComparer.Ordinal)
            .ToImmutableList();
        ConditionScenario = conditionScenario ?? DeterministicConditionScenarioSnapshot.NotConfigured;
        Workpieces = (workpieces ?? Array.Empty<PickPlaceWorkpieceSnapshot>())
            .OrderBy(workpiece => workpiece.Id, StringComparer.Ordinal)
            .ToImmutableList();
        LoadLocks = (loadLocks ?? Array.Empty<LoadLockSnapshot>())
            .OrderBy(loadLock => loadLock.Id, StringComparer.Ordinal)
            .ToImmutableList();
        WaferHandlers = (waferHandlers ?? Array.Empty<WaferHandlerSnapshot>())
            .OrderBy(handler => handler.Id, StringComparer.Ordinal)
            .ToImmutableList();
        InspectionSortRouters = (inspectionSortRouters ?? Array.Empty<InspectionSortRouterSnapshot>())
            .OrderBy(sorter => sorter.Id, StringComparer.Ordinal)
            .ToImmutableList();
        InspectionHandoffs = (inspectionHandoffs ?? Array.Empty<InspectionHandoffSnapshot>())
            .OrderBy(handoff => handoff.Id, StringComparer.Ordinal)
            .ToImmutableList();
        OhtHandoffs = (ohtHandoffs ?? Array.Empty<OhtHandoffSnapshot>())
            .OrderBy(handoff => handoff.Id, StringComparer.Ordinal)
            .ToImmutableList();
        Prealigners = (prealigners ?? Array.Empty<PrealignerSnapshot>())
            .OrderBy(prealigner => prealigner.Id, StringComparer.Ordinal)
            .ToImmutableList();
        SequenceDebug = sequenceDebug ?? SequenceDebugSnapshot.Empty;
    }
}
