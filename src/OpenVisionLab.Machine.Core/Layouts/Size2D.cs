using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Layouts;

public sealed class Size2D
{
    [JsonPropertyName("width")]
    public double Width { get; set; } = 100.0;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 100.0;
}
