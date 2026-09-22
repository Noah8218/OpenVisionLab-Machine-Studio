namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>
/// Atomically applies the configured start input, starts its sequence, and
/// optionally switches the simulation to real-time automatic operation.
/// </summary>
public sealed class StartAutomaticRunCommand : SimulationCommand
{
    public StartAutomaticRunCommand(
        bool beginRealTime = true,
        bool waitForExternalResult = false)
    {
        BeginRealTime = beginRealTime;
        WaitForExternalResult = waitForExternalResult;
    }

    public bool BeginRealTime { get; }

    public bool WaitForExternalResult { get; }
}
