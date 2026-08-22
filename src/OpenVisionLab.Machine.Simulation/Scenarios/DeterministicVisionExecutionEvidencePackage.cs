using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public sealed record DeterministicVisionMetricEvidence(string Name, double Value);

public sealed record DeterministicVisionEventEvidence(
    int Order,
    long TickOffset,
    long SimulationTimeOffsetTicks,
    string Category,
    string Code,
    string Message);

public sealed record DeterministicVisionExecutionComparison(
    bool IsMatch,
    string? MismatchCode,
    string? Detail);

/// <summary>
/// Portable evidence for one project-owned manual camera inspection. Absolute
/// runtime indices are normalized so equivalent reset-and-repeat executions
/// remain comparable.
/// </summary>
public sealed record DeterministicVisionExecutionEvidencePackage(
    int SchemaVersion,
    string ProjectId,
    string ProjectName,
    string ProjectPath,
    string ProjectHash,
    string BuildIdentity,
    long FixedStepTicks,
    string CameraId,
    string RecipeId,
    string AcquisitionId,
    string FrameId,
    string FrameHash,
    string InspectionId,
    PlaceholderInspectionDecision Decision,
    string Message,
    ImmutableArray<DeterministicVisionMetricEvidence> Metrics,
    long DurationTicks,
    string ConditionHash,
    string FaultHash,
    string SnapshotHash,
    string EventHash,
    ImmutableArray<DeterministicVisionEventEvidence> Events,
    string EvidenceHash)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string ShortEvidenceHash => EvidenceHash.Length <= 12
        ? EvidenceHash
        : EvidenceHash[..12];

    public static DeterministicVisionExecutionEvidencePackage Create(
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        string buildIdentity,
        TimeSpan fixedStep,
        long triggerTick,
        SimulationSnapshot snapshot,
        VirtualCameraSnapshot camera,
        IEnumerable<SimulationEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildIdentity);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(events);
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStep));
        }

        var result = camera.Result
            ?? throw new InvalidOperationException("A completed camera result is required.");
        var frame = result.FrameEvidence ?? camera.FrameEvidence
            ?? throw new InvalidOperationException("Completed frame evidence is required.");
        var inspection = result.InspectionEvidence
            ?? throw new InvalidOperationException("Completed inspection evidence is required.");
        if (camera.State != VirtualCameraState.FrameReady
            || !string.Equals(result.AcquisitionId, inspection.AcquisitionId, StringComparison.Ordinal)
            || !string.Equals(result.AcquisitionId, camera.CurrentAcquisitionId, StringComparison.Ordinal)
            || !string.Equals(result.CameraId, inspection.CameraId, StringComparison.Ordinal)
            || !string.Equals(result.CameraId, camera.Id, StringComparison.Ordinal)
            || !string.Equals(result.RecipeId, inspection.RecipeId, StringComparison.Ordinal)
            || !string.Equals(frame.FrameId, inspection.FrameId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Camera, frame, and inspection evidence are not correlated.");
        }

        var eventList = NormalizeEvents(events);
        RequireEvent(eventList, "CameraTriggered");
        RequireEvent(eventList, "CameraFrameReady");
        RequireEvent(eventList, "VisionResultReady");
        var metrics = inspection.Metrics
            .OrderBy(metric => metric.Key, StringComparer.Ordinal)
            .Select(metric => new DeterministicVisionMetricEvidence(metric.Key, metric.Value))
            .ToImmutableArray();
        var projectHash = Hash(projectJson ?? string.Empty);
        var conditionHash = Hash(JsonSerializer.Serialize(snapshot.ConditionScenario, JsonOptions));
        var faultHash = Hash(string.Join(
            "\n",
            snapshot.Faults
                .OrderBy(fault => fault.Kind)
                .ThenBy(fault => fault.TargetId, StringComparer.Ordinal)
                .Select(fault => JsonSerializer.Serialize(fault, JsonOptions))));
        var snapshotHash = HashSnapshot(camera, snapshot.ConditionScenario, faultHash);
        var eventHash = HashEvents(eventList);
        var durationTicks = snapshot.TickIndex - triggerTick;
        if (durationTicks < 0)
        {
            throw new InvalidOperationException("Completion tick precedes the camera trigger tick.");
        }

        var evidenceHash = HashEvidence(
            projectHash,
            buildIdentity,
            fixedStep.Ticks,
            camera.Id,
            result.RecipeId,
            result.AcquisitionId,
            frame.FrameId,
            frame.ContentSha256,
            inspection.InspectionId,
            inspection.Decision,
            inspection.Message,
            metrics,
            durationTicks,
            conditionHash,
            faultHash,
            snapshotHash,
            eventHash);

        return new(
            CurrentSchemaVersion,
            projectId.Trim(),
            projectName?.Trim() ?? string.Empty,
            Path.GetFullPath(projectPath),
            projectHash,
            buildIdentity.Trim(),
            fixedStep.Ticks,
            camera.Id,
            result.RecipeId,
            result.AcquisitionId,
            frame.FrameId,
            frame.ContentSha256,
            inspection.InspectionId,
            inspection.Decision,
            inspection.Message,
            metrics,
            durationTicks,
            conditionHash,
            faultHash,
            snapshotHash,
            eventHash,
            eventList,
            evidenceHash);
    }

    public bool HasValidEvidenceHash()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(ProjectId)
            || string.IsNullOrWhiteSpace(ProjectHash)
            || string.IsNullOrWhiteSpace(BuildIdentity)
            || FixedStepTicks <= 0
            || string.IsNullOrWhiteSpace(CameraId)
            || string.IsNullOrWhiteSpace(RecipeId)
            || string.IsNullOrWhiteSpace(AcquisitionId)
            || string.IsNullOrWhiteSpace(FrameId)
            || string.IsNullOrWhiteSpace(FrameHash)
            || string.IsNullOrWhiteSpace(InspectionId)
            || string.IsNullOrWhiteSpace(Message)
            || Metrics.IsDefault
            || Events.IsDefault
            || DurationTicks < 0)
        {
            return false;
        }

        if (Events.Select((item, index) => item.Order == index).Any(valid => !valid)
            || Events.Any(item => item.TickOffset < 0 || item.SimulationTimeOffsetTicks < 0)
            || Metrics.Any(metric => string.IsNullOrWhiteSpace(metric.Name) || !double.IsFinite(metric.Value))
            || Metrics.Select(metric => metric.Name).Distinct(StringComparer.Ordinal).Count() != Metrics.Length)
        {
            return false;
        }

        var eventHash = HashEvents(Events);
        var evidenceHash = HashEvidence(
            ProjectHash,
            BuildIdentity,
            FixedStepTicks,
            CameraId,
            RecipeId,
            AcquisitionId,
            FrameId,
            FrameHash,
            InspectionId,
            Decision,
            Message,
            Metrics,
            DurationTicks,
            ConditionHash,
            FaultHash,
            SnapshotHash,
            eventHash);
        return string.Equals(EventHash, eventHash, StringComparison.Ordinal)
            && string.Equals(EvidenceHash, evidenceHash, StringComparison.Ordinal);
    }

    public bool IsForContext(
        string projectId,
        string projectJson,
        string buildIdentity,
        string? cameraId,
        string? recipeId) =>
        HasValidEvidenceHash()
        && string.Equals(ProjectId, projectId, StringComparison.Ordinal)
        && string.Equals(ProjectHash, Hash(projectJson ?? string.Empty), StringComparison.Ordinal)
        && string.Equals(BuildIdentity, buildIdentity, StringComparison.Ordinal)
        && string.Equals(CameraId, cameraId, StringComparison.Ordinal)
        && string.Equals(RecipeId, recipeId, StringComparison.Ordinal);

    public DeterministicVisionExecutionComparison CompareTo(
        DeterministicVisionExecutionEvidencePackage? other)
    {
        if (other is null)
        {
            return new(false, "MissingEvidence", "The comparison evidence is missing.");
        }

        var mismatch = FirstMismatch(other);
        return mismatch is null
            ? new(true, null, null)
            : new(false, mismatch.Value.Code, mismatch.Value.Detail);
    }

    public static string SaveToJson(DeterministicVisionExecutionEvidencePackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static void SaveToJson(
        DeterministicVisionExecutionEvidencePackage package,
        string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException("Invalid Vision evidence cannot be saved.");
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, SaveToJson(package));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static DeterministicVisionExecutionEvidencePackage? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicVisionExecutionEvidencePackage>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private (string Code, string Detail)? FirstMismatch(
        DeterministicVisionExecutionEvidencePackage other)
    {
        if (SchemaVersion != other.SchemaVersion)
        {
            return ("SchemaMismatch", "Vision evidence schemas differ.");
        }
        if (!string.Equals(BuildIdentity, other.BuildIdentity, StringComparison.Ordinal))
        {
            return ("BuildMismatch", "Build identities differ.");
        }
        if (!string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal)
            || !string.Equals(ProjectHash, other.ProjectHash, StringComparison.Ordinal))
        {
            return ("ProjectMismatch", "Project identity or content differs.");
        }
        if (!string.Equals(CameraId, other.CameraId, StringComparison.Ordinal))
        {
            return ("CameraMismatch", "Camera identities differ.");
        }
        if (!string.Equals(RecipeId, other.RecipeId, StringComparison.Ordinal))
        {
            return ("RecipeMismatch", "Recipe identities differ.");
        }
        if (!string.Equals(FrameId, other.FrameId, StringComparison.Ordinal)
            || !string.Equals(FrameHash, other.FrameHash, StringComparison.Ordinal))
        {
            return ("FrameMismatch", "Frame identity or content differs.");
        }
        if (!string.Equals(InspectionId, other.InspectionId, StringComparison.Ordinal)
            || Decision != other.Decision
            || !string.Equals(Message, other.Message, StringComparison.Ordinal)
            || !Metrics.SequenceEqual(other.Metrics))
        {
            return ("InspectionMismatch", "Inspection identity, result, message, or metrics differ.");
        }
        if (!string.Equals(ConditionHash, other.ConditionHash, StringComparison.Ordinal))
        {
            return ("ConditionMismatch", "Condition state differs.");
        }
        if (!string.Equals(FaultHash, other.FaultHash, StringComparison.Ordinal))
        {
            return ("FaultMismatch", "Fault state differs.");
        }
        if (!string.Equals(SnapshotHash, other.SnapshotHash, StringComparison.Ordinal)
            || DurationTicks != other.DurationTicks)
        {
            return ("SnapshotMismatch", "Final camera snapshot or duration differs.");
        }
        if (!string.Equals(EventHash, other.EventHash, StringComparison.Ordinal))
        {
            return ("EventMismatch", "Normalized event history differs.");
        }
        if (!string.Equals(EvidenceHash, other.EvidenceHash, StringComparison.Ordinal))
        {
            return ("EvidenceMismatch", "Combined Vision evidence differs.");
        }
        return null;
    }

    private static ImmutableArray<DeterministicVisionEventEvidence> NormalizeEvents(
        IEnumerable<SimulationEvent> events)
    {
        var ordered = events.OrderBy(item => item.EventIndex).ToArray();
        if (ordered.Length == 0)
        {
            return ImmutableArray<DeterministicVisionEventEvidence>.Empty;
        }

        var firstTick = ordered[0].TickIndex;
        var firstTime = ordered[0].SimulationTime.Ticks;
        return ordered.Select((item, index) => new DeterministicVisionEventEvidence(
            index,
            item.TickIndex - firstTick,
            item.SimulationTime.Ticks - firstTime,
            item.Category,
            item.Code,
            item.Message)).ToImmutableArray();
    }

    private static void RequireEvent(
        ImmutableArray<DeterministicVisionEventEvidence> events,
        string code)
    {
        if (!events.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Required Vision event '{code}' is missing.");
        }
    }

    private static string HashSnapshot(
        VirtualCameraSnapshot camera,
        DeterministicConditionScenarioSnapshot condition,
        string faultHash)
    {
        var result = camera.Result!;
        var frame = result.FrameEvidence ?? camera.FrameEvidence!;
        var inspection = result.InspectionEvidence!;
        var metrics = string.Join(
            ";",
            inspection.Metrics.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={item.Value.ToString("R", CultureInfo.InvariantCulture)}"));
        return Hash(string.Join(
            "|",
            camera.Id,
            camera.State,
            result.AcquisitionId,
            result.RecipeId,
            frame.FrameId,
            frame.ContentSha256,
            frame.ContentLength,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            inspection.InspectionId,
            inspection.Decision,
            inspection.Message,
            metrics,
            JsonSerializer.Serialize(condition, JsonOptions),
            faultHash));
    }

    private static string HashEvents(IEnumerable<DeterministicVisionEventEvidence> events) =>
        Hash(string.Join(
            "\n",
            events.Select(item => string.Join(
                "|",
                item.Order,
                item.TickOffset,
                item.SimulationTimeOffsetTicks,
                item.Category,
                item.Code,
                item.Message))));

    private static string HashEvidence(
        string projectHash,
        string buildIdentity,
        long fixedStepTicks,
        string cameraId,
        string recipeId,
        string acquisitionId,
        string frameId,
        string frameHash,
        string inspectionId,
        PlaceholderInspectionDecision decision,
        string message,
        IEnumerable<DeterministicVisionMetricEvidence> metrics,
        long durationTicks,
        string conditionHash,
        string faultHash,
        string snapshotHash,
        string eventHash) =>
        Hash(string.Join(
            "|",
            CurrentSchemaVersion,
            projectHash,
            buildIdentity,
            fixedStepTicks,
            cameraId,
            recipeId,
            acquisitionId,
            frameId,
            frameHash,
            inspectionId,
            decision,
            message,
            string.Join(
                ";",
                metrics.Select(metric =>
                    $"{metric.Name}={metric.Value.ToString("R", CultureInfo.InvariantCulture)}")),
            durationTicks,
            conditionHash,
            faultHash,
            snapshotHash,
            eventHash));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class DeterministicVisionExecutionRecorder
{
    private readonly string _projectId;
    private readonly string _projectName;
    private readonly string _projectPath;
    private readonly string _projectJson;
    private readonly string _buildIdentity;
    private readonly TimeSpan _fixedStep;
    private readonly long _triggerTick;
    private readonly string _commandId;
    private readonly string _cameraId;
    private readonly string _recipeId;
    private readonly string _acquisitionId;
    private readonly string _frameId;
    private readonly string _inspectionId;
    private readonly List<SimulationEvent> _events = [];
    private bool _started;

    public DeterministicVisionExecutionRecorder(
        string projectId,
        string projectName,
        string projectPath,
        string projectJson,
        string buildIdentity,
        TimeSpan fixedStep,
        long triggerTick,
        string commandId,
        string cameraId,
        string recipeId,
        string acquisitionId,
        string frameId,
        string inspectionId)
    {
        _projectId = projectId;
        _projectName = projectName;
        _projectPath = projectPath;
        _projectJson = projectJson;
        _buildIdentity = buildIdentity;
        _fixedStep = fixedStep;
        _triggerTick = triggerTick;
        _commandId = commandId;
        _cameraId = cameraId;
        _recipeId = recipeId;
        _acquisitionId = acquisitionId;
        _frameId = frameId;
        _inspectionId = inspectionId;
    }

    public bool IsReady { get; private set; }

    public void RecordEvent(SimulationEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (!_started)
        {
            if (!string.Equals(runtimeEvent.Code, "CameraTriggered", StringComparison.Ordinal)
                || !string.Equals(runtimeEvent.CommandId, _commandId, StringComparison.Ordinal))
            {
                return;
            }
            _started = true;
        }

        if (IsReady)
        {
            return;
        }

        _events.Add(runtimeEvent);
        if (string.Equals(runtimeEvent.Code, "VisionResultReady", StringComparison.Ordinal)
            && runtimeEvent.Message.Contains(_acquisitionId, StringComparison.Ordinal)
            && runtimeEvent.Message.Contains(_inspectionId, StringComparison.Ordinal))
        {
            IsReady = true;
        }
    }

    public bool CanComplete(SimulationSnapshot snapshot)
    {
        var camera = snapshot.Cameras.FirstOrDefault(item =>
            string.Equals(item.Id, _cameraId, StringComparison.Ordinal));
        var result = camera?.Result;
        return IsReady
            && camera?.State == VirtualCameraState.FrameReady
            && string.Equals(result?.AcquisitionId, _acquisitionId, StringComparison.Ordinal)
            && string.Equals(result?.RecipeId, _recipeId, StringComparison.Ordinal)
            && string.Equals(result?.FrameEvidence?.FrameId ?? camera.FrameEvidence?.FrameId, _frameId, StringComparison.Ordinal)
            && string.Equals(result?.InspectionEvidence?.InspectionId, _inspectionId, StringComparison.Ordinal);
    }

    public DeterministicVisionExecutionEvidencePackage Complete(SimulationSnapshot snapshot)
    {
        if (!CanComplete(snapshot))
        {
            throw new InvalidOperationException("Vision execution evidence is not complete.");
        }

        var camera = snapshot.Cameras.Single(item =>
            string.Equals(item.Id, _cameraId, StringComparison.Ordinal));
        return DeterministicVisionExecutionEvidencePackage.Create(
            _projectId,
            _projectName,
            _projectPath,
            _projectJson,
            _buildIdentity,
            _fixedStep,
            _triggerTick,
            snapshot,
            camera,
            _events);
    }
}
