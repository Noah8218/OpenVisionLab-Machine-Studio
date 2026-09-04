using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeAxisCommissioningReport
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

internal static class SmokeAxisCommissioningVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeAxisCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<Task> scrollIntoView)
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

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Axes.Count > 0,
            "Axis snapshot was unavailable.");
        await scrollIntoView();
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
        await scrollIntoView();
        return new SmokeAxisCommissioningReport
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

        await ScrollIntoViewAsync(window);
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
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
            interaction.MovePointerToCenter(inspector.MoveAxisVelocityButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state.Equals("hover-relative", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-relative", StringComparison.OrdinalIgnoreCase))
        {
            interaction.MovePointerToCenter(inspector.MoveAxisRelativeButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state.Equals("hover-move", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("pressed-move", StringComparison.OrdinalIgnoreCase))
        {
            interaction.MovePointerToCenter(inspector.MoveAxisAbsoluteButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
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
            interaction.MovePointerToCenter(inspector.JogPositiveButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
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
