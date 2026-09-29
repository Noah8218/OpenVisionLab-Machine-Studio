using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.ViewModel;
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
            window.Show();
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
