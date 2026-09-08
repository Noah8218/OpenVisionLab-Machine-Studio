using System.Threading.Channels;

namespace OpenVisionLab.Machine.Simulation.Events;

internal sealed class SimulationEventPublisher : IDisposable
{
    private readonly Channel<SimulationEvent> _channel;
    private readonly SimulationEventJournal _journal;

    internal SimulationEventPublisher(int capacity)
        : this(capacity, capacity)
    {
    }

    internal SimulationEventPublisher(int presentationCapacity, int canonicalCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presentationCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canonicalCapacity);
        _journal = new SimulationEventJournal(canonicalCapacity);
        _channel = Channel.CreateBounded<SimulationEvent>(
            new BoundedChannelOptions(presentationCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
    }

    internal ChannelReader<SimulationEvent> Reader => _channel.Reader;

    internal SimulationEventJournalSnapshot JournalSnapshot => _journal.Snapshot;

    internal IAsyncEnumerable<SimulationEvent> ReadJournalAsync(
        CancellationToken cancellationToken = default) =>
        _journal.ReadAllAsync(cancellationToken);

    internal bool TryPublish(
        long tickIndex,
        TimeSpan simulationTime,
        string category,
        string code,
        string message,
        string? commandId = null)
    {
        var runtimeEvent = _journal.TryAppend(
            tickIndex,
            simulationTime,
            category,
            code,
            message,
            commandId);
        return runtimeEvent is not null && _channel.Writer.TryWrite(runtimeEvent);
    }

    internal void Complete()
    {
        _journal.Complete();
        _channel.Writer.TryComplete();
    }

    public void Dispose() => _journal.Dispose();
}
