namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetCylinderCommand : SimulationCommand
{
    public SetCylinderCommand(string cylinderId, bool extend)
    {
        CylinderId = cylinderId;
        Extend = extend;
    }

    public string CylinderId { get; }
    public bool Extend { get; }
}
