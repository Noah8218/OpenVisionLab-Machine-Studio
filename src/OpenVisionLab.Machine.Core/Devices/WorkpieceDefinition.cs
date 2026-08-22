using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkpieceInspectionState
{
    Pending,
    Passed,
    Failed,
    Skipped
}

public sealed class WorkpieceDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Generic";

    [JsonPropertyName("conveyorComponentId")]
    public string ConveyorComponentId { get; set; } = string.Empty;

    [JsonPropertyName("inspectionState")]
    public WorkpieceInspectionState InspectionState { get; set; } = WorkpieceInspectionState.Pending;
}
