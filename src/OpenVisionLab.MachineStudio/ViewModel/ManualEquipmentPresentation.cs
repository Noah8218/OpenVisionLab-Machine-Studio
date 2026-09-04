using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record ManualEquipmentProjection(
    SimulationSnapshot Snapshot,
    string? SelectedComponentId,
    LayoutComponentKind? SelectedComponentKind,
    bool IsRunMode,
    bool IsApplyingProject,
    bool IsValidationBusy,
    bool IsRuntimeDefinitionDirty,
    bool IsRunning,
    SimulationControlOwner ControlOwner,
    bool IsAutomaticRunActive,
    SequenceExecutionStatus? ActiveSequenceStatus);

/// <summary>
/// Projects selected sensor, cylinder, and conveyor state into the existing
/// Machine Studio presentation contract. Main retains the public presentation
/// and command facade while ManualControlCommandWorkflow owns command mapping.
/// </summary>
internal sealed class ManualEquipmentPresentation
{
    private ManualEquipmentProjection? _projection;

    internal void ApplyProjection(ManualEquipmentProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _projection = projection;
    }

    internal bool HasSelectedDigitalSensor => CurrentSensorSnapshot is not null;

    internal bool IsCurrentSensorFaulted => CurrentSensorOutputChannelId is { } channelId
        && Projection.Snapshot.Faults.Any(fault =>
            fault.Kind == SimulationFaultKind.StuckDigitalInput
            && string.Equals(fault.TargetId, channelId, StringComparison.Ordinal));

    internal bool IsCurrentSensorManuallyForced =>
        !IsCurrentSensorFaulted && CurrentSensorSignal?.OverrideValue.HasValue == true;

    internal string CurrentSensorForceText => !HasSelectedDigitalSensor
        ? "??"
        : IsCurrentSensorFaulted
            ? OpenVisionLanguageService.T("Sensor.FaultOverride")
            : CurrentSensorSignal?.OverrideValue switch
            {
                true => OpenVisionLanguageService.T("Sensor.ForcedOn"),
                false => OpenVisionLanguageService.T("Sensor.ForcedOff"),
                _ => OpenVisionLanguageService.T("Sensor.ForceReleased")
            };

    internal string SensorCommissioningHintText => !HasSelectedDigitalSensor
        ? OpenVisionLanguageService.T("Sensor.NoSensorHint")
        : IsCurrentSensorFaulted
            ? OpenVisionLanguageService.T("Sensor.ClearFaultHint")
            : Projection.ControlOwner == SimulationControlOwner.Manual
                ? Projection.IsRunning
                    ? OpenVisionLanguageService.T("Sensor.ManualRunningHint")
                    : OpenVisionLanguageService.T("Sensor.ManualPausedHint")
                : Projection.IsRunning || Projection.IsAutomaticRunActive ||
                  Projection.ActiveSequenceStatus == SequenceExecutionStatus.Running
                    ? OpenVisionLanguageService.T("Sensor.ResetForManualHint")
                    : OpenVisionLanguageService.T("Sensor.StartManualHint");

    internal bool CanForceSensorOn => CanUseManualSensor
        && CurrentSensorSignal?.OverrideValue != true;

    internal bool CanForceSensorOff => CanUseManualSensor
        && CurrentSensorSignal?.OverrideValue != false;

    internal bool CanClearSensorForce => CanUseManualSensor && IsCurrentSensorManuallyForced;

    internal bool HasSelectedPneumaticCylinder => CurrentCylinderSnapshot is not null;

    internal bool IsCurrentCylinderInterlocked => CurrentCylinderSnapshot is { } cylinder
        && Projection.Snapshot.Faults.Any(fault =>
            fault.Kind == SimulationFaultKind.CylinderTravelBlocked
            && string.Equals(fault.TargetId, cylinder.Id, StringComparison.Ordinal));

    internal string CurrentCylinderInterlockText => OpenVisionLanguageService.T(
        IsCurrentCylinderInterlocked ? "Cylinder.InterlockBlocked" : "Cylinder.InterlockReady");

    internal string CylinderCommissioningHintText => !HasSelectedPneumaticCylinder
        ? OpenVisionLanguageService.T("Cylinder.NoCylinderHint")
        : IsCurrentCylinderInterlocked
            ? OpenVisionLanguageService.T("Cylinder.ClearInterlockHint")
            : Projection.ControlOwner == SimulationControlOwner.Manual
                ? Projection.IsRunning
                    ? OpenVisionLanguageService.T("Cylinder.ManualRunningHint")
                    : OpenVisionLanguageService.T("Cylinder.ManualPausedHint")
                : Projection.IsRunning || Projection.IsAutomaticRunActive ||
                  Projection.ActiveSequenceStatus == SequenceExecutionStatus.Running
                    ? OpenVisionLanguageService.T("Cylinder.ResetForManualHint")
                    : OpenVisionLanguageService.T("Cylinder.StartManualHint");

