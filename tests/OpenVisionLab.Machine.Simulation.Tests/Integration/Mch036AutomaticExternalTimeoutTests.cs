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

public sealed class Mch036AutomaticExternalTimeoutTests
{
    private const string ProjectId = "mch036-automatic-timeout";
    private const string SequenceId = "automatic-inspection";
    private const string TriggerStepId = "trigger-camera";
    private const string WaitStepId = "wait-vision-result";
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
    private static readonly ExternalInspectionConsumerIdentity ThreeDConsumer = new(
        "OpenVisionLab.ThreeDStudio",
        "1.0.0",
        new string('2', 40),
        "Clean");

    [Theory]
    [InlineData(true, true, true, "WallTimeout")]
    [InlineData(true, false, false, "WallTimeout")]
    [InlineData(false, true, false, "Command")]
    [InlineData(false, false, true, "Command")]
    [InlineData(false, false, false, "LogicalTick")]
    public void WallTimeoutWinsWhenCommandAndDeadlineAreReady(
        bool wallTimedOut,
        bool commandTaskCompleted,
        bool commandReady,
        string expected)
    {
        var actual = FixedStepSimulationEngine.ResolveAutomaticExternalInspectionWaitWake(
            wallTimedOut,
            commandTaskCompleted,
            commandReady);

        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public async Task SimulationTimeout_FailsClosedAndQuarantinesLateResult()
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 25,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var timedOut = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => !snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.Faulted
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Aborted);

