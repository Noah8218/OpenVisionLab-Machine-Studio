using System.IO;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum ManualCameraTriggerOutcome
{
    Accepted,
    DispatchRejected,
    SourceRejected,
    InspectionRejected,
    ContextChanged
}

internal sealed record ManualCameraTriggerResult(
    ManualCameraTriggerOutcome Outcome,
    string? Detail = null);

internal sealed record ManualCameraTriggerRequest(
    string ProjectId,
    string ProjectName,
    string ProjectPath,
    string ProjectJson,
    string BuildIdentity,
    TimeSpan SimulationFixedStep,
    SimulationSnapshot BaselineSnapshot,
    VirtualCameraSnapshot BaselineCamera,
    VirtualCameraInspectionRequest InspectionRequest);

/// <summary>
/// Owns one manual virtual-camera acquisition transaction. Shell guards and
/// localized failure presentation remain with the composition shell.
/// </summary>
internal sealed class ManualCameraTriggerWorkflow
{
    private readonly VirtualCameraInspectionWorkflow _inspectionWorkflow = new();
    private readonly Func<SimulationSnapshot> _getCurrentSnapshot;
    private readonly Func<SimulationCommand, string, Task<SimulationCommandResult>>
        _dispatchCameraCommand;
    private readonly VisionExecutionEvidenceViewModel _visionExecutionEvidence;
    private readonly Action<SimulationSnapshot> _applyMonitorSnapshot;

    internal ManualCameraTriggerWorkflow(
        Func<SimulationSnapshot> getCurrentSnapshot,
        Func<SimulationCommand, string, Task<SimulationCommandResult>> dispatchCameraCommand,
        VisionExecutionEvidenceViewModel visionExecutionEvidence,
        Action<SimulationSnapshot> applyMonitorSnapshot)
    {
        _getCurrentSnapshot = getCurrentSnapshot
            ?? throw new ArgumentNullException(nameof(getCurrentSnapshot));
        _dispatchCameraCommand = dispatchCameraCommand
            ?? throw new ArgumentNullException(nameof(dispatchCameraCommand));
        _visionExecutionEvidence = visionExecutionEvidence
            ?? throw new ArgumentNullException(nameof(visionExecutionEvidence));
        _applyMonitorSnapshot = applyMonitorSnapshot
            ?? throw new ArgumentNullException(nameof(applyMonitorSnapshot));
    }

    internal async Task<ManualCameraTriggerResult> ExecuteAsync(
        ManualCameraTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        VirtualFrameDescriptor frame;
        try
        {
            frame = await _inspectionWorkflow.AcquireFrameAsync(
                request.InspectionRequest,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return new(ManualCameraTriggerOutcome.SourceRejected, exception.Message);
        }

        VisionRunResult inspectionResult;
        try
        {
            inspectionResult = await _inspectionWorkflow.RunInspectionAsync(
                request.InspectionRequest,
                frame,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            return new(ManualCameraTriggerOutcome.InspectionRejected, exception.Message);
        }

        if (!IsCurrentContext(request))
        {
            return new(ManualCameraTriggerOutcome.ContextChanged);
        }

        var command = _inspectionWorkflow.CreateTriggerCommand(frame, inspectionResult);
        var recorder = new DeterministicVisionExecutionRecorder(
            request.ProjectId,
            request.ProjectName,
            request.ProjectPath,
            request.ProjectJson,
            request.BuildIdentity,
            request.SimulationFixedStep,
            request.BaselineSnapshot.TickIndex,
            command.CommandId,
            request.InspectionRequest.CameraId,
            request.InspectionRequest.RecipeId,
            frame.AcquisitionId,
            frame.FrameId,
            inspectionResult.InspectionId);
        _visionExecutionEvidence.BeginCapture(recorder);

        var result = await _dispatchCameraCommand(command, "Camera.ActionTrigger");
        if (result.IsAccepted)
        {
            _applyMonitorSnapshot(_getCurrentSnapshot());
            return new(ManualCameraTriggerOutcome.Accepted);
        }

        _visionExecutionEvidence.CancelCapture();
        return new(ManualCameraTriggerOutcome.DispatchRejected, result.Detail);
    }

    private bool IsCurrentContext(ManualCameraTriggerRequest request)
    {
        var current = _getCurrentSnapshot();
        var currentCamera = current.Cameras.FirstOrDefault(camera =>
            string.Equals(
                camera.Id,
                request.BaselineCamera.Id,
                StringComparison.Ordinal));
        return current.RunMode == SimulationRunMode.Paused
            && current.ControlOwner == SimulationControlOwner.Manual
            && current.TickIndex == request.BaselineSnapshot.TickIndex
            && current.SimulationTime == request.BaselineSnapshot.SimulationTime
            && currentCamera is { }
            && currentCamera.AcquisitionOrdinal == request.BaselineCamera.AcquisitionOrdinal
            && currentCamera.State == request.BaselineCamera.State;
    }
}
