using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class WaferHandlerDefinition
{
    [JsonPropertyName("horizontalAxisId")]
    public string HorizontalAxisId { get; set; } = string.Empty;

    [JsonPropertyName("verticalAxisId")]
    public string VerticalAxisId { get; set; } = string.Empty;

    [JsonPropertyName("workpieceComponentId")]
    public string WorkpieceComponentId { get; set; } = string.Empty;

    [JsonPropertyName("sourcePresentSensorChannelId")]
    public string SourcePresentSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("gateOpenSensorChannelId")]
    public string GateOpenSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("pickCommandChannelId")]
    public string PickCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("placeCommandChannelId")]
    public string PlaceCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("holdingFeedbackChannelId")]
    public string HoldingFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("placedFeedbackChannelId")]
    public string PlacedFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("pickHorizontalPosition")]
    public double PickHorizontalPosition { get; set; }

    [JsonPropertyName("pickVerticalPosition")]
    public double PickVerticalPosition { get; set; }

    [JsonPropertyName("placeHorizontalPosition")]
    public double PlaceHorizontalPosition { get; set; }

    [JsonPropertyName("placeVerticalPosition")]
    public double PlaceVerticalPosition { get; set; }
}
