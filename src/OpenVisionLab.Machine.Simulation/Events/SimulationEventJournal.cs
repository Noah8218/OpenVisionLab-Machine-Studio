using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OpenVisionLab.Machine.Simulation.Events;

/// <summary>
/// Describes the canonical event records retained by one simulation session.
/// The journal is finite. An incomplete snapshot is never promoted to complete
/// evidence merely because the UI event window reached its terminal state.
/// </summary>
public sealed record SimulationEventJournalSnapshot(
    int Capacity,
    long StoredEventCount,
    long TotalEventCount,
    long FirstEventIndex,
    long LastEventIndex,
    bool IsCompleted,
    bool IsComplete,
    long? FirstMissingEventIndex);

/// <summary>
/// Optional loss-aware event source implemented by simulation engines that
/// expose the canonical journal separately from the presentation event window.
/// </summary>
public interface ISimulationEventJournalSource
{
    SimulationEventJournalSnapshot EventJournal { get; }

    IAsyncEnumerable<SimulationEvent> ReadCanonicalEventsAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class SimulationEventJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly List<SimulationEvent> _events;
    private readonly SemaphoreSlim _signal = new(0);
    private long _totalEventCount;
    private long _firstEventIndex;
    private long _lastEventIndex;
    private long? _firstMissingEventIndex;
    private bool _completed;
    private bool _disposed;

    internal SimulationEventJournal(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _events = new List<SimulationEvent>(capacity);
    }

    internal SimulationEventJournalSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    internal SimulationEvent? TryAppend(
        long tickIndex,
        TimeSpan simulationTime,
        string category,
        string code,
        string message,
        string? commandId = null)
    {
        lock (_gate)
        {
            if (_disposed || _completed)
            {
                return null;
            }

            var runtimeEvent = new SimulationEvent(
                ++_totalEventCount,
                tickIndex,
                simulationTime,
                category,
                code,
                message,
                commandId);
            _firstEventIndex = _firstEventIndex == 0
                ? runtimeEvent.EventIndex
                : _firstEventIndex;
            _lastEventIndex = runtimeEvent.EventIndex;

            if (_events.Count < _capacity)
            {
                _events.Add(runtimeEvent);
                _signal.Release();
            }
            else if (_firstMissingEventIndex is null)
            {
                _firstMissingEventIndex = runtimeEvent.EventIndex;
            }

            return runtimeEvent;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_disposed || _completed)
            {
                return;
            }

            _completed = true;
            _signal.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _completed = true;
        }

        _signal.Dispose();
    }

    internal async IAsyncEnumerable<SimulationEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var nextEventIndex = 0;
        while (true)
        {
            SimulationEvent? runtimeEvent = null;
            bool completed;
            lock (_gate)
            {
                if (nextEventIndex < _events.Count)
                {
                    runtimeEvent = _events[nextEventIndex++];
                }

                completed = _completed && nextEventIndex >= _events.Count;
            }

            if (runtimeEvent is not null)
            {
                yield return runtimeEvent;
                continue;
            }

            if (completed)
            {
                yield break;
            }

            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SimulationEventJournalSnapshot CreateSnapshot() =>
        new(
            _capacity,
            _events.Count,
            _totalEventCount,
            _firstEventIndex,
            _lastEventIndex,
            _completed,
            _completed && _firstMissingEventIndex is null,
            _firstMissingEventIndex);
}
