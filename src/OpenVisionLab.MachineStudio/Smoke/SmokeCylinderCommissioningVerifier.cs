using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeCylinderCommissioningReport
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

internal static class SmokeCylinderCommissioningVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeCylinderCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<Task> scrollIntoView)
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

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.HasSelectedPneumaticCylinder,
            "A pneumatic cylinder was not selected for commissioning.");
        await scrollIntoView();
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
        await scrollIntoView();
        return new SmokeCylinderCommissioningReport
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
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
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
            interaction.MovePointerToCenter(inspector.ExtendCylinderButton);
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
                $"Unsupported --smoke-cylinder-commissioning-state '{state}'. Expected ready, manual, " +
                "extended, faulted, focus-extend, hover-extend, or pressed-extend.");
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
