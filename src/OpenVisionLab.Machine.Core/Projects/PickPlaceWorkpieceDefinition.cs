using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class PickPlaceWorkpieceDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("xAxisId")]
    public string XAxisId { get; set; } = string.Empty;

    [JsonPropertyName("yAxisId")]
    public string YAxisId { get; set; } = string.Empty;

    [JsonPropertyName("gripperSignalId")]
    public string GripperSignalId { get; set; } = string.Empty;

    [JsonPropertyName("pickX")]
    public double PickX { get; set; }

    [JsonPropertyName("pickY")]
    public double PickY { get; set; }
}
