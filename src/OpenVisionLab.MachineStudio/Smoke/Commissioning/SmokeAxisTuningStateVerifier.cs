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

internal static class SmokeAxisTuningStateVerifier
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
        var editor = viewModel.AxisDriveTuningEditor
            ?? throw new InvalidOperationException(
                "--smoke-axis-tuning-state requires an authored axis selection.");
        var followingErrorInput = FindVisualDescendant<global::Wpf.Ui.Controls.NumberBox>(
            inspector,
            box => string.Equals(
                System.Windows.Automation.AutomationProperties.GetAutomationId(box),
                "AxisFollowingErrorLimitNumberBox",
                StringComparison.Ordinal));
        var resetButton = FindVisualDescendant<Button>(
            inspector,
            button => string.Equals(button.Name, "ResetAxisDriveDefaultsButton", StringComparison.Ordinal));
        var validation = FindVisualDescendant<TextBlock>(
            inspector,
            text => string.Equals(text.Name, "AxisTuningValidationMessage", StringComparison.Ordinal));

        switch (state.ToLowerInvariant())
        {
            case "ready":
                break;
            case "focus":
                followingErrorInput?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                followingErrorInput?.Focus();
                break;
            case "hover":
                followingErrorInput?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(followingErrorInput ?? throw new InvalidOperationException(
                    "Following-error input was not available."));
                break;
            case "validation":
                editor.FollowingErrorLimit = 0;
                validation?.BringIntoView();
                inspector.DesignInspectorScrollViewer.ScrollToEnd();
                break;
            case "pressed":
                resetButton?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(resetButton ?? throw new InvalidOperationException(
                    "Restore drive defaults button was not available."));
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-axis-tuning-state '{state}'. " +
                    "Expected ready, focus, hover, validation, or pressed.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine($"Axis tuning visual state applied: {state}");
    }
}
