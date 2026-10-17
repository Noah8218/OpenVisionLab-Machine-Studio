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
    string SessionId,
    long DocumentRevision,
    string ProjectId,
    string ProjectName,
    string ProjectPath,
    string ProjectJson,
    string BuildIdentity,
    TimeSpan SimulationFixedStep,
    SimulationSnapshot BaselineSnapshot,
    VirtualCameraSnapshot BaselineCamera,
    VirtualCameraInspectionRequest InspectionRequest,
    long RuntimeGeneration,
    bool WaitForExternalResult = false);

internal sealed record ManualCameraSessionIdentity(
    string SessionId,
    long DocumentRevision);

/// <summary>
/// Owns one manual virtual-camera acquisition transaction. Workspace guards
/// and localized failure presentation are composed by the camera owner.
/// </summary>
internal sealed class ManualCameraTriggerWorkflow
{
    private readonly VirtualCameraInspectionWorkflow _inspectionWorkflow = new();
    private readonly Func<VirtualCameraInspectionRequest, CancellationToken, ValueTask<VirtualFrameDescriptor>> _acquireFrameAsync;
    private readonly Func<VirtualCameraInspectionRequest, VirtualFrameDescriptor, CancellationToken, Task<VisionRunResult>> _runInspectionAsync;
    private readonly Func<SimulationSnapshot> _getCurrentSnapshot;
    private readonly Func<SimulationCommand, string, Task<SimulationCommandResult>>
        _dispatchCameraCommand;
    private readonly VisionExecutionEvidenceViewModel _visionExecutionEvidence;
    private readonly Action<SimulationSnapshot> _applyMonitorSnapshot;
    private readonly Func<ManualCameraSessionIdentity> _getCurrentSessionIdentity;

    internal ManualCameraTriggerWorkflow(
        Func<SimulationSnapshot> getCurrentSnapshot,
        Func<SimulationCommand, string, Task<SimulationCommandResult>> dispatchCameraCommand,
        VisionExecutionEvidenceViewModel visionExecutionEvidence,
        Action<SimulationSnapshot> applyMonitorSnapshot,
        Func<ManualCameraSessionIdentity> getCurrentSessionIdentity,
        Func<VirtualCameraInspectionRequest, CancellationToken, ValueTask<VirtualFrameDescriptor>>? acquireFrameAsync = null,
        Func<VirtualCameraInspectionRequest, VirtualFrameDescriptor, CancellationToken, Task<VisionRunResult>>? runInspectionAsync = null)
    {
        _getCurrentSnapshot = getCurrentSnapshot
            ?? throw new ArgumentNullException(nameof(getCurrentSnapshot));
        _dispatchCameraCommand = dispatchCameraCommand
            ?? throw new ArgumentNullException(nameof(dispatchCameraCommand));
        _visionExecutionEvidence = visionExecutionEvidence
            ?? throw new ArgumentNullException(nameof(visionExecutionEvidence));
        _applyMonitorSnapshot = applyMonitorSnapshot
            ?? throw new ArgumentNullException(nameof(applyMonitorSnapshot));
        _getCurrentSessionIdentity = getCurrentSessionIdentity
            ?? throw new ArgumentNullException(nameof(getCurrentSessionIdentity));
        _acquireFrameAsync = acquireFrameAsync ?? _inspectionWorkflow.AcquireFrameAsync;
        _runInspectionAsync = runInspectionAsync ?? _inspectionWorkflow.RunInspectionAsync;
    }

