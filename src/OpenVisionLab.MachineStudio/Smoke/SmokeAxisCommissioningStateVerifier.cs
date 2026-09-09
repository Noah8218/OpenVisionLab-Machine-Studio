using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeAxisCommissioningStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
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
