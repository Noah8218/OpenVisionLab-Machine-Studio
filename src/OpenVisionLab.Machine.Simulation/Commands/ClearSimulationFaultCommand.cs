using OpenVisionLab.Machine.Simulation.Faults;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class ClearSimulationFaultCommand : SimulationCommand
{
    public ClearSimulationFaultCommand(SimulationFaultKind kind, string targetId)
    {
        Kind = kind;
        TargetId = targetId;
    }

    public SimulationFaultKind Kind { get; }
    public string TargetId { get; }
}
