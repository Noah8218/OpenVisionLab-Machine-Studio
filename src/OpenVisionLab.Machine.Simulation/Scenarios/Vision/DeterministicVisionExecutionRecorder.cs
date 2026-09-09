using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Correlates one manual camera transaction with runtime events and completes
/// it into the immutable Vision evidence package.
/// </summary>
public sealed class DeterministicVisionExecutionRecorder
{
    private readonly string _projectId;
    private readonly string _projectName;
    private readonly string _projectPath;
    private readonly string _projectJson;
    private readonly string _buildIdentity;
    private readonly TimeSpan _fixedStep;
    private readonly long _triggerTick;
    private readonly string _commandId;
    private readonly string _cameraId;
    private readonly string _recipeId;
    private readonly string _acquisitionId;
    private readonly string _frameId;
    private readonly string _inspectionId;
    private readonly List<SimulationEvent> _events = [];
    private bool _started;

    public DeterministicVisionExecutionRecorder(
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        string buildIdentity,
        TimeSpan fixedStep,
        long triggerTick,
        string commandId,
        string cameraId,
        string recipeId,
        string acquisitionId,
        string frameId,
        string inspectionId)
    {
        _projectId = projectId;
        _projectName = projectName;
        _projectPath = projectPath;
        _projectJson = projectJson;
        _buildIdentity = buildIdentity;
        _fixedStep = fixedStep;
        _triggerTick = triggerTick;
        _commandId = commandId;
        _cameraId = cameraId;
        _recipeId = recipeId;
        _acquisitionId = acquisitionId;
        _frameId = frameId;
        _inspectionId = inspectionId;
    }

    public bool IsReady { get; private set; }

    public void RecordEvent(SimulationEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (!_started)
        {
            if (!string.Equals(runtimeEvent.Code, "CameraTriggered", StringComparison.Ordinal)
                || !string.Equals(runtimeEvent.CommandId, _commandId, StringComparison.Ordinal))
            {
                return;
            }
            _started = true;
        }

        if (IsReady)
        {
            return;
        }

        _events.Add(runtimeEvent);
        if (string.Equals(runtimeEvent.Code, "VisionResultReady", StringComparison.Ordinal)
            && runtimeEvent.Message.Contains(_acquisitionId, StringComparison.Ordinal)
            && runtimeEvent.Message.Contains(_inspectionId, StringComparison.Ordinal))
        {
            IsReady = true;
        }
    }

    public bool CanComplete(SimulationSnapshot snapshot)
    {
        var camera = snapshot.Cameras.FirstOrDefault(item =>
            string.Equals(item.Id, _cameraId, StringComparison.Ordinal));
        var result = camera?.Result;
        return IsReady
            && camera?.State == VirtualCameraState.FrameReady
            && string.Equals(result?.AcquisitionId, _acquisitionId, StringComparison.Ordinal)
            && string.Equals(result?.RecipeId, _recipeId, StringComparison.Ordinal)
            && string.Equals(result?.FrameEvidence?.FrameId ?? camera.FrameEvidence?.FrameId, _frameId, StringComparison.Ordinal)
            && string.Equals(result?.InspectionEvidence?.InspectionId, _inspectionId, StringComparison.Ordinal);
    }

    public DeterministicVisionExecutionEvidencePackage Complete(SimulationSnapshot snapshot)
    {
        if (!CanComplete(snapshot))
        {
            throw new InvalidOperationException("Vision execution evidence is not complete.");
        }

        var camera = snapshot.Cameras.Single(item =>
            string.Equals(item.Id, _cameraId, StringComparison.Ordinal));
        return DeterministicVisionExecutionEvidencePackage.Create(
            _projectId,
            _projectName,
            _projectPath,
            _projectJson,
            _buildIdentity,
            _fixedStep,
            _triggerTick,
            snapshot,
            camera,
            _events);
    }
}
