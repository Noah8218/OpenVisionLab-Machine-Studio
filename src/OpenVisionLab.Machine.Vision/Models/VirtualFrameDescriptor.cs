namespace OpenVisionLab.Machine.Vision.Models;

public sealed class VirtualFrameDescriptor
{
    public VirtualFrameDescriptor(
        VirtualAcquisitionContext context,
        string frameId,
        string sourceRelativePath,
        string contentSha256,
        long contentLength,
        int width,
        int height,
        string pixelFormat)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        FrameId = VisionContractValidation.RequiredIdentifier(frameId, nameof(frameId));
        SourceRelativePath = VisionContractValidation.RelativePath(sourceRelativePath, nameof(sourceRelativePath));

        if (string.IsNullOrWhiteSpace(contentSha256) ||
            contentSha256.Length != 64 ||
            contentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Content SHA-256 must contain exactly 64 hexadecimal characters.", nameof(contentSha256));
        }

        if (contentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength), contentLength, "Content length must be positive.");
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Frame width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Frame height must be positive.");
        }

        ContentSha256 = contentSha256.ToUpperInvariant();
        ContentLength = contentLength;
        Width = width;
        Height = height;
        PixelFormat = VisionContractValidation.RequiredText(pixelFormat, nameof(pixelFormat));
    }

    public VirtualAcquisitionContext Context { get; }

    public string AcquisitionId => Context.AcquisitionId;

    public string CameraId => Context.CameraId;

    public string RecipeId => Context.RecipeId;

    public long SimulationTick => Context.SimulationTick;

    public TimeSpan SimulationTime => Context.SimulationTime;

    public int Seed => Context.Seed;

    public IReadOnlyDictionary<string, double> AxisPositions => Context.AxisPositions;

    public string FrameId { get; }

    public string SourceRelativePath { get; }

    public string ContentSha256 { get; }

    public long ContentLength { get; }

    public int Width { get; }

    public int Height { get; }

    public string PixelFormat { get; }
}
