using System.Diagnostics;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

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
    private readonly Action<SimulationEngineTerminationResult> _onTerminated;
    private readonly Action<Exception> _onUnhandledException;
    private readonly CancellationTokenSource _cancellation = new();
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
        Action<Exception> onUnhandledException)
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
        _onTerminated = onTerminated ?? throw new ArgumentNullException(nameof(onTerminated));
        _onUnhandledException = onUnhandledException
            ?? throw new ArgumentNullException(nameof(onUnhandledException));
    }

    internal Task RuntimeTask => _runtimeTask
        ?? throw new InvalidOperationException("The simulation runtime loop has not started.");

    internal Task TerminationObservationTask => _terminationObservationTask
        ?? throw new InvalidOperationException("The simulation runtime loop has not started.");

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal bool IsCompleted => _runtimeTask?.IsCompleted == true
        && _terminationObservationTask?.IsCompleted == true;

    internal void Start(SimulationRuntimeConfiguration initialRuntime)
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
            _runtimeTask = StartAndConsumeRuntimeAsync(initialRuntime);
        }
    }

    internal void Cancel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellation.Cancel();
    }

    private async Task StartAndConsumeRuntimeAsync(
        SimulationRuntimeConfiguration initialRuntime)
    {
        try
        {
            await _engine.StartAsync(_cancellation.Token).ConfigureAwait(false);
            var snapshotTask = ConsumeSnapshotsAsync();
            var eventTask = ConsumeEventsAsync();
            var configuration = await _engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(initialRuntime),
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
        await foreach (var runtimeEvent in _engine.EventReader.ReadAllAsync(_cancellation.Token))
        {
            await _dispatch(() => _onEvent(runtimeEvent)).ConfigureAwait(false);
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
    }
}
