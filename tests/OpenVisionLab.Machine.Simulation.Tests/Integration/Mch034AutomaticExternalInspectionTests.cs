using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch034AutomaticExternalInspectionTests
{
    private const string ProjectId = "mch034-automatic-external";
    private const string SequenceId = "automatic-inspection";
    private const string TriggerStepId = "trigger-camera";
    private const string CameraId = "camera.external";
    private const string RecipeId = "recipe.external";
    private static readonly string InputSha256 = new('A', 64);
    private static readonly string RecipeSha256 = new('B', 64);
    private static readonly string ResultSha256 = new('C', 64);
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);
    private static readonly ExternalInspectionConsumerIdentity Consumer = new(
        "OpenVisionLab.TwoDStudio",
        "1.0.0",
        new string('1', 40),
        "Clean");

    [Fact]
    public async Task AutomaticExternalRun_ArmsPausesPublishesAndPassesSequence()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = CreateSource();

        var arm = await fixture.Engine.EnqueueCommandAsync(
            new ArmAutomaticExternalInspectionCommand(
                fixture.RuntimeIdentity,
                SequenceId,
                new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                {
                    [CameraId] = source
                }));
        Assert.True(arm.IsAccepted, arm.Detail);

        var start = await fixture.Engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(waitForExternalResult: true));
        Assert.True(start.IsAccepted, start.Detail);

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.RunMode == SimulationRunMode.Paused
                && snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var camera = Assert.Single(pending.Cameras);
        Assert.Equal(source.CreateEvidence(camera.CurrentAcquisitionId!), camera.FrameEvidence);
        Assert.Equal("wait-vision-result", Assert.Single(pending.Sequences).CurrentStepId);

        var playWhileWaiting = await fixture.Engine.EnqueueCommandAsync(new PlayCommand());
        Assert.False(playWhileWaiting.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.InvalidRunMode, playWhileWaiting.ErrorCode);
        Assert.Equal(
            VirtualCameraState.AwaitingExternalResult,
            Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).State);

        var apply = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(fixture, pending, ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass));
        Assert.True(apply.IsAccepted, apply.Detail);

        var completed = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Completed);
        Assert.Equal(VirtualCameraState.FrameReady, Assert.Single(completed.Cameras).State);
        Assert.Equal(PlaceholderInspectionDecision.Pass, Assert.Single(completed.Cameras).Result!.Decision);
        Assert.False(completed.AutomaticRun.IsActive);

        var events = await fixture.ReadEventsAsync();
        Assert.Single(events, item => item.Code == "AutomaticExternalInspectionArmed");
        Assert.Single(events, item => item.Code == "AutomaticExternalInspectionRequestReady");
        Assert.Single(events, item => item.Code == "AutomaticExternalInspectionResumed");
        Assert.DoesNotContain(events, item => item.Code == "AutomaticExternalInspectionFailedClosed");
    }

    [Fact]
    public async Task AutomaticExternalRun_FastForwardCannotBypassPendingExternalResult()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = CreateSource();
        var arm = await fixture.Engine.EnqueueCommandAsync(
            new ArmAutomaticExternalInspectionCommand(
                fixture.RuntimeIdentity,
                SequenceId,
                new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                {
                    [CameraId] = source
                }));
        Assert.True(arm.IsAccepted, arm.Detail);

        var start = await fixture.Engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(waitForExternalResult: true));
        Assert.True(start.IsAccepted, start.Detail);

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.RunMode == SimulationRunMode.Paused
                && snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var fastForward = await fixture.Engine.EnqueueCommandAsync(new FastForwardCommand(10));

        Assert.False(fastForward.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.InvalidRunMode, fastForward.ErrorCode);
        var unchanged = fixture.Engine.CurrentSnapshot;
        Assert.Equal(pending.TickIndex, unchanged.TickIndex);
        Assert.Equal(pending.SimulationTime, unchanged.SimulationTime);
        Assert.Equal(VirtualCameraState.AwaitingExternalResult, Assert.Single(unchanged.Cameras).State);
        Assert.True(unchanged.AutomaticRun.IsActive);

        var events = await fixture.ReadEventsAsync();
        Assert.DoesNotContain(events, item => item.Code == "FastForwardCompleted");
    }

    [Fact]
    public async Task AutomaticExternalRun_NgUsesFailureBranchAndCompletes()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = CreateSource();
        Assert.True((await fixture.Engine.EnqueueCommandAsync(
            new ArmAutomaticExternalInspectionCommand(
                fixture.RuntimeIdentity,
                SequenceId,
                new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                {
                    [CameraId] = source
                }))).IsAccepted);
        Assert.True((await fixture.Engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(waitForExternalResult: true))).IsAccepted);

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.RunMode == SimulationRunMode.Paused
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var apply = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(fixture, pending, ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Ng));
        Assert.True(apply.IsAccepted, apply.Detail);

        var completed = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Completed);
        Assert.Equal(PlaceholderInspectionDecision.Fail, Assert.Single(completed.Cameras).Result!.Decision);
        Assert.Contains(
            await fixture.ReadEventsAsync(),
            item => item.Code == "SequenceStepTransition"
                && item.Message.Contains("wait-vision-result -> ng-complete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError)]
    [InlineData(ExternalInspectionResultStatus.Cancelled, ExternalInspectionOutcome.NotMeasured)]
    public async Task AutomaticExternalRun_NonDecisionResultFailsClosedAndRequiresAbort(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        await using var fixture = await CreateFixtureAsync();
        var source = CreateSource();
        Assert.True((await fixture.Engine.EnqueueCommandAsync(
            new ArmAutomaticExternalInspectionCommand(
                fixture.RuntimeIdentity,
                SequenceId,
                new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                {
                    [CameraId] = source
                }))).IsAccepted);
        Assert.True((await fixture.Engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(waitForExternalResult: true))).IsAccepted);

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.RunMode == SimulationRunMode.Paused
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var apply = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(fixture, pending, status, outcome));
        var failedClosed = fixture.Engine.CurrentSnapshot;

        Assert.True(apply.IsAccepted, apply.Detail);
        Assert.Equal(SimulationRunMode.Paused, failedClosed.RunMode);
        Assert.True(failedClosed.AutomaticRun.IsActive);
        Assert.Equal(VirtualCameraState.Faulted, Assert.Single(failedClosed.Cameras).State);
        Assert.Equal(SequenceExecutionStatus.Running, Assert.Single(failedClosed.Sequences).Status);

        var abort = await fixture.Engine.EnqueueCommandAsync(new AbortSequenceCommand(SequenceId));
        Assert.True(abort.IsAccepted, abort.Detail);
        Assert.False(fixture.Engine.CurrentSnapshot.AutomaticRun.IsActive);
        Assert.Contains(
            await fixture.ReadEventsAsync(),
            item => item.Code == "AutomaticExternalInspectionFailedClosed");
    }

    [Fact]
    public async Task AutomaticExternalRun_ExecutionErrorResetThenRetry_RearmsOnNewRuntimeGeneration()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = CreateSource();
        Assert.True((await fixture.Engine.EnqueueCommandAsync(
            new ArmAutomaticExternalInspectionCommand(
                fixture.RuntimeIdentity,
                SequenceId,
                new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                {
                    [CameraId] = source
                }))).IsAccepted);
        Assert.True((await fixture.Engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(waitForExternalResult: true))).IsAccepted);

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.RunMode == SimulationRunMode.Paused
                && snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var failed = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                ExternalInspectionResultStatus.Failed,
                ExternalInspectionOutcome.ExecutionError));
        Assert.True(failed.IsAccepted, failed.Detail);

        var faulted = await fixture.Engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(faulted.IsAccepted, faulted.Detail);
        var faultedSnapshot = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.AutomaticRun.IsActive == false
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Faulted);
        var faultedGeneration = faultedSnapshot.RuntimeGeneration;
        Assert.Equal(SequenceId, faultedSnapshot.ResetRetrySequenceId);
        Assert.Equal(VirtualCameraState.Faulted, Assert.Single(faultedSnapshot.Cameras).State);

        var reset = await fixture.Engine.EnqueueCommandAsync(new ResetCommand());
        var resetSnapshot = fixture.Engine.CurrentSnapshot;
        Assert.True(reset.IsAccepted, reset.Detail);
        Assert.Equal(faultedGeneration + 1, resetSnapshot.RuntimeGeneration);
        Assert.Equal(SequenceId, resetSnapshot.ResetRetrySequenceId);
        Assert.Equal(SequenceExecutionStatus.Ready, Assert.Single(resetSnapshot.Sequences).Status);
        Assert.Equal(VirtualCameraState.Idle, Assert.Single(resetSnapshot.Cameras).State);

        var retry = await fixture.Engine.EnqueueCommandAsync(new RetrySequenceCommand(SequenceId));
        var retriedSnapshot = fixture.Engine.CurrentSnapshot;
        Assert.True(retry.IsAccepted, retry.Detail);
        Assert.Null(retriedSnapshot.ResetRetrySequenceId);
        Assert.Equal(resetSnapshot.RuntimeGeneration, retriedSnapshot.RuntimeGeneration);
        Assert.Equal(SimulationRunMode.Paused, retriedSnapshot.RunMode);
        Assert.True(retriedSnapshot.AutomaticRun.IsActive);
        Assert.Equal(SequenceExecutionStatus.Running, Assert.Single(retriedSnapshot.Sequences).Status);
        Assert.Equal(TriggerStepId, Assert.Single(retriedSnapshot.Sequences).CurrentStepId);

        var currentRuntime = new SimulationRuntimeIdentity(
            retriedSnapshot.ProjectId!,
            retriedSnapshot.RuntimeGeneration);
        var rearm = await fixture.Engine.EnqueueCommandAsync(
            new ArmAutomaticExternalInspectionCommand(
                currentRuntime,
                SequenceId,
                new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                {
                    [CameraId] = source
                }));
        Assert.True(rearm.IsAccepted, rearm.Detail);

        var play = await fixture.Engine.EnqueueCommandAsync(new PlayCommand());
        Assert.True(play.IsAccepted, play.Detail);
        var retriedPending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.RunMode == SimulationRunMode.Paused
                && snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        Assert.Equal(resetSnapshot.RuntimeGeneration, retriedPending.RuntimeGeneration);
        Assert.Equal(SequenceId, Assert.Single(retriedPending.Sequences).SequenceId);
    }

    [Fact]
    public async Task AutomaticExternalRun_RequiresPreflightArmAndDoesNotChangeReadySequence()
    {
        await using var fixture = await CreateFixtureAsync();

        var start = await fixture.Engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(waitForExternalResult: true));

        Assert.False(start.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AutomaticRunStartRejected, start.ErrorCode);
        Assert.Equal(SequenceExecutionStatus.Ready, Assert.Single(fixture.Engine.CurrentSnapshot.Sequences).Status);
        Assert.False(fixture.Engine.CurrentSnapshot.AutomaticRun.IsActive);
    }

    private static VirtualCameraExternalSource CreateSource() =>
        new(
            "assets/external-result.pgm",
            InputSha256,
            42,
            16,
            12,
            "Mono8");

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 100,
            EventBufferCapacity = 256
        });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntime(), ProjectId));
        Assert.True(configured.IsAccepted, configured.Detail);
        return new(
            engine,
            new SimulationRuntimeIdentity(
                engine.CurrentSnapshot.ProjectId!,
                engine.CurrentSnapshot.RuntimeGeneration));
    }

    private static SimulationRuntimeConfiguration CreateRuntime()
    {
        var definition = new SequenceDefinition
        {
            Id = SequenceId,
            Name = "Automatic Inspection",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = TriggerStepId,
                    Name = "Trigger Camera",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = CameraId,
                    Parameter = RecipeId,
                    NextStepId = "wait-vision-result"
                },
                new SequenceStepDefinition
                {
                    Id = "wait-vision-result",
                    Name = "Wait Vision Result",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = CameraId,
                    TimeoutMs = 500,
                    NextStepId = "pass-complete",
                    FailureStepId = "ng-complete"
                },
                new SequenceStepDefinition
                {
                    Id = "pass-complete",
                    Name = "Pass Complete",
                    Action = SequenceStepAction.Complete
                },
                new SequenceStepDefinition
                {
                    Id = "ng-complete",
                    Name = "NG Complete",
                    Action = SequenceStepAction.Complete
                }
            }
        };
        var compilation = new SequenceCompiler().Compile(
            definition,
            new SequenceCompilationTargets(
                new Dictionary<string, ChannelKind>(StringComparer.Ordinal),
                Array.Empty<string>(),
                new[] { CameraId }));
        Assert.True(
            compilation.IsSuccess,
            string.Join(Environment.NewLine, compilation.Errors.Select(error => error.Message)));

        return new SimulationRuntimeConfiguration(
            Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
            Array.Empty<ChannelDefinition>(),
            new[] { compilation.Sequence! },
            new[]
            {
                new VirtualCameraConfiguration(
                    CameraId,
                    "External Camera",
                    exposureTicks: 1,
                    transferTicks: 1,
                    PlaceholderInspectionDecision.Pass)
            },
            new AutomaticRunConfiguration(
                SequenceId,
                StartInputId: null,
                StartInputValue: true,
                Repeat: false,
                RepeatDelayMilliseconds: 0));
    }

    private static ApplyExternalInspectionResultCommand CreateResultCommand(
        Fixture fixture,
        SimulationSnapshot pending,
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        var camera = Assert.Single(pending.Cameras);
        var frame = Assert.IsType<VirtualCameraFrameEvidence>(camera.FrameEvidence);
        var correlation = new ExternalInspectionCorrelationIdentity(
            ProjectId,
            "1.0",
            SequenceId,
            TriggerStepId,
            CameraId,
            camera.CurrentAcquisitionId!,
            frame.FrameId,
            "mm",
            "TwoD",
            "Image",
            frame.ContentSha256,
            RecipeSha256,
            Consumer);
        var transactionId = Guid.NewGuid();
        var handoffMessageId = Guid.NewGuid();
        var acknowledgementMessageId = Guid.NewGuid();
        var chain = new ExternalInspectionMessageChain(
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
        return new ApplyExternalInspectionResultCommand(
            fixture.RuntimeIdentity,
            chain,
            correlation,
            correlation,
            Consumer,
            Consumer,
            acknowledgementAccepted: true,
            status,
            outcome,
            "mch034-run");
    }

    private static async Task<SimulationSnapshot> WaitForSnapshotAsync(
        FixedStepSimulationEngine engine,
        Func<SimulationSnapshot, bool> predicate)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var snapshot = engine.CurrentSnapshot;
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(2);
        }

        throw new TimeoutException(
            $"The expected automatic external state was not reached. Last tick: {engine.CurrentSnapshot.TickIndex}.");
    }

    private sealed record Fixture(
        FixedStepSimulationEngine Engine,
        SimulationRuntimeIdentity RuntimeIdentity) : IAsyncDisposable
    {
        public async Task<IReadOnlyList<SimulationEvent>> ReadEventsAsync()
        {
            await Engine.StopAsync();
            var events = new List<SimulationEvent>();
            await foreach (var item in Engine.EventReader.ReadAllAsync())
            {
                events.Add(item);
            }

            return events;
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.StopAsync();
            Engine.Dispose();
        }
    }
}
