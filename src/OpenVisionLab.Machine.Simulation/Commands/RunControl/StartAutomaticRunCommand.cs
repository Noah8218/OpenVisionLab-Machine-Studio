namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>
/// Atomically applies the configured start input, starts its sequence, and
/// optionally switches the simulation to real-time automatic operation.
/// </summary>
public sealed class StartAutomaticRunCommand : SimulationCommand
{
    public StartAutomaticRunCommand(bool beginRealTime = true)
    {
        BeginRealTime = beginRealTime;
    }

    public bool BeginRealTime { get; }
}
