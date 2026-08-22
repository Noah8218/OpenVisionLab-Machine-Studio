namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetVirtualInputForceCommand : SimulationCommand
{
    public SetVirtualInputForceCommand(string channelId, bool? forcedValue)
    {
        ChannelId = channelId;
        ForcedValue = forcedValue;
    }

    public string ChannelId { get; }
    public bool? ForcedValue { get; }
}
