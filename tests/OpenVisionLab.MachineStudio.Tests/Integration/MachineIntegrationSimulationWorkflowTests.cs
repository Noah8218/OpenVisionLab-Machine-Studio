using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationSimulationWorkflowTests
{
    private const string ProjectId = "project-1";
    private const string CameraId = "camera-1";
    private const string AcquisitionId = "camera-1/frame/00000001";
    private const string FrameId = AcquisitionId;
    private static readonly string InputSha256 = new('A', 64);
    private static readonly string RecipeSha256 = new('B', 64);
    private static readonly IntegrationApplicationIdentity Consumer = new(
        IntegrationApplicationIds.TwoDStudio,
        "1.0.0",
        new string('2', 40),
        IntegrationSourceState.Clean);

    [Fact]
    public async Task ExplicitApplyMapsValidatedCurrentProcessResultOnce()
    {
        var snapshot = CreateSnapshot();
        var validated = CreateValidatedResult();
        ApplyExternalInspectionResultCommand? dispatched = null;
        var workflow = new MachineIntegrationSimulationWorkflow(
            () => snapshot,
            command =>
            {
                dispatched = Assert.IsType<ApplyExternalInspectionResultCommand>(command);
                return Task.FromResult(new SimulationCommandResult(
                    command.CommandId,
                    true,
                    snapshot.TickIndex,
                    snapshot.SimulationTime,
                    SimulationCommandErrorCode.None,
                    "accepted"));
            });

        var publishContext = workflow.CapturePublishContext();
        Assert.True(workflow.TryRecordPublishedHandoff(validated.Handoff, publishContext));

        Assert.True(workflow.CanApply(validated));
        Assert.Null(dispatched);
        var result = await workflow.ApplyAsync(validated);

        Assert.True(result.IsAccepted);
        Assert.NotNull(dispatched);
        Assert.Equal(new SimulationRuntimeIdentity(ProjectId, 7), dispatched.ExpectedRuntime);
        Assert.True(dispatched.MessageChain.IsExactlyCorrelated);
        Assert.Equal(validated.ResultDocumentSha256, dispatched.MessageChain.ResultDocumentSha256);
        Assert.Equal(ExternalInspectionResultStatus.Completed, dispatched.Status);
        Assert.Equal(ExternalInspectionOutcome.Pass, dispatched.Outcome);
        Assert.Equal(ProjectId, dispatched.ExpectedCorrelation.ProjectId);
        Assert.Equal(CameraId, dispatched.ExpectedCorrelation.CameraId);
        Assert.Equal(InputSha256, dispatched.ExpectedCorrelation.InputSha256);
        Assert.False(workflow.CanApply(validated));
    }

    [Fact]
    public void RestartAndRuntimeOrAcquisitionChangeCannotReapplyObservedResult()
    {
        var snapshot = CreateSnapshot();
        var validated = CreateValidatedResult();
        var restarted = new MachineIntegrationSimulationWorkflow(
            () => snapshot,
            _ => throw new InvalidOperationException("must not dispatch"));
        Assert.False(restarted.CanApply(validated));

        var workflow = new MachineIntegrationSimulationWorkflow(
            () => snapshot,
            _ => throw new InvalidOperationException("must not dispatch"));
        Assert.True(workflow.TryRecordPublishedHandoff(
            validated.Handoff,
            workflow.CapturePublishContext()));

        snapshot = CreateSnapshot(runtimeGeneration: 8);
        Assert.True(workflow.RefreshRuntimeState());
        snapshot = CreateSnapshot(runtimeGeneration: 7);
        Assert.False(workflow.CanApply(validated));

        Assert.True(workflow.TryRecordPublishedHandoff(
            validated.Handoff,
            workflow.CapturePublishContext()));
        snapshot = CreateSnapshot(
            acquisitionId: "camera-1/frame/00000002",
            frameId: "camera-1/frame/00000002");
        Assert.True(workflow.RefreshRuntimeState());
        snapshot = CreateSnapshot();
        Assert.False(workflow.CanApply(validated));
    }

    [Fact]
    public async Task ClosedAutomaticContextDispatchesLateResultForEngineQuarantine()
    {
        var snapshot = CreateSnapshot();
        var validated = CreateValidatedResult();
        ApplyExternalInspectionResultCommand? dispatched = null;
        var workflow = new MachineIntegrationSimulationWorkflow(
            () => snapshot,
            command =>
            {
                dispatched = Assert.IsType<ApplyExternalInspectionResultCommand>(command);
                return Task.FromResult(new SimulationCommandResult(
                    command.CommandId,
                    false,
                    snapshot.TickIndex,
                    snapshot.SimulationTime,
                    SimulationCommandErrorCode.ExternalInspectionNotPending,
                    "late result quarantined"));
            });

        Assert.True(workflow.TryRecordPublishedHandoff(validated.Handoff, workflow.CapturePublishContext()));
        snapshot = CreateSnapshot(cameraState: VirtualCameraState.Faulted);
        Assert.True(workflow.RefreshRuntimeState());
        Assert.True(workflow.HasClosedPublishedContext);
        Assert.False(workflow.CanApply(validated));
        Assert.True(workflow.CanQuarantineLateResult(validated));

        var result = await workflow.QuarantineLateResultAsync(validated);

        Assert.False(result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, result.ErrorCode);
        Assert.NotNull(dispatched);
        Assert.False(workflow.HasClosedPublishedContext);
        Assert.Null(workflow.PublishedTransactionId);
    }

    [Fact]
    public void ApplyRequiresPausedPendingCameraAndAcceptedAcknowledgement()
    {
        var snapshot = CreateSnapshot(runMode: SimulationRunMode.RealTime);
        var validated = CreateValidatedResult();
        var workflow = new MachineIntegrationSimulationWorkflow(
            () => snapshot,
            _ => throw new InvalidOperationException("must not dispatch"));
        Assert.True(workflow.TryRecordPublishedHandoff(
            validated.Handoff,
            workflow.CapturePublishContext()));
        Assert.False(workflow.CanApply(validated));

        snapshot = CreateSnapshot();
        Assert.True(workflow.CanApply(validated));
        var rejectedAcknowledgement = validated with
        {
            Acknowledgement = validated.Acknowledgement with
            {
                Status = IntegrationAcknowledgementStatus.Rejected,
                Error = new IntegrationError(IntegrationErrorCode.InvalidState, "rejected", false)
            }
        };
        Assert.False(workflow.CanApply(rejectedAcknowledgement));
    }

    [Fact]
    public async Task ThreeDHeightMapUsesAcquisitionForSimulationCorrelation()
    {
        var snapshot = CreateSnapshot();
        var original = CreateValidatedResult();
        var context = original.Handoff.Context with
        {
            FrameId = "frame.c3d-grid-index",
            Modality = IntegrationInspectionModality.ThreeD,
            InputKind = IntegrationInspectionInputKind.HeightMap
        };
        var validated = original with
        {
            Handoff = original.Handoff with { Context = context },
            Result = original.Result with { Correlation = IntegrationRunCorrelation.FromContext(context) }
        };
        ApplyExternalInspectionResultCommand? dispatched = null;
        var workflow = new MachineIntegrationSimulationWorkflow(
            () => snapshot,
            command =>
            {
                dispatched = Assert.IsType<ApplyExternalInspectionResultCommand>(command);
                return Task.FromResult(new SimulationCommandResult(
                    command.CommandId,
                    true,
                    snapshot.TickIndex,
                    snapshot.SimulationTime,
                    SimulationCommandErrorCode.None,
                    "accepted"));
            });

        Assert.True(workflow.TryRecordPublishedHandoff(validated.Handoff, workflow.CapturePublishContext()));
        Assert.True(workflow.CanApply(validated));
        Assert.True((await workflow.ApplyAsync(validated)).IsAccepted);
        Assert.Equal(AcquisitionId, dispatched?.ExpectedCorrelation.FrameId);
        Assert.Equal(AcquisitionId, dispatched?.ResultCorrelation.FrameId);
    }

    private static MachineIntegrationValidatedResult CreateValidatedResult()
    {
        var transactionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var handoffMessageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var acknowledgementMessageId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var createdAt = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var context = new IntegrationInspectionContextV2(
            ProjectId,
            "1.0",
            "sequence-1",
            "step-1",
            CameraId,
            AcquisitionId,
            FrameId,
            "mm",
            IntegrationInspectionModality.TwoD,
            IntegrationInspectionInputKind.Image,
            InputSha256,
            RecipeSha256,
            Consumer,
            []);
        var handoff = new IntegrationHandoffV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Handoff,
            handoffMessageId,
            transactionId,
            createdAt,
            new IntegrationApplicationIdentity(
                IntegrationApplicationIds.MachineStudio,
                "0.2.0-dev.59",
                new string('1', 40),
                IntegrationSourceState.Clean),
            context);
        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            acknowledgementMessageId,
            transactionId,
            handoffMessageId,
            createdAt.AddSeconds(1),
            Consumer,
            IntegrationAcknowledgementStatus.Accepted,
            null);
        var result = new IntegrationResultV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Result,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            transactionId,
            handoffMessageId,
            acknowledgementMessageId,
            createdAt.AddSeconds(2),
            Consumer,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass,
            "run-1",
            null,
            IntegrationRunCorrelation.FromContext(context),
            [],
            [],
            null);
        return new(handoff, acknowledgement, result, new string('C', 64));
    }

    private static SimulationSnapshot CreateSnapshot(
        SimulationRunMode runMode = SimulationRunMode.Paused,
        long runtimeGeneration = 7,
        string acquisitionId = AcquisitionId,
        string frameId = FrameId,
        VirtualCameraState cameraState = VirtualCameraState.AwaitingExternalResult)
    {
        var frame = new VirtualCameraFrameEvidence(
            frameId,
            "assets/source.pgm",
            InputSha256,
            42,
            16,
            12,
            "Mono8");
        var camera = new VirtualCameraSnapshot(
            CameraId,
            "Camera",
            cameraState,
            1,
            acquisitionId,
            "recipe-1",
            0,
            0,
            Result: null,
            frame);
        return new SimulationSnapshot(
            TimeSpan.FromMilliseconds(15),
            3,
            runMode,
            SimulationControlOwner.Manual,
            1,
            Array.Empty<AxisSnapshot>(),
            0,
            Array.Empty<DigitalSignalSnapshot>(),
            Array.Empty<SequenceExecutionSnapshot>(),
            new[] { camera },
            AutomaticRunSnapshot.NotConfigured,
            Array.Empty<LayoutComponentSnapshot>(),
            projectId: ProjectId,
            runtimeGeneration: runtimeGeneration);
    }
}
