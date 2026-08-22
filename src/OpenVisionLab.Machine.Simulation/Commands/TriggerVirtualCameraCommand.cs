using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class TriggerVirtualCameraCommand : SimulationCommand
{
    public TriggerVirtualCameraCommand(
        string cameraId,
        string recipeId,
        VirtualCameraFrameEvidence frameEvidence,
        VirtualCameraInspectionEvidence? inspectionEvidence = null)
    {
        CameraId = cameraId;
        RecipeId = recipeId;
        FrameEvidence = frameEvidence ?? throw new ArgumentNullException(nameof(frameEvidence));
        InspectionEvidence = inspectionEvidence;
    }

    public string CameraId { get; }
    public string RecipeId { get; }
    public VirtualCameraFrameEvidence FrameEvidence { get; }
    public VirtualCameraInspectionEvidence? InspectionEvidence { get; }
}
