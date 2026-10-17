namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetManualOutputCommand(string channelId, bool value) : SimulationCommand
{
    public string ChannelId { get; } = channelId;
    public bool Value { get; } = value;
}
