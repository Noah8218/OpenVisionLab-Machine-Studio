using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicLoadLockTests
{
    [Fact]
    public void Tick_NormalCycle_TransitionsPressureAndNeverOpensBothDoors()
    {
        var (hub, layout) = CreateLayout();

        SetOutput(hub, "do.outer", true);
        Tick(layout, 2);
        Assert.Equal(PneumaticCylinderState.Extended, Cylinder(layout, "outer").CylinderState);
        Assert.Equal(PneumaticCylinderState.Retracted, Cylinder(layout, "inner").CylinderState);

        SetOutput(hub, "do.outer", false);
        Tick(layout, 2);
        SetOutput(hub, "do.evacuate", true);
        Tick(layout, 3);
        Assert.Equal(LoadLockState.Vacuum, LoadLock(layout).State);
        Assert.False(LoadLock(layout).IsOuterDoorPermitted);
        Assert.True(LoadLock(layout).IsInnerDoorPermitted);
        AssertSignal(hub, "di.vacuum", true);

        SetOutput(hub, "do.inner", true);
        Tick(layout, 2);
        Assert.Equal(PneumaticCylinderState.Extended, Cylinder(layout, "inner").CylinderState);
        Assert.Equal(PneumaticCylinderState.Retracted, Cylinder(layout, "outer").CylinderState);

        SetOutput(hub, "do.inner", false);
        Tick(layout, 2);
        SetOutput(hub, "do.evacuate", false);
        SetOutput(hub, "do.vent", true);
        Tick(layout, 3);
        Assert.Equal(LoadLockState.Atmosphere, LoadLock(layout).State);
        Assert.True(LoadLock(layout).IsOuterDoorPermitted);
        Assert.False(LoadLock(layout).IsInnerDoorPermitted);
        AssertSignal(hub, "di.atmosphere", true);
    }

    [Fact]
    public void Tick_SimultaneousDoorRequest_LatchesFaultUntilResetAndForcesRetraction()
    {
        var (hub, layout) = CreateLayout();
        SetOutput(hub, "do.outer", true);
        SetOutput(hub, "do.inner", true);

        layout.Tick(EmptyAxes());

        Assert.Equal(LoadLockState.InterlockFault, LoadLock(layout).State);
        Assert.False(LoadLock(layout).IsOuterDoorPermitted);
        Assert.False(LoadLock(layout).IsInnerDoorPermitted);
        Assert.All(
            layout.CaptureSnapshots().Where(item => item.CylinderState.HasValue),
            item => Assert.Equal(PneumaticCylinderState.Retracted, item.CylinderState));
        AssertSignal(hub, "di.vacuum", false);
        AssertSignal(hub, "di.atmosphere", false);

        SetOutput(hub, "do.outer", false);
        SetOutput(hub, "do.inner", false);
        layout.Tick(EmptyAxes());
        Assert.Equal(LoadLockState.InterlockFault, LoadLock(layout).State);

        layout.Reset();
        Assert.Equal(LoadLockState.Atmosphere, LoadLock(layout).State);
        AssertSignal(hub, "di.atmosphere", true);
    }

    private static (DeterministicSignalHub Hub, DeterministicMachineLayout Layout) CreateLayout()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(new[]
        {
            Channel("do.outer", ChannelKind.DigitalOutput, 0),
            Channel("do.inner", ChannelKind.DigitalOutput, 0),
            Channel("do.evacuate", ChannelKind.DigitalOutput, 0),
            Channel("do.vent", ChannelKind.DigitalOutput, 0),
            Channel("di.outer.extended", ChannelKind.DigitalInput, 0),
            Channel("di.outer.retracted", ChannelKind.DigitalInput, 1),
            Channel("di.inner.extended", ChannelKind.DigitalInput, 0),
            Channel("di.inner.retracted", ChannelKind.DigitalInput, 1),
            Channel("di.vacuum", ChannelKind.DigitalInput, 0),
            Channel("di.atmosphere", ChannelKind.DigitalInput, 1)
        });
        Assert.True(creation.IsAccepted);

        var components = new[]
        {
            CylinderConfiguration("outer", "do.outer", "di.outer.extended", "di.outer.retracted"),
            CylinderConfiguration("inner", "do.inner", "di.inner.extended", "di.inner.retracted")
        };
        var loadLock = new LoadLockRuntimeConfiguration(
            "load-lock",
            "Load Lock",
            "outer",
            "inner",
            "do.evacuate",
            "do.vent",
            "di.vacuum",
            "di.atmosphere",
            2,
            2);
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration("main", "Main", components, new[] { loadLock }),
            creation.Hub!);
        layout.Reset();
        return (creation.Hub!, layout);
    }

    private static PneumaticCylinderRuntimeConfiguration CylinderConfiguration(
        string id,
        string command,
        string extended,
        string retracted) =>
        new(
            id,
            id,
            command,
            extended,
            retracted,
            2,
            2,
            0,
            0,
            10,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(10, 10));

    private static void Tick(DeterministicMachineLayout layout, int count)
    {
        for (int i = 0; i < count; i++)
        {
            layout.Tick(EmptyAxes());
        }
    }

    private static LayoutComponentSnapshot Cylinder(DeterministicMachineLayout layout, string id) =>
        Assert.Single(layout.CaptureSnapshots(), item => item.Id == id);

    private static LoadLockSnapshot LoadLock(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CaptureLoadLockSnapshots());

    private static void SetOutput(DeterministicSignalHub hub, string id, bool value) =>
        Assert.True(hub.SetDigitalOutput(id, value, SignalWriteOwner.EmbeddedSequence).IsAccepted);

    private static void AssertSignal(DeterministicSignalHub hub, string id, bool expected)
    {
        SignalReadResult read = hub.ReadDigitalSignal(id);
        Assert.True(read.IsAccepted);
        Assert.Equal(expected, read.Value);
    }

    private static ChannelDefinition Channel(string id, ChannelKind kind, double initialValue) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = initialValue };

    private static IReadOnlyDictionary<string, AxisSnapshot> EmptyAxes() =>
        new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal);
}
