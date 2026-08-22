using System.Globalization;
using System.Security.Cryptography;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Vision.Models;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class ProjectRelativeSingleImageSourceTests
{
    [Fact]
    public async Task AcquireAsync_SameContextAndFile_ProducesSameDescriptor()
    {
        using var project = new TemporaryProject();
        var content = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await project.WriteAsync("assets/input.raw", content);
        var source = new ProjectRelativeSingleImageSource(
            project.Root,
            @"assets\input.raw",
            width: 2,
            height: 2,
            pixelFormat: "Mono8");
        var context = CreateContext();

        var first = await source.AcquireAsync(context);
        var second = await source.AcquireAsync(context);

        AssertEquivalent(first, second);
        Assert.Equal(context.AcquisitionId, first.FrameId);
        Assert.Equal("assets/input.raw", first.SourceRelativePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), first.ContentSha256);
        Assert.Equal(content.LongLength, first.ContentLength);
        Assert.Equal(2, first.Width);
        Assert.Equal(2, first.Height);
        Assert.Equal("Mono8", first.PixelFormat);
    }

    [Fact]
    public async Task AcquireAsync_WhenFileBytesChange_ChangesContentIdentity()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("input.raw", [0x01, 0x02, 0x03]);
        var source = new ProjectRelativeSingleImageSource(project.Root, "input.raw", 3, 1, "Mono8");
        var context = CreateContext();

        var before = await source.AcquireAsync(context);
        await project.WriteAsync("input.raw", [0x01, 0x02, 0x04]);
        var after = await source.AcquireAsync(context);

        Assert.NotEqual(before.ContentSha256, after.ContentSha256);
        Assert.Equal(before.ContentLength, after.ContentLength);
    }

    [Fact]
    public async Task AcquireAsync_PreservesTheExactAcquisitionContext()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("input.raw", [0x7F]);
        var axisPositions = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["axis-z"] = -12.5,
            ["axis-x"] = 42.25,
        };
        var context = new VirtualAcquisitionContext(
            "cam-top/frame/00000017",
            "cam-top",
            "presence-check",
            simulationTick: 913,
            simulationTime: TimeSpan.FromMilliseconds(4565),
            seed: 271828,
            axisPositions);
        var source = new ProjectRelativeSingleImageSource(project.Root, "input.raw", 1, 1, "Mono8");

        var descriptor = await source.AcquireAsync(context);

        Assert.Same(context, descriptor.Context);
        Assert.Equal("cam-top/frame/00000017", descriptor.Context.AcquisitionId);
        Assert.Equal("cam-top", descriptor.Context.CameraId);
        Assert.Equal("presence-check", descriptor.Context.RecipeId);
        Assert.Equal(913, descriptor.Context.SimulationTick);
        Assert.Equal(TimeSpan.FromMilliseconds(4565), descriptor.Context.SimulationTime);
        Assert.Equal(271828, descriptor.Context.Seed);
        Assert.Equal(axisPositions.Count, descriptor.Context.AxisPositions.Count);
        Assert.Equal(axisPositions["axis-x"], descriptor.Context.AxisPositions["axis-x"]);
        Assert.Equal(axisPositions["axis-z"], descriptor.Context.AxisPositions["axis-z"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../outside.raw")]
    [InlineData("assets/../../outside.raw")]
    public void Constructor_RejectsBlankOrTraversalPaths(string sourceRelativePath)
    {
        using var project = new TemporaryProject();

        Assert.Throws<ArgumentException>(() =>
            new ProjectRelativeSingleImageSource(project.Root, sourceRelativePath, 1, 1, "Mono8"));
    }

    [Fact]
    public void Constructor_RejectsRootedPath()
    {
        using var project = new TemporaryProject();
        var rootedPath = Path.Combine(project.Root, "input.raw");

        Assert.Throws<ArgumentException>(() =>
            new ProjectRelativeSingleImageSource(project.Root, rootedPath, 1, 1, "Mono8"));
    }

    [Fact]
    public async Task AcquireAsync_WhenFileIsMissing_ThrowsFileNotFound()
    {
        using var project = new TemporaryProject();
        var source = new ProjectRelativeSingleImageSource(project.Root, "missing.raw", 1, 1, "Mono8");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            source.AcquireAsync(CreateContext()).AsTask());

        Assert.EndsWith("missing.raw", exception.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcquireAsync_WhenFileIsEmpty_ThrowsInvalidData()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("empty.raw", []);
        var source = new ProjectRelativeSingleImageSource(project.Root, "empty.raw", 1, 1, "Mono8");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.AcquireAsync(CreateContext()).AsTask());

        Assert.Contains("empty.raw", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireAsync_WithCancelledToken_DoesNotOpenOrHashTheFile()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("input.raw", [0x01, 0x02, 0x03]);
        var source = new ProjectRelativeSingleImageSource(project.Root, "input.raw", 3, 1, "Mono8");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.AcquireAsync(CreateContext(), cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(0, 1, "Mono8")]
    [InlineData(-1, 1, "Mono8")]
    [InlineData(1, 0, "Mono8")]
    [InlineData(1, -1, "Mono8")]
    public void Constructor_RejectsNonPositiveDimensions(int width, int height, string pixelFormat)
    {
        using var project = new TemporaryProject();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectRelativeSingleImageSource(project.Root, "input.raw", width, height, pixelFormat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Mono8")]
    [InlineData("Mono8 ")]
    public void Constructor_RejectsBlankPixelFormat(string pixelFormat)
    {
        using var project = new TemporaryProject();

        Assert.Throws<ArgumentException>(() =>
            new ProjectRelativeSingleImageSource(project.Root, "input.raw", 1, 1, pixelFormat));
    }

    [Fact]
    public async Task AcquireAsync_IsIndependentOfCurrentCulture()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("assets/input.raw", [0x49, 0x69, 0x31, 0x2E, 0x35]);
        var source = new ProjectRelativeSingleImageSource(project.Root, "assets/input.raw", 5, 1, "Mono8");
        var context = CreateContext();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = await source.AcquireAsync(context);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            var arabic = await source.AcquireAsync(context);

            AssertEquivalent(turkish, arabic);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static VirtualAcquisitionContext CreateContext() => new(
        "cam-1/frame/00000001",
        "cam-1",
        "presence-check",
        simulationTick: 20,
        simulationTime: TimeSpan.FromMilliseconds(100),
        seed: 1234,
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["axis-x"] = 10.5,
        });

    private static void AssertEquivalent(
        VirtualFrameDescriptor expected,
        VirtualFrameDescriptor actual)
    {
        Assert.Same(expected.Context, actual.Context);
        Assert.Equal(expected.FrameId, actual.FrameId);
        Assert.Equal(expected.SourceRelativePath, actual.SourceRelativePath);
        Assert.Equal(expected.ContentSha256, actual.ContentSha256);
        Assert.Equal(expected.ContentLength, actual.ContentLength);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.PixelFormat, actual.PixelFormat);
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "OpenVisionLab-Machine-Infrastructure-Tests",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public async Task WriteAsync(string relativePath, byte[] content)
        {
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
