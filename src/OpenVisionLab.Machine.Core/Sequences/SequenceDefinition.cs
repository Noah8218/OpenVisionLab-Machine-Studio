using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Sequences;

public sealed class SequenceDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<SequenceStepDefinition> Steps { get; set; } = new();
}
