using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class AsyncOperationLifetimeTests
{
    [Fact]
    public async Task CancelRequestsCurrentOperationAndRetainsItsTask()
    {
        using var lifetime = new AsyncOperationLifetime();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = lifetime.Start(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        await started.Task;
        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        Assert.Same(operation, lifetime.CurrentTask);
    }

    [Fact]
    public async Task DisposeCancelsCurrentOperationAndRejectsNewWork()
    {
        using var lifetime = new AsyncOperationLifetime();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = lifetime.Start(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        await started.Task;
        lifetime.Dispose();
        lifetime.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => lifetime.Start(_ => Task.CompletedTask));
    }
}
