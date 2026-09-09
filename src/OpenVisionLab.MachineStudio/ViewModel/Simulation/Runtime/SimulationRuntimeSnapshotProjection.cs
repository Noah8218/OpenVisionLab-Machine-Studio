using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SimulationRuntimeProjectionSelection(
    LayoutComponentKind? SelectedLayoutComponentKind,
    string? SelectedLayoutBindingId,
    string? SelectedTreeAxisId,
    string? PreferredCameraId,
    SimulationFaultKind ScheduledFaultKind,
    string? ActiveSequenceId);

/// <summary>
/// Owns the pure conversion from one immutable simulation snapshot and shell
/// selection context to the runtime values consumed by MainViewModel.
/// </summary>
internal sealed record SimulationRuntimeSnapshotProjection(
    TimeSpan SimulationTime,
    long TickIndex,
    SimulationRunMode RunMode,
    SimulationControlOwner ControlOwner,
    bool IsRunning,
    AxisSnapshot? CurrentAxis,
    VirtualCameraSnapshot? CurrentCamera,
    SequenceExecutionSnapshot? CurrentSequence,
    AutomaticRunSnapshot AutomaticRun,
    DeterministicConditionScenarioSnapshot ConditionScenario,
    bool? CycleStartInput,
    bool? CycleActiveOutput,
    bool? CycleDoneOutput,
    IReadOnlyList<string> ScenarioTargetIds,
    IReadOnlyList<string> FinalEquipmentTargetIds,
    IReadOnlyList<string> ScheduledFaultTargetIds,
    IReadOnlyList<string> RecoverySequenceIds)
{
    internal static SimulationRuntimeSnapshotProjection Empty { get; } = new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Definition,
        false,
        null,
        null,
        null,
        AutomaticRunSnapshot.NotConfigured,
        DeterministicConditionScenarioSnapshot.NotConfigured,
        null,
        null,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    internal static SimulationRuntimeSnapshotProjection Create(
        SimulationSnapshot snapshot,
        SimulationRuntimeProjectionSelection selection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);

        string[] equipmentTargetIds = snapshot.Axes
            .Select(axis => axis.Id)
            .Concat(snapshot.LayoutComponents.Select(component => component.Id))
            .ToArray();
        string[] scheduledFaultTargetIds = new SimulationFaultTargetCatalog()
            .GetTargets(snapshot, selection.ScheduledFaultKind)
            .Select(target => target.Id)
            .ToArray();

        return new(
            snapshot.SimulationTime,
            snapshot.TickIndex,
            snapshot.RunMode,
            snapshot.ControlOwner,
            snapshot.RunMode is SimulationRunMode.RealTime
                or SimulationRunMode.FastForward
                or SimulationRunMode.SequenceStep,
            SelectAxis(snapshot, selection),
            snapshot.Cameras.FirstOrDefault(camera =>
                camera.Id == selection.PreferredCameraId)
                ?? snapshot.Cameras.FirstOrDefault(),
            FindActiveSequence(snapshot, selection.ActiveSequenceId),
            snapshot.AutomaticRun,
            snapshot.ConditionScenario,
            ReadSignal(snapshot, "di.cycle-start"),
            ReadSignal(snapshot, "do.cycle-active"),
            ReadSignal(snapshot, "do.cycle-done"),
            equipmentTargetIds,
            equipmentTargetIds,
            scheduledFaultTargetIds,
            snapshot.Sequences.Select(sequence => sequence.SequenceId).ToArray());
    }

    internal static AxisSnapshot? SelectAxis(
        SimulationSnapshot snapshot,
        SimulationRuntimeProjectionSelection selection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.SelectedLayoutComponentKind is
            LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            return snapshot.Axes.FirstOrDefault(axis =>
                axis.Id == selection.SelectedLayoutBindingId);
        }

        return snapshot.Axes.FirstOrDefault(axis =>
                axis.Id == selection.SelectedTreeAxisId)
            ?? snapshot.Axes.FirstOrDefault();
    }

    internal static SequenceExecutionSnapshot? FindActiveSequence(
        SimulationSnapshot snapshot,
        string? activeSequenceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return activeSequenceId is null
            ? null
            : snapshot.Sequences.FirstOrDefault(sequence =>
                string.Equals(
                    sequence.SequenceId,
                    activeSequenceId,
                    StringComparison.Ordinal));
    }

    internal static bool? ReadSignal(SimulationSnapshot snapshot, string id) =>
        snapshot.Signals.FirstOrDefault(signal =>
            string.Equals(signal.Id, id, StringComparison.Ordinal))?.Value;
}
