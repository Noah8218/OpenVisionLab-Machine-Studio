using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Layouts;

public sealed class MachineLayoutDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("gridSize")]
    public double GridSize { get; set; } = 10.0;

    [JsonPropertyName("snapToGrid")]
    public bool SnapToGrid { get; set; } = true;

    [JsonPropertyName("components")]
    public List<LayoutComponentDefinition> Components { get; set; } = new();
}
