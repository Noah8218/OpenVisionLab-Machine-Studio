namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>
/// Retries the active faulted sequence from its authored entry step.
/// Equipment faults must be cleared first; automatic continuation remains stopped.
/// </summary>
public sealed class RetrySequenceCommand : SimulationCommand
{
    public RetrySequenceCommand(string sequenceId)
    {
        SequenceId = sequenceId;
    }

    public string SequenceId { get; }
}
