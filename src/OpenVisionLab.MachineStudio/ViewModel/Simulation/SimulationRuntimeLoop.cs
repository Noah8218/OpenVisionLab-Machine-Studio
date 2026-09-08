using System.Diagnostics;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SimulationRuntimeEventConsumptionSnapshot(
    bool IsAvailable,
    bool IsCompleted,
    long ConsumedEventCount,
    long LastConsumedEventIndex);

/// <summary>
/// Owns the background simulation readers and their cancellation lifetime.
/// MainViewModel remains the owner of the projected UI state and shutdown policy.
/// </summary>
internal sealed class SimulationRuntimeLoop : IDisposable
{
    private static readonly TimeSpan MonitorRefreshInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly ISimulationEngine _engine;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action<SimulationSnapshot> _publishSnapshot;
    private readonly Action<SimulationSnapshot> _applySnapshot;
    private readonly Action _onInitialRuntimeApplied;
    private readonly Action<string> _onInitialConfigurationRejected;
    private readonly Action<SimulationEvent> _onEvent;
    private readonly Action<SimulationEvent>? _onCanonicalEvent;
    private readonly Action<SimulationEventJournalSnapshot>? _onCanonicalJournalCompleted;
    private readonly Action<SimulationEngineTerminationResult> _onTerminated;
    private readonly Action<Exception> _onUnhandledException;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _canonicalCancellation = new();
    private bool _canonicalConsumptionAvailable;
    private bool _canonicalConsumptionCompleted;
    private long _canonicalConsumedEventCount;
    private long _canonicalLastConsumedEventIndex;
    private Task? _runtimeTask;
    private Task? _terminationObservationTask;
    private bool _disposed;

    internal SimulationRuntimeLoop(
        ISimulationEngine engine,
        Func<Action, Task> dispatch,
        Action<SimulationSnapshot> publishSnapshot,
        Action<SimulationSnapshot> applySnapshot,
        Action onInitialRuntimeApplied,
        Action<string> onInitialConfigurationRejected,
        Action<SimulationEvent> onEvent,
        Action<SimulationEngineTerminationResult> onTerminated,
        Action<Exception> onUnhandledException,
        Action<SimulationEvent>? onCanonicalEvent = null,
        Action<SimulationEventJournalSnapshot>? onCanonicalJournalCompleted = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _publishSnapshot = publishSnapshot ?? throw new ArgumentNullException(nameof(publishSnapshot));
        _applySnapshot = applySnapshot ?? throw new ArgumentNullException(nameof(applySnapshot));
        _onInitialRuntimeApplied = onInitialRuntimeApplied
            ?? throw new ArgumentNullException(nameof(onInitialRuntimeApplied));
        _onInitialConfigurationRejected = onInitialConfigurationRejected
            ?? throw new ArgumentNullException(nameof(onInitialConfigurationRejected));
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _onCanonicalEvent = onCanonicalEvent;
        _onCanonicalJournalCompleted = onCanonicalJournalCompleted;
        _canonicalConsumptionAvailable = engine is ISimulationEventJournalSource
            && (onCanonicalEvent is not null || onCanonicalJournalCompleted is not null);
        _onTerminated = onTerminated ?? throw new ArgumentNullException(nameof(onTerminated));
        _onUnhandledException = onUnhandledException
            ?? throw new ArgumentNullException(nameof(onUnhandledException));
    }

    internal Task RuntimeTask => _runtimeTask
        ?? throw new InvalidOperationException("The simulation runtime loop has not started.");

