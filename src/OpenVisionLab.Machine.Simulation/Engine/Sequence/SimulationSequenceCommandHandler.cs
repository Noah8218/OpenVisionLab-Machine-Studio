using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationSequenceCommandState(
    SimulationRunMode RunMode,
    SimulationControlOwner ControlOwner,
    int PendingSteps,
    string? ActiveSequenceId,
    bool AutomaticRunActive,
    bool AutomaticRunWaitingForRepeat,
    int AutomaticRunRemainingDelayTicks,
    bool ConditionScheduledFaultInterruptedAutomaticRun);

internal sealed record SimulationSequenceCommandContext(
    SimulationSequenceCommandState State,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    IReadOnlyDictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults,
    DeterministicSequenceDebugState SequenceDebugState,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationSequenceCommandEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationSequenceCommandOutcome(
    SimulationCommandResult Result,
    SimulationSequenceCommandState? State = null,
    IReadOnlyList<SimulationSequenceCommandEvent>? Events = null);

internal sealed class SimulationSequenceCommandHandler
{
    public SimulationSequenceCommandOutcome Apply(
        SimulationCommand command,
        SimulationSequenceCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            StartSequenceCommand startSequence => ApplyStartSequence(command, startSequence, context),
            AbortSequenceCommand abortSequence => ApplyAbortSequence(command, abortSequence, context),
            RetrySequenceCommand retrySequence => ApplyRetrySequence(command, retrySequence, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationSequenceCommandOutcome ApplyStartSequence(
        SimulationCommand command,
        StartSequenceCommand startSequence,
        SimulationSequenceCommandContext context)
    {
        var state = context.State;
        if (state.AutomaticRunActive)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceStartRejected,
                "A configured automatic run is already active.");
        }

        if (!context.SequenceExecutors.TryGetValue(startSequence.SequenceId, out var executor))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Sequence '{startSequence.SequenceId}' is not configured.");
        }

        if (state.ActiveSequenceId is not null
            && context.SequenceExecutors[state.ActiveSequenceId].CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceStartRejected,
                $"Sequence '{state.ActiveSequenceId}' is already running.");
        }

        if (executor.CaptureSnapshot().Status == SequenceExecutionStatus.Faulted)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceStartRejected,
                $"Sequence '{startSequence.SequenceId}' is Faulted; clear the cause and use Retry.");
        }

        var start = executor.Start();
        if (!start.IsSuccess)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceStartRejected,
                start.Error?.Message ?? "Sequence start was rejected.");
        }

        return Accept(
            command,
            context,
            $"Sequence '{startSequence.SequenceId}' started.",
            state with
            {
                ActiveSequenceId = startSequence.SequenceId,
                ControlOwner = SimulationControlOwner.EmbeddedSequence
            },
            new SimulationSequenceCommandEvent(
                "Sequence",
                "SequenceStarted",
                $"{startSequence.SequenceId} entered {start.CurrentStepId}."));
    }

    private static SimulationSequenceCommandOutcome ApplyAbortSequence(
        SimulationCommand command,
        AbortSequenceCommand abortSequence,
        SimulationSequenceCommandContext context)
    {
        var state = context.State;
        if (!context.SequenceExecutors.TryGetValue(abortSequence.SequenceId, out var executor))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Sequence '{abortSequence.SequenceId}' is not configured.");
        }

        var snapshot = executor.CaptureSnapshot();
        if (!string.Equals(state.ActiveSequenceId, abortSequence.SequenceId, StringComparison.Ordinal)
            || snapshot.Status != SequenceExecutionStatus.Running)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceAbortRejected,
                $"Sequence '{abortSequence.SequenceId}' must be the active Running sequence.");
        }

        var aborted = executor.Abort();
        if (!aborted.IsSuccess)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceAbortRejected,
                aborted.Error?.Message ?? "Sequence abort was rejected.");
        }

        context.SequenceDebugState.ClearPendingSemanticStep();
        context.SequenceDebugState.SetPause(
            SequenceDebugPauseReason.SequenceAborted,
            aborted.CurrentStepId);
        var events = new List<SimulationSequenceCommandEvent>
        {
            new(
                "Sequence",
                "SequenceAborted",
                $"{abortSequence.SequenceId} aborted at {aborted.CurrentStepId ?? "the current boundary"}.")
        };
        if (state.AutomaticRunActive)
        {
            events.Add(new SimulationSequenceCommandEvent(
                "AutomaticRun",
                "AutomaticRunAborted",
                $"Automatic sequence '{abortSequence.SequenceId}' was aborted."));
        }

        return Accept(
            command,
            context,
            $"Sequence '{abortSequence.SequenceId}' aborted; reset is required before restart.",
            state with
            {
                RunMode = SimulationRunMode.Paused,
                PendingSteps = 0,
                AutomaticRunActive = false,
                AutomaticRunWaitingForRepeat = false,
                AutomaticRunRemainingDelayTicks = 0,
                ControlOwner = SimulationControlOwner.Definition
            },
            events);
    }

    private static SimulationSequenceCommandOutcome ApplyRetrySequence(
        SimulationCommand command,
        RetrySequenceCommand retrySequence,
        SimulationSequenceCommandContext context)
    {
        var state = context.State;
        if (!context.SequenceExecutors.TryGetValue(retrySequence.SequenceId, out var executor))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Sequence '{retrySequence.SequenceId}' is not configured.");
        }

        var snapshot = executor.CaptureSnapshot();
        if (!string.Equals(state.ActiveSequenceId, retrySequence.SequenceId, StringComparison.Ordinal)
            || snapshot.Status != SequenceExecutionStatus.Faulted)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceRetryRejected,
                $"Sequence '{retrySequence.SequenceId}' must be the active Faulted sequence.");
        }

        if (context.ActiveFaults.Count > 0)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceRetryRejected,
                "Clear active simulation faults before retrying the sequence.");
        }

        var retried = executor.Retry();
        if (!retried.IsSuccess)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceRetryRejected,
                retried.Error?.Message ?? "Sequence retry was rejected.");
        }

        context.SequenceDebugState.ClearPendingSemanticStep();
        context.SequenceDebugState.SetPause(SequenceDebugPauseReason.None, null);
        return Accept(
            command,
            context,
            $"Sequence '{retrySequence.SequenceId}' retried from its entry step; automatic continuation remains stopped.",
            state with
            {
                RunMode = SimulationRunMode.Paused,
                PendingSteps = 0,
                AutomaticRunActive = false,
                AutomaticRunWaitingForRepeat = false,
                AutomaticRunRemainingDelayTicks = 0,
                ConditionScheduledFaultInterruptedAutomaticRun = false,
                ActiveSequenceId = retrySequence.SequenceId,
                ControlOwner = SimulationControlOwner.EmbeddedSequence
            },
            new SimulationSequenceCommandEvent(
                "Sequence",
                "SequenceRetried",
                $"{retrySequence.SequenceId} retried from {snapshot.CurrentStepId ?? "the fault boundary"}; " +
                "entered " +
                $"{retried.CurrentStepId}; automatic continuation remains stopped."));
    }

    private static SimulationSequenceCommandOutcome Accept(
        SimulationCommand command,
        SimulationSequenceCommandContext context,
        string detail,
        SimulationSequenceCommandState state,
        SimulationSequenceCommandEvent? operationEvent = null) =>
        Accept(
            command,
            context,
            detail,
            state,
            operationEvent is null ? null : new[] { operationEvent });

    private static SimulationSequenceCommandOutcome Accept(
        SimulationCommand command,
        SimulationSequenceCommandContext context,
        string detail,
        SimulationSequenceCommandState state,
        IReadOnlyList<SimulationSequenceCommandEvent>? events) =>
        new(
            SimulationCommandResult.Accepted(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                detail),
            state,
            events);

    private static SimulationSequenceCommandOutcome Reject(
        SimulationCommand command,
        SimulationSequenceCommandContext context,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        new(
            SimulationCommandResult.Rejected(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                errorCode,
                detail));
}
