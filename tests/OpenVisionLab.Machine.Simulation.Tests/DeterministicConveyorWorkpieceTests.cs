using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicConveyorWorkpieceTests
{
    [Fact]
    public void Tick_RunAndReverse_TransportsWorkpieceContinuouslyAndClampsAtEnds()
    {
        DeterministicSignalHub hub = CreateHub();
        DeterministicMachineLayout layout = CreateLayout(hub, rotationDegrees: 0);

        AssertWorkpiece(layout.CaptureSnapshots(), -40, 0);
        Assert.True(hub.SetDigitalOutput(
            "do.conveyor.run",
            true,
            SignalWriteOwner.EmbeddedSequence).IsAccepted);

        MachineLayoutTickResult forward = layout.Tick(EmptyAxes());
        AssertWorkpiece(forward.Components, -39, 0);
        ConveyorStateTransition started = Assert.Single(forward.ConveyorStateTransitions);
        Assert.True(started.CurrentRunning);
        Assert.Equal(ConveyorDirection.Forward, started.CurrentDirection);

        for (var tick = 0; tick < 100; tick++)
        {
            layout.Tick(EmptyAxes());
        }
        AssertWorkpiece(layout.CaptureSnapshots(), 40, 0);

        Assert.True(hub.SetDigitalOutput(
            "do.conveyor.reverse",
            true,
            SignalWriteOwner.EmbeddedSequence).IsAccepted);
        MachineLayoutTickResult reverse = layout.Tick(EmptyAxes());
        AssertWorkpiece(reverse.Components, 39, 0);
        Assert.Equal(ConveyorDirection.Reverse, Assert.Single(reverse.ConveyorStateTransitions).CurrentDirection);
    }

    [Fact]
    public void Tick_RotatedConveyor_MovesWorkpieceAlongAuthoredLocalAxisBeforeSensorEvaluation()
    {
        DeterministicSignalHub hub = CreateHub(includeSensor: true);
        DeterministicMachineLayout layout = CreateLayout(hub, rotationDegrees: 90, includeSensor: true);
        hub.SetDigitalOutput("do.conveyor.run", true, SignalWriteOwner.EmbeddedSequence);

        for (var tick = 0; tick < 29; tick++)
        {
            layout.Tick(EmptyAxes());
        }

        LayoutComponentSnapshot workpiece = Component(layout.CaptureSnapshots(), "workpiece-1");
        Assert.Equal(0, workpiece.X, precision: 10);
        Assert.Equal(-11, workpiece.Y, precision: 10);
        Assert.True(Component(layout.CaptureSnapshots(), "sensor-1").IsDetected);
        Assert.True(hub.ReadDigitalSignal("di.sensor").Value);
    }

    [Fact]
    public void Configuration_WorkpieceOutsideConveyor_IsRejected()
    {
        var conveyor = Conveyor(rotationDegrees: 0);
        var workpiece = Workpiece(x: 41, y: 0, rotationDegrees: 0);

        Assert.Throws<ArgumentException>(() => new MachineLayoutRuntimeConfiguration(
            "main",
            "Main",
            new LayoutComponentRuntimeConfiguration[] { conveyor, workpiece }));
    }

    [Fact]
    public async Task ManualCommand_StartStopDirectionStepAndResetUseOrderedEngineState()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(100) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntime()))).IsAccepted);

        var beforeManual = await engine.EnqueueCommandAsync(
            new SetConveyorCommand("conveyor-1", true, ConveyorDirection.Forward));
        Assert.False(beforeManual.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ControlOwnerNotAllowed, beforeManual.ErrorCode);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var missing = await engine.EnqueueCommandAsync(
            new SetConveyorCommand("missing", true, ConveyorDirection.Forward));
        var invalidDirection = await engine.EnqueueCommandAsync(
            new SetConveyorCommand("conveyor-1", true, (ConveyorDirection)99));
        Assert.Equal(SimulationCommandErrorCode.ConveyorNotFound, missing.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.ConveyorCommandInvalid, invalidDirection.ErrorCode);

        var forward = new SetConveyorCommand(
            "conveyor-1",
            true,
            ConveyorDirection.Forward);
        Assert.True((await engine.EnqueueCommandAsync(forward)).IsAccepted);
        Assert.False(Component(engine.CurrentSnapshot.LayoutComponents, "conveyor-1").ConveyorRunning);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        LayoutComponentSnapshot forwardConveyor = Component(
            engine.CurrentSnapshot.LayoutComponents,
            "conveyor-1");
        Assert.True(forwardConveyor.ConveyorRunning);
        Assert.Equal(ConveyorDirection.Forward, forwardConveyor.ConveyorDirection);
        Assert.Equal(-39, Component(
            engine.CurrentSnapshot.LayoutComponents,
            "workpiece-1").CarrierPosition);

        var stop = new SetConveyorCommand(
            "conveyor-1",
            false,
            ConveyorDirection.Forward);
        Assert.True((await engine.EnqueueCommandAsync(stop)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.False(Component(
            engine.CurrentSnapshot.LayoutComponents,
            "conveyor-1").ConveyorRunning);
        Assert.Equal(-39, Component(
            engine.CurrentSnapshot.LayoutComponents,
            "workpiece-1").CarrierPosition);

        var reverse = new SetConveyorCommand(
            "conveyor-1",
            true,
            ConveyorDirection.Reverse);
        Assert.True((await engine.EnqueueCommandAsync(reverse)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        LayoutComponentSnapshot reverseConveyor = Component(
            engine.CurrentSnapshot.LayoutComponents,
            "conveyor-1");
        Assert.True(reverseConveyor.ConveyorRunning);
        Assert.Equal(ConveyorDirection.Reverse, reverseConveyor.ConveyorDirection);
        Assert.Equal(-40, Component(
            engine.CurrentSnapshot.LayoutComponents,
            "workpiece-1").CarrierPosition);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        LayoutComponentSnapshot resetConveyor = Component(
            engine.CurrentSnapshot.LayoutComponents,
            "conveyor-1");
        Assert.False(resetConveyor.ConveyorRunning);
        Assert.Equal(ConveyorDirection.Forward, resetConveyor.ConveyorDirection);
        Assert.Equal(-40, Component(
            engine.CurrentSnapshot.LayoutComponents,
            "workpiece-1").CarrierPosition);
        Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);

        await engine.StopAsync();
        var events = new List<SimulationEvent>();
        await foreach (SimulationEvent item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }
        SimulationEvent forwardCommand = Assert.Single(events, item =>
            item.Code == "ConveyorRunAccepted" && item.CommandId == forward.CommandId);
        SimulationEvent forwardState = Assert.Single(events, item =>
            item.Code == "ConveyorStateChanged"
            && item.Message.Contains("STOPPED Forward -> RUNNING Forward"));
        SimulationEvent stopCommand = Assert.Single(events, item =>
            item.Code == "ConveyorStopAccepted" && item.CommandId == stop.CommandId);
        SimulationEvent stoppedState = Assert.Single(events, item =>
            item.Code == "ConveyorStateChanged"
            && item.Message.Contains("RUNNING Forward -> STOPPED Forward"));
        SimulationEvent reverseCommand = Assert.Single(events, item =>
            item.Code == "ConveyorRunAccepted" && item.CommandId == reverse.CommandId);
        SimulationEvent reverseState = Assert.Single(events, item =>
            item.Code == "ConveyorStateChanged"
            && item.Message.Contains("STOPPED Forward -> RUNNING Reverse"));
        Assert.True(forwardCommand.EventIndex < forwardState.EventIndex);
        Assert.True(forwardState.EventIndex < stopCommand.EventIndex);
        Assert.True(stopCommand.EventIndex < stoppedState.EventIndex);
        Assert.True(stoppedState.EventIndex < reverseCommand.EventIndex);
        Assert.True(reverseCommand.EventIndex < reverseState.EventIndex);
    }

    private static DeterministicSignalHub CreateHub(bool includeSensor = false)
    {
        var channels = new List<ChannelDefinition>
        {
            Channel("do.conveyor.run", ChannelKind.DigitalOutput),
            Channel("do.conveyor.reverse", ChannelKind.DigitalOutput)
        };
        if (includeSensor)
        {
            channels.Add(Channel("di.sensor", ChannelKind.DigitalInput));
        }

        SignalHubCreationResult creation = DeterministicSignalHub.Create(channels);
        Assert.True(creation.IsAccepted);
        return creation.Hub!;
    }

    private static DeterministicMachineLayout CreateLayout(
        DeterministicSignalHub hub,
        double rotationDegrees,
        bool includeSensor = false)
    {
        var components = new List<LayoutComponentRuntimeConfiguration>
        {
            Conveyor(rotationDegrees),
            rotationDegrees == 0
                ? Workpiece(-40, 0, rotationDegrees)
                : Workpiece(0, -40, rotationDegrees)
        };
        if (includeSensor)
        {
            components.Add(new DigitalSensorRuntimeConfiguration(
                "sensor-1",
                "Sensor",
                "di.sensor",
                "workpiece-1",
                0,
                0,
                new LayoutRuntimeTransform(0, 0, rotationDegrees),
                new LayoutRuntimeSize(2, 20)));
        }

        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration("main", "Main", components),
            hub);
        layout.Reset();
        return layout;
    }

    private static SimulationRuntimeConfiguration CreateRuntime()
    {
        ChannelDefinition[] channels =
        [
            Channel("do.conveyor.run", ChannelKind.DigitalOutput),
            Channel("do.conveyor.reverse", ChannelKind.DigitalOutput)
        ];
        var layout = new MachineLayoutRuntimeConfiguration(
            "main",
            "Main",
            new LayoutComponentRuntimeConfiguration[]
            {
                Conveyor(rotationDegrees: 0),
                Workpiece(-40, 0, rotationDegrees: 0)
            });
        return new SimulationRuntimeConfiguration(
            Array.Empty<AxisConfiguration>(),
            channels,
            Array.Empty<CompiledSequence>(),
            Array.Empty<VirtualCameraConfiguration>(),
            null,
            layout);
    }

    private static ConveyorRuntimeConfiguration Conveyor(double rotationDegrees) =>
        new(
            "conveyor-1",
            "Conveyor",
            "do.conveyor.run",
            "do.conveyor.reverse",
            10,
            0.1,
            new LayoutRuntimeTransform(0, 0, rotationDegrees),
            new LayoutRuntimeSize(100, 40));

    private static WorkpieceRuntimeConfiguration Workpiece(
        double x,
        double y,
        double rotationDegrees) =>
        new(
            "workpiece-1",
            "Workpiece",
            "Test Part",
            "conveyor-1",
            WorkpieceInspectionState.Pending,
            new LayoutRuntimeTransform(x, y, rotationDegrees),
            new LayoutRuntimeSize(20, 20));

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = 0
        };

    private static IReadOnlyDictionary<string, AxisSnapshot> EmptyAxes() =>
        new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal);

    private static LayoutComponentSnapshot Component(
        IEnumerable<LayoutComponentSnapshot> snapshots,
        string id) =>
        Assert.Single(snapshots, snapshot => snapshot.Id == id);

    private static void AssertWorkpiece(
        IEnumerable<LayoutComponentSnapshot> snapshots,
        double expectedX,
        double expectedY)
    {
        LayoutComponentSnapshot workpiece = Component(snapshots, "workpiece-1");
        Assert.Equal(expectedX, workpiece.X, precision: 10);
        Assert.Equal(expectedY, workpiece.Y, precision: 10);
        Assert.Equal(expectedX, workpiece.CarrierPosition!.Value, precision: 10);
    }
}
