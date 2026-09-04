using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationRuntimeLoopTests
{
    [Fact]
    public async Task StartsOnceConfiguresAndDeliversSnapshotsBeforeCancellation()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1
            });
        var publishedSnapshots = new List<SimulationSnapshot>();
        var receivedEvents = new List<SimulationEvent>();
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminationObserved = new TaskCompletionSource<SimulationEngineTerminationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var configurationFailures = new List<string>();
        var unhandledExceptions = new List<Exception>();
        using var loop = new SimulationRuntimeLoop(
            engine,
            static action =>
            {
                action();
                return Task.CompletedTask;
            },
            publishedSnapshots.Add,
            _ => { },
            () => initialRuntimeApplied.TrySetResult(true),
            configurationFailures.Add,
            receivedEvents.Add,
            termination => terminationObserved.TrySetResult(termination),
            unhandledExceptions.Add);

        loop.Start(new SimulationRuntimeConfiguration([], [], []));

        await WaitForAsync(() => initialRuntimeApplied.Task.IsCompleted);
        Assert.Throws<InvalidOperationException>(
            () => loop.Start(new SimulationRuntimeConfiguration([], [], [])));
        Assert.True(loop.CancellationToken.CanBeCanceled);

        var stopTask = engine.StopAsync();
        loop.Cancel();
        await stopTask;
        await loop.RuntimeTask;
        var termination = await terminationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEmpty(publishedSnapshots);
        Assert.Empty(configurationFailures);
        Assert.Empty(unhandledExceptions);
        Assert.Equal(SimulationEngineTerminationOutcome.Stopped, termination.Outcome);
        Assert.True(loop.IsCompleted);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The runtime loop did not apply initial configuration.");
    }
}
