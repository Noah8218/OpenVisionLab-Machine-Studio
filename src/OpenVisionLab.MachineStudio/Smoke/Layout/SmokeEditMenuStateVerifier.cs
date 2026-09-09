using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeEditMenuStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task<FrameworkElement?> ApplyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        var editMenu = FindVisualDescendant<MenuItem>(
            window,
            item => string.Equals(item.Name, "EditMenuItem", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Edit menu was not available.");
        FrameworkElement? popupContent = null;

        if (state.Equals("open-enabled", StringComparison.OrdinalIgnoreCase))
        {
            if (!viewModel.CopyLayoutSelectionCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Copy command was unavailable for the selected layout component.");
            }
            viewModel.CopyLayoutSelectionCommand.Execute(null);
            editMenu.IsSubmenuOpen = true;
        }
        else if (state.Equals("open-disabled", StringComparison.OrdinalIgnoreCase))
        {
            editMenu.IsSubmenuOpen = true;
        }
        else if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase))
        {
            interaction.MovePointerToCenter(editMenu);
            interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            interaction.MarkSmokePointerHeld();
        }
        else if (state.Equals("duplicate-pressed", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("delete-pressed", StringComparison.OrdinalIgnoreCase))
        {
            var targetCommand = state.StartsWith("duplicate", StringComparison.OrdinalIgnoreCase)
                ? viewModel.DuplicateLayoutSelectionCommand
                : viewModel.DeleteLayoutComponentCommand;
            if (!targetCommand.CanExecute(null))
            {
                throw new InvalidOperationException($"The {state[..^8]} command was unavailable for the selected layout component.");
            }

            editMenu.IsSubmenuOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            var targetItem = editMenu.Items
                .OfType<MenuItem>()
                .Single(item => ReferenceEquals(item.Command, targetCommand));
            popupContent = targetItem;
            while (VisualTreeHelper.GetParent(popupContent) is FrameworkElement popupParent)
            {
                popupContent = popupParent;
            }
            interaction.MovePointerToCenter(targetItem);
            await Task.Delay(100);
            interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            interaction.MarkSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!targetItem.IsMouseOver)
            {
                throw new InvalidOperationException($"The {state[..^8]} menu item was not under the held pointer.");
            }
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported --smoke-edit-menu-state '{state}'. Expected open-enabled, open-disabled, pressed, duplicate-pressed, or delete-pressed.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        return popupContent;
    }
}
