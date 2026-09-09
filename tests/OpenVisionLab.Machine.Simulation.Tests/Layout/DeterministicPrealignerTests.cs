using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicPrealignerTests
{
    [Fact]
    public void ClampedRotationAndTargetAcceptance_AlignAndReleaseWafer()
    {
        var (hub, layout) = CreateLayout();

        SetInput(hub, "di.wafer", true);
        Tick(layout, AxisState.Idle, 0);
        Assert.Equal(PrealignerState.AwaitingClamp, Prealigner(layout).State);

        SetOutput(hub, "do.clamp", true);
        Tick(layout, AxisState.Idle, 0);
        Assert.Equal(PrealignerState.Ready, Prealigner(layout).State);
        AssertSignal(hub, "di.ready", true);

        Tick(layout, AxisState.Moving, 20);
        Assert.Equal(PrealignerState.Aligning, Prealigner(layout).State);
        Tick(layout, AxisState.Idle, 180);
        Assert.Equal(PrealignerState.Aligning, Prealigner(layout).State);

        SetOutput(hub, "do.accept", true);
        Tick(layout, AxisState.Idle, 180);
        Assert.Equal(PrealignerState.Aligned, Prealigner(layout).State);
        AssertSignal(hub, "di.complete", true);

        SetOutput(hub, "do.clamp", false);
        Tick(layout, AxisState.Idle, 180);
        Assert.Equal(PrealignerState.Released, Prealigner(layout).State);
        AssertSignal(hub, "di.complete", true);
    }

    [Fact]
    public void EarlyAcceptanceAndUnclampedRotation_LatchFaultUntilReset()
    {
        var (hub, layout) = CreateLayout();

        SetOutput(hub, "do.accept", true);
        Tick(layout, AxisState.Idle, 0);
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        Tick(layout, AxisState.Moving, 10);
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        Tick(layout, AxisState.Idle, 0);
        Assert.Equal(PrealignerState.AwaitingWafer, Prealigner(layout).State);
    }

    [Fact]
    public void WaferLossClampReleaseAndSecondRotation_FailClosed()
    {
        var (hub, layout) = CreateLayout();
        PrepareAligning(hub, layout);
        SetInput(hub, "di.wafer", false);
        Tick(layout, AxisState.Moving, 60);
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        PrepareAligning(hub, layout);
        SetOutput(hub, "do.clamp", false);
        Tick(layout, AxisState.Moving, 60);
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        CompleteAlignment(hub, layout, 180);
        Tick(layout, AxisState.Moving, 181);
        AssertFaulted(hub, layout);
    }

    [Fact]
    public void TargetTolerance_AcceptsInsideAndRejectsOutside()
    {
        var (hub, layout) = CreateLayout();
        CompleteAlignment(hub, layout, 180.09);
        Assert.Equal(PrealignerState.Aligned, Prealigner(layout).State);

        hub.Reset();
        layout.Reset();
        PrepareAligning(hub, layout);
        Tick(layout, AxisState.Idle, 180.11);
        AssertFaulted(hub, layout);
    }

    private static void PrepareAligning(DeterministicSignalHub hub, DeterministicMachineLayout layout)
    {
        SetInput(hub, "di.wafer", true);
        Tick(layout, AxisState.Idle, 0);
        SetOutput(hub, "do.clamp", true);
        Tick(layout, AxisState.Idle, 0);
        Tick(layout, AxisState.Moving, 20);
        Assert.Equal(PrealignerState.Aligning, Prealigner(layout).State);
    }

    private static void CompleteAlignment(
        DeterministicSignalHub hub,
        DeterministicMachineLayout layout,
        double position)
    {
        PrepareAligning(hub, layout);
        Tick(layout, AxisState.Idle, position);
        SetOutput(hub, "do.accept", true);
        Tick(layout, AxisState.Idle, position);
    }

    private static (DeterministicSignalHub Hub, DeterministicMachineLayout Layout) CreateLayout()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(new[]
        {
            Channel("di.wafer", ChannelKind.DigitalInput),
            Channel("do.accept", ChannelKind.DigitalOutput),
            Channel("di.ready", ChannelKind.DigitalInput),
            Channel("di.complete", ChannelKind.DigitalInput),
            Channel("do.clamp", ChannelKind.DigitalOutput),
            Channel("di.clamp-extended", ChannelKind.DigitalInput),
            Channel("di.clamp-retracted", ChannelKind.DigitalInput, initialValue: 1)
        });
        Assert.True(creation.IsAccepted);

        var transform = new LayoutRuntimeTransform(0, 0);
        var size = new LayoutRuntimeSize(10, 10);
        LayoutComponentRuntimeConfiguration[] components =
        {
            new RotaryStageRuntimeConfiguration("stage", "Stage", "axis.r", 0, transform, size),
            new PneumaticCylinderRuntimeConfiguration(
                "clamp", "Clamp", "do.clamp", "di.clamp-extended", "di.clamp-retracted",
                1, 1, 0, 0, 1, transform, size)
        };
        var prealigner = new PrealignerRuntimeConfiguration(
            "prealigner", "Pre-aligner", "stage", "axis.r", "clamp",
            "di.wafer", "do.accept", "di.ready", "di.complete", 180, 0.1);
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration(
                "main", "Main", components, prealigners: new[] { prealigner }),
            creation.Hub!);
        layout.Reset();
        return (creation.Hub!, layout);
    }

    private static void Tick(DeterministicMachineLayout layout, AxisState state, double position) =>
        layout.Tick(new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal)
        {
            ["axis.r"] = new("axis.r", "Rotary", state, position, state == AxisState.Moving ? 10 : 0)
        });

    private static PrealignerSnapshot Prealigner(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CapturePrealignerSnapshots());

    private static void AssertFaulted(DeterministicSignalHub hub, DeterministicMachineLayout layout)
    {
        Assert.Equal(PrealignerState.InterlockFault, Prealigner(layout).State);
        AssertSignal(hub, "di.ready", false);
        AssertSignal(hub, "di.complete", false);
    }

    private static void SetInput(DeterministicSignalHub hub, string id, bool value) =>
        Assert.True(hub.SetDigitalInput(id, value, SignalWriteOwner.SimulationComponent).IsAccepted);

    private static void SetOutput(DeterministicSignalHub hub, string id, bool value) =>
        Assert.True(hub.SetDigitalOutput(id, value, SignalWriteOwner.EmbeddedSequence).IsAccepted);

    private static void AssertSignal(DeterministicSignalHub hub, string id, bool expected)
    {
        SignalReadResult read = hub.ReadDigitalSignal(id);
        Assert.True(read.IsAccepted);
        Assert.Equal(expected, read.Value);
    }

    private static ChannelDefinition Channel(string id, ChannelKind kind, int initialValue = 0) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = initialValue };
}
