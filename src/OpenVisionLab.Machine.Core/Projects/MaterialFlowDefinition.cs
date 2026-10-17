using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class MaterialFlowDefinition
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("workpieceName")]
    public string WorkpieceName { get; set; } = "시험 작업물";

    [JsonPropertyName("okRoute")]
    public string OkRoute { get; set; } = "직진 배출";

    [JsonPropertyName("ngRoute")]
    public string NgRoute { get; set; } = "측면 배출";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("totalFeedCount")]
    public int? TotalFeedCount { get; set; }
}
