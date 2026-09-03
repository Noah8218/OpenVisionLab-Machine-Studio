namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed class SimulationRunLoopTiming
{
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(1);
    private readonly TimeSpan _fixedStep;
    private readonly int _maxCatchUpTicks;
    private TimeSpan _lastWallTime;

    public TimeSpan Accumulator { get; private set; }

    public SimulationRunLoopTiming(TimeSpan fixedStep, int maxCatchUpTicks)
    {
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStep), "FixedStep must be positive.");
        }
        if (maxCatchUpTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks), "MaxCatchUpTicks must be positive.");
        }

        _fixedStep = fixedStep;
        _maxCatchUpTicks = maxCatchUpTicks;
    }

    public void Reset(TimeSpan wallTime)
    {
        _lastWallTime = wallTime;
        Accumulator = TimeSpan.Zero;
    }

    public void AlignToWallTime(TimeSpan wallTime) => _lastWallTime = wallTime;

    public int CalculateRealTimeTicks(TimeSpan now, double timeScale)
    {
        ValidateTimeScale(timeScale);
        Accumulator += (now - _lastWallTime) * timeScale;
        _lastWallTime = now;
        var ticksToRun = Math.Min(
            _maxCatchUpTicks,
            (int)(Accumulator.Ticks / _fixedStep.Ticks));
        Accumulator -= _fixedStep * ticksToRun;
        return ticksToRun;
    }

    public TimeSpan CalculateRealTimeDelay(double timeScale)
    {
        ValidateTimeScale(timeScale);
        var remaining = _fixedStep - Accumulator;
        var wallDelay = remaining / timeScale;
        var boundedDelay = wallDelay < _fixedStep
            ? wallDelay
            : _fixedStep;
        return boundedDelay > MinimumDelay
            ? boundedDelay
            : MinimumDelay;
    }

    private static void ValidateTimeScale(double timeScale)
    {
        if (!double.IsFinite(timeScale) || timeScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeScale), "TimeScale must be finite and positive.");
        }
    }
}
