using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSimulationCommandTraceStoreTests
{
    [Fact]
    public void Capture_IsThreadSafeAndClearRemovesOnlyOwnedEntries()
    {
        var store = new DeterministicSimulationCommandTraceStore();
        Assert.Equal(0, store.Count);

        Parallel.For(0, 64, _ =>
        {
            var command = new StepCommand();
            store.Capture(
                command,
                new SimulationCommandResult(
                    command.CommandId,
                    true,
                    0,
                    TimeSpan.Zero,
                    SimulationCommandErrorCode.None,
                    "accepted"));
        });

        var entries = store.Snapshot();
        Assert.Equal(64, entries.Length);
        Assert.Equal(entries.Length, store.Count);
        Assert.Equal(Enumerable.Range(1, 64), entries.Select(entry => entry.Sequence));

        var package = store.CreatePackage(TimeSpan.FromMilliseconds(5));
        Assert.True(package.HasValidTraceHash());
        Assert.Equal(entries.Length, package.Entries.Length);
        Assert.Equal(
            entries.Select(entry => entry.Sequence),
            package.Entries.Select(entry => entry.Sequence));

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Capture_StopsAtCapacityAndMarksTheTraceIncompleteUntilClear()
    {
        var store = new DeterministicSimulationCommandTraceStore(capacity: 2);

        Assert.True(Capture(store));
        Assert.True(Capture(store));
        Assert.False(Capture(store));

        Assert.Equal(2, store.Count);
        Assert.Equal(1, store.DroppedEntryCount);
        Assert.False(store.IsComplete);
        Assert.Throws<InvalidOperationException>(() => store.CreatePackage(TimeSpan.FromMilliseconds(5)));

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.DroppedEntryCount);
        Assert.True(store.IsComplete);
        Assert.True(Capture(store));
        Assert.Equal(1, store.Snapshot().Single().Sequence);

        static bool Capture(DeterministicSimulationCommandTraceStore store)
        {
            var command = new StepCommand();
            return store.Capture(
                command,
                new SimulationCommandResult(
                    command.CommandId,
                    true,
                    0,
                    TimeSpan.Zero,
                    SimulationCommandErrorCode.None,
                    "accepted"));
        }
    }
}
