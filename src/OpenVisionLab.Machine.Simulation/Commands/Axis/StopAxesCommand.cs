namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class StopAxesCommand : SimulationCommand
{
    public IReadOnlyList<string> AxisIds { get; }

    public StopAxesCommand(IEnumerable<string> axisIds)
    {
        ArgumentNullException.ThrowIfNull(axisIds);
        AxisIds = Array.AsReadOnly(axisIds.ToArray());
    }
}
