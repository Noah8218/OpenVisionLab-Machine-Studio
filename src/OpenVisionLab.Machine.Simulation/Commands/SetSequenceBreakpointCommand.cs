namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetSequenceBreakpointCommand : SimulationCommand
{
    public SetSequenceBreakpointCommand(string sequenceId, string stepId, bool isEnabled)
    {
        SequenceId = sequenceId;
        StepId = stepId;
        IsEnabled = isEnabled;
    }

    public string SequenceId { get; }

    public string StepId { get; }

    public bool IsEnabled { get; }
}
