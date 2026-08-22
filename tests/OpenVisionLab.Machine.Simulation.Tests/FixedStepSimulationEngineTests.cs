using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public class FixedStepSimulationEngineTests
{
    private static AxisConfiguration CreateAxisConfig(string id = "x") => new()
    {
        Id = id,
        Name = $"{id.ToUpperInvariant()} Axis",
        MinimumPosition = 0,
        MaximumPosition = 300,
        HomePosition = 0,
        MaximumVelocity = 200,
        Acceleration = 500,
        Deceleration = 500
    };

    [Fact]
    public async Task EnqueueBeforeStart_ReturnsTypedEngineNotStartedWithoutWaiting()
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings());
        var command = new PauseCommand();

        var result = await engine.EnqueueCommandAsync(command).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(result.IsAccepted);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(SimulationCommandErrorCode.EngineNotStarted, result.ErrorCode);
        Assert.Equal(0, result.AppliedTick);
        Assert.Equal(TimeSpan.Zero, result.SimulationTime);
    }

    [Fact]
    public async Task EnqueueAfterStop_ReturnsTypedEngineStoppedInsteadOfChannelException()
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings());
        await engine.StartAsync();
        await engine.StopAsync();

        var command = new PauseCommand();
        var result = await engine.EnqueueCommandAsync(command).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(result.IsAccepted);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(SimulationCommandErrorCode.EngineStopped, result.ErrorCode);
    }

    [Fact]
    public async Task PlayPause_AdvancesAndStopsTime()
    {
        var settings = new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) };
        var engine = new FixedStepSimulationEngine(settings);
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
        await engine.StartAsync();

        await engine.EnqueueCommandAsync(new PlayCommand());
        await Task.Delay(200);
        var snapshotAfterPlay = engine.CurrentSnapshot;
        Assert.True(snapshotAfterPlay.SimulationTime > TimeSpan.Zero);

        await engine.EnqueueCommandAsync(new PauseCommand());
        await Task.Delay(100);
        var pausedTime = engine.CurrentSnapshot.SimulationTime;
        await Task.Delay(100);
        Assert.Equal(pausedTime, engine.CurrentSnapshot.SimulationTime);

        await engine.StopAsync();
    }

    [Fact]
    public async Task SingleStep_AdvancesExactlyOneFixedStep()
    {
        var settings = new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) };
        var engine = new FixedStepSimulationEngine(settings);
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
        await engine.StartAsync();

        await engine.EnqueueCommandAsync(new StepCommand());
        await Task.Delay(100);
        Assert.Equal(TimeSpan.FromMilliseconds(5), engine.CurrentSnapshot.SimulationTime);

        await engine.StopAsync();
    }

    [Fact]
    public async Task Reset_RestoresInitialState()
    {
        var settings = new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) };
        var engine = new FixedStepSimulationEngine(settings);
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
        await engine.StartAsync();

        await engine.EnqueueCommandAsync(new PlayCommand());
        await Task.Delay(200);
        await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 100));
        await Task.Delay(500);
        await engine.EnqueueCommandAsync(new ResetCommand());
        await Task.Delay(100);

        var snapshot = engine.CurrentSnapshot;
        Assert.Equal(TimeSpan.Zero, snapshot.SimulationTime);
        Assert.Equal(0, snapshot.Axes[0].Position);
        Assert.Equal(AxisState.Idle, snapshot.Axes[0].State);

        await engine.StopAsync();
    }

    [Fact]
    public async Task Deterministic_SameInput_SameFinalPosition()
    {
        var results = new List<double>();
        for (var i = 0; i < 2; i++)
        {
            var settings = new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) };
            var engine = new FixedStepSimulationEngine(settings);
            engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
            await engine.StartAsync();
            await engine.EnqueueCommandAsync(new PlayCommand());
            await Task.Delay(100);
            await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 100));
            await Task.Delay(3000);
            results.Add(engine.CurrentSnapshot.Axes[0].Position);
            await engine.StopAsync();
        }

        Assert.Equal(results[0], results[1], 6);
        Assert.Equal(100, results[0], 6);
    }

    [Fact]
    public async Task ManualControl_JogStopHomeAndResetUseOrderedEngineState()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
        await engine.StartAsync();

        var manual = new StartManualControlCommand();
        var manualResult = await engine.EnqueueCommandAsync(manual);
        Assert.True(manualResult.IsAccepted, manualResult.Detail);
        Assert.Equal(SimulationControlOwner.Manual, engine.CurrentSnapshot.ControlOwner);
        Assert.Equal(SimulationRunMode.RealTime, engine.CurrentSnapshot.RunMode);

        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var jog = new JogAxisCommand("x", AxisJogDirection.Positive);
        var jogResult = await engine.EnqueueCommandAsync(jog);
        Assert.True(jogResult.IsAccepted, jogResult.Detail);
        var beforeJogTick = engine.CurrentSnapshot.TickIndex;
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var jogged = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.TickIndex > beforeJogTick && snapshot.Axes[0].Position > 0);

        var stop = new StopAxisCommand("x");
        var stopResult = await engine.EnqueueCommandAsync(stop);
        Assert.True(stopResult.IsAccepted, stopResult.Detail);
        var stoppedPosition = engine.CurrentSnapshot.Axes[0].Position;
        var beforeStoppedStep = engine.CurrentSnapshot.TickIndex;
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var stopped = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.TickIndex > beforeStoppedStep);
        Assert.Equal(stoppedPosition, stopped.Axes[0].Position, 10);
        Assert.Equal(AxisState.Stopped, stopped.Axes[0].State);

        var home = new HomeAxisCommand("x");
        var homeResult = await engine.EnqueueCommandAsync(home);
        Assert.True(homeResult.IsAccepted, homeResult.Detail);
        for (var step = 0; step < 20 && engine.CurrentSnapshot.Axes[0].State == AxisState.Moving; step++)
        {
            var beforeHomeStep = engine.CurrentSnapshot.TickIndex;
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            await WaitForSnapshotAsync(
                engine.SnapshotReader,
                snapshot => snapshot.TickIndex > beforeHomeStep);
        }
        Assert.Equal(0, engine.CurrentSnapshot.Axes[0].Position, 10);
        Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes[0].State);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item => item.Code == "ManualControlStarted" && item.CommandId == manual.CommandId);
        Assert.Contains(events, item => item.Code == "AxisJogAccepted" && item.CommandId == jog.CommandId);
        Assert.Contains(events, item => item.Code == "AxisStopAccepted" && item.CommandId == stop.CommandId);
        Assert.Contains(events, item => item.Code == "AxisHomeAccepted" && item.CommandId == home.CommandId);
        AssertCommandBoundary(events, jog.CommandId, jogResult);
        AssertCommandBoundary(events, stop.CommandId, stopResult);
        AssertCommandBoundary(events, home.CommandId, homeResult);
    }

    [Fact]
    public async Task ManualRelativeMove_UsesCurrentPositionAndPreservesFixedStepEvidence()
    {
        var configuration = CreateAxisConfig();
        configuration.HomePosition = 40;
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        engine.AddAxis(new ServoAxisComponent(configuration));
        await engine.StartAsync();

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var positive = new MoveRelativeCommand("x", 15);
        var positiveResult = await engine.EnqueueCommandAsync(positive);
        Assert.True(positiveResult.IsAccepted, positiveResult.Detail);

        var pausedTick = engine.CurrentSnapshot.TickIndex;
        var pausedPosition = engine.CurrentSnapshot.Axes[0].Position;
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var stepped = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.TickIndex > pausedTick);
        Assert.Equal(pausedTick + 1, stepped.TickIndex);
        Assert.True(stepped.Axes[0].Position > pausedPosition);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        var positiveComplete = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.Axes[0].State == AxisState.Idle &&
                Math.Abs(snapshot.Axes[0].Position - 55) < 1e-9);
        Assert.Equal(55, positiveComplete.Axes[0].Position, 10);

        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var negative = new MoveRelativeCommand("x", -5);
        var negativeResult = await engine.EnqueueCommandAsync(negative);
        Assert.True(negativeResult.IsAccepted, negativeResult.Detail);
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        var negativeComplete = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.Axes[0].State == AxisState.Idle &&
                Math.Abs(snapshot.Axes[0].Position - 50) < 1e-9);
        Assert.Equal(50, negativeComplete.Axes[0].Position, 10);

        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var outOfRange = new MoveRelativeCommand("x", 300);
        var rejected = await engine.EnqueueCommandAsync(outOfRange);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisTargetOutOfRange, rejected.ErrorCode);
        Assert.Equal(50, engine.CurrentSnapshot.Axes[0].Position, 10);

        var invalid = new MoveRelativeCommand("x", double.NaN);
        var invalidResult = await engine.EnqueueCommandAsync(invalid);
        Assert.False(invalidResult.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisTargetInvalid, invalidResult.ErrorCode);
        Assert.Equal(50, engine.CurrentSnapshot.Axes[0].Position, 10);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(40, engine.CurrentSnapshot.Axes[0].Position, 10);
        Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes[0].State);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item =>
            item.Code == "AxisRelativeMoveAccepted" && item.CommandId == positive.CommandId);
        Assert.Contains(events, item =>
            item.Code == "AxisRelativeMoveAccepted" && item.CommandId == negative.CommandId);
        Assert.Contains(events, item =>
            item.Code == "CommandRejected" && item.CommandId == outOfRange.CommandId);
        var relativeEvent = Assert.Single(events, item =>
            item.Code == "AxisRelativeMoveAccepted" && item.CommandId == positive.CommandId);
        var acceptedEvent = Assert.Single(events, item =>
            item.Code == "CommandAccepted" && item.CommandId == positive.CommandId);
        Assert.True(relativeEvent.EventIndex < acceptedEvent.EventIndex);
        AssertCommandBoundary(events, positive.CommandId, positiveResult);
        AssertCommandBoundary(events, negative.CommandId, negativeResult);
        AssertCommandBoundary(events, outOfRange.CommandId, rejected);
        AssertCommandBoundary(events, invalid.CommandId, invalidResult);
    }

    [Fact]
    public async Task ManualVelocityMove_UsesSignedLimitStopAndDeterministicPauseStepReset()
    {
        var configuration = CreateAxisConfig();
        configuration.MinimumPosition = 0;
        configuration.MaximumPosition = 10;
        configuration.HomePosition = 5;
        configuration.MaximumVelocity = 50;
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        engine.AddAxis(new ServoAxisComponent(configuration));
        await engine.StartAsync();

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        var positive = new MoveVelocityCommand("x", 25);
        var positiveResult = await engine.EnqueueCommandAsync(positive);
        Assert.True(positiveResult.IsAccepted, positiveResult.Detail);
        var paused = engine.CurrentSnapshot;
        await Task.Delay(50);
        Assert.Equal(paused.TickIndex, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(paused.Axes[0].Position, engine.CurrentSnapshot.Axes[0].Position, 10);

        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var stepped = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.TickIndex > paused.TickIndex);
        Assert.Equal(paused.TickIndex + 1, stepped.TickIndex);
        Assert.True(stepped.Axes[0].Position > paused.Axes[0].Position);
        Assert.True(stepped.Axes[0].Velocity > 0);

        var stop = new StopAxisCommand("x");
        var stopResult = await engine.EnqueueCommandAsync(stop);
        Assert.True(stopResult.IsAccepted, stopResult.Detail);
        var stoppedPosition = engine.CurrentSnapshot.Axes[0].Position;
        var beforeStoppedStep = engine.CurrentSnapshot.TickIndex;
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        var stopped = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.TickIndex > beforeStoppedStep);
        Assert.Equal(stoppedPosition, stopped.Axes[0].Position, 10);
        Assert.Equal(AxisState.Stopped, stopped.Axes[0].State);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(5, engine.CurrentSnapshot.Axes[0].Position, 10);
        Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes[0].State);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var zero = await engine.EnqueueCommandAsync(new MoveVelocityCommand("x", 0));
        var nonFinite = await engine.EnqueueCommandAsync(new MoveVelocityCommand("x", double.NaN));
        var tooFast = await engine.EnqueueCommandAsync(new MoveVelocityCommand("x", 60));
        Assert.Equal(SimulationCommandErrorCode.AxisVelocityInvalid, zero.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.AxisVelocityInvalid, nonFinite.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.AxisVelocityOutOfRange, tooFast.ErrorCode);

        var negative = new MoveVelocityCommand("x", -25);
        var negativeResult = await engine.EnqueueCommandAsync(negative);
        Assert.True(negativeResult.IsAccepted, negativeResult.Detail);
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        var limited = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            snapshot => snapshot.Axes[0].State == AxisState.Limited);
        Assert.Equal(0, limited.Axes[0].Position, 10);
        Assert.Equal(0, limited.Axes[0].Velocity, 10);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(5, engine.CurrentSnapshot.Axes[0].Position, 10);
        Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes[0].State);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var velocityEvent = Assert.Single(events, item =>
            item.Code == "AxisVelocityMoveAccepted" && item.CommandId == positive.CommandId);
        var acceptedEvent = Assert.Single(events, item =>
            item.Code == "CommandAccepted" && item.CommandId == positive.CommandId);
        Assert.True(velocityEvent.EventIndex < acceptedEvent.EventIndex);
        AssertCommandBoundary(events, positive.CommandId, positiveResult);
        AssertCommandBoundary(events, stop.CommandId, stopResult);
        AssertCommandBoundary(events, negative.CommandId, negativeResult);
    }

    [Fact]
    public async Task CoordinatedAxes_ShareFixedTickPauseStepStopResetAndOrderedEvidenceAcrossRuns()
    {
        var runs = new List<string[]>();
        for (var run = 0; run < 2; run++)
        {
            using var engine = new FixedStepSimulationEngine(
                new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
            engine.AddAxis(new ServoAxisComponent(CreateAxisConfig("x")));
            engine.AddAxis(new ServoAxisComponent(CreateAxisConfig("y")));
            await engine.StartAsync();

            Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
            var paused = engine.CurrentSnapshot;

            var move = new MoveAxesAbsoluteCommand(new[]
            {
                new AxisMoveTarget("y", 20),
                new AxisMoveTarget("x", 10)
            });
            var moveResult = await engine.EnqueueCommandAsync(move);
            Assert.True(moveResult.IsAccepted, moveResult.Detail);
            Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Moving, axis.State));
            Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(0, axis.Position, 10));

            await Task.Delay(50);
            Assert.Equal(paused.TickIndex, engine.CurrentSnapshot.TickIndex);
            Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(0, axis.Position, 10));

            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            var stepped = await WaitForSnapshotAsync(
                engine.SnapshotReader,
                snapshot => snapshot.TickIndex > paused.TickIndex);
            Assert.Equal(paused.TickIndex + 1, stepped.TickIndex);
            Assert.All(stepped.Axes, axis => Assert.True(axis.Position > 0));

            var stop = new StopAxesCommand(new[] { "y", "x" });
            var stopResult = await engine.EnqueueCommandAsync(stop);
            Assert.True(stopResult.IsAccepted, stopResult.Detail);
            var stoppedPositions = engine.CurrentSnapshot.Axes.Select(axis => axis.Position).ToArray();
            Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Stopped, axis.State));

            var stoppedTick = engine.CurrentSnapshot.TickIndex;
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            var stopped = await WaitForSnapshotAsync(
                engine.SnapshotReader,
                snapshot => snapshot.TickIndex > stoppedTick);
            Assert.Equal(stoppedTick + 1, stopped.TickIndex);
            Assert.Equal(stoppedPositions, stopped.Axes.Select(axis => axis.Position).ToArray());

            Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
            Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
            Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
            Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);
            Assert.All(engine.CurrentSnapshot.Axes, axis =>
            {
                Assert.Equal(AxisState.Idle, axis.State);
                Assert.Equal(0, axis.Position, 10);
            });

            await engine.StopAsync();
            var events = await ReadAllEventsAsync(engine);
            var moveEvents = events.Where(item => item.CommandId == move.CommandId).ToArray();
            var stopEvents = events.Where(item => item.CommandId == stop.CommandId).ToArray();
            Assert.Equal(new[] { "AxisGroupMoveAccepted", "CommandAccepted" }, moveEvents.Select(item => item.Code));
            Assert.Equal(new[] { "AxisGroupStopAccepted", "CommandAccepted" }, stopEvents.Select(item => item.Code));
            AssertCommandBoundary(events, move.CommandId, moveResult);
            AssertCommandBoundary(events, stop.CommandId, stopResult);
            Assert.Contains("Targets: y = 20.000, x = 10.000.", moveEvents[0].Message, StringComparison.Ordinal);
            Assert.Contains("Stopped: y = ", stopEvents[0].Message, StringComparison.Ordinal);

            var firstRelevantIndex = moveEvents[0].EventIndex;
            runs.Add(moveEvents.Concat(stopEvents).Select(item =>
                $"{item.EventIndex - firstRelevantIndex}|{item.TickIndex - paused.TickIndex}|" +
                $"{item.SimulationTime - paused.SimulationTime}|{item.Code}|{item.Message}").ToArray());
        }

        Assert.Equal(runs[0], runs[1]);
    }

    [Fact]
    public async Task CoordinatedAxes_RejectPartialMoveAndStopWithoutMutatingOtherAxes()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig("x")));
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig("y")));
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        var outOfRange = await engine.EnqueueCommandAsync(new MoveAxesAbsoluteCommand(new[]
        {
            new AxisMoveTarget("x", 10),
            new AxisMoveTarget("y", 500)
        }));
        Assert.False(outOfRange.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisTargetOutOfRange, outOfRange.ErrorCode);
        Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Idle, axis.State));

        var empty = await engine.EnqueueCommandAsync(
            new MoveAxesAbsoluteCommand(Array.Empty<AxisMoveTarget>()));
        Assert.False(empty.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisGroupInvalid, empty.ErrorCode);
        Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Idle, axis.State));

        Assert.True((await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 30))).IsAccepted);
        var busy = await engine.EnqueueCommandAsync(new MoveAxesAbsoluteCommand(new[]
        {
            new AxisMoveTarget("y", 20),
            new AxisMoveTarget("x", 10)
        }));
        Assert.False(busy.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisBusy, busy.ErrorCode);
        Assert.Equal(AxisState.Moving, engine.CurrentSnapshot.Axes.Single(axis => axis.Id == "x").State);
        Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes.Single(axis => axis.Id == "y").State);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new MoveAxesAbsoluteCommand(new[]
        {
            new AxisMoveTarget("x", 10),
            new AxisMoveTarget("y", 20)
        }))).IsAccepted);

        var missingStop = await engine.EnqueueCommandAsync(new StopAxesCommand(new[] { "x", "missing", "y" }));
        Assert.False(missingStop.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisNotFound, missingStop.ErrorCode);
        Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Moving, axis.State));

        var duplicateStop = await engine.EnqueueCommandAsync(new StopAxesCommand(new[] { "x", "x" }));
        Assert.False(duplicateStop.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisGroupInvalid, duplicateStop.ErrorCode);
        Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Moving, axis.State));

        Assert.True((await engine.EnqueueCommandAsync(new StopAxesCommand(new[] { "x", "y" }))).IsAccepted);
        Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Stopped, axis.State));
        await engine.StopAsync();
    }

    [Fact]
    public async Task ManualControl_RejectsWhileEmbeddedSequenceIsRunning()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntimeConfiguration()))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("main"))).IsAccepted);

        var result = await engine.EnqueueCommandAsync(new StartManualControlCommand());

        Assert.False(result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ControlOwnerNotAllowed, result.ErrorCode);
    }

    [Fact]
    public async Task ConfigureAxes_ReplacesAxesAndPublishesResetSnapshot()
    {
        var settings = new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) };
        var engine = new FixedStepSimulationEngine(settings);
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
        await engine.StartAsync();

        var replacement = new AxisConfiguration
        {
            Id = "inspection-y",
            Name = "Inspection Y Axis",
            MinimumPosition = -50,
            MaximumPosition = 150,
            HomePosition = 25,
            MaximumVelocity = 100,
            Acceleration = 250,
            Deceleration = 250
        };

        await engine.EnqueueCommandAsync(new ConfigureAxesCommand(new[] { replacement }));
        var snapshot = await WaitForSnapshotAsync(
            engine.SnapshotReader,
            candidate => candidate.Axes.Count == 1 && candidate.Axes[0].Id == replacement.Id);

        Assert.Equal(SimulationRunMode.Paused, snapshot.RunMode);
        Assert.Equal(TimeSpan.Zero, snapshot.SimulationTime);
        Assert.Equal(25, snapshot.Axes[0].Position);

        await engine.StopAsync();
        await engine.SnapshotReader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConfigureAxes_ReplacesWholeRuntimeWithAxesOnlyState()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntimeConfiguration()));
        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.Single(engine.CurrentSnapshot.Signals);
        Assert.Single(engine.CurrentSnapshot.Sequences);

        await engine.EnqueueCommandAsync(new StepCommand());
        var replacement = CreateAxisConfig();
        replacement.Id = "replacement";
        replacement.Name = "Replacement Axis";
        replacement.HomePosition = 25;
        var command = new ConfigureAxesCommand(new[] { replacement });
        var result = await engine.EnqueueCommandAsync(command);
        var snapshot = engine.CurrentSnapshot;

        Assert.True(result.IsAccepted, result.Detail);
        Assert.Equal(1, result.AppliedTick);
        Assert.Equal(TimeSpan.FromMilliseconds(5), result.SimulationTime);
        Assert.Equal(0, snapshot.TickIndex);
        Assert.Equal(TimeSpan.Zero, snapshot.SimulationTime);
        Assert.Equal(SimulationRunMode.Paused, snapshot.RunMode);
        Assert.Equal(SimulationControlOwner.Definition, snapshot.ControlOwner);
        Assert.Equal("replacement", Assert.Single(snapshot.Axes).Id);
        Assert.Empty(snapshot.Signals);
        Assert.Empty(snapshot.Sequences);

        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        Assert.True(reset.IsAccepted);
        Assert.Empty(engine.CurrentSnapshot.Signals);
        Assert.Empty(engine.CurrentSnapshot.Sequences);
        Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        AssertCommandBoundary(events, command.CommandId, result);
    }

    [Fact]
    public async Task ResetAndConfigureRuntime_EventsUsePreCommandAppliedBoundary()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        engine.AddAxis(new ServoAxisComponent(CreateAxisConfig()));
        await engine.StartAsync();
        await engine.EnqueueCommandAsync(new StepCommand());
        await engine.EnqueueCommandAsync(new StepCommand());

        var resetCommand = new ResetCommand();
        var resetResult = await engine.EnqueueCommandAsync(resetCommand);
        await engine.EnqueueCommandAsync(new StepCommand());
        var configureCommand = new ConfigureRuntimeCommand(CreateRuntimeConfiguration());
        var configureResult = await engine.EnqueueCommandAsync(configureCommand);

        Assert.Equal(2, resetResult.AppliedTick);
        Assert.Equal(TimeSpan.FromMilliseconds(10), resetResult.SimulationTime);
        Assert.Equal(1, configureResult.AppliedTick);
        Assert.Equal(TimeSpan.FromMilliseconds(5), configureResult.SimulationTime);
        Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(TimeSpan.Zero, engine.CurrentSnapshot.SimulationTime);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        AssertCommandBoundary(events, resetCommand.CommandId, resetResult);
        AssertCommandBoundary(events, configureCommand.CommandId, configureResult);
    }

    [Fact]
    public void RuntimeConfiguration_DeepClonesAxisDefinitionsAtConstruction()
    {
        var source = CreateAxisConfig();
        var configuration = new SimulationRuntimeConfiguration(
            new[] { source },
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>());

        source.Id = "mutated";
        source.Name = "Mutated";
        source.MinimumPosition = -500;
        source.MaximumPosition = 900;
        source.HomePosition = 200;
        source.MaximumVelocity = 1;
        source.Acceleration = 2;
        source.Deceleration = 3;
        source.FollowingErrorLimit = 4;

        var captured = Assert.Single(configuration.Axes);
        Assert.Equal("x", captured.Id);
        Assert.Equal("X Axis", captured.Name);
        Assert.Equal(0, captured.MinimumPosition);
        Assert.Equal(300, captured.MaximumPosition);
        Assert.Equal(0, captured.HomePosition);
        Assert.Equal(200, captured.MaximumVelocity);
        Assert.Equal(500, captured.Acceleration);
        Assert.Equal(500, captured.Deceleration);
        Assert.Equal(0.05, captured.FollowingErrorLimit);
    }

    private static async Task<SimulationSnapshot> WaitForSnapshotAsync(
        System.Threading.Channels.ChannelReader<SimulationSnapshot> reader,
        Func<SimulationSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var snapshot in reader.ReadAllAsync(timeout.Token))
        {
            if (predicate(snapshot))
            {
                return snapshot;
            }
        }

        throw new TimeoutException("The expected simulation snapshot was not published.");
    }

    private static SimulationRuntimeConfiguration CreateRuntimeConfiguration()
    {
        var channel = new ChannelDefinition
        {
            Id = "di.start",
            Name = "Start",
            Kind = ChannelKind.DigitalInput
        };
        var definition = new SequenceDefinition
        {
            Id = "main",
            Name = "Main",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "complete",
                    Name = "Complete",
                    Action = SequenceStepAction.Complete
                }
            }
        };
        var targets = new SequenceCompilationTargets(
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
            {
                [channel.Id] = channel.Kind
            },
            new[] { "x" });
        var compilation = new SequenceCompiler().Compile(definition, targets);
        Assert.True(compilation.IsSuccess);
        return new SimulationRuntimeConfiguration(
            new[] { CreateAxisConfig() },
            new[] { channel },
            new[] { compilation.Sequence! });
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

    private static void AssertCommandBoundary(
        IReadOnlyList<SimulationEvent> events,
        string commandId,
        SimulationCommandResult result)
    {
        var correlated = events.Where(item => item.CommandId == commandId).ToArray();
        Assert.NotEmpty(correlated);
        Assert.All(correlated, item =>
        {
            Assert.Equal(result.AppliedTick, item.TickIndex);
            Assert.Equal(result.SimulationTime, item.SimulationTime);
        });
    }
}
