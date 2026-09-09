using System.Globalization;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public sealed record DeterministicConditionSample(
    long TickIndex,
    string TargetId,
    DeterministicConditionState State,
    int HealthScore);

public sealed record DeterministicConditionTransition(
    long TickIndex,
    string TargetId,
    DeterministicConditionState From,
    DeterministicConditionState To,
    string Reason);

public sealed record DeterministicConditionScenarioSnapshot(
    bool IsConfigured,
    bool IsActive,
    string? ScenarioId,
    string? TargetId,
    int Seed,
    long DurationTicks,
    long ExecutedTicks,
    DeterministicConditionState InitialState,
    DeterministicConditionState State,
    int HealthScore,
    DeterministicConditionTransition? LastTransition)
{
    public static readonly DeterministicConditionScenarioSnapshot NotConfigured = new(
        false,
        false,
        null,
        null,
        0,
        0,
        0,
        DeterministicConditionState.Normal,
        DeterministicConditionState.Normal,
        100,
        null);
}

public sealed class DeterministicConditionStateMachine
{
    private readonly DeterministicConditionScenarioProfile profile;
    private long lastTick = -1;
    private long nextTransitionTick;
    private int phaseIndex;
    private DeterministicConditionState state;
    private int healthScore;

    public DeterministicConditionStateMachine(DeterministicConditionScenarioProfile profile)
    {
        this.profile = DeterministicConditionScenarioProfile.Normalize(profile);
        state = this.profile.InitialState;
        healthScore = HealthFor(state);
        nextTransitionTick = PhaseDuration(phaseIndex);
    }

    public DeterministicConditionState State => state;
    public int HealthScore => healthScore;

    public DeterministicConditionSample Advance(
        long tick,
        out DeterministicConditionTransition? transition)
    {
        if (tick < 0 || tick <= lastTick)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), "Ticks must be non-negative and strictly increasing.");
        }

        transition = null;
        if (tick >= nextTransitionTick)
        {
            var previous = state;
            state = NextState(state);
            phaseIndex++;
            nextTransitionTick += PhaseDuration(phaseIndex);
            transition = new DeterministicConditionTransition(
                tick,
                profile.TargetId,
                previous,
                state,
                $"Deterministic phase {phaseIndex} reached for {profile.TargetId}.");
        }

        int targetHealth = HealthFor(state);
        int adjustment = 3 + (int)(Mix(profile.Seed, profile.TargetId, tick) % 4);
        healthScore = MoveToward(healthScore, targetHealth, adjustment);
        lastTick = tick;
        return new DeterministicConditionSample(
            tick,
            profile.TargetId,
            state,
            healthScore);
    }

    private int PhaseDuration(int phase) =>
        profile.MinimumStateTicks
        + (profile.JitterTicks == 0
            ? 0
            : (int)(Mix(profile.Seed, profile.TargetId, phase) % (uint)profile.JitterTicks));

    private static DeterministicConditionState NextState(DeterministicConditionState current) => current switch
    {
        DeterministicConditionState.Normal => DeterministicConditionState.Degraded,
        DeterministicConditionState.Degraded => DeterministicConditionState.Fault,
        DeterministicConditionState.Fault => DeterministicConditionState.Recovering,
        _ => DeterministicConditionState.Normal
    };

    public static int HealthFor(DeterministicConditionState state) => state switch
    {
        DeterministicConditionState.Normal => 100,
        DeterministicConditionState.Degraded => 68,
        DeterministicConditionState.Fault => 18,
        DeterministicConditionState.Recovering => 72,
        _ => 100
    };

    private static int MoveToward(int current, int target, int amount) =>
        current < target
            ? Math.Min(target, current + amount)
            : Math.Max(target, current - amount);

    private static uint Mix(int seed, string targetId, long value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in $"{seed.ToString(CultureInfo.InvariantCulture)}|{targetId}|{value}")
            {
                hash ^= character;
                hash *= 16777619;
            }

            return hash;
        }
    }
}
