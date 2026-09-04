using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeStationSkeletonReport
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

internal static class SmokeStationSkeletonVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "station-skeleton-focus"
        or "station-skeleton-hover"
        or "station-skeleton-pressed"
        or "station-skeleton-preview"
        or "station-skeleton-apply-focus"
        or "station-skeleton-apply-pressed"
        or "station-skeleton-invalid"
        or "station-skeleton-input-focus"
        or "station-skeleton-input-disabled"
        or "station-skeleton-applied"
        or "station-skeleton-reopen";

    public static bool RequiresProjectPreparation(string? state) =>
        IsSupportedState(state)
        && !string.Equals(state, "station-skeleton-reopen", StringComparison.OrdinalIgnoreCase);

    public static async Task PrepareProjectAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject)
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

    public static async Task<SmokeStationSkeletonReport> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string stationState,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Button stationSkeletonButton,
        string? savePath,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<TextBox, bool>, TextBox?> findTextBox,
        Action activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed, string message)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(message);
                throw new InvalidOperationException(message);
            }
        }

        SmokeStationSkeletonReport CreateReport() => new()
        {
            Checks = checks,
            Failures = failures
        };

        try
        {
            var normalizedState = stationState.ToLowerInvariant();
            if (!IsSupportedState(normalizedState))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-connection-workbench-state '{stationState}'. " +
                    "Expected a supported station-skeleton smoke state.");
            }

            if (normalizedState == "station-skeleton-focus")
            {
                window.Activate();
                stationSkeletonButton.Focus();
                Keyboard.Focus(stationSkeletonButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "station-skeleton-button-focus",
                    stationSkeletonButton.IsKeyboardFocused,
                    "Semiconductor station button did not receive focus.");
                return CreateReport();
            }

            if (normalizedState is "station-skeleton-hover" or "station-skeleton-pressed")
            {
                activateWindow();
                stationSkeletonButton.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                movePointerToCenter(stationSkeletonButton);
                await Task.Delay(100);
                Check(
                    "station-skeleton-button-hover",
                    stationSkeletonButton.IsMouseOver,
                    "Semiconductor station button did not enter hover state.");
                if (normalizedState == "station-skeleton-pressed")
                {
                    mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    markSmokePointerHeld();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(
                        "station-skeleton-button-pressed",
                        stationSkeletonButton.IsPressed,
                        "Semiconductor station button did not enter pointer-down state.");
                }

                return CreateReport();
            }

            if (normalizedState == "station-skeleton-reopen")
            {
                Check(
                    "reopened-preview-command-available",
                    stationSkeletonButton.Command?.CanExecute(stationSkeletonButton.CommandParameter) == true,
                    "Rendered station skeleton Preview command was not available after reopen.");
                stationSkeletonButton.Command!.Execute(stationSkeletonButton.CommandParameter);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var reopenedStationApplyButton = findButton(
                    workbench,
                    candidate => string.Equals(
                        candidate.Name,
                        "ApplySemiconductorStationSkeletonButton",
                        StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("Reopened station Apply button was not available.");
                Check(
                    "reopened-station-skeleton-restored",
                    vm.RecipeConnections.StationSetups.StationSkeletonProposedCount == 0
                    && vm.RecipeConnections.StationSetups.StationSkeletonItems.Count(item => item.IsAlreadyConfigured) == 10
                    && reopenedStationApplyButton.IsEnabled
                    && vm.RecipeConnections.StationSetups.StationName == "Lithography Transfer A"
                    && vm.RecipeConnections.StationSetups.WaferType == "200 mm Wafer"
                    && vm.RecipeConnections.StationSetups.AxisTravelText == "460"
                    && vm.RecipeConnections.StationSetups.TransportSpeedText == "175"
                    && vm.RecipeConnections.StationSetups.EntrySensorPositionText == "145"
                    && vm.RecipeConnections.StationSetups.ProcessSensorPositionText == "510"
                    && vm.RecipeConnections.StationSetups.CylinderTravelTimeText == "180"
                    && !vm.IsRunning
                    && vm.IsDesignMode,
                    "Reopened station skeleton was not recognized as complete and stopped.");
                return CreateReport();
            }

            var stationProject = initialProject
                ?? throw new InvalidOperationException("Station skeleton project was not available.");
            var stationStore = new ProjectDocumentStore();
            var stationBeforePreview = stationStore.Serialize(stationProject);
            var stationRuntimeBefore = vm.SceneSnapshots.Latest;
            var previewCommandAvailable =
                stationSkeletonButton.Command?.CanExecute(stationSkeletonButton.CommandParameter) == true;
            if (!previewCommandAvailable)
            {
                throw new InvalidOperationException("Rendered station skeleton Preview command was not available.");
            }

            stationSkeletonButton.Command!.Execute(stationSkeletonButton.CommandParameter);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var stationPreviewPanel = findBorder(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "SemiconductorStationSkeletonPreview",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Station skeleton preview was not available.");
            var stationApplyButton = findButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ApplySemiconductorStationSkeletonButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Station skeleton Apply button was not available.");
            var stationCancelButton = findButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "CancelSemiconductorStationSkeletonButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Station skeleton Cancel button was not available.");
            var stationResetButton = findButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ResetSemiconductorStationSetupButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Station setup Reset button was not available.");
            var stationNameTextBox = findTextBox(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "StationSetupNameTextBox",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Station setup name input was not available.");
            Check(
                "ten-missing-roles-previewed",
                stationPreviewPanel.IsVisible
                && vm.RecipeConnections.StationSetups.StationSkeletonProposedCount == 10
                && vm.RecipeConnections.StationSetups.StationSkeletonItems.Count == 10
                && vm.RecipeConnections.StationSetups.StationSkeletonItems.All(item => item.IsProposed),
                "Ten missing station roles were not previewed.");
            Check(
                "rendered-preview-command-invoked",
                previewCommandAvailable,
                "Rendered station skeleton Preview command was not invoked.");
            Check(
                "preview-project-unchanged",
                stationBeforePreview == stationStore.Serialize(stationProject),
                "Station skeleton preview changed the project.");
            Check(
                "preview-runtime-unchanged",
                stationApplyButton.IsEnabled
                && !vm.IsRunning
                && vm.IsDesignMode,
                "Station skeleton preview changed the project or runtime before Apply.");

            if (normalizedState == "station-skeleton-input-focus")
            {
                const string longStationName = "Photolithography Wafer Transfer Station A";
                stationNameTextBox.Text = longStationName;
                stationNameTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                window.Activate();
                stationNameTextBox.Focus();
                Keyboard.Focus(stationNameTextBox);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "station-skeleton-input-focus",
                    stationNameTextBox.IsKeyboardFocused
                    && stationNameTextBox.Text == longStationName
                    && vm.RecipeConnections.StationSetups.StationName == longStationName,
                    "Station setup name input did not retain its long value with keyboard focus.");
                return CreateReport();
            }

            if (normalizedState == "station-skeleton-input-disabled")
            {
                vm.RecipeConnections.IsEditable = false;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "station-skeleton-inputs-disabled",
                    !stationNameTextBox.IsEnabled && !stationApplyButton.IsEnabled,
                    "Station setup inputs did not enter their disabled state.");
                return CreateReport();
            }

            if (normalizedState == "station-skeleton-invalid")
            {
                vm.RecipeConnections.StationSetups.AxisTravelText = "-1";
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "station-skeleton-invalid-blocks-apply",
                    vm.RecipeConnections.StationSetups.HasStationSetupValidationError
                    && !stationApplyButton.IsEnabled
                    && stationBeforePreview == stationStore.Serialize(stationProject),
                    "Invalid station setup did not block Apply without changing the project.");
                return CreateReport();
            }

            if (normalizedState == "station-skeleton-preview")
            {
                return CreateReport();
            }

            if (normalizedState == "station-skeleton-applied")
            {
                var cancelCommandAvailable =
                    stationCancelButton.Command?.CanExecute(stationCancelButton.CommandParameter) == true;
                if (!cancelCommandAvailable)
                {
                    throw new InvalidOperationException("Rendered station skeleton Cancel command was not available.");
                }

                stationCancelButton.Command!.Execute(stationCancelButton.CommandParameter);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "rendered-cancel-preserved-project",
                    cancelCommandAvailable
                    && !vm.RecipeConnections.StationSetups.IsStationSkeletonPreviewVisible
                    && stationBeforePreview == stationStore.Serialize(stationProject),
                    "Canceling the station setup changed the project or left the preview open.");
                stationSkeletonButton.Command!.Execute(stationSkeletonButton.CommandParameter);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var resetCommandAvailable =
                    stationResetButton.Command?.CanExecute(stationResetButton.CommandParameter) == true;
                if (!resetCommandAvailable)
                {
                    throw new InvalidOperationException(
                        "Rendered station setup Reset command was not available after reopening Preview.");
                }

                stationResetButton.Command!.Execute(stationResetButton.CommandParameter);
                Check(
                    "defaults-reset-without-project-change",
                    resetCommandAvailable
                    && vm.RecipeConnections.StationSetups.AxisTravelText == "320"
                    && stationBeforePreview == stationStore.Serialize(stationProject),
                    "Resetting the station setup changed the project before Apply.");
                stationNameTextBox.Text = "Lithography Transfer A";
                stationNameTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                vm.RecipeConnections.StationSetups.WaferType = "200 mm Wafer";
                vm.RecipeConnections.StationSetups.AxisTravelText = "460";
                vm.RecipeConnections.StationSetups.TransportSpeedText = "175";
                vm.RecipeConnections.StationSetups.EntrySensorPositionText = "145";
                vm.RecipeConnections.StationSetups.ProcessSensorPositionText = "510";
                vm.RecipeConnections.StationSetups.CylinderTravelTimeText = "180";
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "rendered-input-binding-updated-draft",
                    stationApplyButton.IsEnabled
                    && vm.RecipeConnections.StationSetups.StationName == "Lithography Transfer A"
                    && stationBeforePreview == stationStore.Serialize(stationProject),
                    "Rendered station input binding was not ready to apply without side effects.");
                var applyCommandAvailable =
                    stationApplyButton.Command?.CanExecute(stationApplyButton.CommandParameter) == true;
                if (!applyCommandAvailable)
                {
                    throw new InvalidOperationException("Rendered station skeleton Apply command was not available.");
                }

                stationApplyButton.Command!.Execute(stationApplyButton.CommandParameter);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "custom-setup-applied",
                    !vm.RecipeConnections.StationSetups.IsStationSkeletonPreviewVisible
                    && stationProject.SemiconductorStationSetup?.StationName == "Lithography Transfer A"
                    && stationProject.SemiconductorStationSetup.WaferType == "200 mm Wafer"
                    && stationProject.SemiconductorStationSetup.AxisTravel == 460
                    && stationProject.SemiconductorStationSetup.TransportSpeed == 175
                    && stationProject.SemiconductorStationSetup.EntrySensorPosition == 145
                    && stationProject.SemiconductorStationSetup.ProcessSensorPosition == 510
                    && stationProject.SemiconductorStationSetup.CylinderTravelTimeMilliseconds == 180,
                    "Station skeleton did not apply the custom setup values.");
                Check(
                    "rendered-apply-command-invoked-once",
                    applyCommandAvailable
                    && stationApplyButton.Command?.CanExecute(stationApplyButton.CommandParameter) != true,
                    "Rendered station skeleton Apply command was not invoked exactly once.");
                Check(
                    "seven-connected-layout-components",
                    stationProject.Layouts.Single().Components.Count == 7,
                    "Station skeleton did not create seven connected layout components.");
                Check(
                    "axis-device-channel-graph-created",
                    stationProject.Axes.Count == 1
                    && stationProject.Devices.Count == 5
                    && stationProject.Channels.Count == 9
                    && vm.RecipeConnections.Rows.Count == 7
                    && vm.RecipeConnections.Rows.All(row => row.IsValid),
                    "Station skeleton did not create the connected axis/device/channel graph.");
                Check(
                    "twelve-step-automatic-sequence-created",
                    stationProject.Sequences.Single().Steps.Count == 12,
                    "Station skeleton did not create its twelve-step automatic sequence.");
                Check(
                    "apply-runtime-unchanged",
                    stationRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && stationRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && !vm.IsRunning
                    && vm.IsDesignMode,
                    "Station skeleton Apply caused an unintended runtime action.");
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                Check(
                    "readiness-compilation-passed",
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
                Check(
                    "twelve-step-dry-run-completed",
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
                    && vm.RecipeConnections.RecipeDryRunTimeline.Count == 12,
                    "Applied station skeleton did not complete its 12-step dry run.");

                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    var stationSavePath = Path.GetFullPath(savePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(stationSavePath)!);
                    await vm.SaveProjectAsync(stationSavePath);
                    var stationReopened = await vm.OpenProjectAsync(stationSavePath);
                    Check(
                        "save-reopen-retained-graph",
                        stationReopened
                        && vm.RecipeConnections.Rows.Count == 7
                        && vm.RecipeConnections.RecipeStepCount == 12,
                        "Reopened station skeleton did not retain its graph.");
                    Check(
                        "reopen-stays-stopped-in-design",
                        !vm.IsRunning && vm.IsDesignMode,
                        "Reopened station skeleton did not remain stopped in Design mode.");
                }
                else
                {
                    Check("save-reopen-retained-graph", true, "Station save/reopen was not requested.");
                    Check("reopen-stays-stopped-in-design", true, "Station reopen was not requested.");
                }

                return CreateReport();
            }

            activateWindow();
            stationApplyButton.BringIntoView();
            stationApplyButton.UpdateLayout();
            stationApplyButton.Focus();
            Keyboard.Focus(stationApplyButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "station-skeleton-apply-button-focus",
                stationApplyButton.IsKeyboardFocused,
                "Station skeleton Apply button did not receive focus.");
            if (normalizedState == "station-skeleton-apply-pressed")
            {
                movePointerToCenter(stationApplyButton);
                Mouse.Capture(stationApplyButton, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                Check(
                    "station-skeleton-apply-button-hover",
                    stationApplyButton.IsMouseOver,
                    "Station skeleton Apply button did not enter hover state.");
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                stationApplyButton.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.MouseDownEvent
                });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "station-skeleton-apply-button-pressed",
                    stationApplyButton.IsPressed,
                    "Station skeleton Apply button did not enter pointer-down state.");
            }

            return CreateReport();
        }
        catch (InvalidOperationException) when (failures.Count > 0)
        {
            return CreateReport();
        }
    }
}
