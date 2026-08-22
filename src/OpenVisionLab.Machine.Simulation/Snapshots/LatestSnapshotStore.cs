using System.Threading.Channels;

namespace OpenVisionLab.Machine.Simulation.Snapshots;

public sealed class LatestSnapshotStore
{
    private readonly Channel<SimulationSnapshot> _channel = Channel.CreateBounded<SimulationSnapshot>(
        new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelWriter<SimulationSnapshot> Writer => _channel.Writer;
    public ChannelReader<SimulationSnapshot> Reader => _channel.Reader;

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
