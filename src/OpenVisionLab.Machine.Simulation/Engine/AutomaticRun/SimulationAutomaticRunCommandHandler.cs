using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationAutomaticRunCommandState(
    SimulationRunMode RunMode,
    SimulationControlOwner ControlOwner,
    int PendingSteps,
    string? ActiveSequenceId,
    bool AutomaticRunActive,
    bool AutomaticRunWaitingForRepeat,
    long AutomaticRunCompletedCycleCount,
    int AutomaticRunRemainingDelayTicks);

internal sealed record SimulationAutomaticRunCommandContext(
    AutomaticRunConfiguration? Configuration,
    SimulationAutomaticRunCommandState State,
    DeterministicSignalHub SignalHub,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationAutomaticRunCommandEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationAutomaticRunCommandOutcome(
    SimulationCommandResult Result,
    SimulationAutomaticRunCommandState? State = null,
    IReadOnlyList<SimulationAutomaticRunCommandEvent>? Events = null);

internal sealed class SimulationAutomaticRunCommandHandler
{
    public SimulationAutomaticRunCommandOutcome Apply(
        SimulationCommand command,
        SimulationAutomaticRunCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            StartAutomaticRunCommand start => ApplyStart(command, start, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationAutomaticRunCommandOutcome ApplyStart(
        SimulationCommand command,
        StartAutomaticRunCommand startCommand,
        SimulationAutomaticRunCommandContext context)
    {
        var configuration = context.Configuration;
        if (configuration is null)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.AutomaticRunNotConfigured,
                "Automatic run is not configured.");
        }

        var state = context.State;
        if (state.RunMode != SimulationRunMode.Paused)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.InvalidRunMode,
                "Automatic run can start only while the simulation is paused.");
        }

        if (state.AutomaticRunActive)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                "Automatic run is already active.");
        }

        if (state.ActiveSequenceId is not null
            && context.SequenceExecutors[state.ActiveSequenceId].CaptureSnapshot().Status == SequenceExecutionStatus.Running)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                $"Sequence '{state.ActiveSequenceId}' is already running.");
        }

        if (!context.SequenceExecutors.TryGetValue(configuration.SequenceId, out var executor))
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                $"Automatic sequence '{configuration.SequenceId}' is unavailable.");
        }

        if (executor.CaptureSnapshot().Status != SequenceExecutionStatus.Ready)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.AutomaticRunStartRejected,
                $"Automatic sequence '{configuration.SequenceId}' is not Ready; reset is required.");
        }

        SignalWriteResult? inputWrite = null;
        if (configuration.StartInputId is not null)
        {
            inputWrite = context.SignalHub.SetDigitalInput(
                configuration.StartInputId,
                configuration.StartInputValue,
                SignalWriteOwner.Manual);
            if (!inputWrite.IsAccepted)
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.AutomaticRunStartRejected,
                    $"Automatic start input '{configuration.StartInputId}' failed: {inputWrite.ErrorCode}.");
            }
        }

        var sequenceStart = executor.Start();
        if (!sequenceStart.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Prevalidated automatic sequence '{configuration.SequenceId}' could not start.");
        }

        var nextState = state with
        {
            ActiveSequenceId = configuration.SequenceId,
            AutomaticRunActive = true,
            AutomaticRunWaitingForRepeat = false,
            AutomaticRunCompletedCycleCount = 0,
            AutomaticRunRemainingDelayTicks = 0,
            PendingSteps = 0,
            RunMode = startCommand.BeginRealTime
                ? SimulationRunMode.RealTime
                : SimulationRunMode.Paused,
            ControlOwner = SimulationControlOwner.EmbeddedSequence
        };
        var events = new List<SimulationAutomaticRunCommandEvent>();
        if (inputWrite is { StateChanged: true })
        {
            events.Add(new SimulationAutomaticRunCommandEvent(
                "I/O",
                "DigitalInputChanged",
                $"{configuration.StartInputId} = {FormatSignal(configuration.StartInputValue)}."));
        }
        events.Add(new SimulationAutomaticRunCommandEvent(
            "Sequence",
            "SequenceStarted",
            $"{configuration.SequenceId} entered {sequenceStart.CurrentStepId}."));
        events.Add(new SimulationAutomaticRunCommandEvent(
            "AutomaticRun",
            "AutomaticRunStarted",
            $"Automatic sequence '{configuration.SequenceId}' started."));

        return Accept(
            command,
            context,
            $"Automatic sequence '{configuration.SequenceId}' started.",
            nextState,
            events);
    }

    private static SimulationAutomaticRunCommandOutcome Accept(
        SimulationCommand command,
        SimulationAutomaticRunCommandContext context,
        string detail,
        SimulationAutomaticRunCommandState state,
        IReadOnlyList<SimulationAutomaticRunCommandEvent> events) =>
        new(
            SimulationCommandResult.Accepted(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                detail),
            state,
            events);

    private static SimulationAutomaticRunCommandOutcome Reject(
        SimulationCommand command,
        SimulationAutomaticRunCommandContext context,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        new(
            SimulationCommandResult.Rejected(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                errorCode,
                detail));

    private static string FormatSignal(bool value) => value ? "ON" : "OFF";
}
