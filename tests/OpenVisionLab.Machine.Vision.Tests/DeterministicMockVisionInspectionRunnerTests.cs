using System.Globalization;
using OpenVisionLab.Machine.Vision.Contracts;
using OpenVisionLab.Machine.Vision.Models;
using Xunit;

namespace OpenVisionLab.Machine.Vision.Tests;

public sealed class DeterministicMockVisionInspectionRunnerTests
{
    private const string ContentHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task RunAsync_IsRepeatableAcrossCulturesAndInputOrdering()
    {
        var runner = CreateRunner();
        var recipe = new VisionRecipeReference("presence-check", "recipes\\presence.ovrecipe");
        var firstFrame = CreateFrame(new Dictionary<string, double>
        {
            ["axis-z"] = -0.25,
            ["axis-x"] = 125.5
        });
        var secondFrame = CreateFrame(new Dictionary<string, double>
        {
            ["axis-x"] = 125.5,
            ["axis-z"] = -0.25
        });

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = await runner.RunAsync(recipe, firstFrame);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            var second = await runner.RunAsync(recipe, secondFrame);

            Assert.Equal(first.InspectionId, second.InspectionId);
            Assert.Equal(
                "inspection/sha256/99bb1218251b9605c6e650d861614031c8e06d7985fbce2a38d10da62a678ff2",
                first.InspectionId);
            Assert.Equal(VisionJudgment.OK, first.Judgment);
            Assert.Equal("camera-top/frame/00000001", first.AcquisitionId);
            Assert.Equal("camera-top", first.CameraId);
            Assert.Equal("presence-check", first.RecipeId);
            Assert.Equal("frame-00000001", first.FrameId);
            Assert.Equal("Deterministic mock inspection completed with OK.", first.Message);
            Assert.Equal(new[] { "ContentLengthBytes", "PixelCount", "SimulationTick" }, first.Metrics.Keys);
            Assert.Equal(307_200, first.Metrics["ContentLengthBytes"]);
            Assert.Equal(307_200, first.Metrics["PixelCount"]);
            Assert.Equal(42, first.Metrics["SimulationTick"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task RunAsync_UsesDeepCopiedExplicitRecipeJudgmentMap()
    {
        var judgments = new Dictionary<string, VisionJudgment>
        {
            ["presence-check"] = VisionJudgment.NG,
            ["dimension-check"] = VisionJudgment.OK
        };
        var runner = new DeterministicMockVisionInspectionRunner(judgments);
        judgments["presence-check"] = VisionJudgment.OK;
        judgments["new-recipe"] = VisionJudgment.OK;

        var result = await runner.RunAsync(
            new VisionRecipeReference("presence-check", "recipes/presence.ovrecipe"),
            CreateFrame());

        Assert.Equal(VisionJudgment.NG, result.Judgment);
        Assert.Equal(new[] { "dimension-check", "presence-check" }, runner.RecipeJudgments.Keys);
        Assert.False(runner.RecipeJudgments.ContainsKey("new-recipe"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, VisionJudgment>)runner.RecipeJudgments).Add("other", VisionJudgment.OK));
    }

    [Fact]
    public async Task RunAsync_DoesNotInferJudgmentFromSourceFileName()
    {
        var runner = CreateRunner();
        var frame = CreateFrame(sourceRelativePath: "images/definite-ng-failure-part.pgm");

        var result = await runner.RunAsync(
            new VisionRecipeReference("presence-check", "recipes/presence.ovrecipe"),
            frame);

        Assert.Equal(VisionJudgment.OK, result.Judgment);
    }

    [Fact]
    public async Task RunAsync_ChangesInspectionIdWhenCanonicalEvidenceChanges()
    {
        var runner = CreateRunner();
        var recipe = new VisionRecipeReference("presence-check", "recipes/presence.ovrecipe");
        var baseline = await runner.RunAsync(recipe, CreateFrame());
        var changedAcquisition = await runner.RunAsync(
            recipe,
            CreateFrame(acquisitionId: "camera-top/frame/00000002"));
        var changedRecipeEvidence = await runner.RunAsync(
            new VisionRecipeReference("presence-check", "recipes/presence-v2.ovrecipe"),
            CreateFrame());

        Assert.NotEqual(baseline.InspectionId, changedAcquisition.InspectionId);
        Assert.NotEqual(baseline.InspectionId, changedRecipeEvidence.InspectionId);
    }

    [Fact]
    public async Task RunAsync_ReturnsReadOnlyMetricsThatDoNotAliasInputs()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(
            new VisionRecipeReference("presence-check", "recipes/presence.ovrecipe"),
            CreateFrame());

        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, double>)result.Metrics).Add("Injected", 1));

        var sourceMetrics = new Dictionary<string, double> { ["MetricZ"] = 2, ["MetricA"] = 1 };
        var copiedResult = new VisionRunResult(
            "inspection-manual",
            "acquisition-manual",
            "camera-top",
            "presence-check",
            "frame-manual",
            VisionJudgment.OK,
            "Complete.",
            sourceMetrics);
        sourceMetrics["MetricA"] = 99;
        sourceMetrics["NewMetric"] = 3;

        Assert.Equal(new[] { "MetricA", "MetricZ" }, copiedResult.Metrics.Keys);
        Assert.Equal(1, copiedResult.Metrics["MetricA"]);
        Assert.False(copiedResult.Metrics.ContainsKey("NewMetric"));
    }

    [Fact]
    public async Task RunAsync_RejectsRecipeCorrelationMismatchAndUnmappedRecipe()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new VisionRecipeReference("dimension-check", "recipes/dimension.ovrecipe"),
            CreateFrame()));

        var unmappedFrame = CreateFrame(recipeId: "unmapped-recipe");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => runner.RunAsync(
            new VisionRecipeReference("unmapped-recipe", "recipes/unmapped.ovrecipe"),
            unmappedFrame));
    }

    [Fact]
    public async Task RunAsync_ObservesPreCanceledToken()
    {
        var runner = CreateRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var task = runner.RunAsync(
            new VisionRecipeReference("presence-check", "recipes/presence.ovrecipe"),
            CreateFrame(),
            cancellation.Token);

        Assert.True(task.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Constructor_RejectsImplicitOrInvalidJudgments()
    {
        Assert.Throws<ArgumentNullException>(() => new DeterministicMockVisionInspectionRunner(null!));
        Assert.Throws<ArgumentException>(() => new DeterministicMockVisionInspectionRunner(
            new Dictionary<string, VisionJudgment> { ["presence-check"] = VisionJudgment.None }));
        Assert.Throws<ArgumentException>(() => new DeterministicMockVisionInspectionRunner(
            new Dictionary<string, VisionJudgment> { [" presence-check"] = VisionJudgment.OK }));
        Assert.Throws<ArgumentException>(() => new DeterministicMockVisionInspectionRunner(
            new Dictionary<string, VisionJudgment> { ["presence-check"] = (VisionJudgment)999 }));
    }

    [Fact]
    public void VisionRunResult_RejectsNonFiniteMetrics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisionRunResult(
            "inspection",
            "acquisition",
            "camera",
            "recipe",
            "frame",
            VisionJudgment.OK,
            "Complete.",
            new Dictionary<string, double> { ["Score"] = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisionRunResult(
            "inspection",
            "acquisition",
            "camera",
            "recipe",
            "frame",
            (VisionJudgment)999,
            "Complete."));
    }

    private static DeterministicMockVisionInspectionRunner CreateRunner() => new(
        new Dictionary<string, VisionJudgment>
        {
            ["presence-check"] = VisionJudgment.OK,
            ["dimension-check"] = VisionJudgment.NG
        });

    private static VirtualFrameDescriptor CreateFrame(
        IReadOnlyDictionary<string, double>? axisPositions = null,
        string recipeId = "presence-check",
        string acquisitionId = "camera-top/frame/00000001",
        string sourceRelativePath = "images/part-a.pgm")
    {
        var context = new VirtualAcquisitionContext(
            acquisitionId,
            "camera-top",
            recipeId,
            42,
            TimeSpan.FromMilliseconds(210),
            7301,
            axisPositions ?? new Dictionary<string, double>
            {
                ["axis-x"] = 125.5,
                ["axis-z"] = -0.25
            });

        return new VirtualFrameDescriptor(
            context,
            "frame-00000001",
            sourceRelativePath,
            ContentHash,
            307_200,
            640,
            480,
            "Mono8");
    }
}
