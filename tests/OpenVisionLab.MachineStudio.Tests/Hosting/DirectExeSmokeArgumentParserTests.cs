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

    [Fact]
    public void ParseSmokeOptionsMapsTypedValuesAndDerivedFlags()
    {
        var options = DirectExeSmokeArgumentParser.ParseSmokeOptions(
            new[]
            {
                "--smoke-perf",
                "--smoke-perf-samples", "24",
                "--smoke-perf-report", "perf.json",
                "--smoke-screenshot", "screen.png",
                "--smoke-size", "1920x1040",
                "--smoke-dpi", "150",
                "--smoke-language", "en",
                "--smoke-project", "project.ovmachine",
                "--smoke-layout-select", "layout-1",
                "--smoke-layout-select-many", "layout-1,layout-2",
                "--smoke-layout-align", "HorizontalCenter",
                "--smoke-direct-scene-gesture-state", "drag",
                "--smoke-command-trace", "trace.json",
                "--smoke-command-trace-state", "normal",
                "--smoke-run-layout",
                "--smoke-start-simulation",
                "--smoke-camera-first-use-state", "keyboard-space",
                "--smoke-test-scenario-settings-state", "valid",
                "--smoke-test-scenario-batch",
                "--smoke-unified-evidence-state", "replay",
                "--smoke-batch-persistence-verify",
                "--smoke-cylinder-fault", "cylinder-1"
            });

        Assert.True(options.PerformSmokePerf);
        Assert.Equal(24, options.SmokePerfSampleCount);
        Assert.Equal("perf.json", options.SmokePerfReportPath);
        Assert.Equal("screen.png", options.ScreenshotPath);
        Assert.Equal("1920x1040", options.SizeArgument);
        Assert.Equal((1920, 1040), options.WindowSize);
        Assert.Equal(150, options.DpiScalePercent);
        Assert.Equal("en", options.SmokeLanguage);
        Assert.Equal("project.ovmachine", options.ProjectPath);
        Assert.Equal("layout-1", options.LayoutSelectId);
        Assert.Equal("layout-1,layout-2", options.LayoutSelectMany);
        Assert.Equal("HorizontalCenter", options.LayoutAlignment);
        Assert.Equal("drag", options.DirectSceneGestureState);
        Assert.Equal("trace.json", options.CommandTracePath);
        Assert.Equal("normal", options.CommandTraceState);
        Assert.True(options.CommandTraceStateSpecified);
        Assert.True(options.UseRunLayout);
        Assert.True(options.StartSimulation);
        Assert.True(options.CameraFirstUseRequested);
        Assert.Equal("valid", options.TestScenarioSettingsState);
        Assert.True(options.ShowTestScenarioSettings);
        Assert.True(options.TestScenarioBatch);
        Assert.Equal("replay", options.UnifiedCommissioningEvidenceState);
        Assert.True(options.VerifyBatchPersistence);
        Assert.Equal("cylinder-1", options.CylinderFaultTargetId);
        Assert.True(options.IsSmokeRun);
    }

    [Fact]
    public void ParseSmokeOptionsPreservesDefaults()
    {
        var options = DirectExeSmokeArgumentParser.ParseSmokeOptions(Array.Empty<string>());

        Assert.False(options.PerformSmokePerf);
        Assert.Equal(12, options.SmokePerfSampleCount);
        Assert.Equal("1280x760", options.SizeArgument);
        Assert.Equal((1280, 760), options.WindowSize);
        Assert.Equal(100, options.DpiScalePercent);
        Assert.Equal("normal", options.CommandTraceState);
        Assert.Equal("normal", options.ScenarioEvidenceExchangeState);
        Assert.Equal("normal", options.UnifiedCommissioningEvidenceState);
        Assert.False(options.CommandTraceStateSpecified);
        Assert.False(options.CameraFirstUseRequested);
        Assert.False(options.IsSmokeRun);
    }
}
