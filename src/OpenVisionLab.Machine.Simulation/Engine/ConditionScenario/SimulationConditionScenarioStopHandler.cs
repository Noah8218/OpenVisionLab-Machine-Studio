using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationConditionScenarioStopContext(
    bool ScenarioActive,
    DeterministicConditionScenarioProfile? Profile,
    long ExecutedTicks,
    SimulationConditionScheduledFaultRecoveryContext RecoveryContext,
    SimulationConditionScheduledFaultRecoveryHandler RecoveryHandler);

internal sealed record SimulationConditionScenarioStopState(
    bool ScenarioActive,
    DeterministicConditionTransition? LastTransition,
    SimulationConditionScheduledFaultRecoveryState RecoveryState);

internal sealed record SimulationConditionScenarioStopEvent(
    string Category,
    string Code,
    string Message,
    string? CommandId);

internal sealed record SimulationConditionScenarioStopOutcome(
    SimulationCommandResult Result,
    SimulationConditionScenarioStopState? State = null,
    IReadOnlyList<SimulationConditionScenarioStopEvent>? Events = null);

internal sealed class SimulationConditionScenarioStopHandler
{
    public SimulationConditionScenarioStopOutcome Apply(
        SimulationCommand command,
        SimulationConditionScenarioStopContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            StopConditionScenarioCommand => ApplyStop(command, context),
            _ => Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported.")
        };
    }

    private static SimulationConditionScenarioStopOutcome ApplyStop(
        SimulationCommand command,
        SimulationConditionScenarioStopContext context)
    {
        if (!context.ScenarioActive || context.Profile is null)
        {
            return Reject(
                command,
                context,
                SimulationCommandErrorCode.ConditionScenarioNotActive,
                "No condition scenario is active.");
        }

        var recoveryOutcome = context.RecoveryHandler.Apply(
            context.RecoveryContext with
            {
                Schedule = context.Profile.FaultRecovery,
                RestartSequence = false,
                CommandId = command.CommandId
            });
        var events = recoveryOutcome.Events?.Select(operationEvent =>
                new SimulationConditionScenarioStopEvent(
                    operationEvent.Category,
                    operationEvent.Code,
                    operationEvent.Message,
                    operationEvent.CommandId))
            .ToList()
            ?? new List<SimulationConditionScenarioStopEvent>();
        var recoveryState = recoveryOutcome.State ?? context.RecoveryContext.State;
        events.Add(new SimulationConditionScenarioStopEvent(
            "Condition",
            "ConditionScenarioStopped",
            $"Condition scenario '{context.Profile.ScenarioId}' stopped after " +
            $"{context.ExecutedTicks} ticks.",
            command.CommandId));

        return new(
            SimulationCommandResult.Accepted(
                command,
                context.RecoveryContext.CommandBoundaryTick,
                context.RecoveryContext.CommandBoundaryTime,
                $"Condition scenario '{context.Profile.ScenarioId}' stopped."),
            new SimulationConditionScenarioStopState(
                false,
                null,
                recoveryState),
            events);
    }

    private static SimulationConditionScenarioStopOutcome Reject(
        SimulationCommand command,
        SimulationConditionScenarioStopContext context,
        SimulationCommandErrorCode errorCode,
        string detail) =>
        new(
            SimulationCommandResult.Rejected(
                command,
                context.RecoveryContext.CommandBoundaryTick,
                context.RecoveryContext.CommandBoundaryTime,
                errorCode,
                detail));
}
