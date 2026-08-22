using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class MultiAxisCommissioningRecipeDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "multi-axis-commissioning";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Multi-axis commissioning";

    [JsonPropertyName("targets")]
    public List<MultiAxisCommissioningTargetDefinition> Targets { get; set; } = new();

    [JsonPropertyName("validationRepetitions")]
    public int ValidationRepetitions { get; set; } = 3;
}

public sealed class MultiAxisCommissioningTargetDefinition
{
    [JsonPropertyName("axisId")]
    public string AxisId { get; set; } = string.Empty;

    [JsonPropertyName("targetPosition")]
    public double TargetPosition { get; set; }
}
