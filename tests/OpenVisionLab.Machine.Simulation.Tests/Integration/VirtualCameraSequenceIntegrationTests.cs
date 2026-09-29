using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class VirtualCameraSequenceIntegrationTests
{
    private const string CameraId = "camera.top";
    private const string SecondCameraId = "camera.side";
    private const string RecipeId = "presence-check";
    private const string WorkpieceComponentId = "workpiece.top";
    private const string EmptyWorkpieceAComponentId = "workpiece.empty-a";
    private const string EmptyWorkpieceBComponentId = "workpiece.empty-b";
    private const string WorkpieceInstanceId = "run-0001/WP-001";
    private const string ExpectedAcquisitionId = "camera.top/frame/00000001";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task PassPath_PreservesExactTimingCorrelationAndAxisCameraSequenceEventOrder()
    {
        using var engine = await CreateConfiguredEngineAsync(PlaceholderInspectionDecision.Pass);
        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));

        Assert.True(start.IsAccepted, start.Detail);
        var completed = await StepUntilCompletedAsync(engine);

        var sequence = Assert.Single(completed.Sequences);
        var camera = Assert.Single(completed.Cameras);
        var result = Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result);
        var workpiece = Assert.Single(
            completed.LayoutComponents,
            item => item.Id == WorkpieceComponentId);
        Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status);
        Assert.Equal("pass-complete", sequence.CurrentStepId);
        Assert.Null(sequence.LastError);
        Assert.Equal(VirtualCameraState.FrameReady, camera.State);
        Assert.Equal(ExpectedAcquisitionId, camera.CurrentAcquisitionId);
        Assert.Equal(RecipeId, camera.CurrentRecipeId);
        Assert.Equal(ExpectedAcquisitionId, result.AcquisitionId);
        Assert.Equal(CameraId, result.CameraId);
        Assert.Equal(RecipeId, result.RecipeId);
        Assert.Equal(WorkpieceComponentId, result.WorkpieceComponentId);
        Assert.Equal(WorkpieceInstanceId, result.WorkpieceInstanceId);
        Assert.Equal(WorkpieceInstanceId, workpiece.WorkpieceInstanceId);
        Assert.Equal(
            "run-0001/WP-002",
            GetWorkpiece(completed, "workpiece.z").WorkpieceInstanceId);
        Assert.Equal(1, result.AcquisitionOrdinal);
        Assert.Equal(PlaceholderInspectionDecision.Pass, result.Decision);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var trigger = Assert.Single(events, item => item.Code == "CameraTriggered");
        var exposureCompleted = Assert.Single(events, item => item.Code == "CameraExposureCompleted");
        var frameReady = Assert.Single(events, item => item.Code == "CameraFrameReady");
        var visionReady = Assert.Single(events, item => item.Code == "VisionResultReady");
        var axisReached = Assert.Single(events, item => item.Code == "AxisTargetReached");
        var resultTransition = Assert.Single(
            events,
            item => item.Code == "SequenceStepTransition"
                && item.Message.Contains("wait-vision-result -> pass-complete", StringComparison.Ordinal));

        Assert.Equal(2, trigger.TickIndex);
        Assert.Equal(trigger.TickIndex + 4, exposureCompleted.TickIndex);
        Assert.Equal(trigger.SimulationTime + (FixedStep * 4), exposureCompleted.SimulationTime);
        Assert.Equal(trigger.TickIndex + 10, frameReady.TickIndex);
        Assert.Equal(trigger.SimulationTime + (FixedStep * 10), frameReady.SimulationTime);
        Assert.Equal(frameReady.TickIndex, visionReady.TickIndex);
        Assert.Equal(frameReady.TickIndex, axisReached.TickIndex);
        Assert.Equal(frameReady.TickIndex, resultTransition.TickIndex);
        Assert.True(axisReached.EventIndex < frameReady.EventIndex);
        Assert.True(frameReady.EventIndex < visionReady.EventIndex);
        Assert.True(visionReady.EventIndex < resultTransition.EventIndex);
        Assert.Contains(ExpectedAcquisitionId, trigger.Message, StringComparison.Ordinal);
        Assert.Contains(RecipeId, trigger.Message, StringComparison.Ordinal);
        Assert.Contains("sequence 'inspection-cycle'", trigger.Message, StringComparison.Ordinal);
        Assert.Contains("step 'trigger-camera'", trigger.Message, StringComparison.Ordinal);
        Assert.Contains(WorkpieceComponentId, trigger.Message, StringComparison.Ordinal);
        Assert.Contains(WorkpieceInstanceId, trigger.Message, StringComparison.Ordinal);
        Assert.Contains(ExpectedAcquisitionId, frameReady.Message, StringComparison.Ordinal);
        Assert.Contains(RecipeId, frameReady.Message, StringComparison.Ordinal);
        Assert.Contains(WorkpieceComponentId, frameReady.Message, StringComparison.Ordinal);
        Assert.Contains(WorkpieceInstanceId, frameReady.Message, StringComparison.Ordinal);
        Assert.Contains(WorkpieceComponentId, visionReady.Message, StringComparison.Ordinal);
        Assert.Contains(WorkpieceInstanceId, visionReady.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workpiece.z", "run-0001/WP-002")]
    [InlineData("workpiece.top", WorkpieceInstanceId)]
    public async Task MultipleCameras_CorrelateResultsToSeparateOrSharedActiveWorkpieces(
        string secondWorkpieceComponentId,
        string expectedSecondWorkpieceInstanceId)
    {
        var sequence = new SequenceDefinition
        {
            Id = "two-position-inspection",
            Name = "Two Position Inspection",
            Steps =
            {
                Step(
                    "trigger-top",
                    SequenceStepAction.TriggerCamera,
                    CameraId,
                    RecipeId,
                    "wait-top",
                    WorkpieceComponentId),
                new SequenceStepDefinition
                {
                    Id = "wait-top",
                    Name = "Wait Top",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = CameraId,
                    TimeoutMs = 500,
                    NextStepId = "trigger-side",
                    FailureStepId = "complete-top-failed"
                },
                Step(
                    "trigger-side",
                    SequenceStepAction.TriggerCamera,
                    SecondCameraId,
                    RecipeId,
                    "wait-side",
                    secondWorkpieceComponentId),
                new SequenceStepDefinition
                {
                    Id = "wait-side",
                    Name = "Wait Side",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = SecondCameraId,
                    TimeoutMs = 500,
                    NextStepId = "complete-pass",
                    FailureStepId = "complete-side-failed"
                },
                Step("complete-pass", SequenceStepAction.Complete, string.Empty, string.Empty),
                Step("complete-top-failed", SequenceStepAction.Complete, string.Empty, string.Empty),
                Step("complete-side-failed", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        using var engine = await CreateConfiguredEngineAsync(
            PlaceholderInspectionDecision.Pass,
            sequenceOverride: sequence,
            includeSecondCamera: true);

        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand(sequence.Id));
        Assert.True(start.IsAccepted, start.Detail);

        var completed = await StepUntilCompletedAsync(engine);
        var sequenceSnapshot = Assert.Single(completed.Sequences);
        Assert.Equal(SequenceExecutionStatus.Completed, sequenceSnapshot.Status);
        Assert.Equal("complete-side-failed", sequenceSnapshot.CurrentStepId);

        var topCamera = Assert.Single(completed.Cameras, item => item.Id == CameraId);
        var topResult = Assert.IsType<VirtualCameraAcquisitionResult>(topCamera.Result);
        Assert.Equal(WorkpieceComponentId, topResult.WorkpieceComponentId);
        Assert.Equal("run-0001/WP-001", topResult.WorkpieceInstanceId);
        Assert.Equal(PlaceholderInspectionDecision.Pass, topResult.Decision);

        var sideCamera = Assert.Single(completed.Cameras, item => item.Id == SecondCameraId);
        var sideResult = Assert.IsType<VirtualCameraAcquisitionResult>(sideCamera.Result);
        Assert.Equal(secondWorkpieceComponentId, sideResult.WorkpieceComponentId);
        Assert.Equal(expectedSecondWorkpieceInstanceId, sideResult.WorkpieceInstanceId);
        Assert.Equal(PlaceholderInspectionDecision.Fail, sideResult.Decision);
        Assert.NotEqual(topResult.AcquisitionId, sideResult.AcquisitionId);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item => item.Code == "CameraTriggered"
            && item.Message.Contains(CameraId, StringComparison.Ordinal)
            && item.Message.Contains(WorkpieceComponentId, StringComparison.Ordinal)
            && item.Message.Contains("run-0001/WP-001", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Code == "VisionResultReady"
            && item.Message.Contains(SecondCameraId, StringComparison.Ordinal)
            && item.Message.Contains(secondWorkpieceComponentId, StringComparison.Ordinal)
            && item.Message.Contains(expectedSecondWorkpieceInstanceId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedStartDoesNotAllocateIdentityAndResetStartsANewWorkpieceRun()
    {
        using var engine = await CreateConfiguredEngineAsync(PlaceholderInspectionDecision.Pass);
        var workpiece = Assert.Single(
            engine.CurrentSnapshot.LayoutComponents,
            item => item.Id == WorkpieceComponentId);
        Assert.Null(workpiece.WorkpieceInstanceId);

        var rejectedStart = await engine.EnqueueCommandAsync(new StartSequenceCommand("missing-sequence"));
        Assert.False(rejectedStart.IsAccepted);
        Assert.Null(GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);

        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        Assert.True(start.IsAccepted, start.Detail);
        Assert.Equal(
            WorkpieceInstanceId,
            GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);
        Assert.Equal(
            "run-0001/WP-002",
            GetWorkpiece(engine.CurrentSnapshot, "workpiece.z").WorkpieceInstanceId);

        var duplicateStart = await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        Assert.False(duplicateStart.IsAccepted);
        Assert.Equal(
            WorkpieceInstanceId,
            GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Null(GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);
        Assert.Null(GetWorkpiece(engine.CurrentSnapshot, "workpiece.z").WorkpieceInstanceId);

        var retryStart = await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        Assert.True(retryStart.IsAccepted, retryStart.Detail);
        const string nextWorkpieceInstanceId = "run-0002/WP-001";
        Assert.Equal(
            nextWorkpieceInstanceId,
            GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);
        Assert.Equal(
            "run-0002/WP-002",
            GetWorkpiece(engine.CurrentSnapshot, "workpiece.z").WorkpieceInstanceId);

        var completed = await StepUntilCompletedAsync(engine);
        var camera = Assert.Single(completed.Cameras);
        Assert.Equal(
            nextWorkpieceInstanceId,
            Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result).WorkpieceInstanceId);
    }

    [Fact]
    public async Task WorkpieceInstanceIdentitySurvivesConveyorMovement()
    {
        using var engine = await CreateConfiguredEngineAsync(PlaceholderInspectionDecision.Pass);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"))).IsAccepted);
        var before = GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId);
        var otherBefore = GetWorkpiece(engine.CurrentSnapshot, "workpiece.z");
        Assert.Equal(WorkpieceInstanceId, before.WorkpieceInstanceId);
        Assert.Equal("run-0001/WP-002", otherBefore.WorkpieceInstanceId);

        await StepAsync(engine);

        var after = GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId);
        var otherAfter = GetWorkpiece(engine.CurrentSnapshot, "workpiece.z");
        Assert.NotEqual(before.X, after.X);
        Assert.NotEqual(otherBefore.X, otherAfter.X);
        Assert.Equal(WorkpieceInstanceId, after.WorkpieceInstanceId);
        Assert.Equal("run-0001/WP-002", otherAfter.WorkpieceInstanceId);
    }

    [Fact]
    public async Task AutomaticRunStartAllocatesWorkpieceIdentities()
    {
        using var engine = await CreateConfiguredEngineAsync(
            PlaceholderInspectionDecision.Pass,
            new AutomaticRunConfiguration("inspection-cycle", null, true, Repeat: false, RepeatDelayMilliseconds: 0));
        var start = await engine.EnqueueCommandAsync(new StartAutomaticRunCommand(beginRealTime: false));

        Assert.True(start.IsAccepted, start.Detail);
        Assert.Equal(WorkpieceInstanceId, GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);
        Assert.Equal("run-0001/WP-002", GetWorkpiece(engine.CurrentSnapshot, "workpiece.z").WorkpieceInstanceId);

        var completed = await StepUntilCompletedAsync(engine);
        Assert.Equal(
            WorkpieceInstanceId,
            Assert.IsType<VirtualCameraAcquisitionResult>(Assert.Single(completed.Cameras).Result).WorkpieceInstanceId);
    }

    [Fact]
    public async Task RetryKeepsIdentityWithinFaultedRunAndAllocatesNewIdentityAfterReset()
    {
        using var engine = await CreateConfiguredEngineAsync(
            PlaceholderInspectionDecision.Pass,
            includeRetrySequence: true);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("fault-cycle"))).IsAccepted);

        await StepAsync(engine);
        Assert.Equal(SequenceExecutionStatus.Faulted, Assert.Single(
            engine.CurrentSnapshot.Sequences,
            item => item.SequenceId == "fault-cycle").Status);
        var firstId = "run-0001/WP-001";
        Assert.Equal(firstId, GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);

        var retry = await engine.EnqueueCommandAsync(new RetrySequenceCommand("fault-cycle"));
        Assert.True(retry.IsAccepted, retry.Detail);
        Assert.Equal(firstId, GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);

        await StepAsync(engine);
        Assert.Equal(SequenceExecutionStatus.Faulted, Assert.Single(
            engine.CurrentSnapshot.Sequences,
            item => item.SequenceId == "fault-cycle").Status);
        var retryAgain = await engine.EnqueueCommandAsync(new RetrySequenceCommand("fault-cycle"));
        Assert.True(retryAgain.IsAccepted, retryAgain.Detail);
        Assert.Equal(firstId, GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);

        await StepAsync(engine);
        Assert.Equal(SequenceExecutionStatus.Faulted, Assert.Single(
            engine.CurrentSnapshot.Sequences,
            item => item.SequenceId == "fault-cycle").Status);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Null(GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);

        var retryAfterReset = await engine.EnqueueCommandAsync(new RetrySequenceCommand("fault-cycle"));
        Assert.True(retryAfterReset.IsAccepted, retryAfterReset.Detail);
        Assert.Equal("run-0002/WP-001", GetWorkpiece(engine.CurrentSnapshot, WorkpieceComponentId).WorkpieceInstanceId);
        Assert.Equal("run-0002/WP-002", GetWorkpiece(engine.CurrentSnapshot, "workpiece.z").WorkpieceInstanceId);
    }

    [Fact]
    public async Task MissingCameraWorkpieceRoutesToFeedAndRetriesWithItsNewInstance()
    {
        var definition = new SequenceDefinition
        {
            Id = "camera-empty-recovery",
            Name = "Camera empty recovery",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "trigger-empty-position",
                    Name = "Trigger empty position",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = CameraId,
                    Parameter = RecipeId,
                    WorkpieceComponentId = EmptyWorkpieceAComponentId,
                    NextStepId = "complete",
                    ErrorStepId = "feed-position"
                },
                new SequenceStepDefinition
                {
                    Id = "feed-position",
                    Name = "Feed position",
                    Action = SequenceStepAction.FeedWorkpiece,
                    WorkpieceComponentId = EmptyWorkpieceAComponentId,
                    NextStepId = "trigger-fed-workpiece"
                },
                new SequenceStepDefinition
                {
                    Id = "trigger-fed-workpiece",
                    Name = "Trigger fed workpiece",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = CameraId,
                    Parameter = RecipeId,
                    WorkpieceComponentId = EmptyWorkpieceAComponentId,
                    NextStepId = "wait-result"
                },
                new SequenceStepDefinition
                {
                    Id = "wait-result",
                    Name = "Wait result",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = CameraId,
                    TimeoutMs = 500,
                    NextStepId = "complete",
                    FailureStepId = "complete"
                },
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        using var engine = await CreateConfiguredEngineAsync(
            PlaceholderInspectionDecision.Pass,
            sequenceOverride: definition,
            includeEmptyWorkpieces: true);

        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand(definition.Id));
        Assert.True(start.IsAccepted, start.Detail);
        SimulationSnapshot completed = await StepUntilCompletedAsync(engine);

        var sequence = Assert.Single(completed.Sequences);
        Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status);
        Assert.Equal("complete", sequence.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.CameraTriggerFailed, sequence.LastError!.Code);

        var camera = Assert.Single(completed.Cameras);
        var result = Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result);
        var workpiece = GetWorkpiece(completed, EmptyWorkpieceAComponentId);
        Assert.True(workpiece.IsWorkpiecePresent);
        Assert.Equal("run-0001/WP-003", workpiece.WorkpieceInstanceId);
        Assert.Equal(ExpectedAcquisitionId, camera.CurrentAcquisitionId);
        Assert.Equal(ExpectedAcquisitionId, result.AcquisitionId);
        Assert.Equal(EmptyWorkpieceAComponentId, result.WorkpieceComponentId);
        Assert.Equal(workpiece.WorkpieceInstanceId, result.WorkpieceInstanceId);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var trigger = Assert.Single(events, item => item.Code == "CameraTriggered");
        Assert.Contains(EmptyWorkpieceAComponentId, trigger.Message, StringComparison.Ordinal);
        Assert.Contains("run-0001/WP-003", trigger.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeedAndEject_RejectsOccupiedFeedRoutesRecoveryAndAllocatesOnlyOnSuccess()
    {
        var definition = new SequenceDefinition
        {
            Id = "feed-eject-cycle",
            Name = "Feed and eject cycle",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "eject-empty",
                    Action = SequenceStepAction.EjectWorkpiece,
                    WorkpieceComponentId = EmptyWorkpieceBComponentId,
                    NextStepId = "complete",
                    ErrorStepId = "feed-a"
                },
                new SequenceStepDefinition
                {
                    Id = "feed-a",
                    Action = SequenceStepAction.FeedWorkpiece,
                    WorkpieceComponentId = EmptyWorkpieceAComponentId,
                    NextStepId = "feed-a-again"
                },
                new SequenceStepDefinition
                {
                    Id = "feed-a-again",
                    Action = SequenceStepAction.FeedWorkpiece,
                    WorkpieceComponentId = EmptyWorkpieceAComponentId,
                    NextStepId = "complete",
                    ErrorStepId = "recover"
                },
                new SequenceStepDefinition
                {
                    Id = "recover",
                    Action = SequenceStepAction.EjectWorkpiece,
                    WorkpieceComponentId = EmptyWorkpieceAComponentId,
                    NextStepId = "feed-b"
                },
                new SequenceStepDefinition
                {
                    Id = "feed-b",
                    Action = SequenceStepAction.FeedWorkpiece,
                    WorkpieceComponentId = EmptyWorkpieceBComponentId,
                    NextStepId = "complete"
                },
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        using var engine = await CreateConfiguredEngineAsync(
            PlaceholderInspectionDecision.Pass,
            sequenceOverride: definition,
            includeEmptyWorkpieces: true);

        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand("feed-eject-cycle"));
        Assert.True(start.IsAccepted, start.Detail);
        SimulationSnapshot completed = await StepUntilCompletedAsync(engine);

        var sequence = Assert.Single(completed.Sequences);
        Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status);
        Assert.Equal("complete", sequence.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.WorkpieceOperationFailed, sequence.LastError!.Code);
        Assert.False(GetWorkpiece(completed, EmptyWorkpieceAComponentId).IsWorkpiecePresent);
        Assert.Null(GetWorkpiece(completed, EmptyWorkpieceAComponentId).WorkpieceInstanceId);
        Assert.True(GetWorkpiece(completed, EmptyWorkpieceBComponentId).IsWorkpiecePresent);
        Assert.Equal("run-0001/WP-004", GetWorkpiece(completed, EmptyWorkpieceBComponentId).WorkpieceInstanceId);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.False(GetWorkpiece(engine.CurrentSnapshot, EmptyWorkpieceAComponentId).IsWorkpiecePresent);
        Assert.False(GetWorkpiece(engine.CurrentSnapshot, EmptyWorkpieceBComponentId).IsWorkpiecePresent);
        Assert.Null(GetWorkpiece(engine.CurrentSnapshot, EmptyWorkpieceBComponentId).WorkpieceInstanceId);

        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("feed-eject-cycle"))).IsAccepted);
        SimulationSnapshot repeated = await StepUntilCompletedAsync(engine);
        Assert.Equal("run-0002/WP-004", GetWorkpiece(repeated, EmptyWorkpieceBComponentId).WorkpieceInstanceId);
    }

    [Fact]
    public async Task FailDecision_UsesFailureBranchAndCompletesWithoutLastError()
    {
        using var engine = await CreateConfiguredEngineAsync(PlaceholderInspectionDecision.Fail);
        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));

        Assert.True(start.IsAccepted, start.Detail);
        var completed = await StepUntilCompletedAsync(engine);

        var sequence = Assert.Single(completed.Sequences);
        var camera = Assert.Single(completed.Cameras);
        var result = Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result);
        Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status);
        Assert.Equal("fail-complete", sequence.CurrentStepId);
        Assert.Null(sequence.LastError);
        Assert.Equal(ExpectedAcquisitionId, result.AcquisitionId);
        Assert.Equal(RecipeId, result.RecipeId);
        Assert.Equal(WorkpieceComponentId, result.WorkpieceComponentId);
        Assert.Equal(WorkpieceInstanceId, result.WorkpieceInstanceId);
        Assert.Equal(PlaceholderInspectionDecision.Fail, result.Decision);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Contains(
            events,
            item => item.Code == "SequenceStepTransition"
                && item.Message.Contains("wait-vision-result -> fail-complete", StringComparison.Ordinal));
        Assert.Contains(
            events,
            item => item.Code == "VisionResultReady"
                && item.Message.Contains("FAIL", StringComparison.Ordinal)
                && item.Message.Contains(WorkpieceComponentId, StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Code == "SequenceFaulted");
        Assert.Single(events, item => item.Code == "SequenceCompleted");
    }

    [Fact]
    public async Task ResetMidAcquisition_ClearsCameraStateOrdinalResultAndSequenceCorrelation()
    {
        using var engine = await CreateConfiguredEngineAsync(PlaceholderInspectionDecision.Pass);
        await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        await StepAsync(engine);
        await StepAsync(engine);

        var acquiring = Assert.Single(engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.Exposing, acquiring.State);
        Assert.Equal(1, acquiring.AcquisitionOrdinal);
        Assert.Equal(ExpectedAcquisitionId, acquiring.CurrentAcquisitionId);
        Assert.Equal(RecipeId, acquiring.CurrentRecipeId);
        Assert.Null(acquiring.Result);

        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        var snapshot = engine.CurrentSnapshot;
        var camera = Assert.Single(snapshot.Cameras);
        var sequence = Assert.Single(snapshot.Sequences);

        Assert.True(reset.IsAccepted, reset.Detail);
        Assert.Equal(2, reset.AppliedTick);
        Assert.Equal(TimeSpan.FromMilliseconds(10), reset.SimulationTime);
        Assert.Equal(0, snapshot.TickIndex);
        Assert.Equal(TimeSpan.Zero, snapshot.SimulationTime);
        Assert.Equal(VirtualCameraState.Idle, camera.State);
        Assert.Equal(0, camera.AcquisitionOrdinal);
        Assert.Null(camera.CurrentAcquisitionId);
        Assert.Null(camera.CurrentRecipeId);
        Assert.Equal(0, camera.ExposureTicksRemaining);
        Assert.Equal(0, camera.TransferTicksRemaining);
        Assert.Null(camera.Result);
        Assert.Equal(SequenceExecutionStatus.Ready, sequence.Status);
        Assert.Null(sequence.CurrentStepId);
        Assert.Null(sequence.LastError);
    }

    [Fact]
    public async Task ManualTrigger_PauseStepReset_PreservesFrameEvidenceAndOrderedEvents()
    {
        using var engine = await CreateConfiguredEngineAsync(PlaceholderInspectionDecision.Pass);
        var evidence = new VirtualCameraFrameEvidence(
            ExpectedAcquisitionId,
            "assets/presence-check.pgm",
            new string('A', 64),
            42,
            16,
            12,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            "inspection/sha256/manual",
            ExpectedAcquisitionId,
            CameraId,
            RecipeId,
            evidence.FrameId,
            PlaceholderInspectionDecision.Fail,
            "Deterministic mock inspection completed with NG.",
            new Dictionary<string, double>
            {
                ["SimulationTick"] = 0,
                ["PixelCount"] = 192,
                ["ContentLengthBytes"] = 42
            });
        var beforeManual = await engine.EnqueueCommandAsync(
            new TriggerVirtualCameraCommand(CameraId, RecipeId, evidence));
        Assert.Equal(SimulationCommandErrorCode.ControlOwnerNotAllowed, beforeManual.ErrorCode);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        var whileRunning = await engine.EnqueueCommandAsync(
            new TriggerVirtualCameraCommand(CameraId, RecipeId, evidence));
        Assert.Equal(SimulationCommandErrorCode.InvalidRunMode, whileRunning.ErrorCode);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var paused = engine.CurrentSnapshot;

        var trigger = await engine.EnqueueCommandAsync(
            new TriggerVirtualCameraCommand(CameraId, RecipeId, evidence, inspection));

        Assert.True(trigger.IsAccepted, trigger.Detail);
        var acquiring = Assert.Single(engine.CurrentSnapshot.Cameras);
        Assert.Equal(paused.TickIndex, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(paused.SimulationTime, engine.CurrentSnapshot.SimulationTime);
        Assert.Equal(VirtualCameraState.Exposing, acquiring.State);
        Assert.Equal(4, acquiring.ExposureTicksRemaining);
        Assert.Equal(evidence, acquiring.FrameEvidence);

        for (var index = 0; index < 4; index++)
        {
            await StepAsync(engine);
        }
        Assert.Equal(VirtualCameraState.Transferring, Assert.Single(engine.CurrentSnapshot.Cameras).State);
        for (var index = 0; index < 6; index++)
        {
            await StepAsync(engine);
        }

        var ready = Assert.Single(engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.FrameReady, ready.State);
        Assert.Equal(evidence, ready.FrameEvidence);
        var acquisition = Assert.IsType<VirtualCameraAcquisitionResult>(ready.Result);
        Assert.Equal(evidence, acquisition.FrameEvidence);
        Assert.Equal(inspection, acquisition.InspectionEvidence);
        Assert.Equal(PlaceholderInspectionDecision.Fail, acquisition.Decision);

        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        Assert.True(reset.IsAccepted, reset.Detail);
        var restored = Assert.Single(engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.Idle, restored.State);
        Assert.Equal(0, restored.AcquisitionOrdinal);
        Assert.Null(restored.FrameEvidence);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var triggered = Assert.Single(events, item => item.Code == "CameraTriggered");
        var exposureCompleted = Assert.Single(events, item => item.Code == "CameraExposureCompleted");
        var frameReady = Assert.Single(events, item => item.Code == "CameraFrameReady");
        var visionReady = Assert.Single(events, item => item.Code == "VisionResultReady");
        Assert.Equal(paused.TickIndex, triggered.TickIndex);
        Assert.Equal(triggered.TickIndex + 4, exposureCompleted.TickIndex);
        Assert.Equal(triggered.TickIndex + 10, frameReady.TickIndex);
        Assert.True(triggered.EventIndex < exposureCompleted.EventIndex);
        Assert.True(exposureCompleted.EventIndex < frameReady.EventIndex);
        Assert.True(frameReady.EventIndex < visionReady.EventIndex);
        Assert.Contains(evidence.ContentSha256, triggered.Message, StringComparison.Ordinal);
        Assert.Contains(inspection.InspectionId, triggered.Message, StringComparison.Ordinal);
        Assert.Contains(evidence.ContentSha256, frameReady.Message, StringComparison.Ordinal);
        Assert.Contains(inspection.InspectionId, visionReady.Message, StringComparison.Ordinal);
        Assert.Contains("ContentLengthBytes=42", visionReady.Message, StringComparison.Ordinal);
        Assert.Contains("PixelCount=192", visionReady.Message, StringComparison.Ordinal);
    }

    private static async Task<FixedStepSimulationEngine> CreateConfiguredEngineAsync(
        PlaceholderInspectionDecision decision,
        AutomaticRunConfiguration? automaticRun = null,
        bool includeRetrySequence = false,
        SequenceDefinition? sequenceOverride = null,
        bool includeEmptyWorkpieces = false,
        bool includeSecondCamera = false)
    {
        var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntimeConfiguration(
                decision,
                automaticRun,
                includeRetrySequence,
                sequenceOverride,
                includeEmptyWorkpieces,
                includeSecondCamera)));
        Assert.True(configured.IsAccepted, configured.Detail);
        return engine;
    }

    private static SimulationRuntimeConfiguration CreateRuntimeConfiguration(
        PlaceholderInspectionDecision decision,
        AutomaticRunConfiguration? automaticRun = null,
        bool includeRetrySequence = false,
        SequenceDefinition? sequenceOverride = null,
        bool includeEmptyWorkpieces = false,
        bool includeSecondCamera = false)
    {
        var definition = sequenceOverride ?? new SequenceDefinition
        {
            Id = "inspection-cycle",
            Name = "Inspection Cycle",
            Steps =
            {
                Step("move-axis", SequenceStepAction.MoveAxis, "x", "0.676", "trigger-camera"),
                Step(
                    "trigger-camera",
                    SequenceStepAction.TriggerCamera,
                    CameraId,
                    RecipeId,
                    "wait-vision-result",
                    WorkpieceComponentId),
                new SequenceStepDefinition
                {
                    Id = "wait-vision-result",
                    Name = "Wait Vision Result",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = CameraId,
                    TimeoutMs = 500,
                    NextStepId = "pass-complete",
                    FailureStepId = "fail-complete"
                },
                Step("pass-complete", SequenceStepAction.Complete, string.Empty, string.Empty),
                Step("fail-complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        IEnumerable<string> workpieceComponentIds = includeSecondCamera
            ? new[] { WorkpieceComponentId, "workpiece.z" }
            : new[] { WorkpieceComponentId };
        if (includeEmptyWorkpieces)
        {
            workpieceComponentIds = workpieceComponentIds.Concat(
                new[] { EmptyWorkpieceAComponentId, EmptyWorkpieceBComponentId });
        }

        var cameraIds = includeSecondCamera ? new[] { CameraId, SecondCameraId } : new[] { CameraId };
        var targets = new SequenceCompilationTargets(
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal),
            new[] { "x" },
            cameraIds,
            Array.Empty<string>(),
            workpieceComponentIds);
        var compilation = new SequenceCompiler().Compile(definition, targets);
        Assert.True(
            compilation.IsSuccess,
            string.Join(Environment.NewLine, compilation.Errors.Select(error => error.Message)));

        var sequences = new List<CompiledSequence> { compilation.Sequence! };
        var channels = new List<ChannelDefinition>
        {
            new()
            {
                Id = "do.conveyor.top.run",
                Name = "Top conveyor run",
                Kind = ChannelKind.DigitalOutput,
                InitialValue = 1
            },
            new()
            {
                Id = "do.conveyor.top.reverse",
                Name = "Top conveyor reverse",
                Kind = ChannelKind.DigitalOutput
            },
            new()
            {
                Id = "do.conveyor.second.run",
                Name = "Second conveyor run",
                Kind = ChannelKind.DigitalOutput,
                InitialValue = 1
            },
            new()
            {
                Id = "do.conveyor.second.reverse",
                Name = "Second conveyor reverse",
                Kind = ChannelKind.DigitalOutput
            }
        };
        if (includeRetrySequence)
        {
            const string waitSignalId = "di.fault";
            var retryDefinition = new SequenceDefinition
            {
                Id = "fault-cycle",
                Name = "Fault Cycle",
                WatchdogTimeoutMs = 5,
                Steps =
                {
                    Step("wait-start", SequenceStepAction.WaitSignal, waitSignalId, "true", "complete"),
                    Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
                }
            };
            var retryCompilation = new SequenceCompiler().Compile(
                retryDefinition,
                new SequenceCompilationTargets(
                    new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
                    {
                        [waitSignalId] = ChannelKind.DigitalInput
                    },
                    Array.Empty<string>()));
            Assert.True(
                retryCompilation.IsSuccess,
                string.Join(Environment.NewLine, retryCompilation.Errors.Select(error => error.Message)));
            sequences.Add(retryCompilation.Sequence!);
            channels.Add(new ChannelDefinition
            {
                Id = waitSignalId,
                Name = "Fault test input",
                Kind = ChannelKind.DigitalInput
            });
        }

        var cameras = new List<VirtualCameraConfiguration>
        {
            new(
                CameraId,
                "Top Camera",
                exposureTicks: 4,
                transferTicks: 6,
                decision)
        };
        if (includeSecondCamera)
        {
            cameras.Add(new VirtualCameraConfiguration(
                SecondCameraId,
                "Side Camera",
                exposureTicks: 4,
                transferTicks: 6,
                PlaceholderInspectionDecision.Fail));
        }

        return new SimulationRuntimeConfiguration(
            new[] { CreateAxisConfiguration() },
            channels,
            sequences,
            cameras,
            automaticRun,
            layout: CreateWorkpieceLayout(includeEmptyWorkpieces));
    }

    private static MachineLayoutRuntimeConfiguration CreateWorkpieceLayout(bool includeEmptyWorkpieces = false)
    {
        var components = new List<LayoutComponentRuntimeConfiguration>
        {
            new ConveyorRuntimeConfiguration(
                "conveyor.top",
                "Top Conveyor",
                "do.conveyor.top.run",
                "do.conveyor.top.reverse",
                10,
                FixedStep.TotalSeconds,
                new LayoutRuntimeTransform(0, 0),
                new LayoutRuntimeSize(100, 40)),
            new WorkpieceRuntimeConfiguration(
                WorkpieceComponentId,
                "Top Workpiece",
                "Test Part",
                "conveyor.top",
                WorkpieceInspectionState.Pending,
                new LayoutRuntimeTransform(-40, 0),
                new LayoutRuntimeSize(20, 20)),
            new ConveyorRuntimeConfiguration(
                "conveyor.second",
                "Second Conveyor",
                "do.conveyor.second.run",
                "do.conveyor.second.reverse",
                10,
                FixedStep.TotalSeconds,
                new LayoutRuntimeTransform(0, 100),
                new LayoutRuntimeSize(100, 40)),
            new WorkpieceRuntimeConfiguration(
                "workpiece.z",
                "Second Workpiece",
                "Test Part",
                "conveyor.second",
                WorkpieceInspectionState.Pending,
                new LayoutRuntimeTransform(-40, 100),
                new LayoutRuntimeSize(20, 20))
        };
        if (includeEmptyWorkpieces)
        {
            components.Add(new WorkpieceRuntimeConfiguration(
                EmptyWorkpieceAComponentId,
                "Empty position A",
                "Test Part",
                "conveyor.second",
                WorkpieceInspectionState.Pending,
                new LayoutRuntimeTransform(0, 100),
                new LayoutRuntimeSize(20, 20),
                initiallyPresent: false));
            components.Add(new WorkpieceRuntimeConfiguration(
                EmptyWorkpieceBComponentId,
                "Empty position B",
                "Test Part",
                "conveyor.second",
                WorkpieceInspectionState.Pending,
                new LayoutRuntimeTransform(40, 100),
                new LayoutRuntimeSize(20, 20),
                initiallyPresent: false));
        }

        return new MachineLayoutRuntimeConfiguration(
            "inspection-layout",
            "Inspection Layout",
            components);
    }

    private static LayoutComponentSnapshot GetWorkpiece(SimulationSnapshot snapshot, string componentId) =>
        Assert.Single(snapshot.LayoutComponents, item => item.Id == componentId);

    private static AxisConfiguration CreateAxisConfiguration() =>
        new()
        {
            Id = "x",
            Name = "Inspection X Axis",
            MinimumPosition = 0,
            MaximumPosition = 10,
            HomePosition = 0,
            MaximumVelocity = 100,
            Acceleration = 1000,
            Deceleration = 1000
        };

    private static async Task<SimulationSnapshot> StepUntilCompletedAsync(
        FixedStepSimulationEngine engine)
    {
        for (var index = 0; index < 100; index++)
        {
            await StepAsync(engine);
            var snapshot = engine.CurrentSnapshot;
            if (Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Completed)
            {
                return snapshot;
            }
        }

        throw new TimeoutException("The virtual-camera sequence did not complete within 100 fixed ticks.");
    }

    private static async Task StepAsync(FixedStepSimulationEngine engine)
    {
        var step = await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(step.IsAccepted, step.Detail);
    }

    private static async Task<IReadOnlyList<SimulationEvent>> ReadAllEventsAsync(
        FixedStepSimulationEngine engine)
    {
        var events = new List<SimulationEvent>();
        await foreach (var item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }

        return events;
    }

    private static SequenceStepDefinition Step(
        string id,
        SequenceStepAction action,
        string target,
        string parameter,
        string? next = null,
        string? workpieceComponentId = null) =>
        new()
        {
            Id = id,
            Name = id,
            Action = action,
            TargetId = target,
            Parameter = parameter,
            NextStepId = next,
            WorkpieceComponentId = workpieceComponentId
        };
}
