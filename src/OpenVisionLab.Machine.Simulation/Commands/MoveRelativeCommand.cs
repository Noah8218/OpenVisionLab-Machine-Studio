namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class MoveRelativeCommand : SimulationCommand
{
    public string AxisId { get; }
    public double Distance { get; }

    public MoveRelativeCommand(string axisId, double distance)
    {
        AxisId = axisId;
        Distance = distance;
    }
}
