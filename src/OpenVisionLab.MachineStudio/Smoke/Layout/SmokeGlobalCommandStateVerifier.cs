using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeGlobalCommandStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction uiInteraction,
        SmokeWindowCapture? windowCapture = null,
        string? screenshotPath = null)
    {
        if (state.Equals("abort", StringComparison.OrdinalIgnoreCase))
        {
            var abortName = OpenVisionLanguageService.T("Shell.AbortSequence");
            var abortButton = FindVisualDescendant<Button>(
                window,
                candidate => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(candidate),
                    abortName,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Sequence abort command was not available.");

            var runningState = OpenVisionLanguageService.T("Equipment.State.Running");
            if (!viewModel.AbortSequenceCommand.CanExecute(null)
                || !string.Equals(viewModel.CurrentSequenceStateText, runningState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Sequence abort command was not enabled while the sequence lifecycle was running.");
            }

            uiInteraction.ActivateWindow();
            abortButton.Focus();
            abortButton.BringIntoView();
            abortButton.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            uiInteraction.MovePointerToCenter(abortButton);
            await Task.Delay(100);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!abortButton.IsVisible || !abortButton.IsEnabled || !abortButton.IsMouseOver)
            {
                var cursorPosition = uiInteraction.GetCursorPosition();
                var buttonPoint = abortButton.PointFromScreen(
                    new Point(cursorPosition.X, cursorPosition.Y));
                throw new InvalidOperationException(
                    "Sequence abort command did not expose its enabled hover state. " +
                    $"Visible={abortButton.IsVisible}, Enabled={abortButton.IsEnabled}, " +
                    $"MouseOver={abortButton.IsMouseOver}, Size={abortButton.ActualWidth:F1}x" +
                    $"{abortButton.ActualHeight:F1}, Cursor=({cursorPosition.X},{cursorPosition.Y}), " +
                    $"ButtonPoint=({buttonPoint.X:F1},{buttonPoint.Y:F1}), " +
                    $"DirectOver={Mouse.DirectlyOver?.GetType().Name ?? "null"}.");
            }

            uiInteraction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            uiInteraction.MarkSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!abortButton.IsPressed)
            {
                uiInteraction.ReleaseSmokePointer();
                throw new InvalidOperationException(
                    "Sequence abort command did not enter the pointer-down state.");
            }

            uiInteraction.ReleaseSmokePointer();
            var abortedState = OpenVisionLanguageService.T("Equipment.State.Aborted");
            for (var attempt = 0;
                 attempt < 40 && (viewModel.IsRunning
                     || !string.Equals(viewModel.CurrentSequenceStateText, abortedState, StringComparison.Ordinal));
                 attempt++)
            {
                await Task.Delay(50);
            }

            if (viewModel.IsRunning
                || !string.Equals(viewModel.CurrentSequenceStateText, abortedState, StringComparison.Ordinal)
                || viewModel.AbortSequenceCommand.CanExecute(null)
                || abortButton.IsEnabled)
            {
                throw new InvalidOperationException(
                    "Sequence abort did not publish Aborted state or disable restart-required action.");
            }

            Console.WriteLine("Sequence abort command smoke passed: enabled, applied, Aborted, disabled.");
            return;
        }

        if (state.Equals("retry", StringComparison.OrdinalIgnoreCase))
        {
            var retryName = OpenVisionLanguageService.T("Shell.RetrySequence");
            var retryButton = FindVisualDescendant<Button>(
                window,
                candidate => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(candidate),
                    retryName,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Sequence retry command was not available.");

            var faultedState = OpenVisionLanguageService.T("Equipment.State.Faulted");
            if (!viewModel.CanRetrySequence
                || !string.Equals(viewModel.CurrentSequenceStateText, faultedState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Sequence retry command was not enabled while the sequence lifecycle was faulted.");
            }

            uiInteraction.ActivateWindow();
            retryButton.Focus();
            retryButton.BringIntoView();
            retryButton.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            uiInteraction.MovePointerToCenter(retryButton);
            await Task.Delay(100);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!retryButton.IsVisible || !retryButton.IsEnabled || !retryButton.IsMouseOver)
            {
                var cursorPosition = uiInteraction.GetCursorPosition();
                var buttonPoint = retryButton.PointFromScreen(
                    new Point(cursorPosition.X, cursorPosition.Y));
                throw new InvalidOperationException(
                    "Sequence retry command did not expose its enabled hover state. " +
                    $"Visible={retryButton.IsVisible}, Enabled={retryButton.IsEnabled}, " +
                    $"MouseOver={retryButton.IsMouseOver}, Size={retryButton.ActualWidth:F1}x" +
                    $"{retryButton.ActualHeight:F1}, Cursor=({cursorPosition.X},{cursorPosition.Y}), " +
                    $"ButtonPoint=({buttonPoint.X:F1},{buttonPoint.Y:F1}), " +
                    $"DirectOver={Mouse.DirectlyOver?.GetType().Name ?? "null"}.");
            }

            if (!retryButton.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException("Sequence retry command did not expose keyboard focus.");
            }

            uiInteraction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            uiInteraction.MarkSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!retryButton.IsPressed)
            {
                uiInteraction.ReleaseSmokePointer();
                throw new InvalidOperationException(
                    "Sequence retry command did not enter the pointer-down state.");
            }

            uiInteraction.ReleaseSmokePointer();
            var runningState = OpenVisionLanguageService.T("Equipment.State.Running");
            for (var attempt = 0;
                 attempt < 40
                 && (!string.Equals(viewModel.CurrentSequenceStateText, runningState, StringComparison.Ordinal)
                     || viewModel.CanRetrySequence
                     || retryButton.IsEnabled);
                 attempt++)
            {
                await Task.Delay(50);
            }

            if (!string.Equals(viewModel.CurrentSequenceStateText, runningState, StringComparison.Ordinal)
                || viewModel.CanRetrySequence
                || retryButton.IsEnabled
                || viewModel.IsRunning
                || !string.Equals(
                    viewModel.StatusMessage,
                    OpenVisionLanguageService.T("Shell.SequenceRetriedStatus"),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Sequence retry did not publish Running-from-entry, paused automatic state, and disabled retry.");
            }

            var outsidePoint = window.PointToScreen(new Point(8, 8));
            uiInteraction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
            Mouse.Synchronize();
            await Task.Delay(100);
            if (retryButton.IsMouseOver)
            {
                throw new InvalidOperationException(
                    "Sequence retry command did not recover after the pointer left the button.");
            }

            var simulationMenuName = OpenVisionLanguageService.T("Shell.Simulation");
            var simulationMenu = FindVisualDescendant<MenuItem>(
                window,
                candidate => string.Equals(
                    candidate.Header?.ToString(),
                    simulationMenuName,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Simulation menu was not available.");
            simulationMenu.IsSubmenuOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            var retryMenuItem = simulationMenu.Items
                .OfType<MenuItem>()
                .SingleOrDefault(item => ReferenceEquals(item.Command, viewModel.RetrySequenceCommand))
                ?? throw new InvalidOperationException("Simulation menu Retry item was not available.");
            if (retryMenuItem.IsEnabled)
            {
                throw new InvalidOperationException(
                    "Simulation menu Retry item remained enabled after a successful retry.");
            }
            simulationMenu.IsSubmenuOpen = false;

            Console.WriteLine(
                "Sequence retry command smoke passed: enabled, focused, hover, pressed, applied, " +
                "mouse-leave recovery, disabled, and menu state.");
            return;
        }

        var topCommandState = state.ToLowerInvariant();
        if (topCommandState is "undo-pressed" or "redo-pressed" or "save-pressed")
        {
            await ApplyTopCommandPressedAsync(
                window,
                viewModel,
                topCommandState,
                uiInteraction,
                windowCapture ?? throw new InvalidOperationException("Top-command state requires the window capture owner."),
                screenshotPath);
            return;
        }

        var expectedName = OpenVisionLanguageService.T("Shell.SimulationOn");
        var button = FindVisualDescendant<Button>(
            window,
            candidate => string.Equals(
                System.Windows.Automation.AutomationProperties.GetName(candidate),
                expectedName,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Simulation ON command was not available.");

        switch (state.ToLowerInvariant())
        {
            case "normal":
                if (!button.IsEnabled)
                {
                    throw new InvalidOperationException("Simulation ON command was unexpectedly disabled.");
                }
                break;
            case "focus":
                uiInteraction.ActivateWindow();
                button.Focus();
                break;
            case "hover":
                uiInteraction.MovePointerToCenter(button);
                break;
            case "pressed":
                uiInteraction.ActivateWindow();
                button.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                uiInteraction.MovePointerToCenter(button);
                await Task.Delay(100);
                uiInteraction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                uiInteraction.MarkSmokePointerHeld();
                break;
            case "disabled":
                if (button.IsEnabled)
                {
                    throw new InvalidOperationException(
                        "Simulation ON command remained enabled; use --smoke-start-simulation for disabled evidence.");
                }
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-command-state '{state}'. " +
                    "Expected normal, focus, hover, pressed, disabled, or abort.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase) && !button.IsPressed)
        {
            throw new InvalidOperationException("Simulation ON command did not enter the pointer-down state.");
        }
        Console.WriteLine($"Global command visual state applied: {state}");
    }

    private static async Task ApplyTopCommandPressedAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction uiInteraction,
        SmokeWindowCapture windowCapture,
        string? screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            throw new ArgumentException("--smoke-command-state-screenshot is required for top-command pressed states.");
        }

        var buttonName = state switch
        {
            "undo-pressed" => "TopUndoButton",
            "redo-pressed" => "TopRedoButton",
            _ => "TopSaveButton"
        };
        if (state == "undo-pressed" && !viewModel.UndoLayoutEditCommand.CanExecute(null))
        {
            if (!viewModel.TryAddLayoutComponent(LayoutComponentKind.DigitalSensor))
                throw new InvalidOperationException("A digital sensor fixture could not be added for Undo state.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        else if (state == "redo-pressed" && !viewModel.RedoLayoutEditCommand.CanExecute(null))
        {
            if (!viewModel.TryAddLayoutComponent(LayoutComponentKind.DigitalSensor)
                || !viewModel.UndoLayoutEditCommand.CanExecute(null))
                throw new InvalidOperationException("A layout history fixture could not be prepared for Redo state.");
            viewModel.UndoLayoutEditCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        var button = FindVisualDescendant<Button>(window, candidate => candidate.Name == buttonName)
            ?? throw new InvalidOperationException($"Top command '{buttonName}' was not available.");
        if (!button.IsVisible || !button.IsEnabled || button.Command?.CanExecute(button.CommandParameter) != true)
            throw new InvalidOperationException($"Top command '{buttonName}' was not enabled for its pressed-state check.");

        var saveHash = state == "save-pressed"
            ? SHA256.HashData(File.ReadAllBytes(viewModel.CurrentProjectPath
                ?? throw new InvalidOperationException("Save pressed-state verification requires a saved smoke project.")))
            : null;
        try
        {
            await SmokeButtonPointerState.FocusAsync(
                window,
                button,
                uiInteraction,
                $"Top command '{buttonName}' did not receive keyboard focus.");
            await SmokeButtonPointerState.HoverThenPressAsync(
                window,
                button,
                uiInteraction,
                () => $"Top command '{buttonName}' did not enter hover state.",
                $"Top command '{buttonName}' did not enter the pointer-down state.");

            windowCapture.Capture(window, screenshotPath);
            var outsidePoint = window.PointToScreen(new Point(8, 8));
            uiInteraction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
            Mouse.Capture(null);
            Mouse.Synchronize();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (button.IsPressed)
                throw new InvalidOperationException($"Top command '{buttonName}' stayed pressed after the pointer left it.");
        }
        finally
        {
            var outsidePoint = window.PointToScreen(new Point(8, 8));
            uiInteraction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
            Mouse.Synchronize();
            uiInteraction.ReleaseSmokePointer();
        }

        await Task.Delay(250);
        if (button.Command?.CanExecute(button.CommandParameter) != true)
            throw new InvalidOperationException($"Top command '{buttonName}' ran after its pressed-state capture.");
        if (saveHash is not null)
        {
            var currentPath = viewModel.CurrentProjectPath!;
            if (!SHA256.HashData(File.ReadAllBytes(currentPath)).SequenceEqual(saveHash))
                throw new InvalidOperationException("Save executed when the smoke pointer was released outside its button.");
        }

        Console.WriteLine($"Top command pointer-down captured and released without activation: {buttonName}.");
    }
}
