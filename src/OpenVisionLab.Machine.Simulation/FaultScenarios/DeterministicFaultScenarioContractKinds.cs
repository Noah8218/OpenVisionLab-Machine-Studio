using OpenVisionLab.Machine.Simulation.Faults;

namespace OpenVisionLab.Machine.Simulation.FaultScenarios;

public enum DeterministicFaultScenarioFaultKind
{
    StuckDigitalInput,
    CylinderTravelBlocked
}

internal static class DeterministicFaultScenarioFaultKindExtensions
{
    public static bool IsSupported(this DeterministicFaultScenarioFaultKind kind) =>
        kind is
            DeterministicFaultScenarioFaultKind.StuckDigitalInput or
            DeterministicFaultScenarioFaultKind.CylinderTravelBlocked;

    public static SimulationFaultKind ToSimulationFaultKind(this DeterministicFaultScenarioFaultKind kind) =>
        kind switch
        {
            DeterministicFaultScenarioFaultKind.StuckDigitalInput => SimulationFaultKind.StuckDigitalInput,
            DeterministicFaultScenarioFaultKind.CylinderTravelBlocked => SimulationFaultKind.CylinderTravelBlocked,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported fault kind.")
        };

    public static string DisplayName(this DeterministicFaultScenarioFaultKind kind) =>
        kind switch
        {
            DeterministicFaultScenarioFaultKind.StuckDigitalInput => "Stuck digital input",
            DeterministicFaultScenarioFaultKind.CylinderTravelBlocked => "Cylinder travel blocked",
            _ => string.Empty
        };
}
