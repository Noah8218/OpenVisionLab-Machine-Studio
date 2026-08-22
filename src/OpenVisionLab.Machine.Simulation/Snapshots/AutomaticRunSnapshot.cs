namespace OpenVisionLab.Machine.Simulation.Snapshots;

/// <summary>
/// Immutable, UI-neutral automatic-run lifecycle state.
/// </summary>
public sealed record AutomaticRunSnapshot(
    bool IsConfigured,
    bool IsActive,
    bool IsWaitingForRepeat,
    long CompletedCycleCount,
    int RemainingDelayTicks)
{
    public static AutomaticRunSnapshot NotConfigured { get; } =
        new(false, false, false, 0, 0);
}
