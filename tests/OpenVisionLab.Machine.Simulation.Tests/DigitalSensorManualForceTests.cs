using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DigitalSensorManualForceTests
{
    [Fact]
    public async Task ManualForce_PersistsAcrossTick_ClearsToNominal_ConflictsWithFault_AndResets()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(100) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntime()))).IsAccepted);

        var beforeManual = await engine.EnqueueCommandAsync(
            new SetDigitalSensorForceCommand("sensor-clear", true));
        Assert.False(beforeManual.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ControlOwnerNotAllowed, beforeManual.ErrorCode);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        Assert.Equal(
            "di.clear",
            Assert.Single(
                engine.CurrentSnapshot.LayoutComponents,
                component => component.Id == "sensor-clear").SensorOutputChannelId);
        var missing = await engine.EnqueueCommandAsync(
            new SetDigitalSensorForceCommand("missing", true));
        Assert.Equal(SimulationCommandErrorCode.DigitalSensorNotFound, missing.ErrorCode);

        var forceOn = new SetDigitalSensorForceCommand("sensor-clear", true);
        Assert.True((await engine.EnqueueCommandAsync(forceOn)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        AssertSignal(engine, "di.clear", effective: true, nominal: false, forced: true);

        var clearOn = new SetDigitalSensorForceCommand("sensor-clear", null);
        Assert.True((await engine.EnqueueCommandAsync(clearOn)).IsAccepted);
        AssertSignal(engine, "di.clear", effective: false, nominal: false, forced: null);

        var forceOff = new SetDigitalSensorForceCommand("sensor-detected", false);
        Assert.True((await engine.EnqueueCommandAsync(forceOff)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        AssertSignal(engine, "di.detected", effective: false, nominal: true, forced: false);

        var clearOff = new SetDigitalSensorForceCommand("sensor-detected", null);
        Assert.True((await engine.EnqueueCommandAsync(clearOff)).IsAccepted);
        AssertSignal(engine, "di.detected", effective: true, nominal: true, forced: null);

        Assert.True((await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "di.clear",
                false))).IsAccepted);
        var forceDuringFault = await engine.EnqueueCommandAsync(
            new SetDigitalSensorForceCommand("sensor-clear", true));
        Assert.False(forceDuringFault.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.DigitalSensorInterlocked, forceDuringFault.ErrorCode);
        Assert.True((await engine.EnqueueCommandAsync(
            new ClearSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "di.clear"))).IsAccepted);

        Assert.True((await engine.EnqueueCommandAsync(
            new SetDigitalSensorForceCommand("sensor-clear", true))).IsAccepted);
        var faultDuringForce = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "di.clear",
                false));
        Assert.False(faultDuringForce.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.FaultApplicationRejected, faultDuringForce.ErrorCode);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        AssertSignal(engine, "di.clear", effective: false, nominal: false, forced: null);
        Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);

        await engine.StopAsync();
        var events = new List<SimulationEvent>();
        await foreach (SimulationEvent item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }
        Assert.Contains(events, item =>
            item.Code == "DigitalSensorForceOnAccepted" && item.CommandId == forceOn.CommandId);
        Assert.Contains(events, item =>
            item.Code == "DigitalSensorForceOffAccepted" && item.CommandId == forceOff.CommandId);
        Assert.Contains(events, item =>
            item.Code == "DigitalSensorForceCleared" && item.CommandId == clearOff.CommandId);
    }

    private static SimulationRuntimeConfiguration CreateRuntime()
    {
        ChannelDefinition[] channels =
        [
            Channel("di.clear"),
            Channel("di.detected")
        ];
        LayoutComponentRuntimeConfiguration[] components =
        [
            new MachineFrameRuntimeConfiguration(
                "target-clear",
                "Clear target",
                new LayoutRuntimeTransform(100, 0),
                new LayoutRuntimeSize(10, 10)),
            new MachineFrameRuntimeConfiguration(
                "target-detected",
                "Detected target",
                new LayoutRuntimeTransform(0, 0),
                new LayoutRuntimeSize(10, 10)),
            Sensor("sensor-clear", "di.clear", "target-clear"),
            Sensor("sensor-detected", "di.detected", "target-detected")
        ];
        return new SimulationRuntimeConfiguration(
            Array.Empty<AxisConfiguration>(),
            channels,
            Array.Empty<CompiledSequence>(),
            Array.Empty<VirtualCameraConfiguration>(),
            null,
            new MachineLayoutRuntimeConfiguration("main", "Main", components));
    }

    private static DigitalSensorRuntimeConfiguration Sensor(
        string id,
        string channelId,
        string targetId) =>
        new(
            id,
            id,
            channelId,
            targetId,
            0,
            0,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(10, 10));

    private static ChannelDefinition Channel(string id) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = ChannelKind.DigitalInput,
            InitialValue = 0
        };

    private static void AssertSignal(
        FixedStepSimulationEngine engine,
        string id,
        bool effective,
        bool nominal,
        bool? forced)
    {
        DigitalSignalSnapshot signal = Assert.Single(
            engine.CurrentSnapshot.Signals,
            candidate => candidate.Id == id);
        Assert.Equal(effective, signal.Value);
        Assert.Equal(nominal, signal.NominalValue);
        Assert.Equal(forced, signal.OverrideValue);
    }
}
