using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Shell;

internal sealed class MainWpfInteractionHost
{
    private readonly Dispatcher? _dispatcher;

    internal MainWpfInteractionHost(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher
            ?? Application.Current?.Dispatcher
            ?? (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
                ? Dispatcher.FromThread(Thread.CurrentThread)
                : null);
    }

    // ShellWindow waits for save decisions and runtime shutdown before closing.
    internal void RequestApplicationClose() => Application.Current.MainWindow?.Close();

    internal async Task CommitFocusedEditorAsync()
    {
        Keyboard.ClearFocus();
        if (GetDispatcher() is { } dispatcher
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished)
        {
            await dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.DataBind);
        }
    }

    internal async Task DispatchAsync(Action action)
    {
        var dispatcher = GetDispatcher();
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        await dispatcher.InvokeAsync(action);
    }

    internal async Task DispatchBatchProgressAsync(Action action)
    {
        var dispatcher = GetDispatcher();
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action);
    }

    internal Task DispatchOnUiThreadAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var dispatcher = GetDispatcher();
        if (dispatcher is null)
        {
            return operation();
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return dispatcher.CheckAccess()
            ? operation()
            : dispatcher.InvokeAsync(operation).Task.Unwrap();
    }

    private Dispatcher? GetDispatcher() => _dispatcher ?? Application.Current?.Dispatcher;
}
