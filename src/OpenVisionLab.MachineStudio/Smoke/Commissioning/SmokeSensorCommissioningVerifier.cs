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

internal sealed class SmokeSensorCommissioningReport
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

internal static class SmokeSensorCommissioningVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeSensorCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<Task> scrollIntoView)
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

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.HasSelectedDigitalSensor,
            "A digital sensor was not selected for commissioning.");
        await scrollIntoView();
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
        await scrollIntoView();
        return new SmokeSensorCommissioningReport
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
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
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
            interaction.MovePointerToCenter(inspector.StartManualEquipmentControlButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
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
                $"Unsupported --smoke-sensor-commissioning-state '{state}'. Expected ready, manual, " +
                "forced-on, forced-off, cleared, focus-start, hover-start, pressed-start, " +
                "focus-on, hover-off, or pressed-on.");
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
