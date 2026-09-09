using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class StartConditionScenarioCommand : SimulationCommand
{
    public StartConditionScenarioCommand(DeterministicConditionScenarioProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public DeterministicConditionScenarioProfile Profile { get; }
}
