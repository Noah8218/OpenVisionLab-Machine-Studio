namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class StopAxisCommand : SimulationCommand
{
    public string AxisId { get; }

    public StopAxisCommand(string axisId)
    {
        AxisId = axisId;
    }
}
