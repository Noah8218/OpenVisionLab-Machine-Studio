using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class ConfigureRuntimeCommand : SimulationCommand
{
    public ConfigureRuntimeCommand(
        SimulationRuntimeConfiguration configuration,
        string? projectId = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
    }

    public SimulationRuntimeConfiguration Configuration { get; }
    public string? ProjectId { get; }
}
