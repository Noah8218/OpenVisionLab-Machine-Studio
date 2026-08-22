using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class ConveyorDefinition
{
    [JsonPropertyName("runCommandChannelId")]
    public string RunCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("reverseCommandChannelId")]
    public string ReverseCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("speedUnitsPerSecond")]
    public double SpeedUnitsPerSecond { get; set; } = 100;
}
