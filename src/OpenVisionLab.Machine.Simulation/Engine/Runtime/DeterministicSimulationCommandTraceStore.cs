using System.Collections.Immutable;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Owns the synchronized in-memory command-boundary trace for one engine.
/// </summary>
internal sealed class DeterministicSimulationCommandTraceStore
{
    private readonly object _sync = new();
    private readonly List<DeterministicSimulationCommandTraceEntry> _entries = new();

    public ImmutableArray<DeterministicSimulationCommandTraceEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToImmutableArray();
        }
    }

    public DeterministicSimulationCommandTracePackage CreatePackage(TimeSpan fixedStep) =>
        DeterministicSimulationCommandTracePackage.Create(fixedStep, Snapshot());

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    public void Capture(SimulationCommand command, SimulationCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            _entries.Add(
                DeterministicSimulationCommandTraceEntry.Capture(
                    _entries.Count + 1,
                    command,
                    result));
        }
    }
}
