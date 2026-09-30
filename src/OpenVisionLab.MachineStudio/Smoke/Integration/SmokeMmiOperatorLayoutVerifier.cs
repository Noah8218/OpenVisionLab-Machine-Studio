using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeMmiOperatorLayoutReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string State { get; init; }
    public required string SettingsPath { get; init; }
    public required int RecipeCatalogCount { get; init; }
    public required string SelectedRecipePath { get; init; }
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public required IReadOnlyList<string> EvidencePaths { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
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

internal static class SmokeMmiOperatorLayoutVerifier
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const byte VirtualKeyDown = 0x28;
    private const byte VirtualKeyEscape = 0x1B;
    private const byte VirtualKeyEnter = 0x0D;

    public static async Task<SmokeMmiOperatorLayoutReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        string settingsPath,
        string evidenceRoot,
        SmokeUiInteraction interaction,
        SmokeNativeInput nativeInput,
        SmokeWindowCapture capture)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-mmi-operator-state requires --smoke-run-layout.");
        }

        if (!string.Equals(state, "interaction", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-mmi-operator-state '{state}'. Expected interaction.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var evidencePaths = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        viewModel.Navigation.IsInspectionSettingsExpanded = false;
        viewModel.Navigation.IsInspectionWorkspace = true;
        viewModel.Navigation.SelectedInspectionTabIndex = 1;
        viewModel.Navigation.SelectedInspectionSettingsTabIndex = 1;
        await IdleAsync(window);
        var mmi = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(window);
        Check("run-mode", viewModel.IsRunMode);
        Check("mmi-visible", mmi is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 });
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Check("window-intersects-selected-monitor", monitor.WindowIntersectsMonitor);

        if (mmi is null)
        {
            failures.Add("mmi-not-found");
            return CreateReport(
                state,
                settingsPath,
                viewModel,
                checks,
                failures,
                evidencePaths,
                monitor);
        }

        var mmiScroll = Find<ScrollViewer>(mmi, "MmiInspectionPreviewScrollViewer");
        var sceneViewport = SmokeVisualTreeQuery.FindVisualDescendant<MachineSceneViewport>(window);
        Check(
            "mmi-preview-scroll-container",
            mmiScroll is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
            && mmiScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
        if (mmiScroll is not null)
        {
            mmiScroll.ScrollToBottom();
            await IdleAsync(window);
            var resultText = Find<TextBlock>(mmi, "MmiThreeDResultStatusTextBlock");
            var resultBounds = resultText?.TransformToAncestor(mmiScroll).TransformBounds(new Rect(resultText.RenderSize));
            Check("mmi-preview-scroll-reaches-result", resultText is { IsVisible: true }
                && resultBounds is { } bounds && bounds.Top >= 0 && bounds.Bottom <= mmiScroll.ViewportHeight + 1);
            mmiScroll.ScrollToTop();
            await IdleAsync(window);
        }
        else Check("mmi-preview-scroll-reaches-result", false);
        foreach (var actionName in new[] { "MmiPublishButton", "MmiRefreshButton", "MmiApplyResultButton" })
        {
            var action = Find<Button>(mmi, actionName);
            var actionBounds = action?.TransformToAncestor(mmi).TransformBounds(new Rect(action.RenderSize));
            Check($"{actionName}-anchored-in-visible-workspace", action is { IsVisible: true }
                && actionBounds is { } bounds && new Rect(mmi.RenderSize).Contains(bounds));
        }
        var cameraImageFrame = Find<Border>(mmi, "MmiVirtualCameraImageFrame");
        var expectedCameraImageFrameHeight = viewModel.Navigation.IsNarrowLayout
            ? 240
            : viewModel.Navigation.IsCompactLayout
                ? 180
                : 260;
        Check(
            "camera-input-preview-matches-r19-density",
            cameraImageFrame is { IsVisible: true }
            && Math.Abs(cameraImageFrame.ActualHeight - expectedCameraImageFrameHeight) <= 1.0);
        var inspectionLayout = Find<Grid>(mmi, "MmiInspectionLayoutGrid");
        var previewPanel = Find<Border>(mmi, "MmiInspectionPreviewPanel");
        var setupPanel = Find<Grid>(mmi, "MmiInspectionSetupPanel");
        var externalSettingsGrid = Find<Grid>(mmi, "MmiExternalInspectionSettingsGrid");
        var headerActions = Find<StackPanel>(mmi, "MmiInspectionHeaderActions");
        var statusPill = Find<Border>(mmi, "MmiInspectionStatusPill");
        var settingsToggle = Find<ToggleButton>(mmi, "MmiInspectionSettingsToggle");
        var isNarrowLayout = viewModel.Navigation.IsNarrowLayout;
        var expectedInspectionGap = isNarrowLayout
            ? 0.0
            : viewModel.Navigation.IsCompactLayout
                ? 12.0
                : 18.0;
        var expectedSetupColumnWidth = inspectionLayout is null || isNarrowLayout
            ? 0
            : viewModel.Navigation.IsCompactLayout
                ? 330.0
                : Math.Max(310.0, (inspectionLayout.ActualWidth - expectedInspectionGap) * 0.375);
        var expectedSetupPanelWidth = isNarrowLayout && inspectionLayout is not null
            ? inspectionLayout.ColumnDefinitions[0].ActualWidth
            : expectedSetupColumnWidth;
        Check(
            "inspection-responsive-layout-matches-r19-breakpoints",
            inspectionLayout is { IsVisible: true, ColumnDefinitions.Count: 3, RowDefinitions.Count: 5 }
            && previewPanel is { IsVisible: true, ActualWidth: >= 300 }
            && setupPanel is { IsVisible: true, ActualWidth: > 0 }
            && headerActions is { IsVisible: true }
            && statusPill is { IsVisible: true }
            && settingsToggle is { IsVisible: true, IsChecked: false }
            && Grid.GetColumn(previewPanel) == 0
            && Grid.GetRow(previewPanel) == 2
            && Grid.GetColumn(setupPanel) == (isNarrowLayout ? 0 : 2)
            && Grid.GetRow(setupPanel) == (isNarrowLayout ? 3 : 2)
            && Grid.GetColumn(headerActions) == (isNarrowLayout ? 0 : 2)
            && Grid.GetRow(headerActions) == (isNarrowLayout ? 1 : 0)
            && Math.Abs(previewPanel.ActualWidth - inspectionLayout.ColumnDefinitions[0].ActualWidth) <= 1.0
            && Math.Abs(inspectionLayout.ColumnDefinitions[1].ActualWidth - expectedInspectionGap) <= 1.0
            && Math.Abs(inspectionLayout.ColumnDefinitions[2].ActualWidth - expectedSetupColumnWidth) <= 1.0
            && Math.Abs(setupPanel.ActualWidth - expectedSetupPanelWidth) <= 1.0);

        var settingsToggleInitialText = settingsToggle?.Content?.ToString();
        var recipeBeforeToggle = viewModel.Integration.Setup.SelectedRecipe;
        var setupPathBeforeToggle = viewModel.Integration.Setup.InspectionRecipePath;
        var unsavedBeforeToggle = viewModel.HasUnsavedChanges;
        if (settingsToggle is { IsVisible: true })
        {
            try
            {
                await ToggleWithSpaceAsync(window, settingsToggle, nativeInput);
                await IdleAsync(window);
                var settingsColumns = externalSettingsGrid?.ColumnDefinitions;
                var machineStateCard = Find<Border>(mmi, "MmiMachineStateCard");
                var recipeCard = Find<Border>(mmi, "MmiInspectionRecipeCard");
                var resultCard = Find<Border>(mmi, "MmiInspectionResultCard");
                Check(
                    "inspection-wide-mode-state-bound",
                    viewModel.Navigation.IsInspectionSettingsExpanded);
                Check("inspection-wide-mode-toggle-checked", settingsToggle.IsChecked == true);
                Check(
                    "inspection-wide-mode-hides-input",
                    previewPanel?.Visibility == Visibility.Collapsed);
                Check(
                    "inspection-wide-mode-expands-setup",
                    setupPanel is { IsVisible: true } && externalSettingsGrid is { ColumnDefinitions.Count: 2 }
                    && inspectionLayout is not null
                    && Math.Abs(setupPanel.ActualWidth - inspectionLayout.ActualWidth) <= 1.0
                    && Grid.GetColumn(setupPanel) == 0
                    && Grid.GetColumnSpan(setupPanel) == 3);
                Check(
                    "inspection-wide-mode-two-column-settings",
                    settingsColumns is { Count: 2 }
                    && settingsColumns[1].ActualWidth >= 220
                    && settingsColumns[0].ActualWidth > settingsColumns[1].ActualWidth);
                Check(
                    "inspection-wide-mode-machine-state-card-position",
                    machineStateCard is { IsVisible: true }
                    && Grid.GetRow(machineStateCard) == 0
                    && Grid.GetColumn(machineStateCard) == 1);
                Check(
                    "inspection-wide-mode-recipe-card-position",
                    recipeCard is { IsVisible: true }
                    && Grid.GetRow(recipeCard) == 0
                    && Grid.GetColumn(recipeCard) == 0
                    && Grid.GetRowSpan(recipeCard) == 2);
                Check(
                    "inspection-wide-mode-result-card-position",
                    resultCard is { IsVisible: true }
                    && Grid.GetRow(resultCard) == 1
                    && Grid.GetColumn(resultCard) == 1);
                Check(
                    "inspection-wide-toggle-label-updates",
                    !string.Equals(settingsToggleInitialText, settingsToggle.Content?.ToString(), StringComparison.Ordinal));
                Check(
                    "inspection-wide-toggle-recipe-preserved",
                    viewModel.Integration.Setup.SelectedRecipe == recipeBeforeToggle
                    && viewModel.Integration.Setup.InspectionRecipePath == setupPathBeforeToggle);
                Check("inspection-wide-toggle-unsaved-state-preserved", viewModel.HasUnsavedChanges == unsavedBeforeToggle);

                await ToggleWithSpaceAsync(window, settingsToggle, nativeInput);
                await IdleAsync(window);
                Check(
                    "inspection-wide-mode-restores-input-layout",
                    !viewModel.Navigation.IsInspectionSettingsExpanded
                    && settingsToggle.IsChecked == false
                    && previewPanel is { Visibility: Visibility.Visible, IsVisible: true }
                    && setupPanel is { IsVisible: true }
                    && Grid.GetColumn(setupPanel) == (isNarrowLayout ? 0 : 2)
                    && Grid.GetRow(setupPanel) == (isNarrowLayout ? 3 : 2)
                    && externalSettingsGrid?.ColumnDefinitions[1].ActualWidth == 0
                    && settingsToggle.Content?.ToString() == settingsToggleInitialText
                    && viewModel.Integration.Setup.SelectedRecipe == recipeBeforeToggle
                    && viewModel.Integration.Setup.InspectionRecipePath == setupPathBeforeToggle
                    && viewModel.HasUnsavedChanges == unsavedBeforeToggle);
            }
            catch (Exception exception)
            {
                Check("inspection-wide-mode-interaction", false);
                failures.Add($"inspection-wide-mode-interaction:{exception.Message}");
                viewModel.Navigation.IsInspectionSettingsExpanded = false;
                await IdleAsync(window);
            }
        }
        else
        {
            Check("inspection-wide-mode-state-bound", false);
            Check("inspection-wide-mode-toggle-checked", false);
            Check("inspection-wide-mode-hides-input", false);
            Check("inspection-wide-mode-expands-setup", false);
            Check("inspection-wide-mode-two-column-settings", false);
            Check("inspection-wide-mode-machine-state-card-position", false);
            Check("inspection-wide-mode-recipe-card-position", false);
            Check("inspection-wide-mode-result-card-position", false);
            Check("inspection-wide-toggle-label-updates", false);
            Check("inspection-wide-toggle-recipe-preserved", false);
            Check("inspection-wide-toggle-unsaved-state-preserved", false);
            Check("inspection-wide-mode-restores-input-layout", false);
        }
        Check(
            "inspection-workspace-does-not-split-scene",
            sceneViewport is null || !sceneViewport.IsVisible);

        var combo = Find<ComboBox>(mmi, "MmiActiveRecipeComboBox");
        var addButton = Find<Button>(mmi, "MmiAddRecipeButton");
        var removeButton = Find<Button>(mmi, "MmiRemoveRecipeButton");
        var saveButton = Find<Button>(mmi, "MmiSaveSetupButton");
        var applyButton = Find<Button>(mmi, "MmiApplyResultButton");
        var inspectionExecutionModeText = Find<TextBlock>(mmi, "MmiInspectionExecutionModeTextBlock");
        var twoDResultStatusText = Find<TextBlock>(mmi, "MmiTwoDResultStatusTextBlock");
        var threeDResultStatusText = Find<TextBlock>(mmi, "MmiThreeDResultStatusTextBlock");

        Check("recipe-combo-rendered", IsRendered(combo));
        Check("recipe-catalog-loaded", combo is not null && combo.Items.Count >= 2);
        Check(
            "recipe-selection-rendered",
            combo?.SelectedItem is MachineIntegrationRecipeItemViewModel selected
            && ReferenceEquals(selected, viewModel.Integration.Setup.SelectedRecipe));
        Check("add-button-rendered", IsRendered(addButton));
        Check("remove-button-rendered", IsRendered(removeButton));
        Check("save-button-rendered", IsRendered(saveButton));
        Check("apply-button-rendered", IsRendered(applyButton));
        Check("inspection-execution-mode-rendered", IsRendered(inspectionExecutionModeText));
        Check(
            "inspection-execution-mode-bound",
            inspectionExecutionModeText is not null
            && string.Equals(
                inspectionExecutionModeText.Text,
                viewModel.Integration.InspectionExecutionModeText,
                StringComparison.Ordinal));
        Check("2d-result-status-rendered", IsRendered(twoDResultStatusText));
        Check("3d-result-status-rendered", IsRendered(threeDResultStatusText));
        Check(
            "2d-result-status-bound",
            twoDResultStatusText?.Text == viewModel.Integration.LatestTwoDResultStatusText);
        Check(
            "3d-result-status-bound",
            threeDResultStatusText?.Text == viewModel.Integration.LatestThreeDResultStatusText);
        if (inspectionExecutionModeText is not null)
        {
            var initialExecutionModeText = inspectionExecutionModeText.Text;
            var initialWaitForExternalResult = viewModel.Integration.Setup.WaitForExternalResult;
            viewModel.Integration.Setup.WaitForExternalResult = !initialWaitForExternalResult;
            await IdleAsync(window);
            Check(
                "inspection-execution-mode-updates",
                !string.Equals(inspectionExecutionModeText.Text, initialExecutionModeText, StringComparison.Ordinal)
                && string.Equals(
                    inspectionExecutionModeText.Text,
                    viewModel.Integration.InspectionExecutionModeText,
                    StringComparison.Ordinal));
            viewModel.Integration.Setup.WaitForExternalResult = initialWaitForExternalResult;
            await IdleAsync(window);
        }
        Check(
            "add-command-state-bound",
            addButton?.IsEnabled == viewModel.Integration.Setup.AddRecipeCommand.CanExecute(null));
        Check(
            "remove-command-state-bound",
            removeButton?.IsEnabled == viewModel.Integration.Setup.RemoveRecipeCommand.CanExecute(
                viewModel.Integration.Setup.SelectedRecipe));
        Check(
            "save-command-state-bound",
            saveButton?.IsEnabled == viewModel.Integration.Setup.SaveSetupCommand.CanExecute(null));
        Check(
            "apply-command-state-bound",
            applyButton?.IsEnabled == viewModel.Integration.CanApplyResultToSimulation);
        Check(
            "apply-disabled-without-result",
            applyButton is not null && !applyButton.IsEnabled && !viewModel.Integration.CanApplyResultToSimulation);

        if (combo is not null && combo.IsEnabled && combo.Items.Count >= 2)
        {
            try
            {
                interaction.ActivateWindow();
                combo.BringIntoView();
                combo.UpdateLayout();
                combo.Focus();
                Keyboard.Focus(combo);
                combo.IsDropDownOpen = false;
                var initialSelection = combo.SelectedItem;
                nativeInput.SendKey(VirtualKeyDown);
                await Task.Delay(100);
                await IdleAsync(window);
                if (ReferenceEquals(combo.SelectedItem, initialSelection))
                {
                    RaiseKey(combo, Key.Down);
                    await Task.Delay(100);
                    await IdleAsync(window);
                }
                Check("recipe-combo-arrow-selection", !ReferenceEquals(combo.SelectedItem, initialSelection));
                if (ReferenceEquals(combo.SelectedItem, initialSelection))
                {
                    failures.Add($"recipe-combo-arrow-selection-index:{combo.SelectedIndex}");
                }
                combo.IsDropDownOpen = true;
                await IdleAsync(window);
                Check("recipe-combo-keyboard-focus", combo.IsKeyboardFocusWithin);
                Check("recipe-combo-popup-open", combo.IsDropDownOpen);
                capture.SetPopupContent(
                    (combo.Template.FindName("PART_Popup", combo) as Popup)?.Child as FrameworkElement);
                var popupPath = Path.Combine(evidenceRoot, "mmi-interaction-combo-open.png");
                capture.Capture(window, popupPath);
                evidencePaths.Add(popupPath);
                nativeInput.SendKey(VirtualKeyEscape);
                await Task.Delay(100);
                await IdleAsync(window);
                if (combo.IsDropDownOpen)
                {
                    RaiseKey(combo, Key.Escape);
                    await Task.Delay(100);
                    await IdleAsync(window);
                }
                Check("recipe-combo-popup-closed", !combo.IsDropDownOpen);
                if (combo.IsDropDownOpen)
                {
                    failures.Add("recipe-combo-popup-remained-open-after-escape");
                }
                capture.SetPopupContent(null);
                Check(
                    "recipe-selection-two-way",
                    ReferenceEquals(combo.SelectedItem, viewModel.Integration.Setup.SelectedRecipe)
                    && string.Equals(
                        viewModel.Integration.Setup.InspectionRecipePath,
                        viewModel.Integration.Setup.SelectedRecipe?.Path,
                        StringComparison.OrdinalIgnoreCase));
                Mouse.Capture(null);
            }
            catch (Exception exception)
            {
                checks["recipe-combo-interaction"] = false;
                failures.Add($"recipe-combo-interaction:{exception.Message}");
                combo.IsDropDownOpen = false;
                capture.SetPopupContent(null);
                Mouse.Capture(null);
            }
        }
        else
        {
            Check("recipe-combo-keyboard-focus", false);
            Check("recipe-combo-popup-open", false);
            Check("recipe-combo-arrow-selection", false);
            Check("recipe-combo-popup-closed", false);
            Check("recipe-selection-two-way", false);
        }

        Mouse.Capture(null);
        if (addButton is not null && addButton.IsVisible)
        {
            var catalogCount = viewModel.Integration.Setup.RecipeCatalogItems.Count;
            try
            {
                var probe = await ProbeButtonPointerStateAsync(
                    window,
                    addButton,
                    interaction,
                    nativeInput,
                    Path.Combine(evidenceRoot, "mmi-interaction-add-pressed.png"),
                    capture.Capture);
                evidencePaths.Add(probe.PressedEvidencePath);
                Check("add-button-hover", probe.Hovered);
                Check("add-button-pressed", probe.Pressed);
                Check("add-button-mouse-leave-recovery", probe.Recovered);
                Check("add-button-pointer-down-did-not-open-dialog", catalogCount == viewModel.Integration.Setup.RecipeCatalogItems.Count);
            }
            catch (Exception exception)
            {
                Check("add-button-hover", false);
                Check("add-button-pressed", false);
                Check("add-button-mouse-leave-recovery", false);
                Check("add-button-pointer-down-did-not-open-dialog", false);
                failures.Add($"add-button-interaction:{exception.Message}");
                nativeInput.ReleasePointer();
                Mouse.Capture(null);
            }
        }
        else
        {
            Check("add-button-hover", false);
            Check("add-button-pressed", false);
            Check("add-button-mouse-leave-recovery", false);
            Check("add-button-pointer-down-did-not-open-dialog", false);
        }

        if (saveButton is not null && saveButton.IsVisible)
        {
            try
            {
                interaction.ActivateWindow();
                saveButton.BringIntoView();
                saveButton.UpdateLayout();
                saveButton.Focus();
                Keyboard.Focus(saveButton);
                await IdleAsync(window);
                Check("save-button-keyboard-focus", saveButton.IsKeyboardFocused);
                nativeInput.SendKey(VirtualKeyEnter);
                await Task.Delay(150);
                await IdleAsync(window);
                Check("save-button-keyboard-activation", File.Exists(settingsPath)
                    && (viewModel.Integration.StatusText.Contains("저장", StringComparison.Ordinal)
                        || viewModel.Integration.StatusText.Contains("saved", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception exception)
            {
                Check("save-button-keyboard-focus", false);
                Check("save-button-keyboard-activation", false);
                failures.Add($"save-button-interaction:{exception.Message}");
            }
        }
        else
        {
            Check("save-button-keyboard-focus", false);
            Check("save-button-keyboard-activation", false);
        }

        return CreateReport(
            state,
            settingsPath,
            viewModel,
            checks,
            failures,
            evidencePaths,
            monitor);
    }

    private static async Task<SmokeButtonProbeResult> ProbeButtonPointerStateAsync(
        ShellWindow window,
        Button button,
        SmokeUiInteraction interaction,
        SmokeNativeInput nativeInput,
        string pressedEvidencePath,
        Action<Window, string> capture)
    {
        interaction.ActivateWindow();
        button.BringIntoView();
        button.UpdateLayout();
        button.Focus();
        Keyboard.Focus(button);
        await IdleAsync(window);
        interaction.MovePointerToCenter(button);
        await Task.Delay(100);
        Mouse.Capture(button, CaptureMode.SubTree);
        Mouse.Synchronize();
        nativeInput.SendMouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
        await Task.Delay(100);
        var hovered = button.IsMouseOver;

        var pressed = false;
        for (var attempt = 0; attempt < 3 && !pressed; attempt++)
        {
            Mouse.Capture(button, CaptureMode.SubTree);
            Mouse.Synchronize();
            nativeInput.SendMouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
            nativeInput.SendMouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            nativeInput.MarkPointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            await Task.Delay(50);
            if (!button.IsPressed && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                button.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.MouseDownEvent
                });
            }

            pressed = button.IsPressed;
        }

        capture(window, pressedEvidencePath);

        var outsidePoint = window.PointToScreen(new Point(8, 8));
        interaction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
        Mouse.Synchronize();

        nativeInput.ReleasePointer();
        Mouse.Capture(null);
        await IdleAsync(window);
        await Task.Delay(100);
        return new(hovered, pressed, !button.IsMouseOver && !button.IsPressed, pressedEvidencePath);
    }

    private static async Task IdleAsync(Window window) =>
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static async Task ToggleWithSpaceAsync(
        Window window,
        ToggleButton element,
        SmokeNativeInput nativeInput)
    {
        window.Activate();
        nativeInput.ActivateWindow(window);
        element.BringIntoView();
        element.UpdateLayout();
        element.Focus();
        Keyboard.Focus(element);
        await IdleAsync(window);
        if (!element.IsKeyboardFocused)
        {
            throw new InvalidOperationException("Inspection settings toggle did not receive keyboard focus.");
        }
        var expectedChecked = element.IsChecked != true;
        nativeInput.MovePointerToCenter(element);
        await Task.Delay(50);
        var ownership = nativeInput.CheckPointerOwnership(window);
        if (!ownership.IsOwned) throw new InvalidOperationException($"Inspection settings input lost window ownership. {ownership.Diagnostic}");
        nativeInput.SendKey(0x20);
        for (var attempt = 0; attempt < 40 && element.IsChecked != expectedChecked; attempt++)
        {
            await Task.Delay(25);
            await IdleAsync(window);
        }
        if (element.IsChecked != expectedChecked)
        {
            throw new InvalidOperationException($"Inspection settings Space input did not change the toggle to {expectedChecked}.");
        }
        await IdleAsync(window);
    }

    private static void RaiseKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("Inspection keyboard target had no presentation source.");
        target.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static T? Find<T>(DependencyObject parent, string name)
        where T : FrameworkElement =>
        SmokeVisualTreeQuery.FindVisualDescendant<T>(
            parent,
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

    private static T? FindParent<T>(DependencyObject element)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsRendered(FrameworkElement? element) =>
        element is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 };

    private static SmokeMmiOperatorLayoutReport CreateReport(
        string state,
        string settingsPath,
        MainViewModel viewModel,
        IReadOnlyDictionary<string, bool> checks,
        IReadOnlyList<string> failures,
        IReadOnlyList<string> evidencePaths,
        SmokeMonitorEvidence monitor) =>
        new()
        {
            State = state,
            SettingsPath = Path.GetFullPath(settingsPath),
            RecipeCatalogCount = viewModel.Integration.Setup.RecipeCatalogItems.Count,
            SelectedRecipePath = viewModel.Integration.Setup.SelectedRecipe?.Path ?? string.Empty,
            Checks = checks,
            Failures = failures,
            EvidencePaths = evidencePaths,
            Monitor = monitor
        };

    private sealed record SmokeButtonProbeResult(
        bool Hovered,
        bool Pressed,
        bool Recovered,
        string PressedEvidencePath);
}
