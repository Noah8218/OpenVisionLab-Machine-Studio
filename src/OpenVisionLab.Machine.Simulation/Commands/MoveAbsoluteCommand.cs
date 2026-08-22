namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class MoveAbsoluteCommand : SimulationCommand
{
    public string AxisId { get; }
    public double TargetPosition { get; }

    public MoveAbsoluteCommand(string axisId, double targetPosition)
    {
        AxisId = axisId;
        TargetPosition = targetPosition;
    }
}
