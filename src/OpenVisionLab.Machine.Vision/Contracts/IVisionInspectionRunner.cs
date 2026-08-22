using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.Machine.Vision.Contracts;

public interface IVisionInspectionRunner
{
    Task<VisionRunResult> RunAsync(
        VisionRecipeReference recipe,
        VirtualFrameDescriptor frame,
        CancellationToken cancellationToken = default);
}
