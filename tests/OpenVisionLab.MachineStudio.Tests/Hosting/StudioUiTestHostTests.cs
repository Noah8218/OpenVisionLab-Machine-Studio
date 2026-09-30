using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class StudioUiTestHostTests
{
    private readonly StudioUiTestHost _ui;

    public StudioUiTestHostTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task ClosingAWindowAndFailingACallbackKeepTheApplicationUsableOnItsOwningThread()
    {
        var unloaded = false;
        var threadId = await _ui.InvokeTaskAsync(async () =>
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Empty(Application.Current.Windows.Cast<Window>());
            Assert.True(Application.Current.CheckAccess());
            var window = new Window { Style = null, ShowInTaskbar = false, ShowActivated = false };
            window.Unloaded += (_, _) => unloaded = true;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.Close();
            return Environment.CurrentManagedThreadId;
        });
        Assert.True(unloaded);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _ui.InvokeAsync<int>(
            () => throw new InvalidOperationException("Test callback failure")));

        await _ui.InvokeTaskAsync(async () =>
        {
            await Task.Yield();
            Assert.Equal(threadId, Environment.CurrentManagedThreadId);
            Assert.True(Application.Current.CheckAccess());
            Assert.False(Dispatcher.CurrentDispatcher.HasShutdownStarted);
            Assert.Empty(Application.Current.Windows.Cast<Window>());
            var window = new Window { Style = null, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try { Assert.True(window.IsVisible); }
            finally { window.Close(); }
        });
    }
}
