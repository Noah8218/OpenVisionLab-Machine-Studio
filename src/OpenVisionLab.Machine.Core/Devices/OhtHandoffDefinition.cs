using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class OhtHandoffDefinition
{
    [JsonPropertyName("transportConveyorComponentId")]
    public string TransportConveyorComponentId { get; set; } = string.Empty;

    [JsonPropertyName("routeAvailableSensorChannelId")]
    public string RouteAvailableSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("vehicleDockedSensorChannelId")]
    public string VehicleDockedSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("loadPortReadySensorChannelId")]
    public string LoadPortReadySensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("carrierReceivedSensorChannelId")]
    public string CarrierReceivedSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("handoffReadyFeedbackChannelId")]
    public string HandoffReadyFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("carrierTransferredFeedbackChannelId")]
    public string CarrierTransferredFeedbackChannelId { get; set; } = string.Empty;
}