        Assert.Equal(SimulationRunMode.Paused, timedOut.RunMode);
        var late = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(fixture, pending, sequenceId: "published-sequence-alias"));
        Assert.False(late.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, late.ErrorCode);

        var events = await fixture.ReadEventsAsync();
        var timeout = Assert.Single(events, item => item.Code == "AutomaticExternalInspectionTimedOut");
        Assert.Contains("timeoutKind=SimulationClock", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("sequence=automatic-inspection", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("step=wait-vision-result", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=25", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("limitMs=25", timeout.Message, StringComparison.Ordinal);
        var quarantined = Assert.Single(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
        Assert.Contains("reason=SimulationTimeout", quarantined.Message, StringComparison.Ordinal);
        Assert.Contains("no runtime mutation", quarantined.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WallTimeout_FailsClosedAndIdentifiesWallClock()
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 500,
            wallTimeout: TimeSpan.FromMilliseconds(40));
        await fixture.ArmAndStartAsync();

        await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => !snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.Faulted
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Aborted);

        var events = await fixture.ReadEventsAsync();
        var timeout = Assert.Single(events, item => item.Code == "AutomaticExternalInspectionTimedOut");
        Assert.Contains("timeoutKind=WallClock", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("limitMs=40", timeout.Message, StringComparison.Ordinal);
        Assert.Contains(
            events,
            item => item.Code == "AutomaticExternalInspectionFailedClosed"
                && item.Message.Contains("automatic retry is disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserPauseDuringExternalResultWait_RemainsPausedUntilExplicitPlay()
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 10_000,
            wallTimeout: TimeSpan.FromSeconds(10));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var pause = await fixture.Engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(pause.IsAccepted, pause.Detail);

        var userPaused = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => snapshot.SequenceDebug.PauseReason == SequenceDebugPauseReason.User);
        Assert.Equal(pending.SimulationTime, userPaused.SimulationTime);

        var applied = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(fixture, pending));
        Assert.True(applied.IsAccepted, applied.Detail);

        var afterResult = fixture.Engine.CurrentSnapshot;
        Assert.Equal(SimulationRunMode.Paused, afterResult.RunMode);
        Assert.Equal(userPaused.TickIndex, afterResult.TickIndex);
        Assert.Equal(userPaused.SimulationTime, afterResult.SimulationTime);
        Assert.Equal(SequenceDebugPauseReason.User, afterResult.SequenceDebug.PauseReason);

        var play = await fixture.Engine.EnqueueCommandAsync(new PlayCommand());
        Assert.True(play.IsAccepted, play.Detail);
        var completed = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => !snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Completed);
        Assert.True(completed.SimulationTime > userPaused.SimulationTime);
    }

    [Theory]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Ng)]
    [InlineData(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError)]
    [InlineData(ExternalInspectionResultStatus.Cancelled, ExternalInspectionOutcome.Indeterminate)]
    public async Task ThreeDHeightMapWallTimeout_QuarantinesAllLateResultStatuses(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 500,
            wallTimeout: TimeSpan.FromMilliseconds(40));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var timedOut = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => !snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.Faulted
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Aborted);

        Assert.Equal(SimulationRunMode.Paused, timedOut.RunMode);
        var late = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                consumer: ThreeDConsumer,
                modality: "ThreeD",
                inputKind: "HeightMap",
                status: status,
                outcome: outcome));
        Assert.False(late.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, late.ErrorCode);

        var events = await fixture.ReadEventsAsync();
        var timeout = Assert.Single(events, item => item.Code == "AutomaticExternalInspectionTimedOut");
        Assert.Contains("timeoutKind=WallClock", timeout.Message, StringComparison.Ordinal);
        var quarantined = Assert.Single(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
        Assert.Contains("reason=WallTimeout", quarantined.Message, StringComparison.Ordinal);
        Assert.Contains("no runtime mutation", quarantined.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Ng)]
    [InlineData(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError)]
    [InlineData(ExternalInspectionResultStatus.Cancelled, ExternalInspectionOutcome.Indeterminate)]
    public async Task ThreeDHeightMapSimulationTimeout_QuarantinesAllLateResultStatuses(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 25,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var timedOut = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => !snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.Faulted
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Aborted);

        Assert.Equal(SimulationRunMode.Paused, timedOut.RunMode);
        var late = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                consumer: ThreeDConsumer,
                modality: "ThreeD",
                inputKind: "HeightMap",
                status: status,
                outcome: outcome));
        Assert.False(late.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, late.ErrorCode);

        var events = await fixture.ReadEventsAsync();
        var timeout = Assert.Single(events, item => item.Code == "AutomaticExternalInspectionTimedOut");
        Assert.Contains("timeoutKind=SimulationClock", timeout.Message, StringComparison.Ordinal);
        var quarantined = Assert.Single(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
        Assert.Contains("reason=SimulationTimeout", quarantined.Message, StringComparison.Ordinal);
        Assert.Contains("no runtime mutation", quarantined.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeDHeightMapRepeatedLateResults_QuarantineWithoutMutation()
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 25,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var timedOut = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => !snapshot.AutomaticRun.IsActive
                && Assert.Single(snapshot.Cameras).State == VirtualCameraState.Faulted
                && Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Aborted);

        Assert.Equal(SimulationRunMode.Paused, timedOut.RunMode);
        var firstLate = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                sequenceId: "published-sequence-alias",
                consumer: ThreeDConsumer,
                modality: "ThreeD",
                inputKind: "HeightMap",
                status: ExternalInspectionResultStatus.Completed,
                outcome: ExternalInspectionOutcome.Pass));
        var secondLate = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                sequenceId: "published-sequence-alias",
                consumer: ThreeDConsumer,
                modality: "ThreeD",
                inputKind: "HeightMap",
                status: ExternalInspectionResultStatus.Completed,
                outcome: ExternalInspectionOutcome.Ng));

        Assert.False(firstLate.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, firstLate.ErrorCode);
        Assert.False(secondLate.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, secondLate.ErrorCode);

        var unchanged = fixture.Engine.CurrentSnapshot;
        Assert.False(unchanged.AutomaticRun.IsActive);
        Assert.Equal(SimulationRunMode.Paused, unchanged.RunMode);
        Assert.Equal(VirtualCameraState.Faulted, Assert.Single(unchanged.Cameras).State);
        Assert.Null(Assert.Single(unchanged.Cameras).ExternalResultEvidence);
        Assert.Equal(SequenceExecutionStatus.Aborted, Assert.Single(unchanged.Sequences).Status);

        var events = await fixture.ReadEventsAsync();
        var quarantined = events
            .Where(item => item.Code == "AutomaticExternalInspectionLateResultQuarantined")
            .ToArray();
        Assert.Equal(2, quarantined.Length);
        Assert.All(
            quarantined,
            item =>
            {
                Assert.Contains("reason=SimulationTimeout", item.Message, StringComparison.Ordinal);
                Assert.Contains("no runtime mutation", item.Message, StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Ng)]
    [InlineData(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError)]
    [InlineData(ExternalInspectionResultStatus.Cancelled, ExternalInspectionOutcome.Indeterminate)]
    public async Task ThreeDHeightMapResultBeforeTimeout_AppliesAllResultStatuses(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 250,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var applied = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                consumer: ThreeDConsumer,
                modality: "ThreeD",
                inputKind: "HeightMap",
                status: status,
                outcome: outcome));
        Assert.True(applied.IsAccepted, applied.Detail);

        if (status == ExternalInspectionResultStatus.Completed)
        {
            var completed = await WaitForSnapshotAsync(
                fixture.Engine,
                snapshot => Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Completed);
            var camera = Assert.Single(completed.Cameras);
            Assert.Equal(VirtualCameraState.FrameReady, camera.State);
            Assert.Equal(
                outcome == ExternalInspectionOutcome.Pass
                    ? PlaceholderInspectionDecision.Pass
                    : PlaceholderInspectionDecision.Fail,
                Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result).Decision);
        }
        else
        {
            var failedClosed = fixture.Engine.CurrentSnapshot;
            Assert.Equal(SimulationRunMode.Paused, failedClosed.RunMode);
            Assert.True(failedClosed.AutomaticRun.IsActive);
            Assert.Equal(VirtualCameraState.Faulted, Assert.Single(failedClosed.Cameras).State);
            Assert.Equal(SequenceExecutionStatus.Running, Assert.Single(failedClosed.Sequences).Status);

            var abort = await fixture.Engine.EnqueueCommandAsync(new AbortSequenceCommand(SequenceId));
            Assert.True(abort.IsAccepted, abort.Detail);
            Assert.False(fixture.Engine.CurrentSnapshot.AutomaticRun.IsActive);
        }

        var events = await fixture.ReadEventsAsync();
        Assert.DoesNotContain(events, item => item.Code == "AutomaticExternalInspectionTimedOut");
        Assert.DoesNotContain(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
    }

    [Fact]
    public async Task ResultBeforeTimeout_CompletesWithoutTimeoutOrQuarantine()
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 250,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var applied = await fixture.Engine.EnqueueCommandAsync(CreateResultCommand(fixture, pending));
        Assert.True(applied.IsAccepted, applied.Detail);

        var completed = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Sequences).Status == SequenceExecutionStatus.Completed);
        Assert.Equal(VirtualCameraState.FrameReady, Assert.Single(completed.Cameras).State);

        var events = await fixture.ReadEventsAsync();
        Assert.DoesNotContain(events, item => item.Code == "AutomaticExternalInspectionTimedOut");
        Assert.DoesNotContain(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
    }

    [Fact]
    public async Task AbortWhileWaiting_QuarantinesLateResultWithoutMutation()
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 250,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var abort = await fixture.Engine.EnqueueCommandAsync(new AbortSequenceCommand(SequenceId));
        Assert.True(abort.IsAccepted, abort.Detail);
        Assert.False(fixture.Engine.CurrentSnapshot.AutomaticRun.IsActive);
        Assert.Equal(VirtualCameraState.Faulted, Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).State);

        var late = await fixture.Engine.EnqueueCommandAsync(CreateResultCommand(fixture, pending));
        Assert.False(late.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, late.ErrorCode);

        var events = await fixture.ReadEventsAsync();
        var quarantined = Assert.Single(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
        Assert.Contains("reason=Aborted", quarantined.Message, StringComparison.Ordinal);
        Assert.Contains("no runtime mutation", quarantined.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass)]
    [InlineData(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Ng)]
    [InlineData(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError)]
    [InlineData(ExternalInspectionResultStatus.Cancelled, ExternalInspectionOutcome.Indeterminate)]
    public async Task ThreeDHeightMapAbortWhileWaiting_QuarantinesAllLateResultStatuses(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome)
    {
        await using var fixture = await CreateFixtureAsync(
            waitTimeoutMs: 250,
            wallTimeout: TimeSpan.FromSeconds(1));
        await fixture.ArmAndStartAsync();

        var pending = await WaitForSnapshotAsync(
            fixture.Engine,
            snapshot => Assert.Single(snapshot.Cameras).State == VirtualCameraState.AwaitingExternalResult);
        var abort = await fixture.Engine.EnqueueCommandAsync(new AbortSequenceCommand(SequenceId));
        Assert.True(abort.IsAccepted, abort.Detail);
        Assert.False(fixture.Engine.CurrentSnapshot.AutomaticRun.IsActive);
        Assert.Equal(VirtualCameraState.Faulted, Assert.Single(fixture.Engine.CurrentSnapshot.Cameras).State);

        var late = await fixture.Engine.EnqueueCommandAsync(
            CreateResultCommand(
                fixture,
                pending,
                consumer: ThreeDConsumer,
                modality: "ThreeD",
                inputKind: "HeightMap",
                status: status,
                outcome: outcome));
        Assert.False(late.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ExternalInspectionNotPending, late.ErrorCode);

        var events = await fixture.ReadEventsAsync();
        var quarantined = Assert.Single(
            events,
            item => item.Code == "AutomaticExternalInspectionLateResultQuarantined");
        Assert.Contains("reason=Aborted", quarantined.Message, StringComparison.Ordinal);
        Assert.Contains("no runtime mutation", quarantined.Message, StringComparison.Ordinal);
    }

    private static VirtualCameraExternalSource CreateSource() =>
        new(
            "assets/external-result.pgm",
            InputSha256,
            42,
            16,
            12,
            "Mono8");

    private static async Task<Fixture> CreateFixtureAsync(int waitTimeoutMs, TimeSpan wallTimeout)
    {
        var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 100,
            EventBufferCapacity = 256,
            AutomaticExternalInspectionWallTimeout = wallTimeout
        });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntime(waitTimeoutMs), ProjectId));
        Assert.True(configured.IsAccepted, configured.Detail);
        return new(
            engine,
            new SimulationRuntimeIdentity(
                engine.CurrentSnapshot.ProjectId!,
                engine.CurrentSnapshot.RuntimeGeneration));
    }

    private static SimulationRuntimeConfiguration CreateRuntime(int waitTimeoutMs)
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
                    NextStepId = WaitStepId
                },
                new SequenceStepDefinition
                {
                    Id = WaitStepId,
                    Name = "Wait Vision Result",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = CameraId,
                    TimeoutMs = waitTimeoutMs,
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
        string? sequenceId = null,
        ExternalInspectionConsumerIdentity? consumer = null,
        string modality = "TwoD",
        string inputKind = "Image",
        ExternalInspectionResultStatus status = ExternalInspectionResultStatus.Completed,
        ExternalInspectionOutcome outcome = ExternalInspectionOutcome.Pass)
    {
        var camera = Assert.Single(pending.Cameras);
        var frame = Assert.IsType<VirtualCameraFrameEvidence>(camera.FrameEvidence);
        var correlation = new ExternalInspectionCorrelationIdentity(
            ProjectId,
            "1.0",
            sequenceId ?? SequenceId,
            TriggerStepId,
            CameraId,
            camera.CurrentAcquisitionId!,
            frame.FrameId,
            "mm",
            modality,
            inputKind,
            frame.ContentSha256,
            RecipeSha256,
            consumer ?? Consumer);
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
            consumer ?? Consumer,
            consumer ?? Consumer,
            acknowledgementAccepted: true,
            status,
            outcome,
            "mch036-run");
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
        public async Task ArmAndStartAsync()
        {
            var arm = await Engine.EnqueueCommandAsync(
                new ArmAutomaticExternalInspectionCommand(
                    RuntimeIdentity,
                    SequenceId,
                    new Dictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal)
                    {
                        [CameraId] = CreateSource()
                    }));
            Assert.True(arm.IsAccepted, arm.Detail);

            var start = await Engine.EnqueueCommandAsync(
                new StartAutomaticRunCommand(waitForExternalResult: true));
            Assert.True(start.IsAccepted, start.Detail);
        }

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
