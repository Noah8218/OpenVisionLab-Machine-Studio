using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectDocumentOperationGateTests
{
    [Fact]
    public async Task SerializesConcurrentOperations()
    {
        var gate = new ProjectDocumentOperationGate();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();
        var active = 0;
        var maximumActive = 0;
        var order = new List<string>();

        var first = gate.RunAsync(async () =>
        {
            Enter("first");
            firstEntered.SetResult(true);
            await releaseFirst.Task;
            Exit("first");
        });

        await firstEntered.Task;
        var second = gate.RunAsync(() =>
        {
            Enter("second");
            Exit("second");
            return Task.CompletedTask;
        });

        releaseFirst.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActive);
        Assert.Equal(new[] { "first-enter", "first-exit", "second-enter", "second-exit" }, order);

        void Enter(string name)
        {
            lock (sync)
            {
                order.Add($"{name}-enter");
                active++;
                maximumActive = Math.Max(maximumActive, active);
            }
        }

        void Exit(string name)
        {
            lock (sync)
            {
                order.Add($"{name}-exit");
                active--;
            }
        }
    }

    [Fact]
    public async Task ReleasesAdmissionAfterFailure()
    {
        var gate = new ProjectDocumentOperationGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(async () =>
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("expected");
        }));

        var result = await gate.RunAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
    }
}
