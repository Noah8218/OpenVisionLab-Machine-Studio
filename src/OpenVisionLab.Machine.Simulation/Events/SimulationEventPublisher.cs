using System.Threading.Channels;

namespace OpenVisionLab.Machine.Simulation.Events;

internal sealed class SimulationEventPublisher
{
    private readonly Channel<SimulationEvent> _channel;
    private long _eventIndex;

    internal SimulationEventPublisher(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<SimulationEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
    }

    internal ChannelReader<SimulationEvent> Reader => _channel.Reader;

    internal bool TryPublish(
        long tickIndex,
        TimeSpan simulationTime,
        string category,
        string code,
        string message,
        string? commandId = null) =>
        _channel.Writer.TryWrite(new SimulationEvent(
            ++_eventIndex,
            tickIndex,
            simulationTime,
            category,
            code,
            message,
            commandId));

    internal void Complete() => _channel.Writer.TryComplete();
}
