namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class StartSequenceCommand : SimulationCommand
{
    public StartSequenceCommand(string sequenceId)
    {
        SequenceId = sequenceId;
    }

    public string SequenceId { get; }
}
