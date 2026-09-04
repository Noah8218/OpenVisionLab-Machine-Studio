using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationRunControlContext(
    SimulationRunMode RunMode,
    int PendingSteps,
    string? ActiveSequenceId,
    string? CurrentSequenceStepId,
    IReadOnlyDictionary<string, CompiledSequence> CompiledSequences,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    DeterministicSequenceDebugState SequenceDebugState,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationRunControlEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationRunControlOutcome(
    SimulationCommandResult Result,
    SimulationRunMode? RunMode = null,
    SimulationControlOwner? ControlOwner = null,
    int? PendingSteps = null,
    IReadOnlyList<SimulationRunControlEvent>? Events = null);

internal sealed class SimulationRunControlCommandHandler
{
    public SimulationRunControlOutcome Apply(
        SimulationCommand command,
        SimulationRunControlContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            PlayCommand => ApplyPlay(command, context),
            PauseCommand => ApplyPause(command, context),
            StepCommand => ApplyStep(command, context),
            StepSequenceCommand stepSequence => ApplyStepSequence(command, stepSequence, context),
            SetSequenceBreakpointCommand setBreakpoint => ApplySequenceBreakpoint(command, setBreakpoint, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationRunControlOutcome ApplyPlay(
        SimulationCommand command,
        SimulationRunControlContext context)
    {
        context.SequenceDebugState.ClearPendingSemanticStep();
        context.SequenceDebugState.SetPause(SequenceDebugPauseReason.None, null);
        return Accept(
            command,
            context,
            "Simulation entered RealTime mode.",
            runMode: SimulationRunMode.RealTime,
            controlOwner: context.SequenceExecutors.Count > 0
                ? SimulationControlOwner.EmbeddedSequence
                : SimulationControlOwner.Manual,
            pendingSteps: 0);
    }

    private static SimulationRunControlOutcome ApplyPause(
        SimulationCommand command,
        SimulationRunControlContext context)
    {
        context.SequenceDebugState.ClearPendingSemanticStep();
        context.SequenceDebugState.SetPause(
            SequenceDebugPauseReason.User,
            context.CurrentSequenceStepId);
        return Accept(
            command,
            context,
            "Simulation paused.",
            runMode: SimulationRunMode.Paused,
            pendingSteps: 0);
    }

    private static SimulationRunControlOutcome ApplyStep(
        SimulationCommand command,
        SimulationRunControlContext context)
    {
        if (context.RunMode is SimulationRunMode.RealTime
            or SimulationRunMode.FastForward
            or SimulationRunMode.SequenceStep)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.InvalidRunMode,
                "Single-step is available only while paused.");
        }

        context.SequenceDebugState.ClearPendingSemanticStep();
        context.SequenceDebugState.SetPause(
            SequenceDebugPauseReason.FixedTick,
            context.CurrentSequenceStepId);
        return Accept(
            command,
            context,
            "One fixed tick was scheduled.",
            runMode: SimulationRunMode.SingleStep,
            pendingSteps: context.PendingSteps + 1);
    }

    private static SimulationRunControlOutcome ApplyStepSequence(
        SimulationCommand command,
        StepSequenceCommand stepSequence,
        SimulationRunControlContext context)
    {
        if (context.RunMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.InvalidRunMode,
                "Semantic Sequence step is available only while paused.");
        }

        if (!context.SequenceExecutors.TryGetValue(stepSequence.SequenceId, out var executor))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Sequence '{stepSequence.SequenceId}' is not configured.");
        }

        var snapshot = executor.CaptureSnapshot();
        if (!string.Equals(context.ActiveSequenceId, stepSequence.SequenceId, StringComparison.Ordinal)
            || snapshot.Status != SequenceExecutionStatus.Running
            || snapshot.CurrentStepId is null)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceStepRejected,
                $"Sequence '{stepSequence.SequenceId}' must be the active Running sequence.");
        }

        context.SequenceDebugState.BeginSemanticStep(
            stepSequence.SequenceId,
            snapshot.CurrentStepId);
        context.SequenceDebugState.SetPause(SequenceDebugPauseReason.None, null);
        return Accept(
            command,
            context,
            $"Sequence '{stepSequence.SequenceId}' will pause at its next semantic boundary.",
            runMode: SimulationRunMode.SequenceStep,
            pendingSteps: 0);
    }

    private static SimulationRunControlOutcome ApplySequenceBreakpoint(
        SimulationCommand command,
        SetSequenceBreakpointCommand setBreakpoint,
        SimulationRunControlContext context)
    {
        if (!context.CompiledSequences.TryGetValue(setBreakpoint.SequenceId, out var sequence))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceNotFound,
                $"Sequence '{setBreakpoint.SequenceId}' is not configured.");
        }

        if (string.IsNullOrWhiteSpace(setBreakpoint.StepId)
            || !sequence.TryGetStep(setBreakpoint.StepId, out _))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.SequenceBreakpointRejected,
                $"Step '{setBreakpoint.StepId}' was not found in Sequence '{setBreakpoint.SequenceId}'.");
        }

        context.SequenceDebugState.SetBreakpoint(
            setBreakpoint.SequenceId,
            setBreakpoint.StepId,
            setBreakpoint.IsEnabled);
        var events = new List<SimulationRunControlEvent>
        {
            new(
                "Sequence",
                "SequenceBreakpointChanged",
                $"{setBreakpoint.SequenceId}/{setBreakpoint.StepId} breakpoint " +
                (setBreakpoint.IsEnabled ? "enabled." : "disabled."))
        };

        SequenceExecutionSnapshot? activeSnapshot = context.ActiveSequenceId is { } activeSequenceId
            && context.SequenceExecutors.TryGetValue(activeSequenceId, out var activeExecutor)
            ? activeExecutor.CaptureSnapshot()
            : null;
        if (setBreakpoint.IsEnabled
            && activeSnapshot is
            {
                Status: SequenceExecutionStatus.Running,
                CurrentStepId: { } currentStepId
            }
            && string.Equals(
                activeSnapshot.ActiveSequenceId ?? activeSnapshot.SequenceId,
                setBreakpoint.SequenceId,
                StringComparison.Ordinal)
            && string.Equals(currentStepId, setBreakpoint.StepId, StringComparison.Ordinal)
            && context.RunMode != SimulationRunMode.Paused)
        {
            context.SequenceDebugState.ClearPendingSemanticStep();
            context.SequenceDebugState.SetPause(
                SequenceDebugPauseReason.Breakpoint,
                setBreakpoint.StepId);
            events.Add(new SimulationRunControlEvent(
                "Sequence",
                "SequenceBreakpointHit",
                $"{setBreakpoint.SequenceId} paused before {setBreakpoint.StepId} executes."));
            return Accept(
                command,
                context,
                $"Sequence breakpoint '{setBreakpoint.SequenceId}/{setBreakpoint.StepId}' " +
                (setBreakpoint.IsEnabled ? "enabled." : "disabled."),
                runMode: SimulationRunMode.Paused,
                events: events);
        }

        return Accept(
            command,
            context,
            $"Sequence breakpoint '{setBreakpoint.SequenceId}/{setBreakpoint.StepId}' " +
            (setBreakpoint.IsEnabled ? "enabled." : "disabled."),
            events: events);
    }

    private static SimulationRunControlOutcome Accept(
        SimulationCommand command,
        SimulationRunControlContext context,
        string detail,
        SimulationRunMode? runMode = null,
        SimulationControlOwner? controlOwner = null,
        int? pendingSteps = null,
        IReadOnlyList<SimulationRunControlEvent>? events = null) =>
        new(
            SimulationCommandResult.Accepted(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                detail),
            runMode,
            controlOwner,
            pendingSteps,
            events);

    private static SimulationRunControlOutcome Reject(
        SimulationCommand command,
        SimulationRunControlContext context,
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
