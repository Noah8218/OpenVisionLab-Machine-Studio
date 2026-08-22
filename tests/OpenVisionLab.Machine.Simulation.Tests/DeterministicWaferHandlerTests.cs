using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicWaferHandlerTests
{
    [Fact]
    public void Tick_ValidPickAndPlace_TransfersOwnershipOnCommandEdges()
    {
        var (hub, layout) = CreateLayout();
        SetInput(hub, "di.source", true);

        SetOutput(hub, "do.pick", true);
        layout.Tick(Axes(0, 260));
        Assert.Equal(WaferHandlerOwnershipState.Handler, Handler(layout).State);
        Assert.Equal(WaferHandlerOwnershipState.Handler, Workpiece(layout).TransferOwnershipState);
        Assert.Equal("handler", Workpiece(layout).TransferOwnerId);
        AssertSignal(hub, "di.holding", true);

        layout.Tick(Axes(0, 260));
        Assert.Equal(WaferHandlerOwnershipState.Handler, Handler(layout).State);

        SetOutput(hub, "do.pick", false);
        SetInput(hub, "di.gate", true);
        SetOutput(hub, "do.place", true);
        layout.Tick(Axes(140, 260));

        Assert.Equal(WaferHandlerOwnershipState.Destination, Handler(layout).State);
        Assert.Equal(WaferHandlerOwnershipState.Destination, Workpiece(layout).TransferOwnershipState);
        AssertSignal(hub, "di.holding", false);
        AssertSignal(hub, "di.placed", true);
    }

    [Fact]
    public void Tick_UnsafeOrSimultaneousRequest_LatchesFailClosedUntilReset()
    {
        var (hub, layout) = CreateLayout();
        SetInput(hub, "di.source", true);
        SetOutput(hub, "do.pick", true);
        SetOutput(hub, "do.place", true);

        layout.Tick(Axes(0, 260));

        Assert.Equal(WaferHandlerOwnershipState.InterlockFault, Handler(layout).State);
        Assert.Equal(WaferHandlerOwnershipState.InterlockFault, Workpiece(layout).TransferOwnershipState);
        AssertSignal(hub, "di.holding", false);
        AssertSignal(hub, "di.placed", false);

        SetOutput(hub, "do.pick", false);
        SetOutput(hub, "do.place", false);
        layout.Tick(Axes(0, 260));
        Assert.Equal(WaferHandlerOwnershipState.InterlockFault, Handler(layout).State);

        layout.Reset();
        Assert.Equal(WaferHandlerOwnershipState.Source, Handler(layout).State);
        Assert.Equal(WaferHandlerOwnershipState.Source, Workpiece(layout).TransferOwnershipState);
    }

    [Fact]
    public void Tick_PlaceBeforePick_LatchesFailClosedFault()
    {
        var (hub, layout) = CreateLayout();
        SetInput(hub, "di.gate", true);
        SetOutput(hub, "do.place", true);

        layout.Tick(Axes(140, 260));

        Assert.Equal(WaferHandlerOwnershipState.InterlockFault, Handler(layout).State);
        AssertSignal(hub, "di.holding", false);
        AssertSignal(hub, "di.placed", false);
    }

    [Fact]
    public void Configuration_TwoHandlersForOneWorkpiece_IsRejected()
    {
        var conveyor = new ConveyorRuntimeConfiguration(
            "transport", "Transport", "do.conveyor.run", "do.conveyor.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(300, 40));
        var wafer = new WorkpieceRuntimeConfiguration(
            "wafer", "Wafer", "300 mm Wafer", "transport", WorkpieceInspectionState.Pending,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(20, 20));
        var first = HandlerConfiguration("handler-1", "1");
        var second = HandlerConfiguration("handler-2", "2");

        var error = Assert.Throws<ArgumentException>(() => new MachineLayoutRuntimeConfiguration(
            "main", "Main", new LayoutComponentRuntimeConfiguration[] { conveyor, wafer },
            waferHandlers: new[] { first, second }));

        Assert.Contains(
            "cannot be controlled by more than one wafer-handler",
            error.Message,
            StringComparison.Ordinal);
    }

    private static (DeterministicSignalHub Hub, DeterministicMachineLayout Layout) CreateLayout()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(new[]
        {
            Channel("do.conveyor.run", ChannelKind.DigitalOutput),
            Channel("do.conveyor.reverse", ChannelKind.DigitalOutput),
            Channel("di.source", ChannelKind.DigitalInput),
            Channel("di.gate", ChannelKind.DigitalInput),
            Channel("do.pick", ChannelKind.DigitalOutput),
            Channel("do.place", ChannelKind.DigitalOutput),
            Channel("di.holding", ChannelKind.DigitalInput),
            Channel("di.placed", ChannelKind.DigitalInput)
        });
        Assert.True(creation.IsAccepted);

        var conveyor = new ConveyorRuntimeConfiguration(
            "transport", "Transport", "do.conveyor.run", "do.conveyor.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(300, 40));
        var wafer = new WorkpieceRuntimeConfiguration(
            "wafer", "Wafer", "300 mm Wafer", "transport", WorkpieceInspectionState.Pending,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(20, 20));
        var handler = new WaferHandlerRuntimeConfiguration(
            "handler", "Handler", "axis.horizontal", "axis.vertical", "wafer",
            "di.source", "di.gate", "do.pick", "do.place", "di.holding", "di.placed",
            0, 260, 140, 260);
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration(
                "main", "Main", new LayoutComponentRuntimeConfiguration[] { conveyor, wafer },
                waferHandlers: new[] { handler }),
            creation.Hub!);
        layout.Reset();
        return (creation.Hub!, layout);
    }

    private static IReadOnlyDictionary<string, AxisSnapshot> Axes(double horizontal, double vertical) =>
        new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal)
        {
            ["axis.horizontal"] = new("axis.horizontal", "Horizontal", AxisState.Idle, horizontal, 0),
            ["axis.vertical"] = new("axis.vertical", "Vertical", AxisState.Idle, vertical, 0)
        };

    private static WaferHandlerSnapshot Handler(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CaptureWaferHandlerSnapshots());

    private static LayoutComponentSnapshot Workpiece(DeterministicMachineLayout layout) =>
        layout.CaptureSnapshots().Single(component => component.Id == "wafer");

    private static WaferHandlerRuntimeConfiguration HandlerConfiguration(string id, string suffix) =>
        new(
            id, id, $"axis.horizontal.{suffix}", $"axis.vertical.{suffix}", "wafer",
            $"di.source.{suffix}", $"di.gate.{suffix}", $"do.pick.{suffix}", $"do.place.{suffix}",
            $"di.holding.{suffix}", $"di.placed.{suffix}", 0, 260, 140, 260);

    private static void SetOutput(DeterministicSignalHub hub, string id, bool value) =>
        Assert.True(hub.SetDigitalOutput(id, value, SignalWriteOwner.EmbeddedSequence).IsAccepted);

    private static void SetInput(DeterministicSignalHub hub, string id, bool value) =>
        Assert.True(hub.SetDigitalInput(id, value, SignalWriteOwner.SimulationComponent).IsAccepted);

    private static void AssertSignal(DeterministicSignalHub hub, string id, bool expected)
    {
        SignalReadResult read = hub.ReadDigitalSignal(id);
        Assert.True(read.IsAccepted);
        Assert.Equal(expected, read.Value);
    }

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = 0 };
}
