namespace OpenVisionLab.Machine.Simulation.Engine;

public sealed class SimulationSettings
{
    public const int DefaultCommandQueueCapacity = 1024;
    public const int DefaultEventBufferCapacity = 4096;

    public TimeSpan FixedStep { get; init; } = TimeSpan.FromMilliseconds(5);
    public double TimeScale { get; init; } = 1.0;
    public int MaxCatchUpTicks { get; init; } = 10;
    public int Seed { get; init; } = 1001;
    public int CommandQueueCapacity { get; init; } = DefaultCommandQueueCapacity;
    public int EventBufferCapacity { get; init; } = DefaultEventBufferCapacity;
}
