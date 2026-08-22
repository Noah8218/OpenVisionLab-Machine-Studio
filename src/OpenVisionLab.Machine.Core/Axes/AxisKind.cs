using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Axes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AxisKind
{
    Linear,
    Rotary
}
