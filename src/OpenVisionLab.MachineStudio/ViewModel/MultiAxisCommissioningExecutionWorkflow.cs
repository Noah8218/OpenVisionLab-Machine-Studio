using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MultiAxisCommissioningExecutionOutcome
{
    Accepted,
    PauseRejected,
    ManualControlRejected,
    MoveRejected
}

internal sealed record MultiAxisCommissioningExecutionResult(
    MultiAxisCommissioningExecutionOutcome Outcome,
    SimulationCommandResult? RejectedCommand,
    bool PausedBeforeExecution)
{
    internal bool IsAccepted => Outcome == MultiAxisCommissioningExecutionOutcome.Accepted;
}

/// <summary>
/// Executes the ordered multi-axis commissioning transaction.
/// Recipe validation and command availability remain in the owning view models.
/// </summary>
internal sealed class MultiAxisCommissioningExecutionWorkflow
{
    private readonly ISimulationEngine _engine;
    private readonly EquipmentCommandDispatcher _equipmentCommandDispatcher;

    internal MultiAxisCommissioningExecutionWorkflow(
        ISimulationEngine engine,
        EquipmentCommandDispatcher equipmentCommandDispatcher)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _equipmentCommandDispatcher = equipmentCommandDispatcher
            ?? throw new ArgumentNullException(nameof(equipmentCommandDispatcher));
    }

    internal async Task<MultiAxisCommissioningExecutionResult> ExecuteAsync(
        IEnumerable<AxisMoveTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var moveTargets = targets.ToArray();
        var pausedBeforeExecution = false;

        if (_engine.CurrentSnapshot.RunMode != SimulationRunMode.Paused)
        {
            var pauseResult = await _engine.EnqueueCommandAsync(new PauseCommand());
            if (!pauseResult.IsAccepted)
            {
                return new(
                    MultiAxisCommissioningExecutionOutcome.PauseRejected,
                    pauseResult,
                    false);
            }

            pausedBeforeExecution = true;
        }

        var manualControlResult = await _equipmentCommandDispatcher.DispatchAxisCommandAsync(
            new StartManualControlCommand(),
            "Axis.ActionStartManual");
        if (!manualControlResult.IsAccepted)
        {
            return new(
                MultiAxisCommissioningExecutionOutcome.ManualControlRejected,
                manualControlResult,
                pausedBeforeExecution);
        }

        var moveResult = await _equipmentCommandDispatcher.DispatchAxisCommandAsync(
            new MoveAxesAbsoluteCommand(moveTargets),
            "Axis.ActionRunRecipe");
        return moveResult.IsAccepted
            ? new(
                MultiAxisCommissioningExecutionOutcome.Accepted,
                null,
                pausedBeforeExecution)
            : new(
                MultiAxisCommissioningExecutionOutcome.MoveRejected,
                moveResult,
                pausedBeforeExecution);
    }
}
