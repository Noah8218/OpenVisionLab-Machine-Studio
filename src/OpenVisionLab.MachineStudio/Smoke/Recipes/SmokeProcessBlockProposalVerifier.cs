using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeProcessBlockProposalVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "process-block-focus"
        or "process-block-hover"
        or "process-block-pressed"
        or "process-block-preview"
        or "process-block-check-focus"
        or "process-block-check-pressed"
        or "process-block-disabled"
        or "process-block-empty";

    public static async Task VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string proposalState,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Button processBlockButton,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<CheckBox, bool>, CheckBox?> findCheckBox,
        Action activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld)
    {
        var normalizedState = proposalState.ToLowerInvariant();
        if (!IsSupportedState(normalizedState))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{proposalState}'. " +
                "Expected process-block-focus, process-block-hover, process-block-pressed, " +
                "process-block-preview, process-block-check-focus, process-block-check-pressed, " +
                "process-block-disabled, or process-block-empty.");
        }

        if (normalizedState == "process-block-focus")
        {
            window.Activate();
            processBlockButton.Focus();
            Keyboard.Focus(processBlockButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                processBlockButton.IsKeyboardFocused,
                "Process block composer button did not receive focus.");
            return;
        }

        if (normalizedState is "process-block-hover" or "process-block-pressed")
        {
            activateWindow();
            processBlockButton.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            movePointerToCenter(processBlockButton);
            await Task.Delay(100);
            Check(
                processBlockButton.IsMouseOver,
                "Process block composer button did not enter hover state.");
            if (normalizedState == "process-block-pressed")
            {
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    processBlockButton.IsPressed,
                    "Process block composer button did not enter pointer-down state.");
            }

            return;
        }

        var context = await SmokeProcessBlockPreparation.PrepareAsync(
            window,
            vm,
            initialProject,
            workbench,
            findBorder,
            findButton,
            findCheckBox);

        if (normalizedState == "process-block-preview")
        {
            return;
        }

        if (normalizedState is "process-block-check-focus" or "process-block-check-pressed")
        {
            window.Activate();
            context.LoadBlockCheckBox.Focus();
            Keyboard.Focus(context.LoadBlockCheckBox);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                context.LoadBlockCheckBox.IsKeyboardFocused,
                "Load block checkbox did not receive focus.");
            if (normalizedState == "process-block-check-pressed")
            {
                movePointerToCenter(context.LoadBlockCheckBox);
                Mouse.Capture(context.LoadBlockCheckBox, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                Check(
                    context.LoadBlockCheckBox.IsMouseOver,
                    "Load block checkbox did not enter hover state.");
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                context.LoadBlockCheckBox.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.MouseDownEvent
                });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    context.LoadBlockCheckBox.IsPressed,
                    "Load block checkbox did not enter pointer-down state.");
            }

            return;
        }

        if (normalizedState == "process-block-disabled")
        {
            vm.RecipeConnections.IsEditable = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                !context.ApplyButton.IsEnabled && !context.LoadBlockCheckBox.IsEnabled,
                "Process block controls did not enter their disabled state.");
            return;
        }

        vm.RecipeConnections.ProcessBlocks.IsLoadBlockSelected = false;
        vm.RecipeConnections.ProcessBlocks.IsAlignBlockSelected = false;
        vm.RecipeConnections.ProcessBlocks.IsProcessBlockSelected = false;
        vm.RecipeConnections.ProcessBlocks.IsInspectBlockSelected = false;
        vm.RecipeConnections.ProcessBlocks.IsUnloadBlockSelected = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockCount == 0
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Count == 0
            && !context.ApplyButton.IsEnabled
            && context.ProjectBefore == context.Store.SerializeForEvidence(context.Project),
            "An empty process plan did not block Apply without changing the project.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
