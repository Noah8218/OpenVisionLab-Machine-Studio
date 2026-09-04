using System.Threading.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationRunControlWorkflowTests
{
    [Fact]
    public async Task ConcurrentRunRequestsDispatchOnlyOneRunCommand()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState();
        var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        var firstRun = workflow.RunAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = workflow.RunAsync();

        engine.ReleaseFirstCommand();
        await Task.WhenAll(firstRun, secondRun);

        var command = Assert.Single(engine.Commands);
        Assert.IsType<PlayCommand>(command);
        Assert.True(state.IsRunning);
    }

    [Fact]
    public async Task DifferentRunControlCommandsAreSerializedAndRecheckState()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState();
        var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        var run = workflow.RunAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = workflow.PauseAsync();

        engine.ReleaseFirstCommand();
        await Task.WhenAll(run, pause);

        Assert.Collection(
            engine.Commands,
            command => Assert.IsType<PlayCommand>(command),
            command => Assert.IsType<PauseCommand>(command));
        Assert.False(state.IsRunning);
    }

    private static SimulationRunControlWorkflow CreateWorkflow(
        RecordingSimulationEngine engine,
        Func<SimulationRunControlState> getState,
        Action<bool> setRunning) => new(
        engine,
        TimeSpan.FromMilliseconds(5),
        getState,
        () => Task.FromResult(true),
        _ => { },
        setRunning,
        _ => { },
        () => { },
        _ => { },
        (_, _) => { },
        () => { });

    private static SimulationRunControlState CreateState() => new(
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRunMode: true,
        IsRunning: false,
        RuntimeDefinitionDirty: false,
        HasAutomaticRun: false,
        AutomaticRunConfigured: false,
        AutomaticRunActive: false,
        HasEmbeddedSequence: false,
        HasAxes: true,
        HasAuthoredLayout: true,
        HasVirtualCamera: false,
        HasCycleStartInput: false,
        CycleStartActive: false,
        HasActiveFaults: false,
        ControlOwner: SimulationControlOwner.Manual,
        ActiveSequenceStatus: null,
        ActiveSequenceId: null);

    private sealed class RecordingSimulationEngine : ISimulationEngine
    {
        private readonly Channel<SimulationSnapshot> _snapshotChannel =
            Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel =
            Channel.CreateUnbounded<SimulationEvent>();
        private readonly TaskCompletionSource<bool> _releaseFirstCommand =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<SimulationCommand> Commands { get; } = [];

        internal TaskCompletionSource<SimulationCommand> FirstCommandSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SimulationSnapshot CurrentSnapshot { get; } = new(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [],
            0,
            [],
            []);

        public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotChannel.Reader;

        public ChannelReader<SimulationEvent> EventReader => _eventChannel.Reader;

        public Task<SimulationEngineTerminationResult> Termination => Task.FromResult(
            new SimulationEngineTerminationResult(
                SimulationEngineTerminationOutcome.Normal,
                0,
                TimeSpan.Zero));

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<SimulationCommandResult> EnqueueCommandAsync(
            SimulationCommand command,
            CancellationToken cancellationToken = default)
        {
            lock (Commands)
            {
                Commands.Add(command);
            }

            if (FirstCommandSeen.TrySetResult(command))
            {
                await _releaseFirstCommand.Task.WaitAsync(cancellationToken);
            }

            return new SimulationCommandResult(
                command.CommandId,
                true,
                0,
                TimeSpan.Zero,
                SimulationCommandErrorCode.None,
                null);
        }

        public void ReleaseFirstCommand() => _releaseFirstCommand.TrySetResult(true);

        public void AddAxis(OpenVisionLab.Machine.Simulation.Axis.ServoAxisComponent axis)
        {
        }

        public void Dispose()
        {
            _snapshotChannel.Writer.TryComplete();
            _eventChannel.Writer.TryComplete();
            _releaseFirstCommand.TrySetCanceled();
        }
    }
}
