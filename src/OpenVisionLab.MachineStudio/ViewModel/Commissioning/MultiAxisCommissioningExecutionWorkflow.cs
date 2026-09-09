using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MultiAxisCommissioningExecutionOutcome
{
    Accepted,
    PauseRejected,
    ManualControlRejected,
    MoveRejected,
    Interrupted
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
internal sealed class MultiAxisCommissioningExecutionWorkflow : IDisposable
{
    private readonly ISimulationEngine _engine;
    private readonly EquipmentCommandDispatcher _equipmentCommandDispatcher;
    private long _executionVersion;
    private int _disposed;

    internal MultiAxisCommissioningExecutionWorkflow(
        ISimulationEngine engine,
        EquipmentCommandDispatcher equipmentCommandDispatcher)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _equipmentCommandDispatcher = equipmentCommandDispatcher
            ?? throw new ArgumentNullException(nameof(equipmentCommandDispatcher));
    }

    internal long ExecutionVersion => Volatile.Read(ref _executionVersion);

    // PL-0110's transaction stays here. Invalidation fixes delayed replies without
    // another owner or gate; AsyncRelayCommand still prevents duplicate Run input.
    internal void InvalidatePendingExecution() => Interlocked.Increment(ref _executionVersion);

    internal Task<MultiAxisCommissioningExecutionResult> ExecuteAsync(IEnumerable<AxisMoveTarget> targets) =>
        ExecuteAsync(targets, () => true);

    internal async Task<MultiAxisCommissioningExecutionResult> ExecuteAsync(
        IEnumerable<AxisMoveTarget> targets,
        Func<bool> isContextCurrent)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(isContextCurrent);
        var executionVersion = ExecutionVersion;
        var runtime = _engine.CurrentSnapshot;
        var expectedRuntime = new SimulationRuntimeIdentity(runtime.ProjectId, runtime.RuntimeGeneration);
        var pausedBeforeExecution = false;

        bool IsCurrent()
        {
            if (Volatile.Read(ref _disposed) != 0 || ExecutionVersion != executionVersion || !isContextCurrent())
            {
                return false;
            }
            var current = _engine.CurrentSnapshot;
            return current.RuntimeGeneration == runtime.RuntimeGeneration
                && string.Equals(current.ProjectId, runtime.ProjectId, StringComparison.Ordinal);
        }

        MultiAxisCommissioningExecutionResult Interrupted() =>
            new(MultiAxisCommissioningExecutionOutcome.Interrupted, null, pausedBeforeExecution);

        if (!IsCurrent())
        {
            return Interrupted();
        }

        var moveTargets = targets.ToArray();
        try
        {
            if (runtime.RunMode != SimulationRunMode.Paused)
            {
                var pauseResult = await _engine.EnqueueCommandAsync(new PauseCommand { ExpectedRuntime = expectedRuntime });
                pausedBeforeExecution = pauseResult.IsAccepted;
                if (!IsCurrent())
                {
                    return Interrupted();
                }
                if (!pauseResult.IsAccepted)
                {
                    return new(MultiAxisCommissioningExecutionOutcome.PauseRejected, pauseResult, false);
                }
            }

            var manualControlResult = await _equipmentCommandDispatcher.DispatchAxisCommandAsync(
                new StartManualControlCommand { ExpectedRuntime = expectedRuntime }, "Axis.ActionStartManual", IsCurrent);
            if (!IsCurrent())
            {
                return Interrupted();
            }
            if (!manualControlResult.IsAccepted)
            {
                return new(MultiAxisCommissioningExecutionOutcome.ManualControlRejected, manualControlResult, pausedBeforeExecution);
            }

            var moveResult = await _equipmentCommandDispatcher.DispatchAxisCommandAsync(
                new MoveAxesAbsoluteCommand(moveTargets) { ExpectedRuntime = expectedRuntime }, "Axis.ActionRunRecipe", IsCurrent);
            if (!IsCurrent())
            {
                return Interrupted();
            }
            return moveResult.IsAccepted
                ? new(MultiAxisCommissioningExecutionOutcome.Accepted, null, pausedBeforeExecution)
                : new(MultiAxisCommissioningExecutionOutcome.MoveRejected, moveResult, pausedBeforeExecution);
        }
        catch (Exception) when (!IsCurrent())
        {
            // A late engine failure belongs to the interrupted request. Current
            // failures still propagate through the existing command error path.
            return Interrupted();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            InvalidatePendingExecution();
        }
    }
}
