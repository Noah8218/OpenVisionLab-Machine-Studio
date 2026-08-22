using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class ConfigureRuntimeCommand : SimulationCommand
{
    public ConfigureRuntimeCommand(SimulationRuntimeConfiguration configuration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public SimulationRuntimeConfiguration Configuration { get; }
}
