using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Owns the mutable condition-scenario state that connects scenario commands,
/// deterministic progress, snapshots, and runtime reset behavior.
/// </summary>
internal sealed class SimulationConditionScenarioRuntime
{
    private readonly SimulationConditionScenarioProgressHandler _progressHandler = new();
    private DeterministicConditionScenarioProfile? _profile;
    private DeterministicConditionStateMachine? _stateMachine;
    private bool _isActive;
    private bool _scheduledFaultActive;
    private bool _scheduledFaultInterruptedAutomaticRun;
    private long _executedTicks;
    private DeterministicConditionTransition? _lastTransition;

    internal DeterministicConditionScenarioProfile? Profile => _profile;
    internal bool IsActive => _isActive;
    internal bool ScheduledFaultActive => _scheduledFaultActive;
    internal bool ScheduledFaultInterruptedAutomaticRun => _scheduledFaultInterruptedAutomaticRun;
    internal long ExecutedTicks => _executedTicks;

    internal void ApplyStartState(SimulationConditionScenarioStartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _profile = state.Profile;
        _stateMachine = state.StateMachine;
        _isActive = state.IsActive;
        _scheduledFaultActive = false;
        _scheduledFaultInterruptedAutomaticRun = false;
        _executedTicks = 0;
        _lastTransition = null;
    }

    internal void ApplyStopState(SimulationConditionScenarioStopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _isActive = state.ScenarioActive;
        ApplyScheduledFaultRecoveryState(
            state.RecoveryState.ScheduledFaultActive,
            state.RecoveryState.InterruptedAutomaticRun);
        _lastTransition = state.LastTransition;
    }

    internal void SetActive(bool isActive) => _isActive = isActive;

    internal void ApplyScheduledFaultInjectionOutcome(
        SimulationConditionScheduledFaultInjectionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.ScheduledFaultActive is { } scheduledFaultActive)
        {
            _scheduledFaultActive = scheduledFaultActive;
        }
        if (outcome.ConditionScenarioActive is { } conditionScenarioActive)
        {
            _isActive = conditionScenarioActive;
        }
    }

    internal void ApplyScheduledFaultRecoveryState(
        bool scheduledFaultActive,
        bool interruptedAutomaticRun)
    {
        _scheduledFaultActive = scheduledFaultActive;
        _scheduledFaultInterruptedAutomaticRun = interruptedAutomaticRun;
    }

    internal void CaptureAutomaticRunInterruption(string? activeSequenceId)
    {
        var recovery = _profile?.FaultRecovery;
        _scheduledFaultInterruptedAutomaticRun =
            _scheduledFaultActive
            && recovery?.RestartSequenceId is not null
            && string.Equals(
                activeSequenceId,
                recovery.RestartSequenceId,
                StringComparison.Ordinal);
    }

    internal void SetAutomaticRunInterruption(bool interruptedAutomaticRun) =>
        _scheduledFaultInterruptedAutomaticRun = interruptedAutomaticRun;

    internal IReadOnlyList<SimulationConditionScenarioProgressEvent> Advance()
    {
        if (_profile is null || _stateMachine is null)
        {
            return Array.Empty<SimulationConditionScenarioProgressEvent>();
        }

        var outcome = _progressHandler.Apply(
            new SimulationConditionScenarioProgressContext(
                _profile,
                _stateMachine,
                _isActive,
                _executedTicks));
        _executedTicks = outcome.State.ExecutedTicks;
        _lastTransition = outcome.State.LastTransition;
        _isActive = outcome.State.IsActive;
        return outcome.Events ?? Array.Empty<SimulationConditionScenarioProgressEvent>();
    }

    internal DeterministicConditionScenarioSnapshot CreateSnapshot()
    {
        if (_profile is null || _stateMachine is null)
        {
            return DeterministicConditionScenarioSnapshot.NotConfigured;
        }

        return new DeterministicConditionScenarioSnapshot(
            true,
            _isActive,
            _profile.ScenarioId,
            _profile.TargetId,
            _profile.Seed,
            _profile.DurationTicks,
            _executedTicks,
            _profile.InitialState,
            _stateMachine.State,
            _stateMachine.HealthScore,
            _lastTransition);
    }

    internal void Reset()
    {
        _isActive = false;
        _scheduledFaultActive = false;
        _scheduledFaultInterruptedAutomaticRun = false;
        _executedTicks = 0;
        _lastTransition = null;
        _stateMachine = _profile is null
            ? null
            : new DeterministicConditionStateMachine(_profile);
    }

    internal void Clear()
    {
        _profile = null;
        _stateMachine = null;
        _isActive = false;
        _scheduledFaultActive = false;
        _scheduledFaultInterruptedAutomaticRun = false;
        _executedTicks = 0;
        _lastTransition = null;
    }
}
