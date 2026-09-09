using OpenVisionLab.Machine.Simulation.Axis;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class ConfigureAxesCommand : SimulationCommand
{
    public ConfigureAxesCommand(IEnumerable<AxisConfiguration> axes)
    {
        Axes = axes?.ToArray() ?? throw new ArgumentNullException(nameof(axes));
    }

    public IReadOnlyList<AxisConfiguration> Axes { get; }
}
