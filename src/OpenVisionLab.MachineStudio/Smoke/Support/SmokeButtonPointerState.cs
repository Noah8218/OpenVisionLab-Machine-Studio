using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeButtonPointerState
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task FocusAsync(
        Window window,
        Button button,
        SmokeUiInteraction interaction,
        string failureMessage)
    {
        interaction.ActivateWindow();
        button.BringIntoView();
        button.UpdateLayout();
        button.Focus();
        Keyboard.Focus(button);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        AssertSmoke(button.IsKeyboardFocused, failureMessage);
    }

    public static async Task HoverAsync(
        Button button,
        SmokeUiInteraction interaction,
        Func<string> failureMessage)
    {
        interaction.MovePointerToCenter(button);
        Mouse.Capture(button, CaptureMode.SubTree);
        Mouse.Synchronize();
        interaction.MouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
        await Task.Delay(200);
        AssertSmoke(button.IsMouseOver, failureMessage());
    }

    public static async Task HoverThenPressAsync(
        Window window,
        Button button,
        SmokeUiInteraction interaction,
        Func<string> hoverFailureMessage,
        string pressedFailureMessage)
    {
        await HoverAsync(button, interaction, hoverFailureMessage);
        for (var attempt = 0; attempt < 3 && !button.IsPressed; attempt++)
        {
            Mouse.Capture(button, CaptureMode.SubTree);
            Mouse.Synchronize();
            interaction.MouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
            interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            interaction.MarkSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            await Task.Delay(20);
            // The native input can arrive before WPF updates ButtonBase; replay the routed event only
            // after WPF reports the left button as pressed so the helper never fakes a release state.
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
            if (!button.IsPressed)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                await Task.Delay(50);
            }
        }

        var captureDescription = Mouse.Captured?.GetType().Name ?? "none";
        AssertSmoke(
            button.IsPressed,
            $"{pressedFailureMessage} " +
            $"IsEnabled={button.IsEnabled}; IsVisible={button.IsVisible}; " +
            $"IsMouseOver={button.IsMouseOver}; IsMouseCaptured={button.IsMouseCaptured}; " +
            $"Captured={captureDescription}; " +
            $"MouseLeftButton={Mouse.LeftButton}; " +
            $"PrimaryLeftButton={Mouse.PrimaryDevice.LeftButton}; " +
            $"IsKeyboardFocused={button.IsKeyboardFocused}.");
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
