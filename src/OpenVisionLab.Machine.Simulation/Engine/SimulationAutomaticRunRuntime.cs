using OpenVisionLab.Machine.Sequence.Runtime;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Owns AutomaticRun configuration and mutable cycle state while leaving
/// shared sequence/control state and event publication to the engine.
/// </summary>
internal sealed class SimulationAutomaticRunRuntime
{
    private AutomaticRunConfiguration? _configuration;
    private bool _isActive;
    private bool _waitingForRepeat;
    private long _completedCycleCount;
    private int _remainingDelayTicks;
    private int _repeatDelayTicks;

    internal AutomaticRunConfiguration? Configuration => _configuration;
    internal bool IsActive => _isActive;
    internal bool WaitingForRepeat => _waitingForRepeat;
    internal long CompletedCycleCount => _completedCycleCount;
    internal int RemainingDelayTicks => _remainingDelayTicks;

    internal void Configure(
        AutomaticRunConfiguration? configuration,
        int repeatDelayTicks)
    {
        _configuration = configuration;
        _repeatDelayTicks = repeatDelayTicks;
        Reset();
    }

    internal SimulationAutomaticRunCommandState CreateCommandState(
        SimulationRunMode runMode,
        SimulationControlOwner controlOwner,
        int pendingSteps,
        string? activeSequenceId) =>
        new(
            runMode,
            controlOwner,
            pendingSteps,
            activeSequenceId,
            _isActive,
            _waitingForRepeat,
            _completedCycleCount,
            _remainingDelayTicks);

    internal void ApplyCommandState(SimulationAutomaticRunCommandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(
            state.AutomaticRunActive,
            state.AutomaticRunWaitingForRepeat,
            state.AutomaticRunCompletedCycleCount,
            state.AutomaticRunRemainingDelayTicks);
    }

    internal void ApplySequenceState(SimulationSequenceCommandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(
            state.AutomaticRunActive,
            state.AutomaticRunWaitingForRepeat,
            _completedCycleCount,
            state.AutomaticRunRemainingDelayTicks);
    }

    internal void ApplyRecoveryState(SimulationConditionScheduledFaultRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(
            state.AutomaticRunActive,
            state.AutomaticRunWaitingForRepeat,
            _completedCycleCount,
            state.AutomaticRunRemainingDelayTicks);
    }

    internal SimulationAutomaticRunCycleContext CreateCycleContext(
        string? activeSequenceId,
        IReadOnlyDictionary<string, DeterministicSequenceExecutor> sequenceExecutors)
    {
        ArgumentNullException.ThrowIfNull(sequenceExecutors);
        return new(
            _configuration,
            new SimulationAutomaticRunCycleState(
                activeSequenceId,
                _isActive,
                _waitingForRepeat,
                _completedCycleCount,
                _remainingDelayTicks),
            sequenceExecutors,
            _repeatDelayTicks);
    }

    internal void ApplyCycleState(SimulationAutomaticRunCycleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(
            state.AutomaticRunActive,
            state.AutomaticRunWaitingForRepeat,
            state.AutomaticRunCompletedCycleCount,
            state.AutomaticRunRemainingDelayTicks);
    }

    internal void MarkFaulted()
    {
        _isActive = false;
        _waitingForRepeat = false;
        _remainingDelayTicks = 0;
    }

    internal void Reset()
    {
        _isActive = false;
        _waitingForRepeat = false;
        _completedCycleCount = 0;
        _remainingDelayTicks = 0;
    }

    private void ApplyState(
        bool isActive,
        bool waitingForRepeat,
        long completedCycleCount,
        int remainingDelayTicks)
    {
        _isActive = isActive;
        _waitingForRepeat = waitingForRepeat;
        _completedCycleCount = completedCycleCount;
        _remainingDelayTicks = remainingDelayTicks;
    }
}
