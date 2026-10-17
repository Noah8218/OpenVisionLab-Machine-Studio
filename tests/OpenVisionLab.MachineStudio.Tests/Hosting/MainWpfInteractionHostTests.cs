using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class MainWpfInteractionHostTests
{
    private readonly StudioUiTestHost _ui;

    public MainWpfInteractionHostTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task CommittingEditorFlushesLostFocusBindingAndRestoresKeyboardFocus()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var source = new TextBox { Text = "Before edit" };
            var editor = new TextBox();
            editor.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text))
            {
                Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            });
            var window = new Window { Content = editor, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 640, 480);
            window.Show();
            try
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                editor.Focus();
                Assert.Same(editor, Keyboard.FocusedElement);
                editor.Text = "After edit";
                Assert.Equal("Before edit", source.Text);
                await new MainWpfInteractionHost(window.Dispatcher).CommitFocusedEditorAsync();
                Assert.Equal("After edit", source.Text);
                Assert.Same(editor, Keyboard.FocusedElement);
                Assert.True(window.IsKeyboardFocusWithin);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task DropsUiCallbacksAfterDispatcherShutdownBegins()
    {
        var executed = await RunOnStaAsync(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var host = new MainWpfInteractionHost(dispatcher);
            var callbackCount = 0;

            dispatcher.InvokeShutdown();

            var dispatchTask = host.DispatchOnUiThreadAsync(() =>
            {
                callbackCount++;
                return Task.CompletedTask;
            });
            var progressTask = host.DispatchBatchProgressAsync(() => callbackCount++);
            Task.WhenAll(dispatchTask, progressTask).GetAwaiter().GetResult();
            return callbackCount;
        });

        Assert.Equal(0, executed);
    }

    private static Task<int> RunOnStaAsync(Func<int> action)
    {
        var completion = new TaskCompletionSource<int>(
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
