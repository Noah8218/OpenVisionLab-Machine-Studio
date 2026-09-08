using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Snapshots;

/// <summary>
/// Composes the current deterministic runtime sources into one immutable
/// simulation snapshot without owning runtime state.
/// </summary>
internal static class SimulationSnapshotFactory
{
    internal static SimulationSnapshot Create(SimulationSnapshotFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var signals = context.SignalHub.CaptureSnapshot();
        return new SimulationSnapshot(
            context.SimulationTime,
            context.TickIndex,
            context.RunMode,
            context.ControlOwner,
            context.TimeScale,
            context.Axes.Select(axis => axis.CreateSnapshot()),
            signals.Revision,
            signals.Signals,
            context.SequenceExecutors
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.CaptureSnapshot()),
            context.Cameras
                .OrderBy(camera => camera.Id, StringComparer.Ordinal)
                .Select(camera => camera.CaptureSnapshot()),
            context.AutomaticRun,
            context.MachineLayout is null
                ? Array.Empty<LayoutComponentSnapshot>()
                : context.MachineLayout.CaptureSnapshots(),
            context.ActiveFaults,
            context.ConditionScenario,
            context.PickPlaceWorkpiece is null
                ? Array.Empty<PickPlaceWorkpieceSnapshot>()
                : new[] { context.PickPlaceWorkpiece.CaptureSnapshot() },
            context.MachineLayout is null
                ? Array.Empty<LoadLockSnapshot>()
                : context.MachineLayout.CaptureLoadLockSnapshots(),
            context.MachineLayout is null
                ? Array.Empty<WaferHandlerSnapshot>()
                : context.MachineLayout.CaptureWaferHandlerSnapshots(),
            context.MachineLayout is null
                ? Array.Empty<InspectionSortRouterSnapshot>()
                : context.MachineLayout.CaptureInspectionSortRouterSnapshots(),
            context.MachineLayout is null
                ? Array.Empty<InspectionHandoffSnapshot>()
                : context.MachineLayout.CaptureInspectionHandoffSnapshots(),
            context.MachineLayout is null
                ? Array.Empty<OhtHandoffSnapshot>()
                : context.MachineLayout.CaptureOhtHandoffSnapshots(),
            context.MachineLayout is null
                ? Array.Empty<PrealignerSnapshot>()
                : context.MachineLayout.CapturePrealignerSnapshots(),
            context.SequenceDebug,
            analogSignals: signals.AnalogSignals,
            projectId: context.ProjectId,
            runtimeGeneration: context.RuntimeGeneration);
    }
}

internal sealed record SimulationSnapshotFactoryContext(
    TimeSpan SimulationTime,
    long TickIndex,
    SimulationRunMode RunMode,
    SimulationControlOwner ControlOwner,
    double TimeScale,
    IReadOnlyList<ServoAxisComponent> Axes,
    DeterministicSignalHub SignalHub,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    IReadOnlyList<DeterministicVirtualCamera> Cameras,
    AutomaticRunSnapshot AutomaticRun,
    DeterministicMachineLayout? MachineLayout,
    IEnumerable<SimulationFaultSnapshot> ActiveFaults,
    DeterministicConditionScenarioSnapshot ConditionScenario,
    DeterministicPickPlaceWorkpiece? PickPlaceWorkpiece,
    SequenceDebugSnapshot SequenceDebug,
    string? ProjectId = null,
    long RuntimeGeneration = 0);
