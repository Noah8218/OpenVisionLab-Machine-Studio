using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public class DeterministicVirtualCameraTests
{
    [Theory]
    [InlineData(0, 1, "exposureTicks")]
    [InlineData(-1, 1, "exposureTicks")]
    [InlineData(1, 0, "transferTicks")]
    [InlineData(1, -1, "transferTicks")]
    public void Configuration_RequiresPositiveTickCounts(
        int exposureTicks,
        int transferTicks,
        string expectedParameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VirtualCameraConfiguration(
                "camera-top",
                "Top Camera",
                exposureTicks,
                transferTicks,
                PlaceholderInspectionDecision.Pass));

        Assert.Equal(expectedParameter, error.ParamName);
    }

    [Fact]
    public void Configuration_RejectsUndefinedPlaceholderDecision()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VirtualCameraConfiguration(
                "camera-top",
                "Top Camera",
                1,
                1,
                (PlaceholderInspectionDecision)99));

        Assert.Equal("placeholderDecision", error.ParamName);
    }

    [Fact]
    public void TriggerAndTick_UseExactExposureAndTransferBoundaries()
    {
        var camera = CreateCamera(exposureTicks: 2, transferTicks: 3);

        var trigger = camera.Trigger("recipe.pass");

        Assert.True(trigger.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.None, trigger.ErrorCode);
        Assert.Equal("camera-top/frame/00000001", trigger.AcquisitionId);
        Assert.Equal(1, trigger.AcquisitionOrdinal);

        var exposureTick1 = camera.Tick();
        Assert.Equal(VirtualCameraState.Exposing, exposureTick1.Snapshot.State);
        Assert.Equal(1, exposureTick1.Snapshot.ExposureTicksRemaining);
        Assert.Equal(VirtualCameraTickTransition.None, exposureTick1.Transition);

        var exposureTick2 = camera.Tick();
        Assert.Equal(VirtualCameraState.Transferring, exposureTick2.Snapshot.State);
        Assert.Equal(0, exposureTick2.Snapshot.ExposureTicksRemaining);
        Assert.Equal(3, exposureTick2.Snapshot.TransferTicksRemaining);
        Assert.Equal(VirtualCameraTickTransition.ExposureCompleted, exposureTick2.Transition);

        Assert.Equal(2, camera.Tick().Snapshot.TransferTicksRemaining);
        Assert.Equal(1, camera.Tick().Snapshot.TransferTicksRemaining);

        var frameReady = camera.Tick();
        Assert.Equal(VirtualCameraState.FrameReady, frameReady.Snapshot.State);
        Assert.Equal(VirtualCameraTickTransition.FrameReady, frameReady.Transition);
        Assert.Equal(0, frameReady.Snapshot.TransferTicksRemaining);
        Assert.NotNull(frameReady.CompletedAcquisition);
        Assert.Equal("camera-top/frame/00000001", frameReady.CompletedAcquisition!.AcquisitionId);
        Assert.Equal("recipe.pass", frameReady.CompletedAcquisition.RecipeId);
        Assert.Equal(PlaceholderInspectionDecision.Pass, frameReady.CompletedAcquisition.Decision);
        Assert.Equal(frameReady.CompletedAcquisition, frameReady.Snapshot.Result);
        Assert.Equal("recipe.pass", frameReady.Snapshot.CurrentRecipeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Trigger_RejectsMissingRecipeIdWithoutChangingState(string? recipeId)
    {
        var camera = CreateCamera(exposureTicks: 1, transferTicks: 1);
        var initial = camera.CaptureSnapshot();

        var rejection = camera.Trigger(recipeId);

        Assert.False(rejection.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.RecipeIdRequired, rejection.ErrorCode);
        Assert.Null(rejection.AcquisitionId);
        Assert.Equal(initial, camera.CaptureSnapshot());
    }

    [Fact]
    public void Trigger_RejectsBusyCameraWithoutChangingActiveAcquisition()
    {
        var camera = CreateCamera(exposureTicks: 2, transferTicks: 2);
        camera.Trigger("recipe.pass");

        var exposingSnapshot = camera.CaptureSnapshot();
        var exposingRejection = camera.Trigger("recipe.other");

        Assert.False(exposingRejection.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.CameraBusy, exposingRejection.ErrorCode);
        Assert.Null(exposingRejection.AcquisitionId);
        Assert.Equal(exposingSnapshot, camera.CaptureSnapshot());

        camera.Tick();
        camera.Tick();
        var transferringSnapshot = camera.CaptureSnapshot();
        var transferringRejection = camera.Trigger("recipe.other");

        Assert.False(transferringRejection.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.CameraBusy, transferringRejection.ErrorCode);
        Assert.Equal(transferringSnapshot, camera.CaptureSnapshot());
    }

    [Fact]
    public void FrameReady_AllowsRetriggerWithNextDeterministicAcquisitionId()
    {
        var camera = CreateCamera(exposureTicks: 1, transferTicks: 1);
        camera.Trigger("recipe.pass");
        camera.Tick();
        camera.Tick();

        var retrigger = camera.Trigger("recipe.fail");
        var snapshot = camera.CaptureSnapshot();

        Assert.True(retrigger.IsAccepted);
        Assert.Equal("camera-top/frame/00000002", retrigger.AcquisitionId);
        Assert.Equal(2, retrigger.AcquisitionOrdinal);
        Assert.Equal(VirtualCameraState.Exposing, snapshot.State);
        Assert.Equal("camera-top/frame/00000002", snapshot.CurrentAcquisitionId);
        Assert.Equal("recipe.fail", snapshot.CurrentRecipeId);
        Assert.Null(snapshot.Result);
    }

    [Fact]
    public void Fault_RejectsTriggerUntilResetAndResetRestoresOrdinalZero()
    {
        var camera = CreateCamera(exposureTicks: 1, transferTicks: 1);
        camera.Trigger("recipe.pass");

        var faulted = camera.Fault();
        var rejected = camera.Trigger("recipe.pass");

        Assert.Equal(VirtualCameraState.Faulted, faulted.State);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.CameraFaulted, rejected.ErrorCode);

        camera.Reset();
        var reset = camera.CaptureSnapshot();
        Assert.Equal(VirtualCameraState.Idle, reset.State);
        Assert.Equal(0, reset.AcquisitionOrdinal);
        Assert.Null(reset.CurrentAcquisitionId);
        Assert.Null(reset.CurrentRecipeId);
        Assert.Null(reset.Result);

        var afterReset = camera.Trigger("recipe.pass");
        Assert.Equal("camera-top/frame/00000001", afterReset.AcquisitionId);
    }

    [Fact]
    public void SameCommandStream_ProducesEqualSnapshotsAndResults()
    {
        var first = CreateCamera(exposureTicks: 2, transferTicks: 2);
        var second = CreateCamera(exposureTicks: 2, transferTicks: 2);
        var evidence = new VirtualCameraFrameEvidence(
            "camera-top/frame/00000001",
            "assets/presence-check.pgm",
            new string('A', 64),
            42,
            16,
            12,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            "inspection/sha256/abc",
            "camera-top/frame/00000001",
            "camera-top",
            "recipe.pass",
            "camera-top/frame/00000001",
            PlaceholderInspectionDecision.Fail,
            "Deterministic inspection completed.",
            new Dictionary<string, double> { ["Z"] = 2, ["A"] = 1 });

        Assert.Equal(
            first.Trigger("recipe.pass", evidence, inspection),
            second.Trigger("recipe.pass", evidence, inspection));
        Assert.Equal(first.Tick(), second.Tick());
        Assert.Equal(first.Tick(), second.Tick());
        Assert.Equal(first.Tick(), second.Tick());
        Assert.Equal(first.Tick(), second.Tick());
        Assert.Equal(first.CaptureSnapshot(), second.CaptureSnapshot());
        Assert.Equal(
            inspection,
            Assert.IsType<VirtualCameraAcquisitionResult>(first.CaptureSnapshot().Result)
                .InspectionEvidence);
        Assert.Equal(
            PlaceholderInspectionDecision.Fail,
            first.CaptureSnapshot().Result!.Decision);
    }

    [Fact]
    public void Trigger_RejectsMismatchedInspectionEvidenceWithoutChangingState()
    {
        var camera = CreateCamera(exposureTicks: 1, transferTicks: 1);
        var initial = camera.CaptureSnapshot();
        var frame = new VirtualCameraFrameEvidence(
            "camera-top/frame/00000001",
            "assets/presence-check.pgm",
            new string('A', 64),
            42,
            16,
            12,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            "inspection/sha256/abc",
            "camera-top/frame/00000002",
            "camera-top",
            "recipe.pass",
            frame.FrameId,
            PlaceholderInspectionDecision.Pass,
            "Mismatched acquisition.");

        var result = camera.Trigger("recipe.pass", frame, inspection);

        Assert.False(result.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.InspectionEvidenceInvalid, result.ErrorCode);
        Assert.Equal(initial, camera.CaptureSnapshot());
    }

    [Fact]
    public void InspectionEvidence_CopiesSortedFiniteMetrics()
    {
        var metrics = new Dictionary<string, double> { ["Z"] = 2, ["A"] = 1 };
        var evidence = new VirtualCameraInspectionEvidence(
            "inspection/sha256/abc",
            "camera-top/frame/00000001",
            "camera-top",
            "recipe.pass",
            "camera-top/frame/00000001",
            PlaceholderInspectionDecision.Pass,
            "Complete.",
            metrics);

        metrics["A"] = 99;

        Assert.Equal(["A", "Z"], evidence.Metrics.Keys);
        Assert.Equal(1, evidence.Metrics["A"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualCameraInspectionEvidence(
            "inspection/sha256/abc",
            "camera-top/frame/00000001",
            "camera-top",
            "recipe.pass",
            "camera-top/frame/00000001",
            PlaceholderInspectionDecision.Pass,
            "Complete.",
            new Dictionary<string, double> { ["Score"] = double.NaN }));
    }

    [Fact]
    public void Trigger_RejectsMismatchedFrameEvidenceWithoutChangingState()
    {
        var camera = CreateCamera(exposureTicks: 1, transferTicks: 1);
        var initial = camera.CaptureSnapshot();
        var evidence = new VirtualCameraFrameEvidence(
            "camera-top/frame/00000002",
            "assets/presence-check.pgm",
            new string('A', 64),
            42,
            16,
            12,
            "Mono8");

        var result = camera.Trigger("recipe.pass", evidence);

        Assert.False(result.IsAccepted);
        Assert.Equal(VirtualCameraTriggerErrorCode.FrameEvidenceInvalid, result.ErrorCode);
        Assert.Equal(initial, camera.CaptureSnapshot());
    }

    private static DeterministicVirtualCamera CreateCamera(
        int exposureTicks,
        int transferTicks) =>
        new(
            new VirtualCameraConfiguration(
                "camera-top",
                "Top Camera",
                exposureTicks,
                transferTicks,
                PlaceholderInspectionDecision.Pass));
}
