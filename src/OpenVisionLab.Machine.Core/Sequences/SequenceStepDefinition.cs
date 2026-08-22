using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Sequences;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SequenceStepAction
{
    None,
    MoveAxis,
    SetChannel,
    Wait,
    TriggerCamera,
    CallSubsequence,
    WaitSignal,
    SetSignal,
    WaitAxisDone,
    Complete,
    WaitVisionResult
}

public sealed class SequenceStepDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public SequenceStepAction Action { get; set; } = SequenceStepAction.None;

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = string.Empty;

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; }

    [JsonPropertyName("nextStepId")]
    public string? NextStepId { get; set; }

    [JsonPropertyName("errorStepId")]
    public string? ErrorStepId { get; set; }

    [JsonPropertyName("failureStepId")]
    public string? FailureStepId { get; set; }

    [JsonPropertyName("expectedTargetId")]
    public string? ExpectedTargetId { get; set; }

    [JsonPropertyName("expectedState")]
    public string? ExpectedState { get; set; }
}
