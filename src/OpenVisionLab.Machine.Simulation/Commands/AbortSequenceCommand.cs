namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>
/// Terminates the active sequence without resetting the authored runtime.
/// Reset is required before the sequence can be started again.
/// </summary>
public sealed class AbortSequenceCommand : SimulationCommand
{
    public AbortSequenceCommand(string sequenceId)
    {
        SequenceId = sequenceId;
    }

    public string SequenceId { get; }
}
