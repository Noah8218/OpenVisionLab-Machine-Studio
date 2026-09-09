namespace OpenVisionLab.Machine.Simulation.Engine;

/// <summary>
/// Immutable runtime policy for starting and optionally repeating one compiled sequence.
/// Authored repeat delay is retained in milliseconds until the engine validates
/// and converts it to its fixed-step tick count.
/// </summary>
public sealed record AutomaticRunConfiguration(
    string SequenceId,
    string? StartInputId,
    bool StartInputValue,
    bool Repeat,
    int RepeatDelayMilliseconds);
