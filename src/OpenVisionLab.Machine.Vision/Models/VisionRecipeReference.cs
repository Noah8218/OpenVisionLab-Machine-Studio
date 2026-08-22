namespace OpenVisionLab.Machine.Vision.Models;

public sealed class VisionRecipeReference
{
    public string Id { get; }
    public string RelativePath { get; }

    public VisionRecipeReference(string id, string relativePath)
    {
        Id = VisionContractValidation.RequiredIdentifier(id, nameof(id));
        RelativePath = VisionContractValidation.RelativePath(relativePath, nameof(relativePath));
    }
}
