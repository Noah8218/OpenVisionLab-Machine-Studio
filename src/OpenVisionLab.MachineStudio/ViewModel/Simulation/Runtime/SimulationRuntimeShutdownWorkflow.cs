using System.Diagnostics;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.MachineStudio.Models.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SimulationRuntimeShutdownDiagnostic(
    SimulationOperationalDiagnosticKind Kind,
    SimulationLogSeverity Severity,
    string Message,
    string Stage,
    SimulationEngineTerminationResult? Termination = null,
    Exception? Exception = null);

/// <summary>
/// Owns the application-independent runtime shutdown transaction. The shell
/// remains responsible for converting the typed diagnostic records into its
/// public observability projection.
/// </summary>
internal sealed class SimulationRuntimeShutdownWorkflow
{
    private readonly ISimulationEngine _engine;
    private readonly SimulationRuntimeLoop _runtimeLoop;
    private readonly SimulationRuntimeResourceOwner _runtimeResources;
    private readonly SimulationRunControlWorkflow _simulationRunControlWorkflow;
    private readonly Action<SimulationRuntimeShutdownDiagnostic> _recordDiagnostic;
    private readonly Func<Action, Task> _dispatch;
    private readonly Func<Action, Task> _dispatchAfterDispose;
    private readonly BoundedShutdownCoordinator _shutdownCoordinator = new();
    private int _shutdownRequested;

    internal SimulationRuntimeShutdownWorkflow(
        ISimulationEngine engine,
        SimulationRuntimeLoop runtimeLoop,
        SimulationRuntimeResourceOwner runtimeResources,
        SimulationRunControlWorkflow simulationRunControlWorkflow,
        Action<SimulationRuntimeShutdownDiagnostic> recordDiagnostic,
        Func<Action, Task> dispatch,
        Func<Action, Task>? dispatchAfterDispose = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runtimeLoop = runtimeLoop ?? throw new ArgumentNullException(nameof(runtimeLoop));
        _runtimeResources = runtimeResources
            ?? throw new ArgumentNullException(nameof(runtimeResources));
        _simulationRunControlWorkflow = simulationRunControlWorkflow
            ?? throw new ArgumentNullException(nameof(simulationRunControlWorkflow));
        _recordDiagnostic = recordDiagnostic
            ?? throw new ArgumentNullException(nameof(recordDiagnostic));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _dispatchAfterDispose = dispatchAfterDispose ?? dispatch;
    }

