using OpenVisionLab.Machine.Simulation.Commands;
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

        loop.Start(new SimulationRuntimeConfiguration([], [], []), "project-a");

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
        Assert.Contains(publishedSnapshots, snapshot => snapshot.ProjectId == "project-a");
        Assert.Empty(configurationFailures);
        Assert.Empty(unhandledExceptions);
        Assert.Equal(SimulationEngineTerminationOutcome.Stopped, termination.Outcome);
        Assert.True(loop.IsCompleted);
    }

    [Fact]
    public async Task DispatchesEngineTerminationThroughThePresentationBoundary()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1
            });
        var dispatchScope = false;
        var terminationWasDispatched = false;
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminationObserved = new TaskCompletionSource<SimulationEngineTerminationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new SimulationRuntimeLoop(
            engine,
            action =>
            {
                var previousScope = dispatchScope;
                dispatchScope = true;
                try
                {
                    action();
                }
                finally
                {
                    dispatchScope = previousScope;
                }

                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            () => initialRuntimeApplied.TrySetResult(true),
            _ => { },
            _ => { },
            termination =>
            {
                terminationWasDispatched = dispatchScope;
                terminationObserved.TrySetResult(termination);
            },
            _ => { });

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await engine.StopAsync();
        loop.Cancel();
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            SimulationEngineTerminationOutcome.Stopped,
            (await terminationObserved.Task).Outcome);
        Assert.True(terminationWasDispatched);
    }

    [Fact]
    public async Task ConsumesCanonicalJournalSeparatelyFromPresentationWindow()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1,
                EventBufferCapacity = 16
            });
        var presentedEvents = new List<SimulationEvent>();
        var canonicalEvents = new List<SimulationEvent>();
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var journalCompleted = new TaskCompletionSource<SimulationEventJournalSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new SimulationRuntimeLoop(
            engine,
            static action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            () => initialRuntimeApplied.TrySetResult(true),
            _ => { },
            presentedEvents.Add,
            _ => { },
            _ => { },
            canonicalEvents.Add,
            journal => journalCompleted.TrySetResult(journal));

        loop.Start(new SimulationRuntimeConfiguration([], [], []), "project-a");
        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        await engine.StopAsync();
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        var journal = await journalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEmpty(canonicalEvents);
        Assert.Equal(
            canonicalEvents.Select(item => item.EventIndex),
            Enumerable.Range(1, canonicalEvents.Count).Select(index => (long)index));
        Assert.Equal(canonicalEvents.Count, journal.StoredEventCount);
        Assert.Equal(canonicalEvents.Count, journal.TotalEventCount);
        Assert.True(journal.IsCompleted);
        Assert.True(journal.IsComplete);
        Assert.Equal(canonicalEvents.Count, presentedEvents.Count);
        Assert.Equal(
            canonicalEvents.Select(item => item.EventIndex),
            presentedEvents.Select(item => item.EventIndex));
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
