using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.View.Shell;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ShellKeyboardCommandBehaviorTests
{
    [Fact]
    public async Task RoutesDeleteAndLeavesTextEditingKeysToTheControl()
    {
        var result = await RunOnStaAsync(() =>
        {
            var window = new Window
            {
                Width = 320,
                Height = 180,
                Content = new TextBox()
            };
            var deleteCount = 0;
            var behavior = new ShellKeyboardCommandBehavior
            {
                DeleteCommand = new RelayCommand(_ => deleteCount++)
            };
            Interaction.GetBehaviors(window).Add(behavior);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 320, 180);
            window.Show();
            var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
            Assert.True(monitor.WindowIntersectsMonitor);
            var evidenceRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR") ?? @"D:\OpenVisionLab-TestData\Machine\r24-sol-scenario-navigation-20261011\captures\focused";
            Directory.CreateDirectory(evidenceRoot);
            File.WriteAllText(Path.Combine(evidenceRoot, "keyboard-monitor-" + nameof(ShellKeyboardCommandBehaviorTests) + "-" + behavior.IsModalOverlayOpen + ".json"), JsonSerializer.Serialize(monitor));
            window.UpdateLayout();

            var rootEvent = CreatePreviewKeyEventArgs(Key.Delete, window);
            window.RaiseEvent(rootEvent);

            var textBox = (TextBox)window.Content;
            Keyboard.Focus(textBox);
            var textEvent = CreatePreviewKeyEventArgs(Key.Delete, textBox);
            textBox.RaiseEvent(textEvent);
            window.Close();

            return (deleteCount, rootHandled: rootEvent.Handled, textHandled: textEvent.Handled);
        });

        Assert.Equal(1, result.deleteCount);
        Assert.True(result.rootHandled);
        Assert.False(result.textHandled);
    }

    [Fact]
    public void ResolvesSaveShortcutsOnlyForTheirExactModifierSets()
    {
        var save = new RelayCommand(_ => { });
        var saveAs = new RelayCommand(_ => { });

        Assert.Same(save, ShellKeyboardCommandBehavior.ResolveSaveCommand(Key.S, ModifierKeys.Control, save, saveAs));
        Assert.Same(saveAs, ShellKeyboardCommandBehavior.ResolveSaveCommand(Key.S, ModifierKeys.Control | ModifierKeys.Shift, save, saveAs));
        Assert.Null(ShellKeyboardCommandBehavior.ResolveSaveCommand(Key.S, ModifierKeys.Control | ModifierKeys.Alt, save, saveAs));
        Assert.Null(ShellKeyboardCommandBehavior.ResolveSaveCommand(Key.A, ModifierKeys.Control, save, saveAs));
    }

    [Theory]
    [InlineData(Key.S, ModifierKeys.Control, true, true)]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, true, true)]
    [InlineData(Key.D, ModifierKeys.Control, true, true)]
    [InlineData(Key.Delete, ModifierKeys.None, false, true)]
    [InlineData(Key.Delete, ModifierKeys.None, true, false)]
    [InlineData(Key.C, ModifierKeys.Control, true, false)]
    [InlineData(Key.V, ModifierKeys.Control, true, false)]
    [InlineData(Key.Z, ModifierKeys.Control, true, false)]
    [InlineData(Key.F5, ModifierKeys.None, true, true)]
    public void ModalOverlayBlocksBackgroundActionsAndRetainsTextEditing(Key key, ModifierKeys modifiers, bool textEditing, bool suppressed) =>
        Assert.Equal(suppressed, ShellKeyboardCommandBehavior.ShouldSuppressModalShortcut(key, modifiers, textEditing));

    [Fact]
    public async Task ModalOverlayEscapeRoutesCloseBeforeGalleryAndDoesNotDelete()
    {
        var result = await RunOnStaAsync(() =>
        {
            var window = new Window { Width = 320, Height = 180, Content = new Button() };
            var deleteCount = 0;
            var closeCount = 0;
            var behavior = new ShellKeyboardCommandBehavior
            {
                IsModalOverlayOpen = true,
                DeleteCommand = new RelayCommand(_ => deleteCount++),
                CloseOverlayCommand = new RelayCommand(_ => closeCount++)
            };
            Interaction.GetBehaviors(window).Add(behavior);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 320, 180);
            window.Show();
            var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
            Assert.True(monitor.WindowIntersectsMonitor);
            var evidenceRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR") ?? @"D:\OpenVisionLab-TestData\Machine\r24-sol-scenario-navigation-20261011\captures\focused";
            Directory.CreateDirectory(evidenceRoot);
            File.WriteAllText(Path.Combine(evidenceRoot, "keyboard-monitor-" + nameof(ShellKeyboardCommandBehaviorTests) + "-" + behavior.IsModalOverlayOpen + ".json"), JsonSerializer.Serialize(monitor));
            window.UpdateLayout();
            Keyboard.Focus((Button)window.Content);
            var delete = CreatePreviewKeyEventArgs(Key.Delete, window);
            window.RaiseEvent(delete);
            var escape = CreatePreviewKeyEventArgs(Key.Escape, window);
            window.RaiseEvent(escape);
            window.Close();
            Assert.Equal(1, closeCount);
            return (deleteCount, delete.Handled, escape.Handled);
        });
        Assert.Equal(0, result.deleteCount);
        Assert.True(result.rootHandled && result.textHandled);
    }

    private static KeyEventArgs CreatePreviewKeyEventArgs(Key key, UIElement sourceElement) => new(
        Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(sourceElement),
        0,
        key)
    {
        RoutedEvent = Keyboard.PreviewKeyDownEvent
    };

    private static Task<(int deleteCount, bool rootHandled, bool textHandled)> RunOnStaAsync(
        Func<(int deleteCount, bool rootHandled, bool textHandled)> action)
    {
        var completion = new TaskCompletionSource<(int deleteCount, bool rootHandled, bool textHandled)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
