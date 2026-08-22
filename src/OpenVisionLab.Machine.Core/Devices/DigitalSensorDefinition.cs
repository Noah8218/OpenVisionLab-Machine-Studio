using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class DigitalSensorDefinition
{
    [JsonPropertyName("outputChannelId")]
    public string OutputChannelId { get; set; } = string.Empty;

    [JsonPropertyName("targetComponentId")]
    public string TargetComponentId { get; set; } = string.Empty;

    [JsonPropertyName("onDelayMilliseconds")]
    public int OnDelayMilliseconds { get; set; }

    [JsonPropertyName("offDelayMilliseconds")]
    public int OffDelayMilliseconds { get; set; }
}
