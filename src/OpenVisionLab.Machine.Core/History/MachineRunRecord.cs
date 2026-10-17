namespace OpenVisionLab.Machine.Core.History;

/// <summary>Persisted run evidence. Null means the producer did not record that information.</summary>
public sealed record MachineRunRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = string.Empty;
    public string RecipeId { get; init; } = string.Empty;
    public string RecipeName { get; init; } = string.Empty;
    public string AppliedRecipeJson { get; init; } = string.Empty;
    public string? ProjectPath { get; init; }
    public string? BuildIdentity { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset EndedAtUtc { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? Scope { get; init; }
    public double StartedSimulationSeconds { get; init; }
    public double EndedSimulationSeconds { get; init; }
    public int? TargetFeedCount { get; init; }
    public int? ActualFeedCount { get; init; }
    public string? RuntimeSettingsJson { get; init; }
    public IReadOnlyList<RunProductRecord> Products { get; init; } = [];
    public IReadOnlyList<RunCaptureRecord> Captures { get; init; } = [];
    public IReadOnlyList<RunInspectionRecord> Inspections { get; init; } = [];
    public IReadOnlyList<string> Faults { get; init; } = [];
}

public sealed record RunProductRecord(string Id, string Name, string? UnitId, string State, string? Verdict, string? ExitRoute, long FirstObservedTick, long LastObservedTick, string IdentityKind = "Instance");

public sealed record RunCaptureRecord
{
    public string AcquisitionId { get; init; } = string.Empty;
    public string CameraId { get; init; } = string.Empty;
    public string? ProductId { get; init; }
    public string? FrameId { get; init; }
    public string? SourceRelativePath { get; init; }
    public string? ContentSha256 { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? PixelFormat { get; init; }
    public byte[]? Pixels { get; init; }
    public byte[]? PreviewGray8 { get; init; }
    public string? ImageError { get; init; }
    public string? Verdict { get; init; }
    public string? ResultJson { get; init; }
    public long Tick { get; init; }
}

public sealed record RunInspectionRecord(string Id, string? FrameId, string? RequestJson, string? ResultJson);

public sealed record RunRecordingContext(string RecipeId, string RecipeName, string AppliedRecipeJson, string? BuildIdentity = null, string? ProjectPath = null);
