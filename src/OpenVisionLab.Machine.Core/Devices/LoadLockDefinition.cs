using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class LoadLockDefinition
{
    [JsonPropertyName("outerDoorComponentId")]
    public string OuterDoorComponentId { get; set; } = string.Empty;

    [JsonPropertyName("innerDoorComponentId")]
    public string InnerDoorComponentId { get; set; } = string.Empty;

    [JsonPropertyName("evacuateCommandChannelId")]
    public string EvacuateCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("ventCommandChannelId")]
    public string VentCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("vacuumReadySensorChannelId")]
    public string VacuumReadySensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("atmosphereReadySensorChannelId")]
    public string AtmosphereReadySensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("pumpDownDurationMilliseconds")]
    public int PumpDownDurationMilliseconds { get; set; } = 500;

    [JsonPropertyName("ventDurationMilliseconds")]
    public int VentDurationMilliseconds { get; set; } = 500;
}
