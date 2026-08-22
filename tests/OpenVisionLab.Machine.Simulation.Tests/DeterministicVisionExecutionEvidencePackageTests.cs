using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicVisionExecutionEvidencePackageTests
{
    private const string ProjectId = "vision-project";
    private const string ProjectJson = "{\"id\":\"vision-project\"}";
    private const string CameraId = "camera.top";
    private const string RecipeId = "presence-check";
    private const string AcquisitionId = "camera.top/frame/00000001";
    private const string FrameId = "frame-001";
    private const string InspectionId = "inspection-001";
    private const string CommandId = "command-001";
    private const string FrameHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void Create_NormalizesAbsoluteEventIndicesForRepeatComparison()
    {
        var first = CreatePackage(100, 20, 50);
        var repeated = CreatePackage(900, 220, 250);

        Assert.True(first.HasValidEvidenceHash());
        Assert.Equal(first.EventHash, repeated.EventHash);
        Assert.Equal(first.EvidenceHash, repeated.EvidenceHash);
        Assert.True(first.CompareTo(repeated).IsMatch);
    }

    [Fact]
    public void CompareTo_ReportsFirstContextMismatch()
    {
        var expected = CreatePackage(100, 20, 50);
        var changedRecipe = expected with
        {
            RecipeId = "edge-check"
        };

        var comparison = expected.CompareTo(changedRecipe);

        Assert.False(comparison.IsMatch);
        Assert.Equal("RecipeMismatch", comparison.MismatchCode);
    }

    [Fact]
    public void SaveLoad_RoundTripsValidEvidenceAndRejectsTampering()
    {
        var package = CreatePackage(100, 20, 50);
        var artifactRoot = Directory.Exists("D:\\")
            ? @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\unit"
            : Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-Studio", "unit");
        Directory.CreateDirectory(artifactRoot);
        var path = Path.Combine(artifactRoot, $"vision-{Guid.NewGuid():N}.json");
        try
        {
            DeterministicVisionExecutionEvidencePackage.SaveToJson(package, path);
            var restored = DeterministicVisionExecutionEvidencePackage.LoadFromJson(path);

            Assert.NotNull(restored);
            Assert.Equal(package.EvidenceHash, restored.EvidenceHash);
            Assert.Equal(package.Metrics.ToArray(), restored.Metrics.ToArray());
            Assert.Equal(package.Events.ToArray(), restored.Events.ToArray());
            Assert.True(restored!.IsForContext(
                ProjectId,
                ProjectJson,
                "0.1.0-test+abc123",
                CameraId,
                RecipeId));
            Assert.False((restored with { FrameHash = new string('0', 64) }).HasValidEvidenceHash());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Recorder_IgnoresUnrelatedEventsAndCompletesCorrelatedExecution()
    {
        var recorder = new DeterministicVisionExecutionRecorder(
            ProjectId,
            "Vision Project",
            ProjectPath(),
            ProjectJson,
            "0.1.0-test+abc123",
            FixedStep,
            20,
            CommandId,
            CameraId,
            RecipeId,
            AcquisitionId,
            FrameId,
            InspectionId);
        recorder.RecordEvent(Event(1, 20, "Camera", "CameraTriggered", "unrelated", "other-command"));
        foreach (var runtimeEvent in Events(100, 20))
        {
            recorder.RecordEvent(runtimeEvent);
        }

        var snapshot = Snapshot(30);
        Assert.True(recorder.IsReady);
        Assert.True(recorder.CanComplete(snapshot));
        Assert.True(recorder.Complete(snapshot).HasValidEvidenceHash());
    }

    private static DeterministicVisionExecutionEvidencePackage CreatePackage(
        long firstEventIndex,
        long triggerTick,
        long absoluteTimeTicks) =>
        DeterministicVisionExecutionEvidencePackage.Create(
            ProjectId,
            "Vision Project",
            ProjectPath(),
            ProjectJson,
            "0.1.0-test+abc123",
            FixedStep,
            triggerTick,
            Snapshot(triggerTick + 10),
            Camera(),
            Events(firstEventIndex, triggerTick, absoluteTimeTicks));

    private static SimulationSnapshot Snapshot(long tick) => new(
        TimeSpan.FromTicks(tick * FixedStep.Ticks),
        tick,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [],
        [],
        [Camera()]);

    private static VirtualCameraSnapshot Camera()
    {
        var frame = new VirtualCameraFrameEvidence(
            FrameId,
            "images/source.png",
            FrameHash,
            42,
            16,
            12,
            "Gray8");
        var inspection = new VirtualCameraInspectionEvidence(
            InspectionId,
            AcquisitionId,
            CameraId,
            RecipeId,
            FrameId,
            PlaceholderInspectionDecision.Pass,
            "Deterministic inspection completed.",
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["ContentLengthBytes"] = 42,
                ["PixelCount"] = 192
            });
        var result = new VirtualCameraAcquisitionResult(
            AcquisitionId,
            CameraId,
            RecipeId,
            1,
            PlaceholderInspectionDecision.Pass,
            frame,
            inspection);
        return new(
            CameraId,
            "Top Camera",
            VirtualCameraState.FrameReady,
            1,
            AcquisitionId,
            RecipeId,
            0,
            0,
            result,
            frame);
    }

    private static IReadOnlyList<SimulationEvent> Events(
        long firstEventIndex,
        long triggerTick,
        long absoluteTimeTicks = 50) =>
    [
        Event(firstEventIndex, triggerTick, "Camera", "CameraTriggered",
            $"{CameraId} started {AcquisitionId} with inspection {InspectionId}.", CommandId, absoluteTimeTicks),
        Event(firstEventIndex + 1, triggerTick + 4, "Camera", "CameraExposureCompleted",
            $"{CameraId} exposure completed for {AcquisitionId}.", null, absoluteTimeTicks + (4 * FixedStep.Ticks)),
        Event(firstEventIndex + 2, triggerTick + 10, "Camera", "CameraFrameReady",
            $"{CameraId} frame {AcquisitionId} is ready.", null, absoluteTimeTicks + (10 * FixedStep.Ticks)),
        Event(firstEventIndex + 3, triggerTick + 10, "Vision", "VisionResultReady",
            $"{AcquisitionId} inspection {InspectionId} result = PASS.", null, absoluteTimeTicks + (10 * FixedStep.Ticks))
    ];

    private static SimulationEvent Event(
        long eventIndex,
        long tick,
        string category,
        string code,
        string message,
        string? commandId = null,
        long? simulationTimeTicks = null) =>
        new(
            eventIndex,
            tick,
            TimeSpan.FromTicks(simulationTimeTicks ?? tick * FixedStep.Ticks),
            category,
            code,
            message,
            commandId);

    private static string ProjectPath() => Path.GetFullPath("VisionProject.ovmachine");
}
