using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class InspectionSortRouterDefinition
{
    [JsonPropertyName("cameraId")]
    public string CameraId { get; set; } = string.Empty;

    [JsonPropertyName("passConveyorComponentId")]
    public string PassConveyorComponentId { get; set; } = string.Empty;

    [JsonPropertyName("ngConveyorComponentId")]
    public string NgConveyorComponentId { get; set; } = string.Empty;

    [JsonPropertyName("passRoutedFeedbackChannelId")]
    public string PassRoutedFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("ngRoutedFeedbackChannelId")]
    public string NgRoutedFeedbackChannelId { get; set; } = string.Empty;
}
