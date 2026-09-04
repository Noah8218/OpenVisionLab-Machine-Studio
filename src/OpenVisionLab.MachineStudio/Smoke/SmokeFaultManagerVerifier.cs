using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeFaultManagerReport
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

internal static class SmokeFaultManagerVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeFaultManagerReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<bool, Task> scrollIntoView)
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
        var inspector = findInspector(window)
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
            string.Equals(target.Id, SmokeRoundTripScenario.RoundTripCylinderId, StringComparison.Ordinal))
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

        await scrollIntoView(false);
        return new SmokeFaultManagerReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    public static async Task ApplyStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
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
            await ScrollIntoViewAsync(window, activeSection: false);
            var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
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
            await ScrollIntoViewAsync(window, activeSection: true);
            var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
                ?? throw new InvalidOperationException("Run inspector was unavailable.");
            var button = inspector.ClearSelectedFaultButton;
            if (state.StartsWith("focus", StringComparison.OrdinalIgnoreCase))
            {
                button.Focus();
            }
            else
            {
                interaction.MovePointerToCenter(button);
                if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
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

        await ScrollIntoViewAsync(
            window,
            activeSection: !state.Equals("active-top", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("popup-kind", StringComparison.OrdinalIgnoreCase));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    public static async Task ScrollIntoViewAsync(
        ShellWindow window,
        bool activeSection)
    {
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
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
}
