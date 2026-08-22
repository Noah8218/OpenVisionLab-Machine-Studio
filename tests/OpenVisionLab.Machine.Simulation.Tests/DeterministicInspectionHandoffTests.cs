using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicInspectionHandoffTests
{
    [Fact]
    public void ReadyTriggerResultAndAcceptance_CompleteOneCorrelatedHandoff()
    {
        var (hub, layout) = CreateLayout();

        SetInput(hub, "di.position", true);
        Tick(layout, Camera(VirtualCameraState.Idle));
        Assert.Equal(InspectionHandoffState.Ready, Handoff(layout).State);
        AssertSignal(hub, "di.ready", true);

        Tick(layout, Camera(VirtualCameraState.Exposing, ordinal: 1));
        Assert.Equal(InspectionHandoffState.Inspecting, Handoff(layout).State);

        Tick(layout, Camera(
            VirtualCameraState.FrameReady,
            ordinal: 1,
            decision: PlaceholderInspectionDecision.Pass));
        InspectionHandoffSnapshot result = Handoff(layout);
        Assert.Equal(InspectionHandoffState.ResultAvailable, result.State);
        Assert.Equal(PlaceholderInspectionDecision.Pass, result.Decision);

        SetOutput(hub, "do.accept", true);
        Tick(layout, Camera(
            VirtualCameraState.FrameReady,
            ordinal: 1,
            decision: PlaceholderInspectionDecision.Pass));
        InspectionHandoffSnapshot completed = Handoff(layout);
        Assert.Equal(InspectionHandoffState.Complete, completed.State);
        Assert.True(completed.IsMaterialPresent);
        Assert.True(completed.IsResultAccepted);
        AssertSignal(hub, "di.ready", false);
        AssertSignal(hub, "di.complete", true);
    }

    [Fact]
    public void PrematureAcceptanceAndAcquisitionWithoutMaterial_LatchFaultUntilReset()
    {
        var (hub, layout) = CreateLayout();

        SetOutput(hub, "do.accept", true);
        Tick(layout, Camera(VirtualCameraState.Idle));
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        Tick(layout, Camera(VirtualCameraState.Exposing, ordinal: 1));
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        Tick(layout, Camera(VirtualCameraState.Idle));
        Assert.Equal(InspectionHandoffState.AwaitingMaterial, Handoff(layout).State);
    }

    [Fact]
    public void MaterialLossCameraFaultAndSecondAcquisition_FailClosed()
    {
        var (hub, layout) = CreateLayout();
        SetInput(hub, "di.position", true);
        Tick(layout, Camera(VirtualCameraState.Idle));
        Tick(layout, Camera(VirtualCameraState.Exposing, ordinal: 1));

        SetInput(hub, "di.position", false);
        Tick(layout, Camera(VirtualCameraState.Transferring, ordinal: 1));
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        SetInput(hub, "di.position", true);
        Tick(layout, Camera(VirtualCameraState.Idle));
        Tick(layout, Camera(VirtualCameraState.Faulted));
        AssertFaulted(hub, layout);

        hub.Reset();
        layout.Reset();
        CompleteHandoff(hub, layout);
        Tick(layout, Camera(VirtualCameraState.Exposing, ordinal: 2));
        AssertFaulted(hub, layout);
    }

    private static void CompleteHandoff(DeterministicSignalHub hub, DeterministicMachineLayout layout)
    {
        SetInput(hub, "di.position", true);
        Tick(layout, Camera(VirtualCameraState.Idle));
        Tick(layout, Camera(VirtualCameraState.Exposing, ordinal: 1));
        Tick(layout, Camera(
            VirtualCameraState.FrameReady,
            ordinal: 1,
            decision: PlaceholderInspectionDecision.Fail));
        SetOutput(hub, "do.accept", true);
        Tick(layout, Camera(
            VirtualCameraState.FrameReady,
            ordinal: 1,
            decision: PlaceholderInspectionDecision.Fail));
        Assert.Equal(InspectionHandoffState.Complete, Handoff(layout).State);
    }

    private static (DeterministicSignalHub Hub, DeterministicMachineLayout Layout) CreateLayout()
    {
        SignalHubCreationResult creation = DeterministicSignalHub.Create(new[]
        {
            Channel("di.position", ChannelKind.DigitalInput),
            Channel("do.accept", ChannelKind.DigitalOutput),
            Channel("di.ready", ChannelKind.DigitalInput),
            Channel("di.complete", ChannelKind.DigitalInput)
        });
        Assert.True(creation.IsAccepted);

        var handoff = new InspectionHandoffRuntimeConfiguration(
            "inspection", "Inspection", "camera", "di.position", "do.accept", "di.ready", "di.complete");
        var layout = new DeterministicMachineLayout(
            new MachineLayoutRuntimeConfiguration(
                "main",
                "Main",
                Array.Empty<LayoutComponentRuntimeConfiguration>(),
                inspectionHandoffs: new[] { handoff }),
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

    private static VirtualCameraSnapshot Camera(
        VirtualCameraState state,
        long ordinal = 0,
        PlaceholderInspectionDecision? decision = null) =>
        new(
            "camera",
            "Camera",
            state,
            ordinal,
            ordinal == 0 ? null : $"camera/frame/{ordinal:D8}",
            ordinal == 0 ? null : "recipe",
            0,
            0,
            decision is null
                ? null
                : new VirtualCameraAcquisitionResult(
                    $"camera/frame/{ordinal:D8}",
                    "camera",
                    "recipe",
                    ordinal,
                    decision.Value));

    private static InspectionHandoffSnapshot Handoff(DeterministicMachineLayout layout) =>
        Assert.Single(layout.CaptureInspectionHandoffSnapshots());

    private static void AssertFaulted(DeterministicSignalHub hub, DeterministicMachineLayout layout)
    {
        Assert.Equal(InspectionHandoffState.InterlockFault, Handoff(layout).State);
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

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = 0 };
}
