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

        public SimulationSnapshot CurrentSnapshot { get; }

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
