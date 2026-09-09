using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicMachineLayoutTests
{
    [Fact]
    public void Tick_StageWorldXUsesAxisDeltaFromAuthoredHome()
    {
        var hub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
        var runtime = new DeterministicMachineLayout(
            CreateLayout(Stage("stage", "axis-x", 10, 100, 4, 4)),
            hub);

        MachineLayoutTickResult result = runtime.Tick(Axes(Axis("axis-x", 25)));

        LayoutComponentSnapshot stage = Assert.Single(result.Components);
        Assert.Equal(115, stage.X);
        Assert.Empty(result.Transitions);
    }

    [Fact]
    public void Tick_RotaryStageUsesAxisDeltaWithoutChangingPositionAndResetRestoresBaseAngle()
    {
        var hub = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!;
        var runtime = new DeterministicMachineLayout(
            CreateLayout(new RotaryStageRuntimeConfiguration(
                "rotary-stage",
                "Rotary Stage",
                "axis-r",
                30,
                new LayoutRuntimeTransform(100, 200, 15),
                Size(80, 40))),
            hub);

        LayoutComponentSnapshot moved = Assert.Single(
            runtime.Tick(Axes(Axis("axis-r", 90))).Components);

        Assert.Equal(LayoutComponentKind.RotaryStage, moved.Kind);
        Assert.Equal(100, moved.X);
        Assert.Equal(200, moved.Y);
        Assert.Equal(75, moved.RotationDegrees);

        runtime.Reset();

        LayoutComponentSnapshot reset = Assert.Single(runtime.CaptureSnapshots());
        Assert.Equal(15, reset.RotationDegrees);
    }

    [Fact]
    public void Tick_RotaryStageDynamicAngleParticipatesInSensorGeometry()
    {
        var hub = CreateHub("di.sensor");
        var runtime = new DeterministicMachineLayout(
            CreateLayout(
                new RotaryStageRuntimeConfiguration(
                    "rotary-stage",
                    "Rotary Stage",
                    "axis-r",
                    0,
                    Transform(0, 0),
                    Size(4, 2)),
                Sensor("sensor", "di.sensor", "rotary-stage", 0, 0, 0, 2.1, 0.2, 0.2)),
            hub);

        MachineLayoutTickResult result = runtime.Tick(Axes(Axis("axis-r", 90)));

        Assert.True(SensorSnapshot(result).IsDetected);
        Assert.Equal(90, result.Components.Single(item => item.Id == "rotary-stage").RotationDegrees);
    }

    [Fact]
    public void Tick_InclusiveAabbBoundary_ActivatesSensor()
    {
        var hub = CreateHub("di.sensor");
        var layout = CreateLayout(
            new LinearStageRuntimeConfiguration(
                "stage",
                "Stage",
                "axis-x",
                0,
                Transform(0, 0),
                Size(2, 2)),
            new DigitalSensorRuntimeConfiguration(
                "sensor",
                "Sensor",
                "di.sensor",
                "stage",
                0,
                0,
                Transform(2, 0),
                Size(2, 2)));
        var runtime = new DeterministicMachineLayout(layout, hub);

        MachineLayoutTickResult result = runtime.Tick(Axes(Axis("axis-x", 0)));

        Assert.True(result.Components.Single(component => component.Id == "sensor").IsDetected);
        Assert.True(hub.ReadDigitalSignal("di.sensor").Value);
        MachineLayoutTransition transition = Assert.Single(result.Transitions);
        Assert.Equal(MachineLayoutTransitionKind.SensorActivated, transition.Kind);
    }

    [Fact]
    public void Tick_AppliesExactConsecutiveOnAndOffDelayTicks()
    {
        var hub = CreateHub("di.sensor");
        var runtime = new DeterministicMachineLayout(
            CreateLayout(
                Stage("stage", "axis-x", 0, 0, 2, 2),
                Sensor("sensor", "di.sensor", "stage", 2, 2, 5, 0, 2, 2)),
            hub);

        MachineLayoutTickResult firstOn = runtime.Tick(Axes(Axis("axis-x", 4)));
        MachineLayoutTickResult secondOn = runtime.Tick(Axes(Axis("axis-x", 4)));
        MachineLayoutTickResult firstOff = runtime.Tick(Axes(Axis("axis-x", 0)));
        MachineLayoutTickResult secondOff = runtime.Tick(Axes(Axis("axis-x", 0)));

        Assert.False(SensorSnapshot(firstOn).IsDetected);
        Assert.Equal(1, SensorSnapshot(firstOn).PendingTransitionTicks);
        Assert.Empty(firstOn.Transitions);
        Assert.True(SensorSnapshot(secondOn).IsDetected);
        Assert.Equal(MachineLayoutTransitionKind.SensorActivated, Assert.Single(secondOn.Transitions).Kind);

        Assert.True(SensorSnapshot(firstOff).IsDetected);
        Assert.Equal(1, SensorSnapshot(firstOff).PendingTransitionTicks);
        Assert.Empty(firstOff.Transitions);
        Assert.False(SensorSnapshot(secondOff).IsDetected);
        Assert.Equal(MachineLayoutTransitionKind.SensorDeactivated, Assert.Single(secondOff.Transitions).Kind);
    }

    [Fact]
    public void Tick_UsesSimulationComponentOwnershipWhileOutputsRemainSequenceOwned()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Channel("di.sensor", ChannelKind.DigitalInput),
            Channel("do.active", ChannelKind.DigitalOutput)
        }).Hub!;
        var runtime = new DeterministicMachineLayout(
            CreateLayout(
                Stage("stage", "axis-x", 0, 0, 2, 2),
                Sensor("sensor", "di.sensor", "stage", 0, 0, 0, 0, 2, 2)),
            hub);

        runtime.Tick(Axes(Axis("axis-x", 0)));
        SignalWriteResult componentInput = hub.SetDigitalInput(
            "di.sensor",
            false,
            SignalWriteOwner.SimulationComponent);
        SignalWriteResult componentOutput = hub.SetDigitalOutput(
            "do.active",
            true,
            SignalWriteOwner.SimulationComponent);
        SignalWriteResult manualInput = hub.SetDigitalInput(
            "di.sensor",
            true,
            SignalWriteOwner.Manual);
        SignalWriteResult sequenceOutput = hub.SetDigitalOutput(
            "do.active",
            true,
            SignalWriteOwner.EmbeddedSequence);

        Assert.True(componentInput.IsAccepted);
        Assert.Equal(SignalHubErrorCode.WriteOwnerNotAllowed, componentOutput.ErrorCode);
        Assert.True(manualInput.IsAccepted);
        Assert.True(sequenceOutput.IsAccepted);
    }

    [Fact]
    public void Reset_RestoresBasePoseAndClearsSensorAndDelayState()
    {
        var hub = CreateHub("di.sensor");
        var runtime = new DeterministicMachineLayout(
            CreateLayout(
                Stage("stage", "axis-x", 10, 100, 4, 4),
                Sensor("sensor", "di.sensor", "stage", 1, 1, 110, 0, 4, 4)),
            hub);

        runtime.Tick(Axes(Axis("axis-x", 20)));
        runtime.Reset();

        IReadOnlyList<LayoutComponentSnapshot> snapshots = runtime.CaptureSnapshots();
        LayoutComponentSnapshot stage = snapshots.Single(component => component.Id == "stage");
        LayoutComponentSnapshot sensor = snapshots.Single(component => component.Id == "sensor");
        Assert.Equal(100, stage.X);
        Assert.False(sensor.IsDetected);
        Assert.Equal(0, sensor.PendingTransitionTicks);
        Assert.False(hub.ReadDigitalSignal("di.sensor").Value);
    }

    [Fact]
    public void Tick_InputOrderDoesNotChangeSnapshotOrTransitionOrder()
    {
        LayoutComponentRuntimeConfiguration[] authored =
        [
            Sensor("sensor-z", "di.z", "stage-m", 0, 0, 0, 0, 4, 4),
            Stage("stage-m", "axis-x", 0, 0, 4, 4),
            new MachineFrameRuntimeConfiguration(
                "frame-a",
                "Frame",
                Transform(50, 50),
                Size(20, 20))
        ];
        var first = new DeterministicMachineLayout(
            CreateLayout(authored),
            CreateHub("di.z"));
        var second = new DeterministicMachineLayout(
            CreateLayout(authored.Reverse().ToArray()),
            CreateHub("di.z"));

        MachineLayoutTickResult firstResult = first.Tick(Axes(Axis("axis-x", 0)));
        MachineLayoutTickResult secondResult = second.Tick(Axes(Axis("axis-x", 0)));

        Assert.Equal(
            new[] { "frame-a", "sensor-z", "stage-m" },
            firstResult.Components.Select(component => component.Id));
        Assert.Equal(firstResult.Components.ToArray(), secondResult.Components.ToArray());
        Assert.Equal(firstResult.Transitions.ToArray(), secondResult.Transitions.ToArray());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LayoutComponentSnapshot>)firstResult.Components).Add(firstResult.Components[0]));
    }

    [Fact]
    public void Configuration_CopiesAndOrdersTheAuthoredComponentCollection()
    {
        var authored = new List<LayoutComponentRuntimeConfiguration>
        {
            Stage("stage-z", "axis-x", 0, 0, 2, 2),
            new MachineFrameRuntimeConfiguration(
                "frame-a",
                "Frame",
                Transform(0, 0),
                Size(2, 2))
        };

        var configuration = new MachineLayoutRuntimeConfiguration("main", "Main", authored);
        authored.Clear();

        Assert.Equal(new[] { "frame-a", "stage-z" }, configuration.Components.Select(component => component.Id));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LayoutComponentRuntimeConfiguration>)configuration.Components).Clear());
    }

    private static LayoutComponentSnapshot SensorSnapshot(MachineLayoutTickResult result) =>
        result.Components.Single(component => component.Kind == LayoutComponentKind.DigitalSensor);

    private static MachineLayoutRuntimeConfiguration CreateLayout(
        params LayoutComponentRuntimeConfiguration[] components) =>
        new("main", "Main", components);

    private static LinearStageRuntimeConfiguration Stage(
        string id,
        string axisId,
        double homePosition,
        double baseX,
        double width,
        double height) =>
        new(id, id, axisId, homePosition, Transform(baseX, 0), Size(width, height));

    private static DigitalSensorRuntimeConfiguration Sensor(
        string id,
        string outputChannelId,
        string targetComponentId,
        int onDelayTicks,
        int offDelayTicks,
        double x,
        double y,
        double width,
        double height) =>
        new(
            id,
            id,
            outputChannelId,
            targetComponentId,
            onDelayTicks,
            offDelayTicks,
            Transform(x, y),
            Size(width, height));

    private static LayoutRuntimeTransform Transform(double x, double y) => new(x, y);

    private static LayoutRuntimeSize Size(double width, double height) => new(width, height);

    private static DeterministicSignalHub CreateHub(string inputId) =>
        DeterministicSignalHub.Create(new[] { Channel(inputId, ChannelKind.DigitalInput) }).Hub!;

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = 0
        };

    private static IReadOnlyDictionary<string, AxisSnapshot> Axes(params AxisSnapshot[] axes) =>
        axes.ToDictionary(axis => axis.Id, StringComparer.Ordinal);

    private static AxisSnapshot Axis(string id, double position) =>
        new(id, id, AxisState.Idle, position, 0);
}
