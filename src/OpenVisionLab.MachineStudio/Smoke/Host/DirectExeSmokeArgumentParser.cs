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

    public static DirectExeSmokeOptions ParseSmokeOptions(IReadOnlyList<string> args)
    {
        var sizeArgument = GetArgumentValue(args, "--smoke-size") ?? "1280x760";
        return new DirectExeSmokeOptions
        {
            PerformSmokePerf = HasArgument(args, "--smoke-perf"),
            SmokePerfSampleCount = ParseIntArgument(
                GetArgumentValue(args, "--smoke-perf-samples"),
                "--smoke-perf-samples",
                defaultValue: 12,
                min: 4,
                max: 1000),
            SmokePerfReportPath = GetArgumentValue(args, "--smoke-perf-report"),
            ScreenshotPath = GetArgumentValue(args, "--smoke-screenshot"),
            LayoutReportPath = GetArgumentValue(args, "--smoke-layout-report"),
            SizeArgument = sizeArgument,
            WindowSize = ParseSize(sizeArgument),
            DpiScalePercent = ParseDpiScalePercent(GetArgumentValue(args, "--smoke-dpi")),
            SmokeLanguage = GetArgumentValue(args, "--smoke-language"),
            ProjectPath = GetArgumentValue(args, "--smoke-project"),
            EquipmentOutlineSearchText = GetArgumentValue(args, "--smoke-equipment-outline-search"),
            SelectPath = GetArgumentValue(args, "--smoke-select"),
            LayoutSelectId = GetArgumentValue(args, "--smoke-layout-select"),
            UseTopView = HasArgument(args, "--smoke-top-view"),
            LayoutSelectMany = GetArgumentValue(args, "--smoke-layout-select-many"),
            LayoutAlignment = GetArgumentValue(args, "--smoke-layout-align"),
            LayoutAlignmentReportPath = GetArgumentValue(args, "--smoke-layout-alignment-report"),
            LayoutHistoryReportPath = GetArgumentValue(args, "--smoke-layout-history-report"),
            DirectSceneReportPath = GetArgumentValue(args, "--smoke-direct-scene-report"),
            CanvasNavigationReportPath = GetArgumentValue(args, "--smoke-canvas-navigation-report"),
            DirectTransformReportPath = GetArgumentValue(args, "--smoke-direct-transform-report"),
            MultiTransformReportPath = GetArgumentValue(args, "--smoke-multi-transform-report"),
            LibraryDropReportPath = GetArgumentValue(args, "--smoke-library-drop-report"),
            LayerOrderReportPath = GetArgumentValue(args, "--smoke-layer-order-report"),
            FaultManagerReportPath = GetArgumentValue(args, "--smoke-fault-manager-report"),
            FaultManagerState = GetArgumentValue(args, "--smoke-fault-manager-state"),
            RuntimeDebuggerReportPath = GetArgumentValue(args, "--smoke-runtime-debugger-report"),
            RuntimeDebuggerState = GetArgumentValue(args, "--smoke-runtime-debugger-state"),
            DigitalIoCommissioningReportPath = GetArgumentValue(args, "--smoke-io-commissioning-report"),
            DigitalIoCommissioningState = GetArgumentValue(args, "--smoke-io-commissioning-state"),
            AnalogIoAuthoringReportPath = GetArgumentValue(args, "--smoke-analog-authoring-report"),
            AnalogIoAuthoringState = GetArgumentValue(args, "--smoke-analog-authoring-state"),
            AnalogIoAuthoringSavePath = GetArgumentValue(args, "--smoke-analog-authoring-save"),
            CameraCommissioningReportPath = GetArgumentValue(args, "--smoke-camera-commissioning-report"),
            CameraCommissioningState = GetArgumentValue(args, "--smoke-camera-commissioning-state"),
            IntegrationPanelState = GetArgumentValue(args, "--smoke-integration-panel-state"),
            IntegrationExchangeRoot = GetArgumentValue(args, "--smoke-integration-exchange-root"),
            IntegrationPanelReportPath = GetArgumentValue(args, "--smoke-integration-panel-report"),
            MmiOperatorState = GetArgumentValue(args, "--smoke-mmi-operator-state"),
            MmiOperatorReportPath = GetArgumentValue(args, "--smoke-mmi-operator-report"),
            EditCameraImageSource = HasArgument(args, "--smoke-camera-source-edit"),
            AxisCommissioningReportPath = GetArgumentValue(args, "--smoke-axis-commissioning-report"),
            AxisCommissioningState = GetArgumentValue(args, "--smoke-axis-commissioning-state"),
            MultiAxisRecipeReportPath = GetArgumentValue(args, "--smoke-multi-axis-recipe-report"),
            MultiAxisRecipeSavePath = GetArgumentValue(args, "--smoke-multi-axis-recipe-save"),
            MultiAxisRecipeState = GetArgumentValue(args, "--smoke-multi-axis-recipe-state"),
            AxisTuningState = GetArgumentValue(args, "--smoke-axis-tuning-state"),
            CylinderCommissioningReportPath = GetArgumentValue(args, "--smoke-cylinder-commissioning-report"),
            CylinderCommissioningState = GetArgumentValue(args, "--smoke-cylinder-commissioning-state"),
            ConveyorCommissioningReportPath = GetArgumentValue(args, "--smoke-conveyor-commissioning-report"),
            ConveyorCommissioningState = GetArgumentValue(args, "--smoke-conveyor-commissioning-state"),
            SensorCommissioningReportPath = GetArgumentValue(args, "--smoke-sensor-commissioning-report"),
            SensorCommissioningState = GetArgumentValue(args, "--smoke-sensor-commissioning-state"),
            LayoutClickId = GetArgumentValue(args, "--smoke-click-layout"),
            LayoutPropertyState = GetArgumentValue(args, "--smoke-layout-property-state"),
            EditMenuState = GetArgumentValue(args, "--smoke-edit-menu-state"),
            DirectSceneGestureState = GetArgumentValue(args, "--smoke-direct-scene-gesture-state"),
            GlobalCommandState = GetArgumentValue(args, "--smoke-command-state"),
            GlobalCommandStateScreenshotPath = GetArgumentValue(args, "--smoke-command-state-screenshot"),
            StartupChoiceState = GetArgumentValue(args, "--smoke-startup-choice-state"),
            RecipeGalleryState = GetArgumentValue(args, "--smoke-recipe-gallery-state"),
            RecipeGalleryCopyPath = GetArgumentValue(args, "--smoke-recipe-gallery-copy"),
            RecipeGalleryReportPath = GetArgumentValue(args, "--smoke-recipe-gallery-report"),
            RecipeGalleryCompatibilityReportPath = GetArgumentValue(
                args,
                "--smoke-recipe-gallery-compatibility-report"),
            RecipeGalleryBaselineReportPath = GetArgumentValue(
                args,
                "--smoke-recipe-gallery-baseline-report"),
            RecipeGalleryCurrentReportPath = GetArgumentValue(
                args,
                "--smoke-recipe-gallery-current-report"),
            RecipeGalleryExpectFailure = HasArgument(args, "--smoke-recipe-gallery-expect-failure"),
            ConnectionWorkbenchReportPath = GetArgumentValue(args, "--smoke-connection-workbench-report"),
            ConnectionWorkbenchSavePath = GetArgumentValue(args, "--smoke-connection-workbench-save"),
            ConnectionWorkbenchState = GetArgumentValue(args, "--smoke-connection-workbench-state"),
            CameraFirstUseReportPath = GetArgumentValue(args, "--smoke-camera-first-use-report"),
            CameraFirstUseSavePath = GetArgumentValue(args, "--smoke-camera-first-use-save"),
            CameraFirstUseState = GetArgumentValue(args, "--smoke-camera-first-use-state"),
            ProjectSafetyReportPath = GetArgumentValue(args, "--smoke-project-safety-report"),
            ProjectSafetySavePath = GetArgumentValue(args, "--smoke-project-safety-save"),
            ProjectDiagnosticsReportPath = GetArgumentValue(args, "--smoke-project-diagnostics-report"),
            ProjectDiagnosticsScreenshotPath = GetArgumentValue(args, "--smoke-project-diagnostics-screenshot"),
            SupportDiagnosticsReportPath = GetArgumentValue(args, "--smoke-support-diagnostics-report"),
            SupportDiagnosticsExportPath = GetArgumentValue(args, "--smoke-support-diagnostics-export"),
            SupportDiagnosticsScreenshotPath = GetArgumentValue(args, "--smoke-support-diagnostics-screenshot"),
            UnsavedDialogScreenshotPath = GetArgumentValue(args, "--smoke-unsaved-dialog-screenshot"),
            ProjectOpenFailureDialogScreenshotPath = GetArgumentValue(
                args,
                "--smoke-project-open-failure-dialog-screenshot"),
            EvidenceDrawerState = GetArgumentValue(args, "--smoke-evidence-state"),
            LeftToolTab = GetArgumentValue(args, "--smoke-left-tool-tab"),
            LibraryEntryState = GetArgumentValue(args, "--smoke-library-entry-state"),
            LibrarySearchText = GetArgumentValue(args, "--smoke-library-search-text"),
            LibraryCardState = GetArgumentValue(args, "--smoke-library-card-state"),
            LibraryDefaultAddKind = GetArgumentValue(args, "--smoke-library-default-add"),
            DocumentTab = GetArgumentValue(args, "--smoke-document-tab"),
            SequenceState = GetArgumentValue(args, "--smoke-sequence-state"),
            PickPlaceState = GetArgumentValue(args, "--smoke-pick-place-state"),
            RoundTripSavePath = GetArgumentValue(args, "--smoke-roundtrip-save"),
            RoundTripReportPath = GetArgumentValue(args, "--smoke-roundtrip-report"),
            VerifyRoundTrip = HasArgument(args, "--smoke-roundtrip-verify"),
            UseRunLayout = HasArgument(args, "--smoke-run-layout"),
            StartSimulation = HasArgument(args, "--smoke-start-simulation"),
            TestConditionScenario = HasArgument(args, "--smoke-test-condition-scenario"),
            TestAxisFaultScenario = HasArgument(args, "--smoke-test-axis-fault-scenario"),
            AxisFaultPersistencePath = GetArgumentValue(args, "--smoke-axis-fault-persistence"),
            TestScenarioSettingsState = GetArgumentValue(args, "--smoke-test-scenario-settings-state"),
            TestScenarioFaultKind = GetArgumentValue(args, "--smoke-test-scenario-fault-kind"),
            ShowTestScenarioSettings = HasArgument(args, "--smoke-test-scenario-settings")
                || !string.IsNullOrWhiteSpace(
                    GetArgumentValue(args, "--smoke-test-scenario-settings-state")),
            TestScenarioBatch = HasArgument(args, "--smoke-test-scenario-batch"),
            ScenarioEvidenceExchangePath = GetArgumentValue(args, "--smoke-scenario-evidence-exchange"),
            ScenarioReportPath = GetArgumentValue(args, "--smoke-scenario-report"),
            ScenarioEvidenceExchangeState = GetArgumentValue(
                args,
                "--smoke-scenario-evidence-state") ?? "normal",
            ScenarioReportState = GetArgumentValue(
                args,
                "--smoke-scenario-report-state") ?? "normal",
            UnifiedCommissioningEvidencePath = GetArgumentValue(
                args,
                "--smoke-unified-commissioning-evidence"),
            UnifiedCommissioningEvidenceState = GetArgumentValue(
                args,
                "--smoke-unified-evidence-state") ?? "normal",
            CommandTracePath = GetArgumentValue(args, "--smoke-command-trace"),
            CommandTraceState = GetArgumentValue(args, "--smoke-command-trace-state") ?? "normal",
            CommandTraceStateSpecified = HasArgument(args, "--smoke-command-trace-state"),
            SaveBatchPersistence = HasArgument(args, "--smoke-batch-persistence-save"),
            VerifyBatchPersistence = HasArgument(args, "--smoke-batch-persistence-verify"),
            VerifyStaleBatchPersistence = HasArgument(args, "--smoke-batch-persistence-stale"),
            CylinderFaultTargetId = GetArgumentValue(args, "--smoke-cylinder-fault"),
            CameraFirstUseRequested = IsCameraFirstUseRequested(args),
            IsSmokeRun = args.Any(argument =>
                argument.StartsWith("--smoke-", StringComparison.OrdinalIgnoreCase))
        };
    }

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
        var globalCommandState = GetArgumentValue(args, "--smoke-command-state");
        var globalCommandStateScreenshotPath = GetArgumentValue(args, "--smoke-command-state-screenshot");
        var commandTraceState = GetArgumentValue(args, "--smoke-command-trace-state") ?? "normal";
        var useRunLayout = HasArgument(args, "--smoke-run-layout");
        var equipmentOutlineSearchText = GetArgumentValue(args, "--smoke-equipment-outline-search");
        if (!string.IsNullOrWhiteSpace(equipmentOutlineSearchText) && !useRunLayout)
        {
            throw new ArgumentException(
                "Equipment outline search smoke requires --smoke-run-layout.");
        }
        var libraryEntryState = GetArgumentValue(args, "--smoke-library-entry-state");
        var hasSimulationLibraryEntry = useRunLayout
            && libraryEntryState?.Equals("tab", StringComparison.OrdinalIgnoreCase) == true;
        var hasEquipmentLibraryEntry = !useRunLayout
            && (libraryEntryState?.Equals("equipment-tab", StringComparison.OrdinalIgnoreCase) == true
                || libraryEntryState?.Equals("equipment-dialog", StringComparison.OrdinalIgnoreCase) == true);
        if (libraryEntryState is not null && !hasSimulationLibraryEntry && !hasEquipmentLibraryEntry)
        {
            throw new ArgumentException(
                "Library entry state smoke requires --smoke-run-layout with state 'tab' or no run-layout mode with state 'equipment-tab' or 'equipment-dialog'.");
        }
        var librarySearchText = GetArgumentValue(args, "--smoke-library-search-text");
        var hasLibrarySearchEntry = hasSimulationLibraryEntry
            || hasEquipmentLibraryEntry
            || (useRunLayout && string.Equals(
                GetArgumentValue(args, "--smoke-left-tool-tab"),
                "Library",
                StringComparison.OrdinalIgnoreCase));
        if (librarySearchText is not null && !hasLibrarySearchEntry)
        {
            throw new ArgumentException(
                "Library search smoke requires --smoke-run-layout with the Library tab or entry state 'tab', or entry state 'equipment-tab' or 'equipment-dialog'.");
        }
        var unifiedCommissioningEvidencePath = GetArgumentValue(args, "--smoke-unified-commissioning-evidence");
        var testScenarioBatch = HasArgument(args, "--smoke-test-scenario-batch");
        var scenarioReportPath = GetArgumentValue(args, "--smoke-scenario-report");
        var scenarioReportState = GetArgumentValue(args, "--smoke-scenario-report-state") ?? "normal";
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
        var projectDiagnosticsReportPath = GetArgumentValue(args, "--smoke-project-diagnostics-report");
        var projectDiagnosticsScreenshotPath = GetArgumentValue(args, "--smoke-project-diagnostics-screenshot");
        var supportDiagnosticsReportPath = GetArgumentValue(args, "--smoke-support-diagnostics-report");
        var supportDiagnosticsExportPath = GetArgumentValue(args, "--smoke-support-diagnostics-export");
        var supportDiagnosticsScreenshotPath = GetArgumentValue(args, "--smoke-support-diagnostics-screenshot");
        var analogIoAuthoringState = GetArgumentValue(args, "--smoke-analog-authoring-state");
        var analogIoAuthoringReportPath = GetArgumentValue(args, "--smoke-analog-authoring-report");
        var analogIoAuthoringSavePath = GetArgumentValue(args, "--smoke-analog-authoring-save");
        var projectOpenFailureDialogScreenshotPath = GetArgumentValue(
            args,
            "--smoke-project-open-failure-dialog-screenshot");
        var mmiOperatorState = GetArgumentValue(args, "--smoke-mmi-operator-state");
        var mmiOperatorReportPath = GetArgumentValue(args, "--smoke-mmi-operator-report");

        var capturesTopCommandPressed = globalCommandState is not null
            && new[] { "undo-pressed", "redo-pressed", "save-pressed" }
                .Contains(globalCommandState, StringComparer.OrdinalIgnoreCase);
        if (capturesTopCommandPressed != !string.IsNullOrWhiteSpace(globalCommandStateScreenshotPath))
        {
            throw new ArgumentException(
                "The top-command pressed states require --smoke-command-state-screenshot, and that path is only valid for those states.");
        }
        if (string.Equals(globalCommandState, "save-pressed", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(GetArgumentValue(args, "--smoke-project")))
        {
            throw new ArgumentException("--smoke-project is required with save-pressed command-state capture.");
        }

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

        if (!string.IsNullOrWhiteSpace(scenarioReportPath) && !testScenarioBatch)
        {
            throw new ArgumentException(
                "--smoke-scenario-report requires --smoke-test-scenario-batch.");
        }

        if (HasArgument(args, "--smoke-scenario-report-state")
            && string.IsNullOrWhiteSpace(scenarioReportPath))
        {
            throw new ArgumentException(
                "--smoke-scenario-report-state requires --smoke-scenario-report.");
        }

        var normalizedScenarioReportState = scenarioReportState.ToLowerInvariant();
        if (normalizedScenarioReportState is not ("normal" or "focus" or "hover" or "pressed" or "disabled"))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-scenario-report-state '{scenarioReportState}'. "
                + "Expected normal, focus, hover, pressed, or disabled.");
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

        if (!string.IsNullOrWhiteSpace(projectDiagnosticsReportPath)
            && string.IsNullOrWhiteSpace(GetArgumentValue(args, "--smoke-project")))
        {
            throw new ArgumentException(
                "--smoke-project is required with --smoke-project-diagnostics-report.");
        }

        if (!string.IsNullOrWhiteSpace(projectDiagnosticsScreenshotPath)
            && string.IsNullOrWhiteSpace(projectDiagnosticsReportPath))
        {
            throw new ArgumentException(
                "--smoke-project-diagnostics-report is required with "
                + "--smoke-project-diagnostics-screenshot.");
        }

        if (!string.IsNullOrWhiteSpace(supportDiagnosticsScreenshotPath)
            && string.IsNullOrWhiteSpace(supportDiagnosticsReportPath))
        {
            throw new ArgumentException(
                "--smoke-support-diagnostics-report is required with "
                + "--smoke-support-diagnostics-screenshot.");
        }

        if (!string.IsNullOrWhiteSpace(supportDiagnosticsReportPath)
            && string.IsNullOrWhiteSpace(supportDiagnosticsExportPath))
        {
            throw new ArgumentException(
                "--smoke-support-diagnostics-export is required with "
                + "--smoke-support-diagnostics-report.");
        }

        if (!string.IsNullOrWhiteSpace(supportDiagnosticsExportPath)
            && string.IsNullOrWhiteSpace(supportDiagnosticsReportPath))
        {
            throw new ArgumentException(
                "--smoke-support-diagnostics-report is required with "
                + "--smoke-support-diagnostics-export.");
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

        if (!string.IsNullOrWhiteSpace(mmiOperatorState) && !useRunLayout)
        {
            throw new ArgumentException(
                "Machine Studio inspection interaction smoke requires --smoke-run-layout.");
        }

        if (!string.IsNullOrWhiteSpace(mmiOperatorState)
            && string.IsNullOrWhiteSpace(mmiOperatorReportPath))
        {
            throw new ArgumentException(
                "--smoke-mmi-operator-report is required with --smoke-mmi-operator-state.");
        }

        if (!string.IsNullOrWhiteSpace(mmiOperatorReportPath)
            && string.IsNullOrWhiteSpace(mmiOperatorState))
        {
            throw new ArgumentException(
                "--smoke-mmi-operator-state is required with --smoke-mmi-operator-report.");
        }

        if (!string.IsNullOrWhiteSpace(mmiOperatorState)
            && !string.Equals(mmiOperatorState, "interaction", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-mmi-operator-state '{mmiOperatorState}'. "
                + "Expected interaction.");
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
