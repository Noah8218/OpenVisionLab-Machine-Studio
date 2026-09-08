using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ConcurrentExecuteCallsStartOneDelegateAndAllowLaterReentry()
    {
        var calls = 0;
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            async _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
            },
            useCommandManagerRequery: false);

        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => command.Execute(null)))
            .ToArray();

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.False(command.CanExecute(null));

        releaseFirst.SetResult();
        await WaitForAsync(() => command.CanExecute(null));

        command.Execute(null);
        await WaitForAsync(() => calls == 2);

        Assert.Equal(2, calls);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }
}
