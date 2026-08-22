namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class HomeAxisCommand : SimulationCommand
{
    public HomeAxisCommand(string axisId)
    {
        AxisId = axisId;
    }

    public string AxisId { get; }
}
