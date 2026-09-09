namespace OpenVisionLab.Machine.Simulation.Engine;

public sealed class SimulationClock
{
    private TimeSpan _time;

    public TimeSpan Time => _time;
    public TimeSpan FixedStep { get; }

    public SimulationClock(TimeSpan fixedStep)
    {
        FixedStep = fixedStep;
    }

    public void Advance()
    {
        _time += FixedStep;
    }

    public void Reset()
    {
        _time = TimeSpan.Zero;
    }
}
