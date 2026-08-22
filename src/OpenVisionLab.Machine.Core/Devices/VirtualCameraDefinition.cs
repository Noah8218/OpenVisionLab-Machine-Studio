using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaceholderInspectionDecision
{
    Pass,
    Fail
}

/// <summary>
/// Authored virtual-camera settings. Runtime code converts the millisecond
/// delays to fixed-step tick counts before creating a simulation component.
/// </summary>
public sealed class VirtualCameraDefinition
{
    private PlaceholderInspectionDecision _placeholderDecision =
        PlaceholderInspectionDecision.Pass;

    [JsonPropertyName("exposureDelayMilliseconds")]
    public int ExposureDelayMilliseconds { get; set; } = 20;

    [JsonPropertyName("transferDelayMilliseconds")]
    public int TransferDelayMilliseconds { get; set; } = 30;

    [JsonPropertyName("placeholderDecision")]
    public PlaceholderInspectionDecision PlaceholderDecision
    {
        get => _placeholderDecision;
        set => _placeholderDecision = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Placeholder inspection decision is not defined.");
    }

    [JsonPropertyName("singleImageSource")]
    public VirtualSingleImageSourceDefinition? SingleImageSource { get; set; }
}

/// <summary>
/// One project-relative image used as deterministic virtual-camera evidence.
/// The runtime validates and hashes the asset only when acquisition is requested.
/// </summary>
public sealed class VirtualSingleImageSourceDefinition
{
    [JsonPropertyName("sourceRelativePath")]
    public string SourceRelativePath { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("pixelFormat")]
    public string PixelFormat { get; set; } = string.Empty;
}
