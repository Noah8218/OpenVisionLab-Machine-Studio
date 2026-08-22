using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Layouts;

public sealed class Transform2D
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("rotationDegrees")]
    public double RotationDegrees { get; set; }
}