    internal bool CanExtendCylinder => CanUseManualCylinder
        && CurrentCylinderSnapshot?.CylinderState is not PneumaticCylinderState.Extending
            and not PneumaticCylinderState.Extended;

    internal bool CanRetractCylinder => CanUseManualCylinder
        && CurrentCylinderSnapshot?.CylinderState is not PneumaticCylinderState.Retracting
            and not PneumaticCylinderState.Retracted;

    internal bool HasSelectedConveyor => CurrentConveyorSnapshot is not null;

    internal string ConveyorCommissioningHintText => !HasSelectedConveyor
        ? OpenVisionLanguageService.T("Conveyor.NoConveyorHint")
        : Projection.ControlOwner == SimulationControlOwner.Manual
            ? Projection.IsRunning
                ? OpenVisionLanguageService.T("Conveyor.ManualRunningHint")
                : OpenVisionLanguageService.T("Conveyor.ManualPausedHint")
            : Projection.IsRunning || Projection.IsAutomaticRunActive ||
              Projection.ActiveSequenceStatus == SequenceExecutionStatus.Running
                ? OpenVisionLanguageService.T("Conveyor.ResetForManualHint")
                : OpenVisionLanguageService.T("Conveyor.StartManualHint");

    internal bool CanRunConveyorForward => CanUseManualConveyor
        && CurrentConveyorSnapshot is { } conveyor
        && (conveyor.ConveyorRunning != true
            || conveyor.ConveyorDirection != ConveyorDirection.Forward);

    internal bool CanRunConveyorReverse => CanUseManualConveyor
        && CurrentConveyorSnapshot is { } conveyor
        && (conveyor.ConveyorRunning != true
            || conveyor.ConveyorDirection != ConveyorDirection.Reverse);

    internal bool CanStopConveyor => CanUseManualConveyor
        && CurrentConveyorSnapshot?.ConveyorRunning == true;

    internal DigitalSignalSnapshot? CurrentSelectedSensorSignal => CurrentSensorSignal;

    internal string? SelectedCylinderId => CurrentCylinderSnapshot?.Id;

    internal string? SelectedSensorId => CurrentSensorSnapshot?.Id;

    internal string? SelectedConveyorId => CurrentConveyorSnapshot?.Id;

    internal ConveyorDirection? SelectedConveyorDirection => CurrentConveyorSnapshot?.ConveyorDirection;

    private ManualEquipmentProjection Projection => _projection
        ?? throw new InvalidOperationException("Manual equipment projection has not been initialized.");

    private LayoutComponentSnapshot? CurrentCylinderSnapshot => FindComponent(
        LayoutComponentKind.PneumaticCylinder);

    private LayoutComponentSnapshot? CurrentSensorSnapshot => FindComponent(
        LayoutComponentKind.DigitalSensor);

    private string? CurrentSensorOutputChannelId => CurrentSensorSnapshot?.SensorOutputChannelId;

    private DigitalSignalSnapshot? CurrentSensorSignal => CurrentSensorOutputChannelId is { } channelId
        ? Projection.Snapshot.Signals.FirstOrDefault(signal =>
            string.Equals(signal.Id, channelId, StringComparison.Ordinal))
        : null;

    private LayoutComponentSnapshot? CurrentConveyorSnapshot => FindComponent(
        LayoutComponentKind.Conveyor);

    private LayoutComponentSnapshot? FindComponent(LayoutComponentKind kind) =>
        Projection.SelectedComponentKind == kind
            ? Projection.Snapshot.LayoutComponents.FirstOrDefault(component =>
                string.Equals(
                    component.Id,
                    Projection.SelectedComponentId,
                    StringComparison.Ordinal)
                && component.Kind == kind)
            : null;

    private bool CanUseManualCylinder => Projection.IsRunMode
        && !Projection.IsApplyingProject
        && !Projection.IsValidationBusy
        && !Projection.IsRuntimeDefinitionDirty
        && Projection.ControlOwner == SimulationControlOwner.Manual
        && !IsCurrentCylinderInterlocked
        && CurrentCylinderSnapshot is not null;

    private bool CanUseManualSensor => Projection.IsRunMode
        && !Projection.IsApplyingProject
        && !Projection.IsValidationBusy
        && !Projection.IsRuntimeDefinitionDirty
        && Projection.ControlOwner == SimulationControlOwner.Manual
        && !IsCurrentSensorFaulted
        && CurrentSensorSnapshot is not null;

    private bool CanUseManualConveyor => Projection.IsRunMode
        && !Projection.IsApplyingProject
        && !Projection.IsValidationBusy
        && !Projection.IsRuntimeDefinitionDirty
        && Projection.ControlOwner == SimulationControlOwner.Manual
        && CurrentConveyorSnapshot is not null;
}
