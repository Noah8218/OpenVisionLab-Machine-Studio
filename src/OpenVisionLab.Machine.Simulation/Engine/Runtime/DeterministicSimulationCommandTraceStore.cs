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
    private readonly int _capacity;
    private readonly List<DeterministicSimulationCommandTraceEntry> _entries = new();
    private long _droppedEntryCount;

    public DeterministicSimulationCommandTraceStore(
        int capacity = SimulationSettings.DefaultCommandTraceEntryCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public int Capacity => _capacity;

    public bool IsComplete
    {
        get
        {
            lock (_sync)
            {
                return _droppedEntryCount == 0;
            }
        }
    }

    public long DroppedEntryCount
    {
        get
        {
            lock (_sync)
            {
                return _droppedEntryCount;
            }
        }
    }

    public ImmutableArray<DeterministicSimulationCommandTraceEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToImmutableArray();
        }
    }

    public DeterministicSimulationCommandTracePackage CreatePackage(TimeSpan fixedStep)
    {
        lock (_sync)
        {
            if (_droppedEntryCount > 0)
            {
                throw new InvalidOperationException(
                    $"The command trace is incomplete; {_droppedEntryCount} entries were dropped.");
            }

            return DeterministicSimulationCommandTracePackage.Create(
                fixedStep,
                _entries.ToImmutableArray());
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _droppedEntryCount = 0;
        }
    }

    public bool Capture(SimulationCommand command, SimulationCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            if (_entries.Count >= _capacity)
            {
                _droppedEntryCount++;
                return false;
            }

            _entries.Add(
                DeterministicSimulationCommandTraceEntry.Capture(
                    _entries.Count + 1,
                    command,
                    result));
            return true;
        }
    }
}
