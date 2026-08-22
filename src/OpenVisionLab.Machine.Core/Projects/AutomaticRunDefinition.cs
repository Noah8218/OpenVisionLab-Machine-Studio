using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Projects;

/// <summary>
/// Authored startup policy for one automatic simulation sequence.
/// </summary>
public sealed class AutomaticRunDefinition
{
    [JsonPropertyName("sequenceId")]
    public string SequenceId { get; set; } = string.Empty;

    [JsonPropertyName("startInputId")]
    public string? StartInputId { get; set; }

    [JsonPropertyName("startInputValue")]
    public bool StartInputValue { get; set; } = true;

    [JsonPropertyName("repeat")]
    public bool Repeat { get; set; }

    [JsonPropertyName("repeatDelayMilliseconds")]
    public int RepeatDelayMilliseconds { get; set; }
}
