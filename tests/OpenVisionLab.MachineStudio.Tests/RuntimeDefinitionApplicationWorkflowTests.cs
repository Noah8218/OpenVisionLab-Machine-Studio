using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeDefinitionApplicationWorkflowTests
{
    [Fact]
    public async Task AppliesCompiledProjectToLiveEngine()
    {
        using var engine = new TestSimulationEngine(true);
        var workflow = new RuntimeDefinitionApplicationWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5));

        var result = await workflow.ApplyAsync(new MachineProjectDocument
        {
            Name = "Valid project"
        });

        Assert.Equal(RuntimeDefinitionApplicationOutcome.Applied, result.Outcome);
        Assert.True(result.IsAccepted);
        Assert.True(result.CommandResult!.IsAccepted);
        var command = Assert.IsType<ConfigureRuntimeCommand>(Assert.Single(engine.Commands));
        Assert.Empty(command.Configuration.Axes);
        Assert.Empty(command.Configuration.Channels);
        Assert.Empty(command.Configuration.Sequences);
    }

    [Fact]
    public async Task ReturnsStableCompilationDetailWithoutTouchingEngine()
    {
        using var engine = new TestSimulationEngine();
        var workflow = new RuntimeDefinitionApplicationWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5));

        var result = await workflow.ApplyAsync(new MachineProjectDocument
        {
            Name = "Invalid fixed-step project",
            Simulation = new SimulationDefinition { FixedStepMilliseconds = 10 }
        });

        Assert.Equal(
            RuntimeDefinitionApplicationOutcome.CompilationRejected,
            result.Outcome);
        Assert.False(result.IsAccepted);
        Assert.Contains("FixedStepMismatch", result.CompilationDetail, StringComparison.Ordinal);
        Assert.Null(result.CommandResult);
        Assert.Empty(engine.Commands);
    }

    [Fact]
    public async Task ReturnsEngineRejectionAfterSuccessfulCompilation()
    {
        using var engine = new TestSimulationEngine(false);
        var workflow = new RuntimeDefinitionApplicationWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5));

        var result = await workflow.ApplyAsync(new MachineProjectDocument
        {
            Name = "Engine rejection project"
        });

        Assert.Equal(RuntimeDefinitionApplicationOutcome.EngineRejected, result.Outcome);
        Assert.False(result.IsAccepted);
        Assert.False(result.CommandResult!.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.EngineFaulted, result.CommandResult.ErrorCode);
        Assert.Single(engine.Commands);
        Assert.IsType<ConfigureRuntimeCommand>(engine.Commands[0]);
    }

    private sealed class TestSimulationEngine : ISimulationEngine
    {
        private readonly bool _acceptCommands;
        private readonly Channel<SimulationSnapshot> _snapshotChannel =
            Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel =
            Channel.CreateUnbounded<SimulationEvent>();

        internal TestSimulationEngine(bool acceptCommands = true)
        {
            _acceptCommands = acceptCommands;
            CurrentSnapshot = new SimulationSnapshot(
                TimeSpan.Zero,
                0,
                SimulationRunMode.Paused,
                SimulationControlOwner.Definition,
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
            return Task.FromResult(
                new SimulationCommandResult(
                    command.CommandId,
                    _acceptCommands,
                    0,
                    TimeSpan.Zero,
                    _acceptCommands
                        ? SimulationCommandErrorCode.None
                        : SimulationCommandErrorCode.EngineFaulted,
                    _acceptCommands ? null : "test rejection"));
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
