using System.Threading.Channels;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualControlCommandWorkflowTests
{
    [Fact]
    public async Task StartEquipmentControlRoutesEachSupportedComponentKind()
    {
        OpenVisionLanguageService.Load();

        foreach (var kind in new[]
        {
            LayoutComponentKind.LinearStage,
            LayoutComponentKind.RotaryStage,
            LayoutComponentKind.DigitalSensor,
            LayoutComponentKind.PneumaticCylinder,
            LayoutComponentKind.Conveyor
        })
        {
            using var engine = new RecordingSimulationEngine(acceptCommands: true);
            var startedCount = 0;
            var workflow = CreateWorkflow(
                engine,
                new ManualEquipmentPresentation(),
                () => kind,
                () => startedCount++);

            await workflow.StartEquipmentControlAsync();

            Assert.IsType<StartManualControlCommand>(Assert.Single(engine.Commands));
            Assert.Equal(1, startedCount);
        }
    }

    [Fact]
    public async Task RejectedStartDoesNotChangeManualControlState()
    {
        OpenVisionLanguageService.Load();
        using var engine = new RecordingSimulationEngine(acceptCommands: false);
        var startedCount = 0;
        var workflow = CreateWorkflow(
            engine,
            new ManualEquipmentPresentation(),
            () => LayoutComponentKind.Conveyor,
            () => startedCount++);

        await workflow.StartEquipmentControlAsync();

        Assert.IsType<StartManualControlCommand>(Assert.Single(engine.Commands));
        Assert.Equal(0, startedCount);
    }

    [Fact]
    public async Task ActuatorCommandsUseThePresentationSelection()
    {
        OpenVisionLanguageService.Load();
        using var engine = new RecordingSimulationEngine(acceptCommands: true);
        var presentation = new ManualEquipmentPresentation();
        var workflow = CreateWorkflow(
            engine,
            presentation,
            () => null,
            () => { });

        presentation.ApplyProjection(CreateProjection(
            LayoutComponentKind.DigitalSensor,
            "sensor-1"));
        await workflow.SetSensorForceAsync(true);

        presentation.ApplyProjection(CreateProjection(
            LayoutComponentKind.PneumaticCylinder,
            "cylinder-1"));
        await workflow.SetCylinderAsync(extend: true);

        presentation.ApplyProjection(CreateProjection(
            LayoutComponentKind.Conveyor,
            "conveyor-1"));
        await workflow.SetConveyorAsync(true, ConveyorDirection.Reverse);

        Assert.Collection(
            engine.Commands,
            command =>
            {
                var force = Assert.IsType<SetDigitalSensorForceCommand>(command);
                Assert.Equal("sensor-1", force.SensorId);
                Assert.True(force.ForcedValue);
            },
            command =>
            {
                var cylinder = Assert.IsType<SetCylinderCommand>(command);
                Assert.Equal("cylinder-1", cylinder.CylinderId);
                Assert.True(cylinder.Extend);
            },
            command =>
            {
                var conveyor = Assert.IsType<SetConveyorCommand>(command);
                Assert.Equal("conveyor-1", conveyor.ConveyorId);
                Assert.True(conveyor.Running);
                Assert.Equal(ConveyorDirection.Reverse, conveyor.Direction);
            });
    }

    private static ManualControlCommandWorkflow CreateWorkflow(
        RecordingSimulationEngine engine,
        ManualEquipmentPresentation presentation,
        Func<LayoutComponentKind?> selectedKind,
        Action markStarted) => new(
        new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
        presentation,
        selectedKind,
        markStarted);

    private static ManualEquipmentProjection CreateProjection(
        LayoutComponentKind selectedKind,
        string selectedId) => new(
        CreateSnapshot(),
        selectedId,
        selectedKind,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        SimulationControlOwner.Manual,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: null);

    private static SimulationSnapshot CreateSnapshot() => new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [new DigitalSignalSnapshot("di.sensor-1", "Sensor", ChannelKind.DigitalInput, false)],
        [],
        [],
        AutomaticRunSnapshot.NotConfigured,
        [
            new LayoutComponentSnapshot(
                "sensor-1",
                "Sensor",
                LayoutComponentKind.DigitalSensor,
                0,
                0,
                0,
                10,
                10,
                false,
                null,
                SensorOutputChannelId: "di.sensor-1"),
            new LayoutComponentSnapshot(
                "cylinder-1",
                "Cylinder",
                LayoutComponentKind.PneumaticCylinder,
                0,
                0,
                0,
                10,
                10,
                false,
                null,
                CylinderState: PneumaticCylinderState.Retracted),
            new LayoutComponentSnapshot(
                "conveyor-1",
                "Conveyor",
                LayoutComponentKind.Conveyor,
                0,
                0,
                0,
                10,
                10,
                false,
                null,
                ConveyorRunning: false,
                ConveyorDirection: ConveyorDirection.Forward)]);

    private sealed class RecordingSimulationEngine : ISimulationEngine
    {
        private readonly bool _acceptCommands;
        private readonly Channel<SimulationSnapshot> _snapshotChannel =
            Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel =
            Channel.CreateUnbounded<SimulationEvent>();

        internal RecordingSimulationEngine(bool acceptCommands)
        {
            _acceptCommands = acceptCommands;
            CurrentSnapshot = new SimulationSnapshot(
                TimeSpan.Zero,
                0,
                SimulationRunMode.Paused,
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

        public void AddAxis(OpenVisionLab.Machine.Simulation.Axis.ServoAxisComponent axis)
        {
        }

        public void Dispose()
        {
            _snapshotChannel.Writer.TryComplete();
            _eventChannel.Writer.TryComplete();
        }
    }
}
