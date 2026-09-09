using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicPneumaticCylinderTests
{
    [Fact]
    public void Tick_ExtendAndRetract_PublishesDelayedEndFeedbackDeterministically()
    {
        var hub = CreateHub();
        var layout = CreateLayout(hub, extendTicks: 3, retractTicks: 2, sensorDelayTicks: 2);

        AssertCylinder(layout.CaptureSnapshots(), PneumaticCylinderState.Retracted, 0);
        AssertSignal(hub, "di.cylinder.retracted", true);
        AssertSignal(hub, "di.cylinder.extended", false);

        Assert.True(hub.SetDigitalOutput(
            "do.cylinder.extend",
            true,
            SignalWriteOwner.EmbeddedSequence).IsAccepted);
        MachineLayoutTickResult extend1 = layout.Tick(EmptyAxes());
        AssertCylinder(extend1.Components, PneumaticCylinderState.Extending, 1d / 3d);
        Assert.Single(extend1.CylinderStateTransitions);
        Assert.Single(extend1.CylinderFeedbackTransitions);
        AssertSignal(hub, "di.cylinder.retracted", false);

        layout.Tick(EmptyAxes());
        MachineLayoutTickResult atExtended = layout.Tick(EmptyAxes());
        AssertCylinder(atExtended.Components, PneumaticCylinderState.Extended, 1);
        AssertSignal(hub, "di.cylinder.extended", false);
        MachineLayoutTickResult extendedFeedback = layout.Tick(EmptyAxes());
        AssertSignal(hub, "di.cylinder.extended", true);
        Assert.Single(extendedFeedback.CylinderFeedbackTransitions);

        Assert.True(hub.SetDigitalOutput(
            "do.cylinder.extend",
            false,
            SignalWriteOwner.EmbeddedSequence).IsAccepted);
        MachineLayoutTickResult retract1 = layout.Tick(EmptyAxes());
        AssertCylinder(retract1.Components, PneumaticCylinderState.Retracting, 0.5);
        AssertSignal(hub, "di.cylinder.extended", false);
        layout.Tick(EmptyAxes());
        AssertSignal(hub, "di.cylinder.retracted", false);
        MachineLayoutTickResult retractedFeedback = layout.Tick(EmptyAxes());
        AssertCylinder(retractedFeedback.Components, PneumaticCylinderState.Retracted, 0);
        AssertSignal(hub, "di.cylinder.retracted", true);
        Assert.Single(retractedFeedback.CylinderFeedbackTransitions);
    }

    [Fact]
    public void Tick_CommandReversalMidStroke_UsesContinuousBoundedProgress()
    {
        var hub = CreateHub();
        var layout = CreateLayout(hub, extendTicks: 4, retractTicks: 4, sensorDelayTicks: 0);
        hub.SetDigitalOutput("do.cylinder.extend", true, SignalWriteOwner.EmbeddedSequence);

        layout.Tick(EmptyAxes());
        MachineLayoutTickResult halfway = layout.Tick(EmptyAxes());
        AssertCylinder(halfway.Components, PneumaticCylinderState.Extending, 0.5);

        hub.SetDigitalOutput("do.cylinder.extend", false, SignalWriteOwner.EmbeddedSequence);
        MachineLayoutTickResult reversed = layout.Tick(EmptyAxes());
        AssertCylinder(reversed.Components, PneumaticCylinderState.Retracting, 0.25);
        MachineLayoutTickResult home = layout.Tick(EmptyAxes());
        AssertCylinder(home.Components, PneumaticCylinderState.Retracted, 0);
    }

    [Fact]
    public void Constructor_FeedbackBoundToDigitalOutput_IsRejected()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(
            new[]
            {
                Channel("do.cylinder.extend", ChannelKind.DigitalOutput, 0),
                Channel("di.cylinder.extended", ChannelKind.DigitalOutput, 0),
                Channel("di.cylinder.retracted", ChannelKind.DigitalInput, 1)
            });
        Assert.True(creation.IsAccepted);

        Assert.Throws<ArgumentException>(() => CreateLayout(creation.Hub!, 2, 2, 0));
    }

    [Fact]
    public void Tick_TravelBlocked_FreezesProgressAndResumesFromSamePosition()
    {
        var hub = CreateHub();
        var layout = CreateLayout(hub, extendTicks: 4, retractTicks: 4, sensorDelayTicks: 0);
        hub.SetDigitalOutput("do.cylinder.extend", true, SignalWriteOwner.EmbeddedSequence);
        layout.Tick(EmptyAxes());

        var blocked = new HashSet<string>(StringComparer.Ordinal) { "cylinder-1" };
        MachineLayoutTickResult faulted = layout.Tick(EmptyAxes(), blocked);
        MachineLayoutTickResult stillFaulted = layout.Tick(EmptyAxes(), blocked);
        MachineLayoutTickResult resumed = layout.Tick(
            EmptyAxes(),
            new HashSet<string>(StringComparer.Ordinal));

        AssertCylinder(faulted.Components, PneumaticCylinderState.Fault, 0.25);
        AssertCylinder(stillFaulted.Components, PneumaticCylinderState.Fault, 0.25);
        AssertCylinder(resumed.Components, PneumaticCylinderState.Extending, 0.5);
        Assert.Single(faulted.CylinderStateTransitions);
        Assert.Empty(stillFaulted.CylinderStateTransitions);
        Assert.Single(resumed.CylinderStateTransitions);
    }

    private static DeterministicSignalHub CreateHub()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(
            new[]
            {
                Channel("do.cylinder.extend", ChannelKind.DigitalOutput, 0),
                Channel("di.cylinder.extended", ChannelKind.DigitalInput, 0),
                Channel("di.cylinder.retracted", ChannelKind.DigitalInput, 1)
            });
        Assert.True(creation.IsAccepted);
        return creation.Hub!;
    }

    private static DeterministicMachineLayout CreateLayout(
        DeterministicSignalHub hub,
        int extendTicks,
        int retractTicks,
        int sensorDelayTicks)
    {
        var cylinder = new PneumaticCylinderRuntimeConfiguration(
            "cylinder-1",
            "Cylinder 1",
            "do.cylinder.extend",
            "di.cylinder.extended",
            "di.cylinder.retracted",
            extendTicks,
            retractTicks,
            sensorDelayTicks,
            sensorDelayTicks,
            80,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(100, 40));
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration("main", "Main", new[] { cylinder }),
            hub);
        layout.Reset();
        return layout;
    }

    private static ChannelDefinition Channel(string id, ChannelKind kind, double initialValue) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = initialValue
        };

    private static IReadOnlyDictionary<string, AxisSnapshot> EmptyAxes() =>
        new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal);

    private static void AssertCylinder(
        IEnumerable<LayoutComponentSnapshot> snapshots,
        PneumaticCylinderState expectedState,
        double expectedProgress)
    {
        LayoutComponentSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(expectedState, snapshot.CylinderState);
        Assert.Equal(expectedProgress, snapshot.MotionProgress!.Value, precision: 10);
    }

    private static void AssertSignal(DeterministicSignalHub hub, string id, bool expected)
    {
        SignalReadResult result = hub.ReadDigitalSignal(id);
        Assert.True(result.IsAccepted);
        Assert.Equal(expected, result.Value);
    }
}
