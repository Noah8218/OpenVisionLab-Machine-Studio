using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeLoadLockSetupReport
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

internal static class SmokeLoadLockSetupVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task<SmokeLoadLockSetupReport> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string loadLockState,
        MachineProjectDocument initialProject,
        RecipeConnectionWorkbenchView workbench,
        Button loadLockSetupButton,
        string? savePath,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<TextBox, bool>, TextBox?> findTextBox,
        Func<DependencyObject, Func<ComboBox, bool>, ComboBox?> findComboBox,
        Action activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld,
        Action<FrameworkElement> setPopupContent)
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

        SmokeLoadLockSetupReport CreateReport() => new()
        {
            Checks = checks,
            Failures = failures
        };

        try
        {
        var normalizedState = loadLockState.ToLowerInvariant();
        if (normalizedState is not (
            "load-lock-focus"
            or "load-lock-hover"
            or "load-lock-pressed"
            or "load-lock-preview"
            or "load-lock-input-focus"
            or "load-lock-input-disabled"
            or "load-lock-invalid"
            or "load-lock-stale"
            or "load-lock-combo-open"
            or "load-lock-apply-focus"
            or "load-lock-apply-pressed"
            or "load-lock-applied"
            or "load-lock-reopen"))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{loadLockState}'. " +
                "Expected a supported load-lock setup smoke state.");
        }

        if (normalizedState == "load-lock-focus")
        {
            window.Activate();
            loadLockSetupButton.Focus();
            Keyboard.Focus(loadLockSetupButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "load-lock-setup-button-focus",
                loadLockSetupButton.IsKeyboardFocused,
                "Load-lock setup button did not receive focus.");
            return CreateReport();
        }

        if (normalizedState is "load-lock-hover" or "load-lock-pressed")
        {
            activateWindow();
            loadLockSetupButton.BringIntoView();
            loadLockSetupButton.UpdateLayout();
            loadLockSetupButton.Focus();
            movePointerToCenter(loadLockSetupButton);
            await Task.Delay(150);
            Check(
                "load-lock-setup-button-hover",
                loadLockSetupButton.IsMouseOver,
                "Load-lock setup button did not enter hover state.");
            if (normalizedState == "load-lock-pressed")
            {
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "load-lock-setup-button-pressed",
                    loadLockSetupButton.IsPressed,
                    "Load-lock setup button did not enter pointer-down state.");
            }

            return CreateReport();
        }

        if (initialProject is null)
        {
            throw new InvalidOperationException("A project is required for load-lock setup smoke.");
        }

        var loadLockStore = new ProjectDocumentStore();
        if (normalizedState == "load-lock-stale")
        {
            initialProject.Devices.Single(device => device.Kind == DeviceKind.LoadLock)
                .LoadLock!.OuterDoorComponentId = "missing.outer-door";
            vm.RecipeConnections.Load(initialProject);
        }

        var loadLockBeforePreview = loadLockStore.Serialize(initialProject);
        var loadLockRuntimeBefore = vm.SceneSnapshots.Latest;
        vm.RecipeConnections.LoadLocks.PreviewCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var loadLockPanel = findBorder(
            workbench,
            candidate => string.Equals(candidate.Name, "LoadLockSetupPreview", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Load-lock setup panel was not available.");
        var loadLockApplyButton = findButton(
            workbench,
            candidate => string.Equals(candidate.Name, "ApplyLoadLockSetupButton", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Load-lock Apply button was not available.");
        var loadLockPumpTextBox = findTextBox(
            workbench,
            candidate => string.Equals(candidate.Name, "LoadLockPumpDownDurationTextBox", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Load-lock pump-down input was not available.");
        var loadLockOuterDoor = findComboBox(
            workbench,
            candidate => string.Equals(candidate.Name, "LoadLockOuterDoorComboBox", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Load-lock outer-door selector was not available.");
        Check(
            "preview-project-unchanged",
            loadLockBeforePreview == loadLockStore.Serialize(initialProject),
            "Load-lock preview changed the project.");
        Check(
            "preview-runtime-unchanged",
            loadLockPanel.IsVisible
            && loadLockRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
            && loadLockRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
            && !vm.IsRunning
            && vm.IsDesignMode,
            "Load-lock preview changed project or runtime state.");

        if (normalizedState == "load-lock-stale")
        {
            Check(
                "stale-reference-blocked",
                vm.RecipeConnections.LoadLocks.OuterDoorComponentId == "missing.outer-door"
                && vm.RecipeConnections.LoadLocks.DoorOptions.Any(option =>
                    option.Id == "missing.outer-door"
                    && option.DisplayName.Contains("missing.outer-door", StringComparison.Ordinal))
                && vm.RecipeConnections.LoadLocks.HasValidationError
                && !loadLockApplyButton.IsEnabled,
                "A stale load-lock reference was not kept visible and blocked from Apply.");
            return CreateReport();
        }

        var expectedPumpDown = normalizedState == "load-lock-reopen" ? "255" : "250";
        var expectedVent = normalizedState == "load-lock-reopen" ? "260" : "250";
        Check(
            "saved-values-restored",
            vm.RecipeConnections.LoadLocks.OuterDoorComponentId == "outer-door"
            && vm.RecipeConnections.LoadLocks.InnerDoorComponentId == "process-cylinder"
            && vm.RecipeConnections.LoadLocks.EvacuateCommandChannelId == "do.load-lock.evacuate"
            && vm.RecipeConnections.LoadLocks.VentCommandChannelId == "do.load-lock.vent"
            && vm.RecipeConnections.LoadLocks.VacuumReadySensorChannelId == "di.load-lock.vacuum-ready"
            && vm.RecipeConnections.LoadLocks.AtmosphereReadySensorChannelId == "di.load-lock.atmosphere-ready"
            && vm.RecipeConnections.LoadLocks.PumpDownDurationText == expectedPumpDown
            && vm.RecipeConnections.LoadLocks.VentDurationText == expectedVent
            && loadLockApplyButton.IsEnabled,
            "Saved load-lock setup was not restored as an editable valid draft.");

        if (normalizedState is "load-lock-preview" or "load-lock-reopen")
        {
            return CreateReport();
        }

        if (normalizedState == "load-lock-input-focus")
        {
            window.Activate();
            loadLockPumpTextBox.Focus();
            Keyboard.Focus(loadLockPumpTextBox);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "load-lock-timing-input-focus",
                loadLockPumpTextBox.IsKeyboardFocused && loadLockPumpTextBox.Text == "250",
                "Load-lock timing input did not render its value with keyboard focus.");
            return CreateReport();
        }

        if (normalizedState == "load-lock-input-disabled")
        {
            vm.RecipeConnections.IsEditable = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "load-lock-inputs-disabled",
                !loadLockPumpTextBox.IsEnabled
                && !loadLockOuterDoor.IsEnabled
                && !loadLockApplyButton.IsEnabled,
                "Load-lock setup inputs did not enter their disabled state.");
            return CreateReport();
        }

        if (normalizedState == "load-lock-invalid")
        {
            vm.RecipeConnections.LoadLocks.PumpDownDurationText = "251";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "load-lock-invalid-timing-blocks-apply",
                vm.RecipeConnections.LoadLocks.HasValidationError
                && !loadLockApplyButton.IsEnabled
                && loadLockBeforePreview == loadLockStore.Serialize(initialProject),
                "Invalid load-lock timing did not block Apply without changing the project.");
            return CreateReport();
        }

        if (normalizedState == "load-lock-combo-open")
        {
            window.Activate();
            loadLockOuterDoor.Focus();
            Keyboard.Focus(loadLockOuterDoor);
            loadLockOuterDoor.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "load-lock-door-popup-open",
                loadLockOuterDoor.IsDropDownOpen && loadLockOuterDoor.Items.Count >= 2,
                "Load-lock door selector popup did not open with candidates.");
            var loadLockWindowRoot = PresentationSource.FromVisual(window)?.RootVisual;
            var loadLockPopupContent = PresentationSource.CurrentSources
                .Cast<PresentationSource>()
                .Select(source => source.RootVisual)
                .OfType<FrameworkElement>()
                .FirstOrDefault(root =>
                    !ReferenceEquals(root, loadLockWindowRoot)
                    && root.IsVisible
                    && root.ActualWidth > 0
                    && root.ActualHeight > 0)
                ?? throw new InvalidOperationException("Load-lock door selector popup content was unavailable.");
            setPopupContent(loadLockPopupContent);
            return CreateReport();
        }

        if (normalizedState == "load-lock-applied")
        {
            vm.RecipeConnections.LoadLocks.PumpDownDurationText = "255";
            vm.RecipeConnections.LoadLocks.VentDurationText = "260";
            vm.RecipeConnections.LoadLocks.OuterDoorComponentId = "process-cylinder";
            var resetAvailable = vm.RecipeConnections.LoadLocks.ResetCommand.CanExecute(null);
            if (resetAvailable)
            {
                vm.RecipeConnections.LoadLocks.ResetCommand.Execute(null);
            }
            Check(
                "reset-without-project-change",
                resetAvailable
                && vm.RecipeConnections.LoadLocks.OuterDoorComponentId == "outer-door"
                && vm.RecipeConnections.LoadLocks.PumpDownDurationText == "250"
                && loadLockBeforePreview == loadLockStore.Serialize(initialProject),
                "Load-lock saved-value reset was unavailable or changed the project before Apply.");
            vm.RecipeConnections.LoadLocks.PumpDownDurationText = "255";
            vm.RecipeConnections.LoadLocks.VentDurationText = "260";
            vm.RecipeConnections.LoadLocks.ApplyCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var appliedLoadLock = initialProject.Devices.Single(device => device.Kind == DeviceKind.LoadLock).LoadLock;
            Check(
                "custom-timings-applied",
                appliedLoadLock is { PumpDownDurationMilliseconds: 255, VentDurationMilliseconds: 260 }
                && !vm.RecipeConnections.LoadLocks.IsVisible
                && !vm.IsRunning
                && vm.IsDesignMode,
                "Applying load-lock settings did not update only the project while staying stopped in Design mode.");
            Check(
                "apply-runtime-unchanged",
                loadLockRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && loadLockRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime,
                "Applying load-lock settings changed the runtime state.");
            vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
            Check(
                "readiness-compilation-passed",
                vm.RecipeConnections.ReadinessPassed == true,
                "Applied load-lock setup did not compile for simulation.");
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                var loadLockSavePath = Path.GetFullPath(savePath);
                Directory.CreateDirectory(Path.GetDirectoryName(loadLockSavePath)!);
                await vm.SaveProjectAsync(loadLockSavePath);
                Check(
                    "save-reopen-restored",
                    await vm.OpenProjectAsync(loadLockSavePath),
                    "Load-lock setup project did not reopen.");
                vm.RecipeConnections.LoadLocks.PreviewCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "reopen-stays-stopped-in-design",
                    vm.RecipeConnections.LoadLocks.PumpDownDurationText == "255"
                    && vm.RecipeConnections.LoadLocks.VentDurationText == "260"
                    && !vm.IsRunning
                    && vm.IsDesignMode,
                    "Saved load-lock setup was not restored safely after reopen.");
            }

            return CreateReport();
        }

        activateWindow();
        loadLockApplyButton.BringIntoView();
        loadLockApplyButton.UpdateLayout();
        loadLockApplyButton.Focus();
        Keyboard.Focus(loadLockApplyButton);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            "load-lock-apply-button-focus",
            loadLockApplyButton.IsKeyboardFocused,
            "Load-lock Apply button did not receive focus.");
        if (normalizedState == "load-lock-apply-pressed")
        {
            movePointerToCenter(loadLockApplyButton);
            Mouse.Capture(loadLockApplyButton, CaptureMode.SubTree);
            Mouse.Synchronize();
            await Task.Delay(150);
            Check(
                "load-lock-apply-button-hover",
                loadLockApplyButton.IsMouseOver,
                "Load-lock Apply button did not enter hover state.");
            mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            markSmokePointerHeld();
            loadLockApplyButton.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent
            });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "load-lock-apply-button-pressed",
                loadLockApplyButton.IsPressed,
                "Load-lock Apply button did not enter pointer-down state.");
        }

        return CreateReport();
        }
        catch (InvalidOperationException) when (failures.Count > 0)
        {
            return CreateReport();
        }
    }
}
