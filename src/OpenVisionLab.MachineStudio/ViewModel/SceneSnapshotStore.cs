using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Holds the latest immutable simulation snapshot and signals consumers without
/// turning high-frequency scene state into WPF property-change traffic.
/// </summary>
public sealed class SceneSnapshotStore
{
    private SimulationSnapshot? _latest;

    public event EventHandler? SnapshotPublished;

    public SimulationSnapshot? Latest => Volatile.Read(ref _latest);

    public void Publish(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
        SnapshotPublished?.Invoke(this, EventArgs.Empty);
    }
}
