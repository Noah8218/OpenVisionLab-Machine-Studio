namespace OpenVisionLab.Machine.Simulation.Commands;

public readonly record struct AxisMoveTarget(string AxisId, double TargetPosition);

public sealed class MoveAxesAbsoluteCommand : SimulationCommand
{
    public IReadOnlyList<AxisMoveTarget> Targets { get; }

    public MoveAxesAbsoluteCommand(IEnumerable<AxisMoveTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Targets = Array.AsReadOnly(targets.ToArray());
    }
}
