namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetVirtualInputCommand : SimulationCommand
{
    public SetVirtualInputCommand(string channelId, bool value)
    {
        ChannelId = channelId;
        Value = value;
    }

    public string ChannelId { get; }
    public bool Value { get; }
}
