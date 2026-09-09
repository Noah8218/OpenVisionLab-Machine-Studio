namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class StepSequenceCommand : SimulationCommand
{
    public StepSequenceCommand(string sequenceId)
    {
        SequenceId = sequenceId;
    }

    public string SequenceId { get; }
}
