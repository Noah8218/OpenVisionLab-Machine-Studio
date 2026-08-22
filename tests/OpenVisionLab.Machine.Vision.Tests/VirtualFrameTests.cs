using OpenVisionLab.Machine.Vision.Models;
using Xunit;

namespace OpenVisionLab.Machine.Vision.Tests;

public sealed class VirtualFrameTests
{
    private const string ContentHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void AcquisitionContext_DeepCopiesAndOrdinallyOrdersAxisPositions()
    {
        var positions = new Dictionary<string, double>
        {
            ["axis-z"] = 30.5,
            ["axis-A"] = -2.25,
            ["axis-x"] = 10
        };

        var context = new VirtualAcquisitionContext(
            "acquisition-0001",
            "camera-top",
            "presence-check",
            42,
            TimeSpan.FromMilliseconds(210),
            7301,
            positions);

        positions["axis-z"] = 999;
        positions["new-axis"] = 1;

        Assert.Equal(new[] { "axis-A", "axis-x", "axis-z" }, context.AxisPositions.Keys);
        Assert.Equal(30.5, context.AxisPositions["axis-z"]);
        Assert.False(context.AxisPositions.ContainsKey("new-axis"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, double>)context.AxisPositions).Add("axis-y", 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void AcquisitionContext_RejectsInvalidIdentifiers(string? identifier)
    {
        Assert.Throws<ArgumentException>(() => new VirtualAcquisitionContext(
            identifier!, "camera", "recipe", 0, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentException>(() => new VirtualAcquisitionContext(
            "acquisition", identifier!, "recipe", 0, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentException>(() => new VirtualAcquisitionContext(
            "acquisition", "camera", identifier!, 0, TimeSpan.Zero, 1));
    }

    [Fact]
    public void AcquisitionContext_RejectsNegativeTimeAndNonFiniteAxisPositions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualAcquisitionContext(
            "acquisition", "camera", "recipe", -1, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualAcquisitionContext(
            "acquisition", "camera", "recipe", 0, TimeSpan.FromTicks(-1), 1));

        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualAcquisitionContext(
                "acquisition",
                "camera",
                "recipe",
                0,
                TimeSpan.Zero,
                1,
                new Dictionary<string, double> { ["axis"] = value }));
        }
    }

    [Fact]
    public void FrameDescriptor_PreservesEvidenceAndCanonicalizesPathAndHash()
    {
        var context = CreateContext();

        var frame = new VirtualFrameDescriptor(
            context,
            "frame-0001",
            "images\\part-a.pgm",
            ContentHash,
            307_200,
            640,
            480,
            "Mono8");

        Assert.Same(context, frame.Context);
        Assert.Equal(context.AcquisitionId, frame.AcquisitionId);
        Assert.Equal(context.CameraId, frame.CameraId);
        Assert.Equal(context.RecipeId, frame.RecipeId);
        Assert.Equal(context.SimulationTick, frame.SimulationTick);
        Assert.Equal(context.SimulationTime, frame.SimulationTime);
        Assert.Equal(context.Seed, frame.Seed);
        Assert.Same(context.AxisPositions, frame.AxisPositions);
        Assert.Equal("frame-0001", frame.FrameId);
        Assert.Equal("images/part-a.pgm", frame.SourceRelativePath);
        Assert.Equal(ContentHash.ToUpperInvariant(), frame.ContentSha256);
        Assert.Equal(307_200, frame.ContentLength);
        Assert.Equal(640, frame.Width);
        Assert.Equal(480, frame.Height);
        Assert.Equal("Mono8", frame.PixelFormat);
    }

    [Theory]
    [InlineData(0, 640, 480)]
    [InlineData(-1, 640, 480)]
    [InlineData(1, 0, 480)]
    [InlineData(1, -1, 480)]
    [InlineData(1, 640, 0)]
    [InlineData(1, 640, -1)]
    public void FrameDescriptor_RejectsInvalidLengthOrDimensions(long contentLength, int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualFrameDescriptor(
            CreateContext(), "frame", "images/part.pgm", ContentHash, contentLength, width, height, "Mono8"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeff")]
    public void FrameDescriptor_RejectsInvalidContentHash(string contentHash)
    {
        Assert.Throws<ArgumentException>(() => new VirtualFrameDescriptor(
            CreateContext(), "frame", "images/part.pgm", contentHash, 1, 1, 1, "Mono8"));
    }

    [Theory]
    [InlineData("/images/part.pgm")]
    [InlineData("C:\\images\\part.pgm")]
    [InlineData("images/../part.pgm")]
    [InlineData("images//part.pgm")]
    public void FrameDescriptor_RejectsNonCanonicalRelativeSourcePath(string sourceRelativePath)
    {
        Assert.Throws<ArgumentException>(() => new VirtualFrameDescriptor(
            CreateContext(), "frame", sourceRelativePath, ContentHash, 1, 1, 1, "Mono8"));
    }

    private static VirtualAcquisitionContext CreateContext() => new(
        "acquisition-0001",
        "camera-top",
        "presence-check",
        42,
        TimeSpan.FromMilliseconds(210),
        7301);
}
