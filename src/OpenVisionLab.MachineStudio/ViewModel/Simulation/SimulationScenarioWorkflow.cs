using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum SimulationScenarioFailureStage
{
    Start,
    ReplayReset,
    ReplayStart
}

internal sealed record SimulationScenarioResult(
    bool IsAccepted,
    bool OwnsRun,
    StartConditionScenarioCommand? StartCommand,
    ScheduledFaultScenarioStartResult? ScheduledFaultResult,
    SimulationScenarioFailureStage? FailureStage,
    SimulationCommandResult? FailureResult);

internal sealed record SimulationScenarioStopResult(
    bool IsAccepted,
    bool OwnsRunAfterOperation,
    SimulationCommandResult StopResult,
    SimulationCommandResult? PauseResult);

/// <summary>
/// Owns the engine command orchestration for the Test Scenario commands.
/// The shell retains target/profile selection, presentation, and run state.
/// </summary>
internal sealed class SimulationScenarioWorkflow
{
    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _enqueueCommand;
    private readonly ScheduledFaultScenarioWorkflow _scheduledFaultScenario;

    internal SimulationScenarioWorkflow(
        Func<SimulationCommand, Task<SimulationCommandResult>> enqueueCommand)
    {
        _enqueueCommand = enqueueCommand
            ?? throw new ArgumentNullException(nameof(enqueueCommand));
        _scheduledFaultScenario = new(enqueueCommand);
    }

    internal async Task<SimulationScenarioResult> StartAsync(
        DeterministicConditionScenarioProfile profile,
        bool hasAutomaticRun)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.FaultRecovery is not null)
        {
            var scheduledResult = await _scheduledFaultScenario.StartAsync(
                profile,
                hasAutomaticRun);
            return new(
                scheduledResult.IsAccepted,
                scheduledResult.IsAccepted,
                scheduledResult.StartCommand,
                scheduledResult,
                null,
                scheduledResult.FailureResult);
        }

        var command = new StartConditionScenarioCommand(profile);
        var result = await _enqueueCommand(command);
        return result.IsAccepted
            ? new(true, false, command, null, null, null)
            : new(
                false,
                false,
                null,
                null,
                SimulationScenarioFailureStage.Start,
                result);
    }

    internal async Task<SimulationScenarioStopResult> StopAsync(bool ownsRun)
    {
        var stopResult = await _enqueueCommand(new StopConditionScenarioCommand());
        if (!stopResult.IsAccepted)
        {
            return new(false, ownsRun, stopResult, null);
        }

        if (!ownsRun)
        {
            return new(true, false, stopResult, null);
        }

        var pauseResult = await _enqueueCommand(new PauseCommand());
        return pauseResult.IsAccepted
            ? new(true, false, stopResult, pauseResult)
            : new(false, true, stopResult, pauseResult);
    }

    internal async Task<SimulationScenarioResult> ReplayAsync(
        DeterministicConditionScenarioProfile profile,
        bool hasAutomaticRun)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.FaultRecovery is not null)
        {
            return await StartAsync(profile, hasAutomaticRun);
        }

        var resetResult = await _enqueueCommand(new ResetCommand());
        if (!resetResult.IsAccepted)
        {
            return new(
                false,
                false,
                null,
                null,
                SimulationScenarioFailureStage.ReplayReset,
                resetResult);
        }

        var startCommand = new StartConditionScenarioCommand(profile);
        var startResult = await _enqueueCommand(startCommand);
        return startResult.IsAccepted
            ? new(true, false, startCommand, null, null, null)
            : new(
                false,
                false,
                null,
                null,
                SimulationScenarioFailureStage.ReplayStart,
                startResult);
    }
}
