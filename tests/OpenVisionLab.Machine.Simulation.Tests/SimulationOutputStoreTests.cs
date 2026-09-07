using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
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
