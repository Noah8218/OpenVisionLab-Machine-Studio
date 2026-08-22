using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokePerformanceReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string WindowTitle { get; init; }
    public required string RequestedSize { get; init; }
    public required int RequestedScalePercent { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public required double StartupToIdleMs { get; init; }
    public required IReadOnlyList<double> NavigationTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyInteractionTimingsMs { get; init; }
    public required double NavigationMeanMs { get; init; }
    public required double NavigationP95Ms { get; init; }
    public required double SteadyInteractionMeanMs { get; init; }
    public required double SteadyInteractionP95Ms { get; init; }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, options));
    }
}

internal sealed class SmokeProjectRoundTripReport
{
    public string Schema { get; init; } = "1.3";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Phase { get; init; }
    public required string ProjectPath { get; init; }
    public required double ExpectedStageX { get; init; }
    public required double ActualStageX { get; init; }
    public required string ExpectedStepName { get; init; }
    public required string ActualStepName { get; init; }
    public required string ExpectedStepCheckpointTargetId { get; init; }
    public required string ActualStepCheckpointTargetId { get; init; }
    public required string ExpectedStepCheckpointState { get; init; }
    public required string ActualStepCheckpointState { get; init; }
    public required string ExpectedComponentName { get; init; }
    public required string ActualComponentName { get; init; }
    public required double ExpectedComponentRotation { get; init; }
    public required double ActualComponentRotation { get; init; }
    public required double ExpectedComponentWidth { get; init; }
    public required double ActualComponentWidth { get; init; }
    public required double ExpectedComponentHeight { get; init; }
    public required double ActualComponentHeight { get; init; }
    public required int ExpectedCylinderExtendDuration { get; init; }
    public required int ActualCylinderExtendDuration { get; init; }
    public required double ExpectedCylinderStroke { get; init; }
    public required double ActualCylinderStroke { get; init; }
    public required double ExpectedAxisMaxVelocity { get; init; }
    public required double ActualAxisMaxVelocity { get; init; }
    public required double ExpectedAxisMaxAcceleration { get; init; }
    public required double ActualAxisMaxAcceleration { get; init; }
    public required double ExpectedAxisMaxDeceleration { get; init; }
    public required double ActualAxisMaxDeceleration { get; init; }
    public required double ExpectedAxisFollowingErrorLimit { get; init; }
    public required double ActualAxisFollowingErrorLimit { get; init; }
    public required double ExpectedAlignedComponentX { get; init; }
    public required double ActualAlignedComponentX { get; init; }
    public required bool IsDesignMode { get; init; }
    public required bool IsRunning { get; init; }
    public required string SimulationStatus { get; init; }
    public required string AxisState { get; init; }
    public required bool HasVirtualCamera { get; init; }
    public required string CameraState { get; init; }
    public required string SequenceState { get; init; }
    public required int ActiveFaultCount { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0;

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, options));
    }
}

internal sealed class SmokeLayoutAlignmentReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<string> RequestedIds { get; init; }
    public required IReadOnlyList<string> SelectedIds { get; init; }
    public required string? PrimaryId { get; init; }
    public required string FinalAlignment { get; init; }
    public required IReadOnlyDictionary<string, double> MaximumDeviationByAlignment { get; init; }
    public required double MaximumNudgeDeviation { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0;

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal sealed class SmokeLayoutHistoryReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> PastedComponentIds { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal sealed class SmokeDirectSceneAuthoringReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
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
    private const string RoundTripStageId = "stage-1";
    private const double RoundTripStageX = 70.0;
    private const string RoundTripCylinderId = "cylinder-1";
    private const string RoundTripAlignedComponentId = "sensor-1";
    private const string RoundTripCylinderName = "Stopper Cylinder RT";
    private const double RoundTripCylinderRotation = 15.0;
    private const double RoundTripCylinderWidth = 110.0;
    private const double RoundTripCylinderHeight = 44.0;
    private const int RoundTripCylinderExtendDuration = 150;
    private const double RoundTripCylinderStroke = 65.0;
    private const double RoundTripAxisMaxVelocity = 175.0;
    private const double RoundTripAxisMaxAcceleration = 650.0;
    private const double RoundTripAxisMaxDeceleration = 575.0;
    private const double RoundTripAxisFollowingErrorLimit = 0.08;
    private const double RoundTripAlignedComponentX = 310.0;
    private const string RoundTripStepId = "cycle-active-on";
    private const string RoundTripStepName = "Cycle Active On [Roundtrip]";
    private const string RoundTripStepCheckpointTargetId = RoundTripCylinderId;
    private const string RoundTripStepCheckpointState = "Retracted";
    private const string RoundTripScenarioProfileId = "fault-injection";
    private const int RoundTripScenarioSeed = 4242;
    private const int RoundTripScenarioDuration = 37;
    private const string RoundTripScenarioTargetId = "conveyor-1";
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private static bool _smokePointerHeld;
    private static FrameworkElement? _smokePopupContent;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument =>
            argument.StartsWith("--smoke-", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--fault-", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--build-identity-report", StringComparison.OrdinalIgnoreCase));

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
        var digitalIoCommissioningReportPath = GetArgumentValue(args, "--smoke-io-commissioning-report");
        var digitalIoCommissioningState = GetArgumentValue(args, "--smoke-io-commissioning-state");
        var cameraCommissioningReportPath = GetArgumentValue(args, "--smoke-camera-commissioning-report");
        var cameraCommissioningState = GetArgumentValue(args, "--smoke-camera-commissioning-state");
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
        var projectSafetyReportPath = GetArgumentValue(args, "--smoke-project-safety-report");
        var projectSafetySavePath = GetArgumentValue(args, "--smoke-project-safety-save");
        var unsavedDialogScreenshotPath = GetArgumentValue(args, "--smoke-unsaved-dialog-screenshot");
        var evidenceDrawerState = GetArgumentValue(args, "--smoke-evidence-state");
        var leftToolTab = GetArgumentValue(args, "--smoke-left-tool-tab");
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
        var saveBatchPersistence = HasArgument(args, "--smoke-batch-persistence-save");
        var verifyBatchPersistence = HasArgument(args, "--smoke-batch-persistence-verify");
        var verifyStaleBatchPersistence = HasArgument(args, "--smoke-batch-persistence-stale");
        var cylinderFaultTargetId = GetArgumentValue(args, "--smoke-cylinder-fault");
        var (width, height) = ParseSize(sizeArg);

        if (!string.IsNullOrWhiteSpace(roundTripSavePath) && verifyRoundTrip)
        {
            throw new ArgumentException(
                "Use either --smoke-roundtrip-save or --smoke-roundtrip-verify, not both.");
        }

        if ((!string.IsNullOrWhiteSpace(roundTripSavePath) || verifyRoundTrip) &&
            string.IsNullOrWhiteSpace(roundTripReportPath))
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
                "Report comparison states require both --smoke-recipe-gallery-baseline-report " +
                "and --smoke-recipe-gallery-current-report.");
        }

        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath)
            && string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
        {
            throw new ArgumentException(
                "--smoke-connection-workbench-save is required with --smoke-connection-workbench-report.");
        }

        if (!string.IsNullOrWhiteSpace(projectSafetyReportPath)
            && string.IsNullOrWhiteSpace(projectSafetySavePath))
        {
            throw new ArgumentException(
                "--smoke-project-safety-save is required with --smoke-project-safety-report.");
        }

        if (!string.IsNullOrWhiteSpace(roundTripReportPath) &&
            string.IsNullOrWhiteSpace(roundTripSavePath) &&
            !verifyRoundTrip)
        {
            throw new ArgumentException(
                "--smoke-roundtrip-report requires a round-trip action.");
        }

        MachineProjectDocument? initialProject = null;
        string? initialProjectPath = null;
        if (!string.IsNullOrEmpty(projectPath) && File.Exists(projectPath))
        {
            var store = new ProjectDocumentStore();
            initialProject = store.Load(File.ReadAllText(projectPath));
            initialProjectPath = projectPath;
        }
        else if (string.IsNullOrEmpty(projectPath))
        {
            var bundledSamplePath = Path.Combine(
                AppContext.BaseDirectory,
                "Samples",
                "AutomaticTransferCell.ovmachine");
            if (File.Exists(bundledSamplePath))
            {
                var store = new ProjectDocumentStore();
                initialProject = store.Load(File.ReadAllText(bundledSamplePath));
            }
        }

        var vm = new MainViewModel(initialProject, initialProjectPath);
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
        var startupToIdleMs = startupPerfStopwatch?.Elapsed.TotalMilliseconds;
        SmokeProjectRoundTripReport? roundTripReport = null;
        SmokeLayoutAlignmentReport? layoutAlignmentReport = null;
        SmokeLayoutHistoryReport? layoutHistoryReport = null;
        SmokeDirectSceneAuthoringReport? directSceneReport = null;
        SmokeDirectSceneAuthoringReport? canvasNavigationReport = null;
        SmokeDirectSceneAuthoringReport? directTransformReport = null;
        SmokeDirectSceneAuthoringReport? multiTransformReport = null;
        SmokeDirectSceneAuthoringReport? libraryDropReport = null;
        SmokeDirectSceneAuthoringReport? layerOrderReport = null;
        SmokeDirectSceneAuthoringReport? faultManagerReport = null;
        SmokeDirectSceneAuthoringReport? digitalIoCommissioningReport = null;
        SmokeDirectSceneAuthoringReport? cameraCommissioningReport = null;
        SmokeDirectSceneAuthoringReport? axisCommissioningReport = null;
        SmokeDirectSceneAuthoringReport? multiAxisRecipeReport = null;
        SmokeDirectSceneAuthoringReport? cylinderCommissioningReport = null;
        SmokeDirectSceneAuthoringReport? conveyorCommissioningReport = null;
        SmokeDirectSceneAuthoringReport? sensorCommissioningReport = null;
        SmokeDirectSceneAuthoringReport? recipeGalleryReport = null;
        SmokeDirectSceneAuthoringReport? connectionWorkbenchReport = null;
        SmokeDirectSceneAuthoringReport? projectSafetyReport = null;

        if (!string.IsNullOrWhiteSpace(recipeGalleryState))
        {
            var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
            var failures = new List<string>();
            void Check(string name, bool passed)
            {
                checks[name] = passed;
                if (!passed)
                {
                    failures.Add(name);
                }
            }

            var titleBeforePreview = vm.Title;
            var runningBeforePreview = vm.IsRunning;
            var designModeBeforePreview = vm.IsDesignMode;
            var mainSnapshotBeforePreview = vm.SceneSnapshots.Latest;
            var projectStoreBeforePreview = new ProjectDocumentStore();
            var projectBeforePreview = initialProject is null
                ? string.Empty
                : projectStoreBeforePreview.Serialize(initialProject);
            vm.SemiconductorRecipes.Open();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            Check("gallery-open", vm.SemiconductorRecipes.IsOpen);
            Check("ten-bundled-recipes", vm.SemiconductorRecipes.Items.Count == 10);
            Check("default-selection", vm.SemiconductorRecipes.SelectedItem is not null);
            Check("all-topology-summaries-visible", vm.SemiconductorRecipes.Items.All(item =>
                !string.IsNullOrWhiteSpace(item.TopologySummary)));
            Check("all-distinctive-equipment-visible", vm.SemiconductorRecipes.Items.All(item =>
                !string.IsNullOrWhiteSpace(item.EquipmentFocus)));
            Check("materially-varied-count-profiles", vm.SemiconductorRecipes.Items
                .Select(item => $"{item.AxisCount}:{item.SensorCount}:{item.CylinderCount}:{item.ConveyorCount}:{item.WorkpieceCount}:{item.StepCount}")
                .Distinct(StringComparer.Ordinal)
                .Count() >= 9);
            Check("preview-title-unchanged", vm.Title == titleBeforePreview);
            Check("preview-run-state-unchanged", vm.IsRunning == runningBeforePreview);
            Check("preview-mode-unchanged", vm.IsDesignMode == designModeBeforePreview);

            if (string.Equals(recipeGalleryState, "selected-last", StringComparison.OrdinalIgnoreCase)
                || string.Equals(recipeGalleryState, "copy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(recipeGalleryState, "pressed", StringComparison.OrdinalIgnoreCase))
            {
                vm.SemiconductorRecipes.SelectedItem = vm.SemiconductorRecipes.Items.LastOrDefault();
            }
            else if (!string.Equals(recipeGalleryState, "open", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "validate-all", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "validate-focus", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "validate-pressed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "validate-disabled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compatibility-disabled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compatibility-pressed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compare", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compare-close", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compare-button-pressed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compare-close-pressed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recipeGalleryState, "compare-invalid", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-recipe-gallery-state '{recipeGalleryState}'. " +
                    "Expected open, selected-last, pressed, validate-all, validate-focus, " +
                    "validate-pressed, validate-disabled, compatibility-disabled, " +
                    "compatibility-pressed, compare, compare-close, compare-button-pressed, " +
                    "compare-close-pressed, compare-invalid, or copy.");
            }

            if (string.Equals(recipeGalleryState, "compare-button-pressed", StringComparison.OrdinalIgnoreCase))
            {
                var compareButton = FindVisualDescendant<Button>(
                    window,
                    candidate => ReferenceEquals(
                        candidate.Command,
                        vm.SemiconductorRecipes.CompareCompatibilityReportsCommand))
                    ?? throw new InvalidOperationException(
                        "Recipe gallery comparison button was not available.");
                Check("comparison-button-enabled",
                    vm.SemiconductorRecipes.CompareCompatibilityReportsCommand.CanExecute(null));
                window.Activate();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                compareButton.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("comparison-button-keyboard-focus", compareButton.IsKeyboardFocusWithin);
                MovePointerToCenter(compareButton);
                await Task.Delay(100);
                Check("comparison-button-pointer-hover", compareButton.IsMouseOver);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("comparison-button-pointer-down", compareButton.IsPressed);
            }

            if (string.Equals(recipeGalleryState, "compare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(recipeGalleryState, "compare-close", StringComparison.OrdinalIgnoreCase)
                || string.Equals(recipeGalleryState, "compare-close-pressed", StringComparison.OrdinalIgnoreCase))
            {
                Check("comparison-loads-valid-reports",
                    vm.SemiconductorRecipes.TryCompareCompatibilityReports(
                        recipeGalleryBaselineReportPath!,
                        recipeGalleryCurrentReportPath!));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Check("comparison-open", vm.SemiconductorRecipes.IsComparisonOpen);
                Check("comparison-all-results-projected", vm.SemiconductorRecipes.ComparisonItems.Count == 11);
                Check("comparison-new-failure", vm.SemiconductorRecipes.NewlyFailedCount > 0);
                Check("comparison-recovered", vm.SemiconductorRecipes.RecoveredCount > 0);
                Check("comparison-metadata-change", vm.SemiconductorRecipes.MetadataChangedCount > 0);
                Check("comparison-added", vm.SemiconductorRecipes.AddedCount > 0);
                Check("comparison-removed", vm.SemiconductorRecipes.RemovedCount > 0);
                Check("comparison-summary-visible",
                    !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.ComparisonSummary)
                    && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.ProjectSchemaComparison));
                Check("comparison-title-unchanged", vm.Title == titleBeforePreview);
                Check("comparison-run-state-unchanged", vm.IsRunning == runningBeforePreview);
                Check("comparison-mode-unchanged", vm.IsDesignMode == designModeBeforePreview);
                Check("comparison-runtime-unchanged",
                    mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime);
                Check("comparison-project-unchanged",
                    initialProject is null
                    || projectStoreBeforePreview.Serialize(initialProject) == projectBeforePreview);

                if (string.Equals(recipeGalleryState, "compare-close-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    var closeComparisonButton = FindVisualDescendant<Button>(
                        window,
                        candidate => ReferenceEquals(
                            candidate.Command,
                            vm.SemiconductorRecipes.CloseCompatibilityComparisonCommand))
                        ?? throw new InvalidOperationException(
                            "Recipe gallery close comparison button was not available.");
                    Check("comparison-close-enabled",
                        vm.SemiconductorRecipes.CloseCompatibilityComparisonCommand.CanExecute(null));
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    closeComparisonButton.Focus();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check("comparison-close-keyboard-focus", closeComparisonButton.IsKeyboardFocusWithin);
                    MovePointerToCenter(closeComparisonButton);
                    await Task.Delay(100);
                    Check("comparison-close-pointer-hover", closeComparisonButton.IsMouseOver);
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check("comparison-close-pointer-down", closeComparisonButton.IsPressed);
                }

                if (string.Equals(recipeGalleryState, "compare-close", StringComparison.OrdinalIgnoreCase))
                {
                    vm.SemiconductorRecipes.CloseCompatibilityComparisonCommand.Execute(null);
                    Check("comparison-close-restores-gallery", !vm.SemiconductorRecipes.IsComparisonOpen);
                    Check("comparison-close-clears-results", vm.SemiconductorRecipes.ComparisonItems.Count == 0);
                }
            }

            if (string.Equals(recipeGalleryState, "compare-invalid", StringComparison.OrdinalIgnoreCase))
            {
                Check("comparison-rejects-invalid-report",
                    !vm.SemiconductorRecipes.TryCompareCompatibilityReports(
                        recipeGalleryBaselineReportPath!,
                        recipeGalleryCurrentReportPath!));
                Check("comparison-invalid-error-visible", vm.SemiconductorRecipes.HasError);
                Check("comparison-invalid-remains-closed", !vm.SemiconductorRecipes.IsComparisonOpen);
                Check("comparison-invalid-project-unchanged",
                    initialProject is null
                    || projectStoreBeforePreview.Serialize(initialProject) == projectBeforePreview);
                Check("comparison-invalid-runtime-unchanged",
                    mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime);
            }

            if (string.Equals(recipeGalleryState, "compatibility-disabled", StringComparison.OrdinalIgnoreCase))
            {
                Check("compatibility-report-disabled-before-validation",
                    !vm.SemiconductorRecipes.SaveCompatibilityReportCommand.CanExecute(null));
            }

            if (string.Equals(recipeGalleryState, "compatibility-pressed", StringComparison.OrdinalIgnoreCase))
            {
                await vm.SemiconductorRecipes.ValidateAllForSmokeAsync();
                var reportButton = FindVisualDescendant<Button>(
                    window,
                    candidate => ReferenceEquals(
                        candidate.Command,
                        vm.SemiconductorRecipes.SaveCompatibilityReportCommand))
                    ?? throw new InvalidOperationException(
                        "Recipe gallery compatibility report button was not available.");
                Check("compatibility-report-enabled-after-validation",
                    vm.SemiconductorRecipes.SaveCompatibilityReportCommand.CanExecute(null));
                window.Activate();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                reportButton.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("compatibility-report-keyboard-focus", reportButton.IsKeyboardFocusWithin);
                MovePointerToCenter(reportButton);
                await Task.Delay(100);
                Check("compatibility-report-pointer-hover", reportButton.IsMouseOver);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("compatibility-report-pointer-down", reportButton.IsPressed);
            }

            if (recipeGalleryState?.StartsWith("validate-", StringComparison.OrdinalIgnoreCase) == true
                && !string.Equals(recipeGalleryState, "validate-all", StringComparison.OrdinalIgnoreCase))
            {
                var validateButton = FindVisualDescendant<Button>(
                    window,
                    candidate => ReferenceEquals(
                        candidate.Command,
                        vm.SemiconductorRecipes.ValidateAllCommand))
                    ?? throw new InvalidOperationException(
                        "Recipe gallery Validate all 10 button was not available.");
                window.Activate();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                validateButton.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("validate-button-keyboard-focus", validateButton.IsKeyboardFocusWithin);

                if (string.Equals(recipeGalleryState, "validate-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    MovePointerToCenter(validateButton);
                    await Task.Delay(100);
                    Check("validate-button-pointer-hover", validateButton.IsMouseOver);
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check("validate-button-pointer-down", validateButton.IsPressed);
                }

                if (string.Equals(recipeGalleryState, "validate-disabled", StringComparison.OrdinalIgnoreCase))
                {
                    vm.SemiconductorRecipes.ValidateAllCommand.Execute(null);
                    for (var attempt = 0; attempt < 100 && !vm.SemiconductorRecipes.IsBusy; attempt++)
                    {
                        await Task.Delay(10);
                    }

                    Check("validate-button-disabled-while-running",
                        vm.SemiconductorRecipes.IsBusy
                        && !vm.SemiconductorRecipes.ValidateAllCommand.CanExecute(null));
                    Check("create-copy-disabled-while-validating",
                        !vm.SemiconductorRecipes.CreateCopyCommand.CanExecute(null));
                    Check("close-disabled-while-validating",
                        !vm.SemiconductorRecipes.CloseCommand.CanExecute(null));
                }
            }

            if (string.Equals(recipeGalleryState, "validate-all", StringComparison.OrdinalIgnoreCase))
            {
                await vm.SemiconductorRecipes.ValidateAllForSmokeAsync();
                Check("validation-queue-completed",
                    vm.SemiconductorRecipes.ValidatedCount == vm.SemiconductorRecipes.Items.Count
                    && vm.SemiconductorRecipes.ValidatedCount == 10);
                Check("validation-all-items-terminal", vm.SemiconductorRecipes.Items.All(item =>
                    item.IsValidationPassed || item.IsValidationFailed));
                Check("validation-progress-10-of-10",
                    vm.SemiconductorRecipes.ValidationProgressText.Contains("10/10", StringComparison.Ordinal));
                Check("validation-summary-visible", vm.SemiconductorRecipes.HasValidationSummary);
                Check("validation-schema-recorded", vm.SemiconductorRecipes.Items.All(item =>
                    !string.IsNullOrWhiteSpace(item.ProjectSchema)));
                Check("validation-build-recorded", vm.SemiconductorRecipes.Items.All(item =>
                    string.Equals(item.ValidationBuildIdentity, BuildIdentity.Current, StringComparison.Ordinal)
                    && string.Equals(item.ValidationSourceCommit, BuildIdentity.SourceCommit, StringComparison.Ordinal)
                    && string.Equals(item.ValidationSourceState, BuildIdentity.SourceState, StringComparison.Ordinal)
                    && item.ValidationIsExactCommit == BuildIdentity.IsExactCommit));
                Check("compatibility-report-available",
                    vm.SemiconductorRecipes.SaveCompatibilityReportCommand.CanExecute(null));
                if (!string.IsNullOrWhiteSpace(recipeGalleryCompatibilityReportPath))
                {
                    vm.SemiconductorRecipes.SaveCompatibilityReport(recipeGalleryCompatibilityReportPath);
                    Check("compatibility-report-created",
                        File.Exists(recipeGalleryCompatibilityReportPath)
                        && new FileInfo(recipeGalleryCompatibilityReportPath).Length > 0);
                }
                if (recipeGalleryExpectFailure)
                {
                    Check("validation-first-failure-captured",
                        vm.SemiconductorRecipes.FailedCount > 0
                        && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.FirstFailureRecipeName)
                        && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.FirstFailureStepId)
                        && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.FirstFailureDetail));
                    Check("validation-first-failure-selected",
                        string.Equals(
                            vm.SemiconductorRecipes.SelectedItem?.DisplayName,
                            vm.SemiconductorRecipes.FirstFailureRecipeName,
                            StringComparison.Ordinal));
                }
                else
                {
                    Check("validation-all-ten-passed",
                        vm.SemiconductorRecipes.PassedCount == 10
                        && vm.SemiconductorRecipes.FailedCount == 0
                        && vm.SemiconductorRecipes.Items.All(item => item.IsValidationPassed));
                }

                Check("validation-title-unchanged", vm.Title == titleBeforePreview);
                Check("validation-run-state-unchanged", vm.IsRunning == runningBeforePreview);
                Check("validation-mode-unchanged", vm.IsDesignMode == designModeBeforePreview);
                Check("validation-runtime-unchanged",
                    mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime);
                Check("validation-project-unchanged",
                    initialProject is null
                    || projectStoreBeforePreview.Serialize(initialProject) == projectBeforePreview);
            }

            if (string.Equals(recipeGalleryState, "pressed", StringComparison.OrdinalIgnoreCase))
            {
                var createCopyButton = FindVisualDescendant<Button>(
                    window,
                    candidate => ReferenceEquals(
                        candidate.Command,
                        vm.SemiconductorRecipes.CreateCopyCommand))
                    ?? throw new InvalidOperationException(
                        "Recipe gallery Create a copy button was not available.");
                window.Activate();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                createCopyButton.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(createCopyButton);
                await Task.Delay(100);
                Check("create-copy-pointer-hover", createCopyButton.IsMouseOver);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("create-copy-pointer-down", createCopyButton.IsPressed);
            }

            if (string.Equals(recipeGalleryState, "copy", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(recipeGalleryCopyPath))
                {
                    throw new ArgumentException(
                        "--smoke-recipe-gallery-copy is required for copy state.");
                }

                var selectedRecipe = vm.SemiconductorRecipes.SelectedItem
                    ?? throw new InvalidOperationException("Recipe gallery selection was not available.");
                var fullCopyPath = Path.GetFullPath(recipeGalleryCopyPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullCopyPath)!);
                var store = new ProjectDocumentStore();
                var sourceProject = await store.LoadAsync(selectedRecipe.SourcePath);
                var created = await vm.SemiconductorRecipes.CreateCopyToAsync(fullCopyPath);
                var copiedProject = File.Exists(fullCopyPath)
                    ? await store.LoadAsync(fullCopyPath)
                    : null;

                Check("copy-created", created && copiedProject is not null);
                Check("copy-has-new-project-id", copiedProject?.Id != sourceProject.Id);
                Check("copy-preserves-axis-count", copiedProject?.Axes.Count == sourceProject.Axes.Count);
                Check("copy-preserves-device-count", copiedProject?.Devices.Count == sourceProject.Devices.Count);
                Check("copy-preserves-channel-count", copiedProject?.Channels.Count == sourceProject.Channels.Count);
                Check("copy-preserves-sequence-count", copiedProject?.Sequences.Count == sourceProject.Sequences.Count);
                Check("copy-opens-in-design-mode", vm.IsDesignMode);
                Check("copy-does-not-run-simulation", !vm.IsRunning);
                Check("copy-becomes-current-project",
                    vm.Title.EndsWith(Path.GetFileNameWithoutExtension(fullCopyPath), StringComparison.Ordinal));
                Check("gallery-closes-after-copy", !vm.SemiconductorRecipes.IsOpen);
            }

            recipeGalleryReport = new SmokeDirectSceneAuthoringReport
            {
                Checks = checks,
                Failures = failures
            };
            if (!string.IsNullOrWhiteSpace(recipeGalleryReportPath))
            {
                recipeGalleryReport.Save(recipeGalleryReportPath);
            }

            Console.WriteLine(
                $"Recipe gallery smoke {(recipeGalleryReport.IsValid ? "passed" : "failed")}.");
        }

        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath)
            && !(connectionWorkbenchState?.StartsWith("station-skeleton-", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(connectionWorkbenchState?.StartsWith("load-lock-", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(connectionWorkbenchState?.StartsWith("semantic-setup-", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(connectionWorkbenchState?.StartsWith("process-block-", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
            var failures = new List<string>();
            void Check(string name, bool passed)
            {
                checks[name] = passed;
                if (!passed)
                {
                    failures.Add(name);
                }
            }

            var bundledRecipePaths = Directory
                .EnumerateFiles(
                    Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                    "*.ovmachine")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Check("ten-bundled-recipe-projections", bundledRecipePaths.Length == 10);
            Check("all-bundled-recipe-connections-valid", bundledRecipePaths.All(path =>
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
                var projection = new RecipeConnectionWorkbenchViewModel(
                    _ => { },
                    (_, _) => { },
                    _ => null,
                    () => null,
                    (sequenceId, stepId, componentId) =>
                        new DeterministicSequenceStepPreviewRunner().RunAsync(
                            project,
                            sequenceId,
                            stepId,
                            componentId),
                    sequenceId => new DeterministicRecipeDryRunRunner().RunAsync(
                        project,
                        sequenceId),
                    _ => { },
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => { });
                projection.Load(project);
                return projection.HasRows && projection.Rows.All(row => row.IsValid);
            }));
            Check("all-bundled-recipes-simulation-ready", bundledRecipePaths.All(path =>
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
                var fixedStep = TimeSpan.FromMilliseconds(project.Simulation.FixedStepMilliseconds);
                return new MachineProjectRuntimeCompiler(fixedStep).Compile(project).IsSuccess;
            }));

            var previewRow = vm.RecipeConnections.Rows.FirstOrDefault(row =>
                row.Kind == LayoutComponentKind.PneumaticCylinder && row.CanPreviewSequenceStep);
            Check("preview-disabled-before-readiness",
                previewRow is not null
                && !vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow));
            Check("dry-run-disabled-before-readiness",
                !vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null));
            var projectStore = new ProjectDocumentStore();
            var projectBeforePreview = projectStore.Serialize(initialProject!);
            var mainSnapshotBeforePreview = vm.SceneSnapshots.Latest;
            var runningBeforePreview = vm.IsRunning;
            var designModeBeforePreview = vm.IsDesignMode;
            vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
            Check("preview-enabled-after-readiness",
                previewRow is not null
                && vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow));
            Check("dry-run-enabled-after-readiness",
                vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null));
            if (previewRow is not null)
            {
                vm.RecipeConnections.PreviewSequenceStepCommand.Execute(previewRow);
                for (var attempt = 0; attempt < 100 && !previewRow.HasPreviewResult; attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
            }

            var previewResult = previewRow?.PreviewResult;
            var mainSnapshotAfterPreview = vm.SceneSnapshots.Latest;
            Check("preview-completed", previewResult?.Outcome == SequenceStepPreviewOutcome.Completed);
            Check("preview-bounded",
                previewResult is { ExecutedTicks: > 0 }
                && previewResult.ExecutedTicks < previewResult.MaximumTicks);
            Check("preview-observed-cylinder-extended",
                previewResult?.FinalSnapshot?.LayoutComponents.FirstOrDefault(component =>
                    component.Id == previewRow?.ComponentId)?.CylinderState
                    == PneumaticCylinderState.Extended);
            Check("preview-main-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == mainSnapshotAfterPreview?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == mainSnapshotAfterPreview?.SimulationTime
                && vm.IsRunning == runningBeforePreview
                && vm.IsDesignMode == designModeBeforePreview);
            Check("preview-project-unchanged",
                projectBeforePreview == projectStore.Serialize(initialProject!));

            vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                 attempt++)
            {
                await Task.Delay(20);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            var dryRunResult = vm.RecipeConnections.RecipeDryRunResult;
            var mainSnapshotAfterDryRun = vm.SceneSnapshots.Latest;
            Check("dry-run-completed", dryRunResult?.Outcome == RecipeDryRunOutcome.Completed);
            Check("dry-run-bounded",
                dryRunResult is { ExecutedTicks: > 0 }
                && dryRunResult.ExecutedTicks < dryRunResult.MaximumTicks);
            Check(
                "dry-run-timeline-complete",
                dryRunResult?.Timeline.Count == vm.RecipeConnections.RecipeStepCount
                    - (initialProject?.Devices.Any(device =>
                        device.InspectionSortRouter is not null) == true ? 3 : 0));
            Check("dry-run-no-issue", dryRunResult?.FirstIssue is null);
            Check("dry-run-final-cylinder-retracted",
                dryRunResult?.FinalSnapshot?.LayoutComponents.FirstOrDefault(component =>
                    component.Kind == LayoutComponentKind.PneumaticCylinder)?.CylinderState
                    == PneumaticCylinderState.Retracted);
            Check("dry-run-final-conveyor-stopped",
                dryRunResult?.FinalSnapshot?.LayoutComponents.FirstOrDefault(component =>
                    component.Kind == LayoutComponentKind.Conveyor)?.ConveyorRunning == false);
            Check("dry-run-main-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == mainSnapshotAfterDryRun?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == mainSnapshotAfterDryRun?.SimulationTime
                && vm.IsRunning == runningBeforePreview
                && vm.IsDesignMode == designModeBeforePreview);
            Check("dry-run-project-unchanged",
                projectBeforePreview == projectStore.Serialize(initialProject!));
            var dryRunNavigationStep = vm.RecipeConnections.RecipeDryRunTimeline.FirstOrDefault(step =>
                string.Equals(step.StepId, "wait-process-position", StringComparison.Ordinal));
            vm.RecipeConnections.SelectedRecipeDryRunStep = dryRunNavigationStep;
            Check("dry-run-timeline-selects-connection",
                dryRunNavigationStep?.ComponentId is not null
                && vm.RecipeConnections.SelectedRow?.ComponentId == dryRunNavigationStep.ComponentId
                && vm.Layout.SelectedItem?.Id == dryRunNavigationStep.ComponentId);
            vm.SelectedDocumentTabIndex = 1;
            vm.RecipeConnections.OpenRecipeDryRunStepCommand.Execute(dryRunNavigationStep);
            Check("dry-run-timeline-opens-exact-sequence",
                vm.SelectedDocumentTabIndex == 2
                && vm.SequenceEditor.SelectedSequence?.Id == dryRunNavigationStep?.SequenceId
                && vm.SequenceEditor.SelectedStep?.Id == dryRunNavigationStep?.StepId);
            Check("dry-run-navigation-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                && vm.IsRunning == runningBeforePreview
                && vm.IsDesignMode == designModeBeforePreview);
            Check("dry-run-navigation-project-unchanged",
                projectBeforePreview == projectStore.Serialize(initialProject!));
            var dryRunPlaybackStep = vm.RecipeConnections.RecipeDryRunTimeline.FirstOrDefault(step =>
                string.Equals(step.StepId, "wait-cylinder-extended", StringComparison.Ordinal));
            vm.SelectedDocumentTabIndex = 1;
            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(dryRunPlaybackStep);
            Check("dry-run-playback-opens-read-only-layout",
                dryRunPlaybackStep is not null
                && vm.IsDryRunPlaybackActive
                && vm.SelectedDocumentTabIndex == 0
                && !vm.IsSceneEditable
                && !vm.Layout.IsEditable);
            Check("dry-run-playback-uses-isolated-boundary",
                ReferenceEquals(vm.SceneSnapshotSource.Latest, dryRunPlaybackStep?.BoundarySnapshot)
                && !ReferenceEquals(vm.SceneSnapshotSource, vm.SceneSnapshots));
            var playbackPropertyEditor = FindVisualDescendant<TextBox>(window, candidate =>
                string.Equals(candidate.Name, "ComponentNameTextBox", StringComparison.Ordinal));
            Check("dry-run-playback-inspector-read-only",
                playbackPropertyEditor is not null
                && !playbackPropertyEditor.IsEnabled
                && vm.SelectedEquipmentStatus?.StateText
                    == OpenVisionLanguageService.T("Equipment.State.Extended"));
            var playbackIndex = vm.RecipeConnections.RecipeDryRunTimeline.IndexOf(dryRunPlaybackStep!);
            vm.NextDryRunPlaybackStepCommand.Execute(null);
            var nextPlaybackStep = vm.RecipeConnections.RecipeDryRunTimeline[playbackIndex + 1];
            Check("dry-run-playback-next-boundary",
                ReferenceEquals(vm.SceneSnapshotSource.Latest, nextPlaybackStep.BoundarySnapshot)
                && ReferenceEquals(vm.RecipeConnections.SelectedRecipeDryRunStep, nextPlaybackStep));
            vm.PreviousDryRunPlaybackStepCommand.Execute(null);
            Check("dry-run-playback-previous-boundary",
                ReferenceEquals(vm.SceneSnapshotSource.Latest, dryRunPlaybackStep?.BoundarySnapshot)
                && ReferenceEquals(vm.RecipeConnections.SelectedRecipeDryRunStep, dryRunPlaybackStep));
            Check("dry-run-playback-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                && vm.IsRunning == runningBeforePreview
                && vm.IsDesignMode == designModeBeforePreview);
            Check("dry-run-playback-project-unchanged",
                projectBeforePreview == projectStore.Serialize(initialProject!));
            vm.ExitDryRunPlaybackCommand.Execute(null);
            Check("dry-run-playback-exit-restores-layout",
                !vm.IsDryRunPlaybackActive
                && vm.IsSceneEditable
                && vm.Layout.IsEditable
                && ReferenceEquals(vm.SceneSnapshotSource, vm.SceneSnapshots)
                && playbackPropertyEditor?.IsEnabled == true);

            var componentCountBefore = vm.Layout.Items.Count(item => item.Component is not null);
            var axisCountBefore = vm.RecipeConnections.Rows.Count(row =>
                row.Kind == LayoutComponentKind.LinearStage);
            var rotaryAxisCountBefore = vm.RecipeConnections.Rows.Count(row =>
                row.Kind == LayoutComponentKind.RotaryStage);
            var runningBefore = vm.IsRunning;
            var designModeBefore = vm.IsDesignMode;
            var stepCountBefore = vm.SequenceEditor.Steps.Count;

            Check("add-axis-stage", vm.TryAddLayoutComponent(LayoutComponentKind.LinearStage));
            var stageId = vm.Layout.SelectedItem?.Id;
            Check("stage-selected", !string.IsNullOrWhiteSpace(stageId));
            Check("add-targeted-sensor", vm.TryAddLayoutComponent(LayoutComponentKind.DigitalSensor));
            var sensorId = vm.Layout.SelectedItem?.Id;
            Check("add-rotary-axis-stage", vm.TryAddLayoutComponent(LayoutComponentKind.RotaryStage));
            var rotaryStageId = vm.Layout.SelectedItem?.Id;
            Check("add-connected-cylinder", vm.TryAddLayoutComponent(LayoutComponentKind.PneumaticCylinder));
            var cylinderId = vm.Layout.SelectedItem?.Id;
            Check("add-connected-conveyor", vm.TryAddLayoutComponent(LayoutComponentKind.Conveyor));
            var conveyorId = vm.Layout.SelectedItem?.Id;
            Check("add-connected-workpiece", vm.TryAddLayoutComponent(LayoutComponentKind.Workpiece));
            var workpieceId = vm.Layout.SelectedItem?.Id;

            Check("six-components-added",
                vm.Layout.Items.Count(item => item.Component is not null) == componentCountBefore + 6);
            Check("workbench-row-count", vm.RecipeConnections.Rows.Count == componentCountBefore + 6);
            Check("all-workbench-rows-valid", vm.RecipeConnections.Rows.All(row => row.IsValid));
            Check("sequence-links-visible", vm.RecipeConnections.SequenceUseCount > 0);

            var linkedRow = vm.RecipeConnections.Rows.FirstOrDefault(row => row.HasSequenceUse);
            vm.SelectedDocumentTabIndex = 1;
            if (linkedRow is not null)
            {
                vm.RecipeConnections.OpenSequenceStepCommand.Execute(linkedRow);
            }
            Check("linked-step-opens-sequence-tab",
                linkedRow is not null && vm.SelectedDocumentTabIndex == 2);
            Check("linked-step-selected",
                linkedRow is not null
                && vm.SequenceEditor.SelectedStep?.Id == linkedRow.FirstSequenceStepId);

            var stageRow = vm.RecipeConnections.Rows.FirstOrDefault(row => row.ComponentId == stageId);
            vm.RecipeConnections.SelectedRow = stageRow;
            Check("row-selection-selects-layout", vm.Layout.SelectedItem?.Id == stageId);
            Check("row-selection-opens-binding-editor",
                vm.Layout.SelectedComponentEditor?.BehaviorBindingId is not null);
            Check("unused-connection-offers-target-step", stageRow?.CanAddSequenceStep == true);
            vm.SelectedDocumentTabIndex = 1;
            if (stageRow is not null)
            {
                vm.RecipeConnections.AddSequenceStepCommand.Execute(stageRow);
            }
            Check("target-step-added", vm.SequenceEditor.Steps.Count == stepCountBefore + 1);
            Check("added-target-step-selected",
                vm.SelectedDocumentTabIndex == 2
                && vm.SequenceEditor.SelectedStep?.TargetId == stageRow?.SequenceTargetId);
            Check("added-target-step-visible-in-connections",
                vm.RecipeConnections.Rows.FirstOrDefault(row => row.ComponentId == stageId)
                    is { HasSequenceUse: true });

            vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
            Check("explicit-readiness-passed", vm.RecipeConnections.ReadinessPassed == true);
            Check("authoring-does-not-run", vm.IsRunning == runningBefore);
            Check("authoring-keeps-design-mode", vm.IsDesignMode == designModeBefore);

            var fullSavePath = Path.GetFullPath(connectionWorkbenchSavePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
            await vm.SaveProjectAsync(fullSavePath);
            Check("saved-project-reopens", await vm.OpenProjectAsync(fullSavePath));

            var saved = await new ProjectDocumentStore().LoadAsync(fullSavePath);
            var savedLayout = saved.Layouts.First(layout =>
                string.Equals(layout.Id, saved.Simulation.ActiveLayoutId, StringComparison.Ordinal));
            var savedStage = savedLayout.Components.Single(component => component.Id == stageId);
            var savedRotaryStage = savedLayout.Components.Single(component => component.Id == rotaryStageId);
            var savedSensor = savedLayout.Components.Single(component => component.Id == sensorId);
            var savedCylinder = savedLayout.Components.Single(component => component.Id == cylinderId);
            var savedConveyor = savedLayout.Components.Single(component => component.Id == conveyorId);
            var savedWorkpiece = savedLayout.Components.Single(component => component.Id == workpieceId);
            var sensorDevice = saved.Devices.Single(device => device.Id == savedSensor.BehaviorBindingId);
            var cylinderDevice = saved.Devices.Single(device => device.Id == savedCylinder.BehaviorBindingId);
            var conveyorDevice = saved.Devices.Single(device => device.Id == savedConveyor.BehaviorBindingId);
            var workpieceDevice = saved.Devices.Single(device => device.Id == savedWorkpiece.BehaviorBindingId);

            Check("axis-binding-persisted", saved.Axes.Any(axis => axis.Id == savedStage.BehaviorBindingId));
            Check("rotary-axis-binding-persisted", saved.Axes.Any(axis =>
                axis.Id == savedRotaryStage.BehaviorBindingId
                && axis.Kind == AxisKind.Rotary
                && axis.Unit == "deg"));
            Check("rotary-stage-kind-persisted", savedRotaryStage.Kind == LayoutComponentKind.RotaryStage);
            Check("target-step-persisted", saved.Sequences.Any(sequence => sequence.Steps.Any(step =>
                step.TargetId == savedStage.BehaviorBindingId)));
            Check("sensor-target-persisted", sensorDevice.Sensor?.TargetComponentId == stageId);
            Check("sensor-di-persisted", saved.Channels.Any(channel =>
                channel.Id == sensorDevice.Sensor?.OutputChannelId && channel.Kind == ChannelKind.DigitalInput));
            Check("cylinder-io-persisted", cylinderDevice.Cylinder is { } cylinder
                && saved.Channels.Any(channel => channel.Id == cylinder.ExtendCommandChannelId && channel.Kind == ChannelKind.DigitalOutput)
                && saved.Channels.Any(channel => channel.Id == cylinder.ExtendedSensorChannelId && channel.Kind == ChannelKind.DigitalInput)
                && saved.Channels.Any(channel => channel.Id == cylinder.RetractedSensorChannelId && channel.Kind == ChannelKind.DigitalInput));
            Check("conveyor-io-persisted", conveyorDevice.Conveyor is { } conveyor
                && saved.Channels.Any(channel => channel.Id == conveyor.RunCommandChannelId && channel.Kind == ChannelKind.DigitalOutput)
                && saved.Channels.Any(channel => channel.Id == conveyor.ReverseCommandChannelId && channel.Kind == ChannelKind.DigitalOutput));
            Check("workpiece-carrier-persisted", workpieceDevice.Workpiece is { } workpiece
                && savedLayout.Components.Any(component =>
                    component.Id == workpiece.ConveyorComponentId
                    && component.Kind == LayoutComponentKind.Conveyor));
            Check("reopen-stays-stopped", !vm.IsRunning && vm.IsDesignMode);
            Check("added-stage-count-visible", vm.RecipeConnections.Rows.Count(row =>
                row.Kind == LayoutComponentKind.LinearStage) == axisCountBefore + 1);
            Check("added-rotary-stage-count-visible", vm.RecipeConnections.Rows.Count(row =>
                row.Kind == LayoutComponentKind.RotaryStage) == rotaryAxisCountBefore + 1);

            connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
            {
                Checks = checks,
                Failures = failures
            };
            connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
            Console.WriteLine(
                $"Connection workbench smoke {(connectionWorkbenchReport.IsValid ? "passed" : "failed")}.");
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
            var stationSkeletonState = connectionWorkbenchState.Equals("station-skeleton-focus", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-hover", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-pressed", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-preview", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-apply-focus", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-apply-pressed", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-invalid", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-input-focus", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-input-disabled", StringComparison.OrdinalIgnoreCase)
                || connectionWorkbenchState.Equals("station-skeleton-applied", StringComparison.OrdinalIgnoreCase);
            if (stationSkeletonState)
            {
                var project = initialProject
                    ?? throw new InvalidOperationException("A project is required for station-skeleton smoke.");
                project.Layouts.Clear();
                project.Axes.Clear();
                project.Devices.Clear();
                project.Channels.Clear();
                project.Sequences.Clear();
                project.Simulation.ActiveLayoutId = null;
                project.Simulation.AutomaticRun = null;
                vm.ProjectTree.LoadProject(project);
                vm.Layout.Load(project);
                vm.RecipeConnections.Load(project);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

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

            void ClearInitialRecipeCheckpoints()
            {
                var project = initialProject
                    ?? throw new InvalidOperationException("A project is required for checkpoint template smoke.");
                foreach (var step in project.Sequences.SelectMany(sequence => sequence.Steps))
                {
                    step.ExpectedTargetId = null;
                    step.ExpectedState = null;
                }
                vm.RecipeConnections.Load(project, vm.Layout.SelectedItem?.Id);
            }

            switch (connectionWorkbenchState.ToLowerInvariant())
            {
                case "semantic-setup-preview":
                case "semantic-setup-invalid":
                case "semantic-setup-applied":
                    var semanticProject = initialProject ?? throw new InvalidOperationException("A project is required for semantic setup smoke.");
                    var semanticStore = new ProjectDocumentStore();
                    var semanticBefore = semanticStore.Serialize(semanticProject);
                    var semanticRuntimeBefore = vm.SceneSnapshots.Latest;
                    var semanticKind = semanticProject.Devices.Any(device => device.Kind == DeviceKind.Prealigner) ? "prealigner"
                        : semanticProject.Devices.Any(device => device.Kind == DeviceKind.Handler) ? "wafer-handler"
                        : semanticProject.Devices.Any(device => device.Kind == DeviceKind.Inspection) ? "inspection-handoff"
                        : semanticProject.Devices.Any(device => device.Kind == DeviceKind.Sorter) ? "inspection-sort"
                        : "oht";
                    switch (semanticKind)
                    {
                        case "prealigner": vm.RecipeConnections.PreviewPrealignerSetupCommand.Execute(null); break;
                        case "wafer-handler": vm.RecipeConnections.PreviewWaferHandlerSetupCommand.Execute(null); break;
                        case "inspection-handoff": vm.RecipeConnections.PreviewInspectionHandoffSetupCommand.Execute(null); break;
                        case "inspection-sort": vm.RecipeConnections.PreviewInspectionSortRouterSetupCommand.Execute(null); break;
                        default: vm.RecipeConnections.PreviewOhtHandoffSetupCommand.Execute(null); break;
                    }
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke((semanticKind == "prealigner" ? vm.RecipeConnections.IsPrealignerSetupVisible : semanticKind == "wafer-handler" ? vm.RecipeConnections.IsWaferHandlerSetupVisible : semanticKind == "inspection-handoff" ? vm.RecipeConnections.IsInspectionHandoffSetupVisible : semanticKind == "inspection-sort" ? vm.RecipeConnections.IsInspectionSortRouterSetupVisible : vm.RecipeConnections.IsOhtHandoffSetupVisible)
                        && semanticBefore == semanticStore.Serialize(semanticProject)
                        && semanticRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && !vm.IsRunning && vm.IsDesignMode,
                        "Semantic setup preview changed project or runtime state.");
                    if (connectionWorkbenchState.Equals("semantic-setup-invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        switch (semanticKind)
                        {
                            case "prealigner": vm.RecipeConnections.PrealignerRotaryStageComponentId = vm.RecipeConnections.PrealignerClampCylinderComponentId; break;
                            case "wafer-handler": vm.RecipeConnections.WaferHandlerHorizontalAxisId = vm.RecipeConnections.WaferHandlerVerticalAxisId; break;
                            case "inspection-handoff": vm.RecipeConnections.InspectionHandoffCameraId = null; break;
                            case "inspection-sort": vm.RecipeConnections.InspectionSortNgConveyorId = vm.RecipeConnections.InspectionSortPassConveyorId; break;
                            default: vm.RecipeConnections.OhtVehicleDockedChannelId = vm.RecipeConnections.OhtRouteAvailableChannelId; break;
                        }
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(semanticKind == "prealigner" ? vm.RecipeConnections.HasPrealignerSetupValidationError : semanticKind == "wafer-handler" ? vm.RecipeConnections.HasWaferHandlerSetupValidationError : semanticKind == "inspection-handoff" ? vm.RecipeConnections.HasInspectionHandoffSetupValidationError : semanticKind == "inspection-sort" ? vm.RecipeConnections.HasInspectionSortRouterSetupValidationError : vm.RecipeConnections.HasOhtHandoffSetupValidationError,
                            "Invalid semantic setup did not block Apply.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("semantic-setup-applied", StringComparison.OrdinalIgnoreCase))
                    {
                        var applyName = semanticKind switch { "prealigner" => "ApplyPrealignerSetupButton", "wafer-handler" => "ApplyWaferHandlerSetupButton", "inspection-handoff" => "ApplyInspectionHandoffSetupButton", "inspection-sort" => "ApplyInspectionSortSetupButton", _ => "ApplyOhtSetupButton" };
                        var apply = FindVisualDescendant<Button>(workbench, candidate => string.Equals(candidate.Name, applyName, StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Semantic setup Apply button was not available.");
                        AssertSmoke(apply.IsEnabled, "Valid semantic setup did not enable Apply.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        switch (semanticKind)
                        {
                            case "prealigner": vm.RecipeConnections.ApplyPrealignerSetupCommand.Execute(null); break;
                            case "wafer-handler": vm.RecipeConnections.ApplyWaferHandlerSetupCommand.Execute(null); break;
                            case "inspection-handoff": vm.RecipeConnections.ApplyInspectionHandoffSetupCommand.Execute(null); break;
                            case "inspection-sort": vm.RecipeConnections.ApplyInspectionSortRouterSetupCommand.Execute(null); break;
                            default: vm.RecipeConnections.ApplyOhtHandoffSetupCommand.Execute(null); break;
                        }
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(!vm.RecipeConnections.IsPrealignerSetupVisible && !vm.RecipeConnections.IsWaferHandlerSetupVisible && !vm.RecipeConnections.IsInspectionHandoffSetupVisible && !vm.RecipeConnections.IsInspectionSortRouterSetupVisible && !vm.RecipeConnections.IsOhtHandoffSetupVisible && !vm.IsRunning && vm.IsDesignMode && semanticRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex,
                            "Applying semantic setup did not preserve stopped design mode.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(vm.RecipeConnections.ReadinessPassed == true, "Applied semantic setup did not pass readiness.");
                        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                        for (var attempt = 0; attempt < 300 && vm.RecipeConnections.IsRecipeDryRunRunning; attempt++) await Task.Delay(20);
                        AssertSmoke(!vm.RecipeConnections.IsRecipeDryRunRunning && vm.RecipeConnections.HasRecipeDryRunResult,
                            "Applied semantic setup did not complete the existing recipe dry-run.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            await vm.SaveProjectAsync(connectionWorkbenchSavePath);
                            AssertSmoke(await vm.OpenProjectAsync(connectionWorkbenchSavePath), "Semantic setup project did not reopen.");
                            switch (semanticKind)
                            {
                                case "prealigner": vm.RecipeConnections.PreviewPrealignerSetupCommand.Execute(null); break;
                                case "wafer-handler": vm.RecipeConnections.PreviewWaferHandlerSetupCommand.Execute(null); break;
                                case "inspection-handoff": vm.RecipeConnections.PreviewInspectionHandoffSetupCommand.Execute(null); break;
                                case "inspection-sort": vm.RecipeConnections.PreviewInspectionSortRouterSetupCommand.Execute(null); break;
                                default: vm.RecipeConnections.PreviewOhtHandoffSetupCommand.Execute(null); break;
                            }
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke((semanticKind == "prealigner" ? vm.RecipeConnections.IsPrealignerSetupVisible : semanticKind == "wafer-handler" ? vm.RecipeConnections.IsWaferHandlerSetupVisible : semanticKind == "inspection-handoff" ? vm.RecipeConnections.IsInspectionHandoffSetupVisible : semanticKind == "inspection-sort" ? vm.RecipeConnections.IsInspectionSortRouterSetupVisible : vm.RecipeConnections.IsOhtHandoffSetupVisible) && !vm.IsRunning && vm.IsDesignMode,
                                "Saved semantic setup was not restored safely after reopen.");
                        }
                        break;
                    }
                    break;
                case "normal":
                    AssertSmoke(addStageButton.IsEnabled, "Axis + stage button was unexpectedly disabled.");
                    AssertSmoke(addRotaryStageButton.IsEnabled, "Rotary axis + stage button was unexpectedly disabled.");
                    AssertSmoke(readinessButton.IsEnabled, "Simulation readiness button was unexpectedly disabled.");
                    AssertSmoke(stationSkeletonButton.IsEnabled, "Semiconductor station button was unexpectedly disabled.");
                    AssertSmoke(processBlockButton.IsEnabled, "Process block composer button was unexpectedly disabled.");
                    AssertSmoke(loadLockSetupButton.IsEnabled, "Load-lock setup button was unexpectedly disabled.");
                    AssertSmoke(checkpointTemplateButton.IsEnabled, "Checkpoint template button was unexpectedly disabled.");
                    AssertSmoke(!dryRunButton.IsEnabled, "Recipe dry run was enabled before readiness passed.");
                    AssertSmoke(
                        FindVisualDescendant<Button>(workbench, candidate =>
                            string.Equals(candidate.Name, "OpenConnectionSequenceStepButton", StringComparison.Ordinal)
                            && candidate.IsVisible) is not null,
                        "No visible linked Sequence step action was available.");
                    break;
                case "focus":
                    window.Activate();
                    addRotaryStageButton.Focus();
                    Keyboard.Focus(addRotaryStageButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(addRotaryStageButton.IsKeyboardFocused, "Rotary axis + stage button did not receive focus.");
                    break;
                case "hover":
                case "pressed":
                    window.Topmost = true;
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    addRotaryStageButton.BringIntoView();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    addRotaryStageButton.UpdateLayout();
                    addRotaryStageButton.Focus();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var rotaryButtonCenter = addRotaryStageButton.PointToScreen(new Point(
                        addRotaryStageButton.ActualWidth / 2,
                        addRotaryStageButton.ActualHeight / 2));
                    SetCursorPos(
                        (int)Math.Round(rotaryButtonCenter.X - addRotaryStageButton.ActualWidth),
                        (int)Math.Round(rotaryButtonCenter.Y));
                    Mouse.Synchronize();
                    await Task.Delay(50);
                    MovePointerToCenter(addRotaryStageButton);
                    mouse_event(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                    await Task.Delay(200);
                    GetCursorPos(out NativePoint cursorPosition);
                    var cursorInButton = addRotaryStageButton.PointFromScreen(
                        new Point(cursorPosition.X, cursorPosition.Y));
                    AssertSmoke(
                        addRotaryStageButton.IsMouseOver,
                        $"Rotary axis + stage button did not enter hover state. " +
                        $"Cursor=({cursorPosition.X},{cursorPosition.Y}), " +
                        $"button=({cursorInButton.X:F1},{cursorInButton.Y:F1})/" +
                        $"{addRotaryStageButton.ActualWidth:F1}x{addRotaryStageButton.ActualHeight:F1}, " +
                        $"direct={Mouse.DirectlyOver?.GetType().Name ?? "null"}.");
                    if (connectionWorkbenchState.Equals("pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(addRotaryStageButton.IsPressed, "Rotary axis + stage button did not enter pointer-down state.");
                    }
                    break;
                case "disabled":
                    AssertSmoke(!addStageButton.IsEnabled, "Axis + stage button remained enabled in Run mode.");
                    AssertSmoke(!addRotaryStageButton.IsEnabled, "Rotary axis + stage button remained enabled in Run mode.");
                    AssertSmoke(!readinessButton.IsEnabled, "Simulation readiness button remained enabled in Run mode.");
                    AssertSmoke(!stationSkeletonButton.IsEnabled, "Semiconductor station button remained enabled in Run mode.");
                    AssertSmoke(!processBlockButton.IsEnabled, "Process block composer button remained enabled in Run mode.");
                    AssertSmoke(!dryRunButton.IsEnabled, "Recipe dry run remained enabled in Run mode.");
                    AssertSmoke(!checkpointTemplateButton.IsEnabled, "Checkpoint template button remained enabled in Run mode.");
                    break;
                case "readiness":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.RecipeConnections.ReadinessPassed == true && !vm.IsRunning,
                        "Simulation readiness did not pass safely without starting simulation.");
                    break;
                case "process-block-focus":
                    window.Activate();
                    processBlockButton.Focus();
                    Keyboard.Focus(processBlockButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(processBlockButton.IsKeyboardFocused, "Process block composer button did not receive focus.");
                    break;
                case "process-block-hover":
                case "process-block-pressed":
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    processBlockButton.Focus();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    MovePointerToCenter(processBlockButton);
                    await Task.Delay(100);
                    AssertSmoke(processBlockButton.IsMouseOver, "Process block composer button did not enter hover state.");
                    if (connectionWorkbenchState.Equals("process-block-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(processBlockButton.IsPressed, "Process block composer button did not enter pointer-down state.");
                    }
                    break;
                case "process-block-preview":
                case "process-block-apply-focus":
                case "process-block-apply-pressed":
                case "process-block-check-focus":
                case "process-block-check-pressed":
                case "process-block-disabled":
                case "process-block-empty":
                case "process-block-applied":
                case "process-block-edit-current":
                case "process-block-edit-remove":
                case "process-block-edit-empty":
                case "process-block-edited":
                case "process-block-step-current":
                case "process-block-step-conflict":
                case "process-block-step-filter":
                case "process-block-step-focus":
                case "process-block-step-pressed":
                case "process-block-step-disabled":
                case "process-block-step-proposed":
                case "process-block-step-removal":
                case "process-block-step-open":
                case "process-block-step-return":
                case "process-block-step-return-sequence":
                case "process-block-step-return-focus":
                case "process-block-step-return-pressed":
                case "process-block-step-return-disabled":
                case "process-block-step-return-closed":
                case "process-block-step-return-reopen":
                case "process-block-step-review":
                case "process-block-timeout-batch":
                    var processProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for process-block smoke.");
                    var processStore = new ProjectDocumentStore();
                    var processBefore = processStore.SerializeForEvidence(processProject);
                    var processRuntimeBefore = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.PreviewProcessBlockCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var processPanel = FindVisualDescendant<Border>(workbench, candidate => string.Equals(
                        candidate.Name,
                        "SemiconductorProcessBlockPreview",
                        StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Process block preview was not available.");
                    var processApplyButton = FindVisualDescendant<Button>(workbench, candidate => string.Equals(
                        candidate.Name,
                        "ApplySemiconductorProcessBlockButton",
                        StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Process block Apply button was not available.");
                    var loadBlockCheckBox = FindVisualDescendant<CheckBox>(workbench, candidate => string.Equals(
                        candidate.Name,
                        "ProcessBlockLoadCheckBox",
                        StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Load block checkbox was not available.");
                    AssertSmoke(
                        processPanel.IsVisible
                        && vm.RecipeConnections.SelectedProcessBlockCount == 5
                        && vm.RecipeConnections.ProcessBlockItems.Count == 13
                        && vm.RecipeConnections.ProcessBlockItems.All(item => item.IsProposed)
                        && loadBlockCheckBox.IsChecked == true
                        && processApplyButton.IsEnabled,
                        "The five-block plan did not preview its thirteen proposed steps.");
                    AssertSmoke(
                        processBefore == processStore.SerializeForEvidence(processProject)
                        && processRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && processRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && vm.IsDesignMode
                        && !vm.IsRunning,
                        "Process block preview changed the project or runtime.");
                    if (connectionWorkbenchState.Equals("process-block-step-proposed", StringComparison.OrdinalIgnoreCase))
                    {
                        var proposedItem = vm.RecipeConnections.ProcessBlockItems[0];
                        var proposedStepButton = FindVisualDescendant<Button>(processPanel, candidate => string.Equals(
                            candidate.Name,
                            "OpenProcessBlockSequenceStepButton",
                            StringComparison.Ordinal));
                        AssertSmoke(
                            vm.RecipeConnections.ProcessBlockItems.All(item => !item.CanOpenSequenceStep)
                            && vm.RecipeConnections.ProcessBlockItems.All(item => !item.DetailText.Contains(
                                $"{OpenVisionLanguageService.T("Sequence.Value")}: ",
                                StringComparison.Ordinal))
                            && !vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(proposedItem)
                            && (proposedStepButton is null || !proposedStepButton.IsVisible),
                            "A proposed process step exposed current settings or navigation before it had an owning Sequence step.");
                        break;
                    }
                    if (connectionWorkbenchState.StartsWith("process-block-edit-", StringComparison.OrdinalIgnoreCase)
                        || connectionWorkbenchState.Equals("process-block-edited", StringComparison.OrdinalIgnoreCase)
                        || connectionWorkbenchState.Equals("process-block-timeout-batch", StringComparison.OrdinalIgnoreCase)
                        || connectionWorkbenchState.StartsWith("process-block-step-", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.ApplyProcessBlockCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.RecipeStepCount == 25
                            && vm.IsDesignMode
                            && !vm.IsRunning,
                            "The editable plan setup did not create the stopped 25-step recipe.");
                        var appliedBeforeEdit = processStore.SerializeForEvidence(processProject);
                        vm.RecipeConnections.PreviewProcessBlockCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.SelectedProcessBlockCount == 5
                            && vm.RecipeConnections.ExistingProcessBlockCount == 5
                            && vm.RecipeConnections.ProcessBlockItems.Count == 13
                            && vm.RecipeConnections.ProcessBlockItems.All(item => item.IsAlreadyConfigured)
                            && !processApplyButton.IsEnabled
                            && appliedBeforeEdit == processStore.SerializeForEvidence(processProject),
                            "The applied five-block plan was not recognized without mutation.");
                        var currentProcessSequence = processProject.Sequences.FirstOrDefault(sequence => string.Equals(
                            sequence.Id,
                            processProject.Simulation.AutomaticRun?.SequenceId,
                            StringComparison.Ordinal));
                        AssertSmoke(
                            currentProcessSequence is not null
                            && vm.RecipeConnections.ProcessBlockItems.All(item =>
                            {
                                var step = currentProcessSequence!.Steps.FirstOrDefault(candidate => string.Equals(
                                    candidate.Id,
                                    item.StepId,
                                    StringComparison.Ordinal));
                                if (step is null)
                                {
                                    return false;
                                }
                                var valueText = string.IsNullOrWhiteSpace(step.Parameter) ? "—" : step.Parameter;
                                return item.DetailText.Contains(
                                        $"{OpenVisionLanguageService.T("Sequence.Target")}: {step.TargetId}",
                                        StringComparison.Ordinal)
                                    && item.DetailText.Contains(
                                        $"{OpenVisionLanguageService.T("Sequence.Value")}: {valueText}",
                                        StringComparison.Ordinal)
                                    && item.DetailText.Contains(
                                        $"{OpenVisionLanguageService.T("Sequence.Timeout")}: "
                                        + $"{step.TimeoutMs.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} ms",
                                        StringComparison.Ordinal);
                            })
                            && appliedBeforeEdit == processStore.SerializeForEvidence(processProject),
                            "Existing process cards did not show their exact current target, value, and timeout safely.");
                        if (connectionWorkbenchState.StartsWith("process-block-step-", StringComparison.OrdinalIgnoreCase)
                            || connectionWorkbenchState.Equals("process-block-timeout-batch", StringComparison.OrdinalIgnoreCase))
                        {
                            var existingItem = vm.RecipeConnections.ProcessBlockItems.Single(item => string.Equals(
                                item.StepId,
                                "process-block.inspect.confirm-position",
                                StringComparison.Ordinal));
                            var openStepButton = FindVisualDescendant<Button>(processPanel, candidate => string.Equals(
                                candidate.Name,
                                "OpenProcessBlockSequenceStepButton",
                                StringComparison.Ordinal))
                                ?? throw new InvalidOperationException("Existing process-step navigation button was not available.");
                            AssertSmoke(
                                existingItem.CanOpenSequenceStep
                                && vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(existingItem)
                                && openStepButton.IsVisible,
                                "An existing process step did not expose enabled Sequence navigation.");

                            if (connectionWorkbenchState.Equals("process-block-timeout-batch", StringComparison.OrdinalIgnoreCase))
                            {
                                var timeoutTextBox = FindVisualDescendant<TextBox>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockTimeoutTextBox",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Managed timeout input was not available.");
                                var previewTimeoutButton = FindVisualDescendant<Button>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "PreviewProcessBlockTimeoutButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Managed timeout preview button was not available.");
                                var applyTimeoutButton = FindVisualDescendant<Button>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ApplyProcessBlockTimeoutButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Managed timeout Apply button was not available.");
                                var cancelTimeoutButton = FindVisualDescendant<Button>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "CancelProcessBlockTimeoutButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Managed timeout Cancel button was not available.");
                                vm.RecipeConnections.ProcessBlockTimeoutText = "-1";
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    !vm.RecipeConnections.IsProcessBlockTimeoutValid
                                    && !previewTimeoutButton.IsEnabled
                                    && string.Equals(timeoutTextBox.Text, "-1", StringComparison.Ordinal),
                                    "Invalid managed timeout input was not rendered and blocked.");

                                vm.RecipeConnections.ProcessBlockTimeoutText = "6000";
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                var timeoutSourceBeforePreview = processStore.SerializeForEvidence(processProject);
                                AssertSmoke(
                                    vm.RecipeConnections.CompatibleProcessBlockTimeoutCount == 6
                                    && previewTimeoutButton.IsEnabled,
                                    "The All filter did not expose its six compatible managed wait steps.");
                                vm.RecipeConnections.PreviewProcessBlockTimeoutsCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    vm.RecipeConnections.IsProcessBlockTimeoutPreviewVisible
                                    && vm.RecipeConnections.ProcessBlockTimeoutItems.Count == 6
                                    && vm.RecipeConnections.ProcessBlockTimeoutItems.All(item =>
                                        item.DetailText.Contains("6,000", StringComparison.Ordinal)
                                        || item.DetailText.Contains("6000", StringComparison.Ordinal))
                                    && applyTimeoutButton.IsVisible
                                    && applyTimeoutButton.IsEnabled
                                    && cancelTimeoutButton.IsVisible
                                    && timeoutSourceBeforePreview == processStore.SerializeForEvidence(processProject),
                                    "Managed timeout preview did not show six per-step changes without mutation.");

                                vm.RecipeConnections.ApplyProcessBlockTimeoutsCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                currentProcessSequence = processProject.Sequences.Single(sequence => string.Equals(
                                    sequence.Id,
                                    processProject.Simulation.AutomaticRun?.SequenceId,
                                    StringComparison.Ordinal));
                                var adjustedWaits = currentProcessSequence.Steps.Where(step =>
                                    step.Id.StartsWith("process-block.", StringComparison.Ordinal)
                                    && SemiconductorProcessBlockComposer.CanAdjustTimeout(step.Action)).ToArray();
                                AssertSmoke(
                                    adjustedWaits.Length == 6
                                    && adjustedWaits.All(step => step.TimeoutMs == 6000)
                                    && vm.RecipeConnections.IsProcessBlockPreviewVisible
                                    && vm.RecipeConnections.IsProcessBlockFilterAll
                                    && vm.IsDesignMode
                                    && !vm.IsRunning,
                                    "Managed timeout Apply did not atomically update six waits and preserve the open plan.");
                                var timeoutSourceAfterApply = processStore.SerializeForEvidence(processProject);
                                AssertSmoke(
                                    timeoutSourceAfterApply != timeoutSourceBeforePreview
                                    && processRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                                    && processRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime,
                                    "Managed timeout Apply changed the runtime or failed to change the authored project.");

                                vm.RecipeConnections.ProcessBlockTimeoutText = "6500";
                                vm.RecipeConnections.PreviewProcessBlockTimeoutsCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    vm.RecipeConnections.ProcessBlockTimeoutItems.Count == 6
                                    && timeoutSourceAfterApply == processStore.SerializeForEvidence(processProject),
                                    "The post-apply timeout preview changed the project or lost its six-step scope.");
                                var timeoutItems = FindVisualDescendant<ItemsControl>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockTimeoutItemsControl",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Managed timeout preview items were not available.");
                                if (vm.IsCompactLayout)
                                {
                                    timeoutItems.BringIntoView();
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                }
                                window.Activate();
                                timeoutTextBox.Focus();
                                Keyboard.Focus(timeoutTextBox);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    timeoutTextBox.IsKeyboardFocused && string.Equals(timeoutTextBox.Text, "6500", StringComparison.Ordinal),
                                    "Managed timeout input did not expose focused non-empty text.");
                                MovePointerToCenter(applyTimeoutButton);
                                Mouse.Capture(applyTimeoutButton, CaptureMode.SubTree);
                                Mouse.Synchronize();
                                await Task.Delay(200);
                                AssertSmoke(applyTimeoutButton.IsMouseOver, "Managed timeout Apply did not enter hover state.");
                                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                                _smokePointerHeld = true;
                                applyTimeoutButton.RaiseEvent(new MouseButtonEventArgs(
                                    Mouse.PrimaryDevice,
                                    Environment.TickCount,
                                    MouseButton.Left)
                                {
                                    RoutedEvent = Mouse.MouseDownEvent
                                });
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(applyTimeoutButton.IsPressed, "Managed timeout Apply did not enter pointer-down state.");
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                                {
                                    AssertSmoke(
                                        !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                        "Managed timeout workflow unexpectedly saved a project file.");
                                }
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                                {
                                    connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                                    {
                                        Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                        {
                                            ["invalid-timeout-blocked"] = true,
                                            ["six-compatible-filtered-waits"] = true,
                                            ["preview-listed-six-step-changes"] = true,
                                            ["preview-left-project-unchanged"] = true,
                                            ["atomic-apply-updated-six-waits"] = true,
                                            ["apply-preserved-open-filtered-plan"] = true,
                                            ["apply-left-runtime-stopped"] = true,
                                            ["second-preview-left-project-unchanged"] = true,
                                            ["non-empty-input-visible"] = true,
                                            ["keyboard-focus-visible"] = true,
                                            ["hover-and-pointer-down-visible"] = true,
                                            ["workflow-did-not-save-project"] = true
                                        },
                                        Failures = []
                                    };
                                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                                }
                                break;
                            }

                            if (connectionWorkbenchState.Equals("process-block-step-conflict", StringComparison.OrdinalIgnoreCase))
                            {
                                var conflictingStep = currentProcessSequence!.Steps.Single(step => string.Equals(
                                    step.Id,
                                    existingItem.StepId,
                                    StringComparison.Ordinal));
                                conflictingStep.Action = SequenceStepAction.SetSignal;
                                vm.RecipeConnections.PreviewProcessBlockCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                var conflictItem = vm.RecipeConnections.ProcessBlockItems.Single(item => string.Equals(
                                    item.StepId,
                                    existingItem.StepId,
                                    StringComparison.Ordinal));
                                vm.RecipeConnections.SelectedProcessBlockItem = conflictItem;
                                var conflictItemsList = FindVisualDescendant<ListBox>(workbench, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockItemsListBox",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Process block step list was not available for conflict evidence.");
                                conflictItemsList.ScrollIntoView(conflictItem);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    conflictItem.IsUnavailable
                                    && !conflictItem.IsCustomized
                                    && !conflictItem.CanOpenSequenceStep
                                    && vm.RecipeConnections.HasProcessBlockPlanError
                                    && !processApplyButton.IsEnabled
                                    && conflictItem.DetailText.Contains(
                                        $"{OpenVisionLanguageService.T("Sequence.Action")}: {SequenceStepAction.SetSignal}",
                                        StringComparison.Ordinal)
                                    && conflictItem.DetailText.Contains(
                                        string.Format(
                                            System.Globalization.CultureInfo.CurrentCulture,
                                            OpenVisionLanguageService.T("Connections.ProcessBlockTemplateValueFormat"),
                                            SequenceStepAction.WaitSignal),
                                        StringComparison.Ordinal),
                                    "An Action-conflicting managed step was not blocked and explained as unavailable.");
                                break;
                            }
                            if (connectionWorkbenchState.Equals("process-block-step-filter", StringComparison.OrdinalIgnoreCase))
                            {
                                var customizedStep = currentProcessSequence!.Steps.Single(step => string.Equals(
                                    step.Id,
                                    existingItem.StepId,
                                    StringComparison.Ordinal));
                                customizedStep.TimeoutMs += 100;
                                var conflictingStep = currentProcessSequence.Steps.Single(step => string.Equals(
                                    step.Id,
                                    "process-block.load.wait-entry",
                                    StringComparison.Ordinal));
                                conflictingStep.Action = SequenceStepAction.SetSignal;
                                vm.RecipeConnections.IsProcessBlockSelected = false;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                                var allFilterButton = FindVisualDescendant<RadioButton>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockFilterAllRadioButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("All process-step filter was not available.");
                                var customizedFilterButton = FindVisualDescendant<RadioButton>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockFilterCustomizedRadioButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Customized process-step filter was not available.");
                                var removalFilterButton = FindVisualDescendant<RadioButton>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockFilterRemovalRadioButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Removal process-step filter was not available.");
                                var conflictFilterButton = FindVisualDescendant<RadioButton>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockFilterConflictRadioButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Conflict process-step filter was not available.");
                                var emptyFilterText = FindVisualDescendant<TextBlock>(processPanel, candidate => string.Equals(
                                    candidate.Text,
                                    OpenVisionLanguageService.T("Connections.ProcessBlockFilterEmpty"),
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Empty process-step filter status was not available.");
                                var filterSource = processStore.SerializeForEvidence(processProject);
                                AssertSmoke(
                                    allFilterButton.IsChecked == true
                                    && vm.RecipeConnections.VisibleProcessBlockItems.Count == 13
                                    && emptyFilterText.Visibility == Visibility.Collapsed
                                    && vm.RecipeConnections.ProcessBlockFilterAllText.Contains("13", StringComparison.Ordinal)
                                    && vm.RecipeConnections.ProcessBlockFilterCustomizedText.Contains("1", StringComparison.Ordinal)
                                    && vm.RecipeConnections.ProcessBlockFilterRemovalText.Contains("4", StringComparison.Ordinal)
                                    && vm.RecipeConnections.ProcessBlockFilterConflictText.Contains("1", StringComparison.Ordinal),
                                    "The default process-step filter did not show the full plan and current status counts.");

                                vm.RecipeConnections.IsProcessBlockFilterCustomized = true;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    customizedFilterButton.IsChecked == true
                                    && vm.RecipeConnections.VisibleProcessBlockItems.Count == 1
                                    && vm.RecipeConnections.VisibleProcessBlockItems.All(item => item.IsCustomized),
                                    "The customized process-step filter showed another status.");

                                vm.RecipeConnections.IsProcessBlockFilterRemoval = true;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    removalFilterButton.IsChecked == true
                                    && vm.RecipeConnections.VisibleProcessBlockItems.Count == 4
                                    && vm.RecipeConnections.VisibleProcessBlockItems.All(item => item.IsProposedRemoval),
                                    "The removal process-step filter showed another status.");
                                vm.RecipeConnections.IsProcessBlockSelected = true;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    vm.RecipeConnections.VisibleProcessBlockItems.Count == 0
                                    && !vm.RecipeConnections.HasVisibleProcessBlockItems
                                    && emptyFilterText.Visibility == Visibility.Visible,
                                    "An empty process-step filter did not explain that no steps match.");
                                vm.RecipeConnections.IsProcessBlockSelected = false;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                                vm.RecipeConnections.IsProcessBlockFilterConflict = true;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    conflictFilterButton.IsChecked == true
                                    && vm.RecipeConnections.VisibleProcessBlockItems.Count == 1
                                    && vm.RecipeConnections.VisibleProcessBlockItems.All(item => item.IsUnavailable),
                                    "The conflict process-step filter showed another status.");

                                vm.RecipeConnections.IsProcessBlockFilterAll = true;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    allFilterButton.IsChecked == true
                                    && vm.RecipeConnections.VisibleProcessBlockItems.Count == vm.RecipeConnections.ProcessBlockItems.Count,
                                    "Returning to the All process-step filter did not restore the full plan.");

                                vm.RecipeConnections.IsProcessBlockFilterCustomized = true;
                                var filteredItem = vm.RecipeConnections.VisibleProcessBlockItems.Single();
                                vm.RecipeConnections.SelectedProcessBlockItem = filteredItem;
                                var filterItemsList = FindVisualDescendant<ListBox>(processPanel, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockItemsListBox",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Filtered process-step list was not available.");
                                filterItemsList.ScrollIntoView(filteredItem);
                                window.Activate();
                                customizedFilterButton.BringIntoView();
                                customizedFilterButton.Focus();
                                Keyboard.Focus(customizedFilterButton);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    customizedFilterButton.IsKeyboardFocused,
                                    "The process-step filter did not expose keyboard focus.");
                                MovePointerToCenter(customizedFilterButton);
                                Mouse.Capture(customizedFilterButton, CaptureMode.SubTree);
                                Mouse.Synchronize();
                                await Task.Delay(200);
                                AssertSmoke(
                                    customizedFilterButton.IsMouseOver,
                                    "The process-step filter did not enter hover state.");
                                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                                _smokePointerHeld = true;
                                customizedFilterButton.RaiseEvent(new MouseButtonEventArgs(
                                    Mouse.PrimaryDevice,
                                    Environment.TickCount,
                                    MouseButton.Left)
                                {
                                    RoutedEvent = Mouse.MouseDownEvent
                                });
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    customizedFilterButton.IsPressed,
                                    "The process-step filter did not enter pointer-down state.");
                                AssertSmoke(
                                    filterSource == processStore.SerializeForEvidence(processProject)
                                    && processRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                                    && processRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                                    && vm.IsDesignMode
                                    && !vm.IsRunning,
                                    "Filtering process steps changed the project or runtime.");
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                                {
                                    AssertSmoke(
                                        !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                        "Filtering process steps unexpectedly saved a project file.");
                                }
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                                {
                                    connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                                    {
                                        Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                        {
                                            ["all-filter-shows-full-plan"] = true,
                                            ["filter-labels-show-status-counts"] = true,
                                            ["customized-filter-is-exact"] = true,
                                            ["removal-filter-is-exact"] = true,
                                            ["empty-filter-state-is-explained"] = true,
                                            ["conflict-filter-is-exact"] = true,
                                            ["all-filter-restores-full-plan"] = true,
                                            ["filtered-card-remains-selectable"] = true,
                                            ["filter-keyboard-focus-visible"] = true,
                                            ["filter-pointer-down-visible"] = true,
                                            ["filter-project-runtime-and-save-unchanged"] = true
                                        },
                                        Failures = []
                                    };
                                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                                }
                                break;
                            }
                            if (connectionWorkbenchState.Equals("process-block-step-current", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                            if (connectionWorkbenchState.Equals("process-block-step-disabled", StringComparison.OrdinalIgnoreCase))
                            {
                                vm.RecipeConnections.IsEditable = false;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    !openStepButton.IsEnabled
                                    && !vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(existingItem),
                                    "Existing process-step navigation did not enter its disabled state.");
                                break;
                            }
                            if (connectionWorkbenchState.Equals("process-block-step-focus", StringComparison.OrdinalIgnoreCase)
                                || connectionWorkbenchState.Equals("process-block-step-pressed", StringComparison.OrdinalIgnoreCase))
                            {
                                window.Activate();
                                openStepButton.BringIntoView();
                                openStepButton.UpdateLayout();
                                openStepButton.Focus();
                                Keyboard.Focus(openStepButton);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(openStepButton.IsKeyboardFocused, "Process-step navigation button did not receive focus.");
                                if (connectionWorkbenchState.Equals("process-block-step-pressed", StringComparison.OrdinalIgnoreCase))
                                {
                                    MovePointerToCenter(openStepButton);
                                    Mouse.Capture(openStepButton, CaptureMode.SubTree);
                                    Mouse.Synchronize();
                                    await Task.Delay(200);
                                    AssertSmoke(openStepButton.IsMouseOver, "Process-step navigation button did not enter hover state.");
                                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                                    _smokePointerHeld = true;
                                    openStepButton.RaiseEvent(new MouseButtonEventArgs(
                                        Mouse.PrimaryDevice,
                                        Environment.TickCount,
                                        MouseButton.Left)
                                    {
                                        RoutedEvent = Mouse.MouseDownEvent
                                    });
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                    AssertSmoke(openStepButton.IsPressed, "Process-step navigation button did not enter pointer-down state.");
                                }
                                break;
                            }
                            if (connectionWorkbenchState.Equals("process-block-step-removal", StringComparison.OrdinalIgnoreCase))
                            {
                                vm.RecipeConnections.IsInspectBlockSelected = false;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                var removalItem = vm.RecipeConnections.ProcessBlockItems.Single(item => string.Equals(
                                    item.StepId,
                                    existingItem.StepId,
                                    StringComparison.Ordinal));
                                var removalStep = currentProcessSequence!.Steps.Single(step => string.Equals(
                                    step.Id,
                                    removalItem.StepId,
                                    StringComparison.Ordinal));
                                var removalValue = string.IsNullOrWhiteSpace(removalStep.Parameter)
                                    ? "—"
                                    : removalStep.Parameter;
                                AssertSmoke(
                                    removalItem.IsProposedRemoval
                                    && !removalItem.CanOpenSequenceStep
                                    && removalItem.DetailText.Contains(
                                        $"{OpenVisionLanguageService.T("Sequence.Value")}: {removalValue}",
                                        StringComparison.Ordinal)
                                    && !vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(removalItem)
                                    && appliedBeforeEdit == processStore.SerializeForEvidence(processProject),
                                    "A proposed-removal process step lost its current settings, exposed navigation, or changed the project.");
                                break;
                            }

                            if (connectionWorkbenchState.Equals("process-block-step-review", StringComparison.OrdinalIgnoreCase))
                            {
                                var reviewStepIds = new[]
                                {
                                    "process-block.load.wait-entry",
                                    "process-block.inspect.confirm-position",
                                    "process-block.unload.wait-clear"
                                };
                                foreach (var reviewStepId in reviewStepIds)
                                {
                                    currentProcessSequence!.Steps.Single(step => string.Equals(
                                        step.Id,
                                        reviewStepId,
                                        StringComparison.Ordinal)).TimeoutMs += 100;
                                }
                                vm.RecipeConnections.PreviewProcessBlockCommand.Execute(null);
                                vm.RecipeConnections.IsProcessBlockFilterCustomized = true;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    vm.RecipeConnections.VisibleProcessBlockItems.Select(item => item.StepId)
                                        .SequenceEqual(reviewStepIds),
                                    "The filtered three-step review list was not created in process order.");

                                var middleReviewItem = vm.RecipeConnections.VisibleProcessBlockItems[1];
                                vm.RecipeConnections.SelectedProcessBlockItem = middleReviewItem;
                                var reviewRuntimeBefore = vm.SceneSnapshots.Latest;
                                vm.RecipeConnections.OpenSequenceStepCommand.Execute(middleReviewItem);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                                var previousReviewButton = FindVisualDescendant<Button>(window, candidate => string.Equals(
                                    candidate.Name,
                                    "PreviousProcessPlanReviewStepButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Previous filtered-step review button was not available.");
                                var nextReviewButton = FindVisualDescendant<Button>(window, candidate => string.Equals(
                                    candidate.Name,
                                    "NextProcessPlanReviewStepButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Next filtered-step review button was not available.");
                                AssertSmoke(
                                    vm.SelectedDocumentTabIndex == 2
                                    && string.Equals(vm.SequenceEditor.SelectedStep?.Id, reviewStepIds[1], StringComparison.Ordinal)
                                    && string.Equals(vm.ProcessPlanReturnStepId, reviewStepIds[1], StringComparison.Ordinal)
                                    && vm.ProcessPlanReviewPositionText.Contains("2/3", StringComparison.Ordinal)
                                    && previousReviewButton.IsEnabled
                                    && nextReviewButton.IsEnabled,
                                    "Opening the middle filtered step did not create a 2/3 review context.");

                                vm.SequenceEditor.SelectedStep!.TimeoutMs += 50;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    vm.HasProcessPlanReturnContext
                                    && vm.RecipeConnections.IsProcessBlockFilterCustomized
                                    && vm.RecipeConnections.VisibleProcessBlockItems.Count == 3
                                    && vm.ProcessPlanReviewPositionText.Contains("2/3", StringComparison.Ordinal)
                                    && vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                                    && vm.NextProcessPlanReviewStepCommand.CanExecute(null),
                                    "Editing the current step discarded its filtered review context.");
                                var reviewSourceAfterEdit = processStore.SerializeForEvidence(processProject);

                                vm.NextProcessPlanReviewStepCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    string.Equals(vm.SequenceEditor.SelectedStep?.Id, reviewStepIds[2], StringComparison.Ordinal)
                                    && string.Equals(vm.ProcessPlanReturnStepId, reviewStepIds[2], StringComparison.Ordinal)
                                    && vm.ProcessPlanReviewPositionText.Contains("3/3", StringComparison.Ordinal)
                                    && vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                                    && !vm.NextProcessPlanReviewStepCommand.CanExecute(null)
                                    && previousReviewButton.IsEnabled
                                    && !nextReviewButton.IsEnabled,
                                    "Next review did not select the exact last filtered step and enforce its boundary.");

                                vm.PreviousProcessPlanReviewStepCommand.Execute(null);
                                vm.PreviousProcessPlanReviewStepCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    string.Equals(vm.SequenceEditor.SelectedStep?.Id, reviewStepIds[0], StringComparison.Ordinal)
                                    && string.Equals(vm.ProcessPlanReturnStepId, reviewStepIds[0], StringComparison.Ordinal)
                                    && vm.ProcessPlanReviewPositionText.Contains("1/3", StringComparison.Ordinal)
                                    && !vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                                    && vm.NextProcessPlanReviewStepCommand.CanExecute(null)
                                    && !previousReviewButton.IsEnabled
                                    && nextReviewButton.IsEnabled,
                                    "Previous review did not select the exact first filtered step and enforce its boundary.");

                                vm.NextProcessPlanReviewStepCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                vm.RecipeConnections.IsEditable = false;
                                ((RelayCommand)vm.PreviousProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                                ((RelayCommand)vm.NextProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    !previousReviewButton.IsEnabled
                                    && !nextReviewButton.IsEnabled
                                    && !vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                                    && !vm.NextProcessPlanReviewStepCommand.CanExecute(null),
                                    "Filtered-step review navigation did not disable with the workbench.");
                                vm.RecipeConnections.IsEditable = true;
                                ((RelayCommand)vm.PreviousProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                                ((RelayCommand)vm.NextProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                                window.Activate();
                                nextReviewButton.BringIntoView();
                                nextReviewButton.Focus();
                                Keyboard.Focus(nextReviewButton);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    nextReviewButton.IsKeyboardFocused,
                                    "Next filtered-step review did not expose keyboard focus.");
                                MovePointerToCenter(nextReviewButton);
                                Mouse.Capture(nextReviewButton, CaptureMode.SubTree);
                                Mouse.Synchronize();
                                await Task.Delay(200);
                                AssertSmoke(
                                    nextReviewButton.IsMouseOver,
                                    "Next filtered-step review did not enter hover state.");
                                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                                _smokePointerHeld = true;
                                nextReviewButton.RaiseEvent(new MouseButtonEventArgs(
                                    Mouse.PrimaryDevice,
                                    Environment.TickCount,
                                    MouseButton.Left)
                                {
                                    RoutedEvent = Mouse.MouseDownEvent
                                });
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(
                                    nextReviewButton.IsPressed,
                                    "Next filtered-step review did not enter pointer-down state.");
                                AssertSmoke(
                                    reviewSourceAfterEdit == processStore.SerializeForEvidence(processProject)
                                    && reviewRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                                    && reviewRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                                    && vm.IsDesignMode
                                    && !vm.IsRunning,
                                    "Filtered-step review navigation changed the project or runtime.");
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                                {
                                    AssertSmoke(
                                        !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                        "Filtered-step review unexpectedly saved a project file.");
                                }
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                                {
                                    connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                                    {
                                        Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                        {
                                            ["filtered-review-list-captured-in-order"] = true,
                                            ["middle-review-position-shown"] = true,
                                            ["sequence-edit-preserved-review-context"] = true,
                                            ["next-selected-exact-filtered-step"] = true,
                                            ["last-step-disabled-next"] = true,
                                            ["previous-selected-exact-filtered-step"] = true,
                                            ["first-step-disabled-previous"] = true,
                                            ["disabled-workbench-disabled-review"] = true,
                                            ["review-keyboard-focus-visible"] = true,
                                            ["review-pointer-down-visible"] = true,
                                            ["review-project-and-runtime-unchanged"] = true,
                                            ["review-did-not-save-project"] = true
                                        },
                                        Failures = []
                                    };
                                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                                }
                                break;
                            }

                            var navigationRuntimeBefore = vm.SceneSnapshots.Latest;
                            vm.RecipeConnections.OpenSequenceStepCommand.Execute(existingItem);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.SelectedDocumentTabIndex == 2
                                && string.Equals(vm.SequenceEditor.SelectedSequence?.Id, existingItem.SequenceId, StringComparison.Ordinal)
                                && string.Equals(vm.SequenceEditor.SelectedStep?.Id, existingItem.StepId, StringComparison.Ordinal),
                                $"Process-step navigation did not select its exact owning Sequence step. "
                                + $"tab={vm.SelectedDocumentTabIndex}, expectedSequence={existingItem.SequenceId}, "
                                + $"actualSequence={vm.SequenceEditor.SelectedSequence?.Id}, expectedStep={existingItem.StepId}, "
                                + $"actualStep={vm.SequenceEditor.SelectedStep?.Id}");
                            AssertSmoke(
                                appliedBeforeEdit == processStore.SerializeForEvidence(processProject)
                                && navigationRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                                && navigationRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                                && vm.IsDesignMode
                                && !vm.IsRunning,
                                "Process-step navigation changed the project or runtime.");

                            if (connectionWorkbenchState.StartsWith("process-block-step-return", StringComparison.OrdinalIgnoreCase))
                            {
                                var returnButton = FindVisualDescendant<Button>(window, candidate => string.Equals(
                                    candidate.Name,
                                    "ReturnToProcessPlanButton",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Return-to-process-plan button was not available.");
                                var returnBar = FindVisualDescendant<Border>(window, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessPlanReturnBar",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Return-to-process-plan bar was not available.");
                                AssertSmoke(
                                    vm.HasProcessPlanReturnContext
                                    && string.Equals(vm.ProcessPlanReturnStepId, existingItem.StepId, StringComparison.Ordinal)
                                    && vm.ReturnToProcessPlanCommand.CanExecute(null)
                                    && returnBar.IsVisible
                                    && returnButton.IsEnabled,
                                    "Opening a managed process step did not retain an enabled return context.");

                                if (connectionWorkbenchState.Equals("process-block-step-return-sequence", StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }
                                if (connectionWorkbenchState.Equals("process-block-step-return-focus", StringComparison.OrdinalIgnoreCase)
                                    || connectionWorkbenchState.Equals("process-block-step-return-pressed", StringComparison.OrdinalIgnoreCase))
                                {
                                    window.Activate();
                                    returnButton.Focus();
                                    Keyboard.Focus(returnButton);
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                    AssertSmoke(returnButton.IsKeyboardFocused, "Return-to-process-plan button did not receive focus.");
                                    if (connectionWorkbenchState.Equals("process-block-step-return-pressed", StringComparison.OrdinalIgnoreCase))
                                    {
                                        MovePointerToCenter(returnButton);
                                        Mouse.Capture(returnButton, CaptureMode.SubTree);
                                        Mouse.Synchronize();
                                        await Task.Delay(200);
                                        AssertSmoke(returnButton.IsMouseOver, "Return-to-process-plan button did not enter hover state.");
                                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                                        _smokePointerHeld = true;
                                        returnButton.RaiseEvent(new MouseButtonEventArgs(
                                            Mouse.PrimaryDevice,
                                            Environment.TickCount,
                                            MouseButton.Left)
                                        {
                                            RoutedEvent = Mouse.MouseDownEvent
                                        });
                                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                        AssertSmoke(returnButton.IsPressed, "Return-to-process-plan button did not enter pointer-down state.");
                                    }
                                    break;
                                }
                                if (connectionWorkbenchState.Equals("process-block-step-return-disabled", StringComparison.OrdinalIgnoreCase))
                                {
                                    vm.RecipeConnections.IsEditable = false;
                                    if (vm.ReturnToProcessPlanCommand is RelayCommand returnCommand)
                                    {
                                        returnCommand.RaiseCanExecuteChanged();
                                    }
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                    AssertSmoke(
                                        !vm.ReturnToProcessPlanCommand.CanExecute(null) && !returnButton.IsEnabled,
                                        "Return-to-process-plan button did not enter its disabled state.");
                                    break;
                                }
                                if (connectionWorkbenchState.Equals("process-block-step-return-closed", StringComparison.OrdinalIgnoreCase))
                                {
                                    vm.RecipeConnections.CancelProcessBlockCommand.Execute(null);
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                    AssertSmoke(
                                        !vm.HasProcessPlanReturnContext
                                        && !vm.ReturnToProcessPlanCommand.CanExecute(null)
                                        && !returnBar.IsVisible,
                                        "Closing the process plan did not clear its return context.");
                                    break;
                                }
                                if (connectionWorkbenchState.Equals("process-block-step-return-reopen", StringComparison.OrdinalIgnoreCase))
                                {
                                    AssertSmoke(
                                        !string.IsNullOrWhiteSpace(projectPath)
                                        && await vm.OpenProjectAsync(Path.GetFullPath(projectPath)),
                                        "The source project could not be reopened for return-context validation.");
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                    AssertSmoke(
                                        !vm.HasProcessPlanReturnContext
                                        && !vm.ReturnToProcessPlanCommand.CanExecute(null)
                                        && !returnBar.IsVisible
                                        && vm.IsDesignMode
                                        && !vm.IsRunning,
                                        "Reopening a project retained stale process-plan return context.");
                                    break;
                                }

                                var selectedSequenceStep = vm.SequenceEditor.SelectedStep
                                    ?? throw new InvalidOperationException("The managed Sequence step was not selected for tuning.");
                                var templateTimeoutMs = selectedSequenceStep.TimeoutMs;
                                selectedSequenceStep.TimeoutMs += 100;
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                var customizedItem = vm.RecipeConnections.ProcessBlockItems.Single(item => string.Equals(
                                    item.StepId,
                                    existingItem.StepId,
                                    StringComparison.Ordinal));
                                AssertSmoke(
                                    vm.HasProcessPlanReturnContext
                                    && vm.RecipeConnections.IsProcessBlockPreviewVisible
                                    && customizedItem.IsCustomized
                                    && !customizedItem.IsUnavailable
                                    && customizedItem.CanOpenSequenceStep
                                    && vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(customizedItem)
                                    && !vm.RecipeConnections.HasProcessBlockPlanError
                                    && !processApplyButton.IsEnabled
                                    && customizedItem.DetailText.Contains(
                                        $"{OpenVisionLanguageService.T("Sequence.Timeout")}: "
                                        + $"{selectedSequenceStep.TimeoutMs.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} ms",
                                        StringComparison.Ordinal)
                                    && customizedItem.DetailText.Contains(
                                        string.Format(
                                            System.Globalization.CultureInfo.CurrentCulture,
                                            OpenVisionLanguageService.T("Connections.ProcessBlockTemplateValueFormat"),
                                            $"{templateTimeoutMs.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} ms"),
                                        StringComparison.Ordinal),
                                    "Editing a managed timeout did not preserve, classify, explain, and keep the plan safely navigable.");
                                var editedBeforeReturn = processStore.SerializeForEvidence(processProject);
                                var runtimeBeforeReturn = vm.SceneSnapshots.Latest;
                                vm.ReturnToProcessPlanCommand.Execute(null);
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                await Task.Delay(100);
                                var processItemsList = FindVisualDescendant<ListBox>(workbench, candidate => string.Equals(
                                    candidate.Name,
                                    "ProcessBlockItemsListBox",
                                    StringComparison.Ordinal))
                                    ?? throw new InvalidOperationException("Process block step list was not available after return.");
                                var returnedItem = vm.RecipeConnections.SelectedProcessBlockItem;
                                var returnedContainer = returnedItem is null
                                    ? null
                                    : processItemsList.ItemContainerGenerator.ContainerFromItem(returnedItem) as FrameworkElement;
                                var returnedBounds = returnedContainer is null
                                    ? Rect.Empty
                                    : new Rect(
                                        returnedContainer.TranslatePoint(new Point(0, 0), processItemsList),
                                        new Size(returnedContainer.ActualWidth, returnedContainer.ActualHeight));
                                var processListViewport = new Rect(
                                    new Point(0, 0),
                                    new Size(processItemsList.ActualWidth, processItemsList.ActualHeight));
                                var returnedWindowBounds = returnedContainer is null
                                    ? Rect.Empty
                                    : new Rect(
                                        returnedContainer.TranslatePoint(new Point(0, 0), window),
                                        new Size(returnedContainer.ActualWidth, returnedContainer.ActualHeight));
                                var windowViewport = new Rect(
                                    new Point(0, 0),
                                    new Size(window.ActualWidth, window.ActualHeight));
                                AssertSmoke(
                                    vm.SelectedDocumentTabIndex == 1
                                    && vm.RecipeConnections.IsProcessBlockPreviewVisible
                                    && returnedItem is not null
                                    && string.Equals(returnedItem.StepId, existingItem.StepId, StringComparison.Ordinal)
                                    && returnedContainer is not null
                                    && returnedBounds.IntersectsWith(processListViewport)
                                    && windowViewport.Contains(returnedWindowBounds.TopLeft)
                                    && windowViewport.Contains(returnedWindowBounds.BottomRight),
                                    "Return-to-process-plan did not restore and reveal the exact originating card.");
                                AssertSmoke(
                                    editedBeforeReturn == processStore.SerializeForEvidence(processProject)
                                    && runtimeBeforeReturn?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                                    && runtimeBeforeReturn?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                                    && vm.IsDesignMode
                                    && !vm.IsRunning,
                                    "Returning to the process plan changed the edited project or runtime.");
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                                {
                                    AssertSmoke(
                                        !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                        "Returning to the process plan unexpectedly saved a project file.");
                                }
                                if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                                {
                                    connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                                    {
                                        Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                        {
                                            ["process-card-return-context-created"] = true,
                                            ["return-command-visible-and-enabled"] = true,
                                            ["sequence-edit-preserved-open-plan"] = true,
                                            ["sequence-edit-refreshed-current-card-settings"] = true,
                                            ["sequence-edit-classified-customized"] = true,
                                            ["customized-card-explained-template-difference"] = true,
                                            ["customized-card-remained-navigable"] = true,
                                            ["connections-document-restored"] = true,
                                            ["exact-origin-card-selected"] = true,
                                            ["exact-origin-card-scrolled-into-view"] = true,
                                            ["return-project-unchanged"] = true,
                                            ["return-runtime-unchanged"] = true,
                                            ["return-remains-stopped-in-design"] = true,
                                            ["return-did-not-save-project"] = true
                                        },
                                        Failures = []
                                    };
                                    connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                                }
                                break;
                            }

                            var bundledNavigationRecipes = Directory.EnumerateFiles(
                                    Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                                    "*.ovmachine")
                                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            var allBundledTargetsExact = bundledNavigationRecipes.Length == 10;
                            foreach (var path in bundledNavigationRecipes)
                            {
                                var bundledProject = processStore.Load(File.ReadAllText(path));
                                var composer = new SemiconductorProcessBlockComposer();
                                composer.Apply(bundledProject, Enum.GetValues<SemiconductorProcessBlockKind>());
                                var preview = composer.Preview(
                                    bundledProject,
                                    Enum.GetValues<SemiconductorProcessBlockKind>());
                                var sequenceId = bundledProject.Simulation.AutomaticRun?.SequenceId;
                                var sequence = bundledProject.Sequences.FirstOrDefault(candidate => string.Equals(
                                    candidate.Id,
                                    sequenceId,
                                    StringComparison.Ordinal));
                                allBundledTargetsExact &= preview.Steps.Count == 13
                                    && preview.Steps.All(step => step.Status == SemiconductorProcessBlockStepStatus.Existing)
                                    && sequence is not null
                                    && preview.Steps.All(entry => sequence.Steps.Any(step => string.Equals(
                                        step.Id,
                                        entry.StepId,
                                        StringComparison.Ordinal)));
                            }
                            AssertSmoke(
                                allBundledTargetsExact,
                                "All ten bundled recipes did not resolve every managed process card to an exact Sequence step.");
                            if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                            {
                                AssertSmoke(
                                    !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                    "Process-step navigation unexpectedly wrote a project file.");
                            }
                            if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                            {
                                connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                                {
                                    Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                    {
                                        ["existing-card-navigation-enabled"] = true,
                                        ["exact-owning-sequence-selected"] = true,
                                        ["exact-owning-step-selected"] = true,
                                        ["sequence-document-opened"] = true,
                                        ["navigation-project-unchanged"] = true,
                                        ["navigation-runtime-unchanged"] = true,
                                        ["navigation-remains-stopped-in-design"] = true,
                                        ["ten-recipes-thirteen-managed-targets-resolved"] = true,
                                        ["navigation-did-not-save-project"] = true
                                    },
                                    Failures = []
                                };
                                connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                            }
                            break;
                        }
                        if (connectionWorkbenchState.Equals("process-block-edit-current", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        vm.RecipeConnections.IsInspectBlockSelected = false;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.SelectedProcessBlockCount == 4
                            && vm.RecipeConnections.ProcessBlockItems.Count == 13
                            && vm.RecipeConnections.ProcessBlockItems.Count(item => item.IsProposedRemoval) == 1
                            && processApplyButton.IsEnabled
                            && appliedBeforeEdit == processStore.SerializeForEvidence(processProject),
                            "Clearing Inspect did not preview exactly one managed-step removal without mutation.");
                        if (connectionWorkbenchState.Equals("process-block-edit-remove", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        if (connectionWorkbenchState.Equals("process-block-edit-empty", StringComparison.OrdinalIgnoreCase))
                        {
                            vm.RecipeConnections.IsLoadBlockSelected = false;
                            vm.RecipeConnections.IsAlignBlockSelected = false;
                            vm.RecipeConnections.IsProcessBlockSelected = false;
                            vm.RecipeConnections.IsUnloadBlockSelected = false;
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.RecipeConnections.SelectedProcessBlockCount == 0
                                && vm.RecipeConnections.ProcessBlockItems.Count == 13
                                && vm.RecipeConnections.ProcessBlockItems.All(item => item.IsProposedRemoval)
                                && processApplyButton.IsEnabled
                                && appliedBeforeEdit == processStore.SerializeForEvidence(processProject),
                                "Clearing the current plan did not preview all managed-step removals safely.");
                            break;
                        }

                        vm.RecipeConnections.ApplyProcessBlockCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.RecipeStepCount == 24
                            && vm.IsDesignMode
                            && !vm.IsRunning,
                            "Removing Inspect did not retain the expected stopped 24-step recipe.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                        for (var attempt = 0;
                             attempt < 200 && !vm.RecipeConnections.HasRecipeDryRunResult;
                             attempt++)
                        {
                            await Task.Delay(20);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        AssertSmoke(
                            vm.RecipeConnections.ReadinessPassed == true
                            && vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
                            && vm.RecipeConnections.RecipeDryRunTimeline.Count == 24,
                            "The edited 24-step recipe did not pass readiness and bounded dry run.");
                        var retainedKinds = Enum.GetValues<SemiconductorProcessBlockKind>()
                            .Where(kind => kind != SemiconductorProcessBlockKind.Inspect)
                            .ToArray();
                        var bundledProcessRecipes = Directory.EnumerateFiles(
                                Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                                "*.ovmachine")
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var allBundledEditsComplete = bundledProcessRecipes.Length == 10;
                        foreach (var path in bundledProcessRecipes)
                        {
                            var bundledProject = processStore.Load(File.ReadAllText(path));
                            var composer = new SemiconductorProcessBlockComposer();
                            allBundledEditsComplete &= composer.Apply(
                                bundledProject,
                                Enum.GetValues<SemiconductorProcessBlockKind>()).Changed;
                            var editResult = composer.Apply(bundledProject, retainedKinds);
                            var bundledSequenceId = bundledProject.Simulation.AutomaticRun?.SequenceId ?? string.Empty;
                            var bundledResult = await new DeterministicRecipeDryRunRunner().RunAsync(
                                bundledProject,
                                bundledSequenceId);
                            allBundledEditsComplete &= editResult is { Changed: true, RemovedStepCount: 1 }
                                && bundledResult.Outcome == RecipeDryRunOutcome.Completed
                                && bundledResult.Timeline.Count == 24;
                        }
                        AssertSmoke(
                            allBundledEditsComplete,
                            "Inspect removal did not complete a 24-step dry run in all ten bundled recipes.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            var savePath = Path.GetFullPath(connectionWorkbenchSavePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                            await vm.SaveProjectAsync(savePath);
                            AssertSmoke(await vm.OpenProjectAsync(savePath), "The edited process plan did not reopen.");
                            vm.RecipeConnections.PreviewProcessBlockCommand.Execute(null);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.RecipeConnections.RecipeStepCount == 24
                                && vm.RecipeConnections.SelectedProcessBlockCount == 4
                                && vm.RecipeConnections.ExistingProcessBlockCount == 4
                                && !processApplyButton.IsEnabled
                                && vm.IsDesignMode
                                && !vm.IsRunning,
                                "Reopened process plan did not restore the edited four-block selection safely.");
                        }
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                        {
                            connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                            {
                                Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                {
                                    ["current-five-block-plan-recognized"] = true,
                                    ["recognized-plan-preview-unchanged"] = true,
                                    ["inspect-removal-previewed-once"] = true,
                                    ["removal-preview-project-unchanged"] = true,
                                    ["inspect-managed-step-removed"] = true,
                                    ["twenty-four-step-recipe-retained"] = true,
                                    ["edit-remains-stopped-in-design"] = true,
                                    ["readiness-passed"] = true,
                                    ["bounded-dry-run-completed"] = true,
                                    ["twenty-four-step-timeline"] = true,
                                    ["ten-bundled-recipes-edited-and-dry-run"] = true,
                                    ["save-reopen-retained-edit"] = true,
                                    ["reopened-four-block-plan-recognized"] = true,
                                    ["reopen-remains-stopped-in-design"] = true
                                },
                                Failures = []
                            };
                            connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                        }
                        break;
                    }
                    if (connectionWorkbenchState.Equals("process-block-preview", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    if (connectionWorkbenchState.Equals("process-block-check-focus", StringComparison.OrdinalIgnoreCase)
                        || connectionWorkbenchState.Equals("process-block-check-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        window.Activate();
                        loadBlockCheckBox.Focus();
                        Keyboard.Focus(loadBlockCheckBox);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(loadBlockCheckBox.IsKeyboardFocused, "Load block checkbox did not receive focus.");
                        if (connectionWorkbenchState.Equals("process-block-check-pressed", StringComparison.OrdinalIgnoreCase))
                        {
                            MovePointerToCenter(loadBlockCheckBox);
                            Mouse.Capture(loadBlockCheckBox, CaptureMode.SubTree);
                            Mouse.Synchronize();
                            await Task.Delay(200);
                            AssertSmoke(loadBlockCheckBox.IsMouseOver, "Load block checkbox did not enter hover state.");
                            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                            _smokePointerHeld = true;
                            loadBlockCheckBox.RaiseEvent(new MouseButtonEventArgs(
                                Mouse.PrimaryDevice,
                                Environment.TickCount,
                                MouseButton.Left)
                            {
                                RoutedEvent = Mouse.MouseDownEvent
                            });
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(loadBlockCheckBox.IsPressed, "Load block checkbox did not enter pointer-down state.");
                        }
                        break;
                    }
                    if (connectionWorkbenchState.Equals("process-block-disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.IsEditable = false;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            !processApplyButton.IsEnabled && !loadBlockCheckBox.IsEnabled,
                            "Process block controls did not enter their disabled state.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("process-block-empty", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.IsLoadBlockSelected = false;
                        vm.RecipeConnections.IsAlignBlockSelected = false;
                        vm.RecipeConnections.IsProcessBlockSelected = false;
                        vm.RecipeConnections.IsInspectBlockSelected = false;
                        vm.RecipeConnections.IsUnloadBlockSelected = false;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.SelectedProcessBlockCount == 0
                            && vm.RecipeConnections.ProcessBlockItems.Count == 0
                            && !processApplyButton.IsEnabled
                            && processBefore == processStore.SerializeForEvidence(processProject),
                            "An empty process plan did not block Apply without changing the project.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("process-block-applied", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.ApplyProcessBlockCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.RecipeStepCount == 25
                            && vm.IsDesignMode
                            && !vm.IsRunning,
                            "Five process blocks did not produce the expected stopped 25-step recipe.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                        for (var attempt = 0;
                             attempt < 200 && !vm.RecipeConnections.HasRecipeDryRunResult;
                             attempt++)
                        {
                            await Task.Delay(20);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        AssertSmoke(
                            vm.RecipeConnections.ReadinessPassed == true
                            && vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
                            && vm.RecipeConnections.RecipeDryRunTimeline.Count == 25,
                            "The composed 25-step recipe did not pass readiness and bounded dry run.");
                        var bundledProcessRecipes = Directory.EnumerateFiles(
                                Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                                "*.ovmachine")
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var allBundledBlocksComplete = bundledProcessRecipes.Length == 10;
                        foreach (var path in bundledProcessRecipes)
                        {
                            var bundledProject = processStore.Load(File.ReadAllText(path));
                            var composer = new SemiconductorProcessBlockComposer();
                            allBundledBlocksComplete &= composer.Apply(
                                bundledProject,
                                Enum.GetValues<SemiconductorProcessBlockKind>()).Changed;
                            var bundledSequenceId = bundledProject.Simulation.AutomaticRun?.SequenceId ?? string.Empty;
                            var bundledResult = await new DeterministicRecipeDryRunRunner().RunAsync(
                                bundledProject,
                                bundledSequenceId);
                            allBundledBlocksComplete &= bundledResult.Outcome == RecipeDryRunOutcome.Completed
                                && bundledResult.Timeline.Count == 25;
                        }
                        AssertSmoke(
                            allBundledBlocksComplete,
                            "Five process blocks did not complete a 25-step dry run in all ten bundled recipes.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            var savePath = Path.GetFullPath(connectionWorkbenchSavePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                            await vm.SaveProjectAsync(savePath);
                            AssertSmoke(await vm.OpenProjectAsync(savePath), "The composed process-block project did not reopen.");
                            AssertSmoke(
                                vm.RecipeConnections.RecipeStepCount == 25
                                && vm.IsDesignMode
                                && !vm.IsRunning,
                                "Reopened process blocks were not retained safely.");
                        }
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                        {
                            connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                            {
                                Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                {
                                    ["five-block-plan-preview-thirteen-steps"] = true,
                                    ["preview-project-unchanged"] = true,
                                    ["preview-runtime-unchanged"] = true,
                                    ["five-blocks-applied-once"] = true,
                                    ["twenty-five-step-recipe"] = true,
                                    ["apply-remains-stopped-in-design"] = true,
                                    ["readiness-passed"] = true,
                                    ["bounded-dry-run-completed"] = true,
                                    ["twenty-five-step-timeline"] = true,
                                    ["ten-bundled-recipes-composed-and-dry-run"] = true,
                                    ["save-reopen-retained-blocks"] = true,
                                    ["reopen-remains-stopped-in-design"] = true
                                },
                                Failures = []
                            };
                            connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                        }
                        break;
                    }

                    window.Activate();
                    processApplyButton.BringIntoView();
                    processApplyButton.UpdateLayout();
                    processApplyButton.Focus();
                    Keyboard.Focus(processApplyButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(processApplyButton.IsKeyboardFocused, "Process block Apply button did not receive focus.");
                    if (connectionWorkbenchState.Equals("process-block-apply-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        MovePointerToCenter(processApplyButton);
                        Mouse.Capture(processApplyButton, CaptureMode.SubTree);
                        Mouse.Synchronize();
                        await Task.Delay(200);
                        AssertSmoke(processApplyButton.IsMouseOver, "Process block Apply button did not enter hover state.");
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        processApplyButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(processApplyButton.IsPressed, "Process block Apply button did not enter pointer-down state.");
                    }
                    break;
                case "load-lock-focus":
                    window.Activate();
                    loadLockSetupButton.Focus();
                    Keyboard.Focus(loadLockSetupButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(loadLockSetupButton.IsKeyboardFocused, "Load-lock setup button did not receive focus.");
                    break;
                case "load-lock-hover":
                case "load-lock-pressed":
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    loadLockSetupButton.BringIntoView();
                    loadLockSetupButton.UpdateLayout();
                    loadLockSetupButton.Focus();
                    MovePointerToCenter(loadLockSetupButton);
                    await Task.Delay(150);
                    AssertSmoke(loadLockSetupButton.IsMouseOver, "Load-lock setup button did not enter hover state.");
                    if (connectionWorkbenchState.Equals("load-lock-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(loadLockSetupButton.IsPressed, "Load-lock setup button did not enter pointer-down state.");
                    }
                    break;
                case "load-lock-preview":
                case "load-lock-input-focus":
                case "load-lock-input-disabled":
                case "load-lock-invalid":
                case "load-lock-stale":
                case "load-lock-combo-open":
                case "load-lock-apply-focus":
                case "load-lock-apply-pressed":
                case "load-lock-applied":
                case "load-lock-reopen":
                    var loadLockStore = new ProjectDocumentStore();
                    var loadLockProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for load-lock setup smoke.");
                    if (connectionWorkbenchState.Equals("load-lock-stale", StringComparison.OrdinalIgnoreCase))
                    {
                        loadLockProject.Devices.Single(device => device.Kind == DeviceKind.LoadLock)
                            .LoadLock!.OuterDoorComponentId = "missing.outer-door";
                        vm.RecipeConnections.Load(loadLockProject);
                    }
                    var loadLockBeforePreview = loadLockStore.Serialize(loadLockProject);
                    var loadLockRuntimeBefore = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.PreviewLoadLockSetupCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var loadLockPanel = FindVisualDescendant<Border>(
                        workbench,
                        candidate => string.Equals(candidate.Name, "LoadLockSetupPreview", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Load-lock setup panel was not available.");
                    var loadLockApplyButton = FindVisualDescendant<Button>(
                        workbench,
                        candidate => string.Equals(candidate.Name, "ApplyLoadLockSetupButton", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Load-lock Apply button was not available.");
                    var loadLockPumpTextBox = FindVisualDescendant<TextBox>(
                        workbench,
                        candidate => string.Equals(candidate.Name, "LoadLockPumpDownDurationTextBox", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Load-lock pump-down input was not available.");
                    var loadLockOuterDoor = FindVisualDescendant<ComboBox>(
                        workbench,
                        candidate => string.Equals(candidate.Name, "LoadLockOuterDoorComboBox", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Load-lock outer-door selector was not available.");
                    AssertSmoke(
                        loadLockPanel.IsVisible
                        && loadLockBeforePreview == loadLockStore.Serialize(loadLockProject)
                        && loadLockRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && loadLockRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && !vm.IsRunning
                        && vm.IsDesignMode,
                        "Load-lock preview changed project or runtime state.");
                    if (connectionWorkbenchState.Equals("load-lock-stale", StringComparison.OrdinalIgnoreCase))
                    {
                        AssertSmoke(
                            vm.RecipeConnections.OuterDoorComponentId == "missing.outer-door"
                            && vm.RecipeConnections.LoadLockDoorOptions.Any(option =>
                                option.Id == "missing.outer-door" && option.DisplayName.Contains("missing.outer-door", StringComparison.Ordinal))
                            && vm.RecipeConnections.HasLoadLockSetupValidationError
                            && !loadLockApplyButton.IsEnabled,
                            "A stale load-lock reference was not kept visible and blocked from Apply.");
                        break;
                    }
                    var expectedPumpDown = connectionWorkbenchState.Equals(
                        "load-lock-reopen",
                        StringComparison.OrdinalIgnoreCase) ? "255" : "250";
                    var expectedVent = connectionWorkbenchState.Equals(
                        "load-lock-reopen",
                        StringComparison.OrdinalIgnoreCase) ? "260" : "250";
                    AssertSmoke(
                        vm.RecipeConnections.OuterDoorComponentId == "outer-door"
                        && vm.RecipeConnections.InnerDoorComponentId == "process-cylinder"
                        && vm.RecipeConnections.EvacuateCommandChannelId == "do.load-lock.evacuate"
                        && vm.RecipeConnections.VentCommandChannelId == "do.load-lock.vent"
                        && vm.RecipeConnections.VacuumReadySensorChannelId == "di.load-lock.vacuum-ready"
                        && vm.RecipeConnections.AtmosphereReadySensorChannelId == "di.load-lock.atmosphere-ready"
                        && vm.RecipeConnections.PumpDownDurationText == expectedPumpDown
                        && vm.RecipeConnections.VentDurationText == expectedVent
                        && loadLockApplyButton.IsEnabled,
                        "Saved load-lock setup was not restored as an editable valid draft.");
                    if (connectionWorkbenchState.Equals("load-lock-preview", StringComparison.OrdinalIgnoreCase)
                        || connectionWorkbenchState.Equals("load-lock-reopen", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    if (connectionWorkbenchState.Equals("load-lock-input-focus", StringComparison.OrdinalIgnoreCase))
                    {
                        window.Activate();
                        loadLockPumpTextBox.Focus();
                        Keyboard.Focus(loadLockPumpTextBox);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            loadLockPumpTextBox.IsKeyboardFocused && loadLockPumpTextBox.Text == "250",
                            "Load-lock timing input did not render its value with keyboard focus.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("load-lock-input-disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.IsEditable = false;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            !loadLockPumpTextBox.IsEnabled && !loadLockOuterDoor.IsEnabled && !loadLockApplyButton.IsEnabled,
                            "Load-lock setup inputs did not enter their disabled state.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("load-lock-invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.PumpDownDurationText = "251";
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.HasLoadLockSetupValidationError
                            && !loadLockApplyButton.IsEnabled
                            && loadLockBeforePreview == loadLockStore.Serialize(loadLockProject),
                            "Invalid load-lock timing did not block Apply without changing the project.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("load-lock-combo-open", StringComparison.OrdinalIgnoreCase))
                    {
                        window.Activate();
                        loadLockOuterDoor.Focus();
                        Keyboard.Focus(loadLockOuterDoor);
                        loadLockOuterDoor.IsDropDownOpen = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            loadLockOuterDoor.IsDropDownOpen && loadLockOuterDoor.Items.Count >= 2,
                            "Load-lock door selector popup did not open with candidates.");
                        var loadLockWindowRoot = PresentationSource.FromVisual(window)?.RootVisual;
                        _smokePopupContent = PresentationSource.CurrentSources
                            .Cast<PresentationSource>()
                            .Select(source => source.RootVisual)
                            .OfType<FrameworkElement>()
                            .FirstOrDefault(root =>
                                !ReferenceEquals(root, loadLockWindowRoot)
                                && root.IsVisible
                                && root.ActualWidth > 0
                                && root.ActualHeight > 0)
                            ?? throw new InvalidOperationException("Load-lock door selector popup content was unavailable.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("load-lock-applied", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.PumpDownDurationText = "255";
                        vm.RecipeConnections.VentDurationText = "260";
                        vm.RecipeConnections.OuterDoorComponentId = "process-cylinder";
                        AssertSmoke(
                            vm.RecipeConnections.ResetLoadLockSetupCommand.CanExecute(null),
                            "Load-lock saved-value reset was not available.");
                        vm.RecipeConnections.ResetLoadLockSetupCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.OuterDoorComponentId == "outer-door"
                            && vm.RecipeConnections.PumpDownDurationText == "250"
                            && loadLockBeforePreview == loadLockStore.Serialize(loadLockProject),
                            "Restoring saved load-lock values changed the project before Apply.");
                        vm.RecipeConnections.PumpDownDurationText = "255";
                        vm.RecipeConnections.VentDurationText = "260";
                        vm.RecipeConnections.ApplyLoadLockSetupCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        var appliedLoadLock = loadLockProject.Devices.Single(device => device.Kind == DeviceKind.LoadLock).LoadLock;
                        AssertSmoke(
                            appliedLoadLock is { PumpDownDurationMilliseconds: 255, VentDurationMilliseconds: 260 }
                            && !vm.RecipeConnections.IsLoadLockSetupVisible
                            && !vm.IsRunning
                            && vm.IsDesignMode
                            && loadLockRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                            && loadLockRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime,
                            "Applying load-lock settings did not update only the project while staying stopped in Design mode.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(vm.RecipeConnections.ReadinessPassed == true, "Applied load-lock setup did not compile for simulation.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            var loadLockSavePath = Path.GetFullPath(connectionWorkbenchSavePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(loadLockSavePath)!);
                            await vm.SaveProjectAsync(loadLockSavePath);
                            AssertSmoke(await vm.OpenProjectAsync(loadLockSavePath), "Load-lock setup project did not reopen.");
                            vm.RecipeConnections.PreviewLoadLockSetupCommand.Execute(null);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.RecipeConnections.PumpDownDurationText == "255"
                                && vm.RecipeConnections.VentDurationText == "260"
                                && !vm.IsRunning
                                && vm.IsDesignMode,
                                "Saved load-lock setup was not restored safely after reopen.");
                        }
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                        {
                            connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                            {
                                Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                {
                                    ["preview-project-unchanged"] = true,
                                    ["preview-runtime-unchanged"] = true,
                                    ["saved-values-restored"] = true,
                                    ["reset-without-project-change"] = true,
                                    ["custom-timings-applied"] = true,
                                    ["apply-runtime-unchanged"] = true,
                                    ["readiness-compilation-passed"] = true,
                                    ["save-reopen-restored"] = true,
                                    ["reopen-stays-stopped-in-design"] = true
                                },
                                Failures = []
                            };
                            connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                        }
                        break;
                    }

                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    loadLockApplyButton.BringIntoView();
                    loadLockApplyButton.UpdateLayout();
                    loadLockApplyButton.Focus();
                    Keyboard.Focus(loadLockApplyButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(loadLockApplyButton.IsKeyboardFocused, "Load-lock Apply button did not receive focus.");
                    if (connectionWorkbenchState.Equals("load-lock-apply-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        MovePointerToCenter(loadLockApplyButton);
                        Mouse.Capture(loadLockApplyButton, CaptureMode.SubTree);
                        Mouse.Synchronize();
                        await Task.Delay(150);
                        AssertSmoke(loadLockApplyButton.IsMouseOver, "Load-lock Apply button did not enter hover state.");
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        loadLockApplyButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(loadLockApplyButton.IsPressed, "Load-lock Apply button did not enter pointer-down state.");
                    }
                    break;
                case "station-skeleton-focus":
                    window.Activate();
                    stationSkeletonButton.Focus();
                    Keyboard.Focus(stationSkeletonButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        stationSkeletonButton.IsKeyboardFocused,
                        "Semiconductor station button did not receive focus.");
                    break;
                case "station-skeleton-hover":
                case "station-skeleton-pressed":
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    stationSkeletonButton.Focus();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    MovePointerToCenter(stationSkeletonButton);
                    await Task.Delay(100);
                    AssertSmoke(
                        stationSkeletonButton.IsMouseOver,
                        "Semiconductor station button did not enter hover state.");
                    if (connectionWorkbenchState.Equals("station-skeleton-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            stationSkeletonButton.IsPressed,
                            "Semiconductor station button did not enter pointer-down state.");
                    }
                    break;
                case "station-skeleton-preview":
                case "station-skeleton-apply-focus":
                case "station-skeleton-apply-pressed":
                case "station-skeleton-invalid":
                case "station-skeleton-input-focus":
                case "station-skeleton-input-disabled":
                case "station-skeleton-applied":
                    var stationStore = new ProjectDocumentStore();
                    var stationProject = initialProject
                        ?? throw new InvalidOperationException("Station skeleton project was not available.");
                    var stationBeforePreview = stationStore.Serialize(stationProject);
                    var stationRuntimeBefore = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.PreviewStationSkeletonCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var stationPreviewPanel = FindVisualDescendant<Border>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "SemiconductorStationSkeletonPreview",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Station skeleton preview was not available.");
                    var stationApplyButton = FindVisualDescendant<Button>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "ApplySemiconductorStationSkeletonButton",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Station skeleton Apply button was not available.");
                    var stationNameTextBox = FindVisualDescendant<TextBox>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "StationSetupNameTextBox",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Station setup name input was not available.");
                    AssertSmoke(
                        stationPreviewPanel.IsVisible
                        && vm.RecipeConnections.StationSkeletonProposedCount == 10
                        && vm.RecipeConnections.StationSkeletonItems.Count == 10
                        && vm.RecipeConnections.StationSkeletonItems.All(item => item.IsProposed),
                        "Ten missing station roles were not previewed.");
                    AssertSmoke(
                        stationApplyButton.IsEnabled
                        && stationBeforePreview == stationStore.Serialize(stationProject)
                        && !vm.IsRunning
                        && vm.IsDesignMode,
                        "Station skeleton preview changed the project or runtime before Apply.");
                    if (connectionWorkbenchState.Equals("station-skeleton-input-focus", StringComparison.OrdinalIgnoreCase))
                    {
                        window.Activate();
                        stationNameTextBox.Focus();
                        Keyboard.Focus(stationNameTextBox);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            stationNameTextBox.IsKeyboardFocused
                            && !string.IsNullOrWhiteSpace(stationNameTextBox.Text),
                            "Station setup name input did not render its value with keyboard focus.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("station-skeleton-input-disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.IsEditable = false;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            !stationNameTextBox.IsEnabled
                            && !stationApplyButton.IsEnabled,
                            "Station setup inputs did not enter their disabled state.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("station-skeleton-invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.AxisTravelText = "-1";
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.HasStationSetupValidationError
                            && !stationApplyButton.IsEnabled
                            && stationBeforePreview == stationStore.Serialize(stationProject),
                            "Invalid station setup did not block Apply without changing the project.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("station-skeleton-preview", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    if (connectionWorkbenchState.Equals("station-skeleton-applied", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.ResetStationSetupCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.AxisTravelText == "320"
                            && stationBeforePreview == stationStore.Serialize(stationProject),
                            "Resetting the station setup changed the project before Apply.");
                        vm.RecipeConnections.StationName = "Lithography Transfer A";
                        vm.RecipeConnections.WaferType = "200 mm Wafer";
                        vm.RecipeConnections.AxisTravelText = "460";
                        vm.RecipeConnections.TransportSpeedText = "175";
                        vm.RecipeConnections.EntrySensorPositionText = "145";
                        vm.RecipeConnections.ProcessSensorPositionText = "510";
                        vm.RecipeConnections.CylinderTravelTimeText = "180";
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            stationApplyButton.IsEnabled
                            && stationBeforePreview == stationStore.Serialize(stationProject),
                            "Valid station setup was not ready to apply without side effects.");
                        vm.RecipeConnections.ApplyStationSkeletonCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            !vm.RecipeConnections.IsStationSkeletonPreviewVisible
                            && vm.RecipeConnections.Rows.Count == 7
                            && vm.RecipeConnections.Rows.All(row => row.IsValid)
                            && stationProject.Layouts.Single().Components.Count == 7
                            && stationProject.Axes.Count == 1
                            && stationProject.Devices.Count == 5
                            && stationProject.Channels.Count == 9
                            && stationProject.Sequences.Single().Steps.Count == 12
                            && stationProject.SemiconductorStationSetup?.StationName == "Lithography Transfer A"
                            && stationProject.SemiconductorStationSetup.WaferType == "200 mm Wafer"
                            && stationProject.SemiconductorStationSetup.AxisTravel == 460
                            && stationProject.SemiconductorStationSetup.TransportSpeed == 175
                            && stationProject.SemiconductorStationSetup.EntrySensorPosition == 145
                            && stationProject.SemiconductorStationSetup.ProcessSensorPosition == 510
                            && stationProject.SemiconductorStationSetup.CylinderTravelTimeMilliseconds == 180,
                            "Station skeleton did not create the connected authored roles.");
                        AssertSmoke(
                            stationRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                            && stationRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                            && !vm.IsRunning
                            && vm.IsDesignMode,
                            "Station skeleton Apply caused an unintended runtime action.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.ReadinessPassed == true,
                            "Applied station skeleton did not pass readiness compilation.");
                        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                        for (var attempt = 0;
                             attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                             attempt++)
                        {
                            await Task.Delay(20);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        AssertSmoke(
                            vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
                            && vm.RecipeConnections.RecipeDryRunTimeline.Count == 12,
                            "Applied station skeleton did not complete its 12-step dry run.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            var stationSavePath = Path.GetFullPath(connectionWorkbenchSavePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(stationSavePath)!);
                            await vm.SaveProjectAsync(stationSavePath);
                            AssertSmoke(
                                await vm.OpenProjectAsync(stationSavePath),
                                "Station skeleton project did not reopen.");
                            AssertSmoke(
                                vm.RecipeConnections.Rows.Count == 7
                                && vm.RecipeConnections.RecipeStepCount == 12
                                && !vm.IsRunning
                                && vm.IsDesignMode,
                                "Reopened station skeleton did not retain its graph safely.");
                        }
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                        {
                            connectionWorkbenchReport = new SmokeDirectSceneAuthoringReport
                            {
                                Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                {
                                    ["ten-missing-roles-previewed"] = true,
                                    ["preview-project-unchanged"] = true,
                                    ["preview-runtime-unchanged"] = true,
                                    ["defaults-reset-without-project-change"] = true,
                                    ["custom-setup-applied"] = true,
                                    ["seven-connected-layout-components"] = true,
                                    ["axis-device-channel-graph-created"] = true,
                                    ["twelve-step-automatic-sequence-created"] = true,
                                    ["apply-runtime-unchanged"] = true,
                                    ["readiness-compilation-passed"] = true,
                                    ["twelve-step-dry-run-completed"] = true,
                                    ["save-reopen-retained-graph"] = true,
                                    ["reopen-stays-stopped-in-design"] = true
                                },
                                Failures = []
                            };
                            connectionWorkbenchReport.Save(connectionWorkbenchReportPath);
                        }
                        break;
                    }

                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    stationApplyButton.BringIntoView();
                    stationApplyButton.UpdateLayout();
                    stationApplyButton.Focus();
                    Keyboard.Focus(stationApplyButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        stationApplyButton.IsKeyboardFocused,
                        "Station skeleton Apply button did not receive focus.");
                    if (connectionWorkbenchState.Equals("station-skeleton-apply-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        MovePointerToCenter(stationApplyButton);
                        Mouse.Capture(stationApplyButton, CaptureMode.SubTree);
                        Mouse.Synchronize();
                        await Task.Delay(200);
                        AssertSmoke(
                            stationApplyButton.IsMouseOver,
                            "Station skeleton Apply button did not enter hover state.");
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        stationApplyButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            stationApplyButton.IsPressed,
                            "Station skeleton Apply button did not enter pointer-down state.");
                    }
                    break;
                case "station-skeleton-reopen":
                    vm.RecipeConnections.PreviewStationSkeletonCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var reopenedStationApplyButton = FindVisualDescendant<Button>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "ApplySemiconductorStationSkeletonButton",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Reopened station Apply button was not available.");
                    AssertSmoke(
                        vm.RecipeConnections.StationSkeletonProposedCount == 0
                        && vm.RecipeConnections.StationSkeletonItems.Count(item => item.IsAlreadyConfigured) == 10
                        && reopenedStationApplyButton.IsEnabled
                        && vm.RecipeConnections.StationName == "Lithography Transfer A"
                        && vm.RecipeConnections.WaferType == "200 mm Wafer"
                        && vm.RecipeConnections.AxisTravelText == "460"
                        && vm.RecipeConnections.TransportSpeedText == "175"
                        && vm.RecipeConnections.EntrySensorPositionText == "145"
                        && vm.RecipeConnections.ProcessSensorPositionText == "510"
                        && vm.RecipeConnections.CylinderTravelTimeText == "180"
                        && !vm.IsRunning
                        && vm.IsDesignMode,
                        "Reopened station skeleton was not recognized as complete and stopped.");
                    break;
                case "checkpoint-coverage":
                    var checkpointCoverageText = FindVisualDescendant<TextBlock>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "RecipeCheckpointCoverageText",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Recipe checkpoint coverage was not available.");
                    AssertSmoke(
                        vm.RecipeConnections.CheckpointStepCount == 5
                        && vm.RecipeConnections.RecipeStepCount == 12,
                        "Recipe checkpoint coverage did not report 5 of 12 steps.");
                    AssertSmoke(
                        checkpointCoverageText.IsVisible
                        && checkpointCoverageText.Text.Contains("5", StringComparison.Ordinal)
                        && checkpointCoverageText.Text.Contains("12", StringComparison.Ordinal),
                        "Recipe checkpoint coverage was not visible before dry run.");
                    AssertSmoke(
                        !vm.RecipeConnections.HasRecipeDryRunResult && !vm.IsRunning,
                        "Checkpoint coverage display caused an unintended run.");
                    break;
                case "checkpoint-template-focus":
                    window.Activate();
                    checkpointTemplateButton.Focus();
                    Keyboard.Focus(checkpointTemplateButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        checkpointTemplateButton.IsKeyboardFocused,
                        "Checkpoint template button did not receive focus.");
                    break;
                case "checkpoint-template-existing":
                    vm.RecipeConnections.PreviewCheckpointTemplateCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var existingTemplateApplyButton = FindVisualDescendant<Button>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "ApplyRecipeCheckpointTemplateButton",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Checkpoint template Apply button was not available.");
                    AssertSmoke(
                        vm.RecipeConnections.IsCheckpointTemplatePreviewVisible
                        && vm.RecipeConnections.CheckpointTemplateProposedCount == 0
                        && vm.RecipeConnections.CheckpointTemplateItems.Count(item =>
                            item.IsAlreadyConfigured) == 5,
                        "Existing representative checkpoints were not recognized.");
                    AssertSmoke(
                        !existingTemplateApplyButton.IsEnabled,
                        "Checkpoint template Apply button was enabled without additions.");
                    break;
                case "checkpoint-template-preview":
                case "checkpoint-template-apply-focus":
                case "checkpoint-template-applied":
                    ClearInitialRecipeCheckpoints();
                    var templateProject = initialProject!;
                    var templateStore = new ProjectDocumentStore();
                    var templateBeforePreview = templateStore.Serialize(templateProject);
                    var templateRuntimeBefore = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.PreviewCheckpointTemplateCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var templatePreviewPanel = FindVisualDescendant<Border>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "RecipeCheckpointTemplatePreview",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Checkpoint template preview panel was not available.");
                    var templateApplyButton = FindVisualDescendant<Button>(
                        workbench,
                        candidate => string.Equals(
                            candidate.Name,
                            "ApplyRecipeCheckpointTemplateButton",
                            StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Checkpoint template Apply button was not available.");
                    AssertSmoke(
                        templatePreviewPanel.IsVisible
                        && vm.RecipeConnections.CheckpointTemplateProposedCount == 5
                        && vm.RecipeConnections.CheckpointTemplateItems.Count == 5
                        && vm.RecipeConnections.CheckpointTemplateItems.All(item => item.IsProposed),
                        "Five representative checkpoint additions were not previewed.");
                    AssertSmoke(
                        templateApplyButton.IsEnabled
                        && templateBeforePreview == templateStore.Serialize(templateProject)
                        && !vm.IsRunning,
                        "Checkpoint preview changed the recipe or runtime before Apply.");
                    if (connectionWorkbenchState.Equals(
                            "checkpoint-template-applied",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        vm.RecipeConnections.ApplyCheckpointTemplateCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            !vm.RecipeConnections.IsCheckpointTemplatePreviewVisible
                            && vm.RecipeConnections.CheckpointStepCount == 5
                            && vm.RecipeConnections.RecipeStepCount == 12,
                            "Checkpoint template did not apply five of twelve steps.");
                        AssertSmoke(
                            templateProject.Sequences.SelectMany(sequence => sequence.Steps).Count(step =>
                                !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
                                && !string.IsNullOrWhiteSpace(step.ExpectedState)) == 5
                            && templateBeforePreview != templateStore.Serialize(templateProject),
                            "Checkpoint template did not update the authored recipe.");
                        AssertSmoke(
                            templateRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                            && templateRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                            && !vm.IsRunning
                            && vm.IsDesignMode
                            && vm.RecipeConnections.ReadinessPassed is null,
                            "Checkpoint template Apply caused an unintended runtime action.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            var templateSavePath = Path.GetFullPath(connectionWorkbenchSavePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(templateSavePath)!);
                            await vm.SaveProjectAsync(templateSavePath);
                            AssertSmoke(
                                await vm.OpenProjectAsync(templateSavePath),
                                "Checkpoint template project did not reopen.");
                            AssertSmoke(
                                vm.RecipeConnections.CheckpointStepCount == 5
                                && !vm.IsRunning
                                && vm.IsDesignMode,
                                "Reopened project did not retain the applied checkpoints safely.");
                        }
                        break;
                    }
                    if (connectionWorkbenchState.Equals(
                            "checkpoint-template-preview",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    for (var attempt = 0; attempt < 10 && !window.IsActive; attempt++)
                    {
                        await Task.Delay(50);
                        window.Activate();
                        SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    }
                    AssertSmoke(window.IsActive, "Machine Studio did not become active for checkpoint Apply pointer testing.");
                    templateApplyButton.BringIntoView();
                    templateApplyButton.UpdateLayout();
                    templateApplyButton.Focus();
                    Keyboard.Focus(templateApplyButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        templateApplyButton.IsKeyboardFocused,
                        "Checkpoint template Apply button did not receive focus.");
                    break;
                case "dry-run":
                    AssertSmoke(!dryRunButton.IsEnabled, "Recipe dry run was enabled before readiness passed.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(dryRunButton.IsEnabled, "Recipe dry run was not enabled after readiness passed.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed,
                        "The isolated recipe dry run did not complete.");
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunTimeline.Count
                            == vm.RecipeConnections.RecipeStepCount
                                - (initialProject?.Devices.Any(device =>
                                    device.InspectionSortRouter is not null) == true ? 3 : 0),
                        "The recipe dry-run timeline was incomplete.");
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunTimeline.Count(step => step.HasCheckpoint)
                            == vm.RecipeConnections.CheckpointStepCount
                        && !vm.RecipeConnections.RecipeDryRunTimeline.Any(step =>
                            step.HasCheckpointMismatch),
                        "The authored recipe checkpoints did not all pass.");
                    AssertSmoke(
                        FindVisualDescendant<Border>(workbench, candidate =>
                            string.Equals(candidate.Name, "RecipeDryRunResult", StringComparison.Ordinal)
                            && candidate.IsVisible) is not null,
                        "The recipe dry-run result panel was not visible.");
                    if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.LoadLocks is [var finalLoadLock])
                    {
                        var loadLockPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                            state.IsLoadLock);
                        AssertSmoke(
                            finalLoadLock.State == LoadLockState.Atmosphere
                            && !finalLoadLock.IsVacuumReady
                            && finalLoadLock.IsAtmosphereReady
                            && finalLoadLock.IsOuterDoorPermitted
                            && !finalLoadLock.IsInnerDoorPermitted
                            && !loadLockPresentation.IsFault
                            && loadLockPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.LoadLockState.Atmosphere"),
                                StringComparison.CurrentCulture)
                            && loadLockPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.LoadLockDoorAllowed"),
                                StringComparison.CurrentCulture)
                            && loadLockPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.LoadLockDoorBlocked"),
                                StringComparison.CurrentCulture),
                            "The normal dry-run result did not expose load-lock pressure readiness and door permissions.");
                    }
                    if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.WaferHandlers is [var finalHandler])
                    {
                        var handlerPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                            state.IsWaferHandler);
                        AssertSmoke(
                            finalHandler.State == WaferHandlerOwnershipState.Destination
                            && vm.RecipeConnections.RecipeDryRunResult!.FinalSnapshot.LayoutComponents.Single(component =>
                                component.Id == finalHandler.WorkpieceComponentId).TransferOwnershipState
                                == WaferHandlerOwnershipState.Destination
                            && !handlerPresentation.IsFault
                            && handlerPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.WaferHandlerState.Destination"),
                                StringComparison.CurrentCulture),
                            "The normal dry-run result did not expose destination wafer ownership.");
                    }
                    if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.InspectionSortRouters is [var finalSorter])
                    {
                        var sorterPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                            state.IsInspectionSorter);
                        AssertSmoke(
                            finalSorter.State == InspectionSortRouteState.NgRouted
                            && finalSorter.Decision == PlaceholderInspectionDecision.Fail
                            && !sorterPresentation.IsFault
                            && sorterPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.InspectionSortState.NgRouted"),
                                StringComparison.CurrentCulture),
                            "The normal dry-run result did not expose the NG inspection route.");
                    }
                    if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.InspectionHandoffs is [var finalInspectionHandoff])
                    {
                        var inspectionPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                            state.IsInspectionHandoff);
                        AssertSmoke(
                            finalInspectionHandoff.State == InspectionHandoffState.Complete
                            && finalInspectionHandoff.Decision == PlaceholderInspectionDecision.Pass
                            && finalInspectionHandoff.IsMaterialPresent
                            && !inspectionPresentation.IsFault
                            && inspectionPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.InspectionHandoffState.Complete"),
                                StringComparison.CurrentCulture),
                            "The normal dry-run result did not expose the completed inspection handoff.");
                    }
                    if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.OhtHandoffs is [var finalHandoff])
                    {
                        var handoffPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                            state.IsOhtHandoff);
                        AssertSmoke(
                            finalHandoff.State == OhtHandoffOwnershipState.LoadPort
                            && finalHandoff.IsCarrierReceived
                            && !handoffPresentation.IsFault
                            && handoffPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.OhtHandoffState.LoadPort"),
                                StringComparison.CurrentCulture),
                            "The normal dry-run result did not expose load-port carrier ownership.");
                    }
                    if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.Prealigners is [var finalPrealigner])
                    {
                        var prealignerPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                            state.IsPrealigner);
                        AssertSmoke(
                            finalPrealigner.State == PrealignerState.Released
                            && finalPrealigner.IsAlignmentComplete
                            && Math.Abs(finalPrealigner.RotaryPositionDegrees - 180) <= finalPrealigner.AlignmentToleranceDegrees
                            && !prealignerPresentation.IsFault
                            && prealignerPresentation.Text.Contains(
                                OpenVisionLanguageService.T("Connections.PrealignerState.Released"),
                                StringComparison.CurrentCulture),
                            "The normal dry-run result did not expose completed pre-alignment and release.");
                    }
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath)
                        && initialProject?.Devices.Any(device => device.WaferHandler is not null) == true)
                    {
                        await vm.SaveProjectAsync(connectionWorkbenchSavePath);
                        AssertSmoke(
                            await vm.OpenProjectAsync(connectionWorkbenchSavePath),
                            "The saved wafer-handler recipe could not be reopened.");
                        var reopened = await new ProjectDocumentStore().LoadAsync(connectionWorkbenchSavePath);
                        var reopenedHandler = reopened.Devices.Single(device =>
                            device.Kind == DeviceKind.Handler && device.WaferHandler is not null);
                        AssertSmoke(
                            reopened.Schema == MachineProjectDocument.CurrentSchema
                            && reopenedHandler.WaferHandler!.HorizontalAxisId == "axis.robot-reach"
                            && reopenedHandler.WaferHandler.VerticalAxisId == "axis.process"
                            && reopenedHandler.WaferHandler.WorkpieceComponentId == "wafer",
                            "Save/reopen did not preserve the typed wafer-handler contract.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                            "The reopened wafer-handler recipe did not restore simulation readiness.");
                    }
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath)
                        && initialProject?.Devices.Any(device => device.InspectionSortRouter is not null) == true)
                    {
                        await vm.SaveProjectAsync(connectionWorkbenchSavePath);
                        AssertSmoke(
                            await vm.OpenProjectAsync(connectionWorkbenchSavePath),
                            "The saved inspection-sorter recipe could not be reopened.");
                        var reopened = await new ProjectDocumentStore().LoadAsync(connectionWorkbenchSavePath);
                        var reopenedSorter = reopened.Devices.Single(device =>
                            device.Kind == DeviceKind.Sorter && device.InspectionSortRouter is not null);
                        AssertSmoke(
                            reopened.Schema == MachineProjectDocument.CurrentSchema
                            && reopenedSorter.InspectionSortRouter!.CameraId == "camera.metrology"
                            && reopenedSorter.InspectionSortRouter.PassConveyorComponentId == "transport"
                            && reopenedSorter.InspectionSortRouter.NgConveyorComponentId == "sort-transport",
                            "Save/reopen did not preserve the typed inspection-sorter contract.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                            "The reopened inspection-sorter recipe did not restore simulation readiness.");
                    }
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath)
                        && initialProject?.Devices.Any(device => device.InspectionHandoff is not null) == true)
                    {
                        await vm.SaveProjectAsync(connectionWorkbenchSavePath);
                        AssertSmoke(
                            await vm.OpenProjectAsync(connectionWorkbenchSavePath),
                            "The saved inspection-handoff recipe could not be reopened.");
                        var reopened = await new ProjectDocumentStore().LoadAsync(connectionWorkbenchSavePath);
                        var reopenedHandoff = reopened.Devices.Single(device =>
                            device.Kind == DeviceKind.Inspection && device.InspectionHandoff is not null);
                        AssertSmoke(
                            reopened.Schema == MachineProjectDocument.CurrentSchema
                            && reopenedHandoff.InspectionHandoff!.CameraId == "camera.ocr"
                            && reopenedHandoff.InspectionHandoff.InspectionPositionSensorChannelId == "di.sensor-process"
                            && reopenedHandoff.InspectionHandoff.ResultAcceptedCommandChannelId == "do.inspection-result-accepted",
                            "Save/reopen did not preserve the typed inspection-handoff contract.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null)
                            && !vm.IsRunning
                            && vm.IsDesignMode
                            && vm.SceneSnapshots.Latest?.TickIndex == 0,
                            "The reopened inspection-handoff recipe did not restore stopped simulation readiness.");
                    }
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath)
                        && initialProject?.Devices.Any(device => device.OhtHandoff is not null) == true)
                    {
                        await vm.SaveProjectAsync(connectionWorkbenchSavePath);
                        AssertSmoke(
                            await vm.OpenProjectAsync(connectionWorkbenchSavePath),
                            "The saved OHT handoff recipe could not be reopened.");
                        var reopened = await new ProjectDocumentStore().LoadAsync(connectionWorkbenchSavePath);
                        var reopenedHandoff = reopened.Devices.Single(device =>
                            device.Kind == DeviceKind.Oht && device.OhtHandoff is not null);
                        AssertSmoke(
                            reopened.Schema == MachineProjectDocument.CurrentSchema
                            && reopenedHandoff.OhtHandoff!.TransportConveyorComponentId == "transport"
                            && reopenedHandoff.OhtHandoff.LoadPortReadySensorChannelId == "di.cylinder.extended"
                            && reopenedHandoff.OhtHandoff.CarrierReceivedSensorChannelId == "di.sensor-process",
                            "Save/reopen did not preserve the typed OHT handoff contract.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null)
                            && !vm.IsRunning,
                            "The reopened OHT handoff recipe did not restore readiness safely.");
                    }
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath)
                        && initialProject?.Devices.Any(device => device.Prealigner is not null) == true)
                    {
                        await vm.SaveProjectAsync(connectionWorkbenchSavePath);
                        AssertSmoke(
                            await vm.OpenProjectAsync(connectionWorkbenchSavePath),
                            "The saved pre-aligner recipe could not be reopened.");
                        var reopened = await new ProjectDocumentStore().LoadAsync(connectionWorkbenchSavePath);
                        var reopenedPrealigner = reopened.Devices.Single(device =>
                            device.Kind == DeviceKind.Prealigner && device.Prealigner is not null);
                        AssertSmoke(
                            reopened.Schema == MachineProjectDocument.CurrentSchema
                            && reopenedPrealigner.Prealigner!.RotaryStageComponentId == "alignment-table"
                            && reopenedPrealigner.Prealigner.ClampCylinderComponentId == "process-cylinder"
                            && reopenedPrealigner.Prealigner.AlignmentTargetDegrees == 180
                            && reopenedPrealigner.Prealigner.AlignmentToleranceDegrees == 0.1,
                            "Save/reopen did not preserve the typed pre-aligner contract.");
                        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                        AssertSmoke(
                            vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null)
                            && !vm.IsRunning
                            && vm.IsDesignMode
                            && vm.SceneSnapshots.Latest?.TickIndex == 0,
                            "The reopened pre-aligner recipe did not restore stopped simulation readiness.");
                    }
                    break;
                case "dry-run-fault":
                    var faultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the recipe dry-run fault state.");
                    var faultSequence = faultProject.Sequences.FirstOrDefault()
                        ?? throw new InvalidOperationException("A sequence is required for the recipe dry-run fault state.");
                    var faultStep = faultSequence.Steps.FirstOrDefault(step =>
                        string.Equals(step.Id, "wait-process-position", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("The process-position wait step was not available.");
                    faultStep.TimeoutMs = 20;
                    vm.RecipeConnections.Load(faultProject);
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted,
                        "The recipe dry-run fault state did not fault.");
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.FirstIssue?.StepId == faultStep.Id
                        && vm.RecipeConnections.RecipeDryRunTimeline.Any(trace => trace.HasIssue),
                        "The recipe dry-run fault state did not identify its first issue.");
                    AssertSmoke(
                        vm.RecipeConnections.SelectedRecipeDryRunStep is { HasIssue: true }
                        && vm.RecipeConnections.SelectedRow?.ComponentId
                            == vm.RecipeConnections.SelectedRecipeDryRunStep.ComponentId
                        && vm.Layout.SelectedItem?.Id
                            == vm.RecipeConnections.SelectedRecipeDryRunStep.ComponentId,
                        "The recipe dry-run fault state did not select its issue and connected equipment.");
                    break;
                case "dry-run-checkpoint-mismatch":
                case "dry-run-checkpoint-playback":
                    var checkpointProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the checkpoint smoke state.");
                    var checkpointStep = checkpointProject.Sequences.First().Steps.Single(step =>
                        string.Equals(step.Id, "wait-cylinder-extended", StringComparison.Ordinal));
                    checkpointStep.ExpectedTargetId = "process-cylinder";
                    checkpointStep.ExpectedState = "Retracted";
                    vm.SequenceEditor.Load(checkpointProject);
                    vm.RecipeConnections.Load(checkpointProject);
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the checkpoint state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome
                            == RecipeDryRunOutcome.CompletedWithMismatch,
                        "The expected-state mismatch did not produce its distinct outcome.");
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.FirstCheckpointMismatch?.StepId
                            == checkpointStep.Id
                        && vm.RecipeConnections.SelectedRecipeDryRunStep is
                            { HasCheckpointMismatch: true },
                        "The first expected-state mismatch was not selected.");
                    AssertSmoke(
                        vm.RecipeConnections.SelectedRow?.ComponentId == "process-cylinder"
                        && vm.Layout.SelectedItem?.Id == "process-cylinder",
                        "The mismatch did not select its connected cylinder.");
                    if (connectionWorkbenchState.Equals(
                            "dry-run-checkpoint-playback",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var mismatchStep = vm.RecipeConnections.SelectedRecipeDryRunStep!;
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(mismatchStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.IsDryRunPlaybackActive
                            && vm.HasDryRunPlaybackMismatch
                            && vm.DryRunPlaybackCheckpointText.Contains("Retracted", StringComparison.Ordinal)
                            && vm.DryRunPlaybackCheckpointText.Contains("Extended", StringComparison.Ordinal),
                            "Checkpoint mismatch detail was not visible in layout playback.");
                    }
                    break;
                case "dry-run-load-lock-fault":
                case "dry-run-load-lock-fault-playback":
                    var loadLockFaultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the load-lock fault state.");
                    var loadLockFaultSequence = loadLockFaultProject.Sequences.FirstOrDefault()
                        ?? throw new InvalidOperationException("A sequence is required for the load-lock fault state.");
                    var requestOuterDoorStep = loadLockFaultSequence.Steps.Single(step =>
                        string.Equals(step.Id, "extend-outer-door", StringComparison.Ordinal));
                    var requestOuterDoorIndex = loadLockFaultSequence.Steps.IndexOf(requestOuterDoorStep);
                    const string conflictStepId = "request-inner-door-conflict";
                    loadLockFaultSequence.Steps.Insert(requestOuterDoorIndex + 1, new SequenceStepDefinition
                    {
                        Id = conflictStepId,
                        Name = "Request Both Load Lock Doors",
                        Action = SequenceStepAction.SetSignal,
                        TargetId = "do.cylinder.extend",
                        Parameter = "true",
                        NextStepId = requestOuterDoorStep.NextStepId
                    });
                    requestOuterDoorStep.NextStepId = conflictStepId;
                    vm.SequenceEditor.Load(loadLockFaultProject);
                    vm.RecipeConnections.Load(loadLockFaultProject);
                    var loadLockFaultStore = new ProjectDocumentStore();
                    var loadLockFaultProjectBeforeRun = loadLockFaultStore.Serialize(loadLockFaultProject);
                    var loadLockFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the load-lock fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var loadLockFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.LoadLocks.Single();
                    var loadLockFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsLoadLock);
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                        && loadLockFaultSnapshot is
                        {
                            State: LoadLockState.InterlockFault,
                            IsVacuumReady: false,
                            IsAtmosphereReady: false,
                            IsOuterDoorPermitted: false,
                            IsInnerDoorPermitted: false
                        }
                        && loadLockFaultPresentation.IsFault
                        && loadLockFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockState.InterlockFault"),
                            StringComparison.CurrentCulture)
                        && loadLockFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockDoorBlocked"),
                            StringComparison.CurrentCulture),
                        "The induced load-lock interlock fault was not exposed with blocked door permissions.");
                    AssertSmoke(
                        loadLockFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && loadLockFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && loadLockFaultProjectBeforeRun == loadLockFaultStore.Serialize(loadLockFaultProject),
                        "The isolated load-lock fault dry run changed the main runtime or project.");
                    if (connectionWorkbenchState.Equals(
                            "dry-run-load-lock-fault-playback",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var conflictTimelineStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                            step.BoundarySnapshot.LoadLocks.Any(loadLock =>
                                loadLock.State == LoadLockState.InterlockFault));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(conflictTimelineStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.IsDryRunPlaybackActive
                            && vm.IsDryRunPlaybackLoadLockFault
                            && vm.DryRunPlaybackLoadLockText.Contains(
                                OpenVisionLanguageService.T("Connections.LoadLockState.InterlockFault"),
                                StringComparison.CurrentCulture)
                            && vm.DryRunPlaybackLoadLockText.Contains(
                                OpenVisionLanguageService.T("Connections.LoadLockDoorBlocked"),
                                StringComparison.CurrentCulture),
                            "The immutable playback overlay did not expose the load-lock interlock fault.");
                    }
                    break;
                case "dry-run-oht-fault-playback":
                    var ohtFaultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the OHT handoff fault state.");
                    ohtFaultProject.Channels.Single(channel =>
                        string.Equals(channel.Id, "di.oht.route-available", StringComparison.Ordinal)).InitialValue = 0;
                    ohtFaultProject.Sequences.Single().Steps.Single(step =>
                        string.Equals(step.Id, "wait-oht-handoff-ready", StringComparison.Ordinal)).TargetId =
                        "di.cylinder.extended";
                    vm.SequenceEditor.Load(ohtFaultProject);
                    vm.RecipeConnections.Load(ohtFaultProject);
                    var ohtFaultStore = new ProjectDocumentStore();
                    var ohtFaultProjectBeforeRun = ohtFaultStore.Serialize(ohtFaultProject);
                    var ohtFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the OHT handoff fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var ohtFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.OhtHandoffs.Single();
                    var ohtFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsOhtHandoff);
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                        && ohtFaultSnapshot?.State == OhtHandoffOwnershipState.InterlockFault
                        && !ohtFaultSnapshot.IsTransferPermitted
                        && ohtFaultPresentation.IsFault
                        && ohtFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.OhtHandoffState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The premature OHT transfer did not expose a fail-closed interlock fault.");
                    AssertSmoke(
                        ohtFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && ohtFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && ohtFaultProjectBeforeRun == ohtFaultStore.Serialize(ohtFaultProject),
                        "The isolated OHT handoff fault dry run changed the main runtime or project.");
                    var ohtConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        step.BoundarySnapshot.OhtHandoffs.Any(handoff =>
                            handoff.State == OhtHandoffOwnershipState.InterlockFault));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(ohtConflictStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.IsDryRunPlaybackOhtHandoffFault
                        && vm.DryRunPlaybackOhtHandoffText.Contains(
                            OpenVisionLanguageService.T("Connections.OhtHandoffState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the OHT handoff interlock fault.");
                    break;
                case "dry-run-wafer-handler-fault":
                case "dry-run-wafer-handler-fault-playback":
                    var handlerFaultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the wafer-handler fault state.");
                    var handlerFaultSequence = handlerFaultProject.Sequences.FirstOrDefault()
                        ?? throw new InvalidOperationException("A sequence is required for the wafer-handler fault state.");
                    var pickStep = handlerFaultSequence.Steps.Single(step =>
                        string.Equals(step.Id, "pick-wafer", StringComparison.Ordinal));
                    var pickIndex = handlerFaultSequence.Steps.IndexOf(pickStep);
                    const string unsafePlaceStepId = "unsafe-place-before-pick";
                    handlerFaultSequence.Steps.Insert(pickIndex, new SequenceStepDefinition
                    {
                        Id = unsafePlaceStepId,
                        Name = "Unsafe Place Before Pick",
                        Action = SequenceStepAction.SetSignal,
                        TargetId = "do.handler.place",
                        Parameter = "true",
                        NextStepId = pickStep.Id
                    });
                    handlerFaultSequence.Steps.Single(step =>
                        string.Equals(step.NextStepId, pickStep.Id, StringComparison.Ordinal)
                        && !string.Equals(step.Id, unsafePlaceStepId, StringComparison.Ordinal)).NextStepId = unsafePlaceStepId;
                    vm.SequenceEditor.Load(handlerFaultProject);
                    vm.RecipeConnections.Load(handlerFaultProject);
                    var handlerFaultStore = new ProjectDocumentStore();
                    var handlerFaultProjectBeforeRun = handlerFaultStore.Serialize(handlerFaultProject);
                    var handlerFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the wafer-handler fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var handlerFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.WaferHandlers.Single();
                    var handlerFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsWaferHandler);
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                        && handlerFaultSnapshot?.State == WaferHandlerOwnershipState.InterlockFault
                        && handlerFaultPresentation.IsFault
                        && handlerFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.WaferHandlerState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The unsafe wafer place did not expose a fail-closed interlock fault.");
                    AssertSmoke(
                        handlerFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && handlerFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && handlerFaultProjectBeforeRun == handlerFaultStore.Serialize(handlerFaultProject),
                        "The isolated wafer-handler fault dry run changed the main runtime or project.");
                    if (connectionWorkbenchState.Equals(
                            "dry-run-wafer-handler-fault-playback",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var handlerConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                            step.BoundarySnapshot.WaferHandlers.Any(handler =>
                                handler.State == WaferHandlerOwnershipState.InterlockFault));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(handlerConflictStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.IsDryRunPlaybackActive
                            && vm.IsDryRunPlaybackWaferHandlerFault
                            && handlerConflictStep.BoundarySnapshot.LayoutComponents.Single(component =>
                                component.Id == handlerConflictStep.BoundarySnapshot.WaferHandlers.Single().WorkpieceComponentId)
                                .TransferOwnershipState == WaferHandlerOwnershipState.InterlockFault
                            && vm.DryRunPlaybackWaferHandlerText.Contains(
                                OpenVisionLanguageService.T("Connections.WaferHandlerState.InterlockFault"),
                                StringComparison.CurrentCulture),
                            "The immutable playback overlay did not expose the wafer-handler interlock fault.");
                        var transferViewport = FindVisualDescendant<MachineSceneViewport>(window)
                            ?? throw new InvalidOperationException("Machine scene viewport was not found.");
                        AssertSmoke(
                            transferViewport.LastRenderedTransferOwnershipState
                                == WaferHandlerOwnershipState.InterlockFault
                            && transferViewport.LastRenderedTransferOwnershipText == "FAULT",
                            "The linked workpiece did not render fail-closed ownership.");
                    }
                    break;
                case "dry-run-inspection-sort-pass-playback":
                    var passSortProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the inspection-sort PASS state.");
                    passSortProject.Devices.Single(device => device.Id == "camera.metrology")
                        .Camera!.PlaceholderDecision = PlaceholderInspectionDecision.Pass;
                    vm.SequenceEditor.Load(passSortProject);
                    vm.RecipeConnections.Load(passSortProject);
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the inspection-sort PASS state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var passSorter = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?
                        .InspectionSortRouters.Single();
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
                        && passSorter?.State == InspectionSortRouteState.PassRouted
                        && passSorter.Decision == PlaceholderInspectionDecision.Pass,
                        "The PASS decision did not select only the PASS route.");
                    var passRoutedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                        string.Equals(step.StepId, "wait-pass-routed", StringComparison.Ordinal));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(passRoutedStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.HasDryRunPlaybackInspectionSorter
                        && !vm.IsDryRunPlaybackInspectionSorterFault
                        && vm.DryRunPlaybackInspectionSorterText.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionSortState.PassRouted"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the PASS route.");
                    break;
                case "dry-run-inspection-sort-fault-playback":
                    var sortFaultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the inspection-sort fault state.");
                    sortFaultProject.Sequences.Single().Steps.Single(step =>
                        string.Equals(step.Id, "wait-metrology-result", StringComparison.Ordinal))
                        .FailureStepId = "start-pass-transport";
                    vm.SequenceEditor.Load(sortFaultProject);
                    vm.RecipeConnections.Load(sortFaultProject);
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the inspection-sort fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var sortFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?
                        .InspectionSortRouters.Single();
                    var sortFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsInspectionSorter);
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                        && sortFaultSnapshot?.State == InspectionSortRouteState.InterlockFault
                        && sortFaultPresentation.IsFault
                        && sortFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionSortState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The wrong inspection route did not expose a fail-closed interlock fault.");
                    var sortConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        step.BoundarySnapshot.InspectionSortRouters.Any(sorter =>
                            sorter.State == InspectionSortRouteState.InterlockFault));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(sortConflictStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.IsDryRunPlaybackInspectionSorterFault
                        && vm.DryRunPlaybackInspectionSorterText.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionSortState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the inspection-sort interlock fault.");
                    break;
                case "dry-run-inspection-handoff-fault-playback":
                    var inspectionFaultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the inspection-handoff fault state.");
                    SequenceDefinition inspectionFaultSequence = inspectionFaultProject.Sequences.Single();
                    SequenceStepDefinition waitFocus = inspectionFaultSequence.Steps.Single(step =>
                        string.Equals(step.Id, "wait-ocr-focus", StringComparison.Ordinal));
                    const string prematureAcceptanceStepId = "smoke-premature-inspection-accept";
                    inspectionFaultSequence.Steps.Insert(
                        inspectionFaultSequence.Steps.IndexOf(waitFocus) + 1,
                        new SequenceStepDefinition
                        {
                            Id = prematureAcceptanceStepId,
                            Name = "Smoke Premature Inspection Acceptance",
                            Action = SequenceStepAction.SetSignal,
                            TargetId = "do.inspection-result-accepted",
                            Parameter = "true",
                            NextStepId = waitFocus.NextStepId
                        });
                    waitFocus.NextStepId = prematureAcceptanceStepId;
                    vm.SequenceEditor.Load(inspectionFaultProject);
                    vm.RecipeConnections.Load(inspectionFaultProject);
                    var inspectionFaultStore = new ProjectDocumentStore();
                    string inspectionFaultBeforeRun = inspectionFaultStore.Serialize(inspectionFaultProject);
                    SimulationSnapshot? inspectionFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the inspection-handoff fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    InspectionHandoffSnapshot? inspectionFaultSnapshot = vm.RecipeConnections
                        .RecipeDryRunResult?.FinalSnapshot?.InspectionHandoffs.Single();
                    var inspectionFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsInspectionHandoff);
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                        && inspectionFaultSnapshot?.State == InspectionHandoffState.InterlockFault
                        && !inspectionFaultSnapshot.IsInspectionReady
                        && !inspectionFaultSnapshot.IsInspectionComplete
                        && inspectionFaultPresentation.IsFault
                        && inspectionFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionHandoffState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "Premature result acceptance did not expose a fail-closed inspection-handoff fault.");
                    AssertSmoke(
                        inspectionFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && inspectionFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && inspectionFaultBeforeRun == inspectionFaultStore.Serialize(inspectionFaultProject),
                        "The isolated inspection-handoff fault dry run changed the main runtime or project.");
                    var inspectionConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        step.BoundarySnapshot.InspectionHandoffs.Any(handoff =>
                            handoff.State == InspectionHandoffState.InterlockFault));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(inspectionConflictStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.IsDryRunPlaybackInspectionHandoffFault
                        && vm.DryRunPlaybackInspectionHandoffText.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionHandoffState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the inspection-handoff interlock fault.");
                    break;
                case "dry-run-prealigner-fault-playback":
                    var prealignerFaultProject = initialProject
                        ?? throw new InvalidOperationException("A project is required for the pre-aligner fault state.");
                    SequenceDefinition prealignerFaultSequence = prealignerFaultProject.Sequences.Single();
                    SequenceStepDefinition waitAlignmentReady = prealignerFaultSequence.Steps.Single(step =>
                        string.Equals(step.Id, "wait-alignment-ready", StringComparison.Ordinal));
                    const string prematureAlignmentAcceptanceStepId = "smoke-premature-alignment-accept";
                    prealignerFaultSequence.Steps.Insert(
                        prealignerFaultSequence.Steps.IndexOf(waitAlignmentReady),
                        new SequenceStepDefinition
                        {
                            Id = prematureAlignmentAcceptanceStepId,
                            Name = "Smoke Premature Alignment Acceptance",
                            Action = SequenceStepAction.SetSignal,
                            TargetId = "do.alignment-accepted",
                            Parameter = "true",
                            NextStepId = waitAlignmentReady.Id
                        });
                    SequenceStepDefinition waitClampExtended = prealignerFaultSequence.Steps.Single(step =>
                        string.Equals(step.Id, "wait-cylinder-extended", StringComparison.Ordinal));
                    waitClampExtended.NextStepId = prematureAlignmentAcceptanceStepId;
                    vm.SequenceEditor.Load(prealignerFaultProject);
                    vm.RecipeConnections.Load(prealignerFaultProject);
                    var prealignerFaultStore = new ProjectDocumentStore();
                    string prealignerFaultBeforeRun = prealignerFaultStore.Serialize(prealignerFaultProject);
                    SimulationSnapshot? prealignerFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "Recipe dry run was not enabled for the pre-aligner fault state.");
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    PrealignerSnapshot? prealignerFaultSnapshot = vm.RecipeConnections
                        .RecipeDryRunResult?.FinalSnapshot?.Prealigners.Single();
                    var prealignerFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsPrealigner);
                    AssertSmoke(
                        vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                        && prealignerFaultSnapshot?.State == PrealignerState.InterlockFault
                        && !prealignerFaultSnapshot.IsAlignmentReady
                        && !prealignerFaultSnapshot.IsAlignmentComplete
                        && prealignerFaultPresentation.IsFault
                        && prealignerFaultPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.PrealignerState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "Premature alignment acceptance did not expose a fail-closed pre-aligner fault.");
                    AssertSmoke(
                        prealignerFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && prealignerFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && prealignerFaultBeforeRun == prealignerFaultStore.Serialize(prealignerFaultProject),
                        "The isolated pre-aligner fault dry run changed the main runtime or project.");
                    var prealignerConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        step.BoundarySnapshot.Prealigners.Any(prealigner =>
                            prealigner.State == PrealignerState.InterlockFault));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(prealignerConflictStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.IsDryRunPlaybackPrealignerFault
                        && vm.DryRunPlaybackPrealignerText.Contains(
                            OpenVisionLanguageService.T("Connections.PrealignerState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the pre-aligner interlock fault.");
                    break;
                case "dry-run-open-step":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var openTimelineStep = vm.RecipeConnections.RecipeDryRunTimeline.FirstOrDefault(step =>
                        string.Equals(step.StepId, "wait-process-position", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("The dry-run navigation step was not available.");
                    vm.RecipeConnections.OpenRecipeDryRunStepCommand.Execute(openTimelineStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.SelectedDocumentTabIndex == 2
                        && vm.SequenceEditor.SelectedSequence?.Id == openTimelineStep.SequenceId
                        && vm.SequenceEditor.SelectedStep?.Id == openTimelineStep.StepId,
                        "The dry-run timeline did not open the exact Sequence step.");
                    AssertSmoke(
                        vm.RecipeConnections.SelectedRow?.ComponentId == openTimelineStep.ComponentId
                        && vm.Layout.SelectedItem?.Id == openTimelineStep.ComponentId,
                        "The dry-run timeline did not retain the connected equipment selection.");
                    break;
                case "dry-run-playback":
                case "dry-run-playback-first":
                case "dry-run-playback-last":
                case "dry-run-playback-control-focus":
                case "dry-run-playback-control-hover":
                case "dry-run-playback-control-pressed":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var playbackStep = connectionWorkbenchState.Equals(
                        "dry-run-playback-first",
                        StringComparison.OrdinalIgnoreCase)
                        ? vm.RecipeConnections.RecipeDryRunTimeline.First()
                        : connectionWorkbenchState.Equals(
                            "dry-run-playback-last",
                            StringComparison.OrdinalIgnoreCase)
                            ? vm.RecipeConnections.RecipeDryRunTimeline.Last()
                            : vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                                string.Equals(step.StepId, "wait-cylinder-extended", StringComparison.Ordinal));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.SelectedDocumentTabIndex == 0
                        && !vm.IsSceneEditable
                        && ReferenceEquals(vm.SceneSnapshotSource.Latest, playbackStep.BoundarySnapshot),
                        "The selected dry-run boundary was not shown read-only on Machine Layout.");
                    if (connectionWorkbenchState.Equals("dry-run-playback-first", StringComparison.OrdinalIgnoreCase))
                    {
                        AssertSmoke(
                            !vm.PreviousDryRunPlaybackStepCommand.CanExecute(null),
                            "Previous remained enabled at the first dry-run boundary.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("dry-run-playback-last", StringComparison.OrdinalIgnoreCase))
                    {
                        AssertSmoke(
                            !vm.NextDryRunPlaybackStepCommand.CanExecute(null),
                            "Next remained enabled at the last dry-run boundary.");
                        break;
                    }
                    if (connectionWorkbenchState.Equals("dry-run-playback", StringComparison.OrdinalIgnoreCase))
                    {
                        if (playbackStep.BoundarySnapshot.LoadLocks is [var vacuumLoadLock])
                        {
                            var vacuumText = vm.DryRunPlaybackLoadLockText;
                            AssertSmoke(
                                vm.HasDryRunPlaybackLoadLock
                                && !vm.IsDryRunPlaybackLoadLockFault
                                && vacuumLoadLock.State == LoadLockState.Vacuum
                                && vacuumLoadLock.IsVacuumReady
                                && !vacuumLoadLock.IsAtmosphereReady
                                && !vacuumLoadLock.IsOuterDoorPermitted
                                && vacuumLoadLock.IsInnerDoorPermitted
                                && vacuumText.Contains(
                                    OpenVisionLanguageService.T("Connections.LoadLockState.Vacuum"),
                                    StringComparison.CurrentCulture),
                                "The vacuum playback boundary did not expose load-lock readiness and door permissions.");
                            var atmosphereStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-atmosphere-ready", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(atmosphereStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            var atmosphereLoadLock = atmosphereStep.BoundarySnapshot.LoadLocks.Single();
                            AssertSmoke(
                                atmosphereLoadLock.State == LoadLockState.Atmosphere
                                && !atmosphereLoadLock.IsVacuumReady
                                && atmosphereLoadLock.IsAtmosphereReady
                                && atmosphereLoadLock.IsOuterDoorPermitted
                                && !atmosphereLoadLock.IsInnerDoorPermitted
                                && !string.Equals(
                                    vacuumText,
                                    vm.DryRunPlaybackLoadLockText,
                                    StringComparison.CurrentCulture),
                                "Load-lock playback did not change from the vacuum boundary to the atmosphere boundary.");
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        if (playbackStep.BoundarySnapshot.OhtHandoffs.Count == 1)
                        {
                            AssertSmoke(
                                vm.HasDryRunPlaybackOhtHandoff
                                && !vm.IsDryRunPlaybackOhtHandoffFault
                                && playbackStep.BoundarySnapshot.OhtHandoffs.Single().State
                                    == OhtHandoffOwnershipState.Ready
                                && vm.DryRunPlaybackOhtHandoffText.Contains(
                                    OpenVisionLanguageService.T("Connections.OhtHandoffState.Ready"),
                                    StringComparison.CurrentCulture),
                                "OHT playback did not expose ready vehicle-to-load-port ownership.");
                            var receivedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-oht-carrier-transferred", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(receivedStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                receivedStep.BoundarySnapshot.OhtHandoffs.Single().State
                                    == OhtHandoffOwnershipState.LoadPort
                                && vm.DryRunPlaybackOhtHandoffText.Contains(
                                    OpenVisionLanguageService.T("Connections.OhtHandoffState.LoadPort"),
                                    StringComparison.CurrentCulture),
                                "OHT playback did not expose load-port ownership after receipt.");
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        if (playbackStep.BoundarySnapshot.WaferHandlers.Count == 1)
                        {
                            var holdingStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-handler-holding", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(holdingStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.HasDryRunPlaybackWaferHandler
                                && !vm.IsDryRunPlaybackWaferHandlerFault
                                && holdingStep.BoundarySnapshot.WaferHandlers.Single().State
                                    == WaferHandlerOwnershipState.Handler
                                && holdingStep.BoundarySnapshot.LayoutComponents.Single(component =>
                                    component.Id == holdingStep.BoundarySnapshot.WaferHandlers.Single().WorkpieceComponentId)
                                    .TransferOwnershipState == WaferHandlerOwnershipState.Handler
                                && vm.DryRunPlaybackWaferHandlerText.Contains(
                                    OpenVisionLanguageService.T("Connections.WaferHandlerState.Handler"),
                                    StringComparison.CurrentCulture),
                                "Wafer-handler playback did not expose handler ownership after pick.");
                            var transferViewport = FindVisualDescendant<MachineSceneViewport>(window)
                                ?? throw new InvalidOperationException("Machine scene viewport was not found.");
                            AssertSmoke(
                                transferViewport.LastRenderedTransferOwnershipState
                                    == WaferHandlerOwnershipState.Handler
                                && transferViewport.LastRenderedTransferOwnershipText == "HANDLER",
                                "The linked workpiece did not render handler ownership.");
                            var placedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-handler-placed", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(placedStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                placedStep.BoundarySnapshot.WaferHandlers.Single().State
                                    == WaferHandlerOwnershipState.Destination
                                && placedStep.BoundarySnapshot.LayoutComponents.Single(component =>
                                    component.Id == placedStep.BoundarySnapshot.WaferHandlers.Single().WorkpieceComponentId)
                                    .TransferOwnershipState == WaferHandlerOwnershipState.Destination
                                && vm.DryRunPlaybackWaferHandlerText.Contains(
                                    OpenVisionLanguageService.T("Connections.WaferHandlerState.Destination"),
                                    StringComparison.CurrentCulture),
                                "Wafer-handler playback did not expose destination ownership after place.");
                            AssertSmoke(
                                transferViewport.LastRenderedTransferOwnershipState
                                    == WaferHandlerOwnershipState.Destination
                                && transferViewport.LastRenderedTransferOwnershipText == "DEST",
                                "The linked workpiece did not render destination ownership.");
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(holdingStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        if (playbackStep.BoundarySnapshot.InspectionSortRouters.Count == 1)
                        {
                            var routedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-ng-routed", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(routedStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.HasDryRunPlaybackInspectionSorter
                                && !vm.IsDryRunPlaybackInspectionSorterFault
                                && routedStep.BoundarySnapshot.InspectionSortRouters.Single().State
                                    == InspectionSortRouteState.NgRouted
                                && vm.DryRunPlaybackInspectionSorterText.Contains(
                                    OpenVisionLanguageService.T("Connections.InspectionSortState.NgRouted"),
                                    StringComparison.CurrentCulture),
                                "Inspection-sorter playback did not expose the NG route selection.");
                        }
                        if (playbackStep.BoundarySnapshot.InspectionHandoffs.Count == 1)
                        {
                            var readyStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-inspection-ready", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(readyStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                vm.HasDryRunPlaybackInspectionHandoff
                                && !vm.IsDryRunPlaybackInspectionHandoffFault
                                && readyStep.BoundarySnapshot.InspectionHandoffs.Single().State
                                    == InspectionHandoffState.Ready
                                && vm.DryRunPlaybackInspectionHandoffText.Contains(
                                    OpenVisionLanguageService.T("Connections.InspectionHandoffState.Ready"),
                                    StringComparison.CurrentCulture),
                                "Inspection-handoff playback did not expose the ready boundary.");
                            var completedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-inspection-complete", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(completedStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                completedStep.BoundarySnapshot.InspectionHandoffs.Single().State
                                    == InspectionHandoffState.Complete
                                && vm.DryRunPlaybackInspectionHandoffText.Contains(
                                    OpenVisionLanguageService.T("Connections.InspectionHandoffState.Complete"),
                                    StringComparison.CurrentCulture),
                                "Inspection-handoff playback did not expose result acceptance and completion.");
                        }
                        if (playbackStep.BoundarySnapshot.Prealigners.Count == 1)
                        {
                            AssertSmoke(
                                vm.HasDryRunPlaybackPrealigner
                                && !vm.IsDryRunPlaybackPrealignerFault
                                && playbackStep.BoundarySnapshot.Prealigners.Single().State == PrealignerState.Ready
                                && vm.DryRunPlaybackPrealignerText.Contains(
                                    OpenVisionLanguageService.T("Connections.PrealignerState.Ready"),
                                    StringComparison.CurrentCulture),
                                "Pre-aligner playback did not expose the clamped ready boundary.");
                            var alignedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-alignment-complete", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(alignedStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                alignedStep.BoundarySnapshot.Prealigners.Single().State == PrealignerState.Aligned
                                && vm.DryRunPlaybackPrealignerText.Contains(
                                    OpenVisionLanguageService.T("Connections.PrealignerState.Aligned"),
                                    StringComparison.CurrentCulture),
                                "Pre-aligner playback did not expose accepted target alignment.");
                            var releasedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                                string.Equals(step.StepId, "wait-cylinder-retracted", StringComparison.Ordinal));
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(releasedStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                releasedStep.BoundarySnapshot.Prealigners.Single().State == PrealignerState.Released
                                && vm.DryRunPlaybackPrealignerText.Contains(
                                    OpenVisionLanguageService.T("Connections.PrealignerState.Released"),
                                    StringComparison.CurrentCulture),
                                "Pre-aligner playback did not expose safe clamp release.");
                            vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        break;
                    }
                    var playbackControl = FindVisualDescendant<Button>(window, candidate =>
                        ReferenceEquals(candidate.Command, vm.NextDryRunPlaybackStepCommand))
                        ?? throw new InvalidOperationException("The dry-run playback Next button was not available.");
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    playbackControl.Focus();
                    Keyboard.Focus(playbackControl);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(playbackControl.IsKeyboardFocused, "The playback Next button did not receive focus.");
                    if (connectionWorkbenchState.Equals("dry-run-playback-control-focus", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    MovePointerToCenter(playbackControl);
                    Mouse.Capture(playbackControl, CaptureMode.SubTree);
                    Mouse.Synchronize();
                    await Task.Delay(200);
                    AssertSmoke(playbackControl.IsMouseOver, "The playback Next button did not enter hover state.");
                    if (connectionWorkbenchState.Equals("dry-run-playback-control-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        playbackControl.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(playbackControl.IsPressed, "The playback Next button did not enter pointer-down state.");
                    }
                    break;
                case "dry-run-playback-entry-focus":
                case "dry-run-playback-entry-hover":
                case "dry-run-playback-entry-pressed":
                case "dry-run-playback-entry-disabled":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var playbackEntryStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        string.Equals(step.StepId, "wait-cylinder-extended", StringComparison.Ordinal));
                    var playbackEntryButton = FindVisualDescendant<Button>(workbench, candidate =>
                        ReferenceEquals(candidate.Command, vm.RecipeConnections.PlayRecipeDryRunStepCommand)
                        && ReferenceEquals(candidate.CommandParameter, playbackEntryStep))
                        ?? throw new InvalidOperationException("The dry-run playback entry button was not available.");
                    if (connectionWorkbenchState.Equals("dry-run-playback-entry-disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.IsRunMode = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(!playbackEntryButton.IsEnabled, "The playback entry remained enabled in Run mode.");
                        break;
                    }
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    playbackEntryButton.BringIntoView();
                    playbackEntryButton.Focus();
                    Keyboard.Focus(playbackEntryButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(playbackEntryButton.IsKeyboardFocused, "The playback entry did not receive focus.");
                    if (connectionWorkbenchState.Equals("dry-run-playback-entry-focus", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    MovePointerToCenter(playbackEntryButton);
                    Mouse.Capture(playbackEntryButton, CaptureMode.SubTree);
                    Mouse.Synchronize();
                    await Task.Delay(200);
                    AssertSmoke(playbackEntryButton.IsMouseOver, "The playback entry did not enter hover state.");
                    if (connectionWorkbenchState.Equals("dry-run-playback-entry-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        playbackEntryButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(playbackEntryButton.IsPressed, "The playback entry did not enter pointer-down state.");
                    }
                    break;
                case "dry-run-timeline-focus":
                case "dry-run-timeline-hover":
                case "dry-run-timeline-pressed":
                case "dry-run-timeline-disabled":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                    for (var attempt = 0;
                         attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                         attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    var timelineStep = vm.RecipeConnections.RecipeDryRunTimeline.FirstOrDefault(step =>
                        string.Equals(step.StepId, "wait-process-position", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("The dry-run timeline visual-state step was not available.");
                    vm.RecipeConnections.SelectedRecipeDryRunStep = timelineStep;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var timelineButton = FindVisualDescendant<Button>(workbench, candidate =>
                        ReferenceEquals(candidate.Command, vm.RecipeConnections.OpenRecipeDryRunStepCommand)
                        && ReferenceEquals(candidate.CommandParameter, timelineStep))
                        ?? throw new InvalidOperationException("The dry-run timeline button was not available.");
                    if (connectionWorkbenchState.Equals("dry-run-timeline-disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.IsRunMode = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(!timelineButton.IsEnabled, "The dry-run timeline button remained enabled in Run mode.");
                        break;
                    }
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    timelineButton.BringIntoView();
                    timelineButton.Focus();
                    Keyboard.Focus(timelineButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(timelineButton.IsKeyboardFocused, "The dry-run timeline button did not receive focus.");
                    if (connectionWorkbenchState.Equals("dry-run-timeline-focus", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    MovePointerToCenter(timelineButton);
                    Mouse.Capture(timelineButton, CaptureMode.SubTree);
                    Mouse.Synchronize();
                    await Task.Delay(200);
                    AssertSmoke(timelineButton.IsMouseOver, "The dry-run timeline button did not enter hover state.");
                    if (connectionWorkbenchState.Equals("dry-run-timeline-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        timelineButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(timelineButton.IsPressed, "The dry-run timeline button did not enter pointer-down state.");
                    }
                    break;
                case "dry-run-focus":
                case "dry-run-hover":
                case "dry-run-pressed":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    dryRunButton.BringIntoView();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    dryRunButton.UpdateLayout();
                    dryRunButton.Focus();
                    Keyboard.Focus(dryRunButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(dryRunButton.IsKeyboardFocused, "Recipe dry-run button did not receive focus.");
                    if (connectionWorkbenchState.Equals("dry-run-focus", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    MovePointerToCenter(dryRunButton);
                    Mouse.Capture(dryRunButton, CaptureMode.SubTree);
                    Mouse.Synchronize();
                    await Task.Delay(200);
                    if (connectionWorkbenchState.Equals("dry-run-hover", StringComparison.OrdinalIgnoreCase))
                    {
                        AssertSmoke(dryRunButton.IsMouseOver, "Recipe dry-run button did not enter hover state.");
                    }
                    else
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        dryRunButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(dryRunButton.IsPressed, "Recipe dry-run button did not enter pointer-down state.");
                    }
                    break;
                case "preview":
                    var previewRow = vm.RecipeConnections.Rows.FirstOrDefault(row =>
                        row.Kind == LayoutComponentKind.PneumaticCylinder
                        && row.CanPreviewSequenceStep)
                        ?? throw new InvalidOperationException("No previewable cylinder row was available.");
                    AssertSmoke(
                        !vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow),
                        "Step preview was enabled before readiness passed.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow),
                        "Step preview was not enabled after readiness passed.");
                    vm.RecipeConnections.PreviewSequenceStepCommand.Execute(previewRow);
                    for (var attempt = 0; attempt < 100 && !previewRow.HasPreviewResult; attempt++)
                    {
                        await Task.Delay(20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    AssertSmoke(
                        previewRow.PreviewResult?.Outcome == SequenceStepPreviewOutcome.Completed,
                        "The isolated cylinder step preview did not complete.");
                    var previewRows = FindVisualDescendant<ListBox>(workbench, candidate =>
                        string.Equals(candidate.Name, "ConnectionRowsListBox", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Connection rows were not available.");
                    previewRows.ScrollIntoView(previewRow);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        FindVisualDescendant<Button>(workbench, candidate =>
                            string.Equals(candidate.Name, "PreviewConnectionSequenceStepButton", StringComparison.Ordinal)
                            && candidate.IsVisible
                            && candidate.IsEnabled) is not null,
                        "The preview step action was not visible and enabled.");
                    break;
                case "preview-hover":
                case "preview-pressed":
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var pressedPreviewRow = vm.RecipeConnections.Rows.First(row =>
                        row.Kind == LayoutComponentKind.LinearStage
                        && row.CanPreviewSequenceStep);
                    var pressedPreviewRows = FindVisualDescendant<ListBox>(workbench, candidate =>
                        string.Equals(candidate.Name, "ConnectionRowsListBox", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Connection rows were not available.");
                    pressedPreviewRows.ScrollIntoView(pressedPreviewRow);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var previewButton = FindVisualDescendant<Button>(workbench, candidate =>
                        string.Equals(candidate.Name, "PreviewConnectionSequenceStepButton", StringComparison.Ordinal)
                        && ReferenceEquals(candidate.DataContext, pressedPreviewRow)
                        && candidate.IsVisible
                        && candidate.IsEnabled)
                        ?? throw new InvalidOperationException("No enabled step preview button was visible.");
                    window.Activate();
                    SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    previewButton.BringIntoView();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    previewButton.UpdateLayout();
                    previewButton.Focus();
                    Keyboard.Focus(previewButton);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(previewButton.IsKeyboardFocused, "Step preview button did not receive focus.");
                    MovePointerToCenter(previewButton);
                    await Task.Delay(200);
                    AssertSmoke(previewButton.IsMouseOver, "Step preview button did not enter hover state.");
                    if (connectionWorkbenchState.Equals("preview-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(previewButton.IsPressed, "Step preview button did not enter pointer-down state.");
                    }
                    break;
                case "add-step":
                    AssertSmoke(
                        vm.TryAddLayoutComponent(LayoutComponentKind.LinearStage),
                        "A stage could not be added for target-step evidence.");
                    var targetRow = vm.RecipeConnections.Rows.First(row =>
                        row.ComponentId == vm.Layout.SelectedItem?.Id);
                    vm.RecipeConnections.SelectedRow = targetRow;
                    var rows = FindVisualDescendant<ListBox>(workbench, candidate =>
                        string.Equals(candidate.Name, "ConnectionRowsListBox", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Connection rows were not available.");
                    rows.ScrollIntoView(targetRow);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        FindVisualDescendant<Button>(workbench, candidate =>
                            string.Equals(candidate.Name, "AddConnectionSequenceStepButton", StringComparison.Ordinal)
                            && candidate.IsVisible
                            && candidate.IsEnabled) is not null,
                        "The unused connection did not expose an enabled target-step action.");
                    break;
                case "validation":
                    var stage = vm.Layout.Items.FirstOrDefault(item =>
                        item.Component?.Kind == LayoutComponentKind.LinearStage)
                        ?? throw new InvalidOperationException("No stage was available for validation evidence.");
                    vm.Layout.Select(stage.Id);
                    var editor = vm.Layout.SelectedComponentEditor
                        ?? throw new InvalidOperationException("Stage binding editor was not available.");
                    editor.BehaviorBindingId = "missing-smoke-axis";
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.RecipeConnections.HasValidationErrors
                        && vm.RecipeConnections.Rows.Any(row =>
                            row.ComponentId == stage.Id && !row.IsValid),
                        "Invalid stage binding did not appear in the connection workbench.");
                    break;
                default:
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

        if (!string.IsNullOrWhiteSpace(sequenceState))
        {
            await ApplySequenceSmokeStateAsync(window, vm, sequenceState);
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
            roundTripReport = CreateRoundTripReport(
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

            roundTripReport = CreateRoundTripReport(
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
                layoutAlignmentReport = ExerciseLayoutAlignment(
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
            await ApplyAxisTuningSmokeStateAsync(window, vm, axisTuningState);
        }

        if (!string.IsNullOrWhiteSpace(layoutPropertyState))
        {
            if (string.IsNullOrWhiteSpace(layoutSelectId) && string.IsNullOrWhiteSpace(layoutSelectMany))
            {
                throw new ArgumentException(
                    "--smoke-layout-property-state requires a layout selection.");
            }

            await ApplyLayoutPropertySmokeStateAsync(window, vm, layoutPropertyState);
        }

        if (!string.IsNullOrWhiteSpace(layoutHistoryReportPath))
        {
            layoutHistoryReport = await ExerciseLayoutHistoryAsync(vm, layoutHistoryReportPath);
            layoutHistoryReport.Save(layoutHistoryReportPath);
        }

        if (!string.IsNullOrWhiteSpace(directSceneReportPath))
        {
            directSceneReport = await ExerciseDirectSceneAuthoringAsync(window, vm, directSceneReportPath);
            directSceneReport.Save(directSceneReportPath);
        }

        if (!string.IsNullOrWhiteSpace(canvasNavigationReportPath))
        {
            canvasNavigationReport = await ExerciseCanvasNavigationAsync(window, vm);
            canvasNavigationReport.Save(canvasNavigationReportPath);
        }

        if (!string.IsNullOrWhiteSpace(directTransformReportPath))
        {
            directTransformReport = await ExerciseDirectTransformHandlesAsync(
                window,
                vm,
                directTransformReportPath);
            directTransformReport.Save(directTransformReportPath);
        }

        if (!string.IsNullOrWhiteSpace(multiTransformReportPath))
        {
            multiTransformReport = await ExerciseMultiSelectionTransformAsync(
                window,
                vm,
                multiTransformReportPath);
            multiTransformReport.Save(multiTransformReportPath);
        }

        if (!string.IsNullOrWhiteSpace(libraryDropReportPath))
        {
            libraryDropReport = await ExerciseLibrarySceneDropAsync(
                window,
                vm,
                libraryDropReportPath);
            libraryDropReport.Save(libraryDropReportPath);
        }

        if (!string.IsNullOrWhiteSpace(layerOrderReportPath))
        {
            layerOrderReport = await ExerciseLayerOrderAsync(
                window,
                vm,
                layerOrderReportPath);
            layerOrderReport.Save(layerOrderReportPath);
        }

        if (!string.IsNullOrWhiteSpace(editMenuState))
        {
            await ApplyEditMenuSmokeStateAsync(window, vm, editMenuState);
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

            vm.RunCommand.Execute(null);
            await Task.Delay(900);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(pickPlaceState))
        {
            if (!startSimulation)
            {
                throw new ArgumentException(
                    "--smoke-pick-place-state requires --smoke-start-simulation.");
            }

            await ApplyPickAndPlaceSmokeStateAsync(window, vm, pickPlaceState);
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

            vm.SimulationWorkspace.BatchRepetitionCount = 3;
            vm.SimulationWorkspace.ScenarioDurationCycles = 100_000;
            for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.RunScenarioBatchCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Repeat validation was unavailable during the smoke run.");
            }

            vm.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0; attempt < 40 && !vm.IsBatchRunning; attempt++)
            {
                await Task.Delay(25);
            }

            if (!vm.IsBatchRunning || !vm.CancelScenarioBatchCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Repeat validation did not enter a cancellable state.");
            }

            vm.CancelScenarioBatchCommand.Execute(null);
            for (var attempt = 0; attempt < 80 && vm.IsBatchRunning; attempt++)
            {
                await Task.Delay(25);
            }

            if (vm.IsBatchRunning || !vm.BatchWasCanceled)
            {
                throw new InvalidOperationException("Repeat validation cancellation did not complete.");
            }

            vm.SimulationWorkspace.BatchRepetitionCount = 2;
            vm.SimulationWorkspace.ScenarioDurationCycles = 1200;
            for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }
            vm.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 120 && (vm.IsBatchRunning || vm.LatestBatchResult is null);
                 attempt++)
            {
                await Task.Delay(25);
            }

            if (vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
            {
                throw new InvalidOperationException("Repeat validation did not produce two identical runs.");
            }
            var outcomes = vm.LatestBatchResult.Runs.Last().Result.AssertionOutcomes;
            if (outcomes.Length != 3
                || outcomes.Any(outcome => !outcome.IsPassed)
                || vm.BatchAssertionOutcomes.Count != 3)
            {
                throw new InvalidOperationException("Repeat validation did not expose three passing acceptance results.");
            }

            vm.AcceptBatchBaselineCommand.Execute(null);
            if (!vm.HasAcceptedBatchBaseline)
            {
                throw new InvalidOperationException("Repeat validation baseline was not accepted.");
            }

            var previousBatch = vm.LatestBatchResult;
            for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }
            vm.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
                 attempt++)
            {
                await Task.Delay(25);
            }

            if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
                || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
            {
                throw new InvalidOperationException("Accepted baseline comparison did not pass.");
            }

            previousBatch = vm.LatestBatchResult;
            vm.SimulationWorkspace.ScenarioSeed++;
            for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }
            vm.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
                 attempt++)
            {
                await Task.Delay(25);
            }

            var mismatch = vm.LatestBatchResult?.FirstMismatch;
            if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
                || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: false }
                || mismatch is null
                || !string.Equals(
                    mismatch.TargetId,
                    vm.SimulationWorkspace.ScenarioTargetId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Changed repeat validation did not expose its first mismatch.");
            }

            vm.NavigateToBatchMismatchCommand.Execute(null);
            if (!string.Equals(vm.Layout.SelectedItem?.Id, mismatch.TargetId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("First mismatch navigation did not select its equipment target.");
            }

            vm.ClearBatchBaselineCommand.Execute(null);
            if (vm.HasAcceptedBatchBaseline)
            {
                throw new InvalidOperationException("Accepted baseline reset did not clear the baseline.");
            }

            previousBatch = vm.LatestBatchResult;
            for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }
            vm.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
                 attempt++)
            {
                await Task.Delay(25);
            }

            if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
                || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
            {
                throw new InvalidOperationException("Changed scenario could not establish a new baseline candidate.");
            }

            Console.WriteLine(
                "Repeat validation smoke passed: cancel, baseline replay/reset, mismatch navigation.");
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

            vm.SimulationWorkspace.BatchRepetitionCount = 2;
            vm.SimulationWorkspace.ScenarioDurationCycles = 1200;
            await vm.SaveProjectAsync(projectPath);
            for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            var previousBatch = vm.LatestBatchResult;
            vm.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
                 attempt++)
            {
                await Task.Delay(25);
            }

            if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
                || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
            {
                throw new InvalidOperationException("Persisted repeat validation did not complete successfully.");
            }

            vm.AcceptBatchBaselineCommand.Execute(null);
            if (!File.Exists($"{Path.GetFullPath(projectPath)}.batch-result.json")
                || !File.Exists($"{Path.GetFullPath(projectPath)}.batch-baseline.json"))
            {
                throw new InvalidOperationException("Project-linked batch sidecars were not saved.");
            }

            if (!await vm.OpenProjectAsync(projectPath) || !vm.HasRestoredBatchArtifacts)
            {
                throw new InvalidOperationException("Saved batch evidence did not restore in the same process.");
            }
            if (!vm.SimulationWorkspace.RequireAutomaticCycleCompleted
                || vm.SimulationWorkspace.MinimumCompletedCycles != 1
                || !vm.SimulationWorkspace.RequireNoActiveFaults
                || !vm.SimulationWorkspace.RequireFinalEquipmentState
                || vm.SimulationWorkspace.FinalEquipmentTargetId != "cylinder-1"
                || vm.SimulationWorkspace.FinalEquipmentExpectedState != "Extended"
                || vm.ConditionScenario.IsConfigured)
            {
                throw new InvalidOperationException(
                    "Acceptance criteria did not round-trip without auto-running.");
            }

            Console.WriteLine("Batch persistence smoke passed: save, result/baseline sidecars, same-process reload.");
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

        if (!string.IsNullOrWhiteSpace(faultManagerReportPath))
        {
            faultManagerReport = await ExerciseFaultManagerAsync(window, vm);
            faultManagerReport.Save(faultManagerReportPath);
        }

        if (!string.IsNullOrWhiteSpace(faultManagerState))
        {
            await ApplyFaultManagerSmokeStateAsync(window, vm, faultManagerState);
        }

        if (!string.IsNullOrWhiteSpace(digitalIoCommissioningReportPath))
        {
            digitalIoCommissioningReport = await ExerciseDigitalIoCommissioningAsync(
                window,
                vm,
                projectPath);
            digitalIoCommissioningReport.Save(digitalIoCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(digitalIoCommissioningState))
        {
            await ApplyDigitalIoCommissioningSmokeStateAsync(
                window,
                vm,
                digitalIoCommissioningState);
        }

        if (!string.IsNullOrWhiteSpace(cameraCommissioningReportPath))
        {
            cameraCommissioningReport = await ExerciseCameraCommissioningAsync(
                window,
                vm,
                projectPath,
                editCameraImageSource);
            cameraCommissioningReport.Save(cameraCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(cameraCommissioningState))
        {
            await ApplyCameraCommissioningSmokeStateAsync(window, vm, cameraCommissioningState);
        }

        if (!string.IsNullOrWhiteSpace(axisCommissioningReportPath))
        {
            axisCommissioningReport = await ExerciseAxisCommissioningAsync(window, vm);
            axisCommissioningReport.Save(axisCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(axisCommissioningState))
        {
            await ApplyAxisCommissioningSmokeStateAsync(window, vm, axisCommissioningState);
        }

        if (!string.IsNullOrWhiteSpace(multiAxisRecipeReportPath))
        {
            multiAxisRecipeReport = await ExerciseMultiAxisCommissioningRecipeAsync(
                window,
                vm,
                multiAxisRecipeSavePath);
            multiAxisRecipeReport.Save(multiAxisRecipeReportPath);
        }

        if (!string.IsNullOrWhiteSpace(multiAxisRecipeState))
        {
            await ApplyMultiAxisCommissioningRecipeSmokeStateAsync(
                window,
                vm,
                multiAxisRecipeState);
        }

        if (!string.IsNullOrWhiteSpace(cylinderCommissioningReportPath))
        {
            cylinderCommissioningReport = await ExerciseCylinderCommissioningAsync(window, vm);
            cylinderCommissioningReport.Save(cylinderCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(cylinderCommissioningState))
        {
            await ApplyCylinderCommissioningSmokeStateAsync(window, vm, cylinderCommissioningState);
        }

        if (!string.IsNullOrWhiteSpace(conveyorCommissioningReportPath))
        {
            conveyorCommissioningReport = await ExerciseConveyorCommissioningAsync(window, vm);
            conveyorCommissioningReport.Save(conveyorCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(conveyorCommissioningState))
        {
            await ApplyConveyorCommissioningSmokeStateAsync(window, vm, conveyorCommissioningState);
        }

        if (!string.IsNullOrWhiteSpace(sensorCommissioningReportPath))
        {
            sensorCommissioningReport = await ExerciseSensorCommissioningAsync(window, vm);
            sensorCommissioningReport.Save(sensorCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(sensorCommissioningState))
        {
            await ApplySensorCommissioningSmokeStateAsync(window, vm, sensorCommissioningState);
        }

        if (!string.IsNullOrWhiteSpace(evidenceDrawerState))
        {
            await ApplyEvidenceDrawerSmokeStateAsync(window, evidenceDrawerState);
        }

        if (!string.IsNullOrWhiteSpace(globalCommandState))
        {
            await ApplyGlobalCommandSmokeStateAsync(window, globalCommandState);
        }

        if (vm.SelectedEquipmentStatus is { } selectedEquipmentStatus)
        {
            Console.WriteLine(
                $"Selected equipment status: {selectedEquipmentStatus.Name} | " +
                $"{selectedEquipmentStatus.StateText} | {selectedEquipmentStatus.ConditionText}");
        }

        if (!string.IsNullOrWhiteSpace(projectSafetyReportPath))
        {
            var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
            var failures = new List<string>();
            void Check(string name, bool passed)
            {
                checks[name] = passed;
                if (!passed)
                {
                    failures.Add(name);
                }
            }

            var untouchedPromptCount = 0;
            vm.UnsavedProjectPrompt = () =>
            {
                untouchedPromptCount++;
                return UnsavedProjectDecision.Cancel;
            };
            Check("untouched-project-closes-without-prompt", await vm.TryResolveUnsavedChangesAsync());
            Check("untouched-project-does-not-request-decision", untouchedPromptCount == 0);

            var fullSavePath = Path.GetFullPath(projectSafetySavePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
            await vm.SaveProjectAsync(fullSavePath);
            var initialComponentCount = int.Parse(
                vm.LayoutComponentCountText,
                System.Globalization.CultureInfo.InvariantCulture);
            Check("initial-project-clean", !vm.HasUnsavedChanges);

            Check("first-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
            Check("first-edit-marks-dirty", vm.HasUnsavedChanges);
            Check("dirty-title-visible", vm.Title.EndsWith(" *", StringComparison.Ordinal));

            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Cancel;
            Check("cancel-blocks-new-project", !await vm.CreateNewProjectAsync());
            Check("cancel-keeps-project-dirty", vm.HasUnsavedChanges);

            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Save;
            Check("save-allows-open-replacement", await vm.OpenProjectReplacingCurrentAsync(fullSavePath));
            Check("save-clears-dirty", !vm.HasUnsavedChanges);
            Check("save-clears-title-marker", !vm.Title.EndsWith(" *", StringComparison.Ordinal));
            Check("backup-created", File.Exists(fullSavePath + ".bak"));
            var savedComponentCount = int.Parse(
                vm.LayoutComponentCountText,
                System.Globalization.CultureInfo.InvariantCulture);
            Check("first-edit-persisted", savedComponentCount == initialComponentCount + 1);

            Check("second-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
            Check("discard-allows-open-replacement", await vm.OpenProjectReplacingCurrentAsync(fullSavePath));
            Check("discarded-edit-not-persisted",
                int.Parse(
                    vm.LayoutComponentCountText,
                    System.Globalization.CultureInfo.InvariantCulture) == savedComponentCount);
            Check("reopen-restores-clean-state", !vm.HasUnsavedChanges);

            Check("new-project-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
            Check("discard-allows-new-project", await vm.CreateNewProjectAsync());
            Check("new-project-is-clean", !vm.HasUnsavedChanges);
            Check("new-project-has-no-path", vm.Title.EndsWith("Untitled", StringComparison.Ordinal));
            Check("saved-project-reopens-after-new", await vm.OpenProjectAsync(fullSavePath));

            Check("visual-dirty-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
            Check("visual-dirty-state", vm.HasUnsavedChanges);

            if (!string.IsNullOrWhiteSpace(unsavedDialogScreenshotPath))
            {
                var dialog = new WpfMessageDialogWindow(MainViewModel.CreateUnsavedProjectDialogOptions())
                {
                    Owner = window,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                dialog.Show();
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(100);
                var dialogMonitor = SmokeDpiTestHook.CaptureMonitorEvidence(dialog);
                Check("dialog-contained-on-test-monitor", dialogMonitor.WindowContainedByMonitor);
                var saveButton = FindVisualDescendant<Button>(dialog, button =>
                    string.Equals(
                        button.Content?.ToString(),
                        OpenVisionLanguageService.T("Project.Save", "저장", "Save"),
                        StringComparison.Ordinal));
                Check("dialog-save-button-visible", saveButton is { IsVisible: true });
                if (saveButton is not null)
                {
                    dialog.Activate();
                    SetForegroundWindow(new WindowInteropHelper(dialog).Handle);
                    saveButton.Focus();
                    MovePointerToCenter(saveButton);
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check("dialog-save-button-pointer-down", saveButton.IsPressed);
                    CaptureWindow(dialog, unsavedDialogScreenshotPath);
                    ReleaseSmokePointer();
                }

                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            }

            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
            projectSafetyReport = new SmokeDirectSceneAuthoringReport
            {
                Checks = checks,
                Failures = failures
            };
            projectSafetyReport.Save(projectSafetyReportPath);
            Console.WriteLine(
                $"Project safety smoke {(projectSafetyReport.IsValid ? "passed" : "failed")}.");
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
            smokePerfReport = await MeasureSmokePerformanceAsync(
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
            ReleaseSmokePointer();
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
            faultManagerReport is not null ||
            digitalIoCommissioningReport is not null ||
            cameraCommissioningReport is not null ||
            axisCommissioningReport is not null ||
            multiAxisRecipeReport is not null ||
            cylinderCommissioningReport is not null ||
            conveyorCommissioningReport is not null ||
            sensorCommissioningReport is not null ||
            recipeGalleryReport is not null ||
            connectionWorkbenchReport is not null ||
            projectSafetyReport is not null ||
            performSmokePerf)
        {
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
                        : faultManagerReport is { IsValid: false }
                            ? 13
                        : cameraCommissioningReport is { IsValid: false }
                            ? 18
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
                        : connectionWorkbenchReport is { IsValid: false }
                            ? 21
                        : projectSafetyReport is { IsValid: false }
                            ? 22
                        : 0;
            Application.Current.Shutdown(exitCode);
        }
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseFaultManagerAsync(
        ShellWindow window,
        MainViewModel viewModel)
    {
        if (!viewModel.IsRunMode || !viewModel.IsRunning)
        {
            throw new ArgumentException(
                "--smoke-fault-manager-report requires --smoke-run-layout and --smoke-start-simulation.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            throw new InvalidOperationException(failureMessage);
        }

        var manager = viewModel.FaultManager;
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => manager.IsEnabled && manager.Targets.Count > 0,
            "Fault Manager did not become available from the runtime snapshot.");
        Check("runModeEnablesFaultManager", manager.IsEnabled);

        manager.SelectedKind = manager.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.StuckDigitalInput);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("digitalInputTargetsAvailable", manager.Targets.Count > 0 &&
            manager.Targets.All(target => target.Kind == SimulationFaultKind.StuckDigitalInput));
        Check("forcedValueRequiredForDigitalInput", manager.RequiresForcedValue);
        var initialFaultCount = manager.ActiveFaults.Count;
        manager.SelectedForcedValue = manager.ForcedValueOptions.Single(option => option.Value);
        var digitalInputTarget = manager.Targets[0];
        Check("selectorChangesDoNotInject", manager.ActiveFaults.Count == initialFaultCount);
        Check("digitalInputInjectAvailable", manager.InjectCommand.CanExecute(null));
        manager.InjectCommand.Execute(null);
        await WaitForAsync(
            () => manager.ActiveFaults.Any(fault =>
                fault.Kind == SimulationFaultKind.StuckDigitalInput &&
                string.Equals(fault.TargetId, digitalInputTarget.Id, StringComparison.Ordinal) &&
                fault.ForcedValue == true),
            "Stuck-DI fault was not published in a runtime snapshot.");
        await WaitForAsync(
            () => manager.OperationStatusText.Contains(
                OpenVisionLanguageService.T("Fault.StuckDigitalInput"),
                StringComparison.CurrentCulture),
            "Localized Stuck-DI injection status was not published.");
        Check("digitalInputFaultPublished", manager.ActiveFaults.Count == 1);
        Check("duplicateInjectionBlocked", !manager.InjectCommand.CanExecute(null));
        Check("injectCommandLogged", viewModel.LogMessages.Any(line =>
            line.Contains(OpenVisionLanguageService.T("Runtime.Category.Fault"), StringComparison.CurrentCulture) &&
            line.Contains(OpenVisionLanguageService.T("Fault.ActionInject"), StringComparison.CurrentCulture)));

        manager.SelectedKind = manager.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.CylinderTravelBlocked);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("cylinderTargetsAvailable", manager.Targets.Count > 0 &&
            manager.Targets.All(target => target.Kind == SimulationFaultKind.CylinderTravelBlocked));
        Check("forcedValueHiddenForCylinder", !manager.RequiresForcedValue);
        var cylinderTarget = manager.Targets.FirstOrDefault(target =>
            string.Equals(target.Id, RoundTripCylinderId, StringComparison.Ordinal))
            ?? manager.Targets[0];
        manager.SelectedTarget = cylinderTarget;
        await WaitForAsync(
            () => manager.InjectCommand.CanExecute(null),
            "Fault Manager remained busy after Stuck-DI injection.");
        Check("cylinderInjectAvailable", manager.InjectCommand.CanExecute(null));
        manager.InjectCommand.Execute(null);
        await WaitForAsync(
            () => manager.ActiveFaults.Any(fault =>
                fault.Kind == SimulationFaultKind.CylinderTravelBlocked &&
                string.Equals(fault.TargetId, cylinderTarget.Id, StringComparison.Ordinal)),
            "Blocked-cylinder fault was not published in a runtime snapshot.");
        Check("twoFaultsPublished", manager.ActiveFaults.Count == 2);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("activeFaultListVisible", inspector.ActiveFaultListBox.IsVisible &&
            inspector.ActiveFaultListBox.Items.Count == 2);
        Check("activeCountLocalized", manager.ActiveFaultCountText == string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Fault.ActiveFaults"),
            2));

        manager.SelectedActiveFault = manager.ActiveFaults.Single(fault =>
            fault.Kind == SimulationFaultKind.StuckDigitalInput);
        await WaitForAsync(
            () => manager.ClearSelectedCommand.CanExecute(null),
            "Fault Manager remained busy before selected clear.");
        Check("clearSelectedAvailable", manager.ClearSelectedCommand.CanExecute(null));
        manager.ClearSelectedCommand.Execute(null);
        await WaitForAsync(
            () => manager.ActiveFaults.Count == 1 &&
                manager.ActiveFaults[0].Kind == SimulationFaultKind.CylinderTravelBlocked,
            "Selected Stuck-DI fault was not cleared from the runtime snapshot.");
        Check("selectedClearPreservesOtherFault", manager.ActiveFaults.Count == 1);
        await WaitForAsync(
            () => manager.ClearAllCommand.CanExecute(null),
            "Fault Manager remained busy before clear all.");
        Check("clearAllAvailable", manager.ClearAllCommand.CanExecute(null));
        manager.ClearAllCommand.Execute(null);
        await WaitForAsync(
            () => manager.ActiveFaults.Count == 0,
            "Clear all did not empty the runtime fault snapshot.");
        await WaitForAsync(
            () => !manager.IsOperationPending,
            "Fault Manager remained busy after clear all.");
        Check("clearAllStatusLocalized", manager.OperationStatusText ==
            OpenVisionLanguageService.T("Fault.RuntimeCleared"));
        Check("clearAllPublishesEmptyState", !manager.HasActiveFaults &&
            manager.ActiveFaultCountText == OpenVisionLanguageService.T("Fault.NoActiveFaults"));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("emptyFaultStateVisible", !inspector.ActiveFaultListBox.IsVisible &&
            inspector.NoActiveFaultsText.IsVisible);
        Check("clearCommandLogged", viewModel.LogMessages.Any(line =>
            line.Contains(OpenVisionLanguageService.T("Runtime.Category.Fault"), StringComparison.CurrentCulture) &&
            line.Contains(OpenVisionLanguageService.T("Fault.ActionClear"), StringComparison.CurrentCulture)));

        manager.SelectedKind = manager.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.CylinderTravelBlocked);
        manager.SelectedTarget = manager.Targets.First(target =>
            string.Equals(target.Id, cylinderTarget.Id, StringComparison.Ordinal));
        manager.InjectCommand.Execute(null);
        await WaitForAsync(
            () => manager.ActiveFaults.Count == 1,
            "Cylinder fault was not restored before Reset verification.");
        await WaitForAsync(
            () => !manager.IsOperationPending,
            "Fault Manager remained busy before Reset verification.");
        Check("resetAvailableWithActiveFault", viewModel.ResetCommand.CanExecute(null));
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => manager.ActiveFaults.Count == 0 && !viewModel.IsRunning,
            "Reset did not clear active faults and pause the runtime.");
        await WaitForAsync(
            () => manager.OperationStatusText == OpenVisionLanguageService.T("Fault.RuntimeCleared"),
            "Reset recovery status was not published from the empty runtime snapshot.");
        Check("resetPublishesRecovery", !manager.HasActiveFaults &&
            manager.SelectedActiveFault is null);

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesStatus", manager.OperationStatusText ==
            OpenVisionLanguageService.T("Fault.SelectTargetHint"));
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewModel.IsRunMode = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("designModeDisablesFaultManager", !manager.IsEnabled &&
            !manager.InjectCommand.CanExecute(null) &&
            !manager.ClearAllCommand.CanExecute(null));
        viewModel.IsRunMode = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("returnToRunRestoresAvailability", manager.IsEnabled);

        await ScrollFaultManagerIntoViewAsync(window, activeSection: false);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseDigitalIoCommissioningAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? projectPath)
    {
        if (!viewModel.IsRunMode || string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException(
                "--smoke-io-commissioning-report requires --smoke-run-layout and --smoke-project.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"I/O action '{action}' was not logged.");
        }

        var commissioning = viewModel.DigitalIo;
        await WaitForAsync(
            () => commissioning.IsEnabled && commissioning.HasSignals,
            "Digital I/O commissioning did not become available from the runtime snapshot.");
        var initialSnapshot = viewModel.SceneSnapshots.Latest
            ?? throw new InvalidOperationException("The initial runtime snapshot was unavailable.");
        Check("snapshotSignalsProjected", commissioning.Signals.Count == initialSnapshot.Signals.Count);

        commissioning.SelectedSignal = commissioning.Signals.FirstOrDefault(signal => !signal.IsInput);
        Check("digitalOutputReadOnly", commissioning.SelectedSignal is not null
            && !commissioning.CanForceOn
            && !commissioning.CanForceOff
            && !commissioning.CanClearForce);

        commissioning.SelectedSignal = commissioning.Signals.FirstOrDefault(signal => signal.IsInput);
        var selectedInput = commissioning.SelectedSignal
            ?? throw new InvalidOperationException("No digital input was available for commissioning.");
        var initialInput = initialSnapshot.Signals.Single(signal => signal.Id == selectedInput.Id);
        Check("selectedValueUsesSnapshot", selectedInput.Value == initialInput.Value
            && selectedInput.NominalValue == initialInput.NominalValue
            && selectedInput.OverrideValue == initialInput.OverrideValue);
        Check("snapshotOwnerAndRevisionProjected",
            commissioning.ControlOwnerText == viewModel.ControlOwnerText
            && commissioning.SignalRevisionText == initialSnapshot.SignalRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        await ExecuteAndWaitAsync(
            commissioning.StartManualControlCommand,
            "Io.ActionStartManual");
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual I/O control did not start.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual I/O control did not pause.");

        long revisionBeforeForce = viewModel.SceneSnapshots.Latest!.SignalRevision;
        await ExecuteAndWaitAsync(commissioning.ForceOnCommand, "Io.ActionForceOn");
        await WaitForAsync(
            () => commissioning.SelectedSignal?.OverrideValue == true,
            "Forced-ON I/O state was not published.");
        Check("forceOnUpdatesImmutableSnapshot", commissioning.SelectedSignal?.Value == true
            && viewModel.SceneSnapshots.Latest!.SignalRevision > revisionBeforeForce);

        long tickBeforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.TickIndex == tickBeforeStep + 1,
            "I/O commissioning Step did not advance exactly one tick.");
        Check("pauseStepRetainsForce", commissioning.SelectedSignal?.OverrideValue == true
            && commissioning.SelectedSignal.Value);

        await ExecuteAndWaitAsync(commissioning.ClearForceCommand, "Io.ActionClearForce");
        await WaitForAsync(
            () => commissioning.SelectedSignal?.OverrideValue is null,
            "Cleared I/O force state was not published.");
        Check("clearRestoresNominal", commissioning.SelectedSignal?.Value
            == commissioning.SelectedSignal?.NominalValue);

        await ExecuteAndWaitAsync(commissioning.ForceOffCommand, "Io.ActionForceOff");
        await WaitForAsync(
            () => commissioning.SelectedSignal?.OverrideValue == false,
            "Forced-OFF I/O state was not published.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest is { TickIndex: 0, ControlOwner: SimulationControlOwner.Definition }
                && commissioning.Signals.All(signal => signal.OverrideValue is null),
            "Reset did not clear the I/O force and restore the authored runtime.");
        Check("resetClearsAllForces", commissioning.Signals.All(signal => signal.OverrideValue is null));

        if (!await viewModel.OpenProjectAsync(projectPath))
        {
            throw new InvalidOperationException("The I/O commissioning project could not be reopened.");
        }
        await WaitForAsync(
            () => commissioning.HasSignals
                && commissioning.Signals.All(signal => signal.OverrideValue is null),
            "Project reopen restored a runtime-only I/O force.");
        Check("reopenDoesNotRestoreRuntimeForce",
            commissioning.Signals.All(signal => signal.OverrideValue is null)
            && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition);

        viewModel.IsRunMode = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await ScrollDigitalIoCommissioningIntoViewAsync(window);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task ApplyFaultManagerSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-fault-manager-state requires --smoke-run-layout.");
        }

        var manager = viewModel.FaultManager;
        for (var attempt = 0; attempt < 40 && manager.IsOperationPending; attempt++)
        {
            await Task.Delay(50);
        }
        if (state.Equals("recovered", StringComparison.OrdinalIgnoreCase))
        {
            if (manager.ClearAllCommand.CanExecute(null))
            {
                manager.ClearAllCommand.Execute(null);
            }
            for (var attempt = 0; attempt < 40 && manager.ActiveFaults.Count > 0; attempt++)
            {
                await Task.Delay(50);
            }
            for (var attempt = 0; attempt < 40 && manager.IsOperationPending; attempt++)
            {
                await Task.Delay(50);
            }
        }
        else if (state.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (!viewModel.ResetCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Reset was unavailable for the Fault Manager smoke state.");
            }
            viewModel.ResetCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 40 && (manager.ActiveFaults.Count > 0 || viewModel.IsRunning);
                 attempt++)
            {
                await Task.Delay(50);
            }
        }
        else if (state.Equals("popup-kind", StringComparison.OrdinalIgnoreCase))
        {
            await ScrollFaultManagerIntoViewAsync(window, activeSection: false);
            var inspector = FindVisualDescendant<RightToolRegionView>(window)
                ?? throw new InvalidOperationException("Run inspector was unavailable.");
            inspector.FaultKindComboBox.IsDropDownOpen = true;
        }
        else if (state.Equals("focus-clear", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("hover-clear", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-clear", StringComparison.OrdinalIgnoreCase))
        {
            if (manager.ActiveFaults.Count == 0)
            {
                throw new InvalidOperationException("An active fault is required for the clear-button smoke state.");
            }
            await ScrollFaultManagerIntoViewAsync(window, activeSection: true);
            var inspector = FindVisualDescendant<RightToolRegionView>(window)
                ?? throw new InvalidOperationException("Run inspector was unavailable.");
            var button = inspector.ClearSelectedFaultButton;
            if (state.StartsWith("focus", StringComparison.OrdinalIgnoreCase))
            {
                button.Focus();
            }
            else
            {
                MovePointerToCenter(button);
                if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
                {
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                }
            }
        }
        else if (!state.Equals("active", StringComparison.OrdinalIgnoreCase) &&
                 !state.Equals("active-top", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-fault-manager-state '{state}'. Expected active, active-top, " +
                "recovered, reset, popup-kind, focus-clear, hover-clear, or pressed-clear.");
        }

        await ScrollFaultManagerIntoViewAsync(
            window,
            activeSection: !state.Equals("active-top", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("popup-kind", StringComparison.OrdinalIgnoreCase));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseCameraCommissioningAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? projectPath,
        bool editImageSource)
    {
        if (!viewModel.IsRunMode || string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException(
                "--smoke-camera-commissioning-report requires --smoke-run-layout and --smoke-project.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task StepAsync()
        {
            var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex
                ?? throw new InvalidOperationException("Camera snapshot was unavailable before Step.");
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.TickIndex == beforeTick + 1,
                "Camera commissioning Step did not advance exactly one tick.");
        }

        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras.Count > 0,
            "Virtual camera snapshot was unavailable.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest is
                {
                    TickIndex: 0,
                    ControlOwner: SimulationControlOwner.Definition
                }
                && viewModel.SceneSnapshots.Latest.Cameras[0].State == VirtualCameraState.Idle,
            "Camera runtime did not reset before commissioning.");

        if (editImageSource)
        {
            var editor = viewModel.CameraImageSourceEditor;
            Check("sourceEditorRestoresProjectValues",
                editor.PathText == "assets/presence-check.pgm"
                && editor.Width == 16
                && editor.Height == 12
                && editor.PixelFormatText == "Mono8"
                && !editor.IsDirty
                && !editor.ApplyCommand.CanExecute(null));

            editor.PathText = "../outside.pgm";
            Check("projectExternalDraftDoesNotMutateProject",
                editor.HasError
                && !editor.ApplyCommand.CanExecute(null)
                && viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
            editor.RevertCommand.Execute(null);

            editor.PixelFormatText = string.Empty;
            Check("invalidDraftDoesNotMutateProject",
                editor.HasError
                && !editor.ApplyCommand.CanExecute(null)
                && viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
            editor.RevertCommand.Execute(null);
            Check("revertRestoresAppliedDefinition",
                editor.PixelFormatText == "Mono8"
                && !editor.IsDirty
                && !editor.HasError);

            var beforeApply = viewModel.SceneSnapshots.Latest!;
            editor.Width = 32;
            editor.Height = 24;
            Check("validDraftEnablesApply",
                editor.IsDirty
                && !editor.HasError
                && editor.ApplyCommand.CanExecute(null));
            editor.ApplyCommand.Execute(null);
            var afterApply = viewModel.SceneSnapshots.Latest!;
            Check("applyDoesNotStartAcquisition",
                afterApply.TickIndex == beforeApply.TickIndex
                && afterApply.SimulationTime == beforeApply.SimulationTime
                && afterApply.Cameras[0].State == VirtualCameraState.Idle
                && afterApply.Cameras[0].AcquisitionOrdinal == 0
                && afterApply.Cameras[0].FrameEvidence is null
                && !editor.IsDirty);

            await viewModel.SaveProjectAsync(projectPath);
            if (!await viewModel.OpenProjectAsync(projectPath))
            {
                throw new InvalidOperationException("Edited camera project could not be reopened.");
            }
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.Cameras[0] is
                {
                    State: VirtualCameraState.Idle,
                    AcquisitionOrdinal: 0,
                    FrameEvidence: null
                },
                "Edited camera project reopen restored an acquisition.");
            Check("saveReopenRestoresSourceSettings",
                editor.PathText == "assets/presence-check.pgm"
                && editor.Width == 32
                && editor.Height == 24
                && editor.PixelFormatText == "Mono8"
                && !editor.IsDirty);
            Check("saveReopenDoesNotAutoAcquire", true);
            viewModel.IsRunMode = true;
        }

        Check("authoredSelectionRestored",
            viewModel.SelectedVirtualCamera is not null
            && viewModel.SelectedCameraRecipe == "presence-check"
            && viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
        Check("manualStartAvailableAfterReset",
            viewModel.StartManualCameraControlCommand.CanExecute(null)
            && !viewModel.TriggerCameraCommand.CanExecute(null));

        viewModel.StartManualCameraControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual camera control did not start.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual camera control did not pause.");
        Check("pausedManualEnablesTrigger", viewModel.TriggerCameraCommand.CanExecute(null));

        var paused = viewModel.SceneSnapshots.Latest!;
        viewModel.TriggerCameraCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras[0] is
            {
                State: VirtualCameraState.Exposing,
                FrameEvidence: not null
            },
            "Camera trigger did not publish immutable frame evidence.");
        var triggered = viewModel.SceneSnapshots.Latest!;
        var camera = triggered.Cameras[0];
        var evidence = camera.FrameEvidence!;
        Check("triggerDoesNotAdvancePausedTick",
            triggered.TickIndex == paused.TickIndex
            && triggered.SimulationTime == paused.SimulationTime
            && camera.ExposureTicksRemaining == 4);

        var sourcePath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
            viewModel.CurrentCameraSourceText.Replace('/', Path.DirectorySeparatorChar));
        await using (var stream = File.OpenRead(sourcePath))
        {
            var expectedSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            Check("frameHashMatchesProjectAsset",
                evidence.ContentSha256 == expectedSha256
                && evidence.FrameId == "cam1/frame/00000001"
                && evidence.SourceRelativePath == "assets/presence-check.pgm"
                && (!editImageSource || evidence is { Width: 32, Height: 24, PixelFormat: "Mono8" }));
        }

        await Task.Delay(150);
        var frozen = viewModel.SceneSnapshots.Latest!;
        Check("pauseFreezesAcquisition",
            frozen.TickIndex == triggered.TickIndex
            && frozen.Cameras[0].ExposureTicksRemaining == 4);

        for (var index = 0; index < 3; index++)
        {
            await StepAsync();
        }
        Check("exposureHoldsUntilFourthTick",
            viewModel.SceneSnapshots.Latest!.Cameras[0].State == VirtualCameraState.Exposing
            && viewModel.SceneSnapshots.Latest.Cameras[0].ExposureTicksRemaining == 1);
        await StepAsync();
        Check("fourthTickStartsTransfer",
            viewModel.SceneSnapshots.Latest!.Cameras[0].State == VirtualCameraState.Transferring
            && viewModel.SceneSnapshots.Latest.Cameras[0].TransferTicksRemaining == 6);

        for (var index = 0; index < 5; index++)
        {
            await StepAsync();
        }
        Check("transferHoldsUntilSixthTick",
            viewModel.SceneSnapshots.Latest!.Cameras[0].State == VirtualCameraState.Transferring
            && viewModel.SceneSnapshots.Latest.Cameras[0].TransferTicksRemaining == 1);
        await StepAsync();
        var ready = viewModel.SceneSnapshots.Latest!.Cameras[0];
        var firstInspection = ready.Result?.InspectionEvidence;
        Check("sixthTransferTickPublishesResult",
            ready.State == VirtualCameraState.FrameReady
            && ready.Result?.FrameEvidence == evidence
            && ready.Result.Decision == PlaceholderInspectionDecision.Pass
            && viewModel.CurrentCameraFrameHashText == evidence.ContentSha256);
        Check("deterministicRunnerPublishesCorrelatedEvidence",
            firstInspection is not null
            && firstInspection.AcquisitionId == evidence.FrameId
            && firstInspection.CameraId == ready.Id
            && firstInspection.RecipeId == "presence-check"
            && firstInspection.FrameId == evidence.FrameId
            && firstInspection.Decision == PlaceholderInspectionDecision.Pass
            && firstInspection.Metrics.SequenceEqual(new Dictionary<string, double>
            {
                ["ContentLengthBytes"] = evidence.ContentLength,
                ["PixelCount"] = (double)evidence.Width * evidence.Height,
                ["SimulationTick"] = triggered.TickIndex
            }));
        await WaitForAsync(
            () => viewModel.LogMessages.Any(line =>
                line.Contains(firstInspection!.InspectionId, StringComparison.Ordinal)
                && line.Contains("PixelCount=", StringComparison.Ordinal)),
            "Inspection identity and metrics did not reach the existing Event Journal.");
        Check("existingEventJournalContainsInspectionEvidence", true);
        await WaitForAsync(
            () => viewModel.LatestVisionEvidence is not null,
            "Project-linked Vision execution evidence was not completed.");
        var firstPackage = viewModel.LatestVisionEvidence!;
        var evidencePath = $"{Path.GetFullPath(projectPath)}.vision-result.json";
        Check("executionEvidenceCorrelatesProjectBuildFrameAndInspection",
            firstPackage.HasValidEvidenceHash()
            && firstPackage.ProjectId == new ProjectDocumentStore()
                .Load(File.ReadAllText(projectPath)).Id
            && firstPackage.BuildIdentity == BuildIdentity.Current
            && firstPackage.CameraId == ready.Id
            && firstPackage.RecipeId == "presence-check"
            && firstPackage.FrameHash == evidence.ContentSha256
            && firstPackage.InspectionId == firstInspection!.InspectionId
            && firstPackage.Events.Any(item => item.Code == "CameraTriggered")
            && firstPackage.Events.Any(item => item.Code == "CameraFrameReady")
            && firstPackage.Events.Any(item => item.Code == "VisionResultReady"));
        Check("executionEvidenceUsesInformationalBuildIdentity",
            BuildIdentity.Current != "0.1.0.0"
            && BuildIdentity.Current.Contains('+', StringComparison.Ordinal));
        Check("executionEvidencePersistsProjectSidecar",
            File.Exists(evidencePath)
            && viewModel.CurrentVisionEvidenceHashText == firstPackage.ShortEvidenceHash
            && viewModel.CurrentCameraInspectionIdText == firstInspection!.InspectionId
            && viewModel.CurrentCameraInspectionMessageText == firstInspection.Message
            && viewModel.CurrentCameraInspectionMetricsText.Contains("PixelCount=", StringComparison.Ordinal));

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras[0] is
            {
                State: VirtualCameraState.Idle,
                AcquisitionOrdinal: 0,
                FrameEvidence: null
            },
            "Reset did not clear camera acquisition evidence.");
        Check("resetClearsInspectionEvidence", true);

        if (!await viewModel.OpenProjectAsync(projectPath))
        {
            throw new InvalidOperationException("Camera commissioning project could not be reopened.");
        }
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.SceneSnapshots.Latest.Cameras[0].State == VirtualCameraState.Idle
                && viewModel.SceneSnapshots.Latest.Cameras[0].FrameEvidence is null,
            "Project reopen restored a runtime camera acquisition.");
        Check("reopenDoesNotRestoreAcquisition", true);
        Check("reopenRestoresRecipe",
            viewModel.SelectedCameraRecipe == "presence-check");
        Check("reopenRestoresImageSource",
            viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
        Check("reopenRestoresMatchingExecutionEvidence",
            viewModel.LatestVisionEvidence?.EvidenceHash == firstPackage.EvidenceHash
            && viewModel.CurrentVisionEvidenceHashText == firstPackage.ShortEvidenceHash);

        viewModel.IsRunMode = true;
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest is
            {
                TickIndex: 0,
                ControlOwner: SimulationControlOwner.Definition
            },
            "Repeat evidence comparison did not reset to the original runtime origin.");
        viewModel.StartManualCameraControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual camera control did not restart for evidence comparison.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Repeat camera control did not pause.");
        viewModel.TriggerCameraCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras[0].State == VirtualCameraState.Exposing,
            "Repeat camera trigger did not start.");
        for (var index = 0; index < 10; index++)
        {
            await StepAsync();
        }
        await WaitForAsync(
            () => viewModel.VisionEvidenceComparison is not null,
            "Repeat Vision execution was not compared with restored evidence.");
        var repeatedPackage = viewModel.LatestVisionEvidence!;
        Check("repeatExecutionBuildMatches", repeatedPackage.BuildIdentity == firstPackage.BuildIdentity);
        Check("repeatExecutionProjectMatches", repeatedPackage.ProjectHash == firstPackage.ProjectHash);
        Check("repeatExecutionFrameMatches", repeatedPackage.FrameHash == firstPackage.FrameHash);
        Check("repeatExecutionReportsFirstMismatch",
            viewModel.VisionEvidenceComparison is
            {
                IsMatch: false,
                MismatchCode: "InspectionMismatch"
            }
            && repeatedPackage.InspectionId != firstPackage.InspectionId
            && repeatedPackage.EvidenceHash != firstPackage.EvidenceHash);

        var appliedWidth = viewModel.CameraImageSourceEditor.Width;
        viewModel.CameraImageSourceEditor.Width = appliedWidth + 1;
        viewModel.CameraImageSourceEditor.ApplyCommand.Execute(null);
        Check("projectChangeMarksExecutionEvidenceStale",
            viewModel.VisionEvidenceStatusText == OpenVisionLanguageService.T("Camera.EvidenceStale"));
        viewModel.CameraImageSourceEditor.Width = appliedWidth;
        viewModel.CameraImageSourceEditor.ApplyCommand.Execute(null);
        Check("restoredProjectContextRevalidatesExecutionEvidence",
            viewModel.VisionEvidenceStatusText == OpenVisionLanguageService.T("Camera.EvidenceSaved"));

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        var originalHint = viewModel.CameraCommissioningHintText;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesCameraHint",
            viewModel.CameraCommissioningHintText != originalHint);
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);

        await ScrollCameraCommissioningIntoViewAsync(window);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task ApplyCameraCommissioningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-camera-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task StepAsync()
        {
            var before = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.TickIndex == before + 1,
                "Camera smoke Step did not advance one tick.");
        }

        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras.Count > 0,
            "No virtual camera was published for the smoke state.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.Cameras[0].State == VirtualCameraState.Idle,
            "Camera smoke state could not reset the runtime.");

        if (state == "source-invalid")
        {
            viewModel.CameraImageSourceEditor.PixelFormatText = string.Empty;
        }
        else if (state is "source-focus" or "source-hover-apply" or "source-pressed-apply")
        {
            viewModel.CameraImageSourceEditor.PixelFormatText = "Mono8";
            viewModel.CameraImageSourceEditor.Width += 1;
        }

        bool needsManual = state is "manual" or "exposing" or "transferring" or "frame-ready"
            or "focus-trigger" or "hover-trigger" or "pressed-trigger";
        if (needsManual)
        {
            viewModel.StartManualCameraControlCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual camera control did not start for the smoke state.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual camera control did not pause.");
        }

        int requestedSteps = state switch
        {
            "transferring" => 4,
            "frame-ready" => 10,
            _ => 0
        };
        if (state is "exposing" or "transferring" or "frame-ready")
        {
            viewModel.TriggerCameraCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.Cameras[0].State == VirtualCameraState.Exposing,
                "Camera did not enter Exposing for the smoke state.");
            for (var index = 0; index < requestedSteps; index++)
            {
                await StepAsync();
            }
        }

        await ScrollCameraCommissioningIntoViewAsync(window);
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        if (state == "frame-ready")
        {
            inspector.CameraExecutionEvidenceDetailsTextBlock.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }
        if (state == "focus-start")
        {
            inspector.StartCameraManualControlButton.Focus();
        }
        else if (state is "hover-start" or "pressed-start")
        {
            MovePointerToCenter(inspector.StartCameraManualControlButton);
            if (state == "pressed-start")
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state == "focus-trigger")
        {
            inspector.TriggerCameraButton.Focus();
        }
        else if (state is "hover-trigger" or "pressed-trigger")
        {
            MovePointerToCenter(inspector.TriggerCameraButton);
            if (state == "pressed-trigger")
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state is "popup-camera" or "popup-recipe")
        {
            var comboBox = state == "popup-camera"
                ? inspector.CameraSelectionComboBox
                : inspector.CameraRecipeComboBox;
            window.Activate();
            comboBox.Focus();
            comboBox.ApplyTemplate();
            comboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!comboBox.IsDropDownOpen)
            {
                throw new InvalidOperationException("Camera commissioning popup did not open.");
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
                ?? throw new InvalidOperationException(
                    "Camera commissioning popup content was unavailable.");
        }
        else if (state is "source-focus" or "source-invalid")
        {
            window.Activate();
            inspector.CameraSourcePixelFormatTextBox.Focus();
        }
        else if (state is "source-hover-browse" or "source-pressed-browse")
        {
            MovePointerToCenter(inspector.BrowseCameraSourceButton);
            if (state == "source-pressed-browse")
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state is "source-hover-apply" or "source-pressed-apply")
        {
            MovePointerToCenter(inspector.ApplyCameraSourceButton);
            if (state == "source-pressed-apply")
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state is not "ready" and not "manual" and not "exposing"
                 and not "transferring" and not "frame-ready"
                 and not "source-hover-browse" and not "source-pressed-browse")
        {
            throw new ArgumentException(
                $"Unsupported --smoke-camera-commissioning-state '{state}'.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static async Task ScrollCameraCommissioningIntoViewAsync(ShellWindow window)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.CameraSectionAnchor.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task ApplyDigitalIoCommissioningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-io-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"I/O action '{action}' was not logged.");
        }

        var commissioning = viewModel.DigitalIo;
        await WaitForAsync(
            () => commissioning.HasSignals,
            "No digital I/O signals were published for the commissioning smoke state.");
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        bool isStartButtonState = state.Equals("focus-start", StringComparison.OrdinalIgnoreCase)
            || state.Equals("hover-start", StringComparison.OrdinalIgnoreCase)
            || state.Equals("pressed-start", StringComparison.OrdinalIgnoreCase);
        bool isOutputState = state.Equals("output-disabled", StringComparison.OrdinalIgnoreCase);

        commissioning.SelectedSignal = isOutputState
            ? commissioning.Signals.FirstOrDefault(signal => !signal.IsInput)
            : commissioning.Signals.FirstOrDefault(signal => signal.IsInput);
        if (commissioning.SelectedSignal is null)
        {
            throw new InvalidOperationException(
                isOutputState
                    ? "No digital output was available for the smoke state."
                    : "No digital input was available for the smoke state.");
        }

        if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
            && !isStartButtonState
            && !isOutputState)
        {
            await ExecuteAndWaitAsync(
                commissioning.StartManualControlCommand,
                "Io.ActionStartManual");
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual I/O control did not start for the smoke state.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual I/O control did not pause.");
        }

        if (state.Equals("focus-start", StringComparison.OrdinalIgnoreCase))
        {
            inspector.StartDigitalIoManualControlButton.Focus();
        }
        else if (state.Equals("hover-start", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-start", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.StartDigitalIoManualControlButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state.Equals("forced-on", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(commissioning.ForceOnCommand, "Io.ActionForceOn");
            await WaitForAsync(
                () => commissioning.SelectedSignal?.OverrideValue == true,
                "Forced-ON I/O state was not published.");
        }
        else if (state.Equals("forced-off", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(commissioning.ForceOffCommand, "Io.ActionForceOff");
            await WaitForAsync(
                () => commissioning.SelectedSignal?.OverrideValue == false,
                "Forced-OFF I/O state was not published.");
        }
        else if (state.Equals("cleared", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(commissioning.ForceOnCommand, "Io.ActionForceOn");
            await ExecuteAndWaitAsync(commissioning.ClearForceCommand, "Io.ActionClearForce");
            await WaitForAsync(
                () => commissioning.SelectedSignal?.OverrideValue is null,
                "Cleared I/O force state was not published.");
        }
        else if (state.Equals("focus-on", StringComparison.OrdinalIgnoreCase))
        {
            inspector.DigitalIoForceOnButton.Focus();
        }
        else if (state.Equals("hover-off", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-on", StringComparison.OrdinalIgnoreCase))
        {
            var button = state.StartsWith("hover", StringComparison.OrdinalIgnoreCase)
                ? inspector.DigitalIoForceOffButton
                : inspector.DigitalIoForceOnButton;
            MovePointerToCenter(button);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
                 && !state.Equals("manual", StringComparison.OrdinalIgnoreCase)
                 && !isOutputState)
        {
            throw new ArgumentException(
                $"Unsupported --smoke-io-commissioning-state '{state}'. Expected ready, manual, " +
                "forced-on, forced-off, cleared, output-disabled, focus-start, hover-start, " +
                "pressed-start, focus-on, hover-off, or pressed-on.");
        }

        await ScrollDigitalIoCommissioningIntoViewAsync(window);
        await Task.Delay(150);
    }

    private static async Task ScrollDigitalIoCommissioningIntoViewAsync(ShellWindow window)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.DigitalIoSectionAnchor.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task ScrollFaultManagerIntoViewAsync(
        ShellWindow window,
        bool activeSection)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var target = activeSection
            ? (FrameworkElement)inspector.FaultOperationStatusText
            : inspector.FaultManagerSectionAnchor;
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = target.TranslatePoint(new Point(), scrollViewer);
        var targetViewportY = activeSection
            ? scrollViewer.ViewportHeight - target.ActualHeight - 12
            : 8;
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset + targetPosition.Y - targetViewportY);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseAxisCommissioningAsync(
        ShellWindow window,
        MainViewModel viewModel)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-axis-commissioning-report requires --smoke-run-layout.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Axes.Count > 0,
            "Axis snapshot was unavailable.");
        await ScrollAxisCommissioningIntoViewAsync(window);
        var initialAxis = viewModel.SceneSnapshots.Latest!.Axes[0];
        Check("axisControlsVisible", inspector.SelectedEquipmentRuntimeCard.IsVisible &&
            inspector.ManualCommissioningPanel.IsVisible &&
            inspector.AxisCommissioningPanel.IsVisible &&
            !inspector.SensorCommissioningPanel.IsVisible &&
            !inspector.CylinderCommissioningPanel.IsVisible &&
            !inspector.ConveyorCommissioningPanel.IsVisible &&
            inspector.StartManualEquipmentControlButton.IsVisible &&
            inspector.AxisTargetPositionTextBox.IsVisible &&
            inspector.MoveAxisAbsoluteButton.IsVisible &&
            inspector.AxisRelativeDistanceTextBox.IsVisible &&
            inspector.MoveAxisRelativeButton.IsVisible &&
            inspector.AxisCommandVelocityTextBox.IsVisible &&
            inspector.AxisDriveTuningText.IsVisible &&
            inspector.MoveAxisVelocityButton.IsVisible &&
            inspector.AxisFollowingErrorText.IsVisible &&
            inspector.AxisDriveAlarmStatusText.IsVisible &&
            inspector.HomeAxisButton.IsVisible &&
            inspector.JogNegativeButton.IsVisible &&
            inspector.JogPositiveButton.IsVisible &&
            inspector.StopAxisMotionButton.IsVisible);
        Check("manualStartAvailableWhilePaused", viewModel.StartManualEquipmentControlCommand.CanExecute(null));
        Check("motionCommandsDisabledBeforeManualStart", !viewModel.CanJogAxis &&
            !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            !viewModel.HomeAxisCommand.CanExecute(null) &&
            !viewModel.StopAxisMotionCommand.CanExecute(null));
        Check("targetInputInitialized", !string.IsNullOrWhiteSpace(viewModel.AxisTargetPositionText) &&
            string.Equals(
                inspector.AxisTargetPositionTextBox.Text,
                viewModel.AxisTargetPositionText,
                StringComparison.Ordinal));
        Check("relativeDistanceInitialized", viewModel.AxisRelativeDistanceText == "10.000" &&
            string.Equals(
                inspector.AxisRelativeDistanceTextBox.Text,
                viewModel.AxisRelativeDistanceText,
                StringComparison.Ordinal));
        Check("velocityInputInitialized", viewModel.AxisCommandVelocityText == "50.000" &&
            string.Equals(
                inspector.AxisCommandVelocityTextBox.Text,
                viewModel.AxisCommandVelocityText,
                StringComparison.Ordinal));
        Check("driveAlarmTelemetryReady", !viewModel.IsCurrentAxisDriveAlarmActive &&
            viewModel.SceneSnapshots.Latest!.Axes[0].FollowingError == 0 &&
            viewModel.SceneSnapshots.Latest.Axes[0].FollowingErrorLimit > 0 &&
            inspector.AxisFollowingErrorText.Text == viewModel.CurrentAxisFollowingErrorText &&
            inspector.AxisDriveAlarmStatusText.Text == viewModel.CurrentAxisDriveAlarmText);
        Check("authoredDriveTuningPublished", initialAxis.MaximumVelocity == 180 &&
            initialAxis.Acceleration == 600 &&
            initialAxis.Deceleration == 600 &&
            initialAxis.FollowingErrorLimit == 0.05 &&
            inspector.AxisDriveTuningText.Text == viewModel.CurrentAxisDriveTuningText &&
            !string.IsNullOrWhiteSpace(inspector.AxisDriveTuningText.Text));

        viewModel.AxisTargetPositionText = "not-a-number";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("invalidTargetDisablesMove", viewModel.HasAxisTargetPositionError &&
            !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            inspector.AxisTargetValidationText.IsVisible);
        viewModel.AxisTargetPositionText = "301";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("outOfRangeTargetDisablesMove", viewModel.HasAxisTargetPositionError &&
            !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            !string.IsNullOrWhiteSpace(viewModel.AxisTargetPositionValidationText));
        viewModel.AxisTargetPositionText = "40";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("validTargetAcceptedBeforeManualStart", viewModel.IsAxisTargetPositionValid &&
            !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            !inspector.AxisTargetValidationText.IsVisible);
        viewModel.AxisRelativeDistanceText = "NaN";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("nonFiniteRelativeDistanceDisablesMove", viewModel.HasAxisRelativeDistanceError &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null));
        viewModel.AxisRelativeDistanceText = "0";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("zeroRelativeDistanceDisablesMove", viewModel.HasAxisRelativeDistanceError &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            inspector.AxisRelativeDistanceValidationText.IsVisible);
        viewModel.AxisRelativeDistanceText = "15";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("signedRelativeDistanceAcceptedBeforeManualStart", viewModel.IsAxisRelativeDistanceValid &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            !inspector.AxisRelativeDistanceValidationText.IsVisible);
        viewModel.AxisCommandVelocityText = "NaN";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("nonFiniteVelocityDisablesMove", viewModel.HasAxisCommandVelocityError &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null));
        viewModel.AxisCommandVelocityText = "0";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("zeroVelocityDisablesMove", viewModel.HasAxisCommandVelocityError &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            inspector.AxisCommandVelocityValidationText.IsVisible);
        viewModel.AxisCommandVelocityText = "181";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("overAuthoredVelocityDisablesMove", viewModel.HasAxisCommandVelocityError &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            !string.IsNullOrWhiteSpace(viewModel.AxisCommandVelocityValidationText));
        viewModel.AxisCommandVelocityText = "50";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("signedVelocityAcceptedBeforeManualStart", viewModel.IsAxisCommandVelocityValid &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            !inspector.AxisCommandVelocityValidationText.IsVisible);

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning &&
                viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual axis control did not start.");
        Check("manualOwnerPublished", viewModel.SceneSnapshots.Latest?.ControlOwner ==
            SimulationControlOwner.Manual);
        Check("manualMotionEnabled", viewModel.CanJogAxis &&
            viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            viewModel.HomeAxisCommand.CanExecute(null));

        viewModel.MoveAxisAbsoluteCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Moving &&
                viewModel.SceneSnapshots.Latest.Axes[0].Position > 0.01,
            "Absolute move did not start toward the entered target.");
        Check("absoluteMoveLogged", viewModel.LogMessages.Any(line =>
            line.Contains(OpenVisionLanguageService.T("Axis.ActionMove"), StringComparison.CurrentCulture)));
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Absolute move did not pause.");
        var pausedTick = viewModel.SceneSnapshots.Latest!.TickIndex;
        var pausedPosition = viewModel.SceneSnapshots.Latest.Axes[0].Position;
        await Task.Delay(150);
        Check("pauseFreezesAbsoluteMove", viewModel.SceneSnapshots.Latest!.TickIndex == pausedTick &&
            Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - pausedPosition) < 1e-9);
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > pausedTick,
            "Absolute move Step did not advance.");
        Check("absoluteMoveStepAdvancesOneTick",
            viewModel.SceneSnapshots.Latest!.TickIndex == pausedTick + 1 &&
            viewModel.SceneSnapshots.Latest.Axes[0].Position > pausedPosition);
        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning &&
                viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - 40) < 1e-6,
            "Absolute move did not reach the entered target after resume.");
        Check("absoluteMoveReachesTarget", true);

        await WaitForAsync(
            () => viewModel.MoveAxisRelativeCommand.CanExecute(null),
            "Relative move did not become available after the absolute move.");
        viewModel.MoveAxisRelativeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Moving &&
                viewModel.SceneSnapshots.Latest.Axes[0].Position > 40.01,
            "Positive relative move did not start from the current position.");
        Check("relativeMoveLogged", viewModel.LogMessages.Any(line =>
            line.Contains(OpenVisionLanguageService.T("Axis.ActionMoveRelative"), StringComparison.CurrentCulture)));
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Relative move did not pause.");
        var relativePausedTick = viewModel.SceneSnapshots.Latest!.TickIndex;
        var relativePausedPosition = viewModel.SceneSnapshots.Latest.Axes[0].Position;
        await Task.Delay(150);
        Check("pauseFreezesRelativeMove", viewModel.SceneSnapshots.Latest!.TickIndex == relativePausedTick &&
            Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - relativePausedPosition) < 1e-9);
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > relativePausedTick,
            "Relative move Step did not advance.");
        Check("relativeMoveStepAdvancesOneTick",
            viewModel.SceneSnapshots.Latest!.TickIndex == relativePausedTick + 1 &&
            viewModel.SceneSnapshots.Latest.Axes[0].Position > relativePausedPosition);
        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning &&
                viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - 55) < 1e-6,
            "Positive relative move did not reach 55 mm after resume.");
        Check("positiveRelativeMoveUsesCurrentPosition", true);

        viewModel.AxisRelativeDistanceText = "-5";
        await WaitForAsync(
            () => viewModel.MoveAxisRelativeCommand.CanExecute(null),
            "Negative relative move did not become available after the positive move.");
        viewModel.MoveAxisRelativeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - 50) < 1e-6,
            "Negative relative move did not reach 50 mm.");
        Check("negativeRelativeMoveUsesCurrentPosition", true);

        viewModel.AxisRelativeDistanceText = "300";
        await WaitForAsync(
            () => viewModel.MoveAxisRelativeCommand.CanExecute(null),
            "Out-of-range relative move did not become available for engine validation.");
        var rejectedRelativeLogCount = viewModel.LogMessages.Count(line =>
            line.Contains(OpenVisionLanguageService.T("Axis.ActionMoveRelative"), StringComparison.CurrentCulture));
        viewModel.MoveAxisRelativeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.LogMessages.Count(line =>
                line.Contains(OpenVisionLanguageService.T("Axis.ActionMoveRelative"), StringComparison.CurrentCulture)) >
                rejectedRelativeLogCount,
            "Out-of-range relative move was not logged.");
        Check("relativeSoftLimitRejectedByEngine",
            Math.Abs(viewModel.SceneSnapshots.Latest!.Axes[0].Position - 50) < 1e-6 &&
            viewModel.LogMessages.Any(line => line.Contains(
                nameof(SimulationCommandErrorCode.AxisTargetOutOfRange),
                StringComparison.Ordinal)));

        viewModel.AxisCommandVelocityText = "50";
        viewModel.MoveAxisVelocityCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Moving &&
                viewModel.SceneSnapshots.Latest.Axes[0].Velocity > 0,
            "Positive velocity move did not start.");
        Check("velocityMoveLogged", viewModel.LogMessages.Any(line =>
            line.Contains(OpenVisionLanguageService.T("Axis.ActionMoveVelocity"), StringComparison.CurrentCulture)));
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Velocity move did not pause.");
        var velocityPausedTick = viewModel.SceneSnapshots.Latest!.TickIndex;
        var velocityPausedPosition = viewModel.SceneSnapshots.Latest.Axes[0].Position;
        await Task.Delay(150);
        Check("pauseFreezesVelocityMove", viewModel.SceneSnapshots.Latest.TickIndex == velocityPausedTick &&
            Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - velocityPausedPosition) < 1e-9);
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > velocityPausedTick,
            "Velocity move Step did not advance.");
        Check("velocityMoveStepAdvancesOneTick",
            viewModel.SceneSnapshots.Latest!.TickIndex == velocityPausedTick + 1 &&
            viewModel.SceneSnapshots.Latest.Axes[0].Position > velocityPausedPosition);
        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning && viewModel.StopAxisMotionCommand.CanExecute(null),
            "Velocity move did not resume for Stop.");
        viewModel.StopAxisMotionCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Stopped,
            "Velocity Stop did not publish Stopped.");
        var velocityStoppedPosition = viewModel.SceneSnapshots.Latest!.Axes[0].Position;
        await Task.Delay(150);
        Check("velocityStopFreezesPosition", Math.Abs(
            viewModel.SceneSnapshots.Latest!.Axes[0].Position - velocityStoppedPosition) < 1e-9);

        viewModel.AxisCommandVelocityText = "-100";
        await WaitForAsync(
            () => viewModel.MoveAxisVelocityCommand.CanExecute(null),
            "Negative velocity move did not become available after Stop.");
        viewModel.MoveAxisVelocityCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Limited &&
                Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position) < 1e-9,
            "Negative velocity move did not reach the authored soft limit.");
        Check("velocityMoveReachesSignedSoftLimit", Math.Abs(
            viewModel.SceneSnapshots.Latest!.Axes[0].Velocity) < 1e-9);

        viewModel.AxisCommandVelocityText = "5";
        await WaitForAsync(
            () => viewModel.MoveAxisVelocityCommand.CanExecute(null),
            "Following-error setup motion did not become available.");
        viewModel.MoveAxisVelocityCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Moving &&
                viewModel.SceneSnapshots.Latest.Axes[0].Velocity > 0,
            "Following-error setup motion did not start.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Following-error setup motion did not pause.");
        var faultManager = viewModel.FaultManager;
        faultManager.SelectedKind = faultManager.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.AxisFollowingError);
        faultManager.SelectedTarget = faultManager.Targets.Single(target =>
            string.Equals(target.Id, viewModel.SceneSnapshots.Latest!.Axes[0].Id, StringComparison.Ordinal));
        faultManager.InjectCommand.Execute(null);
        await WaitForAsync(
            () => !faultManager.IsOperationPending &&
                viewModel.SceneSnapshots.Latest!.Faults.Any(fault =>
                    fault.Kind == SimulationFaultKind.AxisFollowingError),
            "Following-error fault did not activate.");
        var faultPausedTick = viewModel.SceneSnapshots.Latest!.TickIndex;
        var faultPausedPosition = viewModel.SceneSnapshots.Latest.Axes[0].Position;
        await Task.Delay(150);
        Check("pauseFreezesFollowingError", viewModel.SceneSnapshots.Latest.TickIndex == faultPausedTick &&
            Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - faultPausedPosition) < 1e-9 &&
            !viewModel.SceneSnapshots.Latest.Axes[0].DriveAlarmActive);

        var singleTickSteps = true;
        for (var step = 0; step < 10 && !viewModel.SceneSnapshots.Latest!.Axes[0].DriveAlarmActive; step++)
        {
            var beforeStep = viewModel.SceneSnapshots.Latest.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep,
                "Following-error Step did not advance.");
            singleTickSteps &= viewModel.SceneSnapshots.Latest.TickIndex == beforeStep + 1;
        }
        await WaitForAsync(
            () => viewModel.IsCurrentAxisDriveAlarmActive,
            "Following error did not latch the drive alarm.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var alarmedAxis = viewModel.SceneSnapshots.Latest!.Axes[0];
        Check("followingErrorTripsAtConfiguredLimit", singleTickSteps &&
            alarmedAxis.State == AxisState.Error &&
            Math.Abs(alarmedAxis.FollowingError) >= alarmedAxis.FollowingErrorLimit &&
            Math.Abs(alarmedAxis.Position - faultPausedPosition) < 1e-9 &&
            !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null));
        Check("driveAlarmSnapshotVisible", inspector.AxisDriveAlarmStatusText.Text ==
            OpenVisionLanguageService.T("Axis.DriveAlarmActive") &&
            inspector.AxisFollowingErrorText.Text == viewModel.CurrentAxisFollowingErrorText);
        Check("driveAlarmEventLogged", viewModel.LogMessages.Any(line =>
            line.Contains("AxisDriveAlarmActivated", StringComparison.Ordinal) ||
            line.Contains("following error", StringComparison.OrdinalIgnoreCase)));

        await WaitForAsync(
            () => faultManager.ActiveFaults.Any(fault =>
                fault.Kind == SimulationFaultKind.AxisFollowingError),
            "Following-error active fault was not listed.");
        faultManager.SelectedActiveFault = faultManager.ActiveFaults.Single(fault =>
            fault.Kind == SimulationFaultKind.AxisFollowingError);
        faultManager.ClearSelectedCommand.Execute(null);
        await WaitForAsync(
            () => !faultManager.IsOperationPending &&
                viewModel.SceneSnapshots.Latest!.Faults.All(fault =>
                    fault.Kind != SimulationFaultKind.AxisFollowingError) &&
                viewModel.SceneSnapshots.Latest.Axes[0].State == AxisState.Stopped &&
                !viewModel.SceneSnapshots.Latest.Axes[0].DriveAlarmActive,
            "Explicit following-error Clear did not recover the axis.");
        Check("driveAlarmClearRecoversStopped", viewModel.SceneSnapshots.Latest!.Axes[0].FollowingError == 0 &&
            viewModel.CurrentAxisDriveAlarmText == OpenVisionLanguageService.T("Axis.DriveAlarmReady"));

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning && viewModel.CanJogAxis,
            "Manual control did not resume after drive-alarm recovery.");

        var startPosition = viewModel.SceneSnapshots.Latest!.Axes[0].Position;
        if (!viewModel.BeginAxisJog(AxisJogDirection.Positive))
        {
            throw new InvalidOperationException("Jog+ did not start after relative-move validation.");
        }
        try
        {
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].Position > startPosition + 0.05,
                "Jog+ did not advance the axis.");
            Check("jogMoves", viewModel.SceneSnapshots.Latest!.Axes[0].Velocity > 0);
        }
        finally
        {
            await viewModel.EndAxisJogAsync();
        }

        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Stopped &&
                Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Velocity) < 1e-9,
            "Releasing Jog+ did not stop the axis.");
        var stoppedPosition = viewModel.SceneSnapshots.Latest!.Axes[0].Position;
        await Task.Delay(150);
        Check("jogReleaseFreezesPosition", Math.Abs(
            viewModel.SceneSnapshots.Latest!.Axes[0].Position - stoppedPosition) < 1e-9);
        Check("jogAndStopLogged", viewModel.LogMessages.Any(line =>
                line.Contains(OpenVisionLanguageService.T("Axis.ActionJogPositive"), StringComparison.CurrentCulture)) &&
            viewModel.LogMessages.Any(line =>
                line.Contains(OpenVisionLanguageService.T("Axis.ActionStop"), StringComparison.CurrentCulture)));

        Check("homeAvailableAfterStop", viewModel.HomeAxisCommand.CanExecute(null));
        viewModel.HomeAxisCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position) < 1e-9,
            "Home did not restore the authored home position.");
        Check("homeRestoresAuthoredPosition", Math.Abs(
            viewModel.SceneSnapshots.Latest!.Axes[0].Position) < 1e-9);
        Check("homeLogged", viewModel.LogMessages.Any(line =>
            line.Contains(OpenVisionLanguageService.T("Axis.ActionHome"), StringComparison.CurrentCulture)));

        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual control did not pause.");
        Check("manualResumeAvailable", viewModel.StartManualEquipmentControlCommand.CanExecute(null));

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(() => viewModel.IsRunning, "Manual control did not resume.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsRunning &&
                viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition,
            "Reset did not restore Definition ownership.");
        Check("resetRestoresDefinition", viewModel.SceneSnapshots.Latest?.RunMode ==
            SimulationRunMode.Paused && Math.Abs(
                viewModel.SceneSnapshots.Latest.Axes[0].Position) < 1e-9);
        Check("resetPreservesRelativeDistanceInput", viewModel.AxisRelativeDistanceText == "300");
        Check("resetPreservesVelocityInput", viewModel.AxisCommandVelocityText == "5");

        viewModel.RunCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning &&
                viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.EmbeddedSequence,
            "Automatic sequence ownership did not start.");
        Check("automaticOwnerBlocksManualStart", !viewModel.StartManualEquipmentControlCommand.CanExecute(null) &&
            !viewModel.CanJogAxis && !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null));
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Final Reset did not pause the runtime.");

        faultManager.SelectedKind = faultManager.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.AxisMotionBlocked);
        faultManager.SelectedTarget = faultManager.Targets.Single(target =>
            string.Equals(target.Id, viewModel.SceneSnapshots.Latest!.Axes[0].Id, StringComparison.Ordinal));
        Check("axisInterlockTargetAvailable", faultManager.InjectCommand.CanExecute(null));
        faultManager.InjectCommand.Execute(null);
        await WaitForAsync(
            () => !faultManager.IsOperationPending &&
                viewModel.SceneSnapshots.Latest!.Faults.Any(fault =>
                    fault.Kind == SimulationFaultKind.AxisMotionBlocked) &&
                viewModel.SceneSnapshots.Latest.Axes[0].State == AxisState.Error,
            "Blocked-axis fault did not publish its snapshot state.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("axisInterlockSnapshotVisible", viewModel.IsCurrentAxisInterlocked &&
            viewModel.CurrentAxisInterlockText == OpenVisionLanguageService.T("Axis.InterlockBlocked"));
        Check("axisInterlockDisablesMotion", !viewModel.StartManualEquipmentControlCommand.CanExecute(null) &&
            !viewModel.CanJogAxis && !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            !viewModel.HomeAxisCommand.CanExecute(null));
        Check("axisInterlockEvidenceVisible", inspector.AxisInterlockStatusText.IsVisible &&
            string.Equals(
                inspector.AxisInterlockStatusText.Text,
                OpenVisionLanguageService.T("Axis.InterlockBlocked"),
                StringComparison.CurrentCulture));

        faultManager.ClearSelectedCommand.Execute(null);
        await WaitForAsync(
            () => !faultManager.IsOperationPending &&
                viewModel.SceneSnapshots.Latest!.Faults.All(fault =>
                    fault.Kind != SimulationFaultKind.AxisMotionBlocked) &&
                viewModel.SceneSnapshots.Latest.Axes[0].State == AxisState.Stopped,
            "Clearing blocked-axis fault did not recover the runtime axis.");
        Check("axisInterlockClearRecovers", !viewModel.IsCurrentAxisInterlocked &&
            viewModel.StartManualEquipmentControlCommand.CanExecute(null));

        var selectedStage = viewModel.Layout.SelectedItem
            ?? throw new InvalidOperationException("The selected stage was unavailable.");
        string authoredAxisId = selectedStage.BehaviorBindingId
            ?? throw new InvalidOperationException("The selected stage did not have an axis binding.");
        selectedStage.Component!.BehaviorBindingId = "missing-smoke-axis";
        viewModel.Layout.Select("sensor-1");
        viewModel.Layout.Select(selectedStage.Id);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("unresolvedSelectedStageBindingFailsClosed",
            !viewModel.StartManualEquipmentControlCommand.CanExecute(null)
            && viewModel.CurrentAxisName == OpenVisionLanguageService.T("Shell.NoAxis"));
        selectedStage.Component.BehaviorBindingId = authoredAxisId;
        viewModel.Layout.Select("sensor-1");
        viewModel.Layout.Select(selectedStage.Id);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("selectedStageBindingRecoveryRestoresAxis",
            viewModel.CurrentAxisName != OpenVisionLanguageService.T("Shell.NoAxis"));

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesAxisHint", viewModel.AxisCommissioningHintText ==
            OpenVisionLanguageService.T("Axis.VelocityMoveStartManualHint"));
        Check("languageSwitchRefreshesDriveAlarm", viewModel.CurrentAxisDriveAlarmText ==
            OpenVisionLanguageService.T("Axis.DriveAlarmReady"));
        viewModel.AxisTargetPositionText = "invalid";
        Check("languageSwitchRefreshesTargetValidation",
            viewModel.AxisTargetPositionValidationText == OpenVisionLanguageService.T("Axis.TargetInvalid"));
        viewModel.AxisTargetPositionText = "40";
        viewModel.AxisRelativeDistanceText = "invalid";
        Check("languageSwitchRefreshesRelativeValidation",
            viewModel.AxisRelativeDistanceValidationText == OpenVisionLanguageService.T("Axis.RelativeInvalid"));
        viewModel.AxisRelativeDistanceText = "10";
        viewModel.AxisCommandVelocityText = "invalid";
        Check("languageSwitchRefreshesVelocityValidation",
            viewModel.AxisCommandVelocityValidationText == OpenVisionLanguageService.T("Axis.VelocityInvalid"));
        viewModel.AxisCommandVelocityText = "50";
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewModel.IsRunMode = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("designModeDisablesAxisCommissioning", !viewModel.StartManualEquipmentControlCommand.CanExecute(null) &&
            !viewModel.CanJogAxis && !viewModel.MoveAxisAbsoluteCommand.CanExecute(null) &&
            !viewModel.MoveAxisRelativeCommand.CanExecute(null) &&
            !viewModel.MoveAxisVelocityCommand.CanExecute(null) &&
            !viewModel.HomeAxisCommand.CanExecute(null));
        viewModel.IsRunMode = true;
        await ScrollAxisCommissioningIntoViewAsync(window);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseMultiAxisCommissioningRecipeAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? savePath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            throw new InvalidOperationException(failureMessage);
        }

        var recipe = viewModel.MultiAxisCommissioningRecipe;
        Check("recipeConfigured", recipe.IsConfigured);
        Check("recipeValid", recipe.IsValid);
        Check("orderedTargets", recipe.Targets.Select(target => target.AxisId)
            .SequenceEqual(new[] { "y", "x" }, StringComparer.Ordinal));
        Check("loadedWithoutExecution", viewModel.IsDesignMode
            && !viewModel.IsRunning
            && viewModel.SceneSnapshots.Latest?.TickIndex == 0
            && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition);

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            await viewModel.SaveProjectAsync(savePath);
            Check("savedRecipeOrder", new ProjectDocumentStore().Load(File.ReadAllText(savePath))
                .MultiAxisCommissioningRecipe?.Targets.Select(target => target.AxisId)
                .SequenceEqual(new[] { "y", "x" }, StringComparer.Ordinal) == true);
            Check("reopenAccepted", await viewModel.OpenProjectAsync(savePath));
            Check("reopenDoesNotExecute", viewModel.IsDesignMode
                && !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.SceneSnapshots.Latest.Axes.All(axis => axis.State == AxisState.Idle));
            Check("reopenPreservesTargets", viewModel.MultiAxisCommissioningRecipe.Targets
                .Select(target => $"{target.AxisId}:{target.TargetPosition:F3}")
                .SequenceEqual(new[] { "y:120.000", "x:240.000" }, StringComparer.Ordinal)
                && viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions == 3);
            Check("reopenPreservesDistinctAxisLayout",
                viewModel.Layout.Items.Single(item => item.Id == "x").Position.Y == 200
                && viewModel.Layout.Items.Single(item => item.Id == "y").Position.Y == 400);
        }

        viewModel.IsRunMode = true;
        var scene = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene was unavailable.");
        var bottomRailY = Math.Clamp(
            scene.ActualHeight * 0.62,
            Math.Min(160, Math.Max(0, scene.ActualHeight - 90)),
            Math.Max(0, scene.ActualHeight - 90));
        Check("xAxisSelectableOnDistinctRail",
            scene.SelectItemAt(new Point(72, bottomRailY - 96))
            && viewModel.Layout.SelectedItem?.Id == "x");
        Check("yAxisSelectableOnDistinctRail",
            scene.SelectItemAt(new Point(72, bottomRailY))
            && viewModel.Layout.SelectedItem?.Id == "y");
        Check("runCommandAvailable", viewModel.RunMultiAxisCommissioningRecipeCommand.CanExecute(null));
        viewModel.RunMultiAxisCommissioningRecipeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Axes.Any(axis => axis.State == AxisState.Moving) == true,
            "The multi-axis recipe did not start through manual group motion.");
        await WaitForAsync(
            () => viewModel.IsRunning && viewModel.PauseCommand.CanExecute(null),
            "Recipe motion did not enter the running command state.");
        Check("manualOwner", viewModel.SceneSnapshots.Latest!.ControlOwner == SimulationControlOwner.Manual);
        Check("bothAxesMove", viewModel.SceneSnapshots.Latest!.Axes.Count(axis => axis.State == AxisState.Moving) == 2);
        await WaitForAsync(
            () => viewModel.LogMessages.Any(message =>
                message.Contains("Targets: y = 120.000, x = 240.000.", StringComparison.Ordinal)),
            "Ordered recipe move evidence was not published.");
        Check("orderedMoveEvidence", true);

        Check("pauseAvailable", true);
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Recipe motion did not pause.");
        var paused = viewModel.SceneSnapshots.Latest!;
        var pausedPositions = paused.Axes.Select(axis => axis.Position).ToArray();
        await Task.Delay(100);
        Check("pauseFreezesTick", viewModel.SceneSnapshots.Latest!.TickIndex == paused.TickIndex);
        Check("pauseFreezesPositions", pausedPositions.SequenceEqual(
            viewModel.SceneSnapshots.Latest.Axes.Select(axis => axis.Position)));

        Check("stepAvailable", viewModel.StepCommand.CanExecute(null));
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > paused.TickIndex,
            "Recipe Step did not advance.");
        var stepped = viewModel.SceneSnapshots.Latest!;
        Check("stepAdvancesOneTick", stepped.TickIndex == paused.TickIndex + 1);
        Check("stepAdvancesBothAxes", stepped.Axes.Zip(pausedPositions)
            .All(pair => pair.First.Position > pair.Second));

        await WaitForAsync(
            () => viewModel.StopMultiAxisCommissioningRecipeCommand.CanExecute(null),
            "Recipe group stop was unavailable after Step.");
        Check("stopAvailable", true);
        viewModel.StopMultiAxisCommissioningRecipeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes.All(axis => axis.State == AxisState.Stopped),
            "Recipe group stop did not stop every target axis.");
        var stopped = viewModel.SceneSnapshots.Latest!;
        var stoppedPositions = stopped.Axes.Select(axis => axis.Position).ToArray();
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > stopped.TickIndex,
            "Stopped recipe Step did not advance.");
        Check("stopFreezesBothAxes", stoppedPositions.SequenceEqual(
            viewModel.SceneSnapshots.Latest!.Axes.Select(axis => axis.Position)));
        await WaitForAsync(
            () => viewModel.LogMessages.Any(message =>
                message.Contains("Stopped: y = ", StringComparison.Ordinal)
                && message.Contains(", x = ", StringComparison.Ordinal)),
            "Ordered recipe stop evidence was not published.");
        Check("orderedStopEvidence", true);

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest is
            {
                TickIndex: 0,
                ControlOwner: SimulationControlOwner.Definition
            },
            "Recipe Reset did not restore the authored runtime boundary.");
        var reset = viewModel.SceneSnapshots.Latest!;
        Check("resetRestoresAuthoredHome", reset.Axes.All(axis =>
            axis.State == AxisState.Idle && Math.Abs(axis.Position) <= 1e-9));

        Check("repeatValidationAvailable", viewModel.ValidateMultiAxisCommissioningRecipeCommand.CanExecute(null));
        var mainSnapshotBeforeValidation = viewModel.SceneSnapshots.Latest!;
        viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsCommissioningValidationRunning
                && viewModel.LatestCommissioningResult is not null,
            "Recipe repeat validation did not complete.");
        var validation = viewModel.LatestCommissioningResult!;
        Check("repeatValidationPassed", validation.IsSuccess
            && validation.CompletedRuns == viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions
            && validation.Runs.All(run => run.IsMatch));
        Check("repeatEvidenceValid", validation.HasValidEvidenceHash()
            && validation.Runs.Select(run => run.SnapshotHash).Distinct(StringComparer.Ordinal).Count() == 1
            && validation.Runs.Select(run => run.EventHash).Distinct(StringComparer.Ordinal).Count() == 1);
        Check("historyAppended", viewModel.CommissioningResultHistory.Entries.Length == 1
            && viewModel.SelectedCommissioningHistoryEntry?.Sequence == 1);
        Check("baselineAcceptanceAvailable", viewModel.AcceptCommissioningBaselineCommand.CanExecute(null));
        viewModel.AcceptCommissioningBaselineCommand.Execute(null);
        Check("baselineAccepted", viewModel.AcceptedCommissioningBaseline?.HasValidEvidenceHash() == true
            && viewModel.CommissioningBaselineComparison?.IsMatch == true);
        Check("repeatValidationLeavesMainRuntimeUnchanged",
            viewModel.SceneSnapshots.Latest!.TickIndex == mainSnapshotBeforeValidation.TickIndex
            && viewModel.SceneSnapshots.Latest.ControlOwner == mainSnapshotBeforeValidation.ControlOwner
            && viewModel.SceneSnapshots.Latest.Axes.Select(axis => axis.Position)
                .SequenceEqual(mainSnapshotBeforeValidation.Axes.Select(axis => axis.Position)));

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            var evidencePath = $"{Path.GetFullPath(savePath)}.commissioning-result.json";
            var historyPath = $"{Path.GetFullPath(savePath)}.commissioning-history.json";
            var baselinePath = $"{Path.GetFullPath(savePath)}.commissioning-baseline.json";
            Check("repeatEvidenceSaved", File.Exists(evidencePath));
            Check("historyAndBaselineSaved", File.Exists(historyPath)
                && File.Exists(baselinePath)
                && DeterministicMultiAxisCommissioningResultHistory.LoadFromJson(historyPath)
                    is { Entries.Length: 1 } history
                && history.HasValidEvidenceHash()
                && DeterministicMultiAxisCommissioningBaseline.LoadFromJson(baselinePath)
                    is { } baseline
                && baseline.HasValidEvidenceHash());
            Check("repeatEvidenceRoundTrips",
                DeterministicMultiAxisCommissioningResultPackage.LoadFromJson(evidencePath) is
                { IsSuccess: true } saved
                && saved.HasValidEvidenceHash()
                && string.Equals(saved.EvidenceHash, validation.EvidenceHash, StringComparison.Ordinal));
            Check("repeatReopenAccepted", await viewModel.OpenProjectAsync(savePath));
            Check("repeatEvidenceRestoredWithoutExecution", viewModel.HasRestoredCommissioningResult
                && viewModel.LatestCommissioningResult?.EvidenceHash == validation.EvidenceHash
                && viewModel.IsDesignMode
                && !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.SceneSnapshots.Latest.Axes.All(axis => axis.State == AxisState.Idle));
            Check("historyAndBaselineRestoredWithoutExecution",
                viewModel.CommissioningResultHistory.Entries.Length == 1
                && viewModel.AcceptedCommissioningBaseline?.HasValidEvidenceHash() == true
                && viewModel.CommissioningBaselineComparison?.IsMatch == true
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0);
            viewModel.MultiAxisCommissioningRecipe.Targets[0].TargetPosition += 1;
            Check("recipeChangeMarksEvidenceStale", viewModel.RejectedStaleCommissioningResult
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0
                && viewModel.SceneSnapshots.Latest.Axes.All(axis => axis.State == AxisState.Idle));
            viewModel.IsRunMode = true;
            viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
            await WaitForAsync(
                () => !viewModel.IsCommissioningValidationRunning
                    && viewModel.CommissioningResultHistory.Entries.Length == 2,
                "Changed recipe validation did not complete.");
            var mismatch = viewModel.CommissioningBaselineComparison?.FirstMismatch;
            Check("intentionalChangeFindsFirstMismatch", mismatch is not null);
            Check("intentionalMismatchIsOrderedEvent", mismatch?.EvidenceKind == "Event");
            Check("intentionalMismatchTargetsChangedAxis", mismatch?.TargetId == "y");
            Check("intentionalMismatchHasTick", mismatch?.TickIndex >= 0);
            Check("mismatchNavigationAvailable",
                viewModel.NavigateToCommissioningMismatchCommand.CanExecute(null));
            viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
            Check("yMismatchNavigatesToAxisStage",
                viewModel.Layout.SelectedItem?.Id == "y");

            viewModel.MultiAxisCommissioningRecipe.Targets[0].TargetPosition -= 1;
            viewModel.MultiAxisCommissioningRecipe.Targets[1].TargetPosition += 1;
            var secondAxisHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
            viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
            await WaitForAsync(
                () => !viewModel.IsCommissioningValidationRunning
                    && viewModel.CommissioningResultHistory.Entries.Length > secondAxisHistoryCount,
                "Second-axis recipe validation did not complete.");
            var secondAxisMismatch = viewModel.CommissioningBaselineComparison?.FirstMismatch;
            Check("secondAxisMismatchIsOrderedEvent", secondAxisMismatch?.EvidenceKind == "Event");
            Check("secondAxisMismatchTargetsChangedAxis", secondAxisMismatch?.TargetId == "x");
            Check("secondAxisMismatchHasTick", secondAxisMismatch?.TickIndex >= 0);
            Check("secondAxisMismatchNavigationAvailable",
                viewModel.NavigateToCommissioningMismatchCommand.CanExecute(null));
            viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
            Check("xMismatchNavigatesToAxisStage",
                viewModel.Layout.SelectedItem?.Id == "x");
        }

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task ApplyMultiAxisCommissioningRecipeSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was unavailable.");
        switch (state.ToLowerInvariant())
        {
            case "design":
            case "design-focus":
            case "design-popup":
                if (!viewModel.IsDesignMode)
                {
                    throw new InvalidOperationException("Recipe design state requires Design mode.");
                }
                viewModel.ProjectTree.SelectedNode = viewModel.ProjectTree.Roots.Single();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                inspector.MultiAxisRecipeDesignPanel.BringIntoView();
                if (state.Equals("design-focus", StringComparison.OrdinalIgnoreCase))
                {
                    inspector.MultiAxisRecipeNameTextBox.Text = "Pick position smoke";
                    inspector.MultiAxisRecipeNameTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    window.Activate();
                    inspector.MultiAxisRecipeNameTextBox.Focus();
                    Keyboard.Focus(inspector.MultiAxisRecipeNameTextBox);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.MultiAxisRecipeNameTextBox.IsKeyboardFocusWithin
                        || inspector.MultiAxisRecipeNameTextBox.Text != "Pick position smoke")
                    {
                        throw new InvalidOperationException("Recipe name did not render its focused non-empty value.");
                    }
                }
                else if (state.Equals("design-popup", StringComparison.OrdinalIgnoreCase))
                {
                    var comboBox = FindVisualDescendant<ComboBox>(
                        inspector.MultiAxisRecipeDesignPanel,
                        candidate => candidate.IsVisible && candidate.IsEnabled)
                        ?? throw new InvalidOperationException("Recipe axis selector was unavailable.");
                    window.Activate();
                    comboBox.Focus();
                    comboBox.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!comboBox.IsDropDownOpen)
                    {
                        throw new InvalidOperationException("Recipe axis selector popup did not open.");
                    }
                }
                break;
            case "ready":
            case "run-hover":
            case "run-pressed":
            case "validation-focus":
            case "validation-hover":
            case "validation-pressed":
            case "validated":
            case "validating":
            case "history-selected":
            case "baseline-pressed":
            case "baseline-accepted":
            case "baseline-mismatch":
            case "baseline-mismatch-x":
                viewModel.IsRunMode = true;
                inspector.RunInspectorScrollViewer.ScrollToTop();
                inspector.MultiAxisRecipeRunPanel.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!viewModel.RunMultiAxisCommissioningRecipeCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("Recipe Run was unavailable in its ready state.");
                }
                if (state.Equals("validation-focus", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions = 4;
                    window.Activate();
                    inspector.CommissioningValidationRepetitionsTextBox.Focus();
                    Keyboard.Focus(inspector.CommissioningValidationRepetitionsTextBox);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.CommissioningValidationRepetitionsTextBox.IsKeyboardFocusWithin
                        || inspector.CommissioningValidationRepetitionsTextBox.Text != "4")
                    {
                        throw new InvalidOperationException("Commissioning repetitions did not render its focused value.");
                    }
                }
                else if (state.Equals("validation-hover", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("validation-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    window.Activate();
                    MovePointerToCenter(inspector.ValidateMultiAxisRecipeButton);
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.ValidateMultiAxisRecipeButton.IsMouseOver)
                    {
                        throw new InvalidOperationException("Recipe validation did not enter pointer-hover state.");
                    }
                    if (state.Equals("validation-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!inspector.ValidateMultiAxisRecipeButton.IsPressed)
                        {
                            throw new InvalidOperationException("Recipe validation did not enter pointer-down state.");
                        }
                    }
                }
                else if (state.Equals("history-selected", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-pressed", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-accepted", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions = 2;
                    var initialHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
                    viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                    for (var attempt = 0; attempt < 200
                         && (viewModel.IsCommissioningValidationRunning
                             || viewModel.CommissioningResultHistory.Entries.Length <= initialHistoryCount);
                         attempt++)
                    {
                        await Task.Delay(25);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    viewModel.SelectedCommissioningHistoryEntry =
                        viewModel.CommissioningResultHistory.Entries.LastOrDefault();
                    if (viewModel.SelectedCommissioningHistoryEntry is null)
                    {
                        throw new InvalidOperationException("Commissioning history selection was unavailable.");
                    }

                    if (!state.Equals("history-selected", StringComparison.OrdinalIgnoreCase))
                    {
                        if (state.Equals("baseline-pressed", StringComparison.OrdinalIgnoreCase))
                        {
                            inspector.AcceptCommissioningBaselineButton.BringIntoView();
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            window.Activate();
                            MovePointerToCenter(inspector.AcceptCommissioningBaselineButton);
                            await Task.Delay(100);
                            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                            _smokePointerHeld = true;
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            if (!inspector.AcceptCommissioningBaselineButton.IsPressed)
                            {
                                throw new InvalidOperationException("Baseline accept did not enter pointer-down state.");
                            }
                        }
                        else
                        {
                            viewModel.AcceptCommissioningBaselineCommand.Execute(null);
                            if (viewModel.AcceptedCommissioningBaseline is null)
                            {
                                throw new InvalidOperationException("Commissioning baseline was not accepted.");
                            }
                            if (state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                                || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                            {
                                var targetIndex = state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase)
                                    ? 1
                                    : 0;
                                viewModel.MultiAxisCommissioningRecipe.Targets[targetIndex].TargetPosition += 1;
                                var changedHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
                                viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                                for (var attempt = 0; attempt < 200
                                     && (viewModel.IsCommissioningValidationRunning
                                         || viewModel.CommissioningResultHistory.Entries.Length <= changedHistoryCount);
                                     attempt++)
                                {
                                    await Task.Delay(25);
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                }
                                var expectedAxisId = viewModel.MultiAxisCommissioningRecipe.Targets[targetIndex].AxisId;
                                if (viewModel.CommissioningBaselineComparison?.FirstMismatch?.TargetId != expectedAxisId)
                                {
                                    throw new InvalidOperationException(
                                        $"Commissioning baseline mismatch did not target axis '{expectedAxisId}'.");
                                }
                                viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
                                if (viewModel.Layout.SelectedItem?.Id != expectedAxisId)
                                {
                                    throw new InvalidOperationException(
                                        $"Commissioning mismatch navigation did not select axis '{expectedAxisId}'.");
                                }
                            }
                        }
                    }
                    if (state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                        || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                    {
                        inspector.NavigateCommissioningMismatchButton.BringIntoView();
                    }
                    else if (state.Equals("baseline-accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        inspector.AcceptCommissioningBaselineButton.BringIntoView();
                    }
                    else
                    {
                        inspector.CommissioningResultHistoryList.BringIntoView();
                    }
                }
                else if (state.Equals("validated", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("validating", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions =
                        state.Equals("validating", StringComparison.OrdinalIgnoreCase) ? 100 : 3;
                    viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                    for (var attempt = 0; attempt < 200; attempt++)
                    {
                        var ready = state.Equals("validating", StringComparison.OrdinalIgnoreCase)
                            ? viewModel.IsCommissioningValidationRunning
                            : !viewModel.IsCommissioningValidationRunning
                                && viewModel.LatestCommissioningResult is not null;
                        if (ready)
                        {
                            break;
                        }
                        await Task.Delay(25);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    if (state.Equals("validating", StringComparison.OrdinalIgnoreCase)
                        ? !viewModel.IsCommissioningValidationRunning
                        : viewModel.LatestCommissioningResult is not { IsSuccess: true })
                    {
                        throw new InvalidOperationException("Recipe validation smoke state was not reached.");
                    }
                }
                else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    window.Activate();
                    MovePointerToCenter(inspector.RunMultiAxisRecipeButton);
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.RunMultiAxisRecipeButton.IsMouseOver)
                    {
                        throw new InvalidOperationException("Recipe Run did not enter pointer-hover state.");
                    }
                    if (state.Equals("run-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        _smokePointerHeld = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!inspector.RunMultiAxisRecipeButton.IsPressed)
                        {
                            throw new InvalidOperationException("Recipe Run did not enter pointer-down state.");
                        }
                    }
                }
                break;
            case "running":
                viewModel.IsRunMode = true;
                viewModel.RunMultiAxisCommissioningRecipeCommand.Execute(null);
                for (var attempt = 0; attempt < 80 &&
                     !viewModel.MultiAxisCommissioningRecipe.Targets.Any(target =>
                         target.RuntimeState == AxisState.Moving); attempt++)
                {
                    await Task.Delay(25);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                inspector.RunInspectorScrollViewer.ScrollToTop();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-multi-axis-recipe-state '{state}'. " +
                    "Expected design, design-focus, design-popup, ready, run-hover, run-pressed, running, " +
                    "validation-focus, validation-hover, validation-pressed, validated, validating, " +
                    "history-selected, baseline-pressed, baseline-accepted, baseline-mismatch, or baseline-mismatch-x.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static async Task ApplyAxisCommissioningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-axis-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        await ScrollAxisCommissioningIntoViewAsync(window);
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        bool isReadyState = state.Equals("ready", StringComparison.OrdinalIgnoreCase)
            || state.Equals("interlocked", StringComparison.OrdinalIgnoreCase)
            || state.Equals("invalid-target", StringComparison.OrdinalIgnoreCase)
            || state.Equals("focus-target", StringComparison.OrdinalIgnoreCase)
            || state.Equals("invalid-relative", StringComparison.OrdinalIgnoreCase)
            || state.Equals("focus-relative", StringComparison.OrdinalIgnoreCase)
            || state.Equals("invalid-velocity", StringComparison.OrdinalIgnoreCase)
            || state.Equals("focus-velocity", StringComparison.OrdinalIgnoreCase)
            || state.Equals("following-error-ready", StringComparison.OrdinalIgnoreCase);
        if (!isReadyState)
        {
            viewModel.AxisTargetPositionText = "40";
            viewModel.AxisRelativeDistanceText = "40";
            viewModel.AxisCommandVelocityText = "50";
            if (!viewModel.StartManualEquipmentControlCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Manual axis control was unavailable for the smoke state.");
            }
            viewModel.StartManualEquipmentControlCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.IsRunning && viewModel.CanJogAxis,
                "Manual axis control did not start for the smoke state.");
        }

        if (state.Equals("target", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.MoveAxisAbsoluteCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                    Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - 40) < 1e-6,
                "The axis did not reach the target smoke state.");
        }
        else if (state.Equals("relative-target", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.MoveAxisRelativeCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                    Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position - 40) < 1e-6,
                "The axis did not reach the relative target smoke state.");
        }
        else if (state.Equals("invalid-target", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisTargetPositionText = "invalid";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        else if (state.Equals("focus-target", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisTargetPositionText = "125.500";
            inspector.AxisTargetPositionTextBox.Focus();
        }
        else if (state.Equals("invalid-relative", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisRelativeDistanceText = "0";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        else if (state.Equals("focus-relative", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisRelativeDistanceText = "-25.500";
            inspector.AxisRelativeDistanceTextBox.Focus();
        }
        else if (state.Equals("invalid-velocity", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisCommandVelocityText = "0";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        else if (state.Equals("focus-velocity", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisCommandVelocityText = "-50.000";
            inspector.AxisCommandVelocityTextBox.Focus();
        }
        else if (state.Equals("velocity-running", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.MoveAxisVelocityCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Moving &&
                    viewModel.SceneSnapshots.Latest.Axes[0].Velocity > 0,
                "The axis did not enter the velocity-running smoke state.");
        }
        else if (state.Equals("velocity-limited", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisCommandVelocityText = "180";
            viewModel.MoveAxisVelocityCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Limited,
                "The axis did not reach the velocity-limited smoke state.");
        }
        else if (state.Equals("following-error-alarm", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.AxisCommandVelocityText = "5";
            viewModel.MoveAxisVelocityCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Moving,
                "Following-error smoke motion did not start.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Following-error smoke motion did not pause.");
            var manager = viewModel.FaultManager;
            manager.SelectedKind = manager.AvailableKinds.Single(option =>
                option.Kind == SimulationFaultKind.AxisFollowingError);
            manager.SelectedTarget = manager.Targets.Single(target =>
                string.Equals(target.Id, viewModel.SceneSnapshots.Latest!.Axes[0].Id, StringComparison.Ordinal));
            manager.InjectCommand.Execute(null);
            await WaitForAsync(
                () => !manager.IsOperationPending && viewModel.SceneSnapshots.Latest!.Faults.Any(fault =>
                    fault.Kind == SimulationFaultKind.AxisFollowingError),
                "Following-error smoke fault did not activate.");
            for (var step = 0; step < 10 && !viewModel.IsCurrentAxisDriveAlarmActive; step++)
            {
                var beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
                viewModel.StepCommand.Execute(null);
                await WaitForAsync(
                    () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep,
                    "Following-error smoke Step did not advance.");
            }
            await WaitForAsync(
                () => viewModel.IsCurrentAxisDriveAlarmActive,
                "Following-error smoke alarm did not latch.");
        }
        else if (state.Equals("hover-velocity", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-velocity", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.MoveAxisVelocityButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state.Equals("hover-relative", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-relative", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.MoveAxisRelativeButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state.Equals("hover-move", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-move", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.MoveAxisAbsoluteButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state.Equals("homed", StringComparison.OrdinalIgnoreCase))
        {
            if (!viewModel.BeginAxisJog(AxisJogDirection.Positive))
            {
                throw new InvalidOperationException("Jog+ did not start for the homed state.");
            }
            var start = viewModel.SceneSnapshots.Latest!.Axes[0].Position;
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].Position > start + 0.05,
                "Jog+ did not advance before Home.");
            await viewModel.EndAxisJogAsync();
            await WaitForAsync(
                () => viewModel.HomeAxisCommand.CanExecute(null),
                "Home remained unavailable after Jog stop.");
            viewModel.HomeAxisCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Idle &&
                    Math.Abs(viewModel.SceneSnapshots.Latest.Axes[0].Position) < 1e-9,
                "Home did not complete for the smoke state.");
        }
        else if (state.Equals("interlocked", StringComparison.OrdinalIgnoreCase))
        {
            var manager = viewModel.FaultManager;
            manager.SelectedKind = manager.AvailableKinds.Single(option =>
                option.Kind == SimulationFaultKind.AxisMotionBlocked);
            manager.SelectedTarget = manager.Targets.Single(target =>
                string.Equals(target.Id, viewModel.SceneSnapshots.Latest!.Axes[0].Id, StringComparison.Ordinal));
            manager.InjectCommand.Execute(null);
            await WaitForAsync(
                () => !manager.IsOperationPending && viewModel.IsCurrentAxisInterlocked &&
                    viewModel.SceneSnapshots.Latest!.Axes[0].State == AxisState.Error,
                "Blocked-axis fault did not activate for the smoke state.");
        }
        else if (state.Equals("focus-home", StringComparison.OrdinalIgnoreCase))
        {
            inspector.HomeAxisButton.Focus();
        }
        else if (state.Equals("hover-jog-positive", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-jog-positive", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.JogPositiveButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                await WaitForAsync(
                    () => viewModel.SceneSnapshots.Latest!.Axes[0].Velocity > 0,
                    "Pressed Jog+ did not move the axis.");
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase) &&
                  !state.Equals("following-error-ready", StringComparison.OrdinalIgnoreCase) &&
                  !state.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-axis-commissioning-state '{state}'. Expected ready, manual, " +
                "target, relative-target, invalid-target, focus-target, invalid-relative, focus-relative, " +
                "invalid-velocity, focus-velocity, velocity-running, velocity-limited, hover-velocity, " +
                "following-error-ready, following-error-alarm, " +
                "pressed-velocity, hover-move, pressed-move, hover-relative, pressed-relative, homed, interlocked, " +
                "focus-home, hover-jog-positive, or pressed-jog-positive.");
        }

        await ScrollAxisCommissioningIntoViewAsync(window);
        await Task.Delay(150);
    }

    private static async Task ScrollAxisCommissioningIntoViewAsync(ShellWindow window)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.SelectedEquipmentRuntimeCard.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseCylinderCommissioningAsync(
        ShellWindow window,
        MainViewModel viewModel)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-cylinder-commissioning-report requires --smoke-run-layout.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        LayoutComponentSnapshot Cylinder() => viewModel.SceneSnapshots.Latest!.LayoutComponents
            .Single(component => string.Equals(
                component.Id,
                viewModel.Layout.SelectedItem!.Id,
                StringComparison.Ordinal));

        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.HasSelectedPneumaticCylinder,
            "A pneumatic cylinder was not selected for commissioning.");
        await ScrollCylinderCommissioningIntoViewAsync(window);
        Check("cylinderControlsVisible", inspector.SelectedEquipmentRuntimeCard.IsVisible
            && inspector.CylinderCommissioningPanel.IsVisible
            && !inspector.AxisCommissioningPanel.IsVisible
            && !inspector.SensorCommissioningPanel.IsVisible
            && !inspector.ConveyorCommissioningPanel.IsVisible
            && inspector.ManualCommissioningPanel.IsVisible
            && inspector.StartManualEquipmentControlButton.IsVisible
            && inspector.RetractCylinderButton.IsVisible
            && inspector.ExtendCylinderButton.IsVisible);
        Check("manualStartAvailableWhilePaused",
            viewModel.StartManualEquipmentControlCommand.CanExecute(null));
        Check("motionCommandsDisabledBeforeManualStart",
            !viewModel.CanExtendCylinder && !viewModel.CanRetractCylinder);

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual cylinder control did not start.");
        Check("manualOwnerPublished", viewModel.SceneSnapshots.Latest?.ControlOwner ==
            SimulationControlOwner.Manual);
        Check("extendEnabledAtRetractedState", viewModel.CanExtendCylinder
            && !viewModel.CanRetractCylinder);

        viewModel.ExtendCylinderCommand.Execute(null);
        await WaitForAsync(
            () => Cylinder().CylinderState == PneumaticCylinderState.Extended,
            "The cylinder did not reach Extended through fixed engine ticks.");
        Check("extendReachesSnapshotFeedback", Cylinder().MotionProgress == 1
            && !viewModel.CanExtendCylinder && viewModel.CanRetractCylinder);
        Check("extendCommandLogged", viewModel.LogMessages.Any(line => line.Contains(
            OpenVisionLanguageService.T("Cylinder.ActionExtend"),
            StringComparison.CurrentCulture)));

        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual cylinder control did not pause.");
        Check("manualPauseEnablesStep", viewModel.StepCommand.CanExecute(null));
        viewModel.RetractCylinderCommand.Execute(null);
        long beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep,
            "Manual cylinder Step did not advance.");
        Check("stepAdvancesExactlyOneTick", viewModel.SceneSnapshots.Latest!.TickIndex == beforeStep + 1);
        Check("stepPublishesRetractingSnapshot", Cylinder().CylinderState ==
            PneumaticCylinderState.Retracting && Cylinder().MotionProgress is > 0 and < 1);

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning && Cylinder().CylinderState == PneumaticCylinderState.Retracted,
            "The cylinder did not finish retracting after manual resume.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual control did not pause for fault injection.");
        viewModel.ExtendCylinderCommand.Execute(null);
        beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep
                && Cylinder().CylinderState == PneumaticCylinderState.Extending,
            "The cylinder did not enter Extending before fault injection.");
        double blockedProgress = Cylinder().MotionProgress!.Value;

        var manager = viewModel.FaultManager;
        manager.SelectedKind = manager.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.CylinderTravelBlocked);
        manager.SelectedTarget = manager.Targets.Single(target => string.Equals(
            target.Id,
            Cylinder().Id,
            StringComparison.Ordinal));
        manager.InjectCommand.Execute(null);
        await WaitForAsync(
            () => !manager.IsOperationPending && viewModel.IsCurrentCylinderInterlocked,
            "The blocked-travel fault was not published.");
        beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep
                && Cylinder().CylinderState == PneumaticCylinderState.Fault,
            "The cylinder did not publish Fault on the next fixed tick.");
        Check("blockedTravelFreezesProgress", Cylinder().MotionProgress == blockedProgress);
        Check("interlockDisablesCylinderCommands", !viewModel.CanExtendCylinder
            && !viewModel.CanRetractCylinder);
        Check("interlockEvidenceVisible", inspector.CylinderInterlockStatusText.IsVisible
            && string.Equals(
                inspector.CylinderInterlockStatusText.Text,
                OpenVisionLanguageService.T("Cylinder.InterlockBlocked"),
                StringComparison.CurrentCulture));

        manager.SelectedActiveFault = manager.ActiveFaults.Single(fault =>
            fault.Kind == SimulationFaultKind.CylinderTravelBlocked
            && string.Equals(fault.TargetId, Cylinder().Id, StringComparison.Ordinal));
        manager.ClearSelectedCommand.Execute(null);
        await WaitForAsync(
            () => !manager.IsOperationPending && !viewModel.IsCurrentCylinderInterlocked,
            "The blocked-travel fault did not clear.");
        Check("clearRequiresExplicitRecoveryCommand", viewModel.CanRetractCylinder
            && Cylinder().CylinderState == PneumaticCylinderState.Fault);
        viewModel.RetractCylinderCommand.Execute(null);
        beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep
                && Cylinder().CylinderState == PneumaticCylinderState.Retracted,
            "Explicit Retract did not recover the cylinder after fault clear.");
        Check("explicitCommandRecovers", Cylinder().MotionProgress == 0
            && !viewModel.IsCurrentCylinderInterlocked);

        viewModel.ExtendCylinderCommand.Execute(null);
        beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(() => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep,
            "The pre-Reset cylinder step did not advance.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && Cylinder().CylinderState == PneumaticCylinderState.Retracted,
            "Reset did not restore the authored cylinder state.");
        Check("resetRestoresAuthoredState", Cylinder().MotionProgress == 0
            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused);

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesCylinderHint", viewModel.CylinderCommissioningHintText ==
            OpenVisionLanguageService.T("Cylinder.StartManualHint"));
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewModel.IsRunMode = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("designModeDisablesCylinderCommissioning",
            !viewModel.StartManualEquipmentControlCommand.CanExecute(null)
            && !viewModel.CanExtendCylinder && !viewModel.CanRetractCylinder);
        viewModel.IsRunMode = true;
        await ScrollCylinderCommissioningIntoViewAsync(window);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task ApplyCylinderCommissioningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-cylinder-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        LayoutComponentSnapshot Cylinder() => viewModel.SceneSnapshots.Latest!.LayoutComponents
            .Single(component => string.Equals(
                component.Id,
                viewModel.Layout.SelectedItem!.Id,
                StringComparison.Ordinal));

        await WaitForAsync(
            () => viewModel.HasSelectedPneumaticCylinder,
            "A pneumatic cylinder was not selected for the smoke state.");
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        bool needsManual = !state.Equals("ready", StringComparison.OrdinalIgnoreCase);
        if (needsManual)
        {
            viewModel.StartManualEquipmentControlCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual cylinder control did not start for the smoke state.");
        }

        if (state.Equals("extended", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.ExtendCylinderCommand.Execute(null);
            await WaitForAsync(
                () => Cylinder().CylinderState == PneumaticCylinderState.Extended,
                "The cylinder did not reach Extended for the smoke state.");
        }
        else if (state.Equals("faulted", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual control did not pause for the faulted state.");
            viewModel.ExtendCylinderCommand.Execute(null);
            long beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep,
                "The cylinder did not advance before the faulted state.");
            var manager = viewModel.FaultManager;
            manager.SelectedKind = manager.AvailableKinds.Single(option =>
                option.Kind == SimulationFaultKind.CylinderTravelBlocked);
            manager.SelectedTarget = manager.Targets.Single(target => string.Equals(
                target.Id,
                Cylinder().Id,
                StringComparison.Ordinal));
            manager.InjectCommand.Execute(null);
            await WaitForAsync(
                () => !manager.IsOperationPending && viewModel.IsCurrentCylinderInterlocked,
                "The blocked-travel fault did not activate for the smoke state.");
            beforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeStep
                    && Cylinder().CylinderState == PneumaticCylinderState.Fault,
                "The cylinder did not publish Fault for the smoke state.");
        }
        else if (state.Equals("focus-extend", StringComparison.OrdinalIgnoreCase))
        {
            inspector.ExtendCylinderButton.Focus();
        }
        else if (state.Equals("hover-extend", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-extend", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.ExtendCylinderButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
                 && !state.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-cylinder-commissioning-state '{state}'. Expected ready, manual, " +
                "extended, faulted, focus-extend, hover-extend, or pressed-extend.");
        }

        await ScrollCylinderCommissioningIntoViewAsync(window);
        await Task.Delay(150);
    }

    private static async Task ScrollCylinderCommissioningIntoViewAsync(ShellWindow window)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.SelectedEquipmentRuntimeCard.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseConveyorCommissioningAsync(
        ShellWindow window,
        MainViewModel viewModel)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-conveyor-commissioning-report requires --smoke-run-layout.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        LayoutComponentSnapshot Conveyor() => viewModel.SceneSnapshots.Latest!.LayoutComponents
            .Single(component => string.Equals(
                component.Id,
                viewModel.Layout.SelectedItem!.Id,
                StringComparison.Ordinal));
        LayoutComponentSnapshot Workpiece() => viewModel.SceneSnapshots.Latest!.LayoutComponents
            .Single(component => string.Equals(
                component.CarrierComponentId,
                Conveyor().Id,
                StringComparison.Ordinal));
        async Task ApplyOneStepAsync()
        {
            long beforeTick = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeTick,
                "Conveyor Step did not advance.");
            Check("stepAdvancesExactlyOneTick",
                viewModel.SceneSnapshots.Latest!.TickIndex == beforeTick + 1);
        }

        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.HasSelectedConveyor,
            "A conveyor was not selected for commissioning.");
        await ScrollConveyorCommissioningIntoViewAsync(window);
        Check("conveyorControlsVisible", inspector.SelectedEquipmentRuntimeCard.IsVisible
            && inspector.ConveyorCommissioningPanel.IsVisible
            && !inspector.AxisCommissioningPanel.IsVisible
            && !inspector.SensorCommissioningPanel.IsVisible
            && !inspector.CylinderCommissioningPanel.IsVisible
            && inspector.ManualCommissioningPanel.IsVisible
            && inspector.StartManualEquipmentControlButton.IsVisible
            && inspector.RunConveyorForwardButton.IsVisible
            && inspector.StopConveyorButton.IsVisible
            && inspector.RunConveyorReverseButton.IsVisible);
        Check("manualStartAvailableWhilePaused",
            viewModel.StartManualEquipmentControlCommand.CanExecute(null));
        Check("motionCommandsDisabledBeforeManualStart", !viewModel.CanRunConveyorForward
            && !viewModel.CanRunConveyorReverse && !viewModel.CanStopConveyor);

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual conveyor control did not start.");
        Check("manualOwnerPublished", viewModel.SceneSnapshots.Latest?.ControlOwner ==
            SimulationControlOwner.Manual);
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual conveyor control did not pause.");
        Check("manualPauseEnablesStep", viewModel.StepCommand.CanExecute(null));

        double initialPosition = Workpiece().CarrierPosition!.Value;
        double conveyorSpeed = Conveyor().ConveyorSpeedUnitsPerSecond!.Value;
        TimeSpan beforeForwardTime = viewModel.SceneSnapshots.Latest!.SimulationTime;
        viewModel.RunConveyorForwardCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Conveyor.ActionRunForward"),
                StringComparison.CurrentCulture)),
            "Forward command evidence was not logged.");
        await ApplyOneStepAsync();
        double travelPerTick = conveyorSpeed
            * (viewModel.SceneSnapshots.Latest!.SimulationTime - beforeForwardTime).TotalSeconds;
        Check("forwardSnapshotPublished", Conveyor().ConveyorRunning == true
            && Conveyor().ConveyorDirection == ConveyorDirection.Forward);
        Check("forwardMovesOneTick", Math.Abs(
            Workpiece().CarrierPosition!.Value - initialPosition - travelPerTick) < 1e-9);

        double forwardPosition = Workpiece().CarrierPosition!.Value;
        viewModel.StopConveyorCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Conveyor.ActionStop"),
                StringComparison.CurrentCulture)),
            "Stop command evidence was not logged.");
        await ApplyOneStepAsync();
        Check("stopFreezesWorkpiece", Conveyor().ConveyorRunning == false
            && Math.Abs(Workpiece().CarrierPosition!.Value - forwardPosition) < 1e-9);

        viewModel.RunConveyorReverseCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Conveyor.ActionRunReverse"),
                StringComparison.CurrentCulture)),
            "Reverse command evidence was not logged.");
        await ApplyOneStepAsync();
        Check("reverseSnapshotPublished", Conveyor().ConveyorRunning == true
            && Conveyor().ConveyorDirection == ConveyorDirection.Reverse);
        Check("reverseMovesOneTick", Math.Abs(
            Workpiece().CarrierPosition!.Value - forwardPosition + travelPerTick) < 1e-9);
        Check("manualCommandsLogged", viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Conveyor.ActionRunForward"),
                StringComparison.CurrentCulture))
            && viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Conveyor.ActionStop"),
                StringComparison.CurrentCulture))
            && viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Conveyor.ActionRunReverse"),
                StringComparison.CurrentCulture)));

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && Conveyor().ConveyorRunning == false,
            "Reset did not restore the authored conveyor state.");
        Check("resetRestoresAuthoredState", Conveyor().ConveyorDirection == ConveyorDirection.Forward
            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused);

        viewModel.RunCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner ==
                    SimulationControlOwner.EmbeddedSequence,
            "Automatic sequence ownership did not start.");
        Check("automaticOwnerBlocksManualStart",
            !viewModel.StartManualEquipmentControlCommand.CanExecute(null)
            && !viewModel.CanRunConveyorForward && !viewModel.CanRunConveyorReverse);
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Final Reset did not pause the runtime.");

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesConveyorHint", viewModel.ConveyorCommissioningHintText ==
            OpenVisionLanguageService.T("Conveyor.StartManualHint"));
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewModel.IsRunMode = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("designModeDisablesConveyorCommissioning",
            !viewModel.StartManualEquipmentControlCommand.CanExecute(null)
            && !viewModel.CanRunConveyorForward && !viewModel.CanRunConveyorReverse
            && !viewModel.CanStopConveyor);
        viewModel.IsRunMode = true;
        await ScrollConveyorCommissioningIntoViewAsync(window);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task ApplyConveyorCommissioningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-conveyor-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        LayoutComponentSnapshot Conveyor() => viewModel.SceneSnapshots.Latest!.LayoutComponents
            .Single(component => string.Equals(
                component.Id,
                viewModel.Layout.SelectedItem!.Id,
                StringComparison.Ordinal));
        async Task ApplyOneStepAsync()
        {
            long beforeTick = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeTick,
                "Conveyor smoke-state Step did not advance.");
        }
        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"Conveyor action '{action}' was not logged.");
        }

        await WaitForAsync(
            () => viewModel.HasSelectedConveyor,
            "A conveyor was not selected for the smoke state.");
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.StartManualEquipmentControlCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual conveyor control did not start for the smoke state.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual conveyor control did not pause.");
        }

        if (state.Equals("forward", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(
                viewModel.RunConveyorForwardCommand,
                "Conveyor.ActionRunForward");
            await ApplyOneStepAsync();
            await WaitForAsync(
                () => Conveyor().ConveyorRunning == true
                    && Conveyor().ConveyorDirection == ConveyorDirection.Forward,
                "Forward conveyor state was not published.");
        }
        else if (state.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(
                viewModel.RunConveyorReverseCommand,
                "Conveyor.ActionRunReverse");
            await ApplyOneStepAsync();
            await WaitForAsync(
                () => Conveyor().ConveyorRunning == true
                    && Conveyor().ConveyorDirection == ConveyorDirection.Reverse,
                "Reverse conveyor state was not published.");
        }
        else if (state.Equals("stopped", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(
                viewModel.RunConveyorForwardCommand,
                "Conveyor.ActionRunForward");
            await ApplyOneStepAsync();
            await ExecuteAndWaitAsync(
                viewModel.StopConveyorCommand,
                "Conveyor.ActionStop");
            await ApplyOneStepAsync();
            await WaitForAsync(
                () => Conveyor().ConveyorRunning == false,
                "Stopped conveyor state was not published.");
        }
        else if (state.Equals("focus-forward", StringComparison.OrdinalIgnoreCase))
        {
            inspector.RunConveyorForwardButton.Focus();
        }
        else if (state.Equals("hover-reverse", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-forward", StringComparison.OrdinalIgnoreCase))
        {
            var button = state.StartsWith("hover", StringComparison.OrdinalIgnoreCase)
                ? inspector.RunConveyorReverseButton
                : inspector.RunConveyorForwardButton;
            MovePointerToCenter(button);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
                 && !state.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-conveyor-commissioning-state '{state}'. Expected ready, manual, " +
                "forward, reverse, stopped, focus-forward, hover-reverse, or pressed-forward.");
        }

        await ScrollConveyorCommissioningIntoViewAsync(window);
        await Task.Delay(150);
    }

    private static async Task ScrollConveyorCommissioningIntoViewAsync(ShellWindow window)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.SelectedEquipmentRuntimeCard.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseSensorCommissioningAsync(
        ShellWindow window,
        MainViewModel viewModel)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-sensor-commissioning-report requires --smoke-run-layout.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task ApplyOneStepAsync()
        {
            long beforeTick = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest!.TickIndex > beforeTick,
                "Sensor Step did not advance.");
            Check("stepAdvancesExactlyOneTick",
                viewModel.SceneSnapshots.Latest!.TickIndex == beforeTick + 1);
        }

        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"Sensor action '{action}' was not logged.");
        }

        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.HasSelectedDigitalSensor,
            "A digital sensor was not selected for commissioning.");
        await ScrollSensorCommissioningIntoViewAsync(window);
        Check("sensorControlsVisible", inspector.SelectedEquipmentRuntimeCard.IsVisible
            && inspector.SensorCommissioningPanel.IsVisible
            && !inspector.AxisCommissioningPanel.IsVisible
            && !inspector.CylinderCommissioningPanel.IsVisible
            && !inspector.ConveyorCommissioningPanel.IsVisible
            && inspector.ManualCommissioningPanel.IsVisible
            && inspector.StartManualEquipmentControlButton.IsVisible
            && inspector.ForceSensorOnButton.IsVisible
            && inspector.ForceSensorOffButton.IsVisible
            && inspector.ClearSensorForceButton.IsVisible);
        Check("manualStartAvailableWhilePaused",
            viewModel.StartManualEquipmentControlCommand.CanExecute(null));
        Check("forceCommandsDisabledBeforeManualStart", !viewModel.CanForceSensorOn
            && !viewModel.CanForceSensorOff && !viewModel.CanClearSensorForce);

        viewModel.StartManualEquipmentControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual sensor control did not start.");
        Check("manualOwnerPublished", viewModel.SceneSnapshots.Latest?.ControlOwner ==
            SimulationControlOwner.Manual);
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual sensor control did not pause.");
        Check("manualPauseEnablesStep", viewModel.StepCommand.CanExecute(null));

        await ExecuteAndWaitAsync(viewModel.ForceSensorOnCommand, "Sensor.ActionForceOn");
        await WaitForAsync(
            () => viewModel.CurrentSelectedSensorSignal?.OverrideValue == true,
            "Force ON was not published in the signal snapshot.");
        await ApplyOneStepAsync();
        Check("forceOnPersistsAcrossTick", viewModel.CurrentSelectedSensorSignal is
            { Value: true, OverrideValue: true });

        await ExecuteAndWaitAsync(viewModel.ClearSensorForceCommand, "Sensor.ActionClearForce");
        await WaitForAsync(
            () => viewModel.CurrentSelectedSensorSignal?.OverrideValue is null,
            "Force ON was not cleared.");
        Check("clearRestoresNominalAfterForceOn",
            viewModel.CurrentSelectedSensorSignal?.Value ==
            viewModel.CurrentSelectedSensorSignal?.NominalValue);

        await ApplyOneStepAsync();
        await ExecuteAndWaitAsync(viewModel.ForceSensorOffCommand, "Sensor.ActionForceOff");
        await WaitForAsync(
            () => viewModel.CurrentSelectedSensorSignal?.OverrideValue == false,
            "Force OFF was not published in the signal snapshot.");
        await ApplyOneStepAsync();
        Check("forceOffPersistsAcrossTick", viewModel.CurrentSelectedSensorSignal is
            { Value: false, NominalValue: true, OverrideValue: false });
        Check("selectedEquipmentUsesEffectiveForcedValue",
            viewModel.SelectedEquipmentStatus?.StateText ==
            OpenVisionLanguageService.T("Equipment.Off"));

        await ExecuteAndWaitAsync(viewModel.ClearSensorForceCommand, "Sensor.ActionClearForce");
        await WaitForAsync(
            () => viewModel.CurrentSelectedSensorSignal is
                { Value: true, NominalValue: true, OverrideValue: null },
            "Force OFF did not restore the latest nominal detection.");
        Check("clearRestoresLatestNominal", true);
        Check("selectedEquipmentRestoresNominalValue",
            viewModel.SelectedEquipmentStatus?.StateText ==
            OpenVisionLanguageService.T("Equipment.On"));
        Check("manualCommandsLogged", viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Sensor.ActionForceOn"),
                StringComparison.CurrentCulture))
            && viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Sensor.ActionForceOff"),
                StringComparison.CurrentCulture))
            && viewModel.LogMessages.Any(line => line.Contains(
                OpenVisionLanguageService.T("Sensor.ActionClearForce"),
                StringComparison.CurrentCulture)));

        await ExecuteAndWaitAsync(viewModel.ForceSensorOnCommand, "Sensor.ActionForceOn");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.CurrentSelectedSensorSignal?.OverrideValue is null,
            "Reset did not clear the sensor force.");
        Check("resetClearsForceAndRestoresDefinitionOwner",
            viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused);

        viewModel.RunCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner ==
                    SimulationControlOwner.EmbeddedSequence,
            "Automatic sequence ownership did not start.");
        Check("automaticOwnerBlocksManualForce",
            !viewModel.StartManualEquipmentControlCommand.CanExecute(null)
            && !viewModel.CanForceSensorOn && !viewModel.CanForceSensorOff);
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Final Reset did not pause the runtime.");

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesSensorHint", viewModel.SensorCommissioningHintText ==
            OpenVisionLanguageService.T("Sensor.StartManualHint"));
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewModel.IsRunMode = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("designModeDisablesSensorCommissioning",
            !viewModel.StartManualEquipmentControlCommand.CanExecute(null)
            && !viewModel.CanForceSensorOn && !viewModel.CanForceSensorOff
            && !viewModel.CanClearSensorForce);
        viewModel.IsRunMode = true;
        await ScrollSensorCommissioningIntoViewAsync(window);
        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task ApplySensorCommissioningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-sensor-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"Sensor action '{action}' was not logged.");
        }

        await WaitForAsync(
            () => viewModel.HasSelectedDigitalSensor,
            "A digital sensor was not selected for the smoke state.");
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        bool isStartButtonState = state.Equals("focus-start", StringComparison.OrdinalIgnoreCase)
            || state.Equals("hover-start", StringComparison.OrdinalIgnoreCase)
            || state.Equals("pressed-start", StringComparison.OrdinalIgnoreCase);
        if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase) && !isStartButtonState)
        {
            viewModel.StartManualEquipmentControlCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual sensor control did not start for the smoke state.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual sensor control did not pause.");
        }

        if (state.Equals("focus-start", StringComparison.OrdinalIgnoreCase))
        {
            inspector.StartManualEquipmentControlButton.Focus();
        }
        else if (state.Equals("hover-start", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-start", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(inspector.StartManualEquipmentControlButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (state.Equals("forced-on", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(viewModel.ForceSensorOnCommand, "Sensor.ActionForceOn");
            await WaitForAsync(
                () => viewModel.CurrentSelectedSensorSignal?.OverrideValue == true,
                "Forced-ON sensor state was not published.");
        }
        else if (state.Equals("forced-off", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(viewModel.ForceSensorOffCommand, "Sensor.ActionForceOff");
            await WaitForAsync(
                () => viewModel.CurrentSelectedSensorSignal?.OverrideValue == false,
                "Forced-OFF sensor state was not published.");
        }
        else if (state.Equals("cleared", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(viewModel.ForceSensorOnCommand, "Sensor.ActionForceOn");
            await ExecuteAndWaitAsync(viewModel.ClearSensorForceCommand, "Sensor.ActionClearForce");
            await WaitForAsync(
                () => viewModel.CurrentSelectedSensorSignal?.OverrideValue is null,
                "Cleared sensor force state was not published.");
        }
        else if (state.Equals("focus-on", StringComparison.OrdinalIgnoreCase))
        {
            inspector.ForceSensorOnButton.Focus();
        }
        else if (state.Equals("hover-off", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-on", StringComparison.OrdinalIgnoreCase))
        {
            var button = state.StartsWith("hover", StringComparison.OrdinalIgnoreCase)
                ? inspector.ForceSensorOffButton
                : inspector.ForceSensorOnButton;
            MovePointerToCenter(button);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
                 && !state.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-sensor-commissioning-state '{state}'. Expected ready, manual, " +
                "forced-on, forced-off, cleared, focus-start, hover-start, pressed-start, " +
                "focus-on, hover-off, or pressed-on.");
        }

        await ScrollSensorCommissioningIntoViewAsync(window);
        await Task.Delay(150);
    }

    private static async Task ScrollSensorCommissioningIntoViewAsync(ShellWindow window)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.SelectedEquipmentRuntimeCard.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
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

    private static async Task ApplyPickAndPlaceSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        var normalizedState = state.ToLowerInvariant();
        var expected = normalizedState switch
        {
            "available" => (AxisX: 0d, AxisY: 0d, Gripper: false, WorkpieceX: 240d, WorkpieceY: 120d, WorkpieceState: PickPlaceWorkpieceState.Available),
            "pick-held" => (AxisX: 240d, AxisY: 120d, Gripper: true, WorkpieceX: 240d, WorkpieceY: 120d, WorkpieceState: PickPlaceWorkpieceState.Attached),
            "place-held" => (AxisX: 400d, AxisY: 240d, Gripper: true, WorkpieceX: 400d, WorkpieceY: 240d, WorkpieceState: PickPlaceWorkpieceState.Attached),
            "released" => (AxisX: 400d, AxisY: 240d, Gripper: false, WorkpieceX: 400d, WorkpieceY: 240d, WorkpieceState: PickPlaceWorkpieceState.Placed),
            _ => throw new ArgumentException(
                $"Unsupported --smoke-pick-place-state '{state}'. " +
                "Expected available, pick-held, place-held, or released.")
        };

        if (viewModel.IsRunning)
        {
            viewModel.PauseCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && viewModel.IsRunning; attempt++)
            {
                await Task.Delay(10);
            }
        }

        static double AxisPosition(MainViewModel viewModel, string id) =>
            viewModel.SceneSnapshots.Latest?.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, id, StringComparison.Ordinal))?.Position ?? double.NaN;

        static bool? GripperValue(MainViewModel viewModel) =>
            viewModel.SceneSnapshots.Latest?.Signals.FirstOrDefault(signal =>
                string.Equals(signal.Id, "do.gripper", StringComparison.Ordinal))?.Value;

        static PickPlaceWorkpieceSnapshot? Workpiece(MainViewModel viewModel) =>
            viewModel.SceneSnapshots.Latest?.Workpieces.SingleOrDefault();

        if (normalizedState == "available")
        {
            viewModel.ResetCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        bool IsExpectedState() =>
            Math.Abs(AxisPosition(viewModel, "x") - expected.AxisX) <= 1e-9 &&
            Math.Abs(AxisPosition(viewModel, "y") - expected.AxisY) <= 1e-9 &&
            GripperValue(viewModel) == expected.Gripper &&
            Workpiece(viewModel) is { } workpiece &&
            workpiece.State == expected.WorkpieceState &&
            Math.Abs(workpiece.X - expected.WorkpieceX) <= 1e-9 &&
            Math.Abs(workpiece.Y - expected.WorkpieceY) <= 1e-9;

        for (var step = 0; step < 2_000 && !IsExpectedState(); step++)
        {
            if (!viewModel.StepCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    $"Step was unavailable before Pick-and-Place state '{state}'.");
            }

            var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex ?? -1;
            viewModel.StepCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 100 && viewModel.SceneSnapshots.Latest?.TickIndex <= beforeTick;
                 attempt++)
            {
                await Task.Delay(5);
            }

            if (viewModel.SceneSnapshots.Latest?.TickIndex != beforeTick + 1)
            {
                throw new InvalidOperationException(
                    $"Pick-and-Place Step did not advance exactly one Tick from {beforeTick}.");
            }
        }

        if (!IsExpectedState())
        {
            throw new InvalidOperationException(
                $"Pick-and-Place state '{state}' was not reached within 2,000 fixed Ticks.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        var expectedText = OpenVisionLanguageService.T(
            expected.Gripper ? "Scene.GripperClosed" : "Scene.GripperOpen");
        if (viewport.LastRenderedGripperValue != expected.Gripper ||
            !string.Equals(viewport.LastRenderedGripperText, expectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The scene did not render the gripper snapshot state '{expectedText}'.");
        }
        var expectedWorkpiece = Workpiece(viewModel)!;
        var expectedWorkpieceText = OpenVisionLanguageService.T(expected.WorkpieceState switch
        {
            PickPlaceWorkpieceState.Attached => "Scene.WorkpieceAttached",
            PickPlaceWorkpieceState.Placed => "Scene.WorkpiecePlaced",
            _ => "Scene.WorkpieceAvailable"
        });
        if (!ReferenceEquals(viewport.LastRenderedWorkpiece, expectedWorkpiece) ||
            !string.Equals(viewport.LastRenderedWorkpieceText, expectedWorkpieceText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The scene did not render the workpiece snapshot state '{expectedWorkpieceText}'.");
        }

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var alternateText = OpenVisionLanguageService.T(
            expected.Gripper ? "Scene.GripperClosed" : "Scene.GripperOpen");
        if (!string.Equals(viewport.LastRenderedGripperText, alternateText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The gripper scene label did not follow the language switch.");
        }
        var alternateWorkpieceText = OpenVisionLanguageService.T(expected.WorkpieceState switch
        {
            PickPlaceWorkpieceState.Attached => "Scene.WorkpieceAttached",
            PickPlaceWorkpieceState.Placed => "Scene.WorkpiecePlaced",
            _ => "Scene.WorkpieceAvailable"
        });
        if (!string.Equals(viewport.LastRenderedWorkpieceText, alternateWorkpieceText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The workpiece scene label did not follow the language switch.");
        }

        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine(
            $"Pick-and-Place visual state applied: {state} | " +
            $"x={expected.AxisX:F3}, y={expected.AxisY:F3}, gripper={expected.Gripper}, " +
            $"workpiece={expected.WorkpieceState}@({expected.WorkpieceX:F3},{expected.WorkpieceY:F3})");
    }

    private static async Task ApplySequenceSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        var editor = FindVisualDescendant<OpenVisionLab.MachineStudio.View.Sequence.SequenceEditorView>(window)
            ?? throw new InvalidOperationException(
                "The Sequence document tab must be visible for a sequence-state smoke.");

        if (state.Equals("focus", StringComparison.OrdinalIgnoreCase))
        {
            var textBox = FindVisualDescendant<TextBox>(editor, candidate =>
                    candidate.IsVisible &&
                    candidate.IsEnabled &&
                    !candidate.IsReadOnly &&
                    candidate.Focusable)
                ?? throw new InvalidOperationException("No sequence TextBox was visible.");
            window.Activate();
            textBox.Focus();
            Keyboard.Focus(textBox);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!textBox.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException("Sequence TextBox did not receive keyboard focus.");
            }
        }
        else if (state.Equals("hover", StringComparison.OrdinalIgnoreCase))
        {
            var row = FindVisualDescendant<DataGridRow>(editor, candidate =>
                    candidate.IsVisible && !candidate.IsSelected)
                ?? throw new InvalidOperationException(
                    "No unselected sequence DataGrid row was visible.");
            window.Activate();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var center = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
            if (!SetCursorPos(checked((int)Math.Round(center.X)), checked((int)Math.Round(center.Y))))
            {
                throw new InvalidOperationException("The pointer could not be placed over a sequence row.");
            }

            await Task.Delay(100);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!row.IsMouseOver)
            {
                throw new InvalidOperationException("Sequence row did not enter the pointer-hover state.");
            }
        }
        else if (state.Equals("popup", StringComparison.OrdinalIgnoreCase))
        {
            var comboBox = FindVisualDescendant<ComboBox>(editor)
                ?? throw new InvalidOperationException("No sequence ComboBox was visible.");
            window.Activate();
            comboBox.Focus();
            comboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!comboBox.IsDropDownOpen)
            {
                throw new InvalidOperationException("Sequence ComboBox popup did not open.");
            }
        }
        else if (state.Equals("validation", StringComparison.OrdinalIgnoreCase))
        {
            var step = viewModel.SequenceEditor.SelectedStep
                ?? throw new InvalidOperationException("No sequence step was selected.");
            step.NextStepId = "missing-smoke-step";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (viewModel.SequenceEditor.ValidationMessages.Count == 0)
            {
                throw new InvalidOperationException("Invalid sequence input produced no validation state.");
            }
        }
        else if (state.StartsWith("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.SequenceEditor.SelectStep("wait-cylinder-extended");
            var step = viewModel.SequenceEditor.SelectedStep
                ?? throw new InvalidOperationException("The checkpoint smoke step was not available.");
            step.HasExpectedState = true;
            step.ExpectedTargetId = "process-cylinder";
            step.ExpectedState = "Extended";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!step.HasExpectedState
                || step.ExpectedTargetId != "process-cylinder"
                || step.ExpectedState != "Extended"
                || !step.AvailableExpectedStates.Contains("Extended", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The expected-state checkpoint editor was not populated.");
            }

            var checkBox = FindVisualDescendant<CheckBox>(editor, candidate =>
                    candidate.IsVisible
                    && candidate.IsChecked == true)
                ?? throw new InvalidOperationException("The expected-state checkpoint checkbox was not visible.");
            if (state.Equals("checkpoint-focus", StringComparison.OrdinalIgnoreCase))
            {
                window.Activate();
                checkBox.Focus();
                Keyboard.Focus(checkBox);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!checkBox.IsKeyboardFocused)
                {
                    throw new InvalidOperationException("The checkpoint checkbox did not receive keyboard focus.");
                }
            }
            else if (state.Equals("checkpoint-hover", StringComparison.OrdinalIgnoreCase)
                     || state.Equals("checkpoint-pressed", StringComparison.OrdinalIgnoreCase))
            {
                window.Activate();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                MovePointerToCenter(checkBox);
                await Task.Delay(150);
                if (!checkBox.IsMouseOver)
                {
                    throw new InvalidOperationException("The checkpoint checkbox did not enter hover state.");
                }
                if (state.Equals("checkpoint-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    _smokePointerHeld = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!checkBox.IsPressed)
                    {
                        throw new InvalidOperationException("The checkpoint checkbox did not enter pointer-down state.");
                    }
                }
            }
            else if (state.Equals("checkpoint-popup", StringComparison.OrdinalIgnoreCase))
            {
                var comboBox = FindVisualDescendant<ComboBox>(editor, candidate =>
                        candidate.IsVisible
                        && candidate.IsEnabled
                        && candidate.Items.OfType<string>().Contains("Extended", StringComparer.Ordinal))
                    ?? throw new InvalidOperationException("The expected-state ComboBox was not visible.");
                window.Activate();
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!comboBox.IsDropDownOpen)
                {
                    throw new InvalidOperationException("The expected-state ComboBox popup did not open.");
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
                    ?? throw new InvalidOperationException(
                        "The expected-state ComboBox popup content was unavailable.");
            }
            else if (state.Equals("checkpoint-disabled", StringComparison.OrdinalIgnoreCase))
            {
                viewModel.IsRunMode = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (checkBox.IsEnabled)
                {
                    throw new InvalidOperationException("The checkpoint editor remained enabled in Run mode.");
                }
            }
            else if (state.Equals("checkpoint-validation", StringComparison.OrdinalIgnoreCase))
            {
                step.ExpectedState = string.Empty;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (viewModel.SequenceEditor.ValidationMessages.Count == 0)
                {
                    throw new InvalidOperationException("An incomplete checkpoint produced no validation state.");
                }
            }
            else if (!state.Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-sequence-state '{state}'. Expected checkpoint, " +
                    "checkpoint-focus, checkpoint-hover, checkpoint-pressed, checkpoint-popup, " +
                    "checkpoint-disabled, or checkpoint-validation.");
            }
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported --smoke-sequence-state '{state}'. " +
                "Expected focus, hover, popup, validation, or checkpoint.");
        }

        Console.WriteLine($"Sequence visual state applied: {state}");
    }

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
    private static extern void mouse_event(
        uint dwFlags,
        uint dx,
        uint dy,
        uint dwData,
        UIntPtr dwExtraInfo);

    private static async Task ApplyAxisTuningSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was not available.");
        var editor = viewModel.AxisDriveTuningEditor
            ?? throw new InvalidOperationException(
                "--smoke-axis-tuning-state requires an authored axis selection.");
        var followingErrorInput = FindVisualDescendant<global::Wpf.Ui.Controls.NumberBox>(
            inspector,
            box => string.Equals(
                System.Windows.Automation.AutomationProperties.GetAutomationId(box),
                "AxisFollowingErrorLimitNumberBox",
                StringComparison.Ordinal));
        var resetButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "ResetAxisDriveDefaultsButton", StringComparison.Ordinal));
        var validation = FindVisualDescendant<TextBlock>(
            inspector,
            text => string.Equals(text.Name, "AxisTuningValidationMessage", StringComparison.Ordinal));

        switch (state.ToLowerInvariant())
        {
            case "ready":
                break;
            case "focus":
                followingErrorInput?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                followingErrorInput?.Focus();
                break;
            case "hover":
                followingErrorInput?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(followingErrorInput ?? throw new InvalidOperationException(
                    "Following-error input was not available."));
                break;
            case "validation":
                editor.FollowingErrorLimit = 0;
                validation?.BringIntoView();
                inspector.DesignInspectorScrollViewer.ScrollToEnd();
                break;
            case "pressed":
                resetButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(resetButton ?? throw new InvalidOperationException(
                    "Restore drive defaults button was not available."));
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-axis-tuning-state '{state}'. " +
                    "Expected ready, focus, hover, validation, or pressed.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine($"Axis tuning visual state applied: {state}");
    }

    private static async Task ApplyLayoutPropertySmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        var inspector = FindVisualDescendant<
            OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was not available.");
        var nameTextBox = FindVisualDescendant<TextBox>(
            inspector,
            textBox => string.Equals(textBox.Name, "ComponentNameTextBox", StringComparison.Ordinal));
        var behaviorComboBox = FindVisualDescendant<ComboBox>(
            inspector,
            comboBox => string.Equals(comboBox.Name, "BehaviorBindingComboBox", StringComparison.Ordinal));
        var nudgeRightButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "NudgeRightButton", StringComparison.Ordinal));
        var cylinderSection = FindVisualDescendant<StackPanel>(
            inspector,
            panel => string.Equals(panel.Name, "CylinderPropertiesSection", StringComparison.Ordinal));
        var validationMessage = FindVisualDescendant<TextBlock>(
            inspector,
            textBlock => string.Equals(textBlock.Name, "PropertyValidationMessage", StringComparison.Ordinal));
        var alignLeftButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "AlignLeftButton", StringComparison.Ordinal));
        var bringForwardButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "BringForwardButton", StringComparison.Ordinal));
        var bringToFrontButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "BringToFrontButton", StringComparison.Ordinal));

        switch (state.ToLowerInvariant())
        {
            case "focus":
                nameTextBox?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                nameTextBox?.Focus();
                if (nameTextBox is not null)
                {
                    nameTextBox.CaretIndex = nameTextBox.Text.Length;
                }
                break;
            case "hover":
                behaviorComboBox?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(behaviorComboBox ?? throw new InvalidOperationException(
                    "Behavior binding combo box was not available."));
                break;
            case "popup":
                behaviorComboBox?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (behaviorComboBox is null)
                {
                    throw new InvalidOperationException("Behavior binding combo box was not available.");
                }
                behaviorComboBox.Focus();
                behaviorComboBox.IsDropDownOpen = true;
                break;
            case "validation":
                if (viewModel.Layout.SelectedComponentEditor is not { } editor)
                {
                    throw new InvalidOperationException("Layout property editor was not available.");
                }
                editor.Name = " ";
                validationMessage?.BringIntoView();
                break;
            case "bottom":
                cylinderSection?.BringIntoView();
                break;
            case "pressed":
                nudgeRightButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(nudgeRightButton ?? throw new InvalidOperationException(
                    "Nudge button was not available."));
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                break;
            case "alignment-focus":
                alignLeftButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                alignLeftButton?.Focus();
                break;
            case "alignment-hover":
                alignLeftButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(alignLeftButton ?? throw new InvalidOperationException(
                    "Alignment button was not available."));
                break;
            case "alignment-pressed":
                alignLeftButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(alignLeftButton ?? throw new InvalidOperationException(
                    "Alignment button was not available."));
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                break;
            case "layer-focus":
                bringForwardButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                bringForwardButton?.Focus();
                break;
            case "layer-hover":
                bringForwardButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(bringForwardButton ?? throw new InvalidOperationException(
                    "Bring forward button was not available."));
                break;
            case "layer-pressed":
                bringForwardButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(bringForwardButton ?? throw new InvalidOperationException(
                    "Bring forward button was not available."));
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                break;
            case "layer-disabled":
                if (viewModel.ChangeLayoutLayerOrderCommand.CanExecute(LayoutLayerOrder.BringToFront.ToString()))
                {
                    viewModel.ChangeLayoutLayerOrderCommand.Execute(LayoutLayerOrder.BringToFront.ToString());
                }
                bringToFrontButton?.BringIntoView();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-layout-property-state '{state}'. " +
                    "Expected focus, hover, popup, validation, bottom, pressed, " +
                    "alignment-focus, alignment-hover, alignment-pressed, layer-focus, " +
                    "layer-hover, layer-pressed, or layer-disabled.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static async Task ApplyEditMenuSmokeStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state)
    {
        var editMenu = FindVisualDescendant<MenuItem>(
            window,
            item => string.Equals(item.Name, "EditMenuItem", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Edit menu was not available.");
        if (state.Equals("open-enabled", StringComparison.OrdinalIgnoreCase))
        {
            if (!viewModel.CopyLayoutSelectionCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Copy command was unavailable for the selected layout component.");
            }
            viewModel.CopyLayoutSelectionCommand.Execute(null);
            editMenu.IsSubmenuOpen = true;
        }
        else if (state.Equals("open-disabled", StringComparison.OrdinalIgnoreCase))
        {
            editMenu.IsSubmenuOpen = true;
        }
        else if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase))
        {
            MovePointerToCenter(editMenu);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            _smokePointerHeld = true;
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported --smoke-edit-menu-state '{state}'. Expected open-enabled, open-disabled, or pressed.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

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

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ApplyEvidenceDrawerSmokeStateAsync(
        ShellWindow window,
        string state)
    {
        var viewModel = window.DataContext as MainViewModel
            ?? throw new InvalidOperationException("Main view model was not available.");
        var snapshotBefore = viewModel.SceneSnapshots.Latest;
        var toggle = FindVisualDescendant<ToggleButton>(
            window,
            candidate => string.Equals(
                candidate.Name,
                "EvidenceDrawerToggle",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Evidence drawer toggle was not available.");
        var scrollToLatest = false;

        switch (state.ToLowerInvariant())
        {
            case "collapsed":
                toggle.IsChecked = false;
                break;
            case "expanded":
                toggle.IsChecked = true;
                break;
            case "expanded-latest":
                toggle.IsChecked = true;
                scrollToLatest = true;
                break;
            case "focus":
                toggle.IsChecked = false;
                window.Activate();
                toggle.Focus();
                break;
            case "hover":
                toggle.IsChecked = false;
                MovePointerToCenter(toggle);
                break;
            case "pressed":
                toggle.IsChecked = false;
                window.Activate();
                toggle.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(toggle);
                await Task.Delay(100);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-evidence-state '{state}'. " +
                    "Expected collapsed, expanded, expanded-latest, focus, hover, or pressed.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (scrollToLatest && viewModel.LogMessages.Count > 0)
        {
            var journal = FindVisualDescendant<ListBox>(
                window,
                candidate => ReferenceEquals(candidate.ItemsSource, viewModel.LogMessages))
                ?? throw new InvalidOperationException("Evidence journal was not available.");
            journal.ScrollIntoView(viewModel.LogMessages[^1]);
        }
        await Task.Delay(150);
        if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase) && !toggle.IsPressed)
        {
            throw new InvalidOperationException("Evidence drawer did not enter the pointer-down state.");
        }
        if (!ReferenceEquals(snapshotBefore, viewModel.SceneSnapshots.Latest))
        {
            throw new InvalidOperationException("Evidence drawer interaction changed the runtime snapshot.");
        }
        Console.WriteLine($"Evidence drawer visual state applied: {state}");
    }

    private static async Task ApplyGlobalCommandSmokeStateAsync(
        ShellWindow window,
        string state)
    {
        var expectedName = OpenVisionLanguageService.T("Shell.SimulationOn");
        var button = FindVisualDescendant<Button>(
            window,
            candidate => string.Equals(
                System.Windows.Automation.AutomationProperties.GetName(candidate),
                expectedName,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Simulation ON command was not available.");

        switch (state.ToLowerInvariant())
        {
            case "normal":
                if (!button.IsEnabled)
                {
                    throw new InvalidOperationException("Simulation ON command was unexpectedly disabled.");
                }
                break;
            case "focus":
                window.Activate();
                button.Focus();
                break;
            case "hover":
                MovePointerToCenter(button);
                break;
            case "pressed":
                window.Activate();
                button.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                MovePointerToCenter(button);
                await Task.Delay(100);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                _smokePointerHeld = true;
                break;
            case "disabled":
                if (button.IsEnabled)
                {
                    throw new InvalidOperationException(
                        "Simulation ON command remained enabled; use --smoke-start-simulation for disabled evidence.");
                }
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-command-state '{state}'. " +
                    "Expected normal, focus, hover, pressed, or disabled.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase) && !button.IsPressed)
        {
            throw new InvalidOperationException("Simulation ON command did not enter the pointer-down state.");
        }
        Console.WriteLine($"Global command visual state applied: {state}");
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

    private static SmokeLayoutAlignmentReport ExerciseLayoutAlignment(
        MachineLayoutViewModel layout,
        IReadOnlyList<string> requestedIds,
        string finalAlignmentValue)
    {
        if (!Enum.TryParse(finalAlignmentValue, out LayoutSelectionAlignment finalAlignment))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-layout-align '{finalAlignmentValue}'.");
        }

        var selected = layout.Items.Where(item => requestedIds.Contains(item.Id)).ToArray();
        var originalPositions = selected.ToDictionary(
            item => item.Id,
            item => (item.CurrentX, item.CurrentY),
            StringComparer.Ordinal);
        var deviations = new Dictionary<string, double>(StringComparer.Ordinal);
        var failures = new List<string>();

        if (selected.Length != requestedIds.Count)
        {
            failures.Add(
                $"Requested {requestedIds.Count} items but found {selected.Length}.");
        }

        foreach (var alignment in Enum.GetValues<LayoutSelectionAlignment>())
        {
            RestorePositions(selected, originalPositions);
            layout.SelectMany(requestedIds, requestedIds[^1]);
            layout.AlignSelection(alignment);

            var primary = layout.SelectedItem
                ?? throw new InvalidOperationException("Alignment reference item was not selected.");
            var anchor = GetAlignmentCoordinate(primary, alignment);
            var maximumDeviation = selected
                .Select(item => Math.Abs(GetAlignmentCoordinate(item, alignment) - anchor))
                .DefaultIfEmpty(double.PositiveInfinity)
                .Max();
            deviations[alignment.ToString()] = maximumDeviation;
            if (maximumDeviation > 0.000001d)
            {
                failures.Add($"{alignment} deviation was {maximumDeviation:R}.");
            }
        }

        RestorePositions(selected, originalPositions);
        layout.SelectMany(requestedIds, requestedIds[^1]);
        layout.AlignSelection(finalAlignment);

        var beforeNudge = selected.ToDictionary(
            item => item.Id,
            item => (item.CurrentX, item.CurrentY),
            StringComparer.Ordinal);
        var nudgeStep = layout.Definition?.SnapToGrid == false ? 1d : layout.GridSize;
        layout.NudgeSelection("Right");
        var maximumNudgeDeviation = selected
            .Select(item => Math.Max(
                Math.Abs((item.CurrentX - beforeNudge[item.Id].CurrentX) - nudgeStep),
                Math.Abs(item.CurrentY - beforeNudge[item.Id].CurrentY)))
            .DefaultIfEmpty(double.PositiveInfinity)
            .Max();
        if (maximumNudgeDeviation > 0.000001d)
        {
            failures.Add($"Group nudge deviation was {maximumNudgeDeviation:R}.");
        }
        layout.NudgeSelection("Left");
        layout.AlignSelection(finalAlignment);

        return new SmokeLayoutAlignmentReport
        {
            RequestedIds = requestedIds.ToArray(),
            SelectedIds = layout.SelectedItems.Select(item => item.Id).ToArray(),
            PrimaryId = layout.SelectedItem?.Id,
            FinalAlignment = finalAlignment.ToString(),
            MaximumDeviationByAlignment = deviations,
            MaximumNudgeDeviation = maximumNudgeDeviation,
            Failures = failures
        };
    }

    private static void RestorePositions(
        IEnumerable<LayoutItem> items,
        IReadOnlyDictionary<string, (double CurrentX, double CurrentY)> positions)
    {
        foreach (var item in items)
        {
            item.CurrentX = positions[item.Id].CurrentX;
            item.CurrentY = positions[item.Id].CurrentY;
        }
    }

    private static double GetAlignmentCoordinate(
        LayoutItem item,
        LayoutSelectionAlignment alignment)
    {
        var radians = item.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var halfWidth = ((item.Width * cosine) + (item.Height * sine)) / 2d;
        var halfHeight = ((item.Width * sine) + (item.Height * cosine)) / 2d;
        return alignment switch
        {
            LayoutSelectionAlignment.Left => item.CurrentX - halfWidth,
            LayoutSelectionAlignment.HorizontalCenter => item.CurrentX,
            LayoutSelectionAlignment.Right => item.CurrentX + halfWidth,
            LayoutSelectionAlignment.Top => item.CurrentY - halfHeight,
            LayoutSelectionAlignment.VerticalCenter => item.CurrentY,
            LayoutSelectionAlignment.Bottom => item.CurrentY + halfHeight,
            _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, null)
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseDirectSceneAuthoringAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static void Execute(ICommand command)
        {
            if (!command.CanExecute(null))
            {
                throw new InvalidOperationException("Expected layout history command was disabled.");
            }
            command.Execute(null);
        }

        static IReadOnlyDictionary<string, (double X, double Y)> Positions(
            MachineLayoutViewModel layout,
            IEnumerable<string> ids) => ids.ToDictionary(
                id => id,
                id =>
                {
                    var item = layout.Items.Single(candidate => candidate.Id == id);
                    return (item.CurrentX, item.CurrentY);
                },
                StringComparer.Ordinal);

        static bool SamePosition(
            MachineLayoutViewModel layout,
            IReadOnlyDictionary<string, (double X, double Y)> expected) => expected.All(entry =>
        {
            var item = layout.Items.Single(candidate => candidate.Id == entry.Key);
            return item.CurrentX == entry.Value.X && item.CurrentY == entry.Value.Y;
        });

        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var groupIds = new[] { RoundTripStageId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(groupIds, RoundTripCylinderId);
        var groupBefore = Positions(viewModel.Layout, groupIds);
        Check("groupDragRequested", viewport.RequestSelectionDrag(RoundTripCylinderId, new Vector(48, 24)));
        var groupAfter = Positions(viewModel.Layout, groupIds);
        var groupDelta = (
            X: groupAfter[RoundTripCylinderId].X - groupBefore[RoundTripCylinderId].X,
            Y: groupAfter[RoundTripCylinderId].Y - groupBefore[RoundTripCylinderId].Y);
        Check("groupDragApplied", groupDelta != default);
        Check(
            "groupOffsetsPreserved",
            groupIds.All(id =>
                groupAfter[id].X - groupBefore[id].X == groupDelta.X &&
                groupAfter[id].Y - groupBefore[id].Y == groupDelta.Y));
        Check(
            "groupDragSnapped",
            Math.Abs(groupDelta.X % viewModel.Layout.GridSize) < 0.000001 &&
            Math.Abs(groupDelta.Y % viewModel.Layout.GridSize) < 0.000001);
        Check("groupDragCreatedHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));

        Execute(viewModel.UndoLayoutEditCommand);
        Check("groupDragUndo", SamePosition(viewModel.Layout, groupBefore));
        Check("oneUndoEntryPerGesture", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.RedoLayoutEditCommand);
        Check("groupDragRedo", SamePosition(viewModel.Layout, groupAfter));
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select(RoundTripCylinderId);
        var singleBefore = Positions(viewModel.Layout, new[] { RoundTripCylinderId });
        Check("singleDragRequested", viewport.RequestSelectionDrag(RoundTripCylinderId, new Vector(32, -18)));
        var singleAfter = Positions(viewModel.Layout, new[] { RoundTripCylinderId });
        Check("singleDragApplied", !SamePosition(viewModel.Layout, singleBefore));
        Check("newDragClearsRedo", !viewModel.RedoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        Check("singleDragUndo", SamePosition(viewModel.Layout, singleBefore));

        var stageBounds = viewport.GetItemScreenBounds(RoundTripStageId)
            ?? throw new InvalidOperationException("Stage screen bounds were unavailable.");
        stageBounds.Inflate(2, 2);
        var stageMarqueeIds = viewport.RequestMarqueeSelection(stageBounds, ModifierKeys.None);
        Check(
            "marqueeReplace",
            stageMarqueeIds.Contains(RoundTripStageId, StringComparer.Ordinal) &&
            viewModel.Layout.SelectedItems.All(item => item.Kind != LayoutItemKind.MachineFrame) &&
            viewModel.Layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(stageMarqueeIds));

        var cylinderBounds = viewport.GetItemScreenBounds(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder screen bounds were unavailable.");
        cylinderBounds.Inflate(2, 2);
        viewport.RequestMarqueeSelection(cylinderBounds, ModifierKeys.Shift);
        Check(
            "marqueeShiftAdd",
            viewModel.Layout.SelectedItems.Any(item => item.Id == RoundTripStageId) &&
            viewModel.Layout.SelectedItems.Any(item => item.Id == RoundTripCylinderId));
        viewport.RequestMarqueeSelection(stageBounds, ModifierKeys.Control);
        Check(
            "marqueeControlToggle",
            viewModel.Layout.SelectedItems.All(item => item.Id != RoundTripStageId) &&
            viewModel.Layout.SelectedItems.Any(item => item.Id == RoundTripCylinderId));
        Check(
            "marqueeDoesNotCreateHistory",
            !viewModel.UndoLayoutEditCommand.CanExecute(null) &&
            viewModel.RedoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.Select(RoundTripStageId);
        var canceledBefore = Positions(viewModel.Layout, new[] { RoundTripStageId });
        Check("cancelDragBegins", viewModel.Layout.BeginSelectionDrag());
        viewModel.Layout.UpdateSelectionDrag(30, 20);
        viewModel.Layout.CancelSelectionDrag();
        Check("cancelDragRestores", SamePosition(viewModel.Layout, canceledBefore));
        Check("cancelDragDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.IsRunMode = true;
        Check("runPolicyBlocksDrag", !viewModel.Layout.BeginSelectionDrag());
        viewModel.IsRunMode = false;

        viewModel.Layout.Select(RoundTripCylinderId);
        viewport.RequestSelectionDrag(RoundTripCylinderId, new Vector(44, 0));
        var persistedPosition = Positions(viewModel.Layout, new[] { RoundTripCylinderId })[RoundTripCylinderId];
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "direct-scene-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        Check(
            "dragPersists",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentX == persistedPosition.X &&
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentY == persistedPosition.Y);
        Check(
            "reopenDoesNotRun",
            viewModel.IsDesignMode && !viewModel.IsRunning && !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseCanvasNavigationAsync(
        ShellWindow window,
        MainViewModel viewModel)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewport.FitToLayout();
        var initialCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable.");
        var initialBounds = viewport.GetItemScreenBounds(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder bounds were unavailable.");

        var wheelScreenPoint = viewport.PointToScreen(initialCenter);
        SetCursorPos((int)Math.Round(wheelScreenPoint.X), (int)Math.Round(wheelScreenPoint.Y));
        var wheelAnchor = Mouse.GetPosition(viewport);
        viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("wheelZoomRequested", viewport.ZoomFactor > 1d);
        var zoomedCenter = viewport.GetItemCenter(RoundTripCylinderId) ?? default;
        var zoomedBounds = viewport.GetItemScreenBounds(RoundTripCylinderId) ?? Rect.Empty;
        Check("wheelZoomApplied", viewport.ZoomFactor > 1d && zoomedBounds.Width > initialBounds.Width);
        var expectedZoomedCenter = wheelAnchor + ((initialCenter - wheelAnchor) * viewport.ZoomFactor);
        Check("wheelAnchorPreserved", (zoomedCenter - expectedZoomedCenter).Length < 0.001d);
        Check("zoomedHitTest", viewport.SelectItemAt(zoomedCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);

        var panDelta = new Vector(46, -28);
        var panStart = viewport.PointToScreen(zoomedCenter);
        var panEnd = viewport.PointToScreen(zoomedCenter + panDelta);
        SetCursorPos((int)Math.Round(panStart.X), (int)Math.Round(panStart.Y));
        var actualPanStart = Mouse.GetPosition(viewport);
        mouse_event(MouseEventMiddleDown, 0, 0, 0, UIntPtr.Zero);
        viewport.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Middle)
        {
            RoutedEvent = Mouse.MouseDownEvent
        });
        await Task.Delay(50);
        SetCursorPos((int)Math.Round(panEnd.X), (int)Math.Round(panEnd.Y));
        var actualPanEnd = Mouse.GetPosition(viewport);
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        mouse_event(MouseEventMiddleUp, 0, 0, 0, UIntPtr.Zero);
        viewport.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Middle)
        {
            RoutedEvent = Mouse.MouseUpEvent
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var pannedCenter = viewport.GetItemCenter(RoundTripCylinderId) ?? default;
        var expectedPanDelta = actualPanEnd - actualPanStart;
        Check("middlePanRequested", (pannedCenter - zoomedCenter).Length > 1d);
        Check("middlePanApplied", ((pannedCenter - zoomedCenter) - expectedPanDelta).Length < 0.001d);
        Check("pannedHitTest", viewport.SelectItemAt(pannedCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);
        Check("navigationDoesNotCreateHistoryBeforeDrag", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        var cylinder = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var beforeDragX = cylinder.CurrentX;
        Check("dragAfterNavigationRequested", viewport.RequestSelectionDrag(
            RoundTripCylinderId,
            new Vector(24, 0)));
        Check("dragAfterNavigationApplied", cylinder.CurrentX != beforeDragX);
        Check("dragCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("dragAfterNavigationUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentX == beforeDragX &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        var pannedBounds = viewport.GetItemScreenBounds(RoundTripCylinderId) ?? Rect.Empty;
        pannedBounds.Inflate(2, 2);
        var marqueeIds = viewport.RequestMarqueeSelection(pannedBounds, ModifierKeys.None);
        Check("marqueeAfterNavigation", marqueeIds.Contains(RoundTripCylinderId, StringComparer.Ordinal));

        var document = FindVisualDescendant<SceneDocumentView>(window)
            ?? throw new InvalidOperationException("Scene document view was not available.");
        var fitButton = document.FindName("FitLayoutButton") as Button
            ?? throw new InvalidOperationException("Fit layout button was not available.");
        fitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var fittedCenter = viewport.GetItemCenter(RoundTripCylinderId) ?? default;
        var fittedBounds = viewport.GetItemScreenBounds(RoundTripCylinderId) ?? Rect.Empty;
        Check("fitResetsZoom", Math.Abs(viewport.ZoomFactor - 1d) < 0.000001d);
        Check("fitButtonInvoked", fitButton.IsEnabled);
        Check("fitRestoresView", (fittedCenter - initialCenter).Length < 0.001d &&
            Math.Abs(fittedBounds.Width - initialBounds.Width) < 0.001d);
        Check("navigationDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseDirectTransformHandlesAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static void Execute(ICommand command)
        {
            if (!command.CanExecute(null))
            {
                throw new InvalidOperationException("Expected layout history command was disabled.");
            }
            command.Execute(null);
        }

        static (double X, double Y) Corner(LayoutItem item, double signX, double signY)
        {
            var radians = item.CurrentRotationDegrees * Math.PI / 180d;
            var axisXX = Math.Cos(radians);
            var axisXY = Math.Sin(radians);
            var axisYX = -axisXY;
            var axisYY = axisXX;
            return (
                item.CurrentX + (signX * item.CurrentWidth * axisXX / 2d) +
                    (signY * item.CurrentHeight * axisYX / 2d),
                item.CurrentY + (signX * item.CurrentWidth * axisXY / 2d) +
                    (signY * item.CurrentHeight * axisYY / 2d));
        }

        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        viewModel.Layout.Select(RoundTripCylinderId);
        viewport.FitToLayout();

        var initial = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var initialWidth = initial.CurrentWidth;
        var initialHeight = initial.CurrentHeight;
        var initialRotation = initial.CurrentRotationDegrees;
        var initialBinding = initial.BehaviorBindingId;
        var fixedCornerBefore = Corner(initial, -1d, -1d);
        var itemCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable.");
        viewport.ZoomAt(itemCenter, 120);
        viewport.PanBy(new Vector(38, -22));

        var resizeHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight);
        Check("singleSelectionShowsResizeHandle", resizeHandle is not null);
        var rotationHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.Rotation);
        Check("singleSelectionShowsRotationHandle", rotationHandle is not null);
        if (resizeHandle is null || rotationHandle is null)
        {
            throw new InvalidOperationException("Transform handles were unavailable.");
        }

        var cursorPoint = viewport.PointToScreen(resizeHandle.Value);
        SetCursorPos((int)Math.Round(cursorPoint.X), (int)Math.Round(cursorPoint.Y));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        Check("resizeHandleCursor", viewport.Cursor == Cursors.SizeNWSE);
        Check("navigationDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        Check("resizeRequested", viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight,
            resizeHandle.Value + new Vector(48, 32)));
        var resized = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var fixedCornerAfter = Corner(resized, -1d, -1d);
        Check("resizeApplied", resized.CurrentWidth > initialWidth && resized.CurrentHeight > initialHeight);
        Check("resizeSnapped",
            Math.Abs(resized.CurrentWidth % viewModel.Layout.GridSize) < 0.000001d &&
            Math.Abs(resized.CurrentHeight % viewModel.Layout.GridSize) < 0.000001d);
        Check("resizeFixedOppositeCorner",
            Math.Abs(fixedCornerAfter.X - fixedCornerBefore.X) < 0.000001d &&
            Math.Abs(fixedCornerAfter.Y - fixedCornerBefore.Y) < 0.000001d);
        Check("resizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        var resizeUndone = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("resizeUndo", resizeUndone.CurrentWidth == initialWidth &&
            resizeUndone.CurrentHeight == initialHeight);
        Check("oneUndoEntryPerResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.RedoLayoutEditCommand);
        Check("resizeRedo", viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentWidth ==
            resized.CurrentWidth);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select(RoundTripCylinderId);
        var aspectHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Aspect-ratio resize handle was unavailable.");
        Check("aspectRatioResizeRequested", viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight,
            aspectHandle + new Vector(80, 10),
            ModifierKeys.Shift));
        var aspectResized = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var aspectFixedCorner = Corner(aspectResized, -1d, -1d);
        var aspectWidthScale = aspectResized.CurrentWidth / initialWidth;
        var aspectHeightScale = aspectResized.CurrentHeight / initialHeight;
        Check("aspectRatioResizeApplied", aspectResized.CurrentWidth > initialWidth &&
            aspectResized.CurrentHeight > initialHeight);
        Check("aspectRatioPreserved", Math.Abs(aspectWidthScale - aspectHeightScale) < 0.000001d);
        Check("aspectRatioKeepsOppositeCorner",
            Math.Abs(aspectFixedCorner.X - fixedCornerBefore.X) < 0.000001d &&
            Math.Abs(aspectFixedCorner.Y - fixedCornerBefore.Y) < 0.000001d);
        Check("aspectRatioPreservesRotationAndBinding",
            aspectResized.CurrentRotationDegrees == initialRotation &&
            string.Equals(aspectResized.BehaviorBindingId, initialBinding, StringComparison.Ordinal));
        Check("aspectRatioResizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        Check("aspectRatioResizeUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentWidth == initialWidth &&
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentHeight == initialHeight);
        Check("oneUndoEntryPerAspectRatioResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.RedoLayoutEditCommand);
        Check("aspectRatioResizeRedo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentWidth ==
                aspectResized.CurrentWidth &&
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentHeight ==
                aspectResized.CurrentHeight);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select(RoundTripCylinderId);
        var rotationCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable after resize Undo.");
        Check("rotationRequested", viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.Rotation,
            rotationCenter + new Vector(64, 0)));
        var rotated = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("rotationApplied", Math.Abs(rotated.CurrentRotationDegrees - 90d) < 0.000001d);
        Check("rotationCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        Check("rotationUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentRotationDegrees ==
            initialRotation);
        Check("oneUndoEntryPerRotation", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.Select(RoundTripCylinderId);
        var cancelItem = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("cancelBegins", viewModel.Layout.BeginSelectionTransform(LayoutTransformHandle.TopLeft));
        viewModel.Layout.UpdateSelectionTransform(
            cancelItem.CurrentX - 40,
            cancelItem.CurrentY - 25,
            preserveAspectRatio: true);
        viewModel.Layout.CancelSelectionTransform();
        Check("cancelRestores", cancelItem.CurrentWidth == initialWidth &&
            cancelItem.CurrentHeight == initialHeight &&
            cancelItem.CurrentRotationDegrees == initialRotation);
        Check("cancelDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.SelectMany(new[] { RoundTripStageId, RoundTripCylinderId }, RoundTripCylinderId);
        Check("multiSelectionHidesHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is null);
        viewModel.Layout.Select(RoundTripCylinderId);
        viewModel.IsRunMode = true;
        Check("runModeHidesHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is null);
        Check("runModeBlocksTransform",
            !viewModel.Layout.BeginSelectionTransform(LayoutTransformHandle.BottomRight));
        viewModel.IsRunMode = false;

        viewModel.Layout.Select(RoundTripCylinderId);
        var finalHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Final resize handle was unavailable.");
        viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight,
            finalHandle + new Vector(56, 18),
            ModifierKeys.Shift);
        var persisted = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var persistedSize = (persisted.CurrentWidth, persisted.CurrentHeight);
        Check("persistedAspectRatioPreserved",
            Math.Abs(
                (persisted.CurrentWidth / initialWidth) -
                (persisted.CurrentHeight / initialHeight)) < 0.000001d);
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "direct-transform-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        var reopened = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("transformPersists", reopened.CurrentWidth == persistedSize.CurrentWidth &&
            reopened.CurrentHeight == persistedSize.CurrentHeight);
        viewModel.Layout.Select(RoundTripCylinderId);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("aspectRatioHintVisible", FindVisualDescendant<RightToolRegionView>(window)?
            .AspectRatioHintText is { IsVisible: true, Text.Length: > 0 });
        Check("reopenDoesNotRun", viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeLayoutHistoryReport> ExerciseLayoutHistoryAsync(
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var pastedComponentIds = Array.Empty<string>();

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static void Execute(ICommand command, object? parameter = null)
        {
            if (!command.CanExecute(parameter))
            {
                throw new InvalidOperationException("Expected layout edit command was disabled.");
            }
            command.Execute(parameter);
        }

        viewModel.Layout.Select(RoundTripCylinderId);
        var originalEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Cylinder property editor was not available.");
        var originalName = originalEditor.Name;
        var originalRotation = originalEditor.RotationDegrees;
        var originalWidth = originalEditor.Width;
        var originalHeight = originalEditor.Height;
        var originalStroke = originalEditor.CylinderStroke;

        originalEditor.Name = "History Cylinder";
        originalEditor.RotationDegrees = originalRotation + 15;
        originalEditor.Width = originalWidth + 10;
        originalEditor.Height = originalHeight + 8;
        originalEditor.CylinderStroke = originalStroke + 5;
        for (var index = 0; index < 5; index++)
        {
            Execute(viewModel.UndoLayoutEditCommand);
        }

        var undoneEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Undo did not restore the property editor.");
        Check(
            "propertyUndo",
            undoneEditor.Name == originalName &&
            undoneEditor.RotationDegrees == originalRotation &&
            undoneEditor.Width == originalWidth &&
            undoneEditor.Height == originalHeight &&
            undoneEditor.CylinderStroke == originalStroke);

        for (var index = 0; index < 5; index++)
        {
            Execute(viewModel.RedoLayoutEditCommand);
        }
        var redoneEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Redo did not restore the property editor.");
        Check(
            "propertyRedo",
            redoneEditor.Name == "History Cylinder" &&
            redoneEditor.RotationDegrees == originalRotation + 15 &&
            redoneEditor.Width == originalWidth + 10 &&
            redoneEditor.Height == originalHeight + 8 &&
            redoneEditor.CylinderStroke == originalStroke + 5);

        Execute(viewModel.UndoLayoutEditCommand);
        var branchEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Undo did not restore the branch editor.");
        branchEditor.CylinderStroke = originalStroke + 7;
        Check("newEditClearsRedo", !viewModel.RedoLayoutEditCommand.CanExecute(null));

        var moveIds = new[] { RoundTripStageId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(moveIds, RoundTripCylinderId);
        var beforeMove = viewModel.Layout.SelectedItems.ToDictionary(
            item => item.Id,
            item => (item.CurrentX, item.CurrentY),
            StringComparer.Ordinal);
        Execute(viewModel.NudgeLayoutComponentCommand, "Right");
        var step = viewModel.Layout.GridSize;
        Check(
            "groupMoveApplied",
            viewModel.Layout.SelectedItems.All(item =>
                item.CurrentX == beforeMove[item.Id].CurrentX + step &&
                item.CurrentY == beforeMove[item.Id].CurrentY));
        Execute(viewModel.UndoLayoutEditCommand);
        Check(
            "groupMoveUndo",
            viewModel.Layout.SelectedItems.All(item =>
                item.CurrentX == beforeMove[item.Id].CurrentX &&
                item.CurrentY == beforeMove[item.Id].CurrentY));
        Execute(viewModel.RedoLayoutEditCommand);
        Check(
            "groupMoveRedo",
            viewModel.Layout.SelectedItems.All(item =>
                item.CurrentX == beforeMove[item.Id].CurrentX + step &&
                item.CurrentY == beforeMove[item.Id].CurrentY));

        var alignIds = new[] { RoundTripAlignedComponentId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(alignIds, RoundTripCylinderId);
        var beforeAlignX = viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX;
        Execute(viewModel.AlignLayoutSelectionCommand, nameof(LayoutSelectionAlignment.HorizontalCenter));
        var alignedX = viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX;
        Check("alignmentApplied", alignedX != beforeAlignX);
        Execute(viewModel.UndoLayoutEditCommand);
        Check(
            "alignmentUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX == beforeAlignX);
        Execute(viewModel.RedoLayoutEditCommand);
        Check(
            "alignmentRedo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX == alignedX);

        var initialComponentCount = viewModel.Layout.Items.Count;
        Execute(viewModel.AddLayoutComponentCommand, LayoutComponentKind.MachineFrame);
        var addedFrameId = viewModel.Layout.SelectedItem?.Id;
        Check("addApplied", viewModel.Layout.Items.Count == initialComponentCount + 1 && addedFrameId is not null);
        Execute(viewModel.UndoLayoutEditCommand);
        Check("addUndo", viewModel.Layout.Items.Count == initialComponentCount);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("addRedo", viewModel.Layout.Items.Count == initialComponentCount + 1);

        viewModel.Layout.Select(addedFrameId!);
        Execute(viewModel.DeleteLayoutComponentCommand);
        Check("deleteApplied", viewModel.Layout.Items.Count == initialComponentCount);
        Execute(viewModel.UndoLayoutEditCommand);
        Check("deleteUndo", viewModel.Layout.Items.Count == initialComponentCount + 1);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("deleteRedo", viewModel.Layout.Items.Count == initialComponentCount);

        var copyIds = new[]
        {
            "stage-1",
            "sensor-1",
            "sensor-home",
            "cylinder-1",
            "conveyor-1",
            "workpiece-1"
        };
        viewModel.Layout.SelectMany(copyIds, "workpiece-1");
        Execute(viewModel.CopyLayoutSelectionCommand);
        Execute(viewModel.PasteLayoutSelectionCommand);
        pastedComponentIds = viewModel.Layout.SelectedItems.Select(item => item.Id).ToArray();
        var pastedCount = viewModel.Layout.Items.Count;
        Check("multiPasteApplied", pastedComponentIds.Length == copyIds.Length && pastedCount == initialComponentCount + copyIds.Length);

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "layout-history-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        var savedProject = await new ProjectDocumentStore().LoadAsync(projectPath);
        var validation = new MachineProjectLayoutValidator().Validate(savedProject);
        Check("pastedProjectValid", validation.IsValid);
        var projectStore = new ProjectDocumentStore();
        var invalidPasteTarget = projectStore.Load(projectStore.Serialize(savedProject));
        var invalidPasteLayout = invalidPasteTarget.Layouts.Single(layout =>
            layout.Id == invalidPasteTarget.Simulation.ActiveLayoutId);
        invalidPasteLayout.Components[1].Id = invalidPasteLayout.Components[0].Id;
        var invalidPasteCounts = (
            invalidPasteLayout.Components.Count,
            invalidPasteTarget.Axes.Count,
            invalidPasteTarget.Devices.Count,
            invalidPasteTarget.Channels.Count);
        var atomicClipboard = new LayoutComponentClipboard();
        atomicClipboard.Copy(
            savedProject,
            savedProject.Layouts.Single(layout => layout.Id == savedProject.Simulation.ActiveLayoutId),
            new[] { copyIds[0] });
        var failedPaste = atomicClipboard.Paste(invalidPasteTarget, invalidPasteLayout);
        Check(
            "failedPasteIsAtomic",
            !failedPaste.IsSuccess &&
            invalidPasteCounts == (
                invalidPasteLayout.Components.Count,
                invalidPasteTarget.Axes.Count,
                invalidPasteTarget.Devices.Count,
                invalidPasteTarget.Channels.Count));
        Check(
            "uniqueDefinitionIds",
            savedProject.Layouts.SelectMany(layout => layout.Components).Select(component => component.Id).Distinct(StringComparer.Ordinal).Count() ==
                savedProject.Layouts.SelectMany(layout => layout.Components).Count() &&
            savedProject.Axes.Select(axis => axis.Id).Distinct(StringComparer.Ordinal).Count() == savedProject.Axes.Count &&
            savedProject.Devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count() == savedProject.Devices.Count &&
            savedProject.Channels.Select(channel => channel.Id).Distinct(StringComparer.Ordinal).Count() == savedProject.Channels.Count);

        var pastedComponents = savedProject.Layouts
            .SelectMany(layout => layout.Components)
            .Where(component => pastedComponentIds.Contains(component.Id, StringComparer.Ordinal))
            .ToArray();
        var pastedConveyor = pastedComponents.Single(component => component.Kind == LayoutComponentKind.Conveyor);
        var pastedWorkpiece = pastedComponents.Single(component => component.Kind == LayoutComponentKind.Workpiece);
        var pastedWorkpieceDevice = savedProject.Devices.Single(device => device.Id == pastedWorkpiece.BehaviorBindingId);
        var pastedSensors = pastedComponents.Where(component => component.Kind == LayoutComponentKind.DigitalSensor).ToArray();
        Check(
            "internalBindingGraphRemapped",
            pastedWorkpieceDevice.Workpiece?.ConveyorComponentId == pastedConveyor.Id &&
            pastedSensors.All(component =>
                savedProject.Devices.Single(device => device.Id == component.BehaviorBindingId)
                    .Sensor?.TargetComponentId == pastedWorkpiece.Id));

        var pastedCylinderBindingId = pastedComponents
            .Single(component => component.Kind == LayoutComponentKind.PneumaticCylinder)
            .BehaviorBindingId;
        viewModel.Layout.Select("cylinder-1");
        var bindingEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalBindingId = bindingEditor.BehaviorBindingId;
        bindingEditor.BehaviorBindingId = pastedCylinderBindingId;
        Execute(viewModel.UndoLayoutEditCommand);
        Check("behaviorBindingUndo", viewModel.Layout.SelectedComponentEditor?.BehaviorBindingId == originalBindingId);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("behaviorBindingRedo", viewModel.Layout.SelectedComponentEditor?.BehaviorBindingId == pastedCylinderBindingId);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select("sensor-1");
        var sensorEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalSensorDelay = sensorEditor.SensorOnDelayMilliseconds;
        sensorEditor.SensorOnDelayMilliseconds = originalSensorDelay + 3;
        Execute(viewModel.UndoLayoutEditCommand);
        Check("sensorPropertyUndo", viewModel.Layout.SelectedComponentEditor?.SensorOnDelayMilliseconds == originalSensorDelay);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("sensorPropertyRedo", viewModel.Layout.SelectedComponentEditor?.SensorOnDelayMilliseconds == originalSensorDelay + 3);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select("conveyor-1");
        var conveyorEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalConveyorSpeed = conveyorEditor.ConveyorSpeedUnitsPerSecond;
        conveyorEditor.ConveyorSpeedUnitsPerSecond = originalConveyorSpeed + 10;
        Execute(viewModel.UndoLayoutEditCommand);
        Check("conveyorPropertyUndo", viewModel.Layout.SelectedComponentEditor?.ConveyorSpeedUnitsPerSecond == originalConveyorSpeed);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("conveyorPropertyRedo", viewModel.Layout.SelectedComponentEditor?.ConveyorSpeedUnitsPerSecond == originalConveyorSpeed + 10);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select("workpiece-1");
        var workpieceEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalWorkpieceType = workpieceEditor.WorkpieceType;
        workpieceEditor.WorkpieceType = "History Part";
        Execute(viewModel.UndoLayoutEditCommand);
        Check("workpiecePropertyUndo", viewModel.Layout.SelectedComponentEditor?.WorkpieceType == originalWorkpieceType);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("workpiecePropertyRedo", viewModel.Layout.SelectedComponentEditor?.WorkpieceType == "History Part");
        Execute(viewModel.UndoLayoutEditCommand);

        Execute(viewModel.UndoLayoutEditCommand);
        Check("pasteUndo", viewModel.Layout.Items.Count == initialComponentCount);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("pasteRedo", viewModel.Layout.Items.Count == pastedCount);

        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        Check(
            "historyAndClipboardNotPersisted",
            !viewModel.UndoLayoutEditCommand.CanExecute(null) &&
            !viewModel.RedoLayoutEditCommand.CanExecute(null) &&
            !viewModel.PasteLayoutSelectionCommand.CanExecute(null));
        Check("reopenDoesNotRun", viewModel.IsDesignMode && !viewModel.IsRunning);

        return new SmokeLayoutHistoryReport
        {
            Checks = checks,
            PastedComponentIds = pastedComponentIds,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseLayerOrderAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static void Execute(ICommand command, LayoutLayerOrder order)
        {
            var parameter = order.ToString();
            if (!command.CanExecute(parameter))
            {
                throw new InvalidOperationException($"Layer order command '{order}' was disabled.");
            }
            command.Execute(parameter);
        }

        IReadOnlyList<string> Order() => viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .OrderBy(item => item.ZIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();

        IReadOnlyDictionary<string, int> ZState() => viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .ToDictionary(item => item.Id, item => item.ZIndex, StringComparer.Ordinal);

        IReadOnlyDictionary<string, (double X, double Y, double Width, double Height, double Rotation)> GeometryState() =>
            viewModel.Layout.Items
                .Where(item => item.Component is not null)
                .ToDictionary(
                    item => item.Id,
                    item => (item.CurrentX, item.CurrentY, item.CurrentWidth, item.CurrentHeight, item.CurrentRotationDegrees),
                    StringComparer.Ordinal);

        static bool SameZ(
            IReadOnlyDictionary<string, int> expected,
            IReadOnlyDictionary<string, int> actual) =>
            expected.Count == actual.Count && expected.All(pair =>
                actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

        static bool SameGeometry(
            IReadOnlyDictionary<string, (double X, double Y, double Width, double Height, double Rotation)> expected,
            IReadOnlyDictionary<string, (double X, double Y, double Width, double Height, double Rotation)> actual) =>
            expected.Count == actual.Count && expected.All(pair =>
                actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        var inspector = FindVisualDescendant<OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was not available.");
        var layerPanel = FindVisualDescendant<Border>(inspector, element => element.Name == "LayerOrderPanel")
            ?? throw new InvalidOperationException("Layer order panel was not available.");
        Button ButtonNamed(string name) => FindVisualDescendant<Button>(
                inspector,
                button => string.Equals(button.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Layer order button '{name}' was not available.");
        var sendToBackButton = ButtonNamed("SendToBackButton");
        var sendBackwardButton = ButtonNamed("SendBackwardButton");
        var bringForwardButton = ButtonNamed("BringForwardButton");
        var bringToFrontButton = ButtonNamed("BringToFrontButton");

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var baselinePath = Path.Combine(reportDirectory, "layer-order-baseline.ovmachine");
        await viewModel.SaveProjectAsync(baselinePath);
        Check("baselineOpen", await viewModel.OpenProjectAsync(baselinePath));

        viewModel.Layout.Select(RoundTripCylinderId);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("layerPanelVisible", layerPanel.IsVisible);
        Check("fourLayerButtonsVisible", new[]
        {
            sendToBackButton,
            sendBackwardButton,
            bringForwardButton,
            bringToFrontButton
        }.All(button => button.IsVisible && !string.IsNullOrWhiteSpace(button.Content?.ToString())));
        Check("layerTooltipsAvailable", new[]
        {
            sendToBackButton,
            sendBackwardButton,
            bringForwardButton,
            bringToFrontButton
        }.All(button => !string.IsNullOrWhiteSpace(button.ToolTip?.ToString())));

        var stage = viewModel.Layout.Items.Single(item => item.Id == RoundTripStageId);
        var cylinder = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        stage.CurrentX = cylinder.CurrentX;
        stage.CurrentY = cylinder.CurrentY;
        viewport.FitToLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var overlapCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Overlapping item center was unavailable.");
        Check("initialTopHitUsesZIndex", viewport.SelectItemAt(overlapCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);

        var overlapGeometry = GeometryState();
        var bindingIds = viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .ToDictionary(item => item.Id, item => item.BehaviorBindingId, StringComparer.Ordinal);
        viewModel.Layout.Select(RoundTripStageId);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("uiButtonEnabled", bringToFrontButton.IsEnabled);
        var buttonPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(bringToFrontButton);
        var invokeProvider = buttonPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
            as System.Windows.Automation.Provider.IInvokeProvider
            ?? throw new InvalidOperationException("Bring to front button did not expose the invoke pattern.");
        invokeProvider.Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("uiButtonChangesTopHit", viewport.SelectItemAt(overlapCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripStageId);
        Check("layerChangePreservesGeometry", SameGeometry(overlapGeometry, GeometryState()));
        Check("layerChangePreservesBindings", viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .All(item => string.Equals(bindingIds[item.Id], item.BehaviorBindingId, StringComparison.Ordinal)));
        viewModel.UndoLayoutEditCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("uiButtonUndoRestoresTopHit", viewport.SelectItemAt(overlapCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);

        Check("restoreAfterOverlap", await viewModel.OpenProjectAsync(baselinePath));
        var originalZ = ZState();
        var originalOrder = Order();
        viewModel.Layout.Select(RoundTripCylinderId);
        var initialIndex = Array.IndexOf(originalOrder.ToArray(), RoundTripCylinderId);
        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.BringForward);
        var forwardOrder = Order();
        Check("singleForwardOneLayer", Array.IndexOf(forwardOrder.ToArray(), RoundTripCylinderId) == initialIndex + 1);
        Check("singleForwardCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("singleForwardUndo", SameZ(originalZ, ZState()));
        Check("singleForwardOneHistoryEntry", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("singleForwardRedo", Order().SequenceEqual(forwardOrder));
        viewModel.UndoLayoutEditCommand.Execute(null);

        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.SendBackward);
        Check("singleBackwardOneLayer", Array.IndexOf(Order().ToArray(), RoundTripCylinderId) == initialIndex - 1);
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("singleBackwardUndo", SameZ(originalZ, ZState()) && !viewModel.UndoLayoutEditCommand.CanExecute(null));

        var selectedIds = new[] { RoundTripStageId, RoundTripCylinderId };
        var selectedSet = selectedIds.ToHashSet(StringComparer.Ordinal);
        viewModel.Layout.SelectMany(selectedIds, RoundTripCylinderId);
        var selectedRelativeOrder = originalOrder.Where(selectedSet.Contains).ToArray();
        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.SendToBack);
        var backOrder = Order();
        Check("multiSendToBack", backOrder.Take(selectedIds.Length).All(selectedSet.Contains));
        Check("multiRelativeOrderPreservedAtBack", backOrder.Take(selectedIds.Length).SequenceEqual(selectedRelativeOrder));
        Check("multiSelectionPreserved", viewModel.Layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(selectedSet));
        Check("multiSendToBackCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("multiSendToBackUndo", SameZ(originalZ, ZState()));
        Check("multiSendToBackOneHistoryEntry", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("multiSendToBackRedo", Order().SequenceEqual(backOrder));
        viewModel.UndoLayoutEditCommand.Execute(null);

        viewModel.Layout.SelectMany(selectedIds, RoundTripCylinderId);
        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.BringToFront);
        var frontOrder = Order();
        Check("multiBringToFront", frontOrder.TakeLast(selectedIds.Length).All(selectedSet.Contains));
        Check("multiRelativeOrderPreservedAtFront", frontOrder.TakeLast(selectedIds.Length).SequenceEqual(selectedRelativeOrder));
        Check("frontBoundaryDisablesForward", !viewModel.ChangeLayoutLayerOrderCommand.CanExecute(
            LayoutLayerOrder.BringForward.ToString()) &&
            !viewModel.ChangeLayoutLayerOrderCommand.CanExecute(LayoutLayerOrder.BringToFront.ToString()));
        Check("normalizedZIndexesUnique", ZState().Values.Distinct().Count() == viewModel.Layout.Items.Count);

        var persistedZ = ZState();
        var persistedBindings = viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .ToDictionary(item => item.Id, item => item.BehaviorBindingId, StringComparer.Ordinal);
        viewModel.IsRunMode = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("runModeHidesLayerControls", !layerPanel.IsVisible);
        Check("runModeBlocksLayerOrder", !viewModel.ChangeLayoutLayerOrderCommand.CanExecute(
            LayoutLayerOrder.SendToBack.ToString()));
        viewModel.IsRunMode = false;

        var roundTripPath = Path.Combine(reportDirectory, "layer-order-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(roundTripPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(roundTripPath));
        Check("layerOrderPersists", SameZ(persistedZ, ZState()));
        Check("bindingsPersist", viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .All(item => string.Equals(persistedBindings[item.Id], item.BehaviorBindingId, StringComparison.Ordinal)));
        Check("reopenStoppedAndHistoryCleared", viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseMultiSelectionTransformAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static IReadOnlyDictionary<string, (
            double X,
            double Y,
            double Width,
            double Height,
            double Rotation,
            string? BindingId)> States(
                MachineLayoutViewModel layout,
                IEnumerable<string> ids) => ids.ToDictionary(
                    id => id,
                    id =>
                    {
                        var item = layout.Items.Single(candidate => candidate.Id == id);
                        return (
                            item.CurrentX,
                            item.CurrentY,
                            item.CurrentWidth,
                            item.CurrentHeight,
                            item.CurrentRotationDegrees,
                            item.BehaviorBindingId);
                    },
                    StringComparer.Ordinal);

        static bool Same(
            MachineLayoutViewModel layout,
            IReadOnlyDictionary<string, (
                double X,
                double Y,
                double Width,
                double Height,
                double Rotation,
                string? BindingId)> expected) => expected.All(entry =>
            {
                var item = layout.Items.Single(candidate => candidate.Id == entry.Key);
                return item.CurrentX == entry.Value.X &&
                    item.CurrentY == entry.Value.Y &&
                    item.CurrentWidth == entry.Value.Width &&
                    item.CurrentHeight == entry.Value.Height &&
                    item.CurrentRotationDegrees == entry.Value.Rotation &&
                    string.Equals(item.BehaviorBindingId, entry.Value.BindingId, StringComparison.Ordinal);
            });

        static (double MinimumX, double MinimumY, double MaximumX, double MaximumY) Bounds(
            IReadOnlyDictionary<string, (
                double X,
                double Y,
                double Width,
                double Height,
                double Rotation,
                string? BindingId)> states)
        {
            var minimumX = double.PositiveInfinity;
            var minimumY = double.PositiveInfinity;
            var maximumX = double.NegativeInfinity;
            var maximumY = double.NegativeInfinity;
            foreach (var state in states.Values)
            {
                var radians = state.Rotation * Math.PI / 180d;
                var cosine = Math.Abs(Math.Cos(radians));
                var sine = Math.Abs(Math.Sin(radians));
                var halfWidth = ((state.Width * cosine) + (state.Height * sine)) / 2d;
                var halfHeight = ((state.Width * sine) + (state.Height * cosine)) / 2d;
                minimumX = Math.Min(minimumX, state.X - halfWidth);
                minimumY = Math.Min(minimumY, state.Y - halfHeight);
                maximumX = Math.Max(maximumX, state.X + halfWidth);
                maximumY = Math.Max(maximumY, state.Y + halfHeight);
            }
            return (minimumX, minimumY, maximumX, maximumY);
        }

        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        var ids = new[] { RoundTripStageId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        viewport.FitToLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var initial = States(viewModel.Layout, ids);
        var initialBounds = Bounds(initial);
        var initialBindings = initial.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.BindingId,
            StringComparer.Ordinal);
        var bottomRight = viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight);
        var rotationHandle = viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.Rotation);
        Check("groupShowsResizeHandle", bottomRight is not null);
        Check("groupShowsRotationHandle", rotationHandle is not null);
        Check("groupHidesSingleItemHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is null);
        if (bottomRight is null || rotationHandle is null)
        {
            throw new InvalidOperationException("Multi-selection transform handles were unavailable.");
        }

        var center = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable.");
        viewport.ZoomAt(center, 120);
        viewport.PanBy(new Vector(34, -20));
        bottomRight = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Group resize handle was unavailable after navigation.");
        var cursorPoint = viewport.PointToScreen(bottomRight.Value);
        SetCursorPos((int)Math.Round(cursorPoint.X), (int)Math.Round(cursorPoint.Y));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        Check("groupResizeCursor", viewport.Cursor == Cursors.SizeNWSE);
        Check("navigationDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        Check("groupResizeRequested", viewport.RequestSelectionTransform(
            LayoutTransformHandle.BottomRight,
            bottomRight.Value + new Vector(72, 44)));
        var resized = States(viewModel.Layout, ids);
        var resizedBounds = Bounds(resized);
        Check("groupResizeApplied", ids.All(id =>
            resized[id].Width > initial[id].Width &&
            resized[id].Height > initial[id].Height));
        Check("groupResizeKeepsOppositeCorner",
            Math.Abs(resizedBounds.MinimumX - initialBounds.MinimumX) < 0.000001d &&
            Math.Abs(resizedBounds.MinimumY - initialBounds.MinimumY) < 0.000001d);
        Check("groupResizeUsesCommonScale",
            Math.Abs(
                (resized[ids[0]].Width / initial[ids[0]].Width) -
                (resized[ids[1]].Width / initial[ids[1]].Width)) < 0.000001d &&
            Math.Abs(
                (resized[ids[0]].Height / initial[ids[0]].Height) -
                (resized[ids[1]].Height / initial[ids[1]].Height)) < 0.000001d);
        Check("groupResizePreservesBindings", ids.All(id => string.Equals(
            resized[id].BindingId,
            initialBindings[id],
            StringComparison.Ordinal)));
        Check("groupResizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("groupResizeUndo", Same(viewModel.Layout, initial));
        Check("oneUndoEntryPerGroupResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("groupResizeRedo", Same(viewModel.Layout, resized));
        viewModel.UndoLayoutEditCommand.Execute(null);

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        var aspectHandle = viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Group aspect-ratio resize handle was unavailable.");
        Check("groupAspectRatioResizeRequested", viewport.RequestSelectionTransform(
            LayoutTransformHandle.BottomRight,
            aspectHandle + new Vector(96, 24),
            ModifierKeys.Shift));
        var aspectResized = States(viewModel.Layout, ids);
        var aspectBounds = Bounds(aspectResized);
        var groupAspectWidthScale =
            (aspectBounds.MaximumX - aspectBounds.MinimumX) /
            (initialBounds.MaximumX - initialBounds.MinimumX);
        var groupAspectHeightScale =
            (aspectBounds.MaximumY - aspectBounds.MinimumY) /
            (initialBounds.MaximumY - initialBounds.MinimumY);
        Check("groupAspectRatioResizeApplied", ids.All(id =>
            aspectResized[id].Width > initial[id].Width &&
            aspectResized[id].Height > initial[id].Height));
        Check("groupAspectRatioPreserved",
            Math.Abs(groupAspectWidthScale - groupAspectHeightScale) < 0.000001d);
        Check("groupAspectRatioUsesUniformItemScale", ids.All(id =>
            Math.Abs(
                (aspectResized[id].Width / initial[id].Width) -
                (aspectResized[id].Height / initial[id].Height)) < 0.000001d &&
            Math.Abs(
                (aspectResized[id].Width / initial[id].Width) -
                groupAspectWidthScale) < 0.000001d));
        Check("groupAspectRatioKeepsOppositeCorner",
            Math.Abs(aspectBounds.MinimumX - initialBounds.MinimumX) < 0.000001d &&
            Math.Abs(aspectBounds.MinimumY - initialBounds.MinimumY) < 0.000001d);
        Check("groupAspectRatioPreservesRotationsAndBindings", ids.All(id =>
            aspectResized[id].Rotation == initial[id].Rotation &&
            string.Equals(aspectResized[id].BindingId, initialBindings[id], StringComparison.Ordinal)));
        Check("groupAspectRatioResizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("groupAspectRatioResizeUndo", Same(viewModel.Layout, initial));
        Check("oneUndoEntryPerGroupAspectRatioResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("groupAspectRatioResizeRedo", Same(viewModel.Layout, aspectResized));
        viewModel.UndoLayoutEditCommand.Execute(null);

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        var topLeft = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.TopLeft)
            ?? throw new InvalidOperationException("Group top-left handle was unavailable.");
        var groupBottomRight = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Group bottom-right handle was unavailable.");
        var groupCenter = new Point(
            (topLeft.X + groupBottomRight.X) / 2d,
            (topLeft.Y + groupBottomRight.Y) / 2d);
        Check("groupRotationRequested", viewport.RequestSelectionTransform(
            LayoutTransformHandle.Rotation,
            groupCenter + new Vector(80, 0)));
        var rotated = States(viewModel.Layout, ids);
        var initialCenterX = (initialBounds.MinimumX + initialBounds.MaximumX) / 2d;
        var initialCenterY = (initialBounds.MinimumY + initialBounds.MaximumY) / 2d;
        Check("groupRotationApplied", ids.All(id =>
            Math.Abs(rotated[id].Rotation - 90d) < 0.000001d));
        Check("groupCentersRotateTogether", ids.All(id =>
            Math.Abs(rotated[id].X - (initialCenterX - (initial[id].Y - initialCenterY))) < 0.000001d &&
            Math.Abs(rotated[id].Y - (initialCenterY + (initial[id].X - initialCenterX))) < 0.000001d));
        Check("groupRotationPreservesSize", ids.All(id =>
            rotated[id].Width == initial[id].Width &&
            rotated[id].Height == initial[id].Height));
        Check("groupRotationPreservesBindings", ids.All(id => string.Equals(
            rotated[id].BindingId,
            initialBindings[id],
            StringComparison.Ordinal)));
        Check("groupRotationCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("groupRotationUndo", Same(viewModel.Layout, initial));
        Check("oneUndoEntryPerGroupRotation", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        Check("groupCancelBegins", viewModel.Layout.BeginSelectionTransform(
            LayoutTransformHandle.BottomRight));
        viewModel.Layout.UpdateSelectionTransform(
            initialBounds.MaximumX + 100,
            initialBounds.MaximumY + 80,
            preserveAspectRatio: true);
        viewModel.Layout.CancelSelectionTransform();
        Check("groupCancelRestores", Same(viewModel.Layout, initial));
        Check("groupCancelDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.IsRunMode = true;
        Check("runModeHidesGroupHandles", viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight) is null);
        Check("runModeBlocksGroupTransform", !viewModel.Layout.BeginSelectionTransform(
            LayoutTransformHandle.BottomRight));
        viewModel.IsRunMode = false;

        viewModel.Layout.Select(RoundTripCylinderId);
        Check("singleSelectionStillShowsHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is not null);
        Check("singleSelectionHidesGroupHandles", viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight) is null);

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        topLeft = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.TopLeft)
            ?? throw new InvalidOperationException("Final group top-left handle was unavailable.");
        groupBottomRight = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Final group bottom-right handle was unavailable.");
        groupCenter = new Point(
            (topLeft.X + groupBottomRight.X) / 2d,
            (topLeft.Y + groupBottomRight.Y) / 2d);
        viewport.RequestSelectionTransform(
            LayoutTransformHandle.Rotation,
            groupCenter + new Vector(80, 0));
        var persisted = States(viewModel.Layout, ids);
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "multi-selection-transform-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        Check("groupTransformPersists", Same(viewModel.Layout, persisted));
        Check("reopenDoesNotRun", viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static async Task<SmokeDirectSceneAuthoringReport> ExerciseLibrarySceneDropAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var initialCount = viewModel.Layout.Items.Count;
        foreach (var kind in Enum.GetValues<LayoutComponentKind>())
        {
            viewport.FitToLayout();
            var screenPoint = new Point(
                viewport.ActualWidth * 0.68,
                viewport.ActualHeight * 0.34);
            if (kind == LayoutComponentKind.MachineFrame)
            {
                viewport.ZoomAt(screenPoint, 120);
                viewport.PanBy(new Vector(36, -22));
            }
            var worldPoint = viewport.GetDropWorldPoint(screenPoint)
                ?? throw new InvalidOperationException("Library drop world point was unavailable.");
            var expectedX = Math.Round(
                worldPoint.X / viewModel.Layout.GridSize,
                MidpointRounding.AwayFromZero) * viewModel.Layout.GridSize;
            var expectedY = Math.Round(
                worldPoint.Y / viewModel.Layout.GridSize,
                MidpointRounding.AwayFromZero) * viewModel.Layout.GridSize;

            Check($"{kind}.dropRequested", viewport.RequestLibraryComponentDrop(kind, screenPoint));
            var added = viewModel.Layout.SelectedItem;
            Check($"{kind}.addedAndSelected",
                viewModel.Layout.Items.Count == initialCount + 1 &&
                added?.Component?.Kind == kind);
            Check($"{kind}.usesViewportProjection",
                added is not null &&
                added.CurrentX == expectedX &&
                added.CurrentY == expectedY);
            Check($"{kind}.snappedToGrid",
                added is not null &&
                Math.Abs(added.CurrentX % viewModel.Layout.GridSize) < 0.000001d &&
                Math.Abs(added.CurrentY % viewModel.Layout.GridSize) < 0.000001d);
            Check($"{kind}.createdHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));

            viewModel.UndoLayoutEditCommand.Execute(null);
            Check($"{kind}.undoRemovesDrop", viewModel.Layout.Items.Count == initialCount);
            Check($"{kind}.oneUndoEntry", !viewModel.UndoLayoutEditCommand.CanExecute(null));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        Check(
            "clickAddStillAvailable",
            viewModel.AddLayoutComponentCommand.CanExecute(LayoutComponentKind.MachineFrame));
        viewModel.AddLayoutComponentCommand.Execute(LayoutComponentKind.MachineFrame);
        Check(
            "clickAddStillUsesSharedPath",
            viewModel.Layout.Items.Count == initialCount + 1 &&
            viewModel.Layout.SelectedItem?.Component?.Kind == LayoutComponentKind.MachineFrame);
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("clickAddUndo", viewModel.Layout.Items.Count == initialCount);

        viewModel.IsRunMode = true;
        var runCount = viewModel.Layout.Items.Count;
        Check(
            "runModeBlocksDrop",
            !viewport.RequestLibraryComponentDrop(
                LayoutComponentKind.PneumaticCylinder,
                new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2)) &&
            viewModel.Layout.Items.Count == runCount);
        viewModel.IsRunMode = false;

        viewport.FitToLayout();
        var persistedScreenPoint = new Point(
            viewport.ActualWidth * 0.76,
            viewport.ActualHeight * 0.29);
        Check(
            "persistenceDropRequested",
            viewport.RequestLibraryComponentDrop(LayoutComponentKind.Conveyor, persistedScreenPoint));
        var persisted = viewModel.Layout.SelectedItem
            ?? throw new InvalidOperationException("Dropped conveyor was not selected.");
        var persistedId = persisted.Id;
        var persistedPosition = (persisted.CurrentX, persisted.CurrentY);

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "library-drop-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        var reopened = viewModel.Layout.Items.SingleOrDefault(item => item.Id == persistedId);
        Check(
            "dropPersists",
            reopened is not null &&
            reopened.CurrentX == persistedPosition.CurrentX &&
            reopened.CurrentY == persistedPosition.CurrentY);

        var storedProject = new ProjectDocumentStore().Load(File.ReadAllText(projectPath));
        var storedComponent = storedProject.Layouts
            .SelectMany(layout => layout.Components)
            .Single(component => component.Id == persistedId);
        var storedDevice = storedProject.Devices.Single(device =>
            string.Equals(device.Id, storedComponent.BehaviorBindingId, StringComparison.Ordinal));
        Check(
            "boundDeviceStartsAtDropPosition",
            storedDevice.MountPosition.X == persistedPosition.CurrentX &&
            storedDevice.MountPosition.Y == persistedPosition.CurrentY);
        Check(
            "reopenDoesNotRun",
            viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static SmokeProjectRoundTripReport CreateRoundTripReport(
        string phase,
        string projectPath,
        ShellWindow window,
        MainViewModel viewModel)
    {
        var failures = new List<string>();
        var stage = viewModel.Layout.Items.FirstOrDefault(item =>
            string.Equals(item.Id, RoundTripStageId, StringComparison.Ordinal));
        var alignedItem = viewModel.Layout.Items.FirstOrDefault(item =>
            string.Equals(item.Id, RoundTripAlignedComponentId, StringComparison.Ordinal));
        var step = viewModel.SequenceEditor.Steps.FirstOrDefault(item =>
            string.Equals(item.Id, RoundTripStepId, StringComparison.Ordinal));
        SelectNode(viewModel.ProjectTree, "x");
        var axisEditor = viewModel.AxisDriveTuningEditor;
        window.UpdateLayout();
        if (axisEditor is null)
        {
            failures.Add("Axis drive tuning editor was not restored.");
        }
        else
        {
            CheckValue("Axis maximum velocity", axisEditor.MaxVelocity, RoundTripAxisMaxVelocity);
            CheckValue("Axis maximum acceleration", axisEditor.MaxAcceleration, RoundTripAxisMaxAcceleration);
            CheckValue("Axis maximum deceleration", axisEditor.MaxDeceleration, RoundTripAxisMaxDeceleration);
            CheckValue("Axis following-error limit", axisEditor.FollowingErrorLimit, RoundTripAxisFollowingErrorLimit);
            if (axisEditor.HasValidationErrors)
            {
                failures.Add($"Restored axis tuning was invalid: {axisEditor.ValidationMessage}");
            }

            var tuningPanel = FindVisualDescendant<Border>(
                window,
                element => string.Equals(element.Name, "AxisDriveTuningPanel", StringComparison.Ordinal));
            var visibleValues = tuningPanel is null
                ? Array.Empty<TextBox>()
                : FindVisualDescendants<TextBox>(tuningPanel)
                    .Where(textBox => textBox.IsVisible && !string.IsNullOrWhiteSpace(textBox.Text))
                    .ToArray();
            if (tuningPanel is null || !tuningPanel.IsVisible || visibleValues.Length < 4)
            {
                failures.Add("Axis drive tuning inputs did not render representative non-empty values.");
            }
        }

        foreach (var item in viewModel.Layout.Items.Where(item => item.Component is not null))
        {
            viewModel.Layout.Select(item.Id);
            var editor = viewModel.Layout.SelectedComponentEditor;
            if (editor is null)
            {
                failures.Add($"Layout property editor was unavailable for '{item.Id}'.");
                continue;
            }

            if (editor.HasValidationErrors)
            {
                failures.Add($"Layout property editor for '{item.Id}' was invalid: {editor.ValidationMessage}");
            }

            if (item.Component!.Kind != OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.MachineFrame &&
                editor.BehaviorBindingOptions.Count == 0)
            {
                failures.Add($"Layout property editor for '{item.Id}' had no compatible behavior binding.");
            }

            if (viewModel.Properties.Items.Any(property =>
                    property.Value.Contains("OpenVisionLab.", StringComparison.Ordinal)))
            {
                failures.Add($"Layout properties for '{item.Id}' exposed a CLR type name.");
            }
        }

        viewModel.Layout.Select(RoundTripCylinderId);
        var cylinderItem = viewModel.Layout.SelectedItem;
        var cylinderEditor = viewModel.Layout.SelectedComponentEditor;

        if (stage is null)
        {
            failures.Add($"Layout item '{RoundTripStageId}' was not restored.");
        }
        else if (Math.Abs(stage.CurrentX - RoundTripStageX) > 0.001)
        {
            failures.Add(
                $"Layout X was {stage.CurrentX:F3}; expected {RoundTripStageX:F3}.");
        }

        if (alignedItem is null)
        {
            failures.Add($"Layout item '{RoundTripAlignedComponentId}' was not restored.");
        }
        else if (Math.Abs(alignedItem.CurrentX - RoundTripAlignedComponentX) > 0.001)
        {
            failures.Add(
                $"Aligned component X was {alignedItem.CurrentX:F3}; " +
                $"expected {RoundTripAlignedComponentX:F3}.");
        }
        if (step is null)
        {
            failures.Add($"Sequence step '{RoundTripStepId}' was not restored.");
        }
        else if (!string.Equals(step.Name, RoundTripStepName, StringComparison.Ordinal))
        {
            failures.Add(
                $"Sequence step name was '{step.Name}'; expected '{RoundTripStepName}'.");
        }
        else
        {
            if (!string.Equals(
                    step.ExpectedTargetId,
                    RoundTripStepCheckpointTargetId,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"Sequence checkpoint target was '{step.ExpectedTargetId}'; " +
                    $"expected '{RoundTripStepCheckpointTargetId}'.");
            }
            if (!string.Equals(
                    step.ExpectedState,
                    RoundTripStepCheckpointState,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"Sequence checkpoint state was '{step.ExpectedState}'; " +
                    $"expected '{RoundTripStepCheckpointState}'.");
            }
        }

        if (cylinderItem is null || cylinderEditor is null)
        {
            failures.Add($"Layout item '{RoundTripCylinderId}' was not restored with an editor.");
        }
        else
        {
            if (!string.Equals(cylinderEditor.Name, RoundTripCylinderName, StringComparison.Ordinal))
            {
                failures.Add($"Component name was '{cylinderEditor.Name}'; expected '{RoundTripCylinderName}'.");
            }
            if (Math.Abs(cylinderEditor.RotationDegrees - RoundTripCylinderRotation) > 0.001)
            {
                failures.Add($"Component rotation was {cylinderEditor.RotationDegrees:F3}; expected {RoundTripCylinderRotation:F3}.");
            }
            if (Math.Abs(cylinderEditor.Width - RoundTripCylinderWidth) > 0.001 ||
                Math.Abs(cylinderEditor.Height - RoundTripCylinderHeight) > 0.001)
            {
                failures.Add(
                    $"Component size was {cylinderEditor.Width:F3} x {cylinderEditor.Height:F3}; " +
                    $"expected {RoundTripCylinderWidth:F3} x {RoundTripCylinderHeight:F3}.");
            }
            if (Math.Abs(cylinderEditor.CylinderExtendDurationMilliseconds - RoundTripCylinderExtendDuration) > 0.001)
            {
                failures.Add(
                    $"Cylinder extend duration was {cylinderEditor.CylinderExtendDurationMilliseconds:F0}; " +
                    $"expected {RoundTripCylinderExtendDuration}.");
            }
            if (Math.Abs(cylinderEditor.CylinderStroke - RoundTripCylinderStroke) > 0.001)
            {
                failures.Add(
                    $"Cylinder stroke was {cylinderEditor.CylinderStroke:F3}; expected {RoundTripCylinderStroke:F3}.");
            }
        }
        if (!viewModel.IsDesignMode)
        {
            failures.Add("Project restore changed the application out of Design mode.");
        }

        if (viewModel.IsRunning)
        {
            failures.Add("Project restore started the simulation.");
        }

        if (!viewModel.SimulationStatusText.EndsWith(
                "00:00:00.000",
                StringComparison.Ordinal))
        {
            failures.Add(
                $"Simulation time advanced during restore: {viewModel.SimulationStatusText}.");
        }

        if (!string.Equals(
                viewModel.CurrentAxisStateText,
                OpenVisionLanguageService.T("Equipment.State.Idle", "Idle", "Idle"),
                StringComparison.Ordinal))
        {
            failures.Add($"Axis state after restore was {viewModel.CurrentAxisStateText}.");
        }

        if (viewModel.HasVirtualCamera &&
            !string.Equals(
                viewModel.CurrentCameraStateText,
                OpenVisionLanguageService.T("Equipment.State.Idle", "Idle", "Idle"),
                StringComparison.Ordinal))
        {
            failures.Add($"Camera state after restore was {viewModel.CurrentCameraStateText}.");
        }

        if (!string.Equals(
                viewModel.CurrentSequenceStateText,
                OpenVisionLanguageService.T("Equipment.State.Ready", "Ready", "Ready"),
                StringComparison.Ordinal))
        {
            failures.Add($"Sequence state after restore was {viewModel.CurrentSequenceStateText}.");
        }

        if (viewModel.FaultManager.ActiveFaults.Count != 0)
        {
            failures.Add(
                $"Project restore retained {viewModel.FaultManager.ActiveFaults.Count} active fault(s).");
        }

        if (string.Equals(phase, "SaveReload", StringComparison.Ordinal))
        {
            if (!string.Equals(
                    viewModel.SimulationWorkspace.SelectedScenarioProfile.ProfileId,
                    RoundTripScenarioProfileId,
                    StringComparison.Ordinal))
            {
                failures.Add("Test Scenario profile was not restored.");
            }

            if (viewModel.SimulationWorkspace.ScenarioSeed != RoundTripScenarioSeed)
            {
                failures.Add(
                    $"Test Scenario seed was {viewModel.SimulationWorkspace.ScenarioSeed}; " +
                    $"expected {RoundTripScenarioSeed}.");
            }

            if (viewModel.SimulationWorkspace.ScenarioDurationCycles != RoundTripScenarioDuration)
            {
                failures.Add(
                    $"Test Scenario duration was {viewModel.SimulationWorkspace.ScenarioDurationCycles}; " +
                    $"expected {RoundTripScenarioDuration}.");
            }

            if (!string.Equals(
                    viewModel.SimulationWorkspace.ScenarioTargetId,
                    RoundTripScenarioTargetId,
                    StringComparison.Ordinal))
            {
                failures.Add("Test Scenario target was not restored.");
            }

            if (viewModel.ConditionScenario.IsConfigured || viewModel.ConditionScenario.IsActive)
            {
                failures.Add("Project restore configured or started a runtime Test Scenario.");
            }
        }

        return new SmokeProjectRoundTripReport
        {
            Phase = phase,
            ProjectPath = Path.GetFullPath(projectPath),
            ExpectedStageX = RoundTripStageX,
            ActualStageX = stage?.CurrentX ?? double.NaN,
            ExpectedStepName = RoundTripStepName,
            ActualStepName = step?.Name ?? string.Empty,
            ExpectedStepCheckpointTargetId = RoundTripStepCheckpointTargetId,
            ActualStepCheckpointTargetId = step?.ExpectedTargetId ?? string.Empty,
            ExpectedStepCheckpointState = RoundTripStepCheckpointState,
            ActualStepCheckpointState = step?.ExpectedState ?? string.Empty,
            ExpectedComponentName = RoundTripCylinderName,
            ActualComponentName = cylinderEditor?.Name ?? string.Empty,
            ExpectedComponentRotation = RoundTripCylinderRotation,
            ActualComponentRotation = cylinderEditor?.RotationDegrees ?? double.NaN,
            ExpectedComponentWidth = RoundTripCylinderWidth,
            ActualComponentWidth = cylinderEditor?.Width ?? double.NaN,
            ExpectedComponentHeight = RoundTripCylinderHeight,
            ActualComponentHeight = cylinderEditor?.Height ?? double.NaN,
            ExpectedCylinderExtendDuration = RoundTripCylinderExtendDuration,
            ActualCylinderExtendDuration = cylinderEditor is null
                ? int.MinValue
                : Convert.ToInt32(cylinderEditor.CylinderExtendDurationMilliseconds),
            ExpectedCylinderStroke = RoundTripCylinderStroke,
            ActualCylinderStroke = cylinderEditor?.CylinderStroke ?? double.NaN,
            ExpectedAxisMaxVelocity = RoundTripAxisMaxVelocity,
            ActualAxisMaxVelocity = axisEditor?.MaxVelocity ?? double.NaN,
            ExpectedAxisMaxAcceleration = RoundTripAxisMaxAcceleration,
            ActualAxisMaxAcceleration = axisEditor?.MaxAcceleration ?? double.NaN,
            ExpectedAxisMaxDeceleration = RoundTripAxisMaxDeceleration,
            ActualAxisMaxDeceleration = axisEditor?.MaxDeceleration ?? double.NaN,
            ExpectedAxisFollowingErrorLimit = RoundTripAxisFollowingErrorLimit,
            ActualAxisFollowingErrorLimit = axisEditor?.FollowingErrorLimit ?? double.NaN,
            ExpectedAlignedComponentX = RoundTripAlignedComponentX,
            ActualAlignedComponentX = alignedItem?.CurrentX ?? double.NaN,
            IsDesignMode = viewModel.IsDesignMode,
            IsRunning = viewModel.IsRunning,
            SimulationStatus = viewModel.SimulationStatusText,
            AxisState = viewModel.CurrentAxisStateText,
            HasVirtualCamera = viewModel.HasVirtualCamera,
            CameraState = viewModel.CurrentCameraStateText,
            SequenceState = viewModel.CurrentSequenceStateText,
            ActiveFaultCount = viewModel.FaultManager.ActiveFaults.Count,
            Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window),
            Failures = failures
        };

        void CheckValue(string name, double actual, double expected)
        {
            if (Math.Abs(actual - expected) > 0.000001)
            {
                failures.Add($"{name} was {actual:G6}; expected {expected:G6}.");
            }
        }
    }

    private static async Task<SmokePerformanceReport> MeasureSmokePerformanceAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string requestedSize,
        int requestedScalePercent,
        double startupToIdleMs,
        int navigationSampleCount,
        int steadySampleCount)
    {
        var dispatcher = window.Dispatcher;
        var navigationTimings = await MeasureNavigationTimingsAsync(
            viewModel,
            dispatcher,
            Math.Max(1, navigationSampleCount));
        var steadyTimings = await MeasureSteadyInteractionTimingsAsync(
            window,
            viewModel,
            dispatcher,
            Math.Max(1, steadySampleCount));

        return new SmokePerformanceReport
        {
            WindowTitle = window.Title,
            RequestedSize = requestedSize,
            RequestedScalePercent = requestedScalePercent,
            Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window),
            StartupToIdleMs = startupToIdleMs,
            NavigationTimingsMs = navigationTimings,
            SteadyInteractionTimingsMs = steadyTimings,
            NavigationMeanMs = CalculateMean(navigationTimings),
            NavigationP95Ms = CalculatePercentile(navigationTimings, 0.95),
            SteadyInteractionMeanMs = CalculateMean(steadyTimings),
            SteadyInteractionP95Ms = CalculatePercentile(steadyTimings, 0.95)
        };
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject parent,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static async Task<IReadOnlyList<double>> MeasureNavigationTimingsAsync(
        MainViewModel viewModel,
        Dispatcher dispatcher,
        int sampleCount)
    {
        var samples = new List<double>();
        var navigationPaths = BuildNavigationPaths(viewModel.ProjectTree).ToArray();
        if (navigationPaths.Length == 0)
        {
            return samples;
        }

        var firstPath = navigationPaths[0];
        var secondPath = navigationPaths[Math.Min(1, navigationPaths.Length - 1)];

        // Warm the first tree-selection transition so lazy WPF template creation
        // is not mixed into the repeated navigation measurement.
        SelectNode(viewModel.ProjectTree, firstPath);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SelectNode(viewModel.ProjectTree, secondPath);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var targetPath = sample % 2 == 0 ? firstPath : secondPath;
            var stopwatch = Stopwatch.StartNew();
            var selected = SelectNode(viewModel.ProjectTree, targetPath);
            if (selected is not null)
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return samples;
    }

    private static async Task<IReadOnlyList<double>> MeasureSteadyInteractionTimingsAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Dispatcher dispatcher,
        int sampleCount)
    {
        var samples = new List<double>();
        var wasRunMode = viewModel.IsRunMode;

        // Warm the initial Design -> Run transition so lazy template creation
        // is not counted as a steady-state mode interaction.
        viewModel.IsRunMode = !wasRunMode;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            // Measure both directions as one sample so the metric represents a
            // steady interaction cycle instead of alternating-direction bias.
            var stopwatch = Stopwatch.StartNew();
            viewModel.IsRunMode = wasRunMode;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            viewModel.IsRunMode = !wasRunMode;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds / 2d);
        }

        if (viewModel.IsRunMode != wasRunMode)
        {
            viewModel.IsRunMode = wasRunMode;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        ValidateModeCommandSources(window, viewModel);
        viewModel.IsRunMode = !wasRunMode;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        ValidateModeCommandSources(window, viewModel);
        viewModel.IsRunMode = wasRunMode;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        return samples;
    }

    private static void ValidateModeCommandSources(
        ShellWindow window,
        MainViewModel viewModel)
    {
        _ = viewModel.RunCommand;
        _ = viewModel.PauseCommand;
        _ = viewModel.StepCommand;
        _ = viewModel.ResetCommand;
        _ = viewModel.AddLayoutComponentCommand;
        var checkedSourceCount = 0;
        foreach (var button in FindVisualDescendants<Button>(window))
        {
            if (!button.IsVisible
                || button.Command is not (RelayCommand or AsyncRelayCommand))
            {
                continue;
            }

            checkedSourceCount++;
            var expected = button.Command.CanExecute(button.CommandParameter);
            if (button.IsEnabled != expected)
            {
                throw new InvalidOperationException(
                    $"Visible mode command source '{button.Name}' ({button.Content}) did not refresh " +
                    $"its enabled state: actual={button.IsEnabled}, expected={expected}.");
            }
        }

        if (checkedSourceCount == 0)
        {
            throw new InvalidOperationException("No visible mode command source was available for validation.");
        }
    }

    private static IEnumerable<string> BuildNavigationPaths(ProjectTreeViewModel projectTree)
    {
        foreach (var root in projectTree.Roots)
        {
            foreach (var path in BuildNavigationPathsFromNode(root, root.Id))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> BuildNavigationPathsFromNode(
        TreeNodeViewModel node,
        string pathPrefix)
    {
        yield return pathPrefix;

        foreach (var child in node.Children)
        {
            foreach (var nested in BuildNavigationPathsFromNode(child, $"{pathPrefix}/{child.Id}"))
            {
                yield return nested;
            }
        }
    }

    private static double CalculateMean(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        return Math.Round(values.Average(), 3);
    }

    private static double CalculatePercentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var safePercentile = Math.Clamp(percentile, 0, 1);
        var index = (int)Math.Ceiling(safePercentile * sorted.Length) - 1;
        var clampedIndex = Math.Clamp(index, 0, sorted.Length - 1);
        return Math.Round(sorted[clampedIndex], 3);
    }

    private static TreeNodeViewModel? SelectNode(ProjectTreeViewModel tree, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        TreeNodeViewModel? current = FindByName(tree.Roots, parts[0]);
        if (current is null)
            return null;

        for (var i = 1; i < parts.Length; i++)
        {
            current = FindByName(current.Children, parts[i]);
            if (current is null)
                return null;
        }

        tree.SelectedNode = current;
        return current;
    }

    private static TreeNodeViewModel? FindByName(IEnumerable<TreeNodeViewModel> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindByName(node.Children, name);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static string? GetArgumentValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static bool HasArgument(string[] args, string key) =>
        args.Any(argument => string.Equals(argument, key, StringComparison.OrdinalIgnoreCase));

    private static int ParseIntArgument(
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

    private static (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split("x", StringSplitOptions.None);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var w) &&
            int.TryParse(parts[1], out var h))
        {
            return (w, h);
        }
        return (1280, 760);
    }

    private static int ParseDpiScalePercent(string? value)
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
