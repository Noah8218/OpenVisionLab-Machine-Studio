using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Shell;

internal sealed class MainWpfInteractionHost
{
    internal void ShutdownApplication() => Application.Current.Shutdown();

    internal async Task CommitFocusedEditorAsync()
    {
        Keyboard.ClearFocus();
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            await dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.DataBind);
        }
    }

    internal async Task DispatchAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        await dispatcher.InvokeAsync(action);
    }

    internal async Task DispatchBatchProgressAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action);
    }
}
