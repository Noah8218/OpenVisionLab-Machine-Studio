using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Channels;

public sealed class ChannelDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public ChannelKind Kind { get; set; } = ChannelKind.DigitalInput;

    [JsonPropertyName("initialValue")]
    public double InitialValue { get; set; }

    [JsonPropertyName("interlockIds")]
    public List<string> InterlockIds { get; set; } = new();
}
