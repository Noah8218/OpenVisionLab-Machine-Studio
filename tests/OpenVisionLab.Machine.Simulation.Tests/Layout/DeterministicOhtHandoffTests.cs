using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicOhtHandoffTests
{
    [Fact]
    public void Tick_ReadyTransferAndReceipt_MovesOwnershipToLoadPort()
    {
        var (hub, layout) = CreateLayout();
        SetConditions(hub, routeAvailable: true, vehicleDocked: true, loadPortReady: true);
        Tick(layout);

        Assert.Equal(OhtHandoffOwnershipState.Ready, Handoff(layout).State);
        AssertSignal(hub, "di.handoff-ready", true);

        SetOutput(hub, "do.transport.run", true);
        Tick(layout);
        Assert.Equal(OhtHandoffOwnershipState.Transferring, Handoff(layout).State);
        Assert.True(Transport(layout).ConveyorRunning);

        SetInput(hub, "di.carrier-received", true);
        Tick(layout);
        Assert.Equal(OhtHandoffOwnershipState.LoadPort, Handoff(layout).State);
        AssertSignal(hub, "di.carrier-transferred", true);

        SetOutput(hub, "do.transport.run", false);
        Tick(layout);
        Assert.Equal(OhtHandoffOwnershipState.LoadPort, Handoff(layout).State);
        Assert.False(Transport(layout).ConveyorRunning);
    }

    [Fact]
    public void Tick_TransferBeforeReady_LatchesFaultAndBlocksConveyorUntilReset()
    {
        var (hub, layout) = CreateLayout();
        SetOutput(hub, "do.transport.run", true);

        Tick(layout);

        Assert.Equal(OhtHandoffOwnershipState.InterlockFault, Handoff(layout).State);
        Assert.False(Transport(layout).ConveyorRunning);
        AssertSignal(hub, "di.handoff-ready", false);
        AssertSignal(hub, "di.carrier-transferred", false);

        SetOutput(hub, "do.transport.run", false);
        layout.Reset();
        Assert.Equal(OhtHandoffOwnershipState.Vehicle, Handoff(layout).State);
    }

    [Fact]
    public void Tick_ReverseOrSimultaneousCommand_LatchesFailClosedFault()
    {
        var (hub, layout) = CreateLayout();
        SetConditions(hub, routeAvailable: true, vehicleDocked: true, loadPortReady: true);
        Tick(layout);
        SetOutput(hub, "do.transport.run", true);
        SetOutput(hub, "do.transport.reverse", true);

        Tick(layout);

        Assert.Equal(OhtHandoffOwnershipState.InterlockFault, Handoff(layout).State);
        Assert.False(Transport(layout).ConveyorRunning);
    }

    [Theory]
    [InlineData("do.transport.run", false)]
    [InlineData("di.route-available", false)]
    [InlineData("di.vehicle-docked", false)]
    [InlineData("di.load-port-ready", false)]
    public void Tick_InterruptedTransfer_LatchesFailClosedFault(string channelId, bool value)
    {
        var (hub, layout) = CreateLayout();
        SetConditions(hub, routeAvailable: true, vehicleDocked: true, loadPortReady: true);
        Tick(layout);
        SetOutput(hub, "do.transport.run", true);
        Tick(layout);

        if (channelId.StartsWith("do.", StringComparison.Ordinal))
        {
            SetOutput(hub, channelId, value);
        }
        else
        {
            SetInput(hub, channelId, value);
        }
        Tick(layout);

        Assert.Equal(OhtHandoffOwnershipState.InterlockFault, Handoff(layout).State);
        Assert.False(Transport(layout).ConveyorRunning);
    }

    [Fact]
    public void Tick_ForwardTransportAfterReceipt_RetainsLoadPortOwnership()
    {
        var (hub, layout) = CreateLayout();
        SetConditions(hub, routeAvailable: true, vehicleDocked: true, loadPortReady: true);
        Tick(layout);
        SetOutput(hub, "do.transport.run", true);
        Tick(layout);
        SetInput(hub, "di.carrier-received", true);
        Tick(layout);
        SetOutput(hub, "do.transport.run", false);
        Tick(layout);

        SetOutput(hub, "do.transport.run", true);
        Tick(layout);

        Assert.Equal(OhtHandoffOwnershipState.LoadPort, Handoff(layout).State);
        Assert.True(Transport(layout).ConveyorRunning);
        AssertSignal(hub, "di.carrier-transferred", true);
    }

    private static (DeterministicSignalHub Hub, DeterministicMachineLayout Layout) CreateLayout()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(new[]
        {
            Channel("do.transport.run", ChannelKind.DigitalOutput),
            Channel("do.transport.reverse", ChannelKind.DigitalOutput),
            Channel("di.route-available", ChannelKind.DigitalInput),
            Channel("di.vehicle-docked", ChannelKind.DigitalInput),
            Channel("di.load-port-ready", ChannelKind.DigitalInput),
            Channel("di.carrier-received", ChannelKind.DigitalInput),
            Channel("di.handoff-ready", ChannelKind.DigitalInput),
            Channel("di.carrier-transferred", ChannelKind.DigitalInput)
        });
        Assert.True(creation.IsAccepted);

        var transport = new ConveyorRuntimeConfiguration(
            "transport", "Transport", "do.transport.run", "do.transport.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(300, 40));
        var handoff = new OhtHandoffRuntimeConfiguration(
            "oht", "OHT Handoff", "transport", "do.transport.run", "do.transport.reverse",
            "di.route-available", "di.vehicle-docked", "di.load-port-ready", "di.carrier-received",
            "di.handoff-ready", "di.carrier-transferred");
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration(
                "main", "Main", new LayoutComponentRuntimeConfiguration[] { transport },
                ohtHandoffs: new[] { handoff }),
            creation.Hub!);
        layout.Reset();
        return (creation.Hub!, layout);
    }

    private static void SetConditions(
        DeterministicSignalHub hub,
        bool routeAvailable,
        bool vehicleDocked,
        bool loadPortReady)
    {
        SetInput(hub, "di.route-available", routeAvailable);
        SetInput(hub, "di.vehicle-docked", vehicleDocked);
        SetInput(hub, "di.load-port-ready", loadPortReady);
    }

    private static void Tick(DeterministicMachineLayout layout) =>
        layout.Tick(new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal));

    private static OhtHandoffSnapshot Handoff(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CaptureOhtHandoffSnapshots());

    private static LayoutComponentSnapshot Transport(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CaptureSnapshots(), component => component.Id == "transport");

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
