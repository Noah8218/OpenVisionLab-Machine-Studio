using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationConditionScenarioStartState(
    DeterministicConditionScenarioProfile Profile,
    DeterministicConditionStateMachine StateMachine,
    bool IsActive);

internal sealed record SimulationConditionScenarioCommandContext(
    bool ScenarioActive,
    SimulationSnapshot RuntimeSnapshot,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    IReadOnlyDictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults,
    long CommandBoundaryTick,
    TimeSpan CommandBoundaryTime);

internal sealed record SimulationConditionScenarioCommandEvent(
    string Category,
    string Code,
    string Message);

internal sealed record SimulationConditionScenarioCommandOutcome(
    SimulationCommandResult Result,
    SimulationConditionScenarioStartState? State = null,
    IReadOnlyList<SimulationConditionScenarioCommandEvent>? Events = null);

internal sealed class SimulationConditionScenarioCommandHandler
{
    public SimulationConditionScenarioCommandOutcome Apply(
        SimulationCommand command,
        SimulationConditionScenarioCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            StartConditionScenarioCommand start => ApplyStart(command, start, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationConditionScenarioCommandOutcome ApplyStart(
        SimulationCommand command,
        StartConditionScenarioCommand start,
        SimulationConditionScenarioCommandContext context)
    {
        if (context.ScenarioActive)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.ConditionScenarioAlreadyActive,
                "A condition scenario is already active.");
        }

        var normalized = DeterministicConditionScenarioProfile.Normalize(start.Profile);
        var validationErrors = DeterministicConditionScenarioProfile.Validate(normalized);
        if (validationErrors.Count > 0)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.ConditionScenarioInvalid,
                string.Join(" ", validationErrors));
        }

        bool targetExists = context.RuntimeSnapshot.Axes.Any(axis =>
                string.Equals(axis.Id, normalized.TargetId, StringComparison.Ordinal))
            || context.RuntimeSnapshot.LayoutComponents.Any(component =>
                string.Equals(component.Id, normalized.TargetId, StringComparison.Ordinal));
        if (!targetExists)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
                $"Condition target '{normalized.TargetId}' was not found in the active runtime.");
        }

        var faultRecovery = normalized.FaultRecovery;
        if (faultRecovery is not null)
        {
            var targets = new SimulationFaultTargetCatalog().GetTargets(
                context.RuntimeSnapshot,
                faultRecovery.FaultKind);
            if (!targets.Any(target => string.Equals(
                    target.Id,
                    faultRecovery.TargetId,
                    StringComparison.Ordinal)))
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
                    $"Condition fault target '{faultRecovery.TargetId}' was not found for " +
                    $"'{faultRecovery.FaultKind}' in the active runtime.");
            }

            if (faultRecovery.RestartSequenceId is not null
                && !context.SequenceExecutors.ContainsKey(faultRecovery.RestartSequenceId))
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.ConditionScenarioTargetNotFound,
                    $"Condition recovery sequence '{faultRecovery.RestartSequenceId}' was not found in the active runtime.");
            }

            var faultKey = new SimulationFaultKey(
                faultRecovery.FaultKind,
                faultRecovery.TargetId);
            if (context.ActiveFaults.ContainsKey(faultKey))
            {
                return Reject(
                    command,
                    context,
                    SimulationCommandErrorCode.ConditionScenarioInvalid,
                    $"{faultRecovery.FaultKind} is already active for '{faultRecovery.TargetId}'.");
            }
        }

        var state = new SimulationConditionScenarioStartState(
            normalized,
            new DeterministicConditionStateMachine(normalized),
            normalized.DurationTicks > 0);
        var events = new List<SimulationConditionScenarioCommandEvent>
        {
            new(
                "Condition",
                "ConditionScenarioStarted",
                $"Condition scenario '{normalized.ScenarioId}' started for '{normalized.TargetId}' " +
                $"with seed {normalized.Seed}.")
        };
        if (!state.IsActive)
        {
            events.Add(new SimulationConditionScenarioCommandEvent(
                "Condition",
                "ConditionScenarioCompleted",
                $"Condition scenario '{normalized.ScenarioId}' completed after 0 ticks."));
        }

        return Accept(
            command,
            context,
            $"Condition scenario '{normalized.ScenarioId}' started.",
            state,
            events);
    }

    private static SimulationConditionScenarioCommandOutcome Accept(
        SimulationCommand command,
        SimulationConditionScenarioCommandContext context,
        string detail,
        SimulationConditionScenarioStartState state,
        IReadOnlyList<SimulationConditionScenarioCommandEvent> events) =>
        new(
            SimulationCommandResult.Accepted(
                command,
                context.CommandBoundaryTick,
                context.CommandBoundaryTime,
                detail),
            state,
            events);

    private static SimulationConditionScenarioCommandOutcome Reject(
        SimulationCommand command,
        SimulationConditionScenarioCommandContext context,
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
