using System.Diagnostics;
using OpenVisionLab.Machine.Simulation.Engine;
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
    private readonly BoundedShutdownCoordinator _shutdownCoordinator = new();
    private int _shutdownRequested;

    internal SimulationRuntimeShutdownWorkflow(
        ISimulationEngine engine,
        SimulationRuntimeLoop runtimeLoop,
        SimulationRuntimeResourceOwner runtimeResources,
        SimulationRunControlWorkflow simulationRunControlWorkflow,
        Action<SimulationRuntimeShutdownDiagnostic> recordDiagnostic)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runtimeLoop = runtimeLoop ?? throw new ArgumentNullException(nameof(runtimeLoop));
        _runtimeResources = runtimeResources
            ?? throw new ArgumentNullException(nameof(runtimeResources));
        _simulationRunControlWorkflow = simulationRunControlWorkflow
            ?? throw new ArgumentNullException(nameof(simulationRunControlWorkflow));
        _recordDiagnostic = recordDiagnostic
            ?? throw new ArgumentNullException(nameof(recordDiagnostic));
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

    internal void CompleteDisposeAfterShutdown(Task<RuntimeShutdownResult> shutdownTask)
    {
        ArgumentNullException.ThrowIfNull(shutdownTask);
        if (shutdownTask.IsCompleted)
        {
            try
            {
                shutdownTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());
            }

            TryDisposeIfSafe();
            _simulationRunControlWorkflow.Dispose();
            return;
        }

        _ = CompleteDisposeAfterShutdownAsync(shutdownTask);
    }

    private async Task<RuntimeShutdownResult> ShutdownRuntimeAsync(CancellationToken deadline)
    {
        var stopwatch = Stopwatch.StartNew();
        var stage = "RequestCancellation";
        SimulationEngineTerminationResult? termination = null;
        try
        {
            RecordDiagnostic(
                SimulationOperationalDiagnosticKind.ShutdownRequested,
                SimulationLogSeverity.Info,
                "Machine Studio runtime shutdown requested.",
                stage);
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

            stage = "ResourceDispose";
            _runtimeResources.TryDisposeIfSafe();
            var outcome = termination.IsFaulted
                ? RuntimeShutdownOutcome.Faulted
                : RuntimeShutdownOutcome.Completed;
            var message = outcome == RuntimeShutdownOutcome.Faulted
                ? "Machine Studio runtime shutdown completed after an engine fault."
                : "Machine Studio runtime shutdown completed.";
            RecordDiagnostic(
                outcome == RuntimeShutdownOutcome.Faulted
                    ? SimulationOperationalDiagnosticKind.ShutdownFaulted
                    : SimulationOperationalDiagnosticKind.ShutdownCompleted,
                outcome == RuntimeShutdownOutcome.Faulted
                    ? SimulationLogSeverity.Alarm
                    : SimulationLogSeverity.Info,
                message,
                stage,
                termination,
                termination.Exception);
            return new(outcome, stopwatch.Elapsed, stage, termination, termination.Exception);
        }
        catch (RuntimeShutdownTimeoutException exception)
        {
            RecordDiagnostic(
                SimulationOperationalDiagnosticKind.ShutdownTimedOut,
                SimulationLogSeverity.Alarm,
                $"Machine Studio runtime shutdown timed out during {exception.Stage}.",
                exception.Stage,
                termination,
                exception);
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var exception = new RuntimeShutdownTimeoutException(stage);
            RecordDiagnostic(
                SimulationOperationalDiagnosticKind.ShutdownTimedOut,
                SimulationLogSeverity.Alarm,
                $"Machine Studio runtime shutdown timed out during {stage}.",
                stage,
                termination,
                exception);
            throw exception;
        }
        catch (Exception exception)
        {
            RecordDiagnostic(
                SimulationOperationalDiagnosticKind.ShutdownFaulted,
                SimulationLogSeverity.Alarm,
                $"Machine Studio runtime shutdown failed during {stage}: {exception.Message}",
                stage,
                termination,
                exception);
            return new(RuntimeShutdownOutcome.Faulted, stopwatch.Elapsed, stage, termination, exception);
        }
    }

    private async Task CompleteDisposeAfterShutdownAsync(
        Task<RuntimeShutdownResult> shutdownTask)
    {
        try
        {
            await shutdownTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }

        TryDisposeIfSafe();
        _simulationRunControlWorkflow.Dispose();
    }

    private void TryDisposeIfSafe()
    {
        try
        {
            _runtimeResources.TryDisposeIfSafe();
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void RecordDiagnostic(
        SimulationOperationalDiagnosticKind kind,
        SimulationLogSeverity severity,
        string message,
        string stage,
        SimulationEngineTerminationResult? termination = null,
        Exception? exception = null) =>
        _recordDiagnostic(new(kind, severity, message, stage, termination, exception));
}
