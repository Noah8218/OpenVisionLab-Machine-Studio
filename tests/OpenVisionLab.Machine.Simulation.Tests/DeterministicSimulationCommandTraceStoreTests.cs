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
        Assert.Equal(Enumerable.Range(1, 64), entries.Select(entry => entry.Sequence));

        var package = store.CreatePackage(TimeSpan.FromMilliseconds(5));
        Assert.True(package.HasValidTraceHash());
        Assert.Equal(entries.Length, package.Entries.Length);
        Assert.Equal(
            entries.Select(entry => entry.Sequence),
            package.Entries.Select(entry => entry.Sequence));

        store.Clear();

        Assert.Empty(store.Snapshot());
    }
}