    internal bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    internal Task<RuntimeShutdownResult> ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        return _shutdownCoordinator.ShutdownAsync(
            timeout,
            ShutdownRuntimeAsync,
            cancellationToken);
    }

    internal void CompleteDisposeAfterShutdown(
        Task<RuntimeShutdownResult> shutdownTask,
        Action? completeApplicationDispose = null)
    {
        ArgumentNullException.ThrowIfNull(shutdownTask);
        if (shutdownTask.IsCompleted)
        {
            _ = CompleteDisposeAfterShutdownAsync(shutdownTask, completeApplicationDispose);
            return;
        }

        _ = CompleteDisposeAfterShutdownAsync(shutdownTask, completeApplicationDispose);
    }

    private async Task<RuntimeShutdownResult> ShutdownRuntimeAsync(CancellationToken deadline)
    {
        var stopwatch = Stopwatch.StartNew();
        var stage = "RequestCancellation";
        SimulationEngineTerminationResult? termination = null;
        try
        {
            await RecordDiagnosticAsync(
                SimulationOperationalDiagnosticKind.ShutdownRequested,
                SimulationLogSeverity.Info,
                "Machine Studio runtime shutdown requested.",
                stage).ConfigureAwait(false);
            _simulationRunControlWorkflow.Dispose();
            stage = "EngineStop";
            var stopTask = _engine.StopAsync(CancellationToken.None);
            _runtimeResources.RequestCancellation();
            await BoundedShutdownCoordinator.AwaitStageAsync(stopTask, stage, deadline);

            stage = "EngineTermination";
            termination = await BoundedShutdownCoordinator.AwaitStageAsync(
                _engine.Termination,
                stage,
                deadline);

            stage = "TerminationObserver";
            await BoundedShutdownCoordinator.AwaitStageAsync(
                _runtimeLoop.TerminationObservationTask,
                stage,
                deadline);

            stage = "RuntimeTask";
            await BoundedShutdownCoordinator.AwaitStageAsync(
                _runtimeLoop.RuntimeTask,
                stage,
                deadline);

            stage = "RunControl";
            await BoundedShutdownCoordinator.AwaitStageAsync(
                _simulationRunControlWorkflow.WaitForOperationsAsync(),
                stage,
                deadline);

            stage = "ScenarioBatch";
            if (_runtimeResources.ScenarioBatchTask is { } batchTask)
            {
                await BoundedShutdownCoordinator.AwaitStageAsync(batchTask, stage, deadline);
            }

            stage = "CommissioningValidation";
            if (_runtimeResources.CommissioningValidationTask is { } commissioningTask)
            {
                await BoundedShutdownCoordinator.AwaitStageAsync(
                    commissioningTask,
                    stage,
                    deadline);
            }

            stage = "EventJournal";
            var eventJournalSource = _engine as ISimulationEventJournalSource;
            var eventJournal = eventJournalSource?.EventJournal;
            var eventConsumption = _runtimeLoop.CanonicalEventConsumption;
            var canonicalRecordsRequireConsumption = eventJournal is { StoredEventCount: > 0 };
            var journalIncomplete = eventJournal is not null
                && (!eventJournal.IsComplete
                    || (canonicalRecordsRequireConsumption
                        && (!eventConsumption.IsAvailable
                            || !eventConsumption.IsCompleted
                            || eventConsumption.ConsumedEventCount != eventJournal.StoredEventCount
                            || eventConsumption.LastConsumedEventIndex != eventJournal.LastEventIndex)));
            stage = "ResourceDispose";
            await _dispatch(() => _runtimeResources.TryDisposeIfSafe())
                .ConfigureAwait(false);
            var outcome = termination.IsFaulted
                ? RuntimeShutdownOutcome.Faulted
                : journalIncomplete
                    ? RuntimeShutdownOutcome.Incomplete
                    : RuntimeShutdownOutcome.Completed;
            var message = outcome switch
            {
                RuntimeShutdownOutcome.Faulted =>
                    "Machine Studio runtime shutdown completed after an engine fault.",
                RuntimeShutdownOutcome.Incomplete =>
                    $"Machine Studio runtime shutdown completed with incomplete canonical " +
                    $"event evidence ({eventJournal!.StoredEventCount}/{eventJournal.TotalEventCount} " +
                    $"records; first missing index {eventJournal.FirstMissingEventIndex}).",
                _ => "Machine Studio runtime shutdown completed."
            };
            var diagnosticKind = outcome switch
            {
                RuntimeShutdownOutcome.Faulted => SimulationOperationalDiagnosticKind.ShutdownFaulted,
                RuntimeShutdownOutcome.Incomplete => SimulationOperationalDiagnosticKind.ShutdownIncomplete,
                _ => SimulationOperationalDiagnosticKind.ShutdownCompleted
            };
            var diagnosticSeverity = outcome == RuntimeShutdownOutcome.Completed
                ? SimulationLogSeverity.Info
                : SimulationLogSeverity.Alarm;
            var resultStage = journalIncomplete ? "EventJournal" : stage;
            await RecordDiagnosticAsync(
                diagnosticKind,
                diagnosticSeverity,
                message,
                resultStage,
                termination,
                termination.Exception).ConfigureAwait(false);
            return new(
                outcome,
                stopwatch.Elapsed,
                resultStage,
                termination,
                termination.Exception,
                eventJournal,
                eventConsumption);
        }
        catch (RuntimeShutdownTimeoutException exception)
        {
            await RecordDiagnosticAsync(
                SimulationOperationalDiagnosticKind.ShutdownTimedOut,
                SimulationLogSeverity.Alarm,
                $"Machine Studio runtime shutdown timed out during {exception.Stage}.",
                exception.Stage,
                termination,
                exception).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var exception = new RuntimeShutdownTimeoutException(stage);
            await RecordDiagnosticAsync(
                SimulationOperationalDiagnosticKind.ShutdownTimedOut,
                SimulationLogSeverity.Alarm,
                $"Machine Studio runtime shutdown timed out during {stage}.",
                stage,
                termination,
                exception).ConfigureAwait(false);
            throw exception;
        }
        catch (Exception exception)
        {
            await RecordDiagnosticAsync(
                SimulationOperationalDiagnosticKind.ShutdownFaulted,
                SimulationLogSeverity.Alarm,
                $"Machine Studio runtime shutdown failed during {stage}: {exception.Message}",
                stage,
                termination,
                exception).ConfigureAwait(false);
            return new(RuntimeShutdownOutcome.Faulted, stopwatch.Elapsed, stage, termination, exception);
        }
    }

    private async Task CompleteDisposeAfterShutdownAsync(
        Task<RuntimeShutdownResult> shutdownTask,
        Action? completeApplicationDispose)
    {
        try
        {
            await shutdownTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }

        bool runtimeDisposed;
        try
        {
            runtimeDisposed = await TryCompleteRuntimeDisposeIfSafeAsync()
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            return;
        }

        if (!runtimeDisposed)
        {
            return;
        }

        if (completeApplicationDispose is null)
        {
            return;
        }

        try
        {
            // The shutdown task may complete on a worker continuation; the shell
            // callback owns WPF-bound ViewModel disposal and must use the existing
            // composition dispatch boundary.
            await _dispatch(completeApplicationDispose)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private async Task<bool> TryCompleteRuntimeDisposeIfSafeAsync()
    {
        if (!_simulationRunControlWorkflow.AreOperationsIdle)
        {
            return false;
        }

        var runtimeDisposed = _runtimeResources.IsDisposed;
        if (!runtimeDisposed)
        {
            var disposedByDispatch = false;
            await _dispatchAfterDispose(() => disposedByDispatch = _runtimeResources.TryDisposeIfSafe())
                .ConfigureAwait(false);
            runtimeDisposed |= disposedByDispatch;
        }

        if (!runtimeDisposed)
        {
            return false;
        }

        _simulationRunControlWorkflow.Dispose();

        return true;
    }

    private Task RecordDiagnosticAsync(
        SimulationOperationalDiagnosticKind kind,
        SimulationLogSeverity severity,
        string message,
        string stage,
        SimulationEngineTerminationResult? termination = null,
        Exception? exception = null) =>
        _dispatch(() => _recordDiagnostic(new(kind, severity, message, stage, termination, exception)));
}
