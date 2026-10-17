using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class DigitalSensorDefinition
{
    [JsonPropertyName("outputChannelId")]
    public string OutputChannelId { get; set; } = string.Empty;

    [JsonPropertyName("targetComponentId")]
    public string TargetComponentId { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("detectAllProducts")]
    public bool? DetectAllProducts { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("useMillisecondDelays")]
    public bool? UseMillisecondDelays { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("detectionCenterY")]
    public double? DetectionCenterY { get; set; }

    [JsonPropertyName("onDelayMilliseconds")]
    public int OnDelayMilliseconds { get; set; }

    [JsonPropertyName("offDelayMilliseconds")]
    public int OffDelayMilliseconds { get; set; }
}
