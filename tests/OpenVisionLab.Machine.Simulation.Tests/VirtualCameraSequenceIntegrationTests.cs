using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class VirtualCameraSequenceIntegrationTests
{
    private const string CameraId = "camera.top";
    private const string RecipeId = "presence-check";
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
        Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status);
        Assert.Equal("pass-complete", sequence.CurrentStepId);
        Assert.Null(sequence.LastError);
        Assert.Equal(VirtualCameraState.FrameReady, camera.State);
        Assert.Equal(ExpectedAcquisitionId, camera.CurrentAcquisitionId);
        Assert.Equal(RecipeId, camera.CurrentRecipeId);
        Assert.Equal(ExpectedAcquisitionId, result.AcquisitionId);
        Assert.Equal(CameraId, result.CameraId);
        Assert.Equal(RecipeId, result.RecipeId);
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
        Assert.Contains(ExpectedAcquisitionId, frameReady.Message, StringComparison.Ordinal);
        Assert.Contains(RecipeId, frameReady.Message, StringComparison.Ordinal);
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
                && item.Message.Contains("FAIL", StringComparison.Ordinal));
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
        PlaceholderInspectionDecision decision)
    {
        var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntimeConfiguration(decision)));
        Assert.True(configured.IsAccepted, configured.Detail);
        return engine;
    }

    private static SimulationRuntimeConfiguration CreateRuntimeConfiguration(
        PlaceholderInspectionDecision decision)
    {
        var definition = new SequenceDefinition
        {
            Id = "inspection-cycle",
            Name = "Inspection Cycle",
            Steps =
            {
                Step("move-axis", SequenceStepAction.MoveAxis, "x", "0.676", "trigger-camera"),
                Step("trigger-camera", SequenceStepAction.TriggerCamera, CameraId, RecipeId, "wait-vision-result"),
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
        var targets = new SequenceCompilationTargets(
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal),
            new[] { "x" },
            new[] { CameraId });
        var compilation = new SequenceCompiler().Compile(definition, targets);
        Assert.True(
            compilation.IsSuccess,
            string.Join(Environment.NewLine, compilation.Errors.Select(error => error.Message)));

        return new SimulationRuntimeConfiguration(
            new[] { CreateAxisConfiguration() },
            Array.Empty<ChannelDefinition>(),
            new[] { compilation.Sequence! },
            new[]
            {
                new VirtualCameraConfiguration(
                    CameraId,
                    "Top Camera",
                    exposureTicks: 4,
                    transferTicks: 6,
                    decision)
            });
    }

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
        string? next = null) =>
        new()
        {
            Id = id,
            Name = id,
            Action = action,
            TargetId = target,
            Parameter = parameter,
            NextStepId = next
        };
}
