using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum ScheduledFaultScenarioFailureStage
{
    Reset,
    StartConditionScenario,
    StartAutomaticRun,
    StartRecoverySequence,
    Play
}

internal sealed record ScheduledFaultScenarioStartResult(
    bool IsAccepted,
    ScheduledFaultScenarioFailureStage? FailureStage,
    SimulationCommandResult? FailureResult,
    StartConditionScenarioCommand? StartCommand);

/// <summary>
/// Owns the ordered engine command transaction used to start a scheduled-fault
/// scenario, including cleanup when a post-start stage is rejected.
/// </summary>
internal sealed class ScheduledFaultScenarioWorkflow
{
    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _enqueueCommand;

    internal ScheduledFaultScenarioWorkflow(
        Func<SimulationCommand, Task<SimulationCommandResult>> enqueueCommand)
    {
        _enqueueCommand = enqueueCommand
            ?? throw new ArgumentNullException(nameof(enqueueCommand));
    }

    internal async Task<ScheduledFaultScenarioStartResult> StartAsync(
        DeterministicConditionScenarioProfile profile,
        bool hasAutomaticRun)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var resetResult = await _enqueueCommand(new ResetCommand());
        if (!resetResult.IsAccepted)
        {
            return Failure(ScheduledFaultScenarioFailureStage.Reset, resetResult);
        }

        var startCommand = new StartConditionScenarioCommand(profile);
        var scenarioResult = await _enqueueCommand(startCommand);
        if (!scenarioResult.IsAccepted)
        {
            return Failure(
                ScheduledFaultScenarioFailureStage.StartConditionScenario,
                scenarioResult);
        }

        if (hasAutomaticRun)
        {
            var automaticResult = await _enqueueCommand(new StartAutomaticRunCommand());
            if (!automaticResult.IsAccepted)
            {
                return await FailureAfterCleanupAsync(
                    ScheduledFaultScenarioFailureStage.StartAutomaticRun,
                    automaticResult);
            }
        }
        else if (profile.FaultRecovery?.RestartSequenceId is { } recoverySequenceId)
        {
            var sequenceResult = await _enqueueCommand(
                new StartSequenceCommand(recoverySequenceId));
            if (!sequenceResult.IsAccepted)
            {
                return await FailureAfterCleanupAsync(
                    ScheduledFaultScenarioFailureStage.StartRecoverySequence,
                    sequenceResult);
            }
        }

        var playResult = await _enqueueCommand(new PlayCommand());
        if (!playResult.IsAccepted)
        {
            return await FailureAfterCleanupAsync(
                ScheduledFaultScenarioFailureStage.Play,
                playResult);
        }

        return new(true, null, null, startCommand);
    }

    private async Task<ScheduledFaultScenarioStartResult> FailureAfterCleanupAsync(
        ScheduledFaultScenarioFailureStage stage,
        SimulationCommandResult result)
    {
        await _enqueueCommand(new StopConditionScenarioCommand());
        return Failure(stage, result);
    }

    private static ScheduledFaultScenarioStartResult Failure(
        ScheduledFaultScenarioFailureStage stage,
        SimulationCommandResult result) =>
        new(false, stage, result, null);
}
