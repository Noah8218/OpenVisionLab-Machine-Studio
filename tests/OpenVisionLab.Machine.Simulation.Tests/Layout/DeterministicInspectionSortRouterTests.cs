using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicInspectionSortRouterTests
{
    [Theory]
    [InlineData(PlaceholderInspectionDecision.Pass, "do.pass.run", InspectionSortRouteState.PassRouted, "di.pass-routed")]
    [InlineData(PlaceholderInspectionDecision.Fail, "do.ng.run", InspectionSortRouteState.NgRouted, "di.ng-routed")]
    public void Tick_MatchingRouteEdge_LatchesDecisionAndWritesExclusiveFeedback(
        PlaceholderInspectionDecision decision,
        string commandId,
        InspectionSortRouteState expectedState,
        string expectedFeedbackId)
    {
        var (hub, layout) = CreateLayout();
        Tick(layout, Camera(decision));

        SetOutput(hub, commandId, true);
        Tick(layout, Camera(decision));

        InspectionSortRouterSnapshot sorter = Sorter(layout);
        Assert.Equal(expectedState, sorter.State);
        Assert.Equal(decision, sorter.Decision);
        AssertSignal(hub, expectedFeedbackId, true);
        AssertSignal(hub, expectedFeedbackId == "di.pass-routed" ? "di.ng-routed" : "di.pass-routed", false);
    }

    [Fact]
    public void Tick_WrongRoute_LatchesFailClosedUntilReset()
    {
        var (hub, layout) = CreateLayout();
        Tick(layout, Camera(PlaceholderInspectionDecision.Fail));

        SetOutput(hub, "do.pass.run", true);
        Tick(layout, Camera(PlaceholderInspectionDecision.Fail));
        Assert.Equal(InspectionSortRouteState.InterlockFault, Sorter(layout).State);
        AssertSignal(hub, "di.pass-routed", false);
        AssertSignal(hub, "di.ng-routed", false);

        SetOutput(hub, "do.pass.run", false);
        Tick(layout, Camera(PlaceholderInspectionDecision.Fail));
        Assert.Equal(InspectionSortRouteState.InterlockFault, Sorter(layout).State);

        layout.Reset();
        Assert.Equal(InspectionSortRouteState.AwaitingDecision, Sorter(layout).State);
    }

    [Fact]
    public void Tick_SimultaneousRoutes_LatchesFailClosedFault()
    {
        var (hub, layout) = CreateLayout();
        Tick(layout, Camera(PlaceholderInspectionDecision.Pass));
        SetOutput(hub, "do.pass.run", true);
        SetOutput(hub, "do.ng.run", true);

        Tick(layout, Camera(PlaceholderInspectionDecision.Pass));

        Assert.Equal(InspectionSortRouteState.InterlockFault, Sorter(layout).State);
        AssertSignal(hub, "di.pass-routed", false);
        AssertSignal(hub, "di.ng-routed", false);
    }

    [Fact]
    public void Tick_AlternateRouteAfterSelection_LatchesFailClosedFault()
    {
        var (hub, layout) = CreateLayout();
        Tick(layout, Camera(PlaceholderInspectionDecision.Pass));
        SetOutput(hub, "do.pass.run", true);
        Tick(layout, Camera(PlaceholderInspectionDecision.Pass));
        SetOutput(hub, "do.pass.run", false);
        SetOutput(hub, "do.ng.run", true);

        Tick(layout, Camera(PlaceholderInspectionDecision.Pass));

        Assert.Equal(InspectionSortRouteState.InterlockFault, Sorter(layout).State);
        AssertSignal(hub, "di.pass-routed", false);
        AssertSignal(hub, "di.ng-routed", false);
    }

    [Fact]
    public void Configuration_MismatchedConveyorRunChannel_IsRejected()
    {
        var pass = new ConveyorRuntimeConfiguration(
            "pass", "Pass", "do.pass.run", "do.pass.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(300, 40));
        var ng = new ConveyorRuntimeConfiguration(
            "ng", "NG", "do.ng.run", "do.ng.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 60), new LayoutRuntimeSize(300, 40));
        var sorter = new InspectionSortRouterRuntimeConfiguration(
            "sorter", "Sorter", "camera", "pass", "ng",
            "do.ng.run", "do.pass.run", "di.pass-routed", "di.ng-routed");

        var error = Assert.Throws<ArgumentException>(() =>
            new MachineLayoutRuntimeConfiguration(
                "main", "Main", new LayoutComponentRuntimeConfiguration[] { pass, ng },
                inspectionSortRouters: new[] { sorter }));

        Assert.Contains("must match", error.Message, StringComparison.Ordinal);
    }

    private static (DeterministicSignalHub Hub, DeterministicMachineLayout Layout) CreateLayout()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(new[]
        {
            Channel("do.pass.run", ChannelKind.DigitalOutput),
            Channel("do.pass.reverse", ChannelKind.DigitalOutput),
            Channel("do.ng.run", ChannelKind.DigitalOutput),
            Channel("do.ng.reverse", ChannelKind.DigitalOutput),
            Channel("di.pass-routed", ChannelKind.DigitalInput),
            Channel("di.ng-routed", ChannelKind.DigitalInput)
        });
        Assert.True(creation.IsAccepted);

        var pass = new ConveyorRuntimeConfiguration(
            "pass", "Pass", "do.pass.run", "do.pass.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 0), new LayoutRuntimeSize(300, 40));
        var ng = new ConveyorRuntimeConfiguration(
            "ng", "NG", "do.ng.run", "do.ng.reverse", 100, 0.005,
            new LayoutRuntimeTransform(0, 60), new LayoutRuntimeSize(300, 40));
        var sorter = new InspectionSortRouterRuntimeConfiguration(
            "sorter", "Sorter", "camera", "pass", "ng",
            "do.pass.run", "do.ng.run", "di.pass-routed", "di.ng-routed");
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration(
                "main", "Main", new LayoutComponentRuntimeConfiguration[] { pass, ng },
                inspectionSortRouters: new[] { sorter }),
            creation.Hub!);
        layout.Reset();
        return (creation.Hub!, layout);
    }

    private static void Tick(
        DeterministicMachineLayout layout,
        VirtualCameraSnapshot camera) =>
        layout.Tick(
            new Dictionary<string, AxisSnapshot>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, VirtualCameraSnapshot>(StringComparer.Ordinal) { [camera.Id] = camera });

    private static VirtualCameraSnapshot Camera(PlaceholderInspectionDecision decision) =>
        new(
            "camera",
            "Camera",
            VirtualCameraState.Idle,
            1,
            null,
            null,
            0,
            0,
            new VirtualCameraAcquisitionResult("camera/frame/00000001", "camera", "recipe", 1, decision));

    private static InspectionSortRouterSnapshot Sorter(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CaptureInspectionSortRouterSnapshots());

    private static void SetOutput(DeterministicSignalHub hub, string id, bool value) =>
        Assert.True(hub.SetDigitalOutput(id, value, SignalWriteOwner.EmbeddedSequence).IsAccepted);

    private static void AssertSignal(DeterministicSignalHub hub, string id, bool expected)
    {
        SignalReadResult read = hub.ReadDigitalSignal(id);
        Assert.True(read.IsAccepted);
        Assert.Equal(expected, read.Value);
    }

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = 0 };
}
