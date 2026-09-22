using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationOutputStoreTests
{
    [Fact]
    public void EventPublisher_AssignsIndexesAndCompletesReader()
    {
        var publisher = new SimulationEventPublisher(2);

        Assert.True(publisher.TryPublish(3, TimeSpan.FromMilliseconds(15), "Test", "First", "first"));
        Assert.True(publisher.TryPublish(4, TimeSpan.FromMilliseconds(20), "Test", "Second", "second"));
        publisher.Complete();

        Assert.True(publisher.Reader.TryRead(out var first));
        Assert.True(publisher.Reader.TryRead(out var second));
        Assert.Equal(1, first.EventIndex);
        Assert.Equal(2, second.EventIndex);
        Assert.Equal(3, first.TickIndex);
        Assert.Equal(4, second.TickIndex);
        Assert.False(publisher.Reader.TryRead(out _));
        Assert.True(publisher.Reader.Completion.IsCompleted);
        var journal = publisher.JournalSnapshot;
        Assert.Equal(2, journal.StoredEventCount);
        Assert.Equal(2, journal.TotalEventCount);
        Assert.Equal(1, journal.FirstEventIndex);
        Assert.Equal(2, journal.LastEventIndex);
        Assert.True(journal.IsCompleted);
        Assert.True(journal.IsComplete);
        Assert.Null(journal.FirstMissingEventIndex);
    }

    [Fact]
    public void EventPublisher_RejectsPublicationAfterCompletion()
    {
        var publisher = new SimulationEventPublisher(2);

        Assert.True(publisher.TryPublish(1, TimeSpan.Zero, "Test", "First", "first"));
        publisher.Complete();

        Assert.False(publisher.TryPublish(2, TimeSpan.FromMilliseconds(5), "Test", "Late", "late"));
        Assert.True(publisher.Reader.TryRead(out var first));
        Assert.Equal(1, first.EventIndex);
        Assert.False(publisher.Reader.TryRead(out _));

        var journal = publisher.JournalSnapshot;
        Assert.Equal(1, journal.StoredEventCount);
        Assert.Equal(1, journal.TotalEventCount);
        Assert.Equal(1, journal.FirstEventIndex);
        Assert.Equal(1, journal.LastEventIndex);
        Assert.True(journal.IsCompleted);
        Assert.True(journal.IsComplete);
        Assert.Null(journal.FirstMissingEventIndex);
    }

    [Fact]
    public void EventPublisher_DisposeCompletesPresentationReader()
    {
        var publisher = new SimulationEventPublisher(2);

        Assert.True(publisher.TryPublish(1, TimeSpan.Zero, "Test", "First", "first"));
        publisher.Dispose();

        Assert.True(publisher.Reader.TryRead(out var first));
        Assert.Equal(1, first.EventIndex);
        Assert.False(publisher.Reader.TryRead(out _));
        Assert.True(publisher.Reader.Completion.IsCompleted);
        Assert.True(publisher.JournalSnapshot.IsCompleted);
    }

    [Fact]
    public async Task EventPublisher_CompletionRaceKeepsCanonicalAndPresentationEventsAligned()
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            using var start = new Barrier(3);
            var publisher = new SimulationEventPublisher(1);
            var publishTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return publisher.TryPublish(attempt, TimeSpan.Zero, "Test", "Race", "race");
            });
            var completeTask = Task.Run(() =>
            {
                start.SignalAndWait();
                publisher.Complete();
            });

            start.SignalAndWait();
            await Task.WhenAll(publishTask, completeTask);

            var presentationCount = 0;
            while (publisher.Reader.TryRead(out _))
            {
                presentationCount++;
            }

            var journal = publisher.JournalSnapshot;
            publisher.Dispose();
            Assert.Equal(
                journal.TotalEventCount,
                presentationCount);
        }
    }

    [Fact]
    public async Task EventJournal_PreservesCanonicalRecordsAndMarksBudgetOverflow()
    {
        var publisher = new SimulationEventPublisher(2);

        publisher.TryPublish(1, TimeSpan.Zero, "Test", "First", "first");
        publisher.TryPublish(2, TimeSpan.FromMilliseconds(5), "Test", "Second", "second");
        publisher.TryPublish(3, TimeSpan.FromMilliseconds(10), "Test", "Third", "third");
        publisher.Complete();

        var events = new List<SimulationEvent>();
        await foreach (var runtimeEvent in publisher.ReadJournalAsync())
        {
            events.Add(runtimeEvent);
        }

        Assert.Equal([1L, 2L], events.Select(item => item.EventIndex));
        var journal = publisher.JournalSnapshot;
        Assert.Equal(2, journal.StoredEventCount);
        Assert.Equal(3, journal.TotalEventCount);
        Assert.Equal(1, journal.FirstEventIndex);
        Assert.Equal(3, journal.LastEventIndex);
        Assert.True(journal.IsCompleted);
        Assert.False(journal.IsComplete);
        Assert.Equal(3, journal.FirstMissingEventIndex);
    }

    [Fact]
    public async Task EventJournalExport_CapturesAndRoundTripsCompleteCanonicalPrefix()
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            EventBufferCapacity = 8
        });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        await engine.StopAsync();

        var package = await SimulationEventJournalExportPackage.CaptureAsync(engine);
        var json = SimulationEventJournalExportPackage.SaveToJson(package);
        var path = Path.Combine(
            TestStorage.RootPath,
            "pl-0237-canonical-event-journal-export",
            "journal.json");
        SimulationEventJournalExportPackage.SaveToJson(package, path);
        var restored = SimulationEventJournalExportPackage.LoadFromJson(path);

        Assert.True(package.CanExport);
        Assert.True(package.HasValidJournalHash());
        Assert.NotEmpty(package.Events);
        Assert.NotNull(restored);
        Assert.True(restored!.CanExport);
        Assert.True(restored.HasValidJournalHash());
        Assert.Equal(package.JournalHash, restored.JournalHash);
        Assert.Equal(json, SimulationEventJournalExportPackage.SaveToJson(restored));
    }

    [Fact]
    public void EventJournalExport_RejectsIncompleteBudgetOverflow()
    {
        var journal = new SimulationEventJournalSnapshot(
            Capacity: 1,
            StoredEventCount: 1,
            TotalEventCount: 2,
            FirstEventIndex: 1,
            LastEventIndex: 1,
            IsCompleted: true,
            IsComplete: false,
            FirstMissingEventIndex: 2);
        var events = new[]
        {
            new SimulationEvent(1, 0, TimeSpan.Zero, "Test", "First", "first")
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationEventJournalExportPackage.Create(journal, events));

        Assert.Contains("Incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EventPublisher_SeparatesCanonicalCapacityFromPresentationWindow()
    {
        var publisher = new SimulationEventPublisher(
            presentationCapacity: 1,
            canonicalCapacity: 3);

        publisher.TryPublish(1, TimeSpan.Zero, "Test", "First", "first");
        publisher.TryPublish(2, TimeSpan.FromMilliseconds(5), "Test", "Second", "second");
        publisher.TryPublish(3, TimeSpan.FromMilliseconds(10), "Test", "Third", "third");
        publisher.Complete();

        var canonicalEvents = new List<SimulationEvent>();
        await foreach (var runtimeEvent in publisher.ReadJournalAsync())
        {
            canonicalEvents.Add(runtimeEvent);
        }

        Assert.Equal([1L, 2L, 3L], canonicalEvents.Select(item => item.EventIndex));
        Assert.True(publisher.Reader.TryRead(out var latestPresentationEvent));
        Assert.Equal(3, latestPresentationEvent.EventIndex);
        Assert.True(publisher.JournalSnapshot.IsComplete);
        Assert.Equal(3, publisher.JournalSnapshot.Capacity);
    }

    [Fact]
    public void EventJournalExport_RejectsConfiguredFileBudgetBeforeWriting()
    {
        var journal = new SimulationEventJournalSnapshot(
            Capacity: 1,
            StoredEventCount: 1,
            TotalEventCount: 1,
            FirstEventIndex: 1,
            LastEventIndex: 1,
            IsCompleted: true,
            IsComplete: true,
            FirstMissingEventIndex: null);
        var package = SimulationEventJournalExportPackage.Create(
            journal,
            [new SimulationEvent(1, 0, TimeSpan.Zero, "Test", "First", "first")]);
        var path = Path.Combine(
            TestStorage.RootPath,
            "pl-0237-canonical-event-journal-export",
            "journal-budget-rejected.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationEventJournalExportPackage.SaveToJson(
                package,
                path,
                new SimulationEventJournalExportOptions(MaximumFileBytes: 1)));

        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EventPublisher_RetainsLatestBoundedWindow()
    {
        var publisher = new SimulationEventPublisher(2);

        publisher.TryPublish(1, TimeSpan.Zero, "Test", "First", "first");
        publisher.TryPublish(2, TimeSpan.FromMilliseconds(5), "Test", "Second", "second");
        publisher.TryPublish(3, TimeSpan.FromMilliseconds(10), "Test", "Third", "third");

        Assert.True(publisher.Reader.TryRead(out var first));
        Assert.True(publisher.Reader.TryRead(out var second));
        Assert.Equal(2, first.EventIndex);
        Assert.Equal(3, second.EventIndex);
    }

    [Fact]
    public void LatestSnapshotStore_PublishesCurrentSnapshotAndCompletesReader()
    {
        var first = CreateSnapshot(0);
        var second = CreateSnapshot(1);
        var store = new LatestSnapshotStore(first);

        Assert.Same(first, store.Current);
        store.Publish(second);

        Assert.Same(second, store.Current);
        Assert.True(store.Reader.TryRead(out var queuedSecond));
        Assert.Same(second, queuedSecond);
        Assert.False(store.Reader.TryRead(out _));

        store.Complete();
        Assert.True(store.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void LatestSnapshotStore_RejectsPublicationAfterCompletion()
    {
        var first = CreateSnapshot(0);
        var late = CreateSnapshot(1);
        var store = new LatestSnapshotStore(first);

        store.Complete();
        store.Publish(late);

        Assert.Same(first, store.Current);
        Assert.False(store.Reader.TryRead(out _));
        Assert.True(store.Reader.Completion.IsCompleted);
    }

    private static SimulationSnapshot CreateSnapshot(long tick) =>
        new(
            TimeSpan.FromMilliseconds(tick * 5),
            tick,
            SimulationRunMode.Paused,
            SimulationControlOwner.Definition,
            1,
            Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot>(),
            0,
            Array.Empty<DigitalSignalSnapshot>(),
            Array.Empty<SequenceExecutionSnapshot>());
}
