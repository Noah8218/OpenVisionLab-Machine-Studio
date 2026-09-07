using System.Threading.Channels;

namespace OpenVisionLab.Machine.Simulation.Snapshots;

public sealed class LatestSnapshotStore
{
    private readonly Channel<SimulationSnapshot> _channel = Channel.CreateBounded<SimulationSnapshot>(
        new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    private SimulationSnapshot? _latest;

    public LatestSnapshotStore()
    {
    }

    internal LatestSnapshotStore(SimulationSnapshot initialSnapshot)
    {
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        _latest = initialSnapshot;
    }

    public ChannelWriter<SimulationSnapshot> Writer => _channel.Writer;
    public ChannelReader<SimulationSnapshot> Reader => _channel.Reader;

    internal SimulationSnapshot Current => Volatile.Read(ref _latest)
        ?? throw new InvalidOperationException("No snapshot has been published.");

    internal void SetCurrent(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
    }

    internal void Publish(SimulationSnapshot snapshot)
    {
        SetCurrent(snapshot);
        _channel.Writer.TryWrite(snapshot);
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
