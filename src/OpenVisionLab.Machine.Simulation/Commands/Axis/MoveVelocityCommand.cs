namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class MoveVelocityCommand : SimulationCommand
{
    public string AxisId { get; }
    public double Velocity { get; }

    public MoveVelocityCommand(string axisId, double velocity)
    {
        AxisId = axisId;
        Velocity = velocity;
    }
}
