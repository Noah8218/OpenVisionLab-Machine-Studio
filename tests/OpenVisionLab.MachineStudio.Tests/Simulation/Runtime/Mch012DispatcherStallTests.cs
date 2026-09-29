using System.Collections.Concurrent;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class Mch012DispatcherStallTests
{
    [Fact]
    public async Task DispatcherStallDoesNotChangeFastForwardFinalEngineSnapshot()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(5),
                MaxCatchUpTicks = 3,
                TimeScale = 0.000001
            });
        var publishedSnapshots = new ConcurrentQueue<SimulationSnapshot>();
        var appliedSnapshots = new ConcurrentQueue<SimulationSnapshot>();
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchBlocked = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stallDispatch = 0;
        var loop = new SimulationRuntimeLoop(
            engine,
            async action =>
            {
                if (Volatile.Read(ref stallDispatch) != 0)
                {
                    dispatchBlocked.TrySetResult(true);
                    await releaseDispatch.Task.ConfigureAwait(false);
                }

                action();
            },
            publishedSnapshots.Enqueue,
            appliedSnapshots.Enqueue,
            () => initialRuntimeApplied.TrySetResult(true),
            _ => { },
            _ => { },
            _ => { },
            _ => { });

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref stallDispatch, 1);

        var command = await engine.EnqueueCommandAsync(new FastForwardCommand(17));
        Assert.True(command.IsAccepted, command.Detail);
        await dispatchBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
            engine.CurrentSnapshot.TickIndex == 17
            && engine.CurrentSnapshot.RunMode == SimulationRunMode.Paused);

        var finalEngineSnapshot = engine.CurrentSnapshot;
        Assert.Equal(17, finalEngineSnapshot.TickIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(85), finalEngineSnapshot.SimulationTime);

        releaseDispatch.TrySetResult(true);
        await WaitUntilAsync(() => publishedSnapshots.Any(snapshot => snapshot.TickIndex == 17));
        await WaitUntilAsync(() => appliedSnapshots.Any(snapshot => snapshot.TickIndex == 17));

        loop.Cancel();
        await engine.StopAsync();
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(loop.IsCompleted);
        loop.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
