namespace OpenVisionLab.Machine.Core.Time;

public readonly record struct SimulationTime(TimeSpan Elapsed, DateTimeOffset Utc);
