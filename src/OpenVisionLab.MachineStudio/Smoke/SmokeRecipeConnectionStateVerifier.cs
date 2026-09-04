using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeRecipeConnectionStateVerifier
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "normal"
        or "focus"
        or "hover"
        or "pressed"
        or "disabled"
        or "readiness";

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel vm,
        RecipeConnectionWorkbenchView workbench,
        Button addStageButton,
        Button addRotaryStageButton,
        Button readinessButton,
        Button dryRunButton,
        Button stationSkeletonButton,
        Button processBlockButton,
        Button loadLockSetupButton,
        Button checkpointTemplateButton,
        string connectionWorkbenchState,
        SmokeUiInteraction interaction)
    {
        switch (connectionWorkbenchState.ToLowerInvariant())
        {
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
                interaction.FindButton(workbench, candidate =>
                    string.Equals(candidate.Name, "OpenConnectionSequenceStepButton", StringComparison.Ordinal)
                    && candidate.IsVisible) is not null,
                "No visible linked Sequence step action was available.");
            break;
        case "focus":
            interaction.ActivateWindow();
            addRotaryStageButton.Focus();
            Keyboard.Focus(addRotaryStageButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(addRotaryStageButton.IsKeyboardFocused, "Rotary axis + stage button did not receive focus.");
            break;
        case "hover":
        case "pressed":
            window.Topmost = true;
            interaction.ActivateWindow();
            addRotaryStageButton.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            addRotaryStageButton.UpdateLayout();
            addRotaryStageButton.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var rotaryButtonCenter = addRotaryStageButton.PointToScreen(new Point(
                addRotaryStageButton.ActualWidth / 2,
                addRotaryStageButton.ActualHeight / 2));
            interaction.SetCursorPosition(
                (int)Math.Round(rotaryButtonCenter.X - addRotaryStageButton.ActualWidth),
                (int)Math.Round(rotaryButtonCenter.Y));
            Mouse.Synchronize();
            await Task.Delay(50);
            interaction.MovePointerToCenter(addRotaryStageButton);
            interaction.MouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
            await Task.Delay(200);
            var cursorPosition = interaction.GetCursorPosition();
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
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
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
        default:
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{connectionWorkbenchState}'. " +
                "Expected a supported connection-workbench smoke state, including dry-run, dry-run-playback, or dry-run-wafer-handler-fault-playback.");
        }
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
