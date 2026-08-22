using OpenVisionLab.Machine.Simulation.Faults;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class InjectSimulationFaultCommand : SimulationCommand
{
    public InjectSimulationFaultCommand(
        SimulationFaultKind kind,
        string targetId,
        bool? forcedValue = null)
    {
        Kind = kind;
        TargetId = targetId;
        ForcedValue = forcedValue;
    }

    public SimulationFaultKind Kind { get; }
    public string TargetId { get; }
    public bool? ForcedValue { get; }
}
