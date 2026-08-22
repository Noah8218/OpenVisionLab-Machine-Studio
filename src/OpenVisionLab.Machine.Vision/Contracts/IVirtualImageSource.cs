using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.Machine.Vision.Contracts;

public interface IVirtualImageSource
{
    ValueTask<VirtualFrameDescriptor> AcquireAsync(
        VirtualAcquisitionContext context,
        CancellationToken cancellationToken = default);
}
