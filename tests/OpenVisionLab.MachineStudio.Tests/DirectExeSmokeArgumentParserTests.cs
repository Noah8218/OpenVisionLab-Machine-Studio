using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class DirectExeSmokeArgumentParserTests
{
    [Theory]
    [InlineData("--smoke-project")]
    [InlineData("--SMOKE-PERF")]
    [InlineData("--fault-project")]
    [InlineData("--FAULT-SCENARIO")]
    [InlineData("--build-identity-report")]
    public void IsRequestedRecognizesSupportedDirectExeModes(string argument)
    {
        Assert.True(DirectExeSmokeArgumentParser.IsRequested(new[] { argument }));
    }

    [Fact]
    public void IsRequestedRejectsUnrelatedArguments()
    {
        Assert.False(DirectExeSmokeArgumentParser.IsRequested(new[] { "--project", "sample.ovmachine" }));
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndRequiresAFollowingValue()
    {
        var args = new[]
        {
            "--SMOKE-DPI", "125",
            "--smoke-size", "1920x1040",
            "--smoke-project"
        };

        Assert.Equal("125", DirectExeSmokeArgumentParser.GetArgumentValue(args, "--smoke-dpi"));
        Assert.Equal("1920x1040", DirectExeSmokeArgumentParser.GetArgumentValue(args, "--SMOKE-SIZE"));
        Assert.Null(DirectExeSmokeArgumentParser.GetArgumentValue(args, "--smoke-project"));
        Assert.True(DirectExeSmokeArgumentParser.HasArgument(args, "--SMOKE-PROJECT"));
        Assert.False(DirectExeSmokeArgumentParser.HasArgument(args, "--smoke-missing"));
    }

    [Fact]
    public void IntegerParsingPreservesDefaultRangeAndErrorContract()
    {
        Assert.Equal(12, DirectExeSmokeArgumentParser.ParseIntArgument(null, "--samples", 12, 4, 100));
        Assert.Equal(4, DirectExeSmokeArgumentParser.ParseIntArgument("4", "--samples", 12, 4, 100));
        Assert.Equal(100, DirectExeSmokeArgumentParser.ParseIntArgument("100", "--samples", 12, 4, 100));

        var exception = Assert.Throws<ArgumentException>(
            () => DirectExeSmokeArgumentParser.ParseIntArgument("101", "--samples", 12, 4, 100));

        Assert.Contains("Expected an integer from 4 to 100", exception.Message);
    }

    [Fact]
    public void SizeAndDpiParsingPreserveExistingDefaultsAndRanges()
    {
        Assert.Equal((1920, 1040), DirectExeSmokeArgumentParser.ParseSize("1920x1040"));
        Assert.Equal((1280, 760), DirectExeSmokeArgumentParser.ParseSize("invalid"));
        Assert.Equal(100, DirectExeSmokeArgumentParser.ParseDpiScalePercent(null));
        Assert.Equal(200, DirectExeSmokeArgumentParser.ParseDpiScalePercent("200"));

        var exception = Assert.Throws<ArgumentException>(
            () => DirectExeSmokeArgumentParser.ParseDpiScalePercent("201"));

        Assert.Contains("Expected an integer from 100 to 200", exception.Message);
    }

    [Fact]
    public void ValidationPreservesCommandTraceAndRoundTripContracts()
    {
        var commandTraceException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-command-trace", "trace.json" }));
        Assert.Contains("--smoke-run-layout", commandTraceException.Message);

        var roundTripException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[]
                {
                    "--smoke-roundtrip-save", "project.ovmachine",
                    "--smoke-roundtrip-verify",
                    "--smoke-roundtrip-report", "roundtrip.json"
                }));
        Assert.Contains("either --smoke-roundtrip-save", roundTripException.Message);

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(
            new[]
            {
                "--smoke-run-layout",
                "--smoke-command-trace", "trace.json",
                "--smoke-command-trace-state", "normal",
                "--smoke-roundtrip-report", "roundtrip.json",
                "--smoke-roundtrip-verify"
            });
    }

    [Fact]
    public void CameraFirstUseDerivationPreservesRequestedAndAppliedStates()
    {
        Assert.False(DirectExeSmokeArgumentParser.IsCameraFirstUseRequested(Array.Empty<string>()));
        Assert.True(DirectExeSmokeArgumentParser.IsCameraFirstUseRequested(
            new[] { "--smoke-camera-first-use-state", "keyboard-space" }));
        Assert.True(DirectExeSmokeArgumentParser.IsCameraFirstUseAppliedState(
            new[] { "--smoke-camera-first-use-state", "APPLIED" }));
        Assert.False(DirectExeSmokeArgumentParser.IsCameraFirstUseAppliedState(
            new[] { "--smoke-camera-first-use-state", "idle" }));
    }

    [Fact]
    public void ValidationPreservesUnifiedEvidenceAndAxisFaultDependencies()
    {
        var evidenceException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-unified-evidence-state", "normal" }));
        Assert.Contains("--smoke-test-scenario-batch", evidenceException.Message);

        var axisException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-axis-fault-persistence", "axis.json" }));
        Assert.Contains("--smoke-test-axis-fault-scenario", axisException.Message);
    }

    [Fact]
    public void ValidationPreservesCameraAndRecipeGalleryDependencies()
    {
        var cameraException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-camera-first-use-state", "applied" }));
        Assert.Contains("both report and save paths", cameraException.Message);

        var galleryException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-recipe-gallery-state", "compare" }));
        Assert.Contains("baseline-report", galleryException.Message);
    }

    [Fact]
    public void ValidationPreservesSafetyAndAuthoringDependencies()
    {
        var safetyException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-project-safety-report", "safety.json" }));
        Assert.Contains("--smoke-project-safety-save", safetyException.Message);

        var authoringException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-analog-authoring-save", "analog.ovmachine" }));
        Assert.Contains("save-reload", authoringException.Message);
    }
}
