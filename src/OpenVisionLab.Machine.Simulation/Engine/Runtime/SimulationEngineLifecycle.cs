using System.Threading.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed class SimulationEngineLifecycle : IDisposable
{
    private readonly Channel<SimulationCommand> _commandChannel;
    private readonly SimulationEventPublisher _eventPublisher;
    private readonly LatestSnapshotStore _snapshotStore;
    private readonly Func<SimulationEngineTerminationOutcome, Exception?, SimulationEngineTerminationResult>
        _terminationFactory;
    private readonly Func<SimulationSnapshot> _currentSnapshotFactory;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource<SimulationEngineTerminationResult> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycleLock = new();
    private Task? _runTask;
    private EngineLifecycleState _state = EngineLifecycleState.Created;
    private SimulationEngineTerminationOutcome _requestedTermination =
        SimulationEngineTerminationOutcome.Normal;
    private SimulationEngineTerminationResult? _terminalResult;
    private CancellationTokenRegistration _startCancellationRegistration;
    private bool _disposed;

    internal SimulationEngineLifecycle(
        Channel<SimulationCommand> commandChannel,
        SimulationEventPublisher eventPublisher,
        LatestSnapshotStore snapshotStore,
        Func<SimulationEngineTerminationOutcome, Exception?, SimulationEngineTerminationResult> terminationFactory,
        Func<SimulationSnapshot> currentSnapshotFactory)
    {
        _commandChannel = commandChannel ?? throw new ArgumentNullException(nameof(commandChannel));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _terminationFactory = terminationFactory ?? throw new ArgumentNullException(nameof(terminationFactory));
        _currentSnapshotFactory = currentSnapshotFactory ?? throw new ArgumentNullException(nameof(currentSnapshotFactory));
    }

    internal bool HasStarted => _runTask is not null;
    internal CancellationToken StopToken => _stopCts.Token;
    internal Task<SimulationEngineTerminationResult> Termination => _termination.Task;

    internal Task StartAsync(
        Func<CancellationToken, Task> runLoop,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runLoop);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleLock)
        {
            if (_state == EngineLifecycleState.Running)
            {
                return Task.CompletedTask;
            }

            if (_state != EngineLifecycleState.Created)
            {
                throw new InvalidOperationException("A stopped simulation engine cannot be restarted.");
            }

            _requestedTermination = SimulationEngineTerminationOutcome.Normal;
            _state = EngineLifecycleState.Running;
            _runTask = Task.Run(() => runLoop(_stopCts.Token));
            if (cancellationToken.CanBeCanceled)
            {
                _startCancellationRegistration = cancellationToken.Register(
                    static state => ((SimulationEngineLifecycle)state!).RequestCancellation(),
                    this);
            }
        }

        return Task.CompletedTask;
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_state == EngineLifecycleState.Created)
            {
                _requestedTermination = SimulationEngineTerminationOutcome.Stopped;
                _terminalResult = _terminationFactory(
                    SimulationEngineTerminationOutcome.Stopped,
                    null);
                _state = EngineLifecycleState.Stopped;
                _commandChannel.Writer.TryComplete();
                _snapshotStore.Complete();
                _eventPublisher.Complete();
                _termination.TrySetResult(_terminalResult);
            }
            else if (_state == EngineLifecycleState.Running)
            {
                _requestedTermination = SimulationEngineTerminationOutcome.Stopped;
                _state = EngineLifecycleState.Stopping;
                _commandChannel.Writer.TryComplete();
                _stopCts.Cancel();
            }
        }

        await _termination.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SimulationCommandResult> EnqueueCommandAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        SimulationCommandErrorCode? lifecycleError;
        lock (_lifecycleLock)
        {
            lifecycleError = _state switch
            {
                EngineLifecycleState.Created => SimulationCommandErrorCode.EngineNotStarted,
                EngineLifecycleState.Running => null,
                EngineLifecycleState.Faulted => SimulationCommandErrorCode.EngineFaulted,
                _ => SimulationCommandErrorCode.EngineStopped
            };
        }

        if (lifecycleError.HasValue)
        {
            return CompleteLifecycleRejection(command, lifecycleError.Value);
        }

        try
        {
            await _commandChannel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return CompleteLifecycleRejection(command, GetClosedChannelError());
        }

        return await command.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void CompleteAppliedCommands(IEnumerable<PendingSimulationCommand> pendingCommands)
    {
        foreach (var pendingCommand in pendingCommands)
        {
            if (pendingCommand.Result is null)
            {
                throw new InvalidOperationException(
                    $"Command '{pendingCommand.Command.CommandId}' has no simulation result.");
            }

            pendingCommand.Command.TryComplete(pendingCommand.Result);
        }
    }

    internal void FinalizeRun(
        SimulationEngineTerminationResult termination,
        IReadOnlyCollection<PendingSimulationCommand> pendingCommands)
    {
        lock (_lifecycleLock)
        {
            _terminalResult = termination;
            _state = termination.Outcome == SimulationEngineTerminationOutcome.Faulted
                ? EngineLifecycleState.Faulted
                : EngineLifecycleState.Stopped;
            _commandChannel.Writer.TryComplete();
        }

        CompletePendingCommands(pendingCommands, termination);
        while (_commandChannel.Reader.TryRead(out var command))
        {
            command.TryComplete(CreateTerminalCommandResult(command, termination));
        }

        _snapshotStore.Complete();
        _eventPublisher.Complete();
        _startCancellationRegistration.Dispose();
        _startCancellationRegistration = default;
        _termination.TrySetResult(termination);
    }

    internal void RequestCancellation()
    {
        lock (_lifecycleLock)
        {
            if (_state != EngineLifecycleState.Running)
            {
                return;
            }

            _requestedTermination = SimulationEngineTerminationOutcome.Cancelled;
            _state = EngineLifecycleState.Stopping;
            _commandChannel.Writer.TryComplete();
            _stopCts.Cancel();
        }
    }

    internal SimulationEngineTerminationOutcome GetRequestedTermination()
    {
        lock (_lifecycleLock)
        {
            return _requestedTermination;
        }
    }

    internal SimulationCommandErrorCode GetClosedChannelError()
    {
        lock (_lifecycleLock)
        {
            return _state == EngineLifecycleState.Faulted
                ? SimulationCommandErrorCode.EngineFaulted
                : SimulationCommandErrorCode.EngineStopped;
        }
    }

    private SimulationCommandResult CompleteLifecycleRejection(
        SimulationCommand command,
        SimulationCommandErrorCode errorCode)
    {
        var snapshot = _currentSnapshotFactory();
        SimulationEngineTerminationResult? terminalResult;
        lock (_lifecycleLock)
        {
            terminalResult = _terminalResult;
        }

        var detail = errorCode switch
        {
            SimulationCommandErrorCode.EngineNotStarted => "The simulation engine has not started.",
            SimulationCommandErrorCode.EngineFaulted when terminalResult is not null =>
                CreateTerminationDetail(terminalResult),
            SimulationCommandErrorCode.EngineFaulted => "The simulation engine faulted.",
            _ => "The simulation engine is stopped."
        };
        var result = SimulationCommandResult.Rejected(
            command,
            snapshot.TickIndex,
            snapshot.SimulationTime,
            errorCode,
            detail);
        command.TryComplete(result);
        return result;
    }

    private SimulationCommandResult CreateTerminalCommandResult(
        SimulationCommand command,
        SimulationEngineTerminationResult termination) =>
        SimulationCommandResult.Rejected(
            command,
            termination.TickIndex,
            termination.SimulationTime,
            termination.Outcome == SimulationEngineTerminationOutcome.Faulted
                ? SimulationCommandErrorCode.EngineFaulted
                : SimulationCommandErrorCode.EngineStopped,
            CreateTerminationDetail(termination));

    private static void CompletePendingCommands(
        IEnumerable<PendingSimulationCommand> pendingCommands,
        SimulationEngineTerminationResult termination)
    {
        foreach (var pendingCommand in pendingCommands)
        {
            pendingCommand.Command.TryComplete(
                SimulationCommandResult.Rejected(
                    pendingCommand.Command,
                    termination.TickIndex,
                    termination.SimulationTime,
                    termination.Outcome == SimulationEngineTerminationOutcome.Faulted
                        ? SimulationCommandErrorCode.EngineFaulted
                        : SimulationCommandErrorCode.EngineStopped,
                    CreateTerminationDetail(termination)));
        }
    }

    private static string CreateTerminationDetail(SimulationEngineTerminationResult termination)
    {
        if (termination.Outcome == SimulationEngineTerminationOutcome.Faulted)
        {
            var context = string.Join(
                ", ",
                new[]
                {
                    termination.Operation is null ? null : $"operation={termination.Operation}",
                    termination.CurrentCommandId is null ? null : $"command={termination.CurrentCommandId}",
                    $"tick={termination.TickIndex}",
                    $"time={termination.SimulationTime}"
                }.Where(value => value is not null));
            return $"The simulation engine faulted ({context}): " +
                (termination.Exception?.Message ?? "Unknown simulation engine failure.");
        }

        return termination.Outcome == SimulationEngineTerminationOutcome.Cancelled
            ? "The simulation engine was cancelled before applying the command."
            : "The simulation engine stopped before applying the command.";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        _startCancellationRegistration.Dispose();
        _startCancellationRegistration = default;
        _stopCts.Dispose();
        _eventPublisher.Dispose();
    }

    private enum EngineLifecycleState
    {
        Created,
        Running,
        Stopping,
        Stopped,
        Faulted
    }
}

internal sealed class PendingSimulationCommand(SimulationCommand command)
{
    internal SimulationCommand Command { get; } = command;
    internal SimulationCommandResult? Result { get; set; }
}