    internal Task TerminationObservationTask => _terminationObservationTask
        ?? throw new InvalidOperationException("The simulation runtime loop has not started.");

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal SimulationRuntimeEventConsumptionSnapshot CanonicalEventConsumption
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _canonicalConsumptionAvailable,
                    _canonicalConsumptionCompleted,
                    _canonicalConsumedEventCount,
                    _canonicalLastConsumedEventIndex);
            }
        }
    }

    internal bool IsCompleted => _runtimeTask?.IsCompleted == true
        && _terminationObservationTask?.IsCompleted == true;

    internal void Start(SimulationRuntimeConfiguration initialRuntime, string? projectId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(initialRuntime);

        lock (_gate)
        {
            if (_runtimeTask is not null)
            {
                throw new InvalidOperationException("The simulation runtime loop can start only once.");
            }

            _terminationObservationTask = ObserveEngineTerminationAsync();
            _runtimeTask = StartAndConsumeRuntimeAsync(initialRuntime, projectId);
        }
    }

    internal void Cancel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellation.Cancel();
    }

    private async Task StartAndConsumeRuntimeAsync(
        SimulationRuntimeConfiguration initialRuntime,
        string? projectId)
    {
        Task? snapshotTask = null;
        Task? eventTask = null;
        try
        {
            await _engine.StartAsync(_cancellation.Token).ConfigureAwait(false);
            snapshotTask = ConsumeSnapshotsAsync();
            eventTask = ConsumeEventsAsync();
            var configuration = await _engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(initialRuntime, projectId),
                _cancellation.Token).ConfigureAwait(false);
            if (!configuration.IsAccepted)
            {
                await _dispatch(() =>
                        _onInitialConfigurationRejected(configuration.Detail ?? string.Empty))
                    .ConfigureAwait(false);
            }
            else
            {
                await _dispatch(_onInitialRuntimeApplied).ConfigureAwait(false);
            }

        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _dispatch(() => _onUnhandledException(exception)).ConfigureAwait(false);
        }
        finally
        {
            if (snapshotTask is not null && eventTask is not null)
            {
                try
                {
                    await Task.WhenAll(snapshotTask, eventTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    await _dispatch(() => _onUnhandledException(exception)).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ObserveEngineTerminationAsync()
    {
        var termination = await _engine.Termination.ConfigureAwait(false);
        _onTerminated(termination);
    }

    private async Task ConsumeSnapshotsAsync()
    {
        var monitorStopwatch = Stopwatch.StartNew();
        await foreach (var snapshot in _engine.SnapshotReader.ReadAllAsync(_cancellation.Token))
        {
            _publishSnapshot(snapshot);
            if (snapshot.RunMode == SimulationRunMode.RealTime
                && monitorStopwatch.Elapsed < MonitorRefreshInterval)
            {
                continue;
            }

            monitorStopwatch.Restart();
            await _dispatch(() => _applySnapshot(snapshot)).ConfigureAwait(false);
        }
    }

    private async Task ConsumeEventsAsync()
    {
        if (_engine is ISimulationEventJournalSource journalSource
            && (_onCanonicalEvent is not null || _onCanonicalJournalCompleted is not null))
        {
            await Task.WhenAll(
                ConsumePresentationEventsAsync(),
                ConsumeCanonicalEventsAsync(journalSource)).ConfigureAwait(false);
            return;
        }

        await ConsumePresentationEventsAsync().ConfigureAwait(false);
    }

    private async Task ConsumePresentationEventsAsync()
    {
        await foreach (var runtimeEvent in _engine.EventReader.ReadAllAsync(_cancellation.Token))
        {
            await _dispatch(() => _onEvent(runtimeEvent)).ConfigureAwait(false);
            if (_engine is not ISimulationEventJournalSource && _onCanonicalEvent is not null)
            {
                await _dispatch(() => _onCanonicalEvent(runtimeEvent)).ConfigureAwait(false);
            }
        }
    }

    private async Task ConsumeCanonicalEventsAsync(ISimulationEventJournalSource journalSource)
    {
        await foreach (var runtimeEvent in journalSource.ReadCanonicalEventsAsync(_canonicalCancellation.Token))
        {
            if (_onCanonicalEvent is not null)
            {
                await _dispatch(() => _onCanonicalEvent(runtimeEvent)).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _canonicalConsumedEventCount++;
                _canonicalLastConsumedEventIndex = runtimeEvent.EventIndex;
            }
        }

        if (_onCanonicalJournalCompleted is not null)
        {
            var journal = journalSource.EventJournal;
            await _dispatch(() => _onCanonicalJournalCompleted(journal)).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _canonicalConsumptionCompleted = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!IsCompleted)
        {
            throw new InvalidOperationException(
                "The simulation runtime loop must be stopped before disposal.");
        }

        _disposed = true;
        _cancellation.Dispose();
        _canonicalCancellation.Dispose();
    }
}
