using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record EquipmentUnitSlotItem(string UnitId, string Name, string CapacityText, string InstanceId, string StateText, string ActivityText = "");
public sealed record EquipmentUnitActivityItem(string UnitId, string Name, string StateText);

/// <summary>Projects one slot per unit from assigned workpiece snapshots; never owns material or execution state.</summary>
public sealed class EquipmentMaterialFlowViewModel : ViewModelBase
{
    private IReadOnlyList<EquipmentUnitSlotItem> _slots = Array.Empty<EquipmentUnitSlotItem>();
    private IReadOnlyList<EquipmentUnitActivityItem> _unitActivities = Array.Empty<EquipmentUnitActivityItem>();

    public IReadOnlyList<EquipmentUnitSlotItem> Slots => _slots;
    public bool HasSlots => _slots.Count > 0;
    public IReadOnlyList<EquipmentUnitActivityItem> UnitActivities => _unitActivities;

    public void Update(MachineProjectDocument project, IEnumerable<LayoutItem> items, SimulationSnapshot snapshot)
    {
        var layoutItems = items.ToArray();
        var units = project.Stations.SelectMany(station => station.Units).ToArray();
        var partsByUnit = layoutItems.ToLookup(item => item.UnitId, StringComparer.Ordinal);
        var unitActivities = units.Select(unit =>
        {
            var parts = partsByUnit[unit.Id].ToArray();
            var targets = snapshot.Sequences.Select(sequence => (Sequence: sequence, Step: project.Sequences
                .FirstOrDefault(definition => definition.Id == (sequence.ActiveSequenceId ?? sequence.SequenceId))?.Steps
                .FirstOrDefault(step => step.Id == sequence.CurrentStepId)))
                .Where(entry => entry.Step is not null && parts.Any(part => (!string.IsNullOrWhiteSpace(entry.Step.TargetId) && entry.Step.TargetId is { } target
                    && (part.Id == target || part.BehaviorBindingId == target))
                    || (entry.Step.WorkpieceComponentId is { } workpiece && part.Id == workpiece)))
                .Select(entry => Format("Equipment.UnitCurrentTarget", string.IsNullOrWhiteSpace(entry.Step!.Name) ? entry.Step.Id : entry.Step.Name,
                    OpenVisionLanguageService.T($"Equipment.State.{entry.Sequence.Status}"))).ToArray();
            var equipment = parts.Where(part => part.Kind is LayoutItemKind.LinearStage or LayoutItemKind.RotaryStage
                    or LayoutItemKind.PneumaticCylinder or LayoutItemKind.Conveyor)
                .Select(part => EquipmentStatusPresentation.Create(part, snapshot, project))
                .Where(state => state.IsActive || state.IsFaulted).Select(state => $"{state.Name} / {state.StateText}");
            var cameras = parts.Where(part => part.Kind == LayoutItemKind.Camera)
                .Select(part => snapshot.Cameras.FirstOrDefault(camera => camera.Id == part.BehaviorBindingId))
                .Where(camera => camera?.State is VirtualCameraState.Exposing or VirtualCameraState.Transferring
                    or VirtualCameraState.AwaitingExternalResult or VirtualCameraState.Faulted)
                .Select(camera => $"{camera!.Name} / {OpenVisionLanguageService.T($"Equipment.State.{camera.State}")}");
            var observed = targets.Concat(equipment).Concat(cameras).ToArray();
            var activity = observed.Length == 0 ? OpenVisionLanguageService.T("Equipment.UnitNoCurrentTarget") : string.Join(" / ", observed);
            return new EquipmentUnitActivityItem(unit.Id, unit.Name, activity);
        }).ToArray();
        if (!_unitActivities.SequenceEqual(unitActivities))
        {
            _unitActivities = unitActivities;
            OnPropertyChanged(nameof(UnitActivities));
        }
        var components = snapshot.LayoutComponents.ToDictionary(component => component.Id, StringComparer.Ordinal);
        var steps = project.Sequences.SelectMany(sequence => sequence.Steps).ToArray();
        var workpieces = layoutItems.Where(item => item.Kind == LayoutItemKind.Workpiece).ToLookup(item => item.UnitId, StringComparer.Ordinal);
        var slots = units.Where(_ => workpieces.Count > 0).Select(unit =>
        {
            var positions = workpieces[unit.Id].Select(item => components.GetValueOrDefault(item.Id)).ToArray();
            var occupied = positions.Where(position => position?.IsWorkpiecePresent == true).ToArray();
            var name = Format("Equipment.MaterialSlot", unit.Name);
            var instance = occupied.Length == 0 ? "—" : string.Join(", ", occupied.Select(position => position!.WorkpieceInstanceId ?? OpenVisionLanguageService.T("Equipment.SlotInitialInstance")));
            var state = OpenVisionLanguageService.T("Equipment.SlotEmpty");
            if (occupied.Length > 1)
            {
                state = Format("Equipment.SlotCapacityExceeded", occupied.Length);
            }
            else if (positions.Any(position => position?.IsWorkpiecePresent is null))
            {
                state = OpenVisionLanguageService.T("Equipment.NoRuntimeData");
            }
            else if (occupied.Length == 1)
            {
                var runtime = occupied[0]!;
                var cameras = steps.Where(step => step.Action == SequenceStepAction.TriggerCamera && step.WorkpieceComponentId == runtime.Id)
                    .Select(step => step.TargetId).Distinct(StringComparer.Ordinal).ToArray();
                var associated = snapshot.Cameras.Where(camera => cameras.Contains(camera.Id, StringComparer.Ordinal)).ToArray();
                var results = associated.Where(camera => runtime.WorkpieceInstanceId is not null &&
                    camera.Result is { } result && result.WorkpieceComponentId == runtime.Id && result.WorkpieceInstanceId == runtime.WorkpieceInstanceId).ToArray();
                state = cameras.Length == 0 ? OpenVisionLanguageService.T("Equipment.SlotOccupied") : Format("Equipment.SlotInspectionProgress", results.Length, cameras.Length);
                if (runtime.WorkpieceInstanceId is null) state = OpenVisionLanguageService.T(cameras.Length == 0 ? "Equipment.SlotInitialPlacement" : "Equipment.SlotBeforeInspection");
                if (cameras.Length > 0 && results.Length == cameras.Length)
                {
                    state = OpenVisionLanguageService.T(results.Any(camera => camera.Result!.Decision == PlaceholderInspectionDecision.Fail)
                        ? "Equipment.SlotInspectionFailed" : "Equipment.SlotInspectionPassed");
                }
                // An active camera is attributable only when the recipe gives it a single workpiece target.
                var active = associated.Where(camera => steps.Where(step => step.Action == SequenceStepAction.TriggerCamera && step.TargetId == camera.Id)
                    .Select(step => step.WorkpieceComponentId).Distinct(StringComparer.Ordinal).SequenceEqual(new[] { runtime.Id })).ToArray();
                if (active.Any(camera => camera.State == VirtualCameraState.AwaitingExternalResult)) state = OpenVisionLanguageService.T("Equipment.SlotWaitingResult");
                else if (active.Any(camera => camera.State is VirtualCameraState.Exposing or VirtualCameraState.Transferring)) state = OpenVisionLanguageService.T("Equipment.SlotInspecting");
                else if (active.Any(camera => camera.State == VirtualCameraState.Faulted)) state = OpenVisionLanguageService.T("Equipment.SlotInspectionError");
                if (runtime.CarrierComponentId is { } carrier && components.TryGetValue(carrier, out var conveyor) && conveyor.ConveyorRunning == true)
                    state = OpenVisionLanguageService.T("Equipment.SlotMoving") + " / " + state;
            }
            return new EquipmentUnitSlotItem(unit.Id, name, OpenVisionLanguageService.T("Equipment.SlotCapacity"), instance, state,
                unitActivities.First(activity => activity.UnitId == unit.Id).StateText);
        }).ToArray();
        if (_slots.SequenceEqual(slots)) return;
        _slots = slots;
        OnPropertyChanged(nameof(Slots));
        OnPropertyChanged(nameof(HasSlots));
    }

    private static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
}
