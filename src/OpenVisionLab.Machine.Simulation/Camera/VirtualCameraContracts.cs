using OpenVisionLab.Machine.Core.Devices;

namespace OpenVisionLab.Machine.Simulation.Camera;

public enum VirtualCameraState
{
    Idle,
    Exposing,
    Transferring,
    FrameReady,
    Faulted
}

public enum VirtualCameraTriggerErrorCode
{
    None,
    RecipeIdRequired,
    CameraBusy,
    CameraFaulted,
    FrameEvidenceInvalid,
    InspectionEvidenceInvalid
}

/// <summary>
/// Immutable runtime settings expressed only in fixed simulation ticks.
/// Millisecond-to-tick conversion belongs to the composition boundary.
/// </summary>
public sealed record VirtualCameraConfiguration
{
    public VirtualCameraConfiguration(
        string id,
        string name,
        int exposureTicks,
        int transferTicks,
        PlaceholderInspectionDecision placeholderDecision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (exposureTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exposureTicks),
                exposureTicks,
                "Exposure ticks must be positive.");
        }

        if (transferTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transferTicks),
                transferTicks,
                "Transfer ticks must be positive.");
        }

        if (!Enum.IsDefined(placeholderDecision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(placeholderDecision),
                placeholderDecision,
                "Placeholder inspection decision is not defined.");
        }

        Id = id;
        Name = name ?? string.Empty;
        ExposureTicks = exposureTicks;
        TransferTicks = transferTicks;
        PlaceholderDecision = placeholderDecision;
    }

    public string Id { get; }
    public string Name { get; }
    public int ExposureTicks { get; }
    public int TransferTicks { get; }
    public PlaceholderInspectionDecision PlaceholderDecision { get; }
}

public sealed record VirtualCameraFrameEvidence
{
    public VirtualCameraFrameEvidence(
        string frameId,
        string sourceRelativePath,
        string contentSha256,
        long contentLength,
        int width,
        int height,
        string pixelFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelFormat);
        if (Path.IsPathRooted(sourceRelativePath)
            || sourceRelativePath.Split(['/', '\\']).Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Frame source must be a project-relative path without parent traversal.",
                nameof(sourceRelativePath));
        }
        if (string.IsNullOrWhiteSpace(contentSha256)
            || contentSha256.Length != 64
            || contentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Content SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(contentSha256));
        }
        if (contentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        FrameId = frameId;
        SourceRelativePath = sourceRelativePath;
        ContentSha256 = contentSha256.ToUpperInvariant();
        ContentLength = contentLength;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
    }

    public string FrameId { get; }
    public string SourceRelativePath { get; }
    public string ContentSha256 { get; }
    public long ContentLength { get; }
    public int Width { get; }
    public int Height { get; }
    public string PixelFormat { get; }
}

public sealed class VirtualCameraInspectionEvidence : IEquatable<VirtualCameraInspectionEvidence>
{
    public VirtualCameraInspectionEvidence(
        string inspectionId,
        string acquisitionId,
        string cameraId,
        string recipeId,
        string frameId,
        PlaceholderInspectionDecision decision,
        string message,
        IReadOnlyDictionary<string, double>? metrics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inspectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        var copiedMetrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (metrics is not null)
        {
            foreach (var (name, value) in metrics)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Metric names are required and cannot contain leading or trailing whitespace.",
                        nameof(metrics));
                }
                if (!double.IsFinite(value))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(metrics),
                        value,
                        $"Metric '{name}' must be finite.");
                }
                copiedMetrics.Add(name, value);
            }
        }

        InspectionId = inspectionId;
        AcquisitionId = acquisitionId;
        CameraId = cameraId;
        RecipeId = recipeId;
        FrameId = frameId;
        Decision = decision;
        Message = message;
        Metrics = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(copiedMetrics);
    }

    public string InspectionId { get; }
    public string AcquisitionId { get; }
    public string CameraId { get; }
    public string RecipeId { get; }
    public string FrameId { get; }
    public PlaceholderInspectionDecision Decision { get; }
    public string Message { get; }
    public IReadOnlyDictionary<string, double> Metrics { get; }

    public bool Equals(VirtualCameraInspectionEvidence? other) => other is not null
        && InspectionId == other.InspectionId
        && AcquisitionId == other.AcquisitionId
        && CameraId == other.CameraId
        && RecipeId == other.RecipeId
        && FrameId == other.FrameId
        && Decision == other.Decision
        && Message == other.Message
        && Metrics.SequenceEqual(other.Metrics);

    public override bool Equals(object? obj) => Equals(obj as VirtualCameraInspectionEvidence);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(InspectionId, StringComparer.Ordinal);
        hash.Add(AcquisitionId, StringComparer.Ordinal);
        hash.Add(CameraId, StringComparer.Ordinal);
        hash.Add(RecipeId, StringComparer.Ordinal);
        hash.Add(FrameId, StringComparer.Ordinal);
        hash.Add(Decision);
        hash.Add(Message, StringComparer.Ordinal);
        foreach (var metric in Metrics)
        {
            hash.Add(metric.Key, StringComparer.Ordinal);
            hash.Add(metric.Value);
        }
        return hash.ToHashCode();
    }
}

public sealed record VirtualCameraAcquisitionResult(
    string AcquisitionId,
    string CameraId,
    string RecipeId,
    long AcquisitionOrdinal,
    PlaceholderInspectionDecision Decision,
    VirtualCameraFrameEvidence? FrameEvidence = null,
    VirtualCameraInspectionEvidence? InspectionEvidence = null);

public sealed record VirtualCameraSnapshot(
    string Id,
    string Name,
    VirtualCameraState State,
    long AcquisitionOrdinal,
    string? CurrentAcquisitionId,
    string? CurrentRecipeId,
    int ExposureTicksRemaining,
    int TransferTicksRemaining,
    VirtualCameraAcquisitionResult? Result,
    VirtualCameraFrameEvidence? FrameEvidence = null);

public sealed record VirtualCameraTriggerResult(
    bool IsAccepted,
    VirtualCameraTriggerErrorCode ErrorCode,
    string? AcquisitionId,
    long AcquisitionOrdinal)
{
    internal static VirtualCameraTriggerResult Accepted(
        string acquisitionId,
        long acquisitionOrdinal) =>
        new(
            true,
            VirtualCameraTriggerErrorCode.None,
            acquisitionId,
            acquisitionOrdinal);

    internal static VirtualCameraTriggerResult Rejected(
        VirtualCameraTriggerErrorCode errorCode,
        long acquisitionOrdinal) =>
        new(false, errorCode, null, acquisitionOrdinal);
}

public enum VirtualCameraTickTransition
{
    None,
    ExposureCompleted,
    FrameReady
}

public sealed record VirtualCameraTickResult(
    VirtualCameraSnapshot Snapshot,
    VirtualCameraTickTransition Transition,
    VirtualCameraAcquisitionResult? CompletedAcquisition);
