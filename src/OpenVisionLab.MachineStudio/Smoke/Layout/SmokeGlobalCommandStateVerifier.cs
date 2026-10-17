using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
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
            var commandBar = FindVisualDescendant<GlobalCommandBarView>(window, candidate => candidate.IsVisible)
                ?? throw new InvalidOperationException("Global command bar was not available.");
            var runOptionsPopup = (Popup)commandBar.FindName("RunOptionsPopup");
            runOptionsPopup.SetCurrentValue(Popup.IsOpenProperty, true);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var retryButton = FindVisualDescendant<Button>(runOptionsPopup.Child, candidate => ReferenceEquals(candidate.Command, viewModel.RetrySequenceCommand))
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

            runOptionsPopup.SetCurrentValue(Popup.IsOpenProperty, false);
            var simulationMenuName = OpenVisionLanguageService.T("Shell.Run");
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

        if (state.StartsWith("outline-", StringComparison.OrdinalIgnoreCase) || state.StartsWith("scene-", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyEquipmentPressedAsync(window, viewModel, state.ToLowerInvariant(), uiInteraction,
                windowCapture ?? throw new InvalidOperationException("Outline state requires the window capture owner."), screenshotPath);
            return;
        }

        var topCommandState = state.ToLowerInvariant();
        if (topCommandState is "undo-pressed" or "redo-pressed" or "save-pressed" or "fault-chip-pressed" or "target-chip-pressed" or "run-options-pressed" or "workspace-tab-pressed")
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
                var pointerOwnership = uiInteraction.CheckPointerOwnership(window);
                if (!pointerOwnership.IsOwned || !button.IsMouseOver)
                {
                    throw new InvalidOperationException(
                        "Simulation ON pointer-down prerequisite failed. " + pointerOwnership.Diagnostic +
                        $" Enabled={button.IsEnabled}, Visible={button.IsVisible}, MouseOver={button.IsMouseOver}, " +
                        $"DirectOver={Mouse.DirectlyOver?.GetType().Name ?? "none"}.");
                }
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
            var pointerOwnership = uiInteraction.CheckPointerOwnership(window);
            var cursor = uiInteraction.GetCursorPosition();
            var buttonPoint = button.PointFromScreen(new Point(cursor.X, cursor.Y));
            throw new InvalidOperationException(
                "Simulation ON command did not enter the pointer-down state. " + pointerOwnership.Diagnostic +
                $" Enabled={button.IsEnabled}, Visible={button.IsVisible}, MouseOver={button.IsMouseOver}, " +
                $"MouseCaptured={button.IsMouseCaptured}, LeftButton={Mouse.LeftButton}, " +
                $"DirectOver={Mouse.DirectlyOver?.GetType().Name ?? "none"}, " +
                $"ButtonPoint=({buttonPoint.X:F1},{buttonPoint.Y:F1}), Running={viewModel.IsRunning}.");
        }
        if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase))
        {
            var ownership = uiInteraction.CheckPointerOwnership(window);
            if (!ownership.IsOwned || viewModel.IsRunning || Mouse.LeftButton != MouseButtonState.Pressed)
                throw new InvalidOperationException($"Run pointer-down lost ownership or executed prematurely. {ownership.Diagnostic}");
            Console.WriteLine($"Run native pointer-down: {ownership.Diagnostic} Pressed={button.IsPressed}, LeftButton={Mouse.LeftButton}, Running={viewModel.IsRunning}.");
            if (windowCapture is not null && !string.IsNullOrWhiteSpace(screenshotPath))
            {
                windowCapture.Capture(window, screenshotPath);
                var outside = window.PointToScreen(new Point(8, 8));
                uiInteraction.SetCursorPosition((int)Math.Round(outside.X), (int)Math.Round(outside.Y));
                await window.Dispatcher.InvokeAsync(Mouse.Synchronize, DispatcherPriority.Input);
                await Task.Delay(100);
                uiInteraction.ReleaseSmokePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (button.IsPressed || viewModel.IsRunning)
                    throw new InvalidOperationException("Run executed or stayed pressed after release outside its button.");
                Console.WriteLine("Run pointer-down released outside without activation.");
            }
        }
        Console.WriteLine($"Global command visual state applied: {state}");
    }

    private static async Task ApplyEquipmentPressedAsync(
        ShellWindow window, MainViewModel viewModel, string state, SmokeUiInteraction interaction,
        SmokeWindowCapture capture, string? screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath)) throw new ArgumentException("Outline pressed state requires a capture path.");
        if (state == "outline-clear-pressed") viewModel.Layout.EquipmentOutlineSearchText = viewModel.Layout.SelectedItem?.Id ?? "camera";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var name = state switch
        {
            "outline-unit-pressed" => "OutlineAddUnitButton",
            "outline-device-pressed" => "OutlineAddDeviceButton",
            "outline-scope-pressed" => "OutlineOverviewButton",
            "outline-clear-pressed" => "OutlineClearSearchButton",
            "outline-part-pressed" => string.Empty,
            "scene-fit-pressed" => "FitLayoutButton",
            "scene-zoom-in-pressed" => "SceneZoomInButton",
            "scene-zoom-out-pressed" => "SceneZoomOutButton",
            "scene-inspector-pressed" => "EquipmentInspectorToggleButton",
            "scene-plan-pressed" => "ScenePlanRadioButton",
            "scene-oblique-pressed" => "SceneObliqueRadioButton",
            "scene-slot-pressed" => "EquipmentSlotButton",
            "scene-slot-toggle-pressed" => "EquipmentSlotsToggleButton",
            "scene-manual-pressed" => "EquipmentSinglePartCheckButton",
            "scene-references-pressed" => "EquipmentSequenceReferencesButton",
            "scene-menu-pressed" => "EquipmentMoreActionsButton",
            _ => throw new ArgumentException($"Unsupported outline state '{state}'.")
        };
        var button = FindVisualDescendant<FrameworkElement>(window, candidate => name.Length > 0
            ? candidate.Name == name
            : candidate is ICommandSource command && ReferenceEquals(command.Command, viewModel.Layout.SelectEquipmentOutlinePartCommand)
                && ReferenceEquals(command.CommandParameter, viewModel.Layout.SelectedItem))
            ?? throw new InvalidOperationException($"Outline button '{state}' was not available.");
        if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Outline button was not enabled and visible.");
        var store = new ProjectDocumentStore();
        var project = (MachineProjectDocument)viewModel.ProjectTree.Roots.Single().Model!;
        var document = store.SerializeForEvidence(project);
        var selected = viewModel.Layout.SelectedItems.Select(item => item.Id).ToArray();
        var unit = viewModel.Layout.ActiveUnitId;
        var station = viewModel.Layout.ActiveStationId;
        var query = viewModel.Layout.EquipmentOutlineSearchText;
        var dirty = viewModel.HasUnsavedChanges;
        var oblique = viewModel.Layout.IsObliqueView;
        var inspectorOpen = viewModel.Navigation.IsInspectorOpen;
        try
        {
            if (button is Button commandButton)
                await SmokeButtonPointerState.FocusAsync(window, commandButton, interaction, "Scene/outline button did not receive keyboard focus.");
            else
            {
                interaction.ActivateWindow();
                button.BringIntoView();
                button.Focus();
                Keyboard.Focus(button);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!button.IsKeyboardFocused) throw new InvalidOperationException("Scene projection did not receive keyboard focus.");
            }
            if (state.StartsWith("scene-", StringComparison.Ordinal))
            {
                var neutral = window.PointToScreen(new Point(8, 8));
                interaction.SetCursorPosition((int)Math.Round(neutral.X), (int)Math.Round(neutral.Y));
                Mouse.Synchronize();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                capture.Capture(window, Path.Combine(Path.GetDirectoryName(screenshotPath)!, "focused.png"));
            }
            interaction.MovePointerToCenter(button);
            await Task.Delay(100);
            var ownership = interaction.CheckPointerOwnership(window);
            if (!ownership.IsOwned || !button.IsMouseOver) throw new InvalidOperationException("Outline native hover failed: " + ownership.Diagnostic);
            if (state.StartsWith("scene-", StringComparison.Ordinal))
                capture.Capture(window, Path.Combine(Path.GetDirectoryName(screenshotPath)!, "hover.png"));
            interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            interaction.MarkSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            var pressed = button is ButtonBase { IsPressed: true } || button is MenuItem { IsSubmenuOpen: true };
            if (!pressed || Mouse.LeftButton != MouseButtonState.Pressed) throw new InvalidOperationException("Scene control did not enter actual pointer-down.");
            capture.Capture(window, screenshotPath);
            Console.WriteLine($"Scene/outline native focus/hover/pressed: {state}; {ownership.Diagnostic}; Pressed={pressed}, LeftButton={Mouse.LeftButton}.");
        }
        finally
        {
            var outside = window.PointToScreen(new Point(8, 8));
            interaction.SetCursorPosition((int)Math.Round(outside.X), (int)Math.Round(outside.Y));
            Mouse.Synchronize();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            await Task.Delay(100);
            interaction.ReleaseSmokePointer();
            if (button is MenuItem menu) menu.IsSubmenuOpen = false;
        }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (button is ButtonBase { IsPressed: true } || button is MenuItem { IsSubmenuOpen: true } || button.IsMouseOver || viewModel.IsRunning || viewModel.IsStationUnitEditorOpen
            || viewModel.IsEquipmentComponentLibraryDialogOpen || viewModel.HasUnsavedChanges != dirty
            || viewModel.Layout.ActiveUnitId != unit || viewModel.Layout.ActiveStationId != station
            || viewModel.Layout.IsObliqueView != oblique || viewModel.Navigation.IsInspectorOpen != inspectorOpen
            || viewModel.Layout.EquipmentOutlineSearchText != query
            || !viewModel.Layout.SelectedItems.Select(item => item.Id).SequenceEqual(selected)
            || store.SerializeForEvidence(project) != document)
            throw new InvalidOperationException("Outline release outside did not recover without activating or changing project/selection/scope.");
        Console.WriteLine("Scene/outline release outside: normal recovered; no dialog, project, selection, query, scope or run changes.");
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
            "fault-chip-pressed" => "FaultSimulationButton",
            "target-chip-pressed" => "RunTargetDetailsButton",
            "run-options-pressed" => "RunOptionsButton",
            "workspace-tab-pressed" => "SimulationWorkspaceButton",
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

        var button = FindVisualDescendant<ButtonBase>(window, candidate => candidate.Name == buttonName)
            ?? throw new InvalidOperationException($"Top command '{buttonName}' was not available.");
        if (!button.IsVisible || !button.IsEnabled || (button is Button && button.Command?.CanExecute(button.CommandParameter) != true))
            throw new InvalidOperationException($"Top command '{buttonName}' was not enabled for its pressed-state check.");

        var initialChecked = (button as ToggleButton)?.IsChecked;
        var initiallyRunning = viewModel.IsRunning;
        var saveHash = state == "save-pressed"
            ? SHA256.HashData(File.ReadAllBytes(viewModel.CurrentProjectPath
                ?? throw new InvalidOperationException("Save pressed-state verification requires a saved smoke project.")))
            : null;
        var saveWriteTime = state == "save-pressed" ? File.GetLastWriteTimeUtc(viewModel.CurrentProjectPath!) : (DateTime?)null;
        try
        {
            uiInteraction.ActivateWindow();
            button.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            uiInteraction.MovePointerToCenter(button);
            await Task.Delay(100);
            var ownership = uiInteraction.CheckPointerOwnership(window);
            if (!ownership.IsOwned || !button.IsMouseOver || !button.IsKeyboardFocused)
                throw new InvalidOperationException($"Top command '{buttonName}' native input prerequisite failed. {ownership.Diagnostic}");
            uiInteraction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            uiInteraction.MarkSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            ownership = uiInteraction.CheckPointerOwnership(window);
            if (!ownership.IsOwned || !button.IsPressed || Mouse.LeftButton != MouseButtonState.Pressed)
                throw new InvalidOperationException($"Top command '{buttonName}' did not enter native pointer-down. {ownership.Diagnostic} Pressed={button.IsPressed}, LeftButton={Mouse.LeftButton}.");
            Console.WriteLine($"Top command native pointer-down: {buttonName}; {ownership.Diagnostic} Pressed={button.IsPressed}, LeftButton={Mouse.LeftButton}.");

            windowCapture.Capture(window, screenshotPath);
            var outsidePoint = window.PointToScreen(new Point(8, 8));
            uiInteraction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
            Mouse.Synchronize();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            await Task.Delay(100);
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
        if (button is Button && button.Command?.CanExecute(button.CommandParameter) != true)
            throw new InvalidOperationException($"Top command '{buttonName}' ran after its pressed-state capture.");
        if ((button as ToggleButton)?.IsChecked != initialChecked || viewModel.IsRunning != initiallyRunning)
            throw new InvalidOperationException($"Header control '{buttonName}' changed state after release outside.");
        if (saveHash is not null)
        {
            var currentPath = viewModel.CurrentProjectPath!;
            if (!SHA256.HashData(File.ReadAllBytes(currentPath)).SequenceEqual(saveHash))
                throw new InvalidOperationException("Save executed when the smoke pointer was released outside its button.");
            if (File.GetLastWriteTimeUtc(currentPath) != saveWriteTime)
                throw new InvalidOperationException("Save rewrote the file when the smoke pointer was released outside its button.");
        }

        Console.WriteLine($"Top command pointer-down captured and released without activation: {buttonName}.");
    }
}
