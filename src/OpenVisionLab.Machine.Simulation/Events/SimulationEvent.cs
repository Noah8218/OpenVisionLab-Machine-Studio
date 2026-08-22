namespace OpenVisionLab.Machine.Simulation.Events;

public sealed record SimulationEvent(
    long EventIndex,
    long TickIndex,
    TimeSpan SimulationTime,
    string Category,
    string Code,
    string Message,
    string? CommandId = null);