    internal async Task<ManualCameraTriggerResult> ExecuteAsync(
        ManualCameraTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        VirtualFrameDescriptor frame;
        try
        {
            frame = await _acquireFrameAsync(
                request.InspectionRequest,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return new(ManualCameraTriggerOutcome.SourceRejected, exception.Message);
        }

        VisionRunResult? inspectionResult = null;
        if (!request.WaitForExternalResult)
        {
            try
            {
                inspectionResult = await _runInspectionAsync(
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
        }

        if (!IsCurrentContext(request))
        {
            return new(ManualCameraTriggerOutcome.ContextChanged);
        }

        var command = _inspectionWorkflow.CreateTriggerCommand(
            frame,
            inspectionResult,
            request.ProjectId,
            request.RuntimeGeneration,
            request.WaitForExternalResult);
        if (inspectionResult is not null)
        {
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
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _dispatchCameraCommand(command, "Camera.ActionTrigger");
            if (result.IsAccepted)
            {
                _applyMonitorSnapshot(_getCurrentSnapshot());
                return new(ManualCameraTriggerOutcome.Accepted);
            }

            _visionExecutionEvidence.CancelCapture();
            return new(ManualCameraTriggerOutcome.DispatchRejected, result.Detail);
        }
        catch (OperationCanceledException)
        {
            _visionExecutionEvidence.CancelCapture();
            throw;
        }
    }

    internal async Task<ManualCameraTriggerResult> ExecuteNumericalAsync(ManualCameraTriggerRequestInput input, CancellationToken cancellationToken)
    {
        if (!input.IsAllowed || input.CurrentCamera is not { } camera || input.CameraDefinition?.NumericalInput is not { } settings
            || settings.Validate() is not null || settings.ConfirmedVersion != settings.Version || settings.RecipeName != input.SelectedRecipe)
            return new(ManualCameraTriggerOutcome.SourceRejected, "Apply and confirm the numerical capture recipe first.");
        if (!IsCurrentContext(input.SessionId, input.DocumentRevision, input.ProjectId, input.Snapshot.RuntimeGeneration, input.Snapshot, camera))
            return new(ManualCameraTriggerOutcome.ContextChanged);
        cancellationToken.ThrowIfCancellationRequested();
        var command = TriggerVirtualCameraCommand.CreateNumerical(camera.Id, settings.RecipeName, input.ProjectId, input.Snapshot.RuntimeGeneration,
            input.WaitForExternalResult);
        var result = await _dispatchCameraCommand(command, "Camera.ActionTrigger");
        if (!result.IsAccepted) return new(ManualCameraTriggerOutcome.DispatchRejected, result.Detail);
        _applyMonitorSnapshot(_getCurrentSnapshot());
        return new(ManualCameraTriggerOutcome.Accepted);
    }

    internal ValueTask<VirtualFrameDescriptor> AcquireFrameAsync(
        VirtualCameraInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        _acquireFrameAsync(request, cancellationToken);

    private bool IsCurrentContext(ManualCameraTriggerRequest request) =>
        IsCurrentContext(request.SessionId, request.DocumentRevision, request.ProjectId, request.RuntimeGeneration, request.BaselineSnapshot, request.BaselineCamera);

    private bool IsCurrentContext(string sessionId, long revision, string projectId, long runtimeGeneration, SimulationSnapshot baselineSnapshot, VirtualCameraSnapshot baselineCamera)
    {
        var current = _getCurrentSnapshot();
        var session = _getCurrentSessionIdentity();
        var currentCamera = current.Cameras.FirstOrDefault(camera =>
            string.Equals(
                camera.Id,
                baselineCamera.Id,
                StringComparison.Ordinal));
        return current.RunMode == SimulationRunMode.Paused
            && current.ControlOwner == SimulationControlOwner.Manual
            && current.TickIndex == baselineSnapshot.TickIndex
            && current.SimulationTime == baselineSnapshot.SimulationTime
            && string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)
            && session.DocumentRevision == revision
            && string.Equals(current.ProjectId, projectId, StringComparison.Ordinal)
            && current.RuntimeGeneration == runtimeGeneration
            && currentCamera is { }
            && currentCamera.AcquisitionOrdinal == baselineCamera.AcquisitionOrdinal
            && currentCamera.State == baselineCamera.State;
    }
}
