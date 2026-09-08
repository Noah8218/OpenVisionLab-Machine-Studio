using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Builds the deterministic watch-target list from one immutable runtime snapshot.
/// </summary>
public sealed class RuntimeDebuggerWatchTargetCatalog
{
    public IReadOnlyList<RuntimeWatchTarget> Build(
        SimulationSnapshot snapshot,
        Func<string, string> sequenceNameResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sequenceNameResolver);

        return snapshot.Sequences
            .Select(item => new RuntimeWatchTarget(
                RuntimeWatchKind.Sequence,
                item.SequenceId,
                sequenceNameResolver(item.SequenceId)))
            .Concat(snapshot.Axes.Select(item =>
                new RuntimeWatchTarget(RuntimeWatchKind.Axis, item.Id, item.Name)))
            .Concat(snapshot.Signals.Select(item =>
                new RuntimeWatchTarget(RuntimeWatchKind.Signal, item.Id, item.Name)))
            .Concat(snapshot.LayoutComponents.Select(item =>
                new RuntimeWatchTarget(RuntimeWatchKind.Equipment, item.Id, item.Name)))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
