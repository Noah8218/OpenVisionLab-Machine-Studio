using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch030CorrelationIntegrationTests
{
    private const string ProjectId = "mch-030-project";
    private const string CameraId = "camera.mch030";
    private const string RecipeId = "recipe.mch030";
    private const string InputKind = "Mono8";
    private static readonly string InputSha256 = new('A', 64);
    private static readonly string RecipeSha256 = new('B', 64);
    private static readonly ExternalInspectionConsumerIdentity Consumer = new(
        "OpenVisionLab.TwoDStudio",
        "1.0.0",
        new string('1', 40),
        "Clean");

    [Fact]
    public async Task TwoRunsAndTwoShotsKeepDistinctCorrelationAndRejectResetOrdinalReplay()
    {
        using var engine = await CreateEngineAsync();
        var firstRun = RuntimeIdentity(engine);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        var firstShot = await PreparePendingShotAsync(engine, firstRun, ordinal: 1);
        var firstTransaction = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var firstResult = await ApplyResultAsync(
            engine,
            firstRun,
            firstShot,
            firstTransaction,
            "run-a-shot-1");
        Assert.Equal("run-a-shot-1", firstResult.RunId);
        Assert.Equal(firstShot.AcquisitionId, firstResult.Correlation.AcquisitionId);
        Assert.Equal(firstShot.FrameId, firstResult.Correlation.FrameId);

        var secondShot = await PreparePendingShotAsync(engine, firstRun, ordinal: 2);
        var secondTransaction = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var secondResult = await ApplyResultAsync(
            engine,
            firstRun,
            secondShot,
            secondTransaction,
            "run-a-shot-2");
        Assert.Equal("run-a-shot-2", secondResult.RunId);
        Assert.NotEqual(firstResult.RunId, secondResult.RunId);
        Assert.NotEqual(firstTransaction, secondTransaction);
        Assert.NotEqual(firstShot.AcquisitionId, secondShot.AcquisitionId);
        Assert.NotEqual(firstShot.FrameId, secondShot.FrameId);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        var secondRun = RuntimeIdentity(engine);
        Assert.Equal(firstRun.ProjectId, secondRun.ProjectId);
        Assert.Equal(firstRun.RuntimeGeneration + 1, secondRun.RuntimeGeneration);
        Assert.Equal(0, engine.CurrentSnapshot.Cameras.Single().AcquisitionOrdinal);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var resetShot = await PreparePendingShotAsync(engine, secondRun, ordinal: 1);
        Assert.Equal(firstShot.AcquisitionId, resetShot.AcquisitionId);
        Assert.Equal(firstShot.FrameId, resetShot.FrameId);

        var lateFirstRunResult = await engine.EnqueueCommandAsync(
            CreateApplyCommand(
                firstRun,
                firstShot,
                firstTransaction,
                "run-a-shot-1"));
        Assert.False(lateFirstRunResult.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, lateFirstRunResult.ErrorCode);
        Assert.Equal(
            VirtualCameraState.AwaitingExternalResult,
            engine.CurrentSnapshot.Cameras.Single().State);
        Assert.Null(engine.CurrentSnapshot.Cameras.Single().Result);

        var resetTransaction = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var resetResult = await ApplyResultAsync(
            engine,
            secondRun,
            resetShot,
            resetTransaction,
            "run-b-shot-1");
        Assert.Equal("run-b-shot-1", resetResult.RunId);
        Assert.Equal(resetShot.AcquisitionId, resetResult.Correlation.AcquisitionId);
        Assert.Equal(resetShot.FrameId, resetResult.Correlation.FrameId);
        Assert.NotEqual(firstTransaction, resetTransaction);
        Assert.Equal(
            resetTransaction,
            engine.CurrentSnapshot.Cameras.Single().ExternalResultEvidence?.TransactionId);
    }

    private static async Task<FixedStepSimulationEngine> CreateEngineAsync()
    {
        var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = TimeSpan.FromMilliseconds(5)
        });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            new SimulationRuntimeConfiguration(
                Array.Empty<AxisConfiguration>(),
                Array.Empty<ChannelDefinition>(),
                Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>(),
                new[]
                {
                    new VirtualCameraConfiguration(
                        CameraId,
                        "MCH-030 camera",
                        exposureTicks: 1,
                        transferTicks: 1,
                        PlaceholderInspectionDecision.Pass)
                }),
            ProjectId));
        Assert.True(configured.IsAccepted, configured.Detail);
        return engine;
    }

    private static SimulationRuntimeIdentity RuntimeIdentity(FixedStepSimulationEngine engine) =>
        new(engine.CurrentSnapshot.ProjectId, engine.CurrentSnapshot.RuntimeGeneration);

    private static async Task<ShotIdentity> PreparePendingShotAsync(
        FixedStepSimulationEngine engine,
        SimulationRuntimeIdentity runtime,
        long ordinal)
    {
        var acquisitionId = $"{CameraId}/frame/{ordinal:D8}";
        var frame = new VirtualCameraFrameEvidence(
            acquisitionId,
            $"assets/mch-030/{ordinal:D8}.pgm",
            InputSha256,
            42,
            4,
            3,
            InputKind);
        var trigger = await engine.EnqueueCommandAsync(new TriggerVirtualCameraCommand(
            CameraId,
            RecipeId,
            frame,
            projectId: ProjectId,
            runtimeGeneration: runtime.RuntimeGeneration,
            waitForExternalResult: true));
        Assert.True(trigger.IsAccepted, trigger.Detail);

        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var camera = engine.CurrentSnapshot.Cameras.Single();
        Assert.Equal(VirtualCameraState.AwaitingExternalResult, camera.State);
        Assert.Equal(ordinal, camera.AcquisitionOrdinal);
        Assert.Equal(acquisitionId, camera.CurrentAcquisitionId);
        Assert.Null(camera.Result);
        return new(acquisitionId, frame.FrameId, frame.ContentSha256);
    }

    private static async Task<VirtualCameraExternalResultEvidence> ApplyResultAsync(
        FixedStepSimulationEngine engine,
        SimulationRuntimeIdentity runtime,
        ShotIdentity shot,
        Guid transactionId,
        string runId)
    {
        var applied = await engine.EnqueueCommandAsync(
            CreateApplyCommand(runtime, shot, transactionId, runId));
        Assert.True(applied.IsAccepted, applied.Detail);
        var evidence = engine.CurrentSnapshot.Cameras.Single().ExternalResultEvidence;
        Assert.NotNull(evidence);
        Assert.Equal(transactionId, evidence.TransactionId);
        Assert.Equal(runId, evidence.RunId);
        Assert.Equal(shot.AcquisitionId, evidence.Correlation.AcquisitionId);
        Assert.Equal(shot.FrameId, evidence.Correlation.FrameId);
        Assert.Equal(VirtualCameraState.FrameReady, engine.CurrentSnapshot.Cameras.Single().State);
        return evidence;
    }

    private static ApplyExternalInspectionResultCommand CreateApplyCommand(
        SimulationRuntimeIdentity runtime,
        ShotIdentity shot,
        Guid transactionId,
        string runId) =>
        new(
            runtime,
            CreateMessageChain(transactionId),
            CreateCorrelation(shot),
            CreateCorrelation(shot),
            Consumer,
            Consumer,
            acknowledgementAccepted: true,
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass,
            runId);

    private static ExternalInspectionCorrelationIdentity CreateCorrelation(ShotIdentity shot) =>
        new(
            ProjectId,
            "1.0",
            "sequence.mch030",
            "step.camera",
            CameraId,
            shot.AcquisitionId,
            shot.FrameId,
            "mm",
            "TwoD",
            InputKind,
            shot.InputSha256,
            RecipeSha256,
            Consumer);

    private static ExternalInspectionMessageChain CreateMessageChain(Guid transactionId)
    {
        var handoffMessageId = Guid.NewGuid();
        var acknowledgementMessageId = Guid.NewGuid();
        var resultMessageId = Guid.NewGuid();
        return new(
            transactionId,
            handoffMessageId,
            transactionId,
            handoffMessageId,
            acknowledgementMessageId,
            transactionId,
            handoffMessageId,
            acknowledgementMessageId,
            resultMessageId,
            new string('C', 64));
    }

    private sealed record ShotIdentity(
        string AcquisitionId,
        string FrameId,
        string InputSha256);
}
