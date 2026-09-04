using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.FaultScenarios;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.Wpf.MessageDialogs;
using static OpenVisionLab.MachineStudio.DirectExeSmokeArgumentParser;
using static OpenVisionLab.MachineStudio.SmokeProjectTreeQuery;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeWorkflowReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class DirectExeSmokeHost
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const byte VirtualKeyReturn = 0x0D;
    private const byte VirtualKeyEscape = 0x1B;
    private const uint KeyEventKeyUp = 0x0002;
    private static bool _smokePointerHeld;
    private static FrameworkElement? _smokePopupContent;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        DirectExeSmokeArgumentParser.IsRequested(args);

    public static async Task RunAsync(string[] args)
    {
        var buildIdentityReportPath = GetArgumentValue(args, "--build-identity-report");
        if (!string.IsNullOrWhiteSpace(buildIdentityReportPath))
        {
            BuildIdentity.SaveReport(buildIdentityReportPath);
            Console.WriteLine($"Build identity report saved: {Path.GetFullPath(buildIdentityReportPath)}");
            Application.Current?.Shutdown(0);
            return;
        }

        var performSmokePerf = HasArgument(args, "--smoke-perf");
        var smokePerfSampleCount = ParseIntArgument(
            GetArgumentValue(args, "--smoke-perf-samples"),
            "--smoke-perf-samples",
            defaultValue: 12,
            min: 4,
            max: 1000);
        var smokePerfReportPath = GetArgumentValue(args, "--smoke-perf-report");
        var startupPerfStopwatch = performSmokePerf ? Stopwatch.StartNew() : null;

        var faultProjectPath = GetArgumentValue(args, "--fault-project");
        var faultScenarioPath = GetArgumentValue(args, "--fault-scenario");
        var faultReportPath = GetArgumentValue(args, "--fault-report");
        if (HasArgument(args, "--fault-project") ||
            HasArgument(args, "--fault-scenario") ||
            HasArgument(args, "--fault-report"))
        {
            var exitCode = await RunFaultScenarioHeadlessAsync(
                faultProjectPath,
                faultScenarioPath,
                faultReportPath);
            Application.Current?.Shutdown(exitCode);
            return;
        }

        var screenshotPath = GetArgumentValue(args, "--smoke-screenshot");
        var layoutReportPath = GetArgumentValue(args, "--smoke-layout-report");
        var sizeArg = GetArgumentValue(args, "--smoke-size") ?? "1280x760";
        var dpiScalePercent = ParseDpiScalePercent(GetArgumentValue(args, "--smoke-dpi"));
        var smokeLanguage = GetArgumentValue(args, "--smoke-language");
        if (!string.IsNullOrWhiteSpace(smokeLanguage))
        {
            OpenVisionLanguageService.SetLanguage(
                smokeLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
                    ? OpenVisionLanguage.English
                    : OpenVisionLanguage.Korean,
                save: false);
        }
        var projectPath = GetArgumentValue(args, "--smoke-project");
        var selectPath = GetArgumentValue(args, "--smoke-select");
        var layoutSelectId = GetArgumentValue(args, "--smoke-layout-select");
        var layoutSelectMany = GetArgumentValue(args, "--smoke-layout-select-many");
        var layoutAlignment = GetArgumentValue(args, "--smoke-layout-align");
        var layoutAlignmentReportPath = GetArgumentValue(args, "--smoke-layout-alignment-report");
        var layoutHistoryReportPath = GetArgumentValue(args, "--smoke-layout-history-report");
        var directSceneReportPath = GetArgumentValue(args, "--smoke-direct-scene-report");
        var canvasNavigationReportPath = GetArgumentValue(args, "--smoke-canvas-navigation-report");
        var directTransformReportPath = GetArgumentValue(args, "--smoke-direct-transform-report");
        var multiTransformReportPath = GetArgumentValue(args, "--smoke-multi-transform-report");
        var libraryDropReportPath = GetArgumentValue(args, "--smoke-library-drop-report");
        var layerOrderReportPath = GetArgumentValue(args, "--smoke-layer-order-report");
        var faultManagerReportPath = GetArgumentValue(args, "--smoke-fault-manager-report");
        var faultManagerState = GetArgumentValue(args, "--smoke-fault-manager-state");
        var runtimeDebuggerReportPath = GetArgumentValue(args, "--smoke-runtime-debugger-report");
        var runtimeDebuggerState = GetArgumentValue(args, "--smoke-runtime-debugger-state");
        var digitalIoCommissioningReportPath = GetArgumentValue(args, "--smoke-io-commissioning-report");
        var digitalIoCommissioningState = GetArgumentValue(args, "--smoke-io-commissioning-state");
        var analogIoAuthoringReportPath = GetArgumentValue(args, "--smoke-analog-authoring-report");
        var analogIoAuthoringState = GetArgumentValue(args, "--smoke-analog-authoring-state");
        var analogIoAuthoringSavePath = GetArgumentValue(args, "--smoke-analog-authoring-save");
        var cameraCommissioningReportPath = GetArgumentValue(args, "--smoke-camera-commissioning-report");
        var cameraCommissioningState = GetArgumentValue(args, "--smoke-camera-commissioning-state");
        var integrationPanelState = GetArgumentValue(args, "--smoke-integration-panel-state");
        var integrationExchangeRoot = GetArgumentValue(args, "--smoke-integration-exchange-root");
        var integrationPanelReportPath = GetArgumentValue(args, "--smoke-integration-panel-report");
        var editCameraImageSource = HasArgument(args, "--smoke-camera-source-edit");
        var axisCommissioningReportPath = GetArgumentValue(args, "--smoke-axis-commissioning-report");
        var axisCommissioningState = GetArgumentValue(args, "--smoke-axis-commissioning-state");
        var multiAxisRecipeReportPath = GetArgumentValue(args, "--smoke-multi-axis-recipe-report");
        var multiAxisRecipeSavePath = GetArgumentValue(args, "--smoke-multi-axis-recipe-save");
        var multiAxisRecipeState = GetArgumentValue(args, "--smoke-multi-axis-recipe-state");
        var axisTuningState = GetArgumentValue(args, "--smoke-axis-tuning-state");
        var cylinderCommissioningReportPath = GetArgumentValue(args, "--smoke-cylinder-commissioning-report");
        var cylinderCommissioningState = GetArgumentValue(args, "--smoke-cylinder-commissioning-state");
        var conveyorCommissioningReportPath = GetArgumentValue(args, "--smoke-conveyor-commissioning-report");
        var conveyorCommissioningState = GetArgumentValue(args, "--smoke-conveyor-commissioning-state");
        var sensorCommissioningReportPath = GetArgumentValue(args, "--smoke-sensor-commissioning-report");
        var sensorCommissioningState = GetArgumentValue(args, "--smoke-sensor-commissioning-state");
        var layoutClickId = GetArgumentValue(args, "--smoke-click-layout");
        var layoutPropertyState = GetArgumentValue(args, "--smoke-layout-property-state");
        var editMenuState = GetArgumentValue(args, "--smoke-edit-menu-state");
        var directSceneGestureState = GetArgumentValue(args, "--smoke-direct-scene-gesture-state");
        var globalCommandState = GetArgumentValue(args, "--smoke-command-state");
        var startupChoiceState = GetArgumentValue(args, "--smoke-startup-choice-state");
        var recipeGalleryState = GetArgumentValue(args, "--smoke-recipe-gallery-state");
        var recipeGalleryCopyPath = GetArgumentValue(args, "--smoke-recipe-gallery-copy");
        var recipeGalleryReportPath = GetArgumentValue(args, "--smoke-recipe-gallery-report");
        var recipeGalleryCompatibilityReportPath = GetArgumentValue(
            args,
            "--smoke-recipe-gallery-compatibility-report");
        var recipeGalleryBaselineReportPath = GetArgumentValue(
            args,
            "--smoke-recipe-gallery-baseline-report");
        var recipeGalleryCurrentReportPath = GetArgumentValue(
            args,
            "--smoke-recipe-gallery-current-report");
        var recipeGalleryExpectFailure = HasArgument(args, "--smoke-recipe-gallery-expect-failure");
        var connectionWorkbenchReportPath = GetArgumentValue(args, "--smoke-connection-workbench-report");
        var connectionWorkbenchSavePath = GetArgumentValue(args, "--smoke-connection-workbench-save");
        var connectionWorkbenchState = GetArgumentValue(args, "--smoke-connection-workbench-state");
        var cameraFirstUseReportPath = GetArgumentValue(args, "--smoke-camera-first-use-report");
        var cameraFirstUseSavePath = GetArgumentValue(args, "--smoke-camera-first-use-save");
        var cameraFirstUseState = GetArgumentValue(args, "--smoke-camera-first-use-state");
        var projectSafetyReportPath = GetArgumentValue(args, "--smoke-project-safety-report");
        var projectSafetySavePath = GetArgumentValue(args, "--smoke-project-safety-save");
        var unsavedDialogScreenshotPath = GetArgumentValue(args, "--smoke-unsaved-dialog-screenshot");
        var projectOpenFailureDialogScreenshotPath = GetArgumentValue(
            args,
            "--smoke-project-open-failure-dialog-screenshot");
        var evidenceDrawerState = GetArgumentValue(args, "--smoke-evidence-state");
        var leftToolTab = GetArgumentValue(args, "--smoke-left-tool-tab");
        var libraryCardState = GetArgumentValue(args, "--smoke-library-card-state");
        var libraryDefaultAddKind = GetArgumentValue(args, "--smoke-library-default-add");
        var documentTab = GetArgumentValue(args, "--smoke-document-tab");
        var sequenceState = GetArgumentValue(args, "--smoke-sequence-state");
        var pickPlaceState = GetArgumentValue(args, "--smoke-pick-place-state");
        var roundTripSavePath = GetArgumentValue(args, "--smoke-roundtrip-save");
        var roundTripReportPath = GetArgumentValue(args, "--smoke-roundtrip-report");
        var verifyRoundTrip = HasArgument(args, "--smoke-roundtrip-verify");
        var useRunLayout = HasArgument(args, "--smoke-run-layout");
        var startSimulation = HasArgument(args, "--smoke-start-simulation");
        var testConditionScenario = HasArgument(args, "--smoke-test-condition-scenario");
        var testAxisFaultScenario = HasArgument(args, "--smoke-test-axis-fault-scenario");
        var axisFaultPersistencePath = GetArgumentValue(args, "--smoke-axis-fault-persistence");
        var testScenarioSettingsState = GetArgumentValue(args, "--smoke-test-scenario-settings-state");
        var testScenarioFaultKind = GetArgumentValue(args, "--smoke-test-scenario-fault-kind");
        var showTestScenarioSettings = HasArgument(args, "--smoke-test-scenario-settings")
            || !string.IsNullOrWhiteSpace(testScenarioSettingsState);
        var testScenarioBatch = HasArgument(args, "--smoke-test-scenario-batch");
        var scenarioEvidenceExchangePath = GetArgumentValue(
            args,
            "--smoke-scenario-evidence-exchange");
        var scenarioEvidenceExchangeState = GetArgumentValue(
            args,
            "--smoke-scenario-evidence-state") ?? "normal";
        var unifiedCommissioningEvidencePath = GetArgumentValue(
            args,
            "--smoke-unified-commissioning-evidence");
        var unifiedCommissioningEvidenceState = GetArgumentValue(
            args,
            "--smoke-unified-evidence-state") ?? "normal";
        var commandTracePath = GetArgumentValue(args, "--smoke-command-trace");
        var commandTraceState = GetArgumentValue(args, "--smoke-command-trace-state") ?? "normal";
        var saveBatchPersistence = HasArgument(args, "--smoke-batch-persistence-save");
        var verifyBatchPersistence = HasArgument(args, "--smoke-batch-persistence-verify");
        var verifyStaleBatchPersistence = HasArgument(args, "--smoke-batch-persistence-stale");
        var cylinderFaultTargetId = GetArgumentValue(args, "--smoke-cylinder-fault");
        var (width, height) = ParseSize(sizeArg);

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(args);
        var cameraFirstUseRequested = DirectExeSmokeArgumentParser.IsCameraFirstUseRequested(args);
        var cameraFirstUseAppliedState = DirectExeSmokeArgumentParser.IsCameraFirstUseAppliedState(args);

        var initialProjectLoad = DirectExeSmokeProjectLoader.Load(
            projectPath,
            cameraFirstUseRequested,
            startupChoiceState);
        var initialProject = initialProjectLoad.Project;
        var initialProjectPath = initialProjectLoad.InitialProjectPath;
        var startupSamplePath = initialProjectLoad.StartupSamplePath;

        var vm = new MainViewModel(initialProject, initialProjectPath, startupSamplePath);
        var isSmokeRun = args.Any(argument =>
            argument.StartsWith("--smoke-", StringComparison.OrdinalIgnoreCase));
        if (isSmokeRun)
        {
            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        }

        var window = new ShellWindow
        {
            DataContext = vm,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        if (isSmokeRun)
        {
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
        }

        window.Show();
        SmokeDpiTestHook.Apply(window, dpiScalePercent, width, height);
        if (useRunLayout)
        {
            vm.IsRunMode = true;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        var uiInteraction = CreateUiInteraction(window);

        if (!string.IsNullOrWhiteSpace(analogIoAuthoringState))
        {
            var analogAuthoringReport = await SmokeAnalogIoAuthoringVerifier.VerifyAsync(
                window,
                vm,
                analogIoAuthoringState,
                analogIoAuthoringSavePath,
                screenshotPath,
                root => FindVisualDescendant<RightToolRegionView>(root),
                CaptureWindow);
            analogAuthoringReport.Save(analogIoAuthoringReportPath!);
            Console.WriteLine(
                $"Analog I/O authoring smoke " +
                $"{(analogAuthoringReport.IsValid ? "passed" : "failed")}. ");
            foreach (var failure in analogAuthoringReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            ReleaseSmokePointer();
            Application.Current?.Shutdown(analogAuthoringReport.IsValid ? 0 : 25);
            return;
        }

        if (!string.IsNullOrWhiteSpace(startupChoiceState)
            && !startupChoiceState.Equals("idle", StringComparison.OrdinalIgnoreCase))
        {
            var buttonName = startupChoiceState.StartsWith("sample", StringComparison.OrdinalIgnoreCase)
                ? "StartSampleButton"
                : "StartBlankLayoutButton";
            var button = FindVisualDescendant<Button>(
                window,
                candidate => string.Equals(candidate.Name, buttonName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Startup choice button was not available.");
            button.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            MovePointerToCenter(button);
            await Task.Delay(100);
            AssertSmoke(button.IsMouseOver, "Startup choice button did not enter hover state.");
            if (startupChoiceState.EndsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(button.IsPressed, "Startup choice button did not enter pointer-down state.");
            }
        }

        if (!string.IsNullOrWhiteSpace(commandTracePath)
            || HasArgument(args, "--smoke-command-trace-state"))
        {
            await SmokeRuntimeEvidenceVerifier.VerifyCommandTraceAsync(
                window,
                vm,
                commandTracePath,
                commandTraceState,
                uiInteraction);
        }

        var startupToIdleMs = startupPerfStopwatch?.Elapsed.TotalMilliseconds;
        SmokeProjectRoundTripReport? roundTripReport = null;
        SmokeLayoutAlignmentReport? layoutAlignmentReport = null;
        SmokeLayoutHistoryReport? layoutHistoryReport = null;
        SmokeDirectSceneAuthoringReport? directSceneReport = null;
        SmokeCanvasNavigationReport? canvasNavigationReport = null;
        SmokeDirectTransformReport? directTransformReport = null;
        SmokeMultiSelectionTransformReport? multiTransformReport = null;
        SmokeLibraryDropReport? libraryDropReport = null;
        SmokeLayerOrderReport? layerOrderReport = null;
        SmokeFaultManagerReport? faultManagerReport = null;
        SmokeRuntimeDebuggerReport? runtimeDebuggerReport = null;
        SmokeDigitalIoCommissioningReport? digitalIoCommissioningReport = null;
        SmokeCameraCommissioningReport? cameraCommissioningReport = null;
        SmokeIntegrationResultReport? integrationPanelReport = null;
        SmokeAxisCommissioningReport? axisCommissioningReport = null;
        SmokeMultiAxisCommissioningReport? multiAxisRecipeReport = null;
        SmokeCylinderCommissioningReport? cylinderCommissioningReport = null;
        SmokeConveyorCommissioningReport? conveyorCommissioningReport = null;
        SmokeSensorCommissioningReport? sensorCommissioningReport = null;
        SmokeRecipeGalleryReport? recipeGalleryReport = null;
        SmokeConnectionWorkbenchReport? connectionWorkbenchDefaultReport = null;
        SmokeLoadLockSetupReport? loadLockSetupReport = null;
        SmokeStationSkeletonReport? stationSkeletonReport = null;
        SmokeWorkflowReport? connectionWorkbenchReport = null;
        SmokeCameraFirstUseReport? cameraFirstUseReport = null;
        SmokeProjectSafetyReport? projectSafetyReport = null;

        if (!string.IsNullOrWhiteSpace(recipeGalleryState))
        {
            recipeGalleryReport = await SmokeRecipeGalleryVerifier.VerifyAsync(
                window,
                vm,
                recipeGalleryState,
                initialProject,
                recipeGalleryCopyPath,
                recipeGalleryCompatibilityReportPath,
                recipeGalleryBaselineReportPath,
                recipeGalleryCurrentReportPath,
                recipeGalleryExpectFailure,
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                () =>
                {
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                },
                MovePointerToCenter,
                (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                () => _smokePointerHeld = true);
            if (!string.IsNullOrWhiteSpace(recipeGalleryReportPath))
            {
                recipeGalleryReport.Save(recipeGalleryReportPath);
            }

            Console.WriteLine(
                $"Recipe gallery smoke {(recipeGalleryReport.IsValid ? "passed" : "failed")}.");
        }

        if (cameraFirstUseRequested)
        {
            var effectiveCameraFirstUseState = cameraFirstUseState ?? "applied";
            cameraFirstUseReport = await SmokeCameraFirstUseVerifier.VerifyAsync(
                window,
                vm,
                effectiveCameraFirstUseState,
                cameraFirstUseSavePath,
                root => FindVisualDescendant<RecipeConnectionWorkbenchView>(root),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                () =>
                {
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                },
                MovePointerToCenter,
                (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                () => _smokePointerHeld = true,
                popup => _smokePopupContent = popup,
                ReleaseSmokePointer);
            if (!string.IsNullOrWhiteSpace(cameraFirstUseReportPath))
            {
                cameraFirstUseReport.Save(cameraFirstUseReportPath);
            }

            Console.WriteLine(
                $"Camera first-use smoke {effectiveCameraFirstUseState} " +
                $"{(cameraFirstUseReport.IsValid ? "passed" : "failed")}.");
        }

        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath)
            && !(connectionWorkbenchState?.StartsWith("station-skeleton-", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(connectionWorkbenchState?.StartsWith("load-lock-", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(connectionWorkbenchState?.StartsWith("semantic-setup-", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(connectionWorkbenchState?.StartsWith("process-block-", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            connectionWorkbenchDefaultReport = await SmokeConnectionWorkbenchVerifier.VerifyAsync(
                window,
                vm,
                initialProject!,
                connectionWorkbenchSavePath!,
                (root, predicate) => FindVisualDescendant<TextBox>(root, predicate));
            connectionWorkbenchDefaultReport.Save(connectionWorkbenchReportPath);
            Console.WriteLine(
                $"Connection workbench smoke {(connectionWorkbenchDefaultReport.IsValid ? "passed" : "failed")}.");
        }
        if (!string.IsNullOrWhiteSpace(documentTab))
        {
            var document = FindVisualDescendant<SceneDocumentView>(window)
                ?? throw new InvalidOperationException("Scene document view was not available.");
            var tabs = FindVisualDescendant<TabControl>(document)
                ?? throw new InvalidOperationException("Document tabs were not available.");
            var localizedDocumentTab = documentTab switch
            {
                "Machine Layout" => OpenVisionLanguageService.T("Shell.MachineLayout"),
                "Simulation Workspace" => OpenVisionLanguageService.T("Shell.SimulationWorkspace"),
                "Sequence" => OpenVisionLanguageService.T("Shell.Sequence"),
                "Connections" => OpenVisionLanguageService.T("Connections.Tab"),
                _ => documentTab
            };
            var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                string.Equals(item.Header?.ToString(), documentTab, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    item.Header?.ToString(),
                    localizedDocumentTab,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Document tab '{documentTab}' was not available.");
            tab.IsSelected = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (!string.IsNullOrWhiteSpace(connectionWorkbenchState))
        {
            if (SmokeStationSkeletonVerifier.RequiresProjectPreparation(connectionWorkbenchState))
            {
                await SmokeStationSkeletonVerifier.PrepareProjectAsync(window, vm, initialProject);
            }

            vm.SelectedDocumentTabIndex = 1;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var workbench = FindVisualDescendant<RecipeConnectionWorkbenchView>(window)
                ?? throw new InvalidOperationException("Connection workbench was not available.");
            var addStageButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "AddConnectionStageButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Axis + stage button was not available.");
            var addRotaryStageButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "AddConnectionRotaryStageButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Rotary axis + stage button was not available.");
            var readinessButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ValidateSimulationReadinessButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Simulation readiness button was not available.");
            var dryRunButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "RunRecipeDryRunButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Recipe dry-run button was not available.");
            var checkpointTemplateButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "PreviewRecipeCheckpointTemplateButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Recipe checkpoint template button was not available.");
            var stationSkeletonButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "PreviewSemiconductorStationButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Semiconductor station button was not available.");
            var processBlockButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "PreviewProcessBlockComposerButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Process block composer button was not available.");
            var loadLockSetupButton = FindVisualDescendant<Button>(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "PreviewLoadLockSetupButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Load-lock setup button was not available.");
            var waferHandlerSetupButton = FindVisualDescendant<Button>(workbench, candidate => string.Equals(candidate.Name, "PreviewWaferHandlerSetupButton", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Wafer-handler setup button was not available.");
            var prealignerSetupButton = FindVisualDescendant<Button>(workbench, candidate => string.Equals(candidate.Name, "PreviewPrealignerSetupButton", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Pre-aligner setup button was not available.");
            var inspectionHandoffSetupButton = FindVisualDescendant<Button>(workbench, candidate => string.Equals(candidate.Name, "PreviewInspectionHandoffSetupButton", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Inspection handoff setup button was not available.");
            var inspectionSortSetupButton = FindVisualDescendant<Button>(workbench, candidate => string.Equals(candidate.Name, "PreviewInspectionSortSetupButton", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Inspection sort setup button was not available.");
            var ohtSetupButton = FindVisualDescendant<Button>(workbench, candidate => string.Equals(candidate.Name, "PreviewOhtSetupButton", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("OHT setup button was not available.");

            if (SmokeStationSkeletonVerifier.IsSupportedState(connectionWorkbenchState))
            {
                var stationResult = await SmokeStationSkeletonVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    initialProject,
                    workbench,
                    stationSkeletonButton,
                    connectionWorkbenchSavePath,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<TextBox>(root, predicate),
                    () =>
                    {
                        window.Activate();
                        SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    },
                    MovePointerToCenter,
                    (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                    () => _smokePointerHeld = true);
                if (connectionWorkbenchState.Equals("station-skeleton-applied", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                {
                    stationSkeletonReport = stationResult;
                    stationSkeletonReport.Save(connectionWorkbenchReportPath);
                }

                var stationReportRequested = connectionWorkbenchState.Equals(
                    "station-skeleton-applied",
                    StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath);
                if (!stationResult.IsValid && !stationReportRequested)
                {
                    throw new InvalidOperationException(
                        stationResult.Failures.FirstOrDefault()
                        ?? "Station skeleton smoke failed.");
                }
            }
            else if (connectionWorkbenchState.StartsWith("load-lock-", StringComparison.OrdinalIgnoreCase))
            {
                var loadLockResult = await SmokeLoadLockSetupVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    initialProject!,
                    workbench,
                    loadLockSetupButton,
                    connectionWorkbenchSavePath,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<TextBox>(root, predicate),
                    (root, predicate) => FindVisualDescendant<ComboBox>(root, predicate),
                    () =>
                    {
                        window.Activate();
                        SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    },
                    MovePointerToCenter,
                    (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                    () => _smokePointerHeld = true,
                    popup => _smokePopupContent = popup);
                if (connectionWorkbenchState.Equals("load-lock-applied", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                {
                    loadLockSetupReport = loadLockResult;
                    loadLockSetupReport.Save(connectionWorkbenchReportPath);
                }

                var loadLockReportRequested = connectionWorkbenchState.Equals(
                    "load-lock-applied",
                    StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath);
                if (!loadLockResult.IsValid && !loadLockReportRequested)
                {
                    throw new InvalidOperationException(
                        loadLockResult.Failures.FirstOrDefault()
                        ?? "Load-lock setup smoke failed.");
                }
            }
            else if (SmokeSemanticSetupVerifier.IsSupportedState(connectionWorkbenchState))
            {
                await SmokeSemanticSetupVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    initialProject,
                    workbench,
                    (root, predicate) => FindVisualDescendant<FrameworkElement>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    connectionWorkbenchSavePath);
            }
            else if (SmokeProcessBlockProposalVerifier.IsSupportedState(connectionWorkbenchState))
            {
                await SmokeProcessBlockProposalVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    initialProject,
                    workbench,
                    processBlockButton,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate),
                    () =>
                    {
                        window.Activate();
                        SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    },
                    MovePointerToCenter,
                    (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                    () => _smokePointerHeld = true);
            }
            else if (SmokeProcessBlockApplicationVerifier.IsSupportedState(connectionWorkbenchState))
            {
                var applicationContext = await SmokeProcessBlockPreparation.PrepareAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
                var applicationResult = await SmokeProcessBlockApplicationVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    applicationContext,
                    connectionWorkbenchSavePath,
                    !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath),
                    MovePointerToCenter,
                    (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                    () => _smokePointerHeld = true);
                if (applicationResult.Report is not null
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                {
                    connectionWorkbenchReport = applicationResult.Report;
                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                }
            }
            else if (SmokeProcessBlockEditVerifier.IsSupportedState(connectionWorkbenchState))
            {
                var editPreviewContext = await SmokeProcessBlockPreparation.PrepareAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
                var editAppliedContext = await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                    window,
                    vm,
                    editPreviewContext);
                var editResult = await SmokeProcessBlockEditVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    editAppliedContext,
                    connectionWorkbenchSavePath,
                    !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath));
                if (editResult.Report is not null
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                {
                    connectionWorkbenchReport = editResult.Report;
                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                }
            }
            else if (SmokeProcessBlockTimeoutVerifier.IsSupportedState(connectionWorkbenchState))
            {
                var timeoutPreviewContext = await SmokeProcessBlockPreparation.PrepareAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
                var timeoutAppliedContext = await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                    window,
                    vm,
                    timeoutPreviewContext);
                var timeoutResult = await SmokeProcessBlockTimeoutVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    timeoutAppliedContext,
                    connectionWorkbenchSavePath,
                    !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath),
                    (root, predicate) => FindVisualDescendant<TextBox>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<ItemsControl>(root, predicate),
                    MovePointerToCenter,
                    (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                    () => _smokePointerHeld = true);
                if (timeoutResult.Report is not null
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                {
                    connectionWorkbenchReport = timeoutResult.Report;
                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                }
            }
            else if (SmokeProcessBlockStepStatusVerifier.IsSupportedState(connectionWorkbenchState))
            {
                var stepStatusPreviewContext = await SmokeProcessBlockPreparation.PrepareAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
                var stepStatusAppliedContext = SmokeProcessBlockStepStatusVerifier.RequiresAppliedContext(
                    connectionWorkbenchState)
                    ? await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                        window,
                        vm,
                        stepStatusPreviewContext)
                    : null;
                var stepStatusResult = await SmokeProcessBlockStepStatusVerifier.VerifyAsync(
                    window,
                    vm,
                    connectionWorkbenchState,
                    workbench,
                    stepStatusPreviewContext,
                    stepStatusAppliedContext,
                    connectionWorkbenchSavePath,
                    !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath),
                    (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                    (root, predicate) => FindVisualDescendant<ListBox>(root, predicate),
                    (root, predicate) => FindVisualDescendant<RadioButton>(root, predicate),
                    (root, predicate) => FindVisualDescendant<TextBlock>(root, predicate),
                    MovePointerToCenter,
                    (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                    () => _smokePointerHeld = true);
                if (stepStatusResult.Report is not null
                    && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                {
                    connectionWorkbenchReport = stepStatusResult.Report;
                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                }
            }
            else if (SmokeRecipeDryRunStateVerifier.IsSupportedState(connectionWorkbenchState))
            {
                await SmokeRecipeDryRunStateVerifier.ApplyAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    dryRunButton,
                    connectionWorkbenchState,
                    connectionWorkbenchSavePath,
                    uiInteraction);
            }
            else if (SmokeRecipeConnectionStateVerifier.IsSupportedState(connectionWorkbenchState))
            {
                await SmokeRecipeConnectionStateVerifier.ApplyAsync(
                    window,
                    vm,
                    workbench,
                    addStageButton,
                    addRotaryStageButton,
                    readinessButton,
                    dryRunButton,
                    stationSkeletonButton,
                    processBlockButton,
                    loadLockSetupButton,
                    checkpointTemplateButton,
                    connectionWorkbenchState,
                    uiInteraction);
            }
            else if (SmokeProcessBlockSequenceStateVerifier.IsSupportedState(connectionWorkbenchState))
            {
                connectionWorkbenchReport = await SmokeProcessBlockSequenceStateVerifier.ApplyAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    processBlockButton,
                    projectPath,
                    connectionWorkbenchState,
                    connectionWorkbenchReportPath,
                    connectionWorkbenchSavePath,
                    uiInteraction,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate),
                    (root, predicate) => FindVisualDescendant<ListBox>(root, predicate));
            }
            else if (SmokeRecipeCheckpointStateVerifier.IsSupportedState(connectionWorkbenchState))
            {
                await SmokeRecipeCheckpointStateVerifier.ApplyAsync(
                    window,
                    vm,
                    initialProject,
                    workbench,
                    addStageButton,
                    checkpointTemplateButton,
                    connectionWorkbenchState,
                    connectionWorkbenchSavePath,
                    uiInteraction,
                    (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                    (root, predicate) => FindVisualDescendant<ListBox>(root, predicate));
            }
            else
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-connection-workbench-state '{connectionWorkbenchState}'. " +
                    "Expected a supported connection-workbench smoke state, including dry-run, dry-run-playback, or dry-run-wafer-handler-fault-playback.");
            }
        }

        if (!string.IsNullOrWhiteSpace(leftToolTab))
        {
            var leftTools = FindVisualDescendant<LeftToolRegionView>(window)
                ?? throw new InvalidOperationException("Left tool region was not available.");
            var tabs = FindVisualDescendant<TabControl>(leftTools)
                ?? throw new InvalidOperationException("Left tool tabs were not available.");
            var localizedLeftToolTab = leftToolTab switch
            {
                "Project" => OpenVisionLanguageService.T("Shell.Project"),
                "Library" => OpenVisionLanguageService.T("Shell.Library"),
                _ => leftToolTab
            };
            var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                string.Equals(item.Header?.ToString(), leftToolTab, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    item.Header?.ToString(),
                    localizedLeftToolTab,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Left tool tab '{leftToolTab}' was not available.");
            tab.IsSelected = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (!string.IsNullOrWhiteSpace(libraryCardState))
        {
            var leftTools = FindVisualDescendant<LeftToolRegionView>(window)
                ?? throw new InvalidOperationException("Left tool region was not available.");
            var libraryList = FindVisualDescendant<ListBox>(
                leftTools,
                candidate => ReferenceEquals(candidate.ItemsSource, vm.Layout.LibraryItems))
                ?? throw new InvalidOperationException("Layout library list was not available.");
            var cards = FindVisualDescendants<Button>(libraryList)
                .Where(button => button.DataContext is ComponentLibraryItem)
                .ToArray();
            AssertSmoke(cards.Length == 7, $"Expected 7 library cards; found {cards.Length}.");
            AssertSmoke(
                cards.All(button =>
                {
                    var point = button.TransformToAncestor(libraryList).Transform(new Point());
                    return button.IsVisible
                        && point.Y >= -0.5
                        && point.Y + button.ActualHeight <= libraryList.ActualHeight + 0.5;
                }),
                "Not every library card was visible without scrolling.");
            AssertSmoke(
                !FindVisualDescendants<ScrollBar>(libraryList).Any(scrollBar =>
                    scrollBar.Orientation == Orientation.Vertical && scrollBar.IsVisible),
                "The layout library exposed a vertical scrollbar.");

            var firstCard = cards[0];
            firstCard.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            MovePointerToCenter(firstCard);
            await Task.Delay(100);
            AssertSmoke(firstCard.IsMouseOver, "The layout library card did not enter hover state.");
            if (libraryCardState.Equals("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(firstCard.IsPressed, "The layout library card did not enter pointer-down state.");
            }
            else if (!libraryCardState.Equals("hover", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-library-card-state '{libraryCardState}'. Expected hover or pressed.");
            }
        }

        if (!string.IsNullOrWhiteSpace(libraryDefaultAddKind))
        {
            if (!Enum.TryParse<LayoutComponentKind>(libraryDefaultAddKind, ignoreCase: true, out var kind) ||
                !vm.TryAddLayoutComponent(kind))
            {
                throw new ArgumentException(
                    $"Unsupported or unavailable --smoke-library-default-add '{libraryDefaultAddKind}'.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(sequenceState))
        {
            await SmokeSequenceStateVerifier.ApplyAsync(window, vm, sequenceState, uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(roundTripSavePath))
        {
            var stage = vm.Layout.Items.Single(item =>
                string.Equals(item.Id, RoundTripStageId, StringComparison.Ordinal));
            stage.CurrentX = RoundTripStageX;
            vm.Layout.Select(RoundTripCylinderId);
            var componentEditor = vm.Layout.SelectedComponentEditor
                ?? throw new InvalidOperationException("Cylinder property editor was not available.");
            componentEditor.Name = RoundTripCylinderName;
            componentEditor.RotationDegrees = RoundTripCylinderRotation;
            componentEditor.Width = RoundTripCylinderWidth;
            componentEditor.Height = RoundTripCylinderHeight;
            componentEditor.CylinderExtendDurationMilliseconds = RoundTripCylinderExtendDuration;
            componentEditor.CylinderStroke = RoundTripCylinderStroke;
            if (componentEditor.HasValidationErrors)
            {
                throw new InvalidOperationException(
                    $"Edited cylinder properties were invalid: {componentEditor.ValidationMessage}");
            }
            vm.Layout.SelectMany(
                new[] { RoundTripAlignedComponentId, RoundTripCylinderId },
                RoundTripCylinderId);
            vm.Layout.AlignSelection(LayoutSelectionAlignment.HorizontalCenter);
            var step = vm.SequenceEditor.Steps.Single(item =>
                string.Equals(item.Id, RoundTripStepId, StringComparison.Ordinal));
            step.Name = RoundTripStepName;
            step.HasExpectedState = true;
            step.ExpectedTargetId = RoundTripStepCheckpointTargetId;
            step.ExpectedState = RoundTripStepCheckpointState;
            vm.SimulationWorkspace.SelectedScenarioProfile =
                vm.SimulationWorkspace.ScenarioProfiles.Single(profile =>
                    string.Equals(profile.ProfileId, RoundTripScenarioProfileId, StringComparison.Ordinal));
            vm.SimulationWorkspace.ScenarioSeed = RoundTripScenarioSeed;
            vm.SimulationWorkspace.ScenarioDurationCycles = RoundTripScenarioDuration;
            vm.SimulationWorkspace.ScenarioTargetId = RoundTripScenarioTargetId;
            SelectNode(vm.ProjectTree, "x");
            var axisEditor = vm.AxisDriveTuningEditor
                ?? throw new InvalidOperationException("Axis drive tuning editor was not available.");
            axisEditor.MaxVelocity = RoundTripAxisMaxVelocity;
            axisEditor.MaxAcceleration = RoundTripAxisMaxAcceleration;
            axisEditor.MaxDeceleration = RoundTripAxisMaxDeceleration;
            axisEditor.FollowingErrorLimit = RoundTripAxisFollowingErrorLimit;
            if (axisEditor.HasValidationErrors)
            {
                throw new InvalidOperationException(
                    $"Edited axis tuning was invalid: {axisEditor.ValidationMessage}");
            }

            await vm.SaveProjectAsync(roundTripSavePath);
            if (!await vm.OpenProjectAsync(roundTripSavePath))
            {
                throw new InvalidOperationException("Saved project could not be reloaded.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            roundTripReport = SmokeProjectRoundTripVerifier.CreateReport(
                "SaveReload",
                roundTripSavePath,
                window,
                vm);
        }
        else if (verifyRoundTrip)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException(
                    "--smoke-project is required with --smoke-roundtrip-verify.");
            }

            roundTripReport = SmokeProjectRoundTripVerifier.CreateReport(
                "Reopen",
                projectPath,
                window,
                vm);
        }

        if (roundTripReport is not null)
        {
            roundTripReport.Save(roundTripReportPath!);
            Console.WriteLine(
                $"Project round trip {roundTripReport.Phase} " +
                $"{(roundTripReport.IsValid ? "passed" : "failed")}.");
            foreach (var failure in roundTripReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        if (!string.IsNullOrEmpty(selectPath))
        {
            var selected = SelectNode(vm.ProjectTree, selectPath);
            if (selected is not null)
            {
                selected.IsSelected = true;
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(layoutSelectId))
        {
            vm.Layout.Select(layoutSelectId);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(layoutSelectMany))
        {
            var selectionIds = layoutSelectMany.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (selectionIds.Length < 2)
            {
                throw new ArgumentException(
                    "--smoke-layout-select-many requires at least two comma-separated ids.");
            }

            SelectLayoutItemsThroughScene(window, vm, selectionIds);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            if (!string.IsNullOrWhiteSpace(layoutAlignmentReportPath))
            {
                layoutAlignmentReport = SmokeLayoutAlignmentVerifier.Verify(
                    vm.Layout,
                    selectionIds,
                    layoutAlignment ?? nameof(LayoutSelectionAlignment.HorizontalCenter));
                layoutAlignmentReport.Save(layoutAlignmentReportPath);
            }
            else if (!string.IsNullOrWhiteSpace(layoutAlignment))
            {
                if (!Enum.TryParse(layoutAlignment, out LayoutSelectionAlignment alignment))
                {
                    throw new ArgumentException(
                        $"Unsupported --smoke-layout-align '{layoutAlignment}'.");
                }
                vm.Layout.AlignSelection(alignment);
            }
        }

        if (!string.IsNullOrWhiteSpace(axisTuningState))
        {
            await SmokeAxisTuningStateVerifier.ApplyAsync(window, vm, axisTuningState, uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(layoutPropertyState))
        {
            if (string.IsNullOrWhiteSpace(layoutSelectId) && string.IsNullOrWhiteSpace(layoutSelectMany))
            {
                throw new ArgumentException(
                    "--smoke-layout-property-state requires a layout selection.");
            }

            await SmokeLayoutPropertyStateVerifier.ApplyAsync(window, vm, layoutPropertyState, uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(layoutHistoryReportPath))
        {
            layoutHistoryReport = await SmokeLayoutHistoryVerifier.VerifyAsync(vm, layoutHistoryReportPath);
            layoutHistoryReport.Save(layoutHistoryReportPath);
        }

        if (!string.IsNullOrWhiteSpace(directSceneReportPath))
        {
            directSceneReport = await SmokeDirectSceneAuthoringVerifier.VerifyAsync(
                window,
                vm,
                directSceneReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            directSceneReport.Save(directSceneReportPath);
        }

        if (!string.IsNullOrWhiteSpace(canvasNavigationReportPath))
        {
            canvasNavigationReport = await SmokeCanvasNavigationVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<MachineSceneViewport>(root),
                root => FindVisualDescendant<SceneDocumentView>(root));
            canvasNavigationReport.Save(canvasNavigationReportPath);
        }

        if (!string.IsNullOrWhiteSpace(directTransformReportPath))
        {
            directTransformReport = await SmokeDirectTransformVerifier.VerifyAsync(
                window,
                vm,
                directTransformReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root),
                root => FindVisualDescendant<RightToolRegionView>(root));
            directTransformReport.Save(directTransformReportPath);
        }

        if (!string.IsNullOrWhiteSpace(multiTransformReportPath))
        {
            multiTransformReport = await SmokeMultiSelectionTransformVerifier.VerifyAsync(
                window,
                vm,
                multiTransformReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            multiTransformReport.Save(multiTransformReportPath);
        }

        if (!string.IsNullOrWhiteSpace(libraryDropReportPath))
        {
            libraryDropReport = await SmokeLibraryDropVerifier.VerifyAsync(
                window,
                vm,
                libraryDropReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            libraryDropReport.Save(libraryDropReportPath);
        }

        if (!string.IsNullOrWhiteSpace(layerOrderReportPath))
        {
            layerOrderReport = await SmokeLayerOrderVerifier.VerifyAsync(
                window,
                vm,
                layerOrderReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root),
                root => FindVisualDescendant<RightToolRegionView>(root),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<Border>(root, predicate));
            layerOrderReport.Save(layerOrderReportPath);
        }

        if (!string.IsNullOrWhiteSpace(editMenuState))
        {
            var editMenuPopup = await SmokeEditMenuStateVerifier.ApplyAsync(
                window,
                vm,
                editMenuState,
                uiInteraction);
            if (editMenuPopup is not null)
            {
                _smokePopupContent = editMenuPopup;
            }
        }

        if (!string.IsNullOrWhiteSpace(directSceneGestureState))
        {
            await ApplyDirectSceneGestureStateAsync(window, directSceneGestureState);
        }

        if (!string.IsNullOrWhiteSpace(layoutClickId))
        {
            var viewport = FindVisualDescendant<MachineSceneViewport>(window)
                ?? throw new InvalidOperationException("Machine scene viewport was not available.");
            var point = viewport.GetItemCenter(layoutClickId)
                ?? throw new InvalidOperationException(
                    $"Layout item '{layoutClickId}' was not visible in the machine scene.");
            if (!viewport.SelectItemAt(point)
                || vm.Layout.SelectedItem is not { } selectedLayoutItem
                || !string.Equals(selectedLayoutItem.Id, layoutClickId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Scene hit test did not select layout item '{layoutClickId}'.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"Scene hit test selected: {selectedLayoutItem.Name}");
        }

        var globalCommandSmokeHandled = false;
        if (startSimulation)
        {
            vm.IsRunMode = true;
            for (var attempt = 0; attempt < 20 && !vm.RunCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.RunCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Simulation ON was unavailable during the smoke run.");
            }

            if (string.Equals(globalCommandState, "abort", StringComparison.OrdinalIgnoreCase))
            {
                vm.StepCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 40 && !vm.AbortSequenceCommand.CanExecute(null);
                     attempt++)
                {
                    await Task.Delay(50);
                }

                if (!vm.AbortSequenceCommand.CanExecute(null))
                {
                    throw new InvalidOperationException(
                        "Sequence abort command was not available after simulation start.");
                }

                await SmokeGlobalCommandStateVerifier.ApplyAsync(window, vm, "abort", uiInteraction);
                globalCommandSmokeHandled = true;
            }
            else if (string.Equals(globalCommandState, "retry", StringComparison.OrdinalIgnoreCase))
            {
                vm.StepCommand.Execute(null);
                var faultedState = OpenVisionLanguageService.T("Equipment.State.Faulted");
                for (var attempt = 0;
                     attempt < 40
                     && (!vm.CanRetrySequence
                         || !string.Equals(vm.CurrentSequenceStateText, faultedState, StringComparison.Ordinal));
                     attempt++)
                {
                    await Task.Delay(50);
                }

                if (!vm.CanRetrySequence)
                {
                    throw new InvalidOperationException(
                        "Sequence retry command was not available after the deterministic fault.");
                }

                await SmokeGlobalCommandStateVerifier.ApplyAsync(window, vm, "retry", uiInteraction);
                globalCommandSmokeHandled = true;
            }
            else
            {
                vm.RunCommand.Execute(null);
                await Task.Delay(900);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        if (!string.IsNullOrWhiteSpace(pickPlaceState))
        {
            if (!startSimulation)
            {
                throw new ArgumentException(
                    "--smoke-pick-place-state requires --smoke-start-simulation.");
            }

            await SmokePickAndPlaceStateVerifier.ApplyAsync(window, vm, pickPlaceState);
        }

        if (testConditionScenario)
        {
            if (!startSimulation)
            {
                throw new ArgumentException(
                    "--smoke-test-condition-scenario requires --smoke-start-simulation.");
            }

            vm.SimulationWorkspace.IsScheduledFaultEnabled = false;
            for (var attempt = 0; attempt < 20 && !vm.StartTestScenarioCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.StartTestScenarioCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Test Scenario start was unavailable during the smoke run.");
            }

            vm.StartTestScenarioCommand.Execute(null);
            for (var attempt = 0; attempt < 20 && !vm.ConditionScenario.IsActive; attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.ConditionScenario.IsActive)
            {
                throw new InvalidOperationException("Test Scenario did not become active in the runtime snapshot.");
            }

            vm.PauseCommand.Execute(null);
            for (var attempt = 0; attempt < 20 && vm.IsRunning; attempt++)
            {
                await Task.Delay(50);
            }

            var pausedScenarioTick = vm.ConditionScenario.ExecutedTicks;
            vm.StepCommand.Execute(null);
            for (var attempt = 0; attempt < 20 && vm.ConditionScenario.ExecutedTicks <= pausedScenarioTick; attempt++)
            {
                await Task.Delay(50);
            }

            if (vm.ConditionScenario.ExecutedTicks != pausedScenarioTick + 1)
            {
                throw new InvalidOperationException(
                    $"Test Scenario Step advanced {vm.ConditionScenario.ExecutedTicks - pausedScenarioTick} ticks; expected exactly one.");
            }

            vm.ReplayTestScenarioCommand.Execute(null);
            for (var attempt = 0; attempt < 20 && vm.ConditionScenario.ExecutedTicks != 0; attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.ConditionScenario.IsActive || vm.ConditionScenario.ExecutedTicks != 0)
            {
                throw new InvalidOperationException("Test Scenario Replay did not restore the initial active state.");
            }

            vm.ResetCommand.Execute(null);
            for (var attempt = 0; attempt < 20 && vm.ConditionScenario.IsConfigured; attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.ConditionScenario.IsConfigured
                || vm.ConditionScenario.IsActive
                || vm.ConditionScenario.ExecutedTicks != 0)
            {
                throw new InvalidOperationException(
                    "Test Scenario Reset did not restore the declared initial state.");
            }

            Console.WriteLine("Test Scenario smoke passed: start, pause, one-step, replay, reset.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (testAxisFaultScenario)
        {
            if (!startSimulation)
            {
                throw new ArgumentException(
                    "--smoke-test-axis-fault-scenario requires --smoke-start-simulation.");
            }
            if (vm.ConditionScenario.IsConfigured)
            {
                throw new InvalidOperationException(
                    "Persisted Test Scenario settings started without an explicit action.");
            }

            vm.SimulationWorkspace.ScenarioTargetId = "x";
            vm.SimulationWorkspace.ScenarioDurationCycles = 2_000;
            vm.SimulationWorkspace.IsScheduledFaultEnabled = true;
            vm.SimulationWorkspace.ScheduledFaultKind = SimulationFaultKind.AxisMotionBlocked;
            vm.SimulationWorkspace.ScheduledFaultTargetId = "x";
            vm.SimulationWorkspace.ScheduledFaultInjectTick = 50;
            vm.SimulationWorkspace.ScheduledFaultHoldTicks = 3;
            vm.SimulationWorkspace.RestartSequenceAfterFault = true;
            var recoverySequenceId = vm.RecoverySequences.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException(
                    "Axis fault Test Scenario requires an authored recovery sequence.");
            vm.SimulationWorkspace.RecoverySequenceId = recoverySequenceId;
            for (var attempt = 0; attempt < 40 && !vm.StartTestScenarioCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }
            if (!vm.StartTestScenarioCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Axis fault Test Scenario start was unavailable.");
            }

            vm.StartTestScenarioCommand.Execute(null);
            for (var attempt = 0; attempt < 40 && !vm.ConditionScenario.IsActive; attempt++)
            {
                await Task.Delay(25);
            }
            for (var attempt = 0;
                 attempt < 40
                 && !vm.IsRunning;
                 attempt++)
            {
                await Task.Delay(25);
            }
            if (!vm.PauseCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Axis fault Test Scenario did not enter RealTime mode.");
            }
            vm.PauseCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 40
                 && vm.IsRunning;
                 attempt++)
            {
                await Task.Delay(25);
            }
            if (!vm.ConditionScenario.IsActive
                || vm.ConditionScenario.ExecutedTicks > 50
                || vm.IsRunning)
            {
                throw new InvalidOperationException(
                    "Axis fault Test Scenario could not be paused before its injection tick.");
            }

            while (vm.ConditionScenario.ExecutedTicks <= 50)
            {
                var before = vm.ConditionScenario.ExecutedTicks;
                vm.StepCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 20 && vm.ConditionScenario.ExecutedTicks == before;
                     attempt++)
                {
                    await Task.Delay(10);
                }
                if (vm.ConditionScenario.ExecutedTicks != before + 1)
                {
                    throw new InvalidOperationException("Axis fault scenario Step was not exactly one Tick.");
                }
            }

            var faultSnapshot = vm.SceneSnapshots.Latest ?? throw new InvalidOperationException(
                "Axis fault scenario did not publish a snapshot.");
            AssertSmoke(
                faultSnapshot.Faults.Any(fault =>
                    fault.Kind == SimulationFaultKind.AxisMotionBlocked && fault.TargetId == "x"),
                "Scheduled AxisMotionBlocked fault was not present in the immutable snapshot.");
            long pausedFaultTick = vm.ConditionScenario.ExecutedTicks;
            await Task.Delay(50);
            AssertSmoke(
                vm.ConditionScenario.ExecutedTicks == pausedFaultTick,
                "Axis fault schedule advanced while paused.");

            while (vm.ConditionScenario.ExecutedTicks <= 53)
            {
                var before = vm.ConditionScenario.ExecutedTicks;
                vm.StepCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 20 && vm.ConditionScenario.ExecutedTicks == before;
                     attempt++)
                {
                    await Task.Delay(10);
                }
            }
            AssertSmoke(
                (vm.SceneSnapshots.Latest?.Faults.Count ?? 0) == 0,
                "Scheduled axis fault did not clear after the authored hold ticks.");

            vm.StopTestScenarioCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 40 && (vm.ConditionScenario.IsActive || vm.IsRunning);
                 attempt++)
            {
                await Task.Delay(25);
            }
            AssertSmoke(
                !vm.ConditionScenario.IsActive && !vm.IsRunning,
                "Stopping an axis fault Test Scenario did not stop its owned run.");

            vm.ReplayTestScenarioCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 40 && (!vm.ConditionScenario.IsActive || !vm.IsRunning);
                 attempt++)
            {
                await Task.Delay(25);
            }
            AssertSmoke(
                vm.ConditionScenario.IsActive && vm.IsRunning,
                "Axis fault Test Scenario replay did not restore the active initial run.");

            vm.ResetCommand.Execute(null);
            for (var attempt = 0; attempt < 40 && vm.ConditionScenario.IsActive; attempt++)
            {
                await Task.Delay(25);
            }
            AssertSmoke(
                vm.ConditionScenario.IsConfigured
                && !vm.ConditionScenario.IsActive
                && vm.ConditionScenario.ExecutedTicks == 0
                && (vm.SceneSnapshots.Latest?.Faults.Count ?? 0) == 0,
                "Reset did not restore the authored initial scenario and fault state.");
            if (!string.IsNullOrWhiteSpace(axisFaultPersistencePath))
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(Path.GetFullPath(axisFaultPersistencePath))!);
                await vm.SaveProjectAsync(axisFaultPersistencePath);
                AssertSmoke(
                    await vm.OpenProjectAsync(axisFaultPersistencePath),
                    "Saved axis fault Test Scenario project could not be reopened.");
                AssertSmoke(
                    vm.SimulationWorkspace.IsScheduledFaultEnabled
                    && vm.SimulationWorkspace.ScheduledFaultKind == SimulationFaultKind.AxisMotionBlocked
                    && vm.SimulationWorkspace.ScheduledFaultTargetId == "x"
                    && vm.SimulationWorkspace.ScheduledFaultInjectTick == 50
                    && vm.SimulationWorkspace.ScheduledFaultHoldTicks == 3
                    && vm.SimulationWorkspace.RestartSequenceAfterFault
                    && vm.SimulationWorkspace.RecoverySequenceId == recoverySequenceId
                    && !vm.ConditionScenario.IsConfigured,
                    "Axis fault settings did not round-trip without auto-running.");
            }
            Console.WriteLine(
                "Axis fault Test Scenario smoke passed: explicit start, pause, exact Step, clear, recovery, reset, persistence.");
        }

        if (showTestScenarioSettings)
        {
            if (!useRunLayout)
            {
                throw new ArgumentException(
                    "--smoke-test-scenario-settings requires --smoke-run-layout.");
            }

            var testScenarioAnchor = FindVisualDescendant<TextBlock>(
                window,
                textBlock => string.Equals(
                    textBlock.Text,
                    OpenVisionLanguageService.T("Simulation.TestScenario"),
                    StringComparison.Ordinal));
            if (testScenarioAnchor is null)
            {
                throw new InvalidOperationException("Test Scenario settings were not visible.");
            }

            testScenarioAnchor.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var rightInspector = FindVisualDescendant<
                OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>(window)
                ?? throw new InvalidOperationException("Run inspector was unavailable.");

            var settingsState = testScenarioSettingsState ?? "normal";
            vm.SimulationWorkspace.RequireAutomaticCycleCompleted = true;
            vm.SimulationWorkspace.MinimumCompletedCycles = 1;
            vm.SimulationWorkspace.RequireNoActiveFaults = true;
            vm.SimulationWorkspace.RequireFinalEquipmentState = !settingsState.Equals(
                "disabled",
                StringComparison.OrdinalIgnoreCase);
            vm.SimulationWorkspace.FinalEquipmentTargetId = "cylinder-1";
            vm.SimulationWorkspace.FinalEquipmentExpectedState = settingsState.Equals(
                "validation",
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : "Extended";
            vm.SimulationWorkspace.IsScheduledFaultEnabled = !settingsState.Equals(
                "disabled",
                StringComparison.OrdinalIgnoreCase);
            vm.SimulationWorkspace.ScheduledFaultKind = testScenarioFaultKind?.ToLowerInvariant() switch
            {
                "input" => SimulationFaultKind.StuckDigitalInput,
                "cylinder" => SimulationFaultKind.CylinderTravelBlocked,
                null or "axis" => SimulationFaultKind.AxisMotionBlocked,
                _ => throw new ArgumentException(
                    $"Unsupported --smoke-test-scenario-fault-kind '{testScenarioFaultKind}'. " +
                    "Expected input, cylinder, or axis.")
            };
            vm.SimulationWorkspace.ScenarioDurationCycles = Math.Max(
                vm.SimulationWorkspace.ScenarioDurationCycles,
                500);
            vm.SimulationWorkspace.ScheduledFaultTargetId ??= vm.ScheduledFaultTargets.FirstOrDefault()?.Id;
            vm.SimulationWorkspace.ScheduledFaultInjectTick = 403;
            vm.SimulationWorkspace.ScheduledFaultHoldTicks = 3;
            vm.SimulationWorkspace.RestartSequenceAfterFault = true;
            vm.SimulationWorkspace.RecoverySequenceId ??= vm.RecoverySequences.FirstOrDefault()?.Id;
            if (settingsState.Equals("validation", StringComparison.OrdinalIgnoreCase))
            {
                vm.SimulationWorkspace.ScheduledFaultInjectTick =
                    vm.SimulationWorkspace.ScenarioDurationCycles - 1;
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            rightInspector.ScenarioAssertionsSectionAnchor.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var rightInspectorScroll = FindVisualDescendant<ScrollViewer>(
                rightInspector,
                scrollViewer => scrollViewer.IsVisible
                    && scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                    && scrollViewer.ScrollableHeight > 0);
            rightInspectorScroll?.ScrollToVerticalOffset(
                rightInspectorScroll.VerticalOffset + (width <= 1280 ? 660 : 650));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!settingsState.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                && !settingsState.Equals("validation", StringComparison.OrdinalIgnoreCase)
                && (rightInspector.ScheduledFaultInjectTickTextBox.Text != "403"
                    || rightInspector.ScheduledFaultHoldTicksTextBox.Text != "3"
                    || rightInspector.MinimumCompletedCyclesTextBox.Text != "1"
                    || rightInspector.FinalEquipmentExpectedStateTextBox.Text != "Extended"))
            {
                throw new InvalidOperationException(
                    "Test Scenario values were not rendered from the current settings.");
            }

            switch (settingsState.ToLowerInvariant())
            {
                case "normal":
                    if (!vm.SimulationWorkspace.IsScheduledFaultConfigurationValid
                        || !vm.SimulationWorkspace.IsAssertionConfigurationValid
                        || rightInspector.ScheduledFaultValidationText.IsVisible
                        || rightInspector.ScenarioAssertionValidationText.IsVisible)
                    {
                        throw new InvalidOperationException("Valid Test Scenario settings were not rendered normally.");
                    }
                    break;
                case "focus":
                    window.Activate();
                    rightInspector.FinalEquipmentExpectedStateTextBox.Focus();
                    break;
                case "hover":
                    MovePointerToCenter(rightInspector.FinalEquipmentStateAssertionCheckBox);
                    break;
                case "pressed":
                    window.Activate();
                    rightInspector.FinalEquipmentStateAssertionCheckBox.Focus();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    MovePointerToCenter(rightInspector.FinalEquipmentStateAssertionCheckBox);
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                    break;
                case "disabled":
                    if (rightInspector.ScheduledFaultTargetComboBox.IsEnabled
                        || rightInspector.FinalEquipmentTargetComboBox.IsEnabled
                        || rightInspector.FinalEquipmentExpectedStateTextBox.IsEnabled)
                    {
                        throw new InvalidOperationException("Disabled Test Scenario settings remained interactive.");
                    }
                    break;
                case "validation":
                    if (vm.SimulationWorkspace.IsScheduledFaultConfigurationValid
                        || vm.SimulationWorkspace.IsAssertionConfigurationValid
                        || !rightInspector.ScheduledFaultValidationText.IsVisible
                        || !rightInspector.ScenarioAssertionValidationText.IsVisible)
                    {
                        throw new InvalidOperationException("Invalid Test Scenario settings were not surfaced.");
                    }
                    rightInspector.ScenarioAssertionValidationText.BringIntoView();
                    break;
                case "open-popup":
                    window.Activate();
                    rightInspector.FinalEquipmentTargetComboBox.Focus();
                    rightInspector.FinalEquipmentTargetComboBox.ApplyTemplate();
                    rightInspector.FinalEquipmentTargetComboBox.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(
                        () => { },
                        DispatcherPriority.ApplicationIdle);
                    if (!rightInspector.FinalEquipmentTargetComboBox.IsDropDownOpen)
                    {
                        throw new InvalidOperationException("Assertion equipment popup did not open.");
                    }
                    var windowRoot = PresentationSource.FromVisual(window)?.RootVisual;
                    _smokePopupContent = PresentationSource.CurrentSources
                        .Cast<PresentationSource>()
                        .Select(source => source.RootVisual)
                        .OfType<FrameworkElement>()
                        .FirstOrDefault(root =>
                            !ReferenceEquals(root, windowRoot)
                            && root.IsVisible
                            && root.ActualWidth > 0
                            && root.ActualHeight > 0)
                        ?? throw new InvalidOperationException("Assertion equipment popup content was unavailable.");
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported --smoke-test-scenario-settings-state '{settingsState}'. " +
                        "Expected normal, focus, hover, pressed, disabled, validation, or open-popup.");
            }

            await Task.Delay(100);
            Console.WriteLine($"Test Scenario settings smoke passed: {settingsState}.");
        }

        if (testScenarioBatch)
        {
            if (!useRunLayout)
            {
                throw new ArgumentException(
                    "--smoke-test-scenario-batch requires --smoke-run-layout.");
            }

            await SmokeScenarioBatchVerifier.VerifyAsync(
                window,
                vm,
                projectPath,
                scenarioEvidenceExchangePath,
                scenarioEvidenceExchangeState,
                unifiedCommissioningEvidencePath,
                unifiedCommissioningEvidenceState,
                uiInteraction);

            var repeatValidationAnchor = FindVisualDescendant<TextBlock>(
                window,
                textBlock => string.Equals(
                    textBlock.Name,
                    "RepeatValidationSectionAnchor",
                    StringComparison.Ordinal));
            repeatValidationAnchor?.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var rightInspector = FindVisualDescendant<
                OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>(window);
            rightInspector?.ScenarioAssertionOutcomesPanel.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var rightInspectorScroll = rightInspector is null
                ? null
                : FindVisualDescendant<ScrollViewer>(
                    rightInspector,
                    scrollViewer => scrollViewer.IsVisible
                        && scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                        && scrollViewer.ScrollableHeight > 0);
            rightInspectorScroll?.ScrollToVerticalOffset(
                rightInspectorScroll.VerticalOffset + (width <= 1280 ? 290 : 110));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (saveBatchPersistence)
        {
            if (!useRunLayout || string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException(
                    "--smoke-batch-persistence-save requires --smoke-run-layout and --smoke-project.");
            }

            await SmokeBatchPersistenceVerifier.VerifySaveAndReloadAsync(vm, projectPath);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (verifyBatchPersistence && !vm.HasRestoredBatchArtifacts)
        {
            throw new InvalidOperationException(
                "Saved batch evidence did not restore in a new application process.");
        }

        if (verifyStaleBatchPersistence && !vm.RejectedStaleBatchArtifacts)
        {
            throw new InvalidOperationException(
                "Changed project or scenario evidence was not rejected as stale.");
        }

        if (saveBatchPersistence || verifyBatchPersistence || verifyStaleBatchPersistence)
        {
            vm.IsRunMode = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var repeatValidationAnchor = FindVisualDescendant<TextBlock>(
                window,
                textBlock => string.Equals(
                    textBlock.Name,
                    "RepeatValidationSectionAnchor",
                    StringComparison.Ordinal));
            repeatValidationAnchor?.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var rightInspector = FindVisualDescendant<
                OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>(window);
            var rightInspectorScroll = rightInspector is null
                ? null
                : FindVisualDescendant<ScrollViewer>(
                    rightInspector,
                    scrollViewer => scrollViewer.IsVisible
                        && scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                        && scrollViewer.ScrollableHeight > 0);
            rightInspectorScroll?.ScrollToVerticalOffset(
                rightInspectorScroll.VerticalOffset + (width <= 1280 ? 330 : 150));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (!string.IsNullOrWhiteSpace(cylinderFaultTargetId))
        {
            vm.FaultManager.SelectedKind = vm.FaultManager.AvailableKinds.Single(option =>
                option.Kind == SimulationFaultKind.CylinderTravelBlocked);
            vm.FaultManager.SelectedTarget = vm.FaultManager.Targets.FirstOrDefault(target =>
                string.Equals(target.Id, cylinderFaultTargetId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Cylinder fault target '{cylinderFaultTargetId}' was not available.");
            if (!vm.FaultManager.InjectCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Cylinder fault injection was unavailable.");
            }

            vm.FaultManager.InjectCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 20 && !vm.FaultManager.ActiveFaults.Any(fault =>
                     fault.Kind == SimulationFaultKind.CylinderTravelBlocked
                     && string.Equals(fault.TargetId, cylinderFaultTargetId, StringComparison.Ordinal));
                 attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.FaultManager.ActiveFaults.Any(fault =>
                    fault.Kind == SimulationFaultKind.CylinderTravelBlocked
                    && string.Equals(fault.TargetId, cylinderFaultTargetId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Cylinder fault was not published in a runtime snapshot.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(runtimeDebuggerReportPath))
        {
            runtimeDebuggerReport = await SmokeRuntimeDebuggerVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                targetWindow =>
                {
                    targetWindow.Activate();
                    SetForegroundWindow(new WindowInteropHelper(targetWindow).Handle);
                },
                MovePointerToCenter,
                () =>
                {
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                },
                ReleaseSmokePointer,
                runtimeDebuggerState);
            runtimeDebuggerReport.Save(runtimeDebuggerReportPath);
        }

        if (!string.IsNullOrWhiteSpace(faultManagerReportPath))
        {
            faultManagerReport = await SmokeFaultManagerVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                activeSection => SmokeFaultManagerVerifier.ScrollIntoViewAsync(window, activeSection));
            faultManagerReport.Save(faultManagerReportPath);
        }

        if (!string.IsNullOrWhiteSpace(faultManagerState))
        {
            await SmokeFaultManagerVerifier.ApplyStateAsync(
                window,
                vm,
                faultManagerState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(digitalIoCommissioningReportPath))
        {
            digitalIoCommissioningReport = await SmokeDigitalIoCommissioningVerifier.VerifyAsync(
                window,
                vm,
                projectPath,
                () => SmokeDigitalIoCommissioningVerifier.ScrollIntoViewAsync(window));
            digitalIoCommissioningReport.Save(digitalIoCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(digitalIoCommissioningState))
        {
            await SmokeDigitalIoCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                digitalIoCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(cameraCommissioningReportPath))
        {
            cameraCommissioningReport = await SmokeCameraCommissioningVerifier.VerifyAsync(
                window,
                vm,
                projectPath,
                editCameraImageSource,
                () => SmokeCameraCommissioningVerifier.ScrollIntoViewAsync(window));
            cameraCommissioningReport.Save(cameraCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(cameraCommissioningState))
        {
            await SmokeCameraCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                cameraCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(integrationPanelState))
        {
            integrationPanelReport = await SmokeIntegrationResultVerifier.VerifyAsync(
                window,
                vm,
                integrationPanelState,
                integrationExchangeRoot,
                root => FindVisualDescendant<RightToolRegionView>(root));
            if (!string.IsNullOrWhiteSpace(integrationPanelReportPath))
            {
                integrationPanelReport.Save(integrationPanelReportPath);
            }
        }

        if (!string.IsNullOrWhiteSpace(axisCommissioningReportPath))
        {
            axisCommissioningReport = await SmokeAxisCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeAxisCommissioningVerifier.ScrollIntoViewAsync(window));
            axisCommissioningReport.Save(axisCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(axisCommissioningState))
        {
            await SmokeAxisCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                axisCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(multiAxisRecipeReportPath))
        {
            multiAxisRecipeReport = await SmokeMultiAxisCommissioningVerifier.VerifyAsync(
                window,
                vm,
                multiAxisRecipeSavePath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            multiAxisRecipeReport.Save(multiAxisRecipeReportPath);
        }

        if (!string.IsNullOrWhiteSpace(multiAxisRecipeState))
        {
            await SmokeMultiAxisCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                multiAxisRecipeState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(cylinderCommissioningReportPath))
        {
            cylinderCommissioningReport = await SmokeCylinderCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeCylinderCommissioningVerifier.ScrollIntoViewAsync(window));
            cylinderCommissioningReport.Save(cylinderCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(cylinderCommissioningState))
        {
            await SmokeCylinderCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                cylinderCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(conveyorCommissioningReportPath))
        {
            conveyorCommissioningReport = await SmokeConveyorCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeConveyorCommissioningVerifier.ScrollIntoViewAsync(window));
            conveyorCommissioningReport.Save(conveyorCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(conveyorCommissioningState))
        {
            await SmokeConveyorCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                conveyorCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(sensorCommissioningReportPath))
        {
            sensorCommissioningReport = await SmokeSensorCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeSensorCommissioningVerifier.ScrollIntoViewAsync(window));
            sensorCommissioningReport.Save(sensorCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(sensorCommissioningState))
        {
            await SmokeSensorCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                sensorCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(evidenceDrawerState))
        {
            await SmokeEvidenceDrawerStateVerifier.ApplyAsync(
                window,
                vm,
                evidenceDrawerState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(globalCommandState) && !globalCommandSmokeHandled)
        {
            await SmokeGlobalCommandStateVerifier.ApplyAsync(window, vm, globalCommandState, uiInteraction);
        }

        if (vm.SelectedEquipmentStatus is { } selectedEquipmentStatus)
        {
            Console.WriteLine(
                $"Selected equipment status: {selectedEquipmentStatus.Name} | " +
                $"{selectedEquipmentStatus.StateText} | {selectedEquipmentStatus.ConditionText}");
        }

        if (!string.IsNullOrWhiteSpace(projectSafetyReportPath))
        {
            projectSafetyReport = await SmokeProjectSafetyVerifier.VerifyAsync(
                window,
                vm,
                projectSafetySavePath!,
                unsavedDialogScreenshotPath,
                projectOpenFailureDialogScreenshotPath,
                dpiScalePercent,
                (root, predicate) => FindVisualDescendant<TextBlock>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                target =>
                {
                    target.Activate();
                    SetForegroundWindow(new WindowInteropHelper(target).Handle);
                },
                MovePointerToCenter,
                (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
                () => _smokePointerHeld = true,
                (x, y) =>
                {
                    SetCursorPos(x, y);
                },
                Mouse.Synchronize,
                ReleaseSmokePointer,
                (target, dpi, targetWidth, targetHeight) =>
                    SmokeDpiTestHook.Apply(target, dpi, targetWidth, targetHeight),
                SmokeDpiTestHook.CaptureMonitorEvidence,
                CaptureWindow,
                key =>
                {
                    keybd_event(key, 0, 0, UIntPtr.Zero);
                    keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
                });
            if (!string.IsNullOrWhiteSpace(projectSafetyReportPath))
            {
                projectSafetyReport.Save(projectSafetyReportPath);
            }

            Console.WriteLine(
                $"Project safety smoke {(projectSafetyReport.IsValid ? "passed" : "failed")}.");
        }

        if (!string.IsNullOrWhiteSpace(unifiedCommissioningEvidencePath))
        {
            var unifiedEvidenceAnchor = FindVisualDescendant<TextBlock>(
                window,
                candidate => string.Equals(
                    candidate.Name,
                    "UnifiedCommissioningEvidenceSectionAnchor",
                    StringComparison.Ordinal));
            unifiedEvidenceAnchor?.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        SmokeLayoutReport? layoutReport = null;
        if (!string.IsNullOrEmpty(layoutReportPath))
        {
            layoutReport = SmokeLayoutValidator.Validate(
                window,
                width,
                height,
                dpiScalePercent);
            layoutReport.Save(layoutReportPath);
            Console.WriteLine(
                $"Layout validation {(layoutReport.IsValid ? "passed" : "failed")}: " +
                $"{sizeArg} at {dpiScalePercent}% DPI.");
            foreach (var failure in layoutReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        SmokePerformanceReport? smokePerfReport = null;
        if (performSmokePerf)
        {
            smokePerfReport = await SmokePerformanceVerifier.MeasureAsync(
                window,
                vm,
                sizeArg,
                dpiScalePercent,
                startupToIdleMs ?? 0,
                smokePerfSampleCount,
                smokePerfSampleCount);

            if (!string.IsNullOrWhiteSpace(smokePerfReportPath))
            {
                smokePerfReport.Save(smokePerfReportPath);
            }

            Console.WriteLine(
                $"Startup-to-idle: {smokePerfReport.StartupToIdleMs:F2} ms");
            Console.WriteLine(
                $"Navigation mean/p95: {smokePerfReport.NavigationMeanMs:F2} / " +
                $"{smokePerfReport.NavigationP95Ms:F2} ms");
            Console.WriteLine(
                $"Steady interaction mean/p95: " +
                $"{smokePerfReport.SteadyInteractionMeanMs:F2} / " +
                $"{smokePerfReport.SteadyInteractionP95Ms:F2} ms");
        }

        if (!string.IsNullOrEmpty(screenshotPath))
        {
            CaptureWindow(window, screenshotPath);
        }

        if (!string.IsNullOrEmpty(screenshotPath) ||
            !string.IsNullOrEmpty(layoutReportPath) ||
            !string.IsNullOrEmpty(smokePerfReportPath) ||
            roundTripReport is not null ||
            layoutAlignmentReport is not null ||
            layoutHistoryReport is not null ||
            directSceneReport is not null ||
            canvasNavigationReport is not null ||
            directTransformReport is not null ||
            multiTransformReport is not null ||
            libraryDropReport is not null ||
            layerOrderReport is not null ||
            runtimeDebuggerReport is not null ||
            faultManagerReport is not null ||
            digitalIoCommissioningReport is not null ||
            cameraCommissioningReport is not null ||
            integrationPanelReport is not null ||
            axisCommissioningReport is not null ||
            multiAxisRecipeReport is not null ||
            cylinderCommissioningReport is not null ||
            conveyorCommissioningReport is not null ||
            sensorCommissioningReport is not null ||
            recipeGalleryReport is not null ||
            connectionWorkbenchDefaultReport is not null ||
            loadLockSetupReport is not null ||
            stationSkeletonReport is not null ||
            connectionWorkbenchReport is not null ||
            cameraFirstUseReport is not null ||
            projectSafetyReport is not null ||
            performSmokePerf)
        {
            if (string.Equals(cameraFirstUseState, "pressed", StringComparison.OrdinalIgnoreCase)
                && _smokePointerHeld)
            {
                var workbench = FindVisualDescendant<RecipeConnectionWorkbenchView>(window);
                var cancelTarget = workbench is null
                    ? null
                    : FindVisualDescendant<Button>(workbench, candidate =>
                        string.Equals(candidate.Name, "AddConnectionStageButton", StringComparison.Ordinal));
                if (cancelTarget is not null)
                {
                    MovePointerToCenter(cancelTarget);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
            }
            ReleaseSmokePointer();

            var exitCode = layoutReport is { IsValid: false }
                ? 3
                : roundTripReport is { IsValid: false }
                    ? 4
                    : layoutAlignmentReport is { IsValid: false }
                        ? 5
                    : layoutHistoryReport is { IsValid: false }
                            ? 6
                        : directSceneReport is { IsValid: false }
                            ? 7
                        : canvasNavigationReport is { IsValid: false }
                            ? 8
                        : directTransformReport is { IsValid: false }
                            ? 9
                        : multiTransformReport is { IsValid: false }
                            ? 10
                        : libraryDropReport is { IsValid: false }
                            ? 11
                        : layerOrderReport is { IsValid: false }
                            ? 12
                        : runtimeDebuggerReport is { IsValid: false }
                            ? 23
                        : faultManagerReport is { IsValid: false }
                            ? 13
                        : cameraCommissioningReport is { IsValid: false }
                            ? 18
                        : integrationPanelReport is { IsValid: false }
                            ? 26
                        : axisCommissioningReport is { IsValid: false }
                            ? 14
                        : multiAxisRecipeReport is { IsValid: false }
                            ? 19
                        : cylinderCommissioningReport is { IsValid: false }
                            ? 15
                        : conveyorCommissioningReport is { IsValid: false }
                            ? 16
                        : sensorCommissioningReport is { IsValid: false }
                            ? 17
                        : recipeGalleryReport is { IsValid: false }
                            ? 20
                        : loadLockSetupReport is { IsValid: false }
                            ? 21
                        : stationSkeletonReport is { IsValid: false }
                            ? 21
                        : connectionWorkbenchDefaultReport is { IsValid: false }
                            ? 21
                        : connectionWorkbenchReport is { IsValid: false }
                            ? 21
                        : cameraFirstUseReport is { IsValid: false }
                            ? 24
                        : projectSafetyReport is { IsValid: false }
                            ? 22
                        : 0;
            Application.Current.Shutdown(exitCode);
        }
    }




    private static async Task<int> RunFaultScenarioHeadlessAsync(
        string? projectPath,
        string? scenarioPath,
        string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            Console.Error.WriteLine("Missing --fault-project argument.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            Console.Error.WriteLine("Missing --fault-scenario argument.");
            return 2;
        }

        var runner = new DeterministicFaultScenarioHeadlessRunner();
        var report = await runner.RunAsync(projectPath, scenarioPath, reportPath);
        if (!report.IsSuccess)
        {
            Console.Error.WriteLine($"Fault-scenario replay failed: {report.FailureReason}");
            foreach (var error in report.CompilationErrors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

            Console.WriteLine(
                $"Fault-scenario replay succeeded: " +
                $"{report.ReplayResult?.CommandResults.Count ?? 0} actions, " +
                $"{report.ReplayResult?.SnapshotHistory.Count ?? 0} snapshots, " +
                $"{report.ReplayResult?.EventHistory.Count ?? 0} events.");
            return 0;
        }

    private static SmokeUiInteraction CreateUiInteraction(ShellWindow window) =>
        new()
        {
            FindTextBlock = (root, predicate) => FindVisualDescendant<TextBlock>(root, predicate),
            FindButton = (root, predicate) => FindVisualDescendant<Button>(root, predicate),
            ActivateWindow = () =>
            {
                window.Activate();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
            },
            MovePointerToCenter = MovePointerToCenter,
            MouseEvent = (flags, dx, dy, data, extraInfo) => mouse_event(flags, dx, dy, data, extraInfo),
            SetCursorPosition = (x, y) => SetCursorPos(x, y),
            GetCursorPosition = () =>
            {
                GetCursorPos(out var point);
                return (point.X, point.Y);
            },
            SetPopupContent = popup => _smokePopupContent = popup,
            MarkSmokePointerHeld = () => _smokePointerHeld = true,
            ReleaseSmokePointer = ReleaseSmokePointer,
            CheckPointerOwnership = target =>
            {
                var isOwned = IsPointerOwnedByWindow(target, out var diagnostic);
                return (isOwned, diagnostic);
            }
        };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint GetAncestorRoot = 2;

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint dwFlags,
        uint dx,
        uint dy,
        uint dwData,
        UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    private static async Task ApplyDirectSceneGestureStateAsync(
        ShellWindow window,
        string state)
    {
        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        if (state.Equals("navigation", StringComparison.OrdinalIgnoreCase))
        {
            var center = new Point(viewport.ActualWidth * 0.54, viewport.ActualHeight * 0.48);
            viewport.ZoomAt(center, 240);
            viewport.PanBy(new Vector(64, -34));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            return;
        }

        if (state.Equals("library-drop", StringComparison.OrdinalIgnoreCase))
        {
            if (!viewport.ShowLibraryDropPreview(new Point(
                    viewport.ActualWidth * 0.72,
                    viewport.ActualHeight * 0.34)))
            {
                throw new InvalidOperationException("Library drop preview was unavailable.");
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            return;
        }

        if (!state.Equals("marquee", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-direct-scene-gesture-state '{state}'. " +
                "Expected marquee, navigation, or library-drop.");
        }

        var start = viewport.PointToScreen(new Point(12, 12));
        var end = viewport.PointToScreen(new Point(
            Math.Max(24, viewport.ActualWidth * 0.72),
            Math.Max(24, viewport.ActualHeight * 0.74)));
        SetCursorPos((int)Math.Round(start.X), (int)Math.Round(start.Y));
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        _smokePointerHeld = true;
        viewport.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        SetCursorPos((int)Math.Round(end.X), (int)Math.Round(end.Y));
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        if (window.DataContext is MainViewModel viewModel)
        {
            viewModel.Layout.SelectedItem = null;
        }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }


    private static void MovePointerToCenter(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(
            Math.Max(1, element.ActualWidth / 2),
            Math.Max(1, element.ActualHeight / 2)));
        SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
        Mouse.Synchronize();
    }

    private static bool IsPointerOwnedByWindow(Window window, out string diagnostic)
    {
        if (!GetCursorPos(out var cursorPosition))
        {
            diagnostic = "GetCursorPos failed.";
            return false;
        }

        var targetWindow = new WindowInteropHelper(window).Handle;
        var pointerWindow = GetAncestor(WindowFromPoint(cursorPosition), GetAncestorRoot);
        var foregroundWindow = GetAncestor(GetForegroundWindow(), GetAncestorRoot);
        var isOwned = targetWindow != IntPtr.Zero
            && pointerWindow == targetWindow
            && foregroundWindow == targetWindow;
        diagnostic =
            $"Target=0x{targetWindow.ToInt64():X}, " +
            $"PointerRoot=0x{pointerWindow.ToInt64():X}, " +
            $"ForegroundRoot=0x{foregroundWindow.ToInt64():X}, " +
            $"Cursor=({cursorPosition.X},{cursorPosition.Y}).";
        return isOwned;
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void ReleaseSmokePointer()
    {
        if (!_smokePointerHeld)
        {
            return;
        }

        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        _smokePointerHeld = false;
    }

    private static void SelectLayoutItemsThroughScene(
        ShellWindow window,
        MainViewModel viewModel,
        IReadOnlyList<string> selectionIds)
    {
        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        for (var index = 0; index < selectionIds.Count; index++)
        {
            var id = selectionIds[index];
            var point = viewport.GetItemCenter(id)
                ?? throw new InvalidOperationException(
                    $"Layout item '{id}' was not visible in the machine scene.");
            var selected = index == 0
                ? viewport.SelectItemAt(point)
                : viewport.RequestExtendedSelectionAt(
                    point,
                    index == 1 ? ModifierKeys.Shift : ModifierKeys.Control);
            if (!selected)
            {
                throw new InvalidOperationException(
                    $"Layout item '{id}' could not be selected through the scene.");
            }
        }

        var actualIds = viewModel.Layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (!actualIds.SetEquals(selectionIds) ||
            !string.Equals(viewModel.Layout.SelectedItem?.Id, selectionIds[^1], StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scene Ctrl/Shift selection did not match the requested set.");
        }
    }

    private static void CaptureWindow(Window window, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = checked((int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = checked((int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
        if (width < 1 || height < 1)
        {
            width = checked((int)Math.Round(window.Width * dpi.DpiScaleX));
            height = checked((int)Math.Round(window.Height * dpi.DpiScaleY));
        }

        var rendered = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rendered.Render(window);

        BitmapSource bitmap = rendered;
        if (_smokePopupContent is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } popup)
        {
            var windowOrigin = window.PointToScreen(new Point(0, 0));
            var popupOrigin = popup.PointToScreen(new Point(0, 0));
            var compositeVisual = new DrawingVisual();
            using (var drawing = compositeVisual.RenderOpen())
            {
                drawing.DrawImage(rendered, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                drawing.DrawRectangle(
                    new VisualBrush(popup),
                    null,
                    new Rect(
                        (popupOrigin.X - windowOrigin.X) / dpi.DpiScaleX,
                        (popupOrigin.Y - windowOrigin.Y) / dpi.DpiScaleY,
                        popup.ActualWidth,
                        popup.ActualHeight));
            }

            var composite = new RenderTargetBitmap(
                width,
                height,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            composite.Render(compositeVisual);
            bitmap = composite;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }

}
