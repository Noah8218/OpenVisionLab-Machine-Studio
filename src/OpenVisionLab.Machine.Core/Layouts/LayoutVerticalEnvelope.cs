using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Layouts;

/// <summary>
/// Schematic vertical bounds in layout units (LU). Layout width and height
/// provide the X/Z footprint; this envelope provides the Y elevation and size.
/// </summary>
public sealed class LayoutVerticalEnvelope
{
    [JsonPropertyName("baseElevation")]
    public double BaseElevation { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    public static double GetSchematicHeight(double footprintWidth, double footprintDepth)
    {
        if (!double.IsFinite(footprintWidth) || footprintWidth <= 0 ||
            !double.IsFinite(footprintDepth) || footprintDepth <= 0)
        {
            return 8d;
        }

        return Math.Max(8d, Math.Min(footprintWidth, footprintDepth) * 0.4d);
    }
}
