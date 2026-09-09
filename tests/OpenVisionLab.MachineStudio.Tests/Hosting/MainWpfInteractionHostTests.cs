using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MainWpfInteractionHostTests
{
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
