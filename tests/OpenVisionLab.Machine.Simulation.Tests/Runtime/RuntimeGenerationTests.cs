using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class RuntimeGenerationTests
{
    [Fact]
    public async Task ResetAdvancesGenerationAndRejectsStaleManualCameraRequest()
    {
        using var engine = await CreateStartedEngineAsync();
        try
        {
            var configured = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(CreateRuntime(), "project-a"));
            Assert.True(configured.IsAccepted, configured.Detail);
            var firstGeneration = engine.CurrentSnapshot.RuntimeGeneration;

            await EnterPausedManualControlAsync(engine);
            var reset = await engine.EnqueueCommandAsync(new ResetCommand());
            Assert.True(reset.IsAccepted, reset.Detail);
            var resetSnapshot = engine.CurrentSnapshot;

            Assert.Equal("project-a", resetSnapshot.ProjectId);
            Assert.Equal(firstGeneration + 1, resetSnapshot.RuntimeGeneration);

            await EnterPausedManualControlAsync(engine);
            var stale = await engine.EnqueueCommandAsync(
                CreateTrigger("project-a", firstGeneration));
            Assert.False(stale.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.CameraTriggerRejected, stale.ErrorCode);

            var current = await engine.EnqueueCommandAsync(
                CreateTrigger("project-a", resetSnapshot.RuntimeGeneration));
            Assert.True(current.IsAccepted, current.Detail);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task ReconfiguredProjectRejectsSameCameraRequestFromPreviousProject()
    {
        using var engine = await CreateStartedEngineAsync();
        try
        {
            var first = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(CreateRuntime(), "project-a"));
            Assert.True(first.IsAccepted, first.Detail);
            var staleGeneration = engine.CurrentSnapshot.RuntimeGeneration;

            var second = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(CreateRuntime(), "project-b"));
            Assert.True(second.IsAccepted, second.Detail);
            var currentSnapshot = engine.CurrentSnapshot;
            Assert.Equal("project-b", currentSnapshot.ProjectId);
            Assert.Equal(staleGeneration + 1, currentSnapshot.RuntimeGeneration);

            await EnterPausedManualControlAsync(engine);
            var stale = await engine.EnqueueCommandAsync(
                CreateTrigger("project-a", staleGeneration));
            Assert.False(stale.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.CameraTriggerRejected, stale.ErrorCode);

            var current = await engine.EnqueueCommandAsync(
                CreateTrigger("project-b", currentSnapshot.RuntimeGeneration));
            Assert.True(current.IsAccepted, current.Detail);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("manual")]
    [InlineData("move")]
    public async Task QueuedOldIdentityIsRejectedUsingOwnedStateBeforeTheNewSnapshotIsPublished(string kind)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdNextApplication = 0;
        using var engine = new FixedStepSimulationEngine(new SimulationSettings(), point =>
        {
            if (point == SimulationEngineFaultPoint.AfterCommandApplication
                && Interlocked.Exchange(ref holdNextApplication, 0) == 1)
            {
                applied.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        await engine.StartAsync();
        SimulationCommand? delayed = null;
        try
        {
            Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateAxisRuntime(), "project-a"))).IsAccepted);
            var old = engine.CurrentSnapshot;
            delayed = CreateBoundCommand(kind, new SimulationRuntimeIdentity(old.ProjectId, old.RuntimeGeneration));
            Volatile.Write(ref holdNextApplication, 1);
            var replacement = engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateAxisRuntime(), "project-b"));
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Internal state has changed, but no new public snapshot exists yet.
            Assert.Equal(old.RuntimeGeneration, engine.CurrentSnapshot.RuntimeGeneration);
            var queued = engine.EnqueueCommandAsync(delayed);
            Assert.False(queued.IsCompleted);
            release.SetResult();

            Assert.True((await replacement.WaitAsync(TimeSpan.FromSeconds(5))).IsAccepted);
            var result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, result.ErrorCode);
            Assert.Equal(delayed.CommandId, result.CommandId);
            Assert.Equal("project-b", engine.CurrentSnapshot.ProjectId);
            Assert.Equal(old.RuntimeGeneration + 1, engine.CurrentSnapshot.RuntimeGeneration);
            Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
            Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);
            Assert.Equal(AxisState.Idle, Assert.Single(engine.CurrentSnapshot.Axes).State);
            var trace = engine.CommandTrace.Last();
            Assert.False(trace.IsAccepted);
            Assert.False(trace.IsReplayable);
            Assert.Equal(result.ErrorCode, trace.ErrorCode);
            Assert.Equal(result.AppliedTick, trace.AppliedTick);
        }
        finally
        {
            release.TrySetResult();
            await engine.StopAsync();
        }

        var events = new List<SimulationEvent>();
        await foreach (var item in engine.ReadCanonicalEventsAsync())
        {
            if (item.CommandId == delayed!.CommandId) events.Add(item);
        }
        var rejection = Assert.Single(events);
        Assert.Equal("Command", rejection.Category);
        Assert.Equal("CommandRejected", rejection.Code);
        Assert.Contains("project-a", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("project-b", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetRejectsQueuedOldIdentityBeforePublishingNewSnapshot()
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdNextApplication = 0;
        using var engine = new FixedStepSimulationEngine(new SimulationSettings(), point =>
        {
            if (point == SimulationEngineFaultPoint.AfterCommandApplication
                && Interlocked.Exchange(ref holdNextApplication, 0) == 1)
            {
                applied.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        await engine.StartAsync();
        StepCommand? delayed = null;
        try
        {
            Assert.True((await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(CreateAxisRuntime(), "project-reset"))).IsAccepted);
            var old = engine.CurrentSnapshot;
            var oldIdentity = new SimulationRuntimeIdentity(old.ProjectId, old.RuntimeGeneration);
            delayed = new StepCommand { ExpectedRuntime = oldIdentity };
            Volatile.Write(ref holdNextApplication, 1);
            var reset = engine.EnqueueCommandAsync(new ResetCommand());
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Reset has changed owned state, but the old public snapshot is still visible.
            Assert.Equal(old.RuntimeGeneration, engine.CurrentSnapshot.RuntimeGeneration);
            var queued = engine.EnqueueCommandAsync(delayed);
            Assert.False(queued.IsCompleted);
            release.SetResult();

            var resetResult = await reset.WaitAsync(TimeSpan.FromSeconds(5));
            var staleResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(resetResult.IsAccepted, resetResult.Detail);
            Assert.False(staleResult.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, staleResult.ErrorCode);
            Assert.Equal(delayed.CommandId, staleResult.CommandId);

            var current = engine.CurrentSnapshot;
            Assert.Equal("project-reset", current.ProjectId);
            Assert.Equal(old.RuntimeGeneration + 1, current.RuntimeGeneration);
            Assert.Equal(0, current.TickIndex);
            Assert.Equal(TimeSpan.Zero, current.SimulationTime);
            Assert.Equal(SimulationRunMode.Paused, current.RunMode);
            Assert.Equal(SimulationControlOwner.Definition, current.ControlOwner);
            Assert.Equal(AxisState.Idle, Assert.Single(current.Axes).State);
            var trace = engine.CommandTrace.Last();
            Assert.Equal(nameof(StepCommand), trace.CommandType);
            Assert.False(trace.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, trace.ErrorCode);
        }
        finally
        {
            release.TrySetResult();
            await engine.StopAsync();
        }

        var events = new List<SimulationEvent>();
        await foreach (var item in engine.ReadCanonicalEventsAsync())
        {
            if (item.CommandId == delayed!.CommandId) events.Add(item);
        }

        var rejection = Assert.Single(events);
        Assert.Equal("Command", rejection.Category);
        Assert.Equal("CommandRejected", rejection.Code);
        Assert.Contains("project-reset", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("generation 1", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("generation 2", rejection.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pause", "project")]
    [InlineData("manual", "project")]
    [InlineData("move", "project")]
    [InlineData("pause", null)]
    [InlineData("manual", null)]
    [InlineData("move", null)]
    public async Task MatchingIdentityRetainsNormalCommandBehavior(string kind, string? projectId)
    {
        using var engine = await CreateStartedEngineAsync();
        try
        {
            Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateAxisRuntime(), projectId))).IsAccepted);
            if (kind == "move") await EnterPausedManualControlAsync(engine);
            var snapshot = engine.CurrentSnapshot;
            var command = CreateBoundCommand(kind, new SimulationRuntimeIdentity(snapshot.ProjectId, snapshot.RuntimeGeneration));
            var result = await engine.EnqueueCommandAsync(command);
            Assert.True(result.IsAccepted, result.Detail);
            Assert.Equal(SimulationCommandErrorCode.None, result.ErrorCode);
            if (kind == "move") Assert.Equal(AxisState.Moving, Assert.Single(engine.CurrentSnapshot.Axes).State);
            if (kind == "manual") Assert.Equal(SimulationRunMode.RealTime, engine.CurrentSnapshot.RunMode);
        }
        finally { await engine.StopAsync(); }
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("same-project")]
    [InlineData("axes")]
    public async Task ExistingGenerationTransitionsInvalidateBoundRequestsButNotUnboundCalls(string transition)
    {
        using var engine = await CreateStartedEngineAsync();
        try
        {
            Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateAxisRuntime(), "project"))).IsAccepted);
            var snapshot = engine.CurrentSnapshot;
            SimulationCommand change = transition switch
            {
                "reset" => new ResetCommand(),
                "axes" => new ConfigureAxesCommand([new AxisConfiguration { Id = "axis-x" }]),
                _ => new ConfigureRuntimeCommand(CreateAxisRuntime(), "project")
            };
            Assert.True((await engine.EnqueueCommandAsync(change)).IsAccepted);
            Assert.Equal(snapshot.RuntimeGeneration + 1, engine.CurrentSnapshot.RuntimeGeneration);
            var stale = await engine.EnqueueCommandAsync(CreateBoundCommand("pause",
                new SimulationRuntimeIdentity(snapshot.ProjectId, snapshot.RuntimeGeneration)));
            Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, stale.ErrorCode);
            Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        }
        finally { await engine.StopAsync(); }
    }

    [Theory]
    [InlineData("PROJECT", 0)]
    [InlineData("different", 0)]
    [InlineData(null, 0)]
    [InlineData("project", -1)]
    [InlineData("project", 1)]
    public async Task ProjectAndGenerationMustBothMatchBeforeAnyHandlerRuns(string? projectId, int offset)
    {
        using var engine = await CreateStartedEngineAsync();
        try
        {
            Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateAxisRuntime(), "project"))).IsAccepted);
            var snapshot = engine.CurrentSnapshot;
            var result = await engine.EnqueueCommandAsync(CreateBoundCommand("move",
                new SimulationRuntimeIdentity(projectId, snapshot.RuntimeGeneration + offset)));
            Assert.Equal(SimulationCommandErrorCode.RuntimeIdentityMismatch, result.ErrorCode);
            Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);
            Assert.Equal(AxisState.Idle, Assert.Single(engine.CurrentSnapshot.Axes).State);
        }
        finally { await engine.StopAsync(); }
    }

    private static SimulationRuntimeConfiguration CreateAxisRuntime() =>
        new([new AxisConfiguration { Id = "axis-x" }], [], []);

    private static SimulationCommand CreateBoundCommand(string kind, SimulationRuntimeIdentity identity) => kind switch
    {
        "pause" => new PauseCommand { ExpectedRuntime = identity },
        "manual" => new StartManualControlCommand { ExpectedRuntime = identity },
        "move" => new MoveAxesAbsoluteCommand([new AxisMoveTarget("axis-x", 10)]) { ExpectedRuntime = identity },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<FixedStepSimulationEngine> CreateStartedEngineAsync()
    {
        var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        return engine;
    }

    private static async Task EnterPausedManualControlAsync(FixedStepSimulationEngine engine)
    {
        var started = await engine.EnqueueCommandAsync(new StartManualControlCommand());
        Assert.True(started.IsAccepted, started.Detail);
        var paused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(paused.IsAccepted, paused.Detail);
    }

    private static SimulationRuntimeConfiguration CreateRuntime() =>
        new(
            Array.Empty<AxisConfiguration>(),
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>(),
            new[]
            {
                new VirtualCameraConfiguration(
                    "camera-1",
                    "Camera",
                    exposureTicks: 1,
                    transferTicks: 1,
                    PlaceholderInspectionDecision.Pass)
            });

    private static TriggerVirtualCameraCommand CreateTrigger(
        string projectId,
        long runtimeGeneration) =>
        new(
            "camera-1",
            "recipe",
            new VirtualCameraFrameEvidence(
                "camera-1/frame/00000001",
                "assets/test.raw",
                new string('A', 64),
                1,
                1,
                1,
                "Mono8"),
            projectId: projectId,
            runtimeGeneration: runtimeGeneration);
}
