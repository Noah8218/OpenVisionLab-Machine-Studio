namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class FixedStepDelayConverter
{
    private readonly TimeSpan _fixedStep;

    internal FixedStepDelayConverter(TimeSpan fixedStep)
    {
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixedStep),
                fixedStep,
                "The runtime fixed step must be positive.");
        }

        _fixedStep = fixedStep;
    }

    internal TimeSpan FixedStep => _fixedStep;

    internal bool TryConvertDelayToTicks(
        int milliseconds,
        bool allowZero,
        out int tickCount)
    {
        tickCount = 0;
        if (milliseconds < 0 || (!allowZero && milliseconds == 0))
        {
            return false;
        }

        long delayTicks = TimeSpan.FromMilliseconds(milliseconds).Ticks;
        if (delayTicks % _fixedStep.Ticks != 0)
        {
            return false;
        }

        long candidate = delayTicks / _fixedStep.Ticks;
        if (candidate > int.MaxValue)
        {
            return false;
        }

        tickCount = (int)candidate;
        return allowZero || tickCount > 0;
    }
}
