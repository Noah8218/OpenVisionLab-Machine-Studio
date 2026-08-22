using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Core.Models;

namespace OpenVisionLab.Machine.Core.Axes;

public sealed class VirtualAxisDefinition
{
    public const double DefaultMaxVelocity = 100.0;
    public const double DefaultMaxAcceleration = 1000.0;
    public const double DefaultFollowingErrorLimit = 0.05;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public AxisKind Kind { get; set; } = AxisKind.Linear;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "mm";

    [JsonPropertyName("softLimitMin")]
    public double? SoftLimitMin { get; set; }

    [JsonPropertyName("softLimitMax")]
    public double? SoftLimitMax { get; set; }

    [JsonPropertyName("homePosition")]
    public double HomePosition { get; set; }

    [JsonPropertyName("maxVelocity")]
    public double MaxVelocity { get; set; } = DefaultMaxVelocity;

    [JsonPropertyName("position")]
    public Coordinate3D Position { get; set; }

    [JsonPropertyName("maxAcceleration")]
    public double MaxAcceleration { get; set; } = DefaultMaxAcceleration;

    [JsonPropertyName("maxDeceleration")]
    public double? MaxDeceleration { get; set; }

    [JsonPropertyName("followingErrorLimit")]
    public double? FollowingErrorLimit { get; set; }
}
