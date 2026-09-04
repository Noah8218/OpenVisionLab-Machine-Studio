namespace OpenVisionLab.Machine.Simulation.Engine;

public enum SimulationEngineTerminationOutcome
{
    Normal,
    Cancelled,
    Stopped,
    Faulted
}

public sealed record SimulationEngineTerminationResult(
    SimulationEngineTerminationOutcome Outcome,
    long TickIndex,
    TimeSpan SimulationTime,
    Exception? Exception = null,
    string? CurrentCommandId = null,
    string? Operation = null)
{
    public bool IsFaulted => Outcome == SimulationEngineTerminationOutcome.Faulted;
}
