using System.Globalization;
using System.Threading.Channels;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationCommandPresentationDispatcherTests
{
    [Fact]
    public async Task AcceptedDebuggerCommandAppliesSnapshotBeforePresentation()
    {
        OpenVisionLanguageService.Load();
        using var engine = new TestSimulationEngine(acceptCommands: true);
        var events = new List<string>();
        var dispatcher = new SimulationCommandPresentationDispatcher(
            engine,
            _ => events.Add("status"),
            (_, _) => events.Add("log"));

        var result = await dispatcher.DispatchRuntimeDebuggerAsync(
            new ResetCommand(),
            () => events.Add("snapshot"));

        Assert.True(result.IsAccepted);
        Assert.Equal(new[] { "snapshot", "status", "log" }, events);
        Assert.IsType<ResetCommand>(Assert.Single(engine.Commands));
    }

    [Fact]
    public async Task RejectedDebuggerIoAndFaultCommandsUseTheirOwnPresentationContracts()
    {
        OpenVisionLanguageService.Load();
        using var engine = new TestSimulationEngine(acceptCommands: false);
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var dispatcher = new SimulationCommandPresentationDispatcher(
            engine,
            statuses.Add,
            (category, message) => logs.Add((category, message)));

        var debuggerResult = await dispatcher.DispatchRuntimeDebuggerAsync(
            new PauseCommand(),
            () => { });
        var ioResult = await dispatcher.DispatchDigitalIoAsync(
            new SetVirtualInputForceCommand("di-1", true));
        var faultResult = await dispatcher.DispatchFaultAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-1"));

        Assert.All(
            new[] { debuggerResult, ioResult, faultResult },
            result => Assert.Equal(SimulationCommandErrorCode.EngineFaulted, result.ErrorCode));
        Assert.Equal(
            new[] { "Sequence", "I/O", "Fault" },
            logs.Select(log => log.Category));
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Io.StatusRejected"),
                OpenVisionLanguageService.T("Io.ActionForceOn")),
            statuses[1]);
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Fault.StatusRejected"),
                OpenVisionLanguageService.T("Fault.ActionInject")),
            statuses[2]);
        Assert.Equal(3, engine.Commands.Count);
    }

    private sealed class TestSimulationEngine : ISimulationEngine
    {
        private readonly bool _acceptCommands;
        private readonly Channel<SimulationSnapshot> _snapshotChannel =
            Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel =
            Channel.CreateUnbounded<SimulationEvent>();

        internal TestSimulationEngine(bool acceptCommands)
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
