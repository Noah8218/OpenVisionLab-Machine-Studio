using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NumericalCameraInputKind { Image2D, HeightMap3D }

/// <summary>Recipe-scoped numerical mock input; never represents an external AI or calibrated camera.</summary>
public sealed record NumericalCameraSettings
{
    public NumericalCameraInputKind InputKind { get; init; }
    public double FieldOfView { get; init; } = 120;
    public double? FieldOfViewHeight { get; init; }
    public int? ResolutionWidth { get; init; }
    public int? ResolutionHeight { get; init; }
    public double CameraOffsetX { get; init; }
    public double CameraCenterY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double Yaw { get; init; }
    public double Tilt { get; init; }
    public double Shake { get; init; }
    public double Light { get; init; } = 1;
    public int Seed { get; init; } = 42;
    public string? AxisId { get; init; }
    public string RecipeName { get; init; } = "외관검사";
    public int Version { get; init; } = 1;
    public int ConfirmedVersion { get; init; } = 1;
    public bool Defect { get; init; }
    public int GenerationDelayMilliseconds { get; init; } = 2000;
    public int ResponseDelayMilliseconds { get; init; } = 600;

    public string? Validate()
    {
        if (!Enum.IsDefined(InputKind)) return "2D 또는 3D 입력 종류를 선택하세요.";
        if (string.IsNullOrWhiteSpace(RecipeName) || RecipeName.Length > 80 || Version is < 1 or > 100000 || ConfirmedVersion < 0 || ConfirmedVersion > Version) return "레시피 이름과 버전을 확인하세요.";
        if (!double.IsFinite(FieldOfView) || FieldOfView is < 10 or > 10000) return "시야는 10–10000 범위의 유한한 값이어야 합니다.";
        if (FieldOfViewHeight is { } height && (!double.IsFinite(height) || height is < 10 or > 10000) || ResolutionWidth is < 1 or > 4096 || ResolutionHeight is < 1 or > 4096 || !double.IsFinite(CameraOffsetX) || !double.IsFinite(CameraCenterY)) return "카메라 FOV·해상도·장착 좌표를 확인하세요.";
        if (new[] { OffsetX, OffsetY, Yaw }.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10000)) return "위치와 회전 편차를 확인하세요.";
        if (!double.IsFinite(Tilt) || Math.Abs(Tilt) > 80 || !double.IsFinite(Shake) || Shake is < 0 or > 50) return "기울기와 흔들림 범위를 확인하세요.";
        if (!double.IsFinite(Light) || Light is < 0 or > 4 || Seed < 0) return "조명은 0–4, 시드는 0 이상의 정수여야 합니다.";
        if (GenerationDelayMilliseconds is < 0 or > 60000 || ResponseDelayMilliseconds is < 0 or > 60000) return "생성·응답 지연은 0–60000ms 범위여야 합니다.";
        return null;
    }
}
