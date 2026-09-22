using System.Threading.Channels;

namespace OpenVisionLab.Machine.Simulation.Snapshots;

public sealed class LatestSnapshotStore
{
    private readonly object _gate = new();
    private readonly Channel<SimulationSnapshot> _channel = Channel.CreateBounded<SimulationSnapshot>(
        new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    private SimulationSnapshot? _latest;
    private bool _completed;

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
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            SetCurrent(snapshot);
            _channel.Writer.TryWrite(snapshot);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _channel.Writer.TryComplete();
        }
    }
}
