using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class PrealignerDefinition
{
    [JsonPropertyName("rotaryStageComponentId")]
    public string RotaryStageComponentId { get; set; } = string.Empty;

    [JsonPropertyName("clampCylinderComponentId")]
    public string ClampCylinderComponentId { get; set; } = string.Empty;

    [JsonPropertyName("waferPresentSensorChannelId")]
    public string WaferPresentSensorChannelId { get; set; } = string.Empty;

    [JsonPropertyName("alignmentAcceptedCommandChannelId")]
    public string AlignmentAcceptedCommandChannelId { get; set; } = string.Empty;

    [JsonPropertyName("alignmentReadyFeedbackChannelId")]
    public string AlignmentReadyFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("alignmentCompleteFeedbackChannelId")]
    public string AlignmentCompleteFeedbackChannelId { get; set; } = string.Empty;

    [JsonPropertyName("alignmentTargetDegrees")]
    public double AlignmentTargetDegrees { get; set; }

    [JsonPropertyName("alignmentToleranceDegrees")]
    public double AlignmentToleranceDegrees { get; set; } = 0.1;
}
