using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class InspectionHandoffDefinition
{
    [JsonPropertyName("cameraId")]
    public string CameraId { get; set; } = string.Empty;

    [JsonPropertyName("inspectionPositionSensorChannelId")]
    public string InspectionPositionSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("resultAcceptedCommandChannelId")]
    public string ResultAcceptedCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("inspectionReadyFeedbackChannelId")]
    public string InspectionReadyFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("inspectionCompleteFeedbackChannelId")]
    public string InspectionCompleteFeedbackChannelId { get; set; } = string.Empty;
}
