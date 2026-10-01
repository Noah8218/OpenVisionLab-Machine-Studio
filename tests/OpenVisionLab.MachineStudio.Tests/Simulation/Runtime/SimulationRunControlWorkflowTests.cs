using System.Threading.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
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

    [Fact]
    public async Task PausedRunResumesWithoutResetAndDesignEditResetsBeforeNextRun()
    {
        using var engine = new RecordingSimulationEngine { BlockFirstCommand = false };
        var state = CreateState() with { IsRunning = true };
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        await workflow.PauseAsync();
        Assert.False(state.IsRunning);
        await workflow.RunAsync();
        Assert.True(state.IsRunning);

        Assert.True(await workflow.ResetForDesignModeAsync());
        Assert.False(state.IsRunning);
        await workflow.RunAsync();
        Assert.True(state.IsRunning);

        Assert.Collection(
            engine.Commands,
            command => Assert.IsType<PauseCommand>(command),
            command => Assert.IsType<PlayCommand>(command),
            command => Assert.IsType<ResetCommand>(command),
            command => Assert.IsType<PlayCommand>(command));
    }

    [Fact]
    public async Task ManualPauseAndResumeDoesNotStartConfiguredSequencesOrExternalInspection()
    {
        using var engine = new RecordingSimulationEngine { BlockFirstCommand = false };
        var state = CreateState() with
        {
            IsRunning = true,
            HasAutomaticRun = true,
            AutomaticRunConfigured = true,
            HasEmbeddedSequence = true,
            ActiveSequenceId = "automatic-sequence",
            ActiveSequenceStatus = SequenceExecutionStatus.Ready,
            AutomaticExternalInspectionEnabled = true
        };
        var preparationCount = 0;
        using var workflow = new SimulationRunControlWorkflow(
            engine, TimeSpan.FromMilliseconds(5), () => state, () => Task.FromResult(true),
            _ => { }, value => state = state with { IsRunning = value },
            _ => { }, () => { }, _ => { }, (_, _) => { }, () => { },
            _ => { preparationCount++; return Task.FromResult(true); });

        await workflow.PauseAsync();
        Assert.False(state.IsRunning);
        await workflow.RunAsync();
        Assert.True(state.IsRunning);
        Assert.Equal(SimulationControlOwner.Manual, state.ControlOwner);
        Assert.Equal(0, preparationCount);
        Assert.Collection(engine.Commands,
            command => Assert.IsType<PauseCommand>(command),
            command => Assert.IsType<PlayCommand>(command));
    }

    [Fact]
    public async Task AutomaticResultWaitAcceptsExplicitPauseWithoutAdvancingTheSequence()
    {
        using var engine = new RecordingSimulationEngine { BlockFirstCommand = false };
        var state = CreateState() with
        {
            HasAutomaticRun = true,
            AutomaticRunConfigured = true,
            AutomaticRunActive = true,
            HasEmbeddedSequence = true,
            ControlOwner = SimulationControlOwner.EmbeddedSequence,
            ActiveSequenceStatus = SequenceExecutionStatus.Running,
            AutomaticExternalInspectionEnabled = true,
            AutomaticExternalInspectionWaiting = true
        };
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with { IsRunning = value });

        Assert.True(workflow.CanPause());
        await workflow.PauseAsync();
        Assert.IsType<PauseCommand>(Assert.Single(engine.Commands));
        Assert.False(state.IsRunning);
        Assert.False(workflow.CanRun());
        Assert.False(workflow.CanStep());
    }

    [Fact]
    public async Task ResumesActiveExternalRunAfterResultWithoutRearmingInspection()
    {
        using var engine = new RecordingSimulationEngine { BlockFirstCommand = false };
        var state = CreateState() with
        {
            HasAutomaticRun = true,
            AutomaticRunConfigured = true,
            AutomaticRunActive = true,
            HasEmbeddedSequence = true,
            ControlOwner = SimulationControlOwner.EmbeddedSequence,
            ActiveSequenceId = "automatic-sequence",
            ActiveSequenceStatus = SequenceExecutionStatus.Running,
            AutomaticExternalInspectionEnabled = true
        };
        var preparationCount = 0;
        using var workflow = new SimulationRunControlWorkflow(
            engine, TimeSpan.FromMilliseconds(5), () => state, () => Task.FromResult(true),
            _ => { }, value => state = state with { IsRunning = value },
            _ => { }, () => { }, _ => { }, (_, _) => { }, () => { },
            _ => { preparationCount++; return Task.FromResult(false); });

        Assert.True(workflow.CanRun());
        await workflow.RunAsync();

        Assert.Equal(0, preparationCount);
        Assert.IsType<PlayCommand>(Assert.Single(engine.Commands));
        Assert.True(state.IsRunning);
        Assert.True(state.AutomaticRunActive);
        Assert.Equal(SimulationControlOwner.EmbeddedSequence, state.ControlOwner);
    }

    [Fact]
    public async Task FailedAutomaticInspectionPreparationKeepsDesignModeAndAllowsRetry()
    {
        using var engine = new RecordingSimulationEngine { BlockFirstCommand = false };
        var state = CreateState() with
        {
            IsRunMode = false,
            HasAutomaticRun = true,
            AutomaticRunConfigured = true,
            ActiveSequenceStatus = SequenceExecutionStatus.Ready,
            AutomaticExternalInspectionEnabled = true
        };
        var preparationReady = false;
        using var workflow = new SimulationRunControlWorkflow(
            engine, TimeSpan.FromMilliseconds(5), () => state, () => Task.FromResult(true),
            value => state = state with { IsRunMode = !value },
            value => state = state with { IsRunning = value },
            _ => { }, () => { }, _ => { }, (_, _) => { }, () => { },
            _ => Task.FromResult(preparationReady));

        await workflow.RunAsync();
        Assert.False(state.IsRunMode);
        Assert.False(state.IsRunning);
        Assert.Empty(engine.Commands);

        preparationReady = true;
        await workflow.RunAsync();
        Assert.True(state.IsRunMode);
        Assert.True(state.IsRunning);
        Assert.IsType<StartAutomaticRunCommand>(Assert.Single(engine.Commands));
    }

    [Fact]
    public async Task RejectedAutomaticStartKeepsDesignMode()
    {
        using var engine = new RecordingSimulationEngine { AcceptCommands = false, BlockFirstCommand = false };
        var state = CreateState() with
        {
            IsRunMode = false,
            HasAutomaticRun = true,
            AutomaticRunConfigured = true,
            ActiveSequenceStatus = SequenceExecutionStatus.Ready
        };
        using var workflow = new SimulationRunControlWorkflow(
            engine, TimeSpan.FromMilliseconds(5), () => state, () => Task.FromResult(true),
            value => state = state with { IsRunMode = !value },
            value => state = state with { IsRunning = value },
            _ => { }, () => { }, _ => { }, (_, _) => { }, () => { });

        await workflow.RunAsync();

        Assert.False(state.IsRunMode);
        Assert.False(state.IsRunning);
        Assert.IsType<StartAutomaticRunCommand>(Assert.Single(engine.Commands));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ManualStartChangesModeOnlyAfterAcceptedCommand(bool hasEmbeddedSequence, bool acceptCommands)
    {
        using var engine = new RecordingSimulationEngine { AcceptCommands = acceptCommands, BlockFirstCommand = false };
        var state = CreateState() with
        {
            IsRunMode = false,
            HasEmbeddedSequence = hasEmbeddedSequence,
            ActiveSequenceId = hasEmbeddedSequence ? "sequence-1" : null,
            ActiveSequenceStatus = hasEmbeddedSequence ? SequenceExecutionStatus.Ready : null
        };
        using var workflow = new SimulationRunControlWorkflow(
            engine, TimeSpan.FromMilliseconds(5), () => state, () => Task.FromResult(true),
            value => state = state with { IsRunMode = !value },
            value => state = state with { IsRunning = value },
            _ => { }, () => { }, _ => { }, (_, _) => { }, () => { });

        await workflow.RunAsync();

        Assert.Equal(acceptCommands, state.IsRunMode);
        Assert.Equal(acceptCommands, state.IsRunning);
        Assert.IsType(
            hasEmbeddedSequence ? typeof(StartSequenceCommand) : typeof(PlayCommand),
            engine.Commands[0]);
        if (acceptCommands && hasEmbeddedSequence)
        {
            Assert.IsType<PlayCommand>(engine.Commands[1]);
        }
        Assert.Equal(acceptCommands && hasEmbeddedSequence ? 2 : 1, engine.Commands.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbortUpdatesRunningStateOnlyAfterAcceptedCommand(bool acceptCommands)
    {
        using var engine = new RecordingSimulationEngine { AcceptCommands = acceptCommands, BlockFirstCommand = false };
        var state = CreateState() with
        {
            IsRunning = true,
            ActiveSequenceId = "sequence-1",
            ActiveSequenceStatus = SequenceExecutionStatus.Running
        };
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with { IsRunning = value });

        await workflow.AbortSequenceAsync();

        Assert.IsType<AbortSequenceCommand>(Assert.Single(engine.Commands));
        Assert.Equal(!acceptCommands, state.IsRunning);
    }

    [Fact]
    public async Task DesignModeResetWaitsForAcceptedBarrierBeforeUpdatingState()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState() with { IsRunning = true };
        var statuses = new List<string>();
        var workflow = new SimulationRunControlWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5),
            () => state,
            () => Task.FromResult(true),
            _ => { },
            value => state = state with { IsRunning = value },
            _ => { },
            () => { },
            statuses.Add,
            (_, _) => { },
            () => { });

        var pause = workflow.ResetForDesignModeAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(state.IsRunning);
        Assert.Empty(statuses);

        engine.ReleaseFirstCommand();
        Assert.True(await pause);
        Assert.False(state.IsRunning);
        Assert.Contains("Design mode", Assert.Single(statuses), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesignModeResetRejectionLeavesRunningStateUnchanged()
    {
        using var engine = new RecordingSimulationEngine { AcceptCommands = false, BlockFirstCommand = false };
        var state = CreateState() with { IsRunning = true };
        var logs = new List<(string Category, string Message)>();
        var workflow = new SimulationRunControlWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5),
            () => state,
            () => Task.FromResult(true),
            _ => { },
            value => state = state with { IsRunning = value },
            _ => { },
            () => { },
            _ => { },
            (category, message) => logs.Add((category, message)),
            () => { });

        Assert.False(await workflow.ResetForDesignModeAsync());
        Assert.True(state.IsRunning);
        Assert.Contains(logs, entry => entry.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RepeatedDesignModeResetRequestsRejectTheDuplicate()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState() with { IsRunning = true };
        var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        var firstPause = workflow.ResetForDesignModeAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondPause = workflow.ResetForDesignModeAsync();

        engine.ReleaseFirstCommand();
        Assert.True(await firstPause);
        Assert.False(await secondPause);
        Assert.Single(engine.Commands);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task InvalidatedDesignModeResetSuppressesLateAcceptance()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState() with { IsRunning = true };
        var statuses = new List<string>();
        var workflow = new SimulationRunControlWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5),
            () => state,
            () => Task.FromResult(true),
            _ => { },
            value => state = state with { IsRunning = value },
            _ => { },
            () => { },
            statuses.Add,
            (_, _) => { },
            () => { });

        var pause = workflow.ResetForDesignModeAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.InvalidatePendingExecution();

        engine.ReleaseFirstCommand();
        Assert.False(await pause);
        Assert.True(state.IsRunning);
        Assert.Empty(statuses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditingEndsBothRunningAndPausedRunsWithoutStartingAnother(bool running)
    {
        using var engine = new RecordingSimulationEngine { BlockFirstCommand = false };
        var state = CreateState() with { IsRunning = running };
        var canceledCaptures = 0;
        SimulationSnapshot? applied = null;
        using var workflow = new SimulationRunControlWorkflow(
            engine, TimeSpan.FromMilliseconds(5), () => state, () => Task.FromResult(true),
            _ => { }, value => state = state with { IsRunning = value },
            snapshot => applied = snapshot, () => canceledCaptures++, _ => { }, (_, _) => { }, () => { });

        Assert.True(await workflow.ResetForDesignModeAsync());
        Assert.IsType<ResetCommand>(Assert.Single(engine.Commands));
        Assert.False(state.IsRunning);
        Assert.Equal(1, canceledCaptures);
        Assert.Same(engine.CurrentSnapshot, applied);
    }

    [Fact]
    public async Task CanceledEditingResetLeavesRunUntouchedAndAllowsRetry()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState() with { IsRunning = true };
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with { IsRunning = value });
        using var cancellation = new CancellationTokenSource();
        var reset = workflow.ResetForDesignModeAsync(cancellation.Token);
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
        Assert.True(state.IsRunning);
        Assert.False(workflow.IsBusy);

        engine.ReleaseFirstCommand();
        Assert.True(await workflow.ResetForDesignModeAsync());
        Assert.False(state.IsRunning);
        Assert.All(engine.Commands, command => Assert.IsType<ResetCommand>(command));
    }

    [Fact]
    public async Task ProjectInvalidationSuppressesLateRunPresentation()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState();
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var workflow = new SimulationRunControlWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5),
            () => state,
            () => Task.FromResult(true),
            _ => { },
            value => state = state with { IsRunning = value },
            _ => { },
            () => { },
            statuses.Add,
            (category, message) => logs.Add((category, message)),
            () => { });

        var run = workflow.RunAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        workflow.InvalidatePendingExecution();
        engine.ReleaseFirstCommand();
        await run;

        Assert.False(state.IsRunning);
        Assert.Empty(statuses);
        Assert.Empty(logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectInvalidationDropsQueuedCommandsAndAllowsExplicitRetry(bool reset)
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState();
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        var run = workflow.RunAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = reset ? workflow.ResetAsync() : workflow.StepAsync();
        Assert.False(queued.IsCompleted);

        workflow.InvalidatePendingExecution();
        engine.ReleaseFirstCommand();
        await Task.WhenAll(run, queued).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<PlayCommand>(Assert.Single(engine.Commands));
        Assert.False(state.IsRunning);
        Assert.False(workflow.IsBusy);

        await (reset ? workflow.ResetAsync() : workflow.StepAsync());
        Assert.Equal(2, engine.Commands.Count);
        Assert.Equal(reset ? typeof(ResetCommand) : typeof(StepCommand), engine.Commands[1].GetType());
    }

    [Fact]
    public async Task DisposePreventsQueuedCommandFromExecuting()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState();
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        var run = workflow.RunAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = workflow.PauseAsync();

        workflow.Dispose();
        engine.ReleaseFirstCommand();
        await Task.WhenAll(run, pause);

        var command = Assert.Single(engine.Commands);
        Assert.IsType<PlayCommand>(command);
    }

    [Fact]
    public async Task WaitForOperationsCompletesOnlyAfterAnActiveCommandFinishes()
    {
        using var engine = new RecordingSimulationEngine();
        var state = CreateState();
        using var workflow = CreateWorkflow(engine, () => state, value => state = state with
        {
            IsRunning = value
        });

        var run = workflow.RunAsync();
        await engine.FirstCommandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        workflow.Dispose();
        var operationsIdle = workflow.WaitForOperationsAsync();

        Assert.False(operationsIdle.IsCompleted);

        engine.ReleaseFirstCommand();
        await run;
        await operationsIdle;
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

        internal bool AcceptCommands { get; set; } = true;

        internal bool BlockFirstCommand { get; set; } = true;

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

            if (BlockFirstCommand && FirstCommandSeen.TrySetResult(command))
            {
                await _releaseFirstCommand.Task.WaitAsync(cancellationToken);
            }

            return new SimulationCommandResult(
                command.CommandId,
                AcceptCommands,
                0,
                TimeSpan.Zero,
                AcceptCommands ? SimulationCommandErrorCode.None : SimulationCommandErrorCode.EngineFaulted,
                AcceptCommands ? null : "test rejection");
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
