using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeLayoutPropertyStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        var inspector = FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was not available.");
        var nameTextBox = FindVisualDescendant<TextBox>(inspector, textBox => textBox.IsVisible && textBox.Name == "R24ComponentNameTextBox")
            ?? FindVisualDescendant<TextBox>(inspector, textBox => textBox.Name == "ComponentNameTextBox");
        var behaviorComboBox = FindVisualDescendant<ComboBox>(
            inspector,
            comboBox => string.Equals(comboBox.Name, "BehaviorBindingComboBox", StringComparison.Ordinal));
        var nudgeRightButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "NudgeRightButton", StringComparison.Ordinal));
        var cylinderSection = FindVisualDescendant<StackPanel>(
            inspector,
            panel => string.Equals(panel.Name, "CylinderPropertiesSection", StringComparison.Ordinal));
        var validationMessage = FindVisualDescendant<TextBlock>(
            inspector,
            textBlock => string.Equals(textBlock.Name, "PropertyValidationMessage", StringComparison.Ordinal));
        var alignLeftButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "AlignLeftButton", StringComparison.Ordinal));
        var bringForwardButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "BringForwardButton", StringComparison.Ordinal));
        var bringToFrontButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "BringToFrontButton", StringComparison.Ordinal));

        state = state.ToLowerInvariant();
        switch (state)
        {
            case "focus":
                nameTextBox?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                nameTextBox?.Focus();
                if (nameTextBox is not null)
                {
                    nameTextBox.CaretIndex = nameTextBox.Text.Length;
                }
                break;
            case "inspector-draft":
            case "inspector-error":
            case "inspector-bottom":
            case "inspector-apply-hover":
            case "inspector-apply-pressed":
            case "inspector-mouse-leave":
            {
                var draftEditor = viewModel.Layout.SelectedComponentEditor
                    ?? throw new InvalidOperationException("A selected component is required for the V09 draft capture.");
                if (state == "inspector-bottom")
                {
                    var scroll = FindVisualDescendant<ScrollViewer>(inspector, control => control.Name == "DesignInspectorScrollViewer")
                        ?? throw new InvalidOperationException("The V09 draft scroll region was not available.");
                    scroll.ScrollToBottom();
                    break;
                }
                var draftX = FindVisualDescendant<TextBox>(inspector, textBox => textBox.IsVisible
                    && System.Windows.Automation.AutomationProperties.GetAutomationId(textBox) == "EquipmentCoordinateX");
                var draftValue = state == "inspector-error" ? "NaN" : "125";
                if (draftX is not null)
                {
                    draftX.SetCurrentValue(TextBox.TextProperty, draftValue);
                    draftX.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                else draftEditor.DraftXText = draftValue;
                var placementApply = viewModel.Navigation.IsEquipmentWorkspace ? draftEditor.ApplyEquipmentPlacementDraftCommand : draftEditor.ApplyPlacementDraftCommand;
                if (state == "inspector-error")
                {
                    placementApply.Execute(null);
                    if (string.IsNullOrWhiteSpace(draftEditor.PlacementDraftError))
                        throw new InvalidOperationException("The V09 non-finite coordinate did not produce an error.");
                    break;
                }
                if (state == "inspector-draft") break;
                var applyButton = FindVisualDescendant<Button>(inspector, button => button.IsVisible && ReferenceEquals(button.Command, placementApply))
                    ?? throw new InvalidOperationException("The V09 pinned Apply button was not available.");
                interaction.ActivateWindow();
                var ownership = interaction.CheckPointerOwnership(window);
                if (!ownership.IsOwned) throw new InvalidOperationException(ownership.Diagnostic);
                interaction.MovePointerToCenter(applyButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!applyButton.IsMouseOver) throw new InvalidOperationException("The pointer did not enter the V09 Apply button.");
                if (state == "inspector-apply-pressed")
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!applyButton.IsPressed) throw new InvalidOperationException("The V09 Apply button was not actually pressed.");
                }
                if (state == "inspector-mouse-leave")
                {
                    interaction.MovePointerToCenter(nameTextBox ?? throw new InvalidOperationException("The V09 name input was not available."));
                    var mouseLeaveDeadline = DateTime.UtcNow.AddSeconds(1);
                    while ((applyButton.IsMouseOver || applyButton.IsPressed) && DateTime.UtcNow < mouseLeaveDeadline) await Task.Delay(10);
                    await window.Dispatcher.InvokeAsync(Mouse.Synchronize, DispatcherPriority.Input);
                    if (applyButton.IsMouseOver || applyButton.IsPressed) throw new InvalidOperationException("The V09 Apply hover did not recover after mouse leave.");
                }
                break;
            }
            case "drive-popup":
            {
                if (!viewModel.OpenEquipmentDriveTabCommand.CanExecute(null))
                    throw new InvalidOperationException("The selected component settings cannot be opened.");
                viewModel.OpenEquipmentDriveTabCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var settingsChoice = FindVisualDescendant<ComboBox>(inspector,
                    comboBox => comboBox.IsVisible && comboBox.IsEnabled && comboBox.DataContext is EquipmentInspectorField { IsChoice: true })
                    ?? throw new InvalidOperationException("The selected component settings have no visible choice field. Select a component with a choice setting, such as a camera mount.");
                settingsChoice.BringIntoView();
                settingsChoice.Focus();
                settingsChoice.IsDropDownOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var settingsPopup = FindVisualDescendant<Popup>(settingsChoice);
                if (settingsPopup?.IsOpen != true || settingsPopup.Child is not FrameworkElement { IsVisible: true } popupContent)
                    throw new InvalidOperationException("The selected component settings popup did not open.");
                interaction.SetPopupContent(popupContent);
                Console.WriteLine($"Equipment settings popup: component={viewModel.Layout.SelectedItem?.Id}, field={((EquipmentInspectorField)settingsChoice.DataContext).Id}, selectedValue={settingsChoice.SelectedValue}.");
                Console.WriteLine($"Equipment settings popup bounds: size={popupContent.RenderSize}, descendants={System.Windows.Media.VisualTreeHelper.GetDescendantBounds(popupContent)}, transform={popupContent.RenderTransform.Value}, screen={popupContent.PointToScreen(new Point())}.");
                break;
            }
            case "inspector-manual":
            case "inspector-locked":
            {
                if (!viewModel.OpenEquipmentSinglePartCheckCommand.CanExecute(null))
                    throw new InvalidOperationException("A single selected manual component is required for the V09 manual capture.");
                viewModel.OpenEquipmentSinglePartCheckCommand.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!viewModel.CanStartManualEquipmentControl && DateTime.UtcNow < deadline) await Task.Delay(10);
                if (!viewModel.CanStartManualEquipmentControl) throw new InvalidOperationException(viewModel.ManualEquipment.ManualControlHintText);
                if (state == "inspector-locked")
                {
                    viewModel.StartManualEquipmentControlCommand.Execute(null);
                    while (!viewModel.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
                    if (!viewModel.IsRunning || !viewModel.IsRunMode || viewModel.IsSceneEditable)
                        throw new InvalidOperationException("The V09 manual run did not acquire control and lock editing.");
                    viewModel.OpenEquipmentPropertiesCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var lockPanel = FindVisualDescendant<Border>(inspector, border => border.Name == "R24ReadOnlyLock");
                    var coordinate = FindVisualDescendant<TextBox>(inspector, textBox => textBox.IsVisible
                        && System.Windows.Automation.AutomationProperties.GetAutomationId(textBox) == "EquipmentCoordinateX");
                    var apply = FindVisualDescendant<Button>(inspector, button => button.Name == "EquipmentPlacementApplyButton");
                    var moveTool = FindVisualDescendant<RadioButton>(window, button => button.Name == "EquipmentMoveToolButton");
                    if (!viewModel.IsRunning || lockPanel?.IsVisible != true || coordinate?.IsEnabled != false || apply?.IsEnabled != false
                        || moveTool?.IsEnabled != false || viewModel.Layout.SelectedComponentEditor?.ApplyEquipmentPlacementDraftCommand.CanExecute(null) != false)
                        throw new InvalidOperationException("The equipment lock, rendered input, Apply and Move command gates do not agree.");
                }
                break;
            }
            case "hover":
                behaviorComboBox?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(behaviorComboBox ?? throw new InvalidOperationException(
                    "Behavior binding combo box was not available."));
                break;
            case "popup":
                behaviorComboBox?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (behaviorComboBox is null)
                {
                    throw new InvalidOperationException("Behavior binding combo box was not available.");
                }
                behaviorComboBox.Focus();
                behaviorComboBox.IsDropDownOpen = true;
                break;
            case "validation":
                if (viewModel.Layout.SelectedComponentEditor is not { } editor)
                {
                    throw new InvalidOperationException("Layout property editor was not available.");
                }
                editor.Name = " ";
                validationMessage?.BringIntoView();
                break;
            case "bottom":
                cylinderSection?.BringIntoView();
                break;
            case "geometry":
            case "geometry-bottom":
            case "geometry-hint":
            case "geometry-estimate":
            {
                var geometryFieldIds = new[]
                {
                    "EquipmentDraftRotation",
                    "EquipmentDraftWidth",
                    "EquipmentDraftDepth",
                    "EquipmentDraftVerticalHeight"
                };
                var geometryFields = geometryFieldIds
                    .Select(id => FindVisualDescendant<TextBox>(
                        inspector,
                        textBox => string.Equals(
                            System.Windows.Automation.AutomationProperties.GetAutomationId(textBox),
                            id,
                            StringComparison.Ordinal)))
                    .ToArray();
                if (geometryFields.Any(field => field is null || !field.IsVisible))
                {
                    throw new InvalidOperationException("The Equipment geometry draft fields were not visible.");
                }
                var inspectorScrollViewer = FindVisualDescendant<ScrollViewer>(
                    inspector,
                    scrollViewer => string.Equals(scrollViewer.Name, "DesignInspectorScrollViewer", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("The Design inspector scroll region was not available.");
                var geometryTargetName = state.ToLowerInvariant() switch
                {
                    "geometry" => "EquipmentDraftRotationField",
                    "geometry-bottom" => "EquipmentDraftVerticalHeightField",
                    "geometry-hint" => "EquipmentDraftVerticalEnvelopeHint",
                    "geometry-estimate" => "EquipmentDraftVerticalEnvelopeEstimated",
                    _ => throw new InvalidOperationException("The Equipment geometry state was not supported.")
                };
                var geometryTarget = FindVisualDescendant<FrameworkElement>(
                    inspector,
                    element => string.Equals(element.Name, geometryTargetName, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"The Equipment geometry target '{geometryTargetName}' was not available.");
                geometryTarget.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                var targetBounds = geometryTarget.TransformToAncestor(inspectorScrollViewer)
                    .TransformBounds(new Rect(new Point(), geometryTarget.RenderSize));
                inspectorScrollViewer.ScrollToVerticalOffset(Math.Max(
                    0,
                    inspectorScrollViewer.VerticalOffset + targetBounds.Top - 4));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                targetBounds = geometryTarget.TransformToAncestor(inspectorScrollViewer)
                    .TransformBounds(new Rect(new Point(), geometryTarget.RenderSize));
                if (targetBounds.Top < 0 || targetBounds.Bottom > inspectorScrollViewer.ViewportHeight)
                {
                    throw new InvalidOperationException(
                        $"The Equipment geometry target '{geometryTargetName}' was outside the inspector viewport " +
                        $"(top={targetBounds.Top:0.##}, bottom={targetBounds.Bottom:0.##}, height={inspectorScrollViewer.ViewportHeight:0.##}).");
                }
                break;
            }
            case "drive":
                if (!viewModel.OpenEquipmentDriveTabCommand.CanExecute(null) ||
                    viewModel.Layout.SelectedComponentEditor?.ShowAxisDriveProperties != true)
                {
                    throw new InvalidOperationException("A selected axis-backed stage is required for the drive draft capture.");
                }
                viewModel.OpenEquipmentDriveTabCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var axisMin = FindVisualDescendant<TextBox>(inspector,
                    textBox => string.Equals(textBox.Name, "ComponentAxisMinTextBox", StringComparison.Ordinal));
                axisMin?.BringIntoView();
                if (axisMin?.IsVisible != true)
                {
                    throw new InvalidOperationException("The selected stage drive draft was not visible.");
                }
                break;
            case "pressed":
                nudgeRightButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(nudgeRightButton ?? throw new InvalidOperationException(
                    "Nudge button was not available."));
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                break;
            case "alignment-focus":
                alignLeftButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                alignLeftButton?.Focus();
                break;
            case "alignment-hover":
                alignLeftButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(alignLeftButton ?? throw new InvalidOperationException(
                    "Alignment button was not available."));
                break;
            case "alignment-pressed":
                alignLeftButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(alignLeftButton ?? throw new InvalidOperationException(
                    "Alignment button was not available."));
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                break;
            case "layer-focus":
                bringForwardButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                bringForwardButton?.Focus();
                break;
            case "layer-hover":
                bringForwardButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(bringForwardButton ?? throw new InvalidOperationException(
                    "Bring forward button was not available."));
                break;
            case "layer-pressed":
                bringForwardButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(bringForwardButton ?? throw new InvalidOperationException(
                    "Bring forward button was not available."));
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                break;
            case "layer-disabled":
                if (viewModel.ChangeLayoutLayerOrderCommand.CanExecute(LayoutLayerOrder.BringToFront.ToString()))
                {
                    viewModel.ChangeLayoutLayerOrderCommand.Execute(LayoutLayerOrder.BringToFront.ToString());
                }
                bringToFrontButton?.BringIntoView();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-layout-property-state '{state}'. " +
                    "Expected focus, hover, popup, validation, bottom, geometry, geometry-bottom, geometry-hint, " +
                    "geometry-estimate, drive, drive-popup, pressed, inspector-draft, inspector-error, inspector-bottom, " +
                    "inspector-apply-hover, inspector-apply-pressed, inspector-mouse-leave, inspector-manual, inspector-locked, " +
                    "alignment-focus, alignment-hover, alignment-pressed, layer-focus, " +
                    "layer-hover, layer-pressed, or layer-disabled.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }
}
