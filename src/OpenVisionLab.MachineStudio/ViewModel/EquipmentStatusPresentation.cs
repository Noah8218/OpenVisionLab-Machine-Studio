using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Snapshot-derived operator text for the currently selected layout component.
/// This type presents runtime truth; it never advances or owns equipment state.
/// </summary>
public sealed record EquipmentStatusPresentation(
    string Name,
    string KindText,
    string StateText,
    string ConditionText,
    string PrimaryLabel,
    string PrimaryValue,
    string SecondaryLabel,
    string SecondaryValue,
    bool IsActive,
    bool IsFaulted)
{
    public static EquipmentStatusPresentation Create(
        LayoutItem item,
        SimulationSnapshot snapshot,
        MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(project);

        var runtime = snapshot.LayoutComponents.FirstOrDefault(component =>
            string.Equals(component.Id, item.Id, StringComparison.Ordinal));
        var axis = item.Kind is LayoutItemKind.LinearStage or LayoutItemKind.RotaryStage
            ? snapshot.Axes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, item.BehaviorBindingId, StringComparison.Ordinal))
            : null;
        var fault = snapshot.Faults.FirstOrDefault(candidate =>
            TargetsItem(candidate, item, project));
        var sensorSignal = item.Kind == LayoutItemKind.DigitalSensor
            ? ResolveSensorSignal(runtime, snapshot)
            : null;
        var isFaulted = fault is not null
            || runtime?.CylinderState == PneumaticCylinderState.Fault;

        var presentation = item.Kind switch
        {
            LayoutItemKind.MachineFrame => CreateFrame(item),
            LayoutItemKind.LinearStage => CreateStage(item, axis, "mm"),
            LayoutItemKind.RotaryStage => CreateStage(item, axis, "deg"),
            LayoutItemKind.DigitalSensor => CreateSensor(item, runtime, sensorSignal),
            LayoutItemKind.PneumaticCylinder => CreateCylinder(item, runtime),
            LayoutItemKind.Conveyor => CreateConveyor(item, runtime),
            LayoutItemKind.Workpiece => CreateWorkpiece(item, runtime),
            _ => new EquipmentStatusPresentation(
                item.Name,
                LocalizeLayoutKind(item.Kind),
                OpenVisionLanguageService.T("Equipment.NoRuntimeData"),
                OpenVisionLanguageService.T("Equipment.Unavailable"),
                OpenVisionLanguageService.T("Equipment.Binding"),
                item.BehaviorBindingId ?? OpenVisionLanguageService.T("Equipment.None"),
                OpenVisionLanguageService.T("Equipment.Position"),
                FormatPosition(item.CurrentX, item.CurrentY),
                false,
                false)
        };

        return isFaulted
            ? presentation with
            {
                StateText = fault is null
                    ? OpenVisionLanguageService.T("Equipment.Fault")
                    : FormatFaultKind(fault.Kind),
                ConditionText = OpenVisionLanguageService.T("Equipment.Fault"),
                IsActive = false,
                IsFaulted = true
            }
            : presentation;
    }

    private static EquipmentStatusPresentation CreateFrame(LayoutItem item) =>
        new(
            item.Name,
            OpenVisionLanguageService.T("Equipment.MachineFrame"),
            OpenVisionLanguageService.T("Equipment.Fixed"),
            OpenVisionLanguageService.T("Equipment.Normal"),
            OpenVisionLanguageService.T("Equipment.Size"),
            $"{item.Width:F0} × {item.Height:F0} mm",
            OpenVisionLanguageService.T("Equipment.Position"),
            FormatPosition(item.CurrentX, item.CurrentY),
            false,
            false);

    private static EquipmentStatusPresentation CreateStage(
        LayoutItem item,
        AxisSnapshot? axis,
        string unit) =>
        new(
            item.Name,
            OpenVisionLanguageService.T(
                item.Kind == LayoutItemKind.RotaryStage
                    ? "Equipment.RotaryStageMotor"
                    : "Equipment.LinearStageMotor"),
            axis is null
                ? OpenVisionLanguageService.T("Equipment.NoRuntimeData")
                : LocalizeAxisState(axis.State),
            axis is null
                ? OpenVisionLanguageService.T("Equipment.Unavailable")
                : OpenVisionLanguageService.T("Equipment.Normal"),
            OpenVisionLanguageService.T("Equipment.Position"),
            axis is null ? "—" : $"{axis.Position:F3} {unit}",
            OpenVisionLanguageService.T("Equipment.Velocity"),
            axis is null ? "—" : $"{axis.Velocity:F3} {unit}/s",
            axis?.State == AxisState.Moving,
            false);

    private static EquipmentStatusPresentation CreateSensor(
        LayoutItem item,
        LayoutComponentSnapshot? runtime,
        DigitalSignalSnapshot? signal)
    {
        var detected = signal?.Value ?? runtime?.IsDetected;
        return new EquipmentStatusPresentation(
            item.Name,
            OpenVisionLanguageService.T("Equipment.DigitalSensor"),
            detected is null
                ? OpenVisionLanguageService.T("Equipment.NoRuntimeData")
                : detected.Value
                    ? OpenVisionLanguageService.T("Equipment.On")
                    : OpenVisionLanguageService.T("Equipment.Off"),
            detected is null
                ? OpenVisionLanguageService.T("Equipment.Unavailable")
                : OpenVisionLanguageService.T("Equipment.Normal"),
            OpenVisionLanguageService.T("Equipment.Detection"),
            detected is null
                ? "—"
                : detected.Value
                    ? OpenVisionLanguageService.T("Equipment.Present")
                    : OpenVisionLanguageService.T("Equipment.Clear"),
            OpenVisionLanguageService.T("Equipment.PendingDelay"),
            runtime?.PendingTransitionTicks is int ticks && ticks > 0
                ? $"{ticks} {OpenVisionLanguageService.T("Equipment.Ticks")}"
                : OpenVisionLanguageService.T("Equipment.None"),
            detected == true,
            false);
    }

    private static DigitalSignalSnapshot? ResolveSensorSignal(
        LayoutComponentSnapshot? runtime,
        SimulationSnapshot snapshot)
    {
        string? outputChannelId = runtime?.SensorOutputChannelId;
        return string.IsNullOrWhiteSpace(outputChannelId)
            ? null
            : snapshot.Signals.FirstOrDefault(signal =>
                string.Equals(signal.Id, outputChannelId, StringComparison.Ordinal));
    }

    private static EquipmentStatusPresentation CreateCylinder(
        LayoutItem item,
        LayoutComponentSnapshot? runtime)
    {
        var state = runtime?.CylinderState;
        var progress = runtime?.MotionProgress;
        return new EquipmentStatusPresentation(
            item.Name,
            OpenVisionLanguageService.T("Equipment.PneumaticCylinder"),
            state is null
                ? OpenVisionLanguageService.T("Equipment.NoRuntimeData")
                : LocalizeCylinderState(state.Value),
            state is null
                ? OpenVisionLanguageService.T("Equipment.Unavailable")
                : OpenVisionLanguageService.T("Equipment.Normal"),
            OpenVisionLanguageService.T("Equipment.Travel"),
            progress is null ? "—" : $"{Math.Clamp(progress.Value, 0, 1):P0}",
            OpenVisionLanguageService.T("Equipment.Binding"),
            item.BehaviorBindingId ?? OpenVisionLanguageService.T("Equipment.None"),
            state is PneumaticCylinderState.Extending
                or PneumaticCylinderState.Retracting
                or PneumaticCylinderState.Extended,
            state == PneumaticCylinderState.Fault);
    }

    private static EquipmentStatusPresentation CreateConveyor(
        LayoutItem item,
        LayoutComponentSnapshot? runtime)
    {
        var running = runtime?.ConveyorRunning;
        return new EquipmentStatusPresentation(
            item.Name,
            OpenVisionLanguageService.T("Equipment.ConveyorMotor"),
            running is null
                ? OpenVisionLanguageService.T("Equipment.NoRuntimeData")
                : running.Value
                    ? OpenVisionLanguageService.T("Equipment.State.Running")
                    : OpenVisionLanguageService.T("Equipment.State.Stopped"),
            running is null
                ? OpenVisionLanguageService.T("Equipment.Unavailable")
                : OpenVisionLanguageService.T("Equipment.Normal"),
            OpenVisionLanguageService.T("Equipment.Speed"),
            runtime?.ConveyorSpeedUnitsPerSecond is double speed
                ? $"{speed:F1} mm/s"
                : "—",
            OpenVisionLanguageService.T("Equipment.Direction"),
            runtime?.ConveyorDirection is { } direction
                ? LocalizeDirection(direction)
                : "—",
            running == true,
            false);
    }

    private static EquipmentStatusPresentation CreateWorkpiece(
        LayoutItem item,
        LayoutComponentSnapshot? runtime) =>
        new(
            item.Name,
            OpenVisionLanguageService.T("Equipment.Workpiece"),
            runtime?.InspectionState is { } inspectionState
                ? LocalizeInspectionState(inspectionState)
                : OpenVisionLanguageService.T("Equipment.NoRuntimeData"),
            runtime is null
                ? OpenVisionLanguageService.T("Equipment.Unavailable")
                : OpenVisionLanguageService.T("Equipment.Normal"),
            OpenVisionLanguageService.T("Equipment.Type"),
            runtime?.WorkpieceType ?? "—",
            OpenVisionLanguageService.T("Equipment.Position"),
            runtime is null ? "—" : FormatPosition(runtime.X, runtime.Y),
            false,
            false);

    private static bool TargetsItem(
        SimulationFaultSnapshot fault,
        LayoutItem item,
        MachineProjectDocument project)
    {
        if (string.Equals(fault.TargetId, item.Id, StringComparison.Ordinal))
        {
            return true;
        }

        if (item.Kind is LayoutItemKind.LinearStage or LayoutItemKind.RotaryStage &&
            string.Equals(fault.TargetId, item.BehaviorBindingId, StringComparison.Ordinal))
        {
            return true;
        }

        if (item.Kind != LayoutItemKind.DigitalSensor)
        {
            return false;
        }

        var outputChannelId = project.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, item.BehaviorBindingId, StringComparison.Ordinal))?.Sensor?.OutputChannelId;
        return !string.IsNullOrWhiteSpace(outputChannelId)
            && string.Equals(fault.TargetId, outputChannelId, StringComparison.Ordinal);
    }

    private static string FormatPosition(double x, double y) => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Equipment.PositionFormat"),
        x,
        y);

    private static string FormatFaultKind(SimulationFaultKind kind) => kind switch
    {
        SimulationFaultKind.StuckDigitalInput => OpenVisionLanguageService.T("Equipment.FaultStuckInput"),
        SimulationFaultKind.CylinderTravelBlocked => OpenVisionLanguageService.T("Equipment.FaultTravelBlocked"),
        SimulationFaultKind.AxisMotionBlocked => OpenVisionLanguageService.T("Equipment.FaultAxisMotionBlocked"),
        SimulationFaultKind.AxisFollowingError => OpenVisionLanguageService.T("Equipment.FaultAxisFollowingError"),
        _ => kind.ToString().ToUpperInvariant()
    };

    private static string LocalizeLayoutKind(LayoutItemKind kind) => kind switch
    {
        LayoutItemKind.MachineFrame => OpenVisionLanguageService.T("Equipment.MachineFrame"),
        LayoutItemKind.LinearStage => OpenVisionLanguageService.T("Equipment.LinearStageMotor"),
        LayoutItemKind.RotaryStage => OpenVisionLanguageService.T("Equipment.RotaryStageMotor"),
        LayoutItemKind.DigitalSensor => OpenVisionLanguageService.T("Equipment.DigitalSensor"),
        LayoutItemKind.PneumaticCylinder => OpenVisionLanguageService.T("Equipment.PneumaticCylinder"),
        LayoutItemKind.Conveyor => OpenVisionLanguageService.T("Equipment.ConveyorMotor"),
        LayoutItemKind.Workpiece => OpenVisionLanguageService.T("Equipment.Workpiece"),
        _ => kind.ToString()
    };

    private static string LocalizeAxisState(AxisState state) =>
        OpenVisionLanguageService.T($"Equipment.State.{state}", state.ToString(), state.ToString());

    private static string LocalizeCylinderState(PneumaticCylinderState state) =>
        OpenVisionLanguageService.T($"Equipment.State.{state}", state.ToString(), state.ToString());

    private static string LocalizeDirection(ConveyorDirection direction) =>
        OpenVisionLanguageService.T(
            $"Equipment.Direction.{direction}",
            direction.ToString(),
            direction.ToString());

    private static string LocalizeInspectionState(WorkpieceInspectionState state) =>
        OpenVisionLanguageService.T(
            $"Equipment.{state}",
            state.ToString().ToUpperInvariant(),
            state.ToString().ToUpperInvariant());
}
