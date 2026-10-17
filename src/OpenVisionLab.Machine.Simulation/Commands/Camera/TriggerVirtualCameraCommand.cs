using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class TriggerVirtualCameraCommand : SimulationCommand
{
    public TriggerVirtualCameraCommand(
        string cameraId,
        string recipeId,
        VirtualCameraFrameEvidence frameEvidence,
        VirtualCameraInspectionEvidence? inspectionEvidence = null,
        string? projectId = null,
        long? runtimeGeneration = null,
        bool waitForExternalResult = false)
    {
        if ((string.IsNullOrWhiteSpace(projectId)) != !runtimeGeneration.HasValue)
        {
            throw new ArgumentException(
                "Project id and runtime generation must be supplied together.",
                nameof(projectId));
        }

        CameraId = cameraId;
        RecipeId = recipeId;
        FrameEvidence = frameEvidence ?? throw new ArgumentNullException(nameof(frameEvidence));
        InspectionEvidence = inspectionEvidence;
        ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
        RuntimeGeneration = runtimeGeneration;
        WaitForExternalResult = waitForExternalResult;
    }

    private TriggerVirtualCameraCommand(string cameraId, string recipeId, string projectId, long runtimeGeneration, bool waitForExternalResult)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        CameraId = cameraId;
        RecipeId = recipeId;
        ProjectId = projectId;
        RuntimeGeneration = runtimeGeneration;
        WaitForExternalResult = waitForExternalResult;
        GeneratesNumericalInput = true;
    }

    public static TriggerVirtualCameraCommand CreateNumerical(string cameraId, string recipeId, string projectId, long runtimeGeneration, bool waitForExternalResult = false) =>
        new(cameraId, recipeId, projectId, runtimeGeneration, waitForExternalResult);

    public bool GeneratesNumericalInput { get; }
    public string CameraId { get; }
    public string RecipeId { get; }
    public VirtualCameraFrameEvidence? FrameEvidence { get; }
    public VirtualCameraInspectionEvidence? InspectionEvidence { get; }
    public string? ProjectId { get; }
    public long? RuntimeGeneration { get; }
    public bool WaitForExternalResult { get; }
    public bool HasRuntimeIdentity => ProjectId is not null && RuntimeGeneration.HasValue;
}
