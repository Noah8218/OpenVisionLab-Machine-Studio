using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeConveyorCommissioningReport
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

internal static class SmokeConveyorCommissioningVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeConveyorCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<Task> scrollIntoView)
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

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.HasSelectedConveyor,
            "A conveyor was not selected for commissioning.");
        await scrollIntoView();
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
        await scrollIntoView();
        return new SmokeConveyorCommissioningReport
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
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
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
            interaction.MovePointerToCenter(button);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
                 && !state.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-conveyor-commissioning-state '{state}'. Expected ready, manual, " +
                "forward, reverse, stopped, focus-forward, hover-reverse, or pressed-forward.");
        }

        await ScrollIntoViewAsync(window);
        await Task.Delay(150);
    }

    public static async Task ScrollIntoViewAsync(ShellWindow window)
    {
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.SelectedEquipmentRuntimeCard.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
