using System.Security.Cryptography;
using OpenVisionLab.Machine.Vision.Contracts;
using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.Machine.Infrastructure.Vision;

/// <summary>
/// Replays one project-owned image as deterministic virtual-camera evidence.
/// The image is streamed only to compute its content identity; pixel bytes are
/// not retained by this source.
/// </summary>
public sealed class ProjectRelativeSingleImageSource : IVirtualImageSource
{
    private readonly ProjectAssetPathResolver _pathResolver;
    private readonly ProjectAssetPath _asset;

    public ProjectRelativeSingleImageSource(
        string projectRoot,
        string sourceRelativePath,
        int width,
        int height,
        string pixelFormat)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Image width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Image height must be positive.");
        }

        if (string.IsNullOrWhiteSpace(pixelFormat) ||
            !string.Equals(pixelFormat, pixelFormat.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Pixel format is required and cannot contain leading or trailing whitespace.",
                nameof(pixelFormat));
        }

        _pathResolver = new ProjectAssetPathResolver(projectRoot);
        _asset = _pathResolver.ResolveFile(sourceRelativePath);
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
    }

    public string SourceRelativePath => _asset.RelativePath;

    public int Width { get; }

    public int Height { get; }

    public string PixelFormat { get; }

    public async ValueTask<VirtualFrameDescriptor> AcquireAsync(
        VirtualAcquisitionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var asset = _pathResolver.ResolveExistingFile(_asset.RelativePath);
        await using var stream = new FileStream(
            asset.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var contentLength = stream.Length;
        if (contentLength == 0)
        {
            throw new InvalidDataException($"Project image asset is empty: '{asset.RelativePath}'.");
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var contentSha256 = Convert.ToHexString(hash);

        return new VirtualFrameDescriptor(
            context,
            context.AcquisitionId,
            asset.RelativePath,
            contentSha256,
            contentLength,
            Width,
            Height,
            PixelFormat);
    }
}
