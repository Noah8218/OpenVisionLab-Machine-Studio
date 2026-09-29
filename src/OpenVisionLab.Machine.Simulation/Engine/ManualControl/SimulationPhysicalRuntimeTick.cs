using System.Globalization;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Advances physical simulation components in the engine's fixed tick order
/// and reports their existing transition messages through the engine boundary.
/// </summary>
internal sealed class SimulationPhysicalRuntimeTick
{
    private static readonly IReadOnlySet<string> EmptyBlockedCylinderIds =
        new HashSet<string>(StringComparer.Ordinal);
    private readonly Action<string, string, string, long, TimeSpan> _emit;

    internal SimulationPhysicalRuntimeTick(Action<string, string, string, long, TimeSpan> emit)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    internal void Advance(SimulationPhysicalRuntimeTickContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var axis in context.Axes)
        {
            var previousState = axis.State;
            var driveAlarmWasActive = axis.DriveAlarmActive;
            axis.Tick(context.FixedStep);
            if (!driveAlarmWasActive && axis.DriveAlarmActive)
            {
                Emit(
                    "Motion",
                    "AxisDriveAlarmActivated",
                    $"{axis.Id} following error {axis.FollowingError:F3} exceeded " +
                    $"limit {axis.FollowingErrorLimit:F3}.",
                    context);
            }
            if (previousState == AxisState.Moving && axis.State == AxisState.Idle)
            {
                Emit(
                    "Motion",
                    "AxisTargetReached",
                    $"{axis.Id} reached {axis.Position:F3}.",
                    context);
            }
        }

        if (context.MachineLayout is not null)
        {
            var axisSnapshots = context.Axes.ToDictionary(
                axis => axis.Id,
                axis => axis.CreateSnapshot(),
                StringComparer.Ordinal);
            var cameraSnapshots = context.Cameras.ToDictionary(
                camera => camera.Id,
                camera => camera.CaptureSnapshot(),
                StringComparer.Ordinal);
            var layoutTick = context.MachineLayout.Tick(
                axisSnapshots,
                context.BlockedCylinderIds ?? EmptyBlockedCylinderIds,
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
                    context);
            }

            foreach (var transition in layoutTick.CylinderStateTransitions)
            {
                Emit(
                    "Cylinder",
                    "CylinderStateChanged",
                    $"{transition.ComponentId}: {transition.PreviousState} -> " +
                    $"{transition.CurrentState} ({transition.MotionProgress:P0}).",
                    context);
            }

            foreach (var transition in layoutTick.CylinderFeedbackTransitions)
            {
                Emit(
                    "Cylinder",
                    "CylinderFeedbackChanged",
                    $"{transition.ComponentId} wrote {transition.ChannelId} = " +
                    $"{FormatSignal(transition.CurrentValue)}.",
                    context);
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
                    context);
            }
        }

        foreach (var camera in context.Cameras)
        {
            var cameraTick = camera.Tick();
            if (cameraTick.Transition == VirtualCameraTickTransition.ExposureCompleted)
            {
                Emit(
                    "Camera",
                    "CameraExposureCompleted",
                    $"{camera.Id} exposure completed for {cameraTick.Snapshot.CurrentAcquisitionId}; transfer started.",
                    context);
            }
            else if (cameraTick.Transition == VirtualCameraTickTransition.FrameReady)
            {
                var acquisition = cameraTick.CompletedAcquisition!;
                string workpieceContext = string.IsNullOrWhiteSpace(acquisition.WorkpieceComponentId)
                    ? string.Empty
                    : $"; workpiece component '{acquisition.WorkpieceComponentId}'";
                if (!string.IsNullOrWhiteSpace(acquisition.WorkpieceInstanceId))
                {
                    workpieceContext += $"; workpiece instance '{acquisition.WorkpieceInstanceId}'";
                }
                Emit(
                    "Camera",
                    "CameraFrameReady",
                    $"{camera.Id} frame {acquisition.AcquisitionId} is ready for recipe " +
                    $"'{acquisition.RecipeId}'{workpieceContext}" +
                    (acquisition.FrameEvidence is null
                        ? "."
                        : $"; SHA-256 {acquisition.FrameEvidence.ContentSha256}."),
                    context);
                Emit(
                    "Vision",
                    "VisionResultReady",
                    acquisition.InspectionEvidence is { } inspection
                        ? $"{acquisition.AcquisitionId} inspection {inspection.InspectionId} result = " +
                          $"{inspection.Decision.ToString().ToUpperInvariant()}; " +
                          $"metrics {FormatInspectionMetrics(inspection.Metrics)}{workpieceContext}."
                        : $"{acquisition.AcquisitionId} placeholder result = " +
                          $"{acquisition.Decision.ToString().ToUpperInvariant()}{workpieceContext}.",
                    context);
            }
            else if (cameraTick.Transition == VirtualCameraTickTransition.ExternalResultPending)
            {
                Emit(
                    "Camera",
                    "CameraExternalResultPending",
                    $"{camera.Id} frame {cameraTick.Snapshot.CurrentAcquisitionId} transferred and is waiting for an external Result.",
                    context);
            }
        }
    }

    private void Emit(
        string category,
        string code,
        string message,
        SimulationPhysicalRuntimeTickContext context) =>
        _emit(category, code, message, context.EventTick, context.EventTime);

    private static string FormatInspectionMetrics(IReadOnlyDictionary<string, double> metrics) =>
        metrics.Count == 0
            ? "none"
            : string.Join(
                ", ",
                metrics.Select(metric => string.Concat(
                    metric.Key,
                    "=",
                    metric.Value.ToString("R", CultureInfo.InvariantCulture))));

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";

    private static string FormatConveyorState(bool isRunning, ConveyorDirection direction) =>
        isRunning ? $"RUNNING {direction}" : $"STOPPED {direction}";
}

internal sealed record SimulationPhysicalRuntimeTickContext(
    TimeSpan FixedStep,
    long EventTick,
    TimeSpan EventTime,
    IReadOnlyList<ServoAxisComponent> Axes,
    DeterministicMachineLayout? MachineLayout,
    IReadOnlySet<string>? BlockedCylinderIds,
    IReadOnlyList<DeterministicVirtualCamera> Cameras);
