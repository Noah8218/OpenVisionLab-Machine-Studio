using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
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
    public async Task RejectsSnapshotWhenRuntimeIdentityChangesBeforeDispatch()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1
            });
        var latePublishedSnapshots = new ConcurrentQueue<SimulationSnapshot>();
        var lateAppliedSnapshots = new ConcurrentQueue<SimulationSnapshot>();
        var runtimeReconfigured = 0;
        var firstDispatchReleased = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new SimulationRuntimeLoop(
            engine,
            action =>
            {
                if (Interlocked.Increment(ref dispatchCount) == 1)
                {
                    return ReleaseFirstDispatchAsync(action, firstDispatchReleased.Task);
                }

                action();
                return Task.CompletedTask;
            },
            snapshot =>
            {
                if (Volatile.Read(ref runtimeReconfigured) != 0)
                {
                    latePublishedSnapshots.Enqueue(snapshot);
                }
            },
            snapshot =>
            {
                if (Volatile.Read(ref runtimeReconfigured) != 0)
                {
                    lateAppliedSnapshots.Enqueue(snapshot);
                }
            },
            () => initialRuntimeApplied.TrySetResult(true),
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            null,
            null,
            snapshot =>
            {
                var current = engine.CurrentSnapshot;
                return snapshot.RuntimeGeneration == current.RuntimeGeneration
                    && string.Equals(snapshot.ProjectId, current.ProjectId, StringComparison.Ordinal);
            });

        loop.Start(new SimulationRuntimeConfiguration([], [], []), "project-a");
        await WaitForAsync(() => engine.CurrentSnapshot.ProjectId == "project-a");

        var reconfigured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration([], [], []),
                "project-b"));
        Assert.True(reconfigured.IsAccepted);
        Volatile.Write(ref runtimeReconfigured, 1);

        firstDispatchReleased.TrySetResult(true);
        await WaitForAsync(() => Volatile.Read(ref dispatchCount) >= 2);

        Assert.DoesNotContain(latePublishedSnapshots, snapshot => snapshot.ProjectId == "project-a");
        Assert.DoesNotContain(lateAppliedSnapshots, snapshot => snapshot.ProjectId == "project-a");

        var stopTask = engine.StopAsync();
        loop.Cancel();
        await stopTask;
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(
            loop.IsCompleted,
            $"Runtime={loop.RuntimeTask.Status}, termination={loop.TerminationObservationTask.Status}");
        loop.Dispose();
    }

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

    [Fact]
    public async Task SlowCanonicalConsumerCannotPromoteAnOverflowToCompleteEvidence()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1,
                EventBufferCapacity = 1,
                CanonicalEventJournalCapacity = 2
            });
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanonicalEventSeen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCanonicalConsumer = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var journalCompleted = new TaskCompletionSource<SimulationEventJournalSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var canonicalEvents = new List<SimulationEvent>();
        var canonicalCallbackCount = 0;
        var unhandledExceptions = new List<Exception>();
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
            _ => { },
            _ => { },
            unhandledExceptions.Add,
            runtimeEvent =>
            {
                canonicalEvents.Add(runtimeEvent);
                if (Interlocked.Increment(ref canonicalCallbackCount) == 1)
                {
                    firstCanonicalEventSeen.TrySetResult(true);
                    releaseCanonicalConsumer.Task.GetAwaiter().GetResult();
                }
            },
            journal => journalCompleted.TrySetResult(journal));

        loop.Start(new SimulationRuntimeConfiguration([], [], []), "project-a");
        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await firstCanonicalEventSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => engine.EnqueueCommandAsync(new StepCommand())));
        Assert.All(results, result => Assert.True(result.IsAccepted, result.Detail));

        releaseCanonicalConsumer.TrySetResult(true);
        await engine.StopAsync();
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        var journal = await journalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(journal.IsComplete);
        Assert.Equal(2, journal.Capacity);
        Assert.Equal(2, journal.StoredEventCount);
        Assert.True(journal.TotalEventCount > journal.StoredEventCount);
        Assert.Equal(3, journal.FirstMissingEventIndex);
        Assert.Equal(journal.StoredEventCount, canonicalEvents.Count);
        Assert.Equal([1L, 2L], canonicalEvents.Select(item => item.EventIndex));
        Assert.True(loop.CanonicalEventConsumption.IsCompleted);
        Assert.Empty(unhandledExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescesExternalWaitMonitorRefreshesWithoutDroppingPauseResultOrReset(bool slowProjection)
    {
        var waiting = CreateExternalSnapshot(VirtualCameraState.AwaitingExternalResult, SequenceDebugPauseReason.None);
        var paused = CreateExternalSnapshot(VirtualCameraState.AwaitingExternalResult, SequenceDebugPauseReason.User);
        var result = CreateExternalSnapshot(VirtualCameraState.FrameReady, SequenceDebugPauseReason.User);
        var reset = new SimulationSnapshot(TimeSpan.Zero, 0, SimulationRunMode.Paused, SimulationControlOwner.Definition, 1, [], 0, [], []);
        var stream = Enumerable.Repeat(waiting, 100).Append(paused).Append(result).Append(reset).ToArray();
        using var engine = new SnapshotBurstEngine(stream);
        var published = new List<SimulationSnapshot>();
        var applied = new List<SimulationSnapshot>();
        var failures = new List<Exception>();
        using var loop = new SimulationRuntimeLoop(engine, async action =>
        {
            action();
            if (slowProjection) await Task.Delay(60);
        }, published.Add, applied.Add, () => { }, _ => { }, _ => { }, _ => { }, failures.Add);

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(15));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(published, applied);
        Assert.Same(waiting, applied[0]);
        Assert.Equal(new[] { paused, result, reset }, applied.TakeLast(3));
        Assert.True(applied.Count < stream.Length, "Repeated external-wait snapshots must not each dispatch a full UI projection.");
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescesRealTimeSceneAndMonitorUpdatesWithoutDroppingUserPause(bool slowProjection)
    {
        var running = new SimulationSnapshot(TimeSpan.FromMilliseconds(60), 12, SimulationRunMode.RealTime, SimulationControlOwner.EmbeddedSequence, 1, [], 0, [], [], [], AutomaticRunSnapshot.NotConfigured, [], projectId: "resume", runtimeGeneration: 1);
        var paused = new SimulationSnapshot(TimeSpan.FromMilliseconds(65), 13, SimulationRunMode.Paused, SimulationControlOwner.EmbeddedSequence, 1, [], 0, [], [], [], AutomaticRunSnapshot.NotConfigured, [], sequenceDebug: new SequenceDebugSnapshot(false, null, SequenceDebugPauseReason.User, null, []), projectId: "resume", runtimeGeneration: 1);
        var stream = Enumerable.Repeat(running, 100).Append(paused).ToArray();
        using var engine = new SnapshotBurstEngine(stream);
        var published = new List<SimulationSnapshot>();
        var applied = new List<SimulationSnapshot>();
        var failures = new List<Exception>();
        using var loop = new SimulationRuntimeLoop(engine, async action =>
        {
            action();
            if (slowProjection) await Task.Delay(60);
        }, published.Add, applied.Add, () => { }, _ => { }, _ => { }, _ => { }, failures.Add);

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(15));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(running, applied[0]);
        Assert.Same(paused, applied[^1]);
        Assert.Equal(published, applied);
        Assert.True(applied.Count < stream.Length, "Real-time refreshes must leave dispatcher time after a slow projection.");
        Assert.Empty(failures);
    }

    private static SimulationSnapshot CreateExternalSnapshot(VirtualCameraState state, SequenceDebugPauseReason reason) => new(
        TimeSpan.FromMilliseconds(60), 12, SimulationRunMode.Paused, SimulationControlOwner.EmbeddedSequence, 1, [], 0, [], [],
        [new VirtualCameraSnapshot("camera", "Camera", state, 1, "acquisition", "recipe", 0, 0, null),
            new VirtualCameraSnapshot("second-camera", "Second camera", VirtualCameraState.AwaitingExternalResult, 1, "second-acquisition", "recipe", 0, 0, null)],
        new AutomaticRunSnapshot(true, true, false, 0, 0), [],
        sequenceDebug: new SequenceDebugSnapshot(false, null, reason, null, []), projectId: "external-wait", runtimeGeneration: 1);

    private sealed class SnapshotBurstEngine : ISimulationEngine
    {
        private readonly Channel<SimulationSnapshot> _snapshots = Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _events = Channel.CreateUnbounded<SimulationEvent>();

        internal SnapshotBurstEngine(IReadOnlyList<SimulationSnapshot> snapshots)
        {
            CurrentSnapshot = snapshots[^1];
            foreach (var snapshot in snapshots) _snapshots.Writer.TryWrite(snapshot);
            _snapshots.Writer.TryComplete();
            _events.Writer.TryComplete();
        }

        public SimulationSnapshot CurrentSnapshot { get; }
        public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshots.Reader;
        public ChannelReader<SimulationEvent> EventReader => _events.Reader;
        public Task<SimulationEngineTerminationResult> Termination => Task.FromResult(new SimulationEngineTerminationResult(SimulationEngineTerminationOutcome.Normal, 0, TimeSpan.Zero));
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SimulationCommandResult> EnqueueCommandAsync(SimulationCommand command, CancellationToken cancellationToken = default) => Task.FromResult(new SimulationCommandResult(command.CommandId, true, 0, TimeSpan.Zero, SimulationCommandErrorCode.None, null));
        public void AddAxis(ServoAxisComponent axis) => throw new NotSupportedException();
        public void Dispose() { }
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

    private static async Task ReleaseFirstDispatchAsync(Action action, Task releaseTask)
    {
        await releaseTask;
        action();
    }
}
