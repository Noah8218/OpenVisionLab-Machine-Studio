using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenVisionLab.MachineStudio;

internal static class DirectExeSmokeArgumentParser
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument =>
            argument.StartsWith("--smoke-", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--fault-", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--build-identity-report", StringComparison.OrdinalIgnoreCase));

    public static string? GetArgumentValue(IReadOnlyList<string> args, string key)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static bool HasArgument(IReadOnlyList<string> args, string key) =>
        args.Any(argument => string.Equals(argument, key, StringComparison.OrdinalIgnoreCase));

    public static int ParseIntArgument(
        string? value,
        string argumentName,
        int defaultValue,
        int min,
        int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException(
                $"Invalid {argumentName} value '{value}'. Expected an integer from {min} to {max}.");
        }

        if (parsed < min || parsed > max)
        {
            throw new ArgumentException(
                $"Invalid {argumentName} value '{value}'. Expected an integer from {min} to {max}.");
        }

        return parsed;
    }

    public static (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split("x", StringSplitOptions.None);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var width) &&
            int.TryParse(parts[1], out var height))
        {
            return (width, height);
        }

        return (1280, 760);
    }

    public static int ParseDpiScalePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 100;
        }

        if (!int.TryParse(value, out var scalePercent) ||
            scalePercent is < 100 or > 200)
        {
            throw new ArgumentException(
                $"Invalid --smoke-dpi value '{value}'. Expected an integer from 100 to 200.");
        }

        return scalePercent;
    }

    public static bool IsCameraFirstUseRequested(IReadOnlyList<string> args) =>
        !string.IsNullOrWhiteSpace(GetArgumentValue(args, "--smoke-camera-first-use-report"))
        || !string.IsNullOrWhiteSpace(GetArgumentValue(args, "--smoke-camera-first-use-save"))
        || !string.IsNullOrWhiteSpace(GetArgumentValue(args, "--smoke-camera-first-use-state"));

    public static bool IsCameraFirstUseAppliedState(IReadOnlyList<string> args)
    {
        var state = GetArgumentValue(args, "--smoke-camera-first-use-state");
        return string.Equals(state, "applied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "keyboard-space", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateSmokeArguments(IReadOnlyList<string> args)
    {
        var commandTracePath = GetArgumentValue(args, "--smoke-command-trace");
        var commandTraceState = GetArgumentValue(args, "--smoke-command-trace-state") ?? "normal";
        var useRunLayout = HasArgument(args, "--smoke-run-layout");
        var unifiedCommissioningEvidencePath = GetArgumentValue(args, "--smoke-unified-commissioning-evidence");
        var testScenarioBatch = HasArgument(args, "--smoke-test-scenario-batch");
        var roundTripSavePath = GetArgumentValue(args, "--smoke-roundtrip-save");
        var roundTripReportPath = GetArgumentValue(args, "--smoke-roundtrip-report");
        var verifyRoundTrip = HasArgument(args, "--smoke-roundtrip-verify");
        var axisFaultPersistencePath = GetArgumentValue(args, "--smoke-axis-fault-persistence");
        var testAxisFaultScenario = HasArgument(args, "--smoke-test-axis-fault-scenario");
        var recipeGalleryCopyPath = GetArgumentValue(args, "--smoke-recipe-gallery-copy");
        var recipeGalleryState = GetArgumentValue(args, "--smoke-recipe-gallery-state");
        var recipeGalleryBaselineReportPath = GetArgumentValue(
            args,
            "--smoke-recipe-gallery-baseline-report");
        var recipeGalleryCurrentReportPath = GetArgumentValue(
            args,
            "--smoke-recipe-gallery-current-report");
        var connectionWorkbenchReportPath = GetArgumentValue(args, "--smoke-connection-workbench-report");
        var connectionWorkbenchSavePath = GetArgumentValue(args, "--smoke-connection-workbench-save");
        var cameraFirstUseReportPath = GetArgumentValue(args, "--smoke-camera-first-use-report");
        var cameraFirstUseSavePath = GetArgumentValue(args, "--smoke-camera-first-use-save");
        var cameraFirstUseState = GetArgumentValue(args, "--smoke-camera-first-use-state");
        var projectSafetyReportPath = GetArgumentValue(args, "--smoke-project-safety-report");
        var projectSafetySavePath = GetArgumentValue(args, "--smoke-project-safety-save");
        var analogIoAuthoringState = GetArgumentValue(args, "--smoke-analog-authoring-state");
        var analogIoAuthoringReportPath = GetArgumentValue(args, "--smoke-analog-authoring-report");
        var analogIoAuthoringSavePath = GetArgumentValue(args, "--smoke-analog-authoring-save");
        var projectOpenFailureDialogScreenshotPath = GetArgumentValue(
            args,
            "--smoke-project-open-failure-dialog-screenshot");

        if ((!string.IsNullOrWhiteSpace(commandTracePath)
                || HasArgument(args, "--smoke-command-trace-state"))
            && !useRunLayout)
        {
            throw new ArgumentException(
                "Command-trace smoke requires --smoke-run-layout.");
        }

        if (string.Equals(commandTraceState, "normal", StringComparison.OrdinalIgnoreCase)
            && HasArgument(args, "--smoke-command-trace-state")
            && string.IsNullOrWhiteSpace(commandTracePath))
        {
            throw new ArgumentException(
                "Normal command-trace smoke requires --smoke-command-trace.");
        }

        if ((!string.IsNullOrWhiteSpace(unifiedCommissioningEvidencePath)
                || HasArgument(args, "--smoke-unified-evidence-state"))
            && !testScenarioBatch)
        {
            throw new ArgumentException(
                "Unified commissioning evidence smoke requires --smoke-test-scenario-batch.");
        }

        if (!string.IsNullOrWhiteSpace(roundTripSavePath) && verifyRoundTrip)
        {
            throw new ArgumentException(
                "Use either --smoke-roundtrip-save or --smoke-roundtrip-verify, not both.");
        }

        if ((!string.IsNullOrWhiteSpace(roundTripSavePath) || verifyRoundTrip)
            && string.IsNullOrWhiteSpace(roundTripReportPath))
        {
            throw new ArgumentException(
                "--smoke-roundtrip-report is required for round-trip verification.");
        }

        if (!string.IsNullOrWhiteSpace(axisFaultPersistencePath) && !testAxisFaultScenario)
        {
            throw new ArgumentException(
                "--smoke-axis-fault-persistence requires --smoke-test-axis-fault-scenario.");
        }

        if (!string.IsNullOrWhiteSpace(recipeGalleryCopyPath)
            && !string.Equals(recipeGalleryState, "copy", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "--smoke-recipe-gallery-copy requires --smoke-recipe-gallery-state copy.");
        }

        if (recipeGalleryState?.StartsWith("compare", StringComparison.OrdinalIgnoreCase) == true
            && !string.Equals(recipeGalleryState, "compare-button-pressed", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(recipeGalleryBaselineReportPath)
                || string.IsNullOrWhiteSpace(recipeGalleryCurrentReportPath)))
        {
            throw new ArgumentException(
                "Report comparison states require both --smoke-recipe-gallery-baseline-report "
                + "and --smoke-recipe-gallery-current-report.");
        }

        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath)
            && string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
        {
            throw new ArgumentException(
                "--smoke-connection-workbench-save is required with --smoke-connection-workbench-report.");
        }

        var cameraFirstUseAppliedState = IsCameraFirstUseAppliedState(args);
        if (!string.IsNullOrWhiteSpace(cameraFirstUseReportPath)
            && string.IsNullOrWhiteSpace(cameraFirstUseSavePath))
        {
            throw new ArgumentException(
                "--smoke-camera-first-use-save is required with --smoke-camera-first-use-report.");
        }

        if (!string.IsNullOrWhiteSpace(cameraFirstUseSavePath)
            && string.IsNullOrWhiteSpace(cameraFirstUseReportPath))
        {
            throw new ArgumentException(
                "--smoke-camera-first-use-report is required with --smoke-camera-first-use-save.");
        }

        if (!string.IsNullOrWhiteSpace(cameraFirstUseReportPath)
            && !string.IsNullOrWhiteSpace(cameraFirstUseState)
            && !cameraFirstUseAppliedState)
        {
            throw new ArgumentException(
                "--smoke-camera-first-use-report supports the applied or keyboard-space state only.");
        }

        if (cameraFirstUseAppliedState
            && (string.IsNullOrWhiteSpace(cameraFirstUseReportPath)
                || string.IsNullOrWhiteSpace(cameraFirstUseSavePath)))
        {
            throw new ArgumentException(
                "The applied and keyboard-space camera-first-use states require both report and save paths.");
        }

        if (!string.IsNullOrWhiteSpace(projectSafetyReportPath)
            && string.IsNullOrWhiteSpace(projectSafetySavePath))
        {
            throw new ArgumentException(
                "--smoke-project-safety-save is required with --smoke-project-safety-report.");
        }

        if (!string.IsNullOrWhiteSpace(analogIoAuthoringState)
            && string.IsNullOrWhiteSpace(analogIoAuthoringReportPath))
        {
            throw new ArgumentException(
                "--smoke-analog-authoring-report is required with --smoke-analog-authoring-state.");
        }

        if (!string.IsNullOrWhiteSpace(analogIoAuthoringSavePath)
            && !string.Equals(analogIoAuthoringState, "save-reload", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "--smoke-analog-authoring-save requires --smoke-analog-authoring-state save-reload.");
        }

        if (!string.IsNullOrWhiteSpace(projectOpenFailureDialogScreenshotPath)
            && string.IsNullOrWhiteSpace(projectSafetyReportPath))
        {
            throw new ArgumentException(
                "--smoke-project-safety-report is required with "
                + "--smoke-project-open-failure-dialog-screenshot.");
        }

        if (!string.IsNullOrWhiteSpace(roundTripReportPath)
            && string.IsNullOrWhiteSpace(roundTripSavePath)
            && !verifyRoundTrip)
        {
            throw new ArgumentException(
                "--smoke-roundtrip-report requires a round-trip action.");
        }
    }
}
