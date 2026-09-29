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
    public void TopViewSmokeOptionUsesTheExistingSceneViewCommand()
    {
        Assert.True(DirectExeSmokeArgumentParser.ParseSmokeOptions(new[] { "--smoke-top-view" }).UseTopView);
        Assert.False(DirectExeSmokeArgumentParser.ParseSmokeOptions(Array.Empty<string>()).UseTopView);
    }

    [Fact]
    public void LibrarySearchSmokeRequiresTheLibraryTabAndRunLayout()
    {
        var args = new[]
        {
            "--smoke-run-layout",
            "--smoke-left-tool-tab", "Library",
            "--smoke-library-search-text", "camera"
        };
        Assert.Equal("camera", DirectExeSmokeArgumentParser.ParseSmokeOptions(args).LibrarySearchText);
        DirectExeSmokeArgumentParser.ValidateSmokeArguments(args);

        var exception = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-library-search-text", "camera" }));
        Assert.Contains("--smoke-run-layout", exception.Message);
        Assert.Contains("Library tab or entry state 'tab'", exception.Message);

        var entryStateArgs = new[]
        {
            "--smoke-run-layout",
            "--smoke-library-entry-state", "tab",
            "--smoke-library-search-text", "camera"
        };
        var options = DirectExeSmokeArgumentParser.ParseSmokeOptions(entryStateArgs);
        Assert.Equal("tab", options.LibraryEntryState);
        Assert.Equal("camera", options.LibrarySearchText);
        DirectExeSmokeArgumentParser.ValidateSmokeArguments(entryStateArgs);

        var equipmentEntryArgs = new[]
        {
            "--smoke-project", "fixture.ovmachine",
            "--smoke-library-entry-state", "equipment-tab",
            "--smoke-library-search-text", "camera"
        };
        var equipmentOptions = DirectExeSmokeArgumentParser.ParseSmokeOptions(equipmentEntryArgs);
        Assert.Equal("equipment-tab", equipmentOptions.LibraryEntryState);
        Assert.Equal("camera", equipmentOptions.LibrarySearchText);
        DirectExeSmokeArgumentParser.ValidateSmokeArguments(equipmentEntryArgs);

        var equipmentDialogArgs = new[]
        {
            "--smoke-project", "fixture.ovmachine",
            "--smoke-library-entry-state", "equipment-dialog",
            "--smoke-library-search-text", "camera"
        };
        Assert.Equal("equipment-dialog", DirectExeSmokeArgumentParser.ParseSmokeOptions(equipmentDialogArgs).LibraryEntryState);
        DirectExeSmokeArgumentParser.ValidateSmokeArguments(equipmentDialogArgs);
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
    public void TopCommandPressedCaptureRequiresStateScreenshotAndSavedFixtureForSave()
    {
        var screenshotException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-command-state", "undo-pressed" }));
        Assert.Contains("--smoke-command-state-screenshot", screenshotException.Message);

        var projectException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[]
                {
                    "--smoke-command-state", "save-pressed",
                    "--smoke-command-state-screenshot", "pressed.png"
                }));
        Assert.Contains("--smoke-project", projectException.Message);

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(
            new[]
            {
                "--smoke-command-state", "save-pressed",
                "--smoke-command-state-screenshot", "pressed.png",
                "--smoke-project", "fixture.ovmachine"
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

        var reportException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-scenario-report", "report.md" }));
        Assert.Contains("--smoke-test-scenario-batch", reportException.Message);

        var reportStateException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-scenario-report-state", "hover" }));
        Assert.Contains("--smoke-scenario-report", reportStateException.Message);

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

        var diagnosticsException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-project-diagnostics-report", "diagnostics.json" }));
        Assert.Contains("--smoke-project", diagnosticsException.Message);

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(
            new[]
            {
                "--smoke-project", "project.ovmachine",
                "--smoke-project-diagnostics-report", "diagnostics.json",
                "--smoke-project-diagnostics-screenshot", "diagnostics.png"
            });

        var supportScreenshotException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-support-diagnostics-screenshot", "support.png" }));
        Assert.Contains("--smoke-support-diagnostics-report", supportScreenshotException.Message);

        var supportExportException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-support-diagnostics-report", "support-report.json" }));
        Assert.Contains("--smoke-support-diagnostics-export", supportExportException.Message);

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(
            new[]
            {
                "--smoke-support-diagnostics-report", "support.json",
                "--smoke-support-diagnostics-export", "support-bundle.json",
                "--smoke-support-diagnostics-screenshot", "support.png"
            });
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
                "--smoke-equipment-outline-search", "r19-no-match",
                "--smoke-layout-select", "layout-1",
                "--smoke-layout-select-many", "layout-1,layout-2",
                "--smoke-layout-align", "HorizontalCenter",
                "--smoke-direct-scene-gesture-state", "drag",
                "--smoke-command-trace", "trace.json",
                "--smoke-command-trace-state", "normal",
                "--smoke-run-layout",
                "--smoke-start-simulation",
                "--smoke-camera-first-use-state", "keyboard-space",
                "--smoke-mmi-operator-state", "interaction",
                "--smoke-mmi-operator-report", "mmi.json",
                "--smoke-project-diagnostics-report", "diagnostics.json",
                "--smoke-project-diagnostics-screenshot", "diagnostics.png",
                "--smoke-support-diagnostics-report", "support.json",
                "--smoke-support-diagnostics-export", "support-bundle.json",
                "--smoke-support-diagnostics-screenshot", "support.png",
                "--smoke-test-scenario-settings-state", "valid",
                "--smoke-test-scenario-batch",
                "--smoke-scenario-report", "report.md",
                "--smoke-scenario-report-state", "pressed",
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
        Assert.Equal("r19-no-match", options.EquipmentOutlineSearchText);
        Assert.Equal("diagnostics.json", options.ProjectDiagnosticsReportPath);
        Assert.Equal("diagnostics.png", options.ProjectDiagnosticsScreenshotPath);
        Assert.Equal("support.json", options.SupportDiagnosticsReportPath);
        Assert.Equal("support-bundle.json", options.SupportDiagnosticsExportPath);
        Assert.Equal("support.png", options.SupportDiagnosticsScreenshotPath);
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
        Assert.Equal("interaction", options.MmiOperatorState);
        Assert.Equal("mmi.json", options.MmiOperatorReportPath);
        Assert.Equal("valid", options.TestScenarioSettingsState);
        Assert.True(options.ShowTestScenarioSettings);
        Assert.True(options.TestScenarioBatch);
        Assert.Equal("report.md", options.ScenarioReportPath);
        Assert.Equal("pressed", options.ScenarioReportState);
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
        Assert.Null(options.ScenarioReportPath);
        Assert.Equal("normal", options.ScenarioReportState);
        Assert.Equal("normal", options.UnifiedCommissioningEvidenceState);
        Assert.False(options.CommandTraceStateSpecified);
        Assert.False(options.CameraFirstUseRequested);
        Assert.Null(options.MmiOperatorState);
        Assert.Null(options.MmiOperatorReportPath);
        Assert.False(options.IsSmokeRun);
    }

    [Fact]
    public void ValidationRequiresRunLayoutAndReportForMmiOperatorInteraction()
    {
        var runLayoutException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-mmi-operator-state", "interaction", "--smoke-mmi-operator-report", "mmi.json" }));
        Assert.Contains("--smoke-run-layout", runLayoutException.Message);

        var reportException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-run-layout", "--smoke-mmi-operator-state", "interaction" }));
        Assert.Contains("--smoke-mmi-operator-report", reportException.Message);

        var stateException = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-run-layout", "--smoke-mmi-operator-state", "focus", "--smoke-mmi-operator-report", "mmi.json" }));
        Assert.Contains("Expected interaction", stateException.Message);

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(
            new[]
            {
                "--smoke-run-layout",
                "--smoke-mmi-operator-state", "interaction",
                "--smoke-mmi-operator-report", "mmi.json"
            });
    }

    [Fact]
    public void EquipmentOutlineSearchSmokeRequiresRunLayout()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DirectExeSmokeArgumentParser.ValidateSmokeArguments(
                new[] { "--smoke-equipment-outline-search", "no-match" }));

        Assert.Contains("--smoke-run-layout", exception.Message);
    }
}
