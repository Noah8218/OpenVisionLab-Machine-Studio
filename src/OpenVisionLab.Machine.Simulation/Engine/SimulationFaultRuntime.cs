using System.Collections;
using OpenVisionLab.Machine.Simulation.Faults;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Owns the active Simulation fault records while exposing the existing
/// read-only dictionary view to command policies that only inspect faults.
/// </summary>
internal sealed class SimulationFaultRuntime : IReadOnlyDictionary<SimulationFaultKey, SimulationFaultSnapshot>
{
    private readonly Dictionary<SimulationFaultKey, SimulationFaultSnapshot> _activeFaults = new();

    public int Count => _activeFaults.Count;
    public IEnumerable<SimulationFaultKey> Keys => _activeFaults.Keys;
    public IEnumerable<SimulationFaultSnapshot> Values => _activeFaults.Values;
    public SimulationFaultSnapshot this[SimulationFaultKey key] => _activeFaults[key];

    public bool ContainsKey(SimulationFaultKey key) => _activeFaults.ContainsKey(key);

    public bool TryGetValue(SimulationFaultKey key, out SimulationFaultSnapshot value)
    {
        if (_activeFaults.TryGetValue(key, out var fault))
        {
            value = fault;
            return true;
        }

        value = null!;
        return false;
    }

    internal void Add(SimulationFaultSnapshot fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        _activeFaults.Add(new SimulationFaultKey(fault.Kind, fault.TargetId), fault);
    }

    internal bool Remove(SimulationFaultKey key) => _activeFaults.Remove(key);

    internal void Clear() => _activeFaults.Clear();

    public IEnumerator<KeyValuePair<SimulationFaultKey, SimulationFaultSnapshot>> GetEnumerator() =>
        _activeFaults.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
