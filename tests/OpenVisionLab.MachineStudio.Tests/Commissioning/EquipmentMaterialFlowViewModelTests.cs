using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class EquipmentMaterialFlowViewModelTests
{
    [Fact]
    public void CardsFollowPresenceAndCurrentInstanceResultsThroughFailureAndRecovery()
    {
        var project = CreateProject();
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var serialized = new ProjectDocumentStore().Serialize(project);
        var flow = new EquipmentMaterialFlowViewModel();
        flow.Update(project, layout.Items, Snapshot());
        Assert.Equal(3, flow.Slots.Count);
        Assert.All(flow.Slots, slot => Assert.Equal(T("Equipment.NoRuntimeData"), slot.StateText));

        flow.Update(project, layout.Items, Snapshot([Position("inspection", false)]));
        Assert.Equal(T("Equipment.SlotEmpty"), flow.Slots[1].StateText);
        Assert.Equal("—", flow.Slots[1].InstanceId);
        var current = Position("inspection", true, "piece-2");
        var previous = Result("top", "piece-1", PlaceholderInspectionDecision.Pass);
        flow.Update(project, layout.Items, Snapshot([current], [previous]));
        Assert.Equal(Progress(0), flow.Slots[1].StateText);
        var top = Result("top", "piece-2", PlaceholderInspectionDecision.Pass);
        flow.Update(project, layout.Items, Snapshot([current], [top]));
        Assert.Equal(Progress(1), flow.Slots[1].StateText);
        flow.Update(project, layout.Items, Snapshot([current], [top, Result("height", "piece-2", PlaceholderInspectionDecision.Fail)]));
        Assert.Equal(T("Equipment.SlotInspectionFailed"), flow.Slots[1].StateText);
        flow.Update(project, layout.Items, Snapshot([current], [top, Result("height", "piece-2", PlaceholderInspectionDecision.Pass)]));
        Assert.Equal(T("Equipment.SlotInspectionPassed"), flow.Slots[1].StateText);
        flow.Update(project, layout.Items, Snapshot([Position("inspection", false)], [top]));
        Assert.Equal(T("Equipment.SlotEmpty"), flow.Slots[1].StateText);
        Assert.Equal(serialized, new ProjectDocumentStore().Serialize(project));
    }

    [Fact]
    public void ActiveCameraAndCarrierStatesResetWithoutLeakingIntoAnotherPosition()
    {
        var project = CreateProject();
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var flow = new EquipmentMaterialFlowViewModel();
        var workpiece = Position("inspection", true, "piece-1") with { CarrierComponentId = "belt" };
        var moving = new LayoutComponentSnapshot("belt", "Belt", LayoutComponentKind.Conveyor, 0, 0, 0, 100, 20, null, null, ConveyorRunning: true);
        foreach (var (cameraState, key) in new[]
        {
            (VirtualCameraState.Exposing, "Equipment.SlotInspecting"),
            (VirtualCameraState.AwaitingExternalResult, "Equipment.SlotWaitingResult"),
            (VirtualCameraState.Faulted, "Equipment.SlotInspectionError")
        })
        {
            flow.Update(project, layout.Items, Snapshot([Position("feed", false), workpiece, moving],
                [new VirtualCameraSnapshot("top", "Top", cameraState, 1, "acq", "recipe", 0, 0, null)]));
            Assert.Equal(T("Equipment.SlotEmpty"), flow.Slots[0].StateText);
            Assert.Equal(T("Equipment.SlotMoving") + " / " + T(key), flow.Slots[1].StateText);
        }
        flow.Update(project, layout.Items, Snapshot([workpiece], [Result("top", "piece-1", PlaceholderInspectionDecision.Pass) with
            { Result = Result("top", "piece-1", PlaceholderInspectionDecision.Pass).Result! with { WorkpieceComponentId = "feed" } }]));
        Assert.Equal(Progress(0), flow.Slots[1].StateText);
        project.Sequences[0].Steps.Add(new SequenceStepDefinition { Action = SequenceStepAction.TriggerCamera, TargetId = "top", WorkpieceComponentId = "feed" });
        flow.Update(project, layout.Items, Snapshot([workpiece], [new VirtualCameraSnapshot("top", "Top", VirtualCameraState.Exposing, 1, "acq", "recipe", 0, 0, null)]));
        Assert.Equal(Progress(0), flow.Slots[1].StateText);
        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        layout.Load(reopened);
        flow.Update(reopened, layout.Items, Snapshot([Position("inspection", true, "piece-new")]));
        Assert.Equal("piece-new", flow.Slots[1].InstanceId);
        Assert.Equal(Progress(0), flow.Slots[1].StateText);
        project.Layouts[0].Components.Clear();
        layout.Load(project);
        flow.Update(project, layout.Items, Snapshot());
        Assert.False(flow.HasSlots);
        Assert.Empty(flow.Slots);
        project.Stations.Clear();
        flow.Update(project, layout.Items, Snapshot());
        Assert.False(flow.HasSlots);
        Assert.Empty(flow.Slots);
    }

    [Fact]
    public void UnitCardsRemainIndependentOfWorkpieceCountAndExposeUnknownOrExcessOccupancy()
    {
        var project = CreateProject();
        var feed = project.Layouts[0].Components.Single(component => component.Id == "feed");
        feed.UnitId = "unit-inspection";
        project.Layouts[0].Components.RemoveAll(component => component.Id == "eject");
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var flow = new EquipmentMaterialFlowViewModel();
        flow.Update(project, layout.Items, Snapshot([Position("feed", false), Position("inspection", false)]));
        Assert.Equal(new[] { "unit-feed", "unit-inspection", "unit-eject" }, flow.Slots.Select(slot => slot.UnitId));
        Assert.All(flow.Slots, slot => Assert.Equal(T("Equipment.SlotEmpty"), slot.StateText));
        flow.Update(project, layout.Items, Snapshot([Position("inspection", true, "piece-1")]));
        Assert.Equal(T("Equipment.NoRuntimeData"), flow.Slots[1].StateText);
        Assert.Equal("piece-1", flow.Slots[1].InstanceId);
        flow.Update(project, layout.Items, Snapshot([Position("feed", true, "piece-2"), Position("inspection", true, "piece-1")]));
        Assert.Equal(string.Format(T("Equipment.SlotCapacityExceeded"), 2), flow.Slots[1].StateText);
        Assert.Equal("piece-2, piece-1", flow.Slots[1].InstanceId);
        project.Stations[0].Units[1].Name = "Renamed unit";
        feed.Name = "Unrelated workpiece title";
        layout.Load(project);
        flow.Update(project, layout.Items, Snapshot([Position("feed", false), Position("inspection", false)]));
        Assert.Equal(string.Format(T("Equipment.MaterialSlot"), "Renamed unit"), flow.Slots[1].Name);
        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        reopened.Stations[0].Units.RemoveAt(2);
        layout.Load(reopened);
        flow.Update(reopened, layout.Items, Snapshot([Position("feed", false), Position("inspection", false)]));
        Assert.Equal(2, flow.Slots.Count);
        Assert.Equal(T("Equipment.SlotEmpty"), flow.Slots[1].StateText);
    }

    [Fact]
    public void InitialPlacementShowsNoRunIdentityAndDoesNotReuseOldInspectionResults()
    {
        var project = CreateProject();
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var flow = new EquipmentMaterialFlowViewModel();
        flow.Update(project, layout.Items, Snapshot([Position("inspection", true)],
            [Result("top", "previous-run", PlaceholderInspectionDecision.Fail)]));
        Assert.Equal(T("Equipment.SlotInitialInstance"), flow.Slots[1].InstanceId);
        Assert.Equal(T("Equipment.SlotBeforeInspection"), flow.Slots[1].StateText);
        flow.Update(project, layout.Items, Snapshot([Position("inspection", true, "new-run")]));
        Assert.Equal("new-run", flow.Slots[1].InstanceId);
        Assert.Equal(Progress(0), flow.Slots[1].StateText);
    }

    [Fact]
    public void UnitActivityUsesExplicitCurrentTargetsAndIndependentEquipmentStates()
    {
        var project = CreateProject();
        var belt = project.Layouts[0].Components.Single(component => component.Id == "belt");
        belt.UnitId = "unit-feed";
        project.Sequences.Add(new SequenceDefinition { Id = "branch", Steps =
            [new SequenceStepDefinition { Id = "target", Name = "Inspect current part", TargetId = "device.inspection" }] });
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var flow = new EquipmentMaterialFlowViewModel();
        var current = new SequenceExecutionSnapshot("inspect", SequenceExecutionStatus.Running, "target", 0,
            TimeSpan.Zero, TimeSpan.Zero, 0, null, TimeSpan.Zero, ActiveSequenceId: "branch");
        var moving = new LayoutComponentSnapshot("belt", "Belt", LayoutComponentKind.Conveyor, 0, 0, 0, 100, 20, null, null, ConveyorRunning: true);
        flow.Update(project, layout.Items, Snapshot([moving], sequences: [current]));
        Assert.Contains("Belt", flow.UnitActivities[0].StateText);
        Assert.Contains("Inspect current part", flow.UnitActivities[1].StateText);
        Assert.Equal(T("Equipment.UnitNoCurrentTarget"), flow.UnitActivities[2].StateText);
        // A global step with no target must not match unbound parts by null equality.
        project.Sequences[1].Steps[0].TargetId = null!;
        flow.Update(project, layout.Items, Snapshot(sequences: [current]));
        Assert.All(flow.UnitActivities, unit => Assert.Equal(T("Equipment.UnitNoCurrentTarget"), unit.StateText));
        project.Layouts[0].Components.RemoveAll(component => component.Kind == LayoutComponentKind.Workpiece);
        layout.Load(project);
        flow.Update(project, layout.Items, Snapshot([moving]));
        Assert.False(flow.HasSlots);
        Assert.Equal(3, flow.UnitActivities.Count);
        Assert.Contains("Belt", flow.UnitActivities[0].StateText);
    }

    internal static MachineProjectDocument CreateProject()
    {
        var project = new MachineProjectDocument
        {
            Stations = [new MachineStationDefinition { Id = "station", Name = "Station", Units =
                new[] { "feed", "inspection", "eject" }.Select(id => new MachineUnitDefinition { Id = "unit-" + id, Name = id }).ToList() }],
            Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "Slots", Components =
                new[] { "feed", "inspection", "eject" }.Select((id, index) => new LayoutComponentDefinition
                {
                    Id = id, Name = id, Kind = LayoutComponentKind.Workpiece, UnitId = "unit-" + id, ZIndex = index, BehaviorBindingId = "device." + id,
                    Transform = new Transform2D { X = 80 + index * 100, Y = 80 }, Size = new Size2D { Width = 34, Height = 34 }
                }).ToList() }],
            Sequences = [new SequenceDefinition { Id = "inspect", Steps = new[] { "top", "height" }.Select(camera =>
                new SequenceStepDefinition { Action = SequenceStepAction.TriggerCamera, TargetId = camera, WorkpieceComponentId = "inspection" }).ToList() }]
        };
        project.Layouts[0].Components.Add(new LayoutComponentDefinition
        {
            Id = "belt", Name = "Belt", Kind = LayoutComponentKind.Conveyor, BehaviorBindingId = "device.belt", ZIndex = -1,
            Transform = new Transform2D { X = 180, Y = 80 }, Size = new Size2D { Width = 400, Height = 34 }
        });
        project.Devices.Add(new DeviceDefinition { Id = "device.belt", Name = "Belt", Kind = DeviceKind.Conveyor,
            Conveyor = new ConveyorDefinition { RunCommandChannelId = "run", ReverseCommandChannelId = "reverse" } });
        project.Channels.Add(new ChannelDefinition { Id = "run", Name = "Run", Kind = ChannelKind.DigitalOutput });
        project.Channels.Add(new ChannelDefinition { Id = "reverse", Name = "Reverse", Kind = ChannelKind.DigitalOutput });
        foreach (var id in new[] { "feed", "inspection", "eject" })
            project.Devices.Add(new DeviceDefinition { Id = "device." + id, Name = id, Kind = DeviceKind.Workpiece,
                Workpiece = new WorkpieceDefinition { ConveyorComponentId = "belt", InitiallyPresent = false } });
        project.Simulation.ActiveLayoutId = "layout";
        return project;
    }

    private static LayoutComponentSnapshot Position(string id, bool present, string? instance = null) =>
        new(id, id, LayoutComponentKind.Workpiece, 0, 0, 0, 34, 34, null, null, WorkpieceInstanceId: instance, IsWorkpiecePresent: present);
    private static VirtualCameraSnapshot Result(string id, string instance, PlaceholderInspectionDecision decision) =>
        new(id, id, VirtualCameraState.FrameReady, 1, "acq", "recipe", 0, 0,
            new VirtualCameraAcquisitionResult("acq", id, "recipe", 1, decision, WorkpieceComponentId: "inspection", WorkpieceInstanceId: instance));
    private static SimulationSnapshot Snapshot(LayoutComponentSnapshot[]? positions = null, VirtualCameraSnapshot[]? cameras = null, SequenceExecutionSnapshot[]? sequences = null) =>
        new(TimeSpan.Zero, 0, SimulationRunMode.Paused, SimulationControlOwner.Definition, 1, [], 0, [], sequences ?? [], cameras ?? [], AutomaticRunSnapshot.NotConfigured, positions ?? []);
    private static string T(string key) => OpenVisionLanguageService.T(key);
    private static string Progress(int count) => string.Format(T("Equipment.SlotInspectionProgress"), count, 2);
}
