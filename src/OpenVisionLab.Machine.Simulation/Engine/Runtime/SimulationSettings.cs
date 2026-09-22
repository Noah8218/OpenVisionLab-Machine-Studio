namespace OpenVisionLab.Machine.Simulation.Engine;

public sealed class SimulationSettings
{
    public const int DefaultCommandQueueCapacity = 1024;
    public const int DefaultEventBufferCapacity = 4096;
    public const int DefaultCommandTraceEntryCapacity = 100_000;
    public static readonly TimeSpan DefaultAutomaticExternalInspectionWallTimeout = TimeSpan.FromSeconds(5);

    public TimeSpan FixedStep { get; init; } = TimeSpan.FromMilliseconds(5);
    public double TimeScale { get; init; } = 1.0;
    public int MaxCatchUpTicks { get; init; } = 10;
    public int Seed { get; init; } = 1001;
    public int CommandQueueCapacity { get; init; } = DefaultCommandQueueCapacity;
    public int EventBufferCapacity { get; init; } = DefaultEventBufferCapacity;
    /// <summary>
    /// Maximum number of command-boundary entries retained in one capture.
    /// Entries beyond this limit are counted as dropped and never replace the
    /// retained prefix.
    /// </summary>
    public int CommandTraceEntryCapacity { get; init; } = DefaultCommandTraceEntryCapacity;
    public TimeSpan AutomaticExternalInspectionWallTimeout { get; init; } =
        DefaultAutomaticExternalInspectionWallTimeout;

    /// <summary>
    /// Optional canonical journal capacity. When omitted, the presentation
    /// event-buffer capacity remains the compatibility default.
    /// </summary>
    public int? CanonicalEventJournalCapacity { get; init; }
}
