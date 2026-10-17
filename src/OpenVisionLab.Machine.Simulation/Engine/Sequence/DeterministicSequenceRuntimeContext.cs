using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.Machine.Sequence.Compilation;

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
    private readonly bool _waitForExternalResult;
    private readonly IReadOnlyCollection<string> _numericalCameraIds;
    private readonly IReadOnlyDictionary<string, VirtualCameraExternalSource> _externalSources;
    private readonly Func<string, string?>? _workpieceInstanceIdAccessor;
    private readonly Func<string, SequenceContextOperationResult>? _workpieceFeedHandler;
    private readonly Func<string, SequenceContextOperationResult>? _workpieceEjectHandler;
    private readonly UnitMaterialFlowRuntime? _unitMaterialFlow;
    private readonly string? _unitId;

    public DeterministicSequenceRuntimeContext(
        DeterministicSignalHub signalHub,
        IReadOnlyList<ServoAxisComponent> axes,
        IReadOnlyList<DeterministicVirtualCamera> cameras,
        long eventTick,
        TimeSpan eventTime,
        Action<string, string, string, long, TimeSpan> emit,
        bool waitForExternalResult = false,
        IReadOnlyDictionary<string, VirtualCameraExternalSource>? externalSources = null,
        Func<string, string?>? workpieceInstanceIdAccessor = null,
        Func<string, SequenceContextOperationResult>? workpieceFeedHandler = null,
        Func<string, SequenceContextOperationResult>? workpieceEjectHandler = null,
        UnitMaterialFlowRuntime? unitMaterialFlow = null,
        string? unitId = null,
        IReadOnlyCollection<string>? numericalCameraIds = null)
    {
        _signalHub = signalHub;
        _axes = axes;
        _cameras = cameras;
        _eventTick = eventTick;
        _eventTime = eventTime;
        _emit = emit;
        _waitForExternalResult = waitForExternalResult;
        _numericalCameraIds = numericalCameraIds ?? Array.Empty<string>();
        _externalSources = externalSources
            ?? new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal);
        _workpieceInstanceIdAccessor = workpieceInstanceIdAccessor;
        _workpieceFeedHandler = workpieceFeedHandler;
        _workpieceEjectHandler = workpieceEjectHandler;
        _unitMaterialFlow = unitMaterialFlow;
        _unitId = unitId;
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
        => TriggerCamera(cameraId, recipeId, workpieceComponentId: null, sequenceId: null, sequenceStepId: null);

    public SequenceCameraTriggerResult TriggerCamera(
        string cameraId,
        string recipeId,
        string? workpieceComponentId)
        => TriggerCamera(cameraId, recipeId, workpieceComponentId, sequenceId: null, sequenceStepId: null);

    public SequenceCameraTriggerResult TriggerCamera(
        string cameraId,
        string recipeId,
        string? workpieceComponentId,
        string? sequenceId,
        string? sequenceStepId)
    {
        var camera = _cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, cameraId, StringComparison.Ordinal));
        if (camera is null)
        {
            return SequenceCameraTriggerResult.Failure(
                SequenceContextErrorCode.TargetNotFound,
                $"Virtual camera '{cameraId}' was not found.");
        }

        string? workpieceInstanceId = null;
        if (_unitId is not null && _unitMaterialFlow is not null && string.IsNullOrWhiteSpace(workpieceComponentId))
        {
            if (!_unitMaterialFlow.BeginCapture(_unitId, cameraId, out workpieceInstanceId))
                return SequenceCameraTriggerResult.Failure(SequenceContextErrorCode.Rejected, "The unit slot has no active workpiece to inspect.");
            if (_unitMaterialFlow.Enabled) workpieceComponentId = _unitId;
        }
        else if (!string.IsNullOrWhiteSpace(workpieceComponentId))
        {
            workpieceInstanceId = _workpieceInstanceIdAccessor?.Invoke(workpieceComponentId);
            if (string.IsNullOrWhiteSpace(workpieceInstanceId))
            {
                return SequenceCameraTriggerResult.Failure(
                    SequenceContextErrorCode.Rejected,
                    $"Workpiece position '{workpieceComponentId}' has no active instance to inspect.");
            }
        }

        VirtualCameraFrameEvidence? frameEvidence = null;
        if (_waitForExternalResult && !_numericalCameraIds.Contains(cameraId, StringComparer.Ordinal))
        {
            if (!_externalSources.TryGetValue(cameraId, out var source))
            {
                return SequenceCameraTriggerResult.Failure(
                    SequenceContextErrorCode.Unavailable,
                    $"No preflighted external source is armed for virtual camera '{cameraId}'.");
            }

            frameEvidence = source.CreateEvidence(camera.CreateNextAcquisitionId());
        }

        var referenceAxis = camera.ReferenceAxisId is { } axisId ? _axes.FirstOrDefault(axis => axis.Id == axisId) : null;
        if (camera.ReferenceAxisId is not null && referenceAxis is null)
            return SequenceCameraTriggerResult.Failure(SequenceContextErrorCode.TargetNotFound, "촬영 기준 축을 찾을 수 없습니다.");
        var trigger = camera.Trigger(
            recipeId,
            frameEvidence,
            inspectionEvidence: null,
            waitForExternalResult: _waitForExternalResult,
            workpieceComponentId: workpieceComponentId,
            workpieceInstanceId: workpieceInstanceId,
            axisOffset: referenceAxis?.Position ?? 0);
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

        string sequenceContext = string.IsNullOrWhiteSpace(sequenceId)
            || string.IsNullOrWhiteSpace(sequenceStepId)
                ? string.Empty
                : $" in sequence '{sequenceId}' at step '{sequenceStepId}'";
        string workpieceContext = string.IsNullOrWhiteSpace(workpieceComponentId)
            ? string.Empty
            : $" for workpiece component '{workpieceComponentId}'";
        string workpieceInstanceContext = string.IsNullOrWhiteSpace(workpieceInstanceId)
            ? string.Empty
            : $" instance '{workpieceInstanceId}'";
        _emit(
            "Camera",
            "CameraTriggered",
            $"{cameraId} started {trigger.AcquisitionId} for recipe '{recipeId}'{sequenceContext}" +
            $"{workpieceContext}{workpieceInstanceContext}.",
            _eventTick,
            _eventTime);
        return SequenceCameraTriggerResult.Success(trigger.AcquisitionId);
    }

    public SequenceContextOperationResult FeedWorkpiece(string workpieceComponentId) =>
        _workpieceFeedHandler?.Invoke(workpieceComponentId)
        ?? SequenceContextOperationResult.Failure(
            SequenceContextErrorCode.Unavailable,
            "Workpiece feeding is not available in this runtime context.");

    public SequenceContextOperationResult EjectWorkpiece(string workpieceComponentId) =>
        _workpieceEjectHandler?.Invoke(workpieceComponentId)
        ?? SequenceContextOperationResult.Failure(
            SequenceContextErrorCode.Unavailable,
            "Workpiece ejection is not available in this runtime context.");

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

        var read = snapshot.State switch
        {
            VirtualCameraState.Idle =>
                SequenceVisionResultReadResult.Success(SequenceVisionResultState.NotTriggered),
            VirtualCameraState.Exposing or VirtualCameraState.Transferring =>
                SequenceVisionResultReadResult.Success(SequenceVisionResultState.Pending),
            VirtualCameraState.AwaitingExternalResult =>
                SequenceVisionResultReadResult.Success(SequenceVisionResultState.Pending),
            VirtualCameraState.Faulted =>
                SequenceVisionResultReadResult.Success(SequenceVisionResultState.Faulted),
            VirtualCameraState.FrameReady => ReadCompletedVisionResult(snapshot, acquisitionId),
            _ => SequenceVisionResultReadResult.Failure(
                SequenceContextErrorCode.Unavailable,
                $"Virtual camera '{cameraId}' returned an unsupported state '{snapshot.State}'.")
        };
        if (_unitId is not null && _unitMaterialFlow is not null && read.IsSuccess)
            _unitMaterialFlow.RecordInspection(_unitId, cameraId, snapshot.Result?.WorkpieceInstanceId, read.State);
        return read;
    }

    public SequenceUnitOperationResult ExecuteUnitOperation(UnitOperationStep step, TimeSpan elapsed) =>
        _unitMaterialFlow?.Execute(step, elapsed) ?? SequenceUnitOperationResult.Failure("Unit material flow is not configured.");

    public SequenceVisionResultReadResult ReadUnitInspection(string unitId) => _unitMaterialFlow?.ReadInspection(unitId)
        ?? SequenceVisionResultReadResult.Failure(SequenceContextErrorCode.Unavailable, "Unit inspection is not configured.");

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
