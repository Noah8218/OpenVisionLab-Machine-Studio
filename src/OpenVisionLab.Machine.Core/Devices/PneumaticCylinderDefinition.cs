using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

/// <summary>
/// Source-neutral authored contract for one double-acting simulated cylinder.
/// The command is a sequence-owned Digital Output; end-position feedback is
/// published through simulation-owned Digital Inputs.
/// </summary>
public sealed class PneumaticCylinderDefinition
{
    [JsonPropertyName("extendCommandChannelId")]
    public string ExtendCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("extendedSensorChannelId")]
    public string ExtendedSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("retractedSensorChannelId")]
    public string RetractedSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("extendDurationMilliseconds")]
    public int ExtendDurationMilliseconds { get; set; } = 250;

    [JsonPropertyName("retractDurationMilliseconds")]
    public int RetractDurationMilliseconds { get; set; } = 250;

    [JsonPropertyName("extendedSensorDelayMilliseconds")]
    public int ExtendedSensorDelayMilliseconds { get; set; }

    [JsonPropertyName("retractedSensorDelayMilliseconds")]
    public int RetractedSensorDelayMilliseconds { get; set; }

    [JsonPropertyName("stroke")]
    public double Stroke { get; set; } = 80;
}
