using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Layouts;

public sealed class LayoutComponentDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public LayoutComponentKind Kind { get; set; }

    [JsonPropertyName("transform")]
    public Transform2D Transform { get; set; } = new();

    [JsonPropertyName("size")]
    public Size2D Size { get; set; } = new();

    [JsonPropertyName("zIndex")]
    public int ZIndex { get; set; }

    [JsonPropertyName("behaviorBindingId")]
    public string? BehaviorBindingId { get; set; }
}
