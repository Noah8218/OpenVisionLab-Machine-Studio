using System.Diagnostics;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum RuntimeShutdownOutcome
{
    Completed,
    Faulted,
    TimedOut
}

internal sealed record RuntimeShutdownResult(
    RuntimeShutdownOutcome Outcome,
    TimeSpan Elapsed,
    string? Stage = null,
    SimulationEngineTerminationResult? EngineTermination = null,
    Exception? Exception = null)
{
    public bool IsCompleted => Outcome == RuntimeShutdownOutcome.Completed;
}

internal sealed class RuntimeShutdownTimeoutException(string stage) : OperationCanceledException
{
    public string Stage { get; } = stage;
}

internal sealed class BoundedShutdownCoordinator
{
    private readonly object _gate = new();
    private Task<RuntimeShutdownResult>? _shutdownTask;

    public Task<RuntimeShutdownResult> ShutdownAsync(
        TimeSpan timeout,
        Func<CancellationToken, Task<RuntimeShutdownResult>> operation,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Shutdown timeout must be positive.");
        }

        ArgumentNullException.ThrowIfNull(operation);

        Task<RuntimeShutdownResult> shutdownTask;
        lock (_gate)
        {
            shutdownTask = _shutdownTask ??= RunAsync(timeout, operation);
        }

        return cancellationToken.CanBeCanceled
            ? shutdownTask.WaitAsync(cancellationToken)
            : shutdownTask;
    }

    internal static async Task AwaitStageAsync(
        Task task,
        string stage,
        CancellationToken deadline)
    {
        try
        {
            await task.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
        {
            // Cancellation of a close-time child task is an expected result of
            // the shutdown request; the deadline remains the failure boundary.
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new RuntimeShutdownTimeoutException(stage);
        }
    }

    internal static async Task<T> AwaitStageAsync<T>(
        Task<T> task,
        string stage,
        CancellationToken deadline)
    {
        try
        {
            return await task.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new RuntimeShutdownTimeoutException(stage);
        }
    }

    private static async Task<RuntimeShutdownResult> RunAsync(
        TimeSpan timeout,
        Func<CancellationToken, Task<RuntimeShutdownResult>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            var result = await operation(deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            return result with { Elapsed = stopwatch.Elapsed };
        }
        catch (RuntimeShutdownTimeoutException exception)
        {
            return new(
                RuntimeShutdownOutcome.TimedOut,
                stopwatch.Elapsed,
                exception.Stage,
                Exception: exception);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(RuntimeShutdownOutcome.TimedOut, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            return new(RuntimeShutdownOutcome.Faulted, stopwatch.Elapsed, Exception: exception);
        }
    }
}
