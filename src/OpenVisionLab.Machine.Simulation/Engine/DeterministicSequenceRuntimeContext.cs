using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Adapts deterministic simulation components to the Sequence runtime contract.
/// </summary>
internal sealed class DeterministicSequenceRuntimeContext : ISequenceRuntimeContext
{
    private readonly DeterministicSignalHub _signalHub;
    private readonly IReadOnlyList<ServoAxisComponent> _axes;
    private readonly IReadOnlyList<DeterministicVirtualCamera> _cameras;
    private readonly Action<string, string, string, long, TimeSpan> _emit;
    private readonly long _eventTick;
    private readonly TimeSpan _eventTime;

    public DeterministicSequenceRuntimeContext(
        DeterministicSignalHub signalHub,
        IReadOnlyList<ServoAxisComponent> axes,
        IReadOnlyList<DeterministicVirtualCamera> cameras,
        long eventTick,
        TimeSpan eventTime,
        Action<string, string, string, long, TimeSpan> emit)
    {
        _signalHub = signalHub;
        _axes = axes;
        _cameras = cameras;
        _eventTick = eventTick;
        _eventTime = eventTime;
        _emit = emit;
    }

    public SequenceSignalReadResult ReadSignal(string signalId)
    {
        var read = _signalHub.ReadDigitalSignal(signalId);
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
        var write = _signalHub.SetDigitalOutput(
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
            _emit(
                "I/O",
                "DigitalOutputChanged",
                $"{signalId} = {FormatSignal(value)}.",
                _eventTick,
                _eventTime);
        }
        return SequenceContextOperationResult.Success();
    }

    public SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition)
    {
        var axis = _axes.FirstOrDefault(candidate =>
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

        _emit(
            "Motion",
            "SequenceAxisMoveAccepted",
            $"{axisId} target = {targetPosition:F3}.",
            _eventTick,
            _eventTime);
        return SequenceContextOperationResult.Success();
    }

    public SequenceAxisMotionReadResult ReadAxisMotionState(string axisId)
    {
        var axis = _axes.FirstOrDefault(candidate =>
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
        var camera = _cameras.FirstOrDefault(candidate =>
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

        _emit(
            "Camera",
            "CameraTriggered",
            $"{cameraId} started {trigger.AcquisitionId} for recipe '{recipeId}'.",
            _eventTick,
            _eventTime);
        return SequenceCameraTriggerResult.Success(trigger.AcquisitionId);
    }

    public SequenceVisionResultReadResult ReadVisionResult(
        string cameraId,
        string acquisitionId)
    {
        var camera = _cameras.FirstOrDefault(candidate =>
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

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";
}
