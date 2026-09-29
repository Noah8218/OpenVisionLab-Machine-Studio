using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class ExternalInspectionResultIntegrationTests
{
    private const string ProjectId = "project-external-result";
    private const string CameraId = "camera.external";
    private const string AcquisitionId = "camera.external/frame/00000001";
    private const string FrameId = AcquisitionId;
    private const string RecipeId = "recipe.external";
    private const string PositionChannelId = "di.position";
    private const string AcceptChannelId = "do.accept";
    private const string ReadyChannelId = "di.ready";
    private const string CompleteChannelId = "di.complete";
    private static readonly string InputSha256 = new('A', 64);
    private static readonly string RecipeSha256 = new('B', 64);
    private static readonly string ResultSha256 = new('C', 64);
    private static readonly ExternalInspectionConsumerIdentity Consumer = new(
        "OpenVisionLab.TwoDStudio",
        "1.0.0",
        new string('1', 40),
        "Clean");

    [Theory]
    [InlineData(ExternalInspectionOutcome.Pass, PlaceholderInspectionDecision.Pass)]
    [InlineData(ExternalInspectionOutcome.Ng, PlaceholderInspectionDecision.Fail)]
    public async Task CompletedDecision_IsVisibleToHandoffOnNextTickWithoutWritingAcceptance(
        ExternalInspectionOutcome outcome,
        PlaceholderInspectionDecision expectedDecision)
    {
        await using var fixture = await CreatePendingFixtureAsync();
        var command = CreateCommand(fixture, ExternalInspectionResultStatus.Completed, outcome);

        var applied = await fixture.Engine.EnqueueCommandAsync(command);
        var beforeTick = fixture.Engine.CurrentSnapshot;

        Assert.True(applied.IsAccepted, applied.Detail);
        Assert.Equal(InspectionHandoffState.Inspecting, Assert.Single(beforeTick.InspectionHandoffs).State);
        Assert.False(Signal(beforeTick, AcceptChannelId).Value);

        await StepAsync(fixture.Engine);
        var visible = fixture.Engine.CurrentSnapshot;
        var camera = Assert.Single(visible.Cameras);
        var acquisition = Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result);

        Assert.Equal(VirtualCameraState.FrameReady, camera.State);
        Assert.Equal(expectedDecision, acquisition.Decision);
        Assert.Equal(command.MessageChain.ResultMessageId, acquisition.ExternalResultEvidence?.ResultMessageId);
        Assert.Equal(InspectionHandoffState.ResultAvailable, Assert.Single(visible.InspectionHandoffs).State);
        Assert.Equal(expectedDecision, Assert.Single(visible.InspectionHandoffs).Decision);
        Assert.False(Signal(visible, AcceptChannelId).Value);
        Assert.False(Signal(visible, CompleteChannelId).Value);
    }

    [Theory]
    [InlineData(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError)]
    [InlineData(ExternalInspectionResultStatus.Cancelled, ExternalInspectionOutcome.NotMeasured)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.NotMeasured)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Indeterminate)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.CorrelationMismatch)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Tampered)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.ExecutionError)]
    public async Task NonDecisionTerminalResult_FailsClosedAndRequiresReset(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        await using var fixture = await CreatePendingFixtureAsync();

        var applied = await fixture.Engine.EnqueueCommandAsync(CreateCommand(fixture, status, outcome));
        await StepAsync(fixture.Engine);
        var snapshot = fixture.Engine.CurrentSnapshot;

        Assert.True(applied.IsAccepted, applied.Detail);
        Assert.Equal(VirtualCameraState.Faulted, Assert.Single(snapshot.Cameras).State);
        Assert.Null(Assert.Single(snapshot.Cameras).Result);
        Assert.Equal(InspectionHandoffState.InterlockFault, Assert.Single(snapshot.InspectionHandoffs).State);
        Assert.False(Signal(snapshot, AcceptChannelId).Value);
        Assert.False(Signal(snapshot, CompleteChannelId).Value);

        Assert.True((await fixture.Engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(VirtualCameraState.Idle, Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).State);
    }

    [Fact]
    public async Task ExactReplayIsIdempotentAndConflictingDuplicateIsRejectedWithoutMutation()
    {
        await using var fixture = await CreatePendingFixtureAsync();
        var chain = CreateMessageChain();
        var first = CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            chain);

        Assert.True((await fixture.Engine.EnqueueCommandAsync(first)).IsAccepted);
        var replay = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            chain));
        var conflictChain = new ExternalInspectionMessageChain(
            chain.HandoffTransactionId,
            chain.HandoffMessageId,
            chain.AcknowledgementTransactionId,
            chain.AcknowledgementHandoffMessageId,
            chain.AcknowledgementMessageId,
            chain.ResultTransactionId,
            chain.ResultHandoffMessageId,
            chain.ResultAcknowledgementMessageId,
            Guid.NewGuid(),
            new string('D', 64));
        var conflict = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            conflictChain));

        Assert.True(replay.IsAccepted, replay.Detail);
        Assert.Contains("already applied", replay.Detail, StringComparison.Ordinal);
        Assert.False(conflict.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionConflictingDuplicate, conflict.ErrorCode);

        await StepAsync(fixture.Engine);
        var evidence = Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).ExternalResultEvidence;
        Assert.NotNull(evidence);
        Assert.Equal(chain.ResultMessageId, evidence.ResultMessageId);
        Assert.Equal(chain.ResultDocumentSha256, evidence.ResultDocumentSha256);
    }

    [Fact]
    public async Task CorrelationRuntimeAndOrderingMismatchesAreRejectedWithoutChangingPendingState()
    {
        await using var fixture = await CreatePendingFixtureAsync();
        var mismatchedResult = CopyCorrelation(fixture.Correlation, stepId: "other-step");

        var correlation = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            resultCorrelation: mismatchedResult));
        var staleRuntime = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            expectedRuntime: fixture.RuntimeIdentity with
            {
                RuntimeGeneration = fixture.RuntimeIdentity.RuntimeGeneration + 1
            }));
        var mismatchedFrame = CopyCorrelation(
            fixture.Correlation,
            frameId: "other-frame",
            inputSha256: new string('E', 64));
        var acquisition = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            expectedCorrelation: mismatchedFrame,
            resultCorrelation: mismatchedFrame));

        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionCorrelationMismatch, correlation.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, staleRuntime.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionAcquisitionMismatch, acquisition.ErrorCode);
        var pending = Assert.Single(fixture.Engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.AwaitingExternalResult, pending.State);
        Assert.Null(pending.Result);

        Assert.True((await fixture.Engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        var late = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass));
        Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, late.ErrorCode);
        var currentRuntime = new SimulationRuntimeIdentity(
            fixture.Engine.CurrentSnapshot.ProjectId,
            fixture.Engine.CurrentSnapshot.RuntimeGeneration);
        var outOfOrder = await fixture.Engine.EnqueueCommandAsync(CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            expectedRuntime: currentRuntime));
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, outOfOrder.ErrorCode);
        Assert.Null(Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).ExternalResultEvidence);
    }

    [Fact]
    public async Task LateResultFromPreviousRuntime_IsRejectedWithoutMutatingCurrentPendingAcquisition()
    {
        await using var fixture = await CreatePendingFixtureAsync();
        var previousRuntime = fixture.RuntimeIdentity;
        var previousResult = CreateCommand(
            fixture,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            expectedRuntime: previousRuntime);

        Assert.True((await fixture.Engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.True((await fixture.Engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await fixture.Engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        await StepAsync(fixture.Engine);
        var currentRuntime = new SimulationRuntimeIdentity(
            fixture.Engine.CurrentSnapshot.ProjectId,
            fixture.Engine.CurrentSnapshot.RuntimeGeneration);
        Assert.NotEqual(previousRuntime.RuntimeGeneration, currentRuntime.RuntimeGeneration);

        var retrigger = await fixture.Engine.EnqueueCommandAsync(new TriggerVirtualCameraCommand(
            CameraId,
            RecipeId,
            new VirtualCameraFrameEvidence(
                FrameId,
                "assets/external-result.pgm",
                InputSha256,
                42,
                16,
                12,
                "Mono8"),
            inspectionEvidence: null,
            ProjectId,
            currentRuntime.RuntimeGeneration,
            waitForExternalResult: true));
        Assert.True(retrigger.IsAccepted, retrigger.Detail);
        await StepAsync(fixture.Engine);
        await StepAsync(fixture.Engine);
        Assert.Equal(
            VirtualCameraState.AwaitingExternalResult,
            Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).State);

        var late = await fixture.Engine.EnqueueCommandAsync(previousResult);

        Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, late.ErrorCode);
        var pending = Assert.Single(fixture.Engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.AwaitingExternalResult, pending.State);
        Assert.Null(pending.Result);
        Assert.Null(pending.ExternalResultEvidence);
    }

    private static async Task<PendingFixture> CreatePendingFixtureAsync()
    {
        var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = TimeSpan.FromMilliseconds(5)
        });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntimeConfiguration(),
            ProjectId));
        Assert.True(configured.IsAccepted, configured.Detail);
        var runtimeIdentity = new SimulationRuntimeIdentity(
            engine.CurrentSnapshot.ProjectId,
            engine.CurrentSnapshot.RuntimeGeneration);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        await StepAsync(engine);
        Assert.Equal(InspectionHandoffState.Ready, Assert.Single(engine.CurrentSnapshot.InspectionHandoffs).State);

        var frame = new VirtualCameraFrameEvidence(
            FrameId,
            "assets/external-result.pgm",
            InputSha256,
            42,
            16,
            12,
            "Mono8");
        var trigger = await engine.EnqueueCommandAsync(new TriggerVirtualCameraCommand(
            CameraId,
            RecipeId,
            frame,
            inspectionEvidence: null,
            ProjectId,
            runtimeIdentity.RuntimeGeneration,
            waitForExternalResult: true));
        Assert.True(trigger.IsAccepted, trigger.Detail);
        await StepAsync(engine);
        await StepAsync(engine);

        var pending = Assert.Single(engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.AwaitingExternalResult, pending.State);
        Assert.Null(pending.Result);
        Assert.Equal(InspectionHandoffState.Inspecting, Assert.Single(engine.CurrentSnapshot.InspectionHandoffs).State);
        return new(
            engine,
            runtimeIdentity,
            new ExternalInspectionCorrelationIdentity(
                ProjectId,
                "1.0",
                "sequence.external",
                "step.trigger",
                CameraId,
                AcquisitionId,
                FrameId,
                "mm",
                "TwoD",
                "Image",
                InputSha256,
                RecipeSha256,
                Consumer));
    }

    private static ApplyExternalInspectionResultCommand CreateCommand(
        PendingFixture fixture,
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome,
        ExternalInspectionMessageChain? messageChain = null,
        ExternalInspectionCorrelationIdentity? expectedCorrelation = null,
        ExternalInspectionCorrelationIdentity? resultCorrelation = null,
        SimulationRuntimeIdentity? expectedRuntime = null) =>
        new(
            expectedRuntime ?? fixture.RuntimeIdentity,
            messageChain ?? CreateMessageChain(),
            expectedCorrelation ?? fixture.Correlation,
            resultCorrelation ?? fixture.Correlation,
            Consumer,
            Consumer,
            acknowledgementAccepted: true,
            status,
            outcome,
            "run-1");

    private static ExternalInspectionMessageChain CreateMessageChain()
    {
        var transactionId = Guid.NewGuid();
        var handoffMessageId = Guid.NewGuid();
        var acknowledgementMessageId = Guid.NewGuid();
        return new(
            transactionId,
            handoffMessageId,
            transactionId,
            handoffMessageId,
            acknowledgementMessageId,
            transactionId,
            handoffMessageId,
            acknowledgementMessageId,
            Guid.NewGuid(),
            ResultSha256);
    }

    private static ExternalInspectionCorrelationIdentity CopyCorrelation(
        ExternalInspectionCorrelationIdentity source,
        string? stepId = null,
        string? frameId = null,
        string? inputSha256 = null) =>
        new(
            source.ProjectId,
            source.ProjectSchema,
            source.SequenceId,
            stepId ?? source.StepId,
            source.CameraId,
            source.AcquisitionId,
            frameId ?? source.FrameId,
            source.Unit,
            source.Modality,
            source.InputKind,
            inputSha256 ?? source.InputSha256,
            source.RecipeSha256,
            source.ConsumerBuild);

    private static SimulationRuntimeConfiguration CreateRuntimeConfiguration()
    {
        var channels = new[]
        {
            Channel(PositionChannelId, ChannelKind.DigitalInput, initialValue: 1),
            Channel(AcceptChannelId, ChannelKind.DigitalOutput),
            Channel(ReadyChannelId, ChannelKind.DigitalInput),
            Channel(CompleteChannelId, ChannelKind.DigitalInput)
        };
        var handoff = new InspectionHandoffRuntimeConfiguration(
            "inspection.external",
            "External Inspection",
            CameraId,
            PositionChannelId,
            AcceptChannelId,
            ReadyChannelId,
            CompleteChannelId);
        var layout = new MachineLayoutRuntimeConfiguration(
            "main",
            "Main",
            Array.Empty<LayoutComponentRuntimeConfiguration>(),
            inspectionHandoffs: new[] { handoff });
        return new(
            Array.Empty<AxisConfiguration>(),
            channels,
            Array.Empty<CompiledSequence>(),
            new[]
            {
                new VirtualCameraConfiguration(
                    CameraId,
                    "External Camera",
                    exposureTicks: 1,
                    transferTicks: 1,
                    PlaceholderInspectionDecision.Pass)
            },
            automaticRun: null,
            layout);
    }

    private static ChannelDefinition Channel(
        string id,
        ChannelKind kind,
        double initialValue = 0) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = initialValue };

    private static DigitalSignalSnapshot Signal(Simulation.Snapshots.SimulationSnapshot snapshot, string id) =>
        Assert.Single(snapshot.Signals, signal => string.Equals(signal.Id, id, StringComparison.Ordinal));

    private static async Task StepAsync(FixedStepSimulationEngine engine)
    {
        var result = await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(result.IsAccepted, result.Detail);
    }

    private sealed record PendingFixture(
        FixedStepSimulationEngine Engine,
        SimulationRuntimeIdentity RuntimeIdentity,
        ExternalInspectionCorrelationIdentity Correlation) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Engine.StopAsync();
            Engine.Dispose();
        }
    }
}
