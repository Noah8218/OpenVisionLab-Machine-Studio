namespace OpenVisionLab.Machine.Simulation.Faults;

using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Snapshots;

public enum SimulationFaultKind
{
    StuckDigitalInput,
    CylinderTravelBlocked,
    AxisMotionBlocked,
    AxisFollowingError
}

public sealed record SimulationFaultSnapshot(
    SimulationFaultKind Kind,
    string TargetId,
    bool? ForcedValue,
    long ActivatedTick,
    TimeSpan ActivatedTime);

public sealed record SimulationFaultTarget(
    SimulationFaultKind Kind,
    string Id,
    string Name)
{
    public string DisplayName => string.Equals(Id, Name, StringComparison.Ordinal)
        ? Id
        : $"{Name} · {Id}";
}

/// <summary>
/// Builds the operator-selectable fault targets from one immutable runtime snapshot.
/// The catalog never consults authored project state, so the choices always match
/// the runtime that will receive the command.
/// </summary>
public sealed class SimulationFaultTargetCatalog
{
    public IReadOnlyList<SimulationFaultTarget> GetTargets(
        SimulationSnapshot snapshot,
        SimulationFaultKind kind)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return kind switch
        {
            SimulationFaultKind.StuckDigitalInput => snapshot.Signals
                .Where(signal => signal.Kind == ChannelKind.DigitalInput)
                .Select(signal => new SimulationFaultTarget(kind, signal.Id, signal.Name))
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray(),
            SimulationFaultKind.CylinderTravelBlocked => snapshot.LayoutComponents
                .Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder)
                .Select(component => new SimulationFaultTarget(kind, component.Id, component.Name))
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray(),
            SimulationFaultKind.AxisMotionBlocked => snapshot.Axes
                .Select(axis => new SimulationFaultTarget(kind, axis.Id, axis.Name))
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray(),
            SimulationFaultKind.AxisFollowingError => snapshot.Axes
                .Select(axis => new SimulationFaultTarget(kind, axis.Id, axis.Name))
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ToArray(),
            _ => Array.Empty<SimulationFaultTarget>()
        };
    }
}

internal readonly record struct SimulationFaultKey(
    SimulationFaultKind Kind,
    string TargetId);
