using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        var nameTextBox = FindVisualDescendant<TextBox>(
            inspector,
            textBox => string.Equals(textBox.Name, "ComponentNameTextBox", StringComparison.Ordinal));
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

        switch (state.ToLowerInvariant())
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
                    "Expected focus, hover, popup, validation, bottom, pressed, " +
                    "alignment-focus, alignment-hover, alignment-pressed, layer-focus, " +
                    "layer-hover, layer-pressed, or layer-disabled.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }
}
