namespace OpenVisionLab.Machine.Simulation.Engine;

public sealed class SimulationSettings
{
    public TimeSpan FixedStep { get; init; } = TimeSpan.FromMilliseconds(5);
    public double TimeScale { get; set; } = 1.0;
    public int MaxCatchUpTicks { get; init; } = 10;
    public int Seed { get; init; } = 1001;
}
