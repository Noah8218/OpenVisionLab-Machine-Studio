using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StudioUiTestCollection : ICollectionFixture<StudioUiTestHost>
{
    public const string Name = "Studio UI application";
}

public sealed class StudioUiTestHost : IAsyncLifetime
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    public StudioUiTestHost()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Studio UI tests" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public async Task InitializeAsync()
    {
        _thread.Start();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        var dispatcher = await _ready.Task;
        try { return await dispatcher.InvokeAsync(action); }
        finally { await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    }

    public async Task<T> InvokeTaskAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = await _ready.Task;
        try { return await (await dispatcher.InvokeAsync(action)); }
        finally { await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    }

    public async Task InvokeTaskAsync(Func<Task> action)
    {
        var dispatcher = await _ready.Task;
        try { await (await dispatcher.InvokeAsync(action)); }
        finally { await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    }

    public async Task DisposeAsync()
    {
        if (_ready.Task.IsCompletedSuccessfully)
        {
            var dispatcher = await _ready.Task;
            await dispatcher.InvokeAsync(() => Application.Current.Shutdown());
        }

        if (!await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(10))))
        {
            throw new TimeoutException("The Studio UI test dispatcher did not shut down.");
        }
    }

    private void Run()
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // Match App.xaml's resources without registering its interactive Startup handler.
            application.Resources.MergedDictionaries.Add(new global::Wpf.Ui.Markup.ThemesDictionary { Theme = global::Wpf.Ui.Appearance.ApplicationTheme.Dark });
            application.Resources.MergedDictionaries.Add(new global::Wpf.Ui.Markup.ControlsDictionary());
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/OpenVisionLab.MachineStudio;component/Theme/OpenVisionMachineTheme.xaml", UriKind.Relative)
            });
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ready.SetResult(dispatcher);
        }
        catch (Exception exception)
        {
            _ready.SetException(exception);
            return;
        }

        Application.Current.Run();
    }
}
