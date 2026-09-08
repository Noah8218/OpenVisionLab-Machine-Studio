using System.Threading.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MultiAxisCommissioningExecutionWorkflowTests
{
    [Fact]
    public async Task ExecutesManualControlAndMoveInOrderWhileAlreadyPaused()
    {
        var engine = new TestSimulationEngine(SimulationRunMode.Paused, true, true);
        var workflow = CreateWorkflow(engine);

        var result = await workflow.ExecuteAsync(
            [new AxisMoveTarget("axis-x", 12.5), new AxisMoveTarget("axis-theta", 90)]);

        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Accepted, result.Outcome);
        Assert.False(result.PausedBeforeExecution);
        Assert.Null(result.RejectedCommand);
        Assert.Collection(
            engine.Commands,
            command => Assert.IsType<StartManualControlCommand>(command),
            command =>
            {
                var move = Assert.IsType<MoveAxesAbsoluteCommand>(command);
                Assert.Equal(
                    new[]
                    {
                        new AxisMoveTarget("axis-x", 12.5),
                        new AxisMoveTarget("axis-theta", 90)
                    },
                    move.Targets);
            });
    }

    [Fact]
    public async Task PausesBeforeManualControlWhenEngineIsRunning()
    {
        var engine = new TestSimulationEngine(SimulationRunMode.RealTime, true, true, true);
        var workflow = CreateWorkflow(engine);

        var result = await workflow.ExecuteAsync(
            [new AxisMoveTarget("axis-x", 12.5)]);

        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Accepted, result.Outcome);
        Assert.True(result.PausedBeforeExecution);
        Assert.Collection(
            engine.Commands,
            command => Assert.IsType<PauseCommand>(command),
            command => Assert.IsType<StartManualControlCommand>(command),
            command => Assert.IsType<MoveAxesAbsoluteCommand>(command));
    }

    [Fact]
    public async Task StopsAfterPauseRejection()
    {
        var engine = new TestSimulationEngine(SimulationRunMode.RealTime, false);
        var workflow = CreateWorkflow(engine);

        var result = await workflow.ExecuteAsync(
            [new AxisMoveTarget("axis-x", 12.5)]);

        Assert.Equal(MultiAxisCommissioningExecutionOutcome.PauseRejected, result.Outcome);
        Assert.False(result.PausedBeforeExecution);
        Assert.False(result.RejectedCommand!.IsAccepted);
        Assert.Single(engine.Commands);
        Assert.IsType<PauseCommand>(engine.Commands[0]);
    }

    [Fact]
    public async Task StopsAfterManualControlRejectionWithoutIssuingMove()
    {
        var engine = new TestSimulationEngine(SimulationRunMode.Paused, false);
        var workflow = CreateWorkflow(engine);

        var result = await workflow.ExecuteAsync(
            [new AxisMoveTarget("axis-x", 12.5)]);

        Assert.Equal(
            MultiAxisCommissioningExecutionOutcome.ManualControlRejected,
            result.Outcome);
        Assert.False(result.PausedBeforeExecution);
        Assert.False(result.RejectedCommand!.IsAccepted);
        Assert.Single(engine.Commands);
        Assert.IsType<StartManualControlCommand>(engine.Commands[0]);
    }

    [Fact]
    public async Task StopsAfterMoveRejectionAndPreservesThatFailure()
    {
        var engine = new TestSimulationEngine(SimulationRunMode.Paused, true, false);
        var workflow = CreateWorkflow(engine);

        var result = await workflow.ExecuteAsync(
            [new AxisMoveTarget("axis-x", 12.5)]);

        Assert.Equal(MultiAxisCommissioningExecutionOutcome.MoveRejected, result.Outcome);
        Assert.False(result.PausedBeforeExecution);
        Assert.False(result.RejectedCommand!.IsAccepted);
        Assert.Collection(
            engine.Commands,
            command => Assert.IsType<StartManualControlCommand>(command),
            command => Assert.IsType<MoveAxesAbsoluteCommand>(command));
    }

    [Fact]
    public async Task RuntimeReplacementDuringPauseDoesNotDispatchCommandsToTheNewRuntime()
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.RealTime);
        var pauseReply = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Reply = command => engine.Commands.Count == 1
            ? pauseReply.Task
            : Task.FromResult(Accept(command));
        var workflow = CreateWorkflow(engine);
        var execution = workflow.ExecuteAsync([new AxisMoveTarget("axis-x", 12.5)]);
        var pause = Assert.IsType<PauseCommand>(Assert.Single(engine.Commands));

        engine.ReplaceRuntime("replacement-project", 2);
        pauseReply.SetResult(Accept(pause));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(engine.Commands);
        Assert.False(result.IsAccepted);
    }

    [Theory]
    [InlineData(1, "invalidate")]
    [InlineData(2, "invalidate")]
    [InlineData(3, "invalidate")]
    [InlineData(1, "generation")]
    [InlineData(2, "generation")]
    [InlineData(3, "generation")]
    [InlineData(1, "project")]
    [InlineData(2, "project")]
    [InlineData(3, "project")]
    [InlineData(1, "context")]
    [InlineData(2, "context")]
    [InlineData(3, "context")]
    [InlineData(1, "dispose")]
    [InlineData(2, "dispose")]
    [InlineData(3, "dispose")]
    public async Task InterruptedPreparationStopsAtEveryAwaitAndDoesNotPresentLateReplies(int blockedStage, string interruption)
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.RealTime);
        engine.ReplaceRuntime("project", 1);
        var reply = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Reply = command => engine.Commands.Count == blockedStage ? reply.Task : Task.FromResult(Accept(command));
        var statuses = new List<string>();
        var logs = new List<string>();
        using var workflow = new MultiAxisCommissioningExecutionWorkflow(
            engine, new EquipmentCommandDispatcher(engine, statuses.Add, (_, message) => logs.Add(message)));
        var contextCurrent = true;
        var execution = workflow.ExecuteAsync([new AxisMoveTarget("axis-x", 10)], () => contextCurrent);
        Assert.Equal(blockedStage, engine.Commands.Count);
        var statusCount = statuses.Count;
        var logCount = logs.Count;

        switch (interruption)
        {
            case "invalidate": workflow.InvalidatePendingExecution(); break;
            case "generation": engine.ReplaceRuntime("project", 2); break;
            case "project": engine.ReplaceRuntime("other-project", 1); break;
            case "context": contextCurrent = false; break;
            case "dispose": workflow.Dispose(); break;
        }
        reply.SetResult(Accept(engine.Commands[^1]));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted, result.Outcome);
        Assert.True(result.PausedBeforeExecution); // This is fact, not a rollback claim.
        Assert.Equal(blockedStage, engine.Commands.Count);
        Assert.Equal(statusCount, statuses.Count);
        Assert.Equal(logCount, logs.Count);
    }

    [Fact]
    public async Task ReopenedAdmissionDoesNotReviveOldPreparationAndFreshExecutionStillWorks()
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.Paused);
        var reply = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Reply = command => engine.Commands.Count == 1 ? reply.Task : Task.FromResult(Accept(command));
        using var workflow = CreateWorkflow(engine);
        var admissionOpen = true;
        var execution = workflow.ExecuteAsync([new AxisMoveTarget("old-axis", 1)], () => admissionOpen);

        admissionOpen = false;
        workflow.InvalidatePendingExecution();
        admissionOpen = true; // Close cancelled or mode changed back before the reply.
        reply.SetResult(Accept(engine.Commands[0]));
        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted,
            (await execution.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Single(engine.Commands);

        Assert.True((await workflow.ExecuteAsync([new AxisMoveTarget("new-axis", 2)], () => admissionOpen)).IsAccepted);
        Assert.Equal("new-axis", Assert.IsType<MoveAxesAbsoluteCommand>(engine.Commands[^1]).Targets.Single().AxisId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineFailurePropagatesOnlyForTheCurrentExecution(bool interrupt)
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.Paused);
        var reply = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Reply = _ => reply.Task;
        var statuses = new List<string>();
        using var workflow = new MultiAxisCommissioningExecutionWorkflow(
            engine, new EquipmentCommandDispatcher(engine, statuses.Add, (_, _) => { }));
        var execution = workflow.ExecuteAsync([new AxisMoveTarget("axis", 1)]);
        if (interrupt)
        {
            workflow.InvalidatePendingExecution();
        }
        var failure = new InvalidOperationException("Controlled engine failure");
        reply.SetException(failure);

        if (interrupt)
        {
            Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted,
                (await execution.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                () => execution.WaitAsync(TimeSpan.FromSeconds(5))));
        }
        Assert.Single(engine.Commands);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task ClosedAdmissionAndDisposedOwnerDoNotDispatchAndTargetsAreCapturedBeforeAwait()
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.Paused);
        var reply = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Reply = command => engine.Commands.Count == 1 ? reply.Task : Task.FromResult(Accept(command));
        using var workflow = CreateWorkflow(engine);
        var targets = new List<AxisMoveTarget> { new("axis", 10) };
        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted,
            (await workflow.ExecuteAsync(targets, () => false)).Outcome);
        Assert.Empty(engine.Commands);

        var execution = workflow.ExecuteAsync(targets);
        targets[0] = new AxisMoveTarget("different-axis", 99);
        reply.SetResult(Accept(engine.Commands[0]));
        Assert.True((await execution.WaitAsync(TimeSpan.FromSeconds(5))).IsAccepted);
        Assert.Equal(new AxisMoveTarget("axis", 10),
            Assert.Single(Assert.IsType<MoveAxesAbsoluteCommand>(engine.Commands[^1]).Targets));

        workflow.Dispose();
        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted, (await workflow.ExecuteAsync(targets)).Outcome);
        Assert.Equal(2, engine.Commands.Count);
    }

    [Fact]
    public async Task ReentrantCloseFromStatusNotificationStopsLoggingAndTheNextMove()
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.Paused, true, true);
        var current = true;
        var statuses = new List<string>();
        var logs = new List<string>();
        using var workflow = new MultiAxisCommissioningExecutionWorkflow(
            engine, new EquipmentCommandDispatcher(engine,
                status => { statuses.Add(status); current = false; },
                (_, message) => logs.Add(message)));

        var result = await workflow.ExecuteAsync([new AxisMoveTarget("axis", 1)], () => current);

        Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted, result.Outcome);
        Assert.Single(engine.Commands);
        Assert.Single(statuses);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task ExistingAsyncCommandGateRejectsRepeatedRunWhilePreparationIsWaiting()
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.Paused);
        var reply = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Reply = command => engine.Commands.Count == 1 ? reply.Task : Task.FromResult(Accept(command));
        using var workflow = CreateWorkflow(engine);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async _ =>
        {
            try { await workflow.ExecuteAsync([new AxisMoveTarget("axis", 1)]); }
            finally { finished.SetResult(); }
        }, useCommandManagerRequery: false);

        command.Execute(null);
        command.Execute(null);
        Assert.Single(engine.Commands);
        Assert.False(command.CanExecute(null));
        reply.SetResult(Accept(engine.Commands[0]));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, engine.Commands.Count);
    }

    [Fact]
    public async Task RuntimeReplacementBetweenHostCheckAndEnqueueCannotStartTheNewRuntime()
    {
        using var actual = new FixedStepSimulationEngine(new SimulationSettings());
        await actual.StartAsync();
        try
        {
            Assert.True((await actual.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(new SimulationRuntimeConfiguration([], [], []), "source-project"))).IsAccepted);
            using var frontage = new TestSimulationEngine(actual.CurrentSnapshot);
            SimulationCommandResult? forwardedResult = null;
            frontage.Reply = async command =>
            {
                // The host already read the old snapshot. Replacement wins the queue race.
                Assert.True((await actual.EnqueueCommandAsync(
                    new ConfigureRuntimeCommand(new SimulationRuntimeConfiguration([], [], []), "replacement-project"))).IsAccepted);
                forwardedResult = await actual.EnqueueCommandAsync(command);
                frontage.ReplaceRuntime("replacement-project", actual.CurrentSnapshot.RuntimeGeneration);
                return forwardedResult;
            };
            using var workflow = CreateWorkflow(frontage);

            var result = await workflow.ExecuteAsync([new AxisMoveTarget("axis-x", 10)])
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(forwardedResult);
            Assert.False(forwardedResult.IsAccepted);
            Assert.Equal(SimulationRunMode.Paused, actual.CurrentSnapshot.RunMode);
            Assert.Equal("replacement-project", actual.CurrentSnapshot.ProjectId);
            Assert.Equal(MultiAxisCommissioningExecutionOutcome.Interrupted, result.Outcome);
            Assert.Single(frontage.Commands);
        }
        finally
        {
            await actual.StopAsync();
        }
    }

    [Fact]
    public async Task AllPreparationCommandsCarryTheSameInitialRuntimeIdentity()
    {
        using var engine = new TestSimulationEngine(SimulationRunMode.RealTime, true, true, true);
        engine.ReplaceRuntime("source-project", 7);
        using var workflow = CreateWorkflow(engine);

        Assert.True((await workflow.ExecuteAsync([new AxisMoveTarget("axis-x", 10)])).IsAccepted);

        Assert.Equal(3, engine.Commands.Count);
        Assert.All(engine.Commands, command =>
            Assert.Equal(new SimulationRuntimeIdentity("source-project", 7), command.ExpectedRuntime));
    }

    private static SimulationCommandResult Accept(SimulationCommand command) =>
        new(command.CommandId, true, 0, TimeSpan.Zero, SimulationCommandErrorCode.None, null);

    private static MultiAxisCommissioningExecutionWorkflow CreateWorkflow(
        TestSimulationEngine engine) => new(
        engine,
        new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }));

    private sealed class TestSimulationEngine : ISimulationEngine
    {
        private readonly Queue<bool> _acceptedResults;
        private readonly Channel<SimulationSnapshot> _snapshotChannel =
            Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel =
            Channel.CreateUnbounded<SimulationEvent>();

        internal TestSimulationEngine(SimulationSnapshot snapshot)
        {
            _acceptedResults = new Queue<bool>();
            CurrentSnapshot = snapshot;
        }

        internal TestSimulationEngine(
            SimulationRunMode runMode,
            params bool[] acceptedResults)
        {
            _acceptedResults = new Queue<bool>(acceptedResults);
            CurrentSnapshot = new SimulationSnapshot(
                TimeSpan.Zero,
                0,
                runMode,
                SimulationControlOwner.Manual,
                1,
                [],
                0,
                [],
                []);
        }

        internal List<SimulationCommand> Commands { get; } = [];

        public SimulationSnapshot CurrentSnapshot { get; private set; }

        internal Func<SimulationCommand, Task<SimulationCommandResult>>? Reply { get; set; }

        internal void ReplaceRuntime(string projectId, long generation)
        {
            var s = CurrentSnapshot;
            CurrentSnapshot = new SimulationSnapshot(
                s.SimulationTime, s.TickIndex, s.RunMode, s.ControlOwner, s.TimeScale,
                s.Axes, s.SignalRevision, s.Signals, s.Sequences, s.Cameras,
                s.AutomaticRun, s.LayoutComponents, projectId: projectId, runtimeGeneration: generation);
        }

        public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotChannel.Reader;

        public ChannelReader<SimulationEvent> EventReader => _eventChannel.Reader;

        public Task<SimulationEngineTerminationResult> Termination => Task.FromResult(
            new SimulationEngineTerminationResult(
                SimulationEngineTerminationOutcome.Normal,
                0,
                TimeSpan.Zero));

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SimulationCommandResult> EnqueueCommandAsync(
            SimulationCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (Reply is not null)
            {
                return Reply(command);
            }
            var isAccepted = _acceptedResults.Dequeue();
            return Task.FromResult(
                new SimulationCommandResult(
                    command.CommandId,
                    isAccepted,
                    0,
                    TimeSpan.Zero,
                    isAccepted
                        ? SimulationCommandErrorCode.None
                        : SimulationCommandErrorCode.EngineFaulted,
                    isAccepted ? null : "test rejection"));
        }

        public void AddAxis(ServoAxisComponent axis)
        {
        }

        public void Dispose()
        {
            _snapshotChannel.Writer.TryComplete();
            _eventChannel.Writer.TryComplete();
        }
    }
}
