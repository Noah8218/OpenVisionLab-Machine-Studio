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
    SequenceExecutionStatus? ActiveSequenceStatus,
    bool HasCurrentAxis = false,
    bool IsCurrentAxisInterlocked = false,
    bool HasCurrentCamera = false,
    bool IsRunControlBusy = false,
    bool IsModeTransitioning = false,
    bool IsSelectionEligible = true);

/// <summary>
/// Projects the selected manual equipment state into the commissioning
/// workspace contract. The shell supplies one immutable runtime projection;
/// this type owns the equipment-specific presentation and command gates.
/// </summary>
internal sealed class ManualEquipmentPresentation
{
    private ManualEquipmentProjection? _projection;

    internal void ApplyProjection(ManualEquipmentProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _projection = projection;
    }

    internal bool HasProjection => _projection is not null;

    internal bool HasSelectedManualEquipment => Projection.SelectedComponentKind is
        LayoutComponentKind.LinearStage or
        LayoutComponentKind.RotaryStage or
        LayoutComponentKind.DigitalSensor or
        LayoutComponentKind.PneumaticCylinder or
        LayoutComponentKind.Conveyor;

    internal bool HasSelectedManualControlTarget => HasSelectedManualEquipment
        || Projection.SelectedComponentKind is LayoutComponentKind.Camera or LayoutComponentKind.DigitalIo or LayoutComponentKind.FeedPosition;

    internal bool CanPrepareManualControl => HasSelectedManualControlTarget
        && IsManualAdmissionReady
        && !IsSelectedTargetInterlocked
        && (Projection.IsRuntimeDefinitionDirty && !Projection.IsRunMode || IsSelectedTargetAvailable);

    internal bool CanAcquireManualControl => HasSelectedManualControlTarget
        && IsManualAdmissionReady
        && !Projection.IsRuntimeDefinitionDirty
        && !IsSelectedTargetInterlocked
        && IsSelectedTargetAvailable;

    internal bool CanStartManualEquipmentControl => Projection.IsRunMode
        && HasSelectedManualEquipment
        && CanAcquireManualControl;

    internal string ManualControlOwnerText => OpenVisionLanguageService.T(
        "Equipment.ManualControlOwner", "현재 제어권: {0}", "Current control: {0}")
        .Replace("{0}", OpenVisionLanguageService.T(
            $"Shell.ControlOwnerLabel.{Projection.ControlOwner}",
            Projection.ControlOwner switch
            {
                SimulationControlOwner.Manual => "수동",
                SimulationControlOwner.EmbeddedSequence => "시퀀스",
                _ => "장비 구성"
            },
            Projection.ControlOwner switch
            {
                SimulationControlOwner.Manual => "Manual",
                SimulationControlOwner.EmbeddedSequence => "Sequence",
                _ => "Equipment definition"
            }), StringComparison.Ordinal);

    internal string ManualControlHintText
    {
        get
        {
            if (!Projection.IsSelectionEligible)
                return Hint("Selection", "장비 구성에서 장치 하나를 선택하고 현재 편집을 마친 뒤 수동 시운전을 시작하세요.", "Select one component in Equipment and finish the current edit before starting manual commissioning.");
            if (Projection.IsApplyingProject)
                return Hint("Applying", "장비 구성을 준비하고 있습니다. 완료 후 다시 시도하세요.", "The equipment definition is being prepared. Try again when it finishes.");
            if (Projection.IsValidationBusy)
                return Hint("Validation", "검증이 끝난 뒤 수동 시운전을 시작하세요.", "Start manual commissioning after validation finishes.");
            if (Projection.IsModeTransitioning || Projection.IsRunControlBusy)
                return Hint("Transition", "현재 운전 명령과 모드 전환이 끝난 뒤 다시 시도하세요.", "Try again after the current run command and mode transition finish.");
            if (Projection.IsAutomaticRunActive || Projection.ActiveSequenceStatus == SequenceExecutionStatus.Running)
                return Hint("Automatic", "자동 운전 또는 시퀀스를 종료·초기화한 뒤 수동 시운전을 시작하세요.", "End and reset the automatic run or sequence before starting manual commissioning.");
            if (IsSelectedTargetInterlocked)
                return Hint("Interlock", "선택 장치의 오류 또는 인터록을 해제한 뒤 다시 시도하세요.", "Clear the selected component fault or interlock before trying again.");
            if (Projection.IsRunning)
                return Projection.ControlOwner == SimulationControlOwner.Manual
                    ? Hint("Running", "수동 운전 중입니다. 선택 장치의 명령을 실행할 수 있습니다. 편집 복귀 시 운전을 종료·초기화합니다.", "Manual control is running. Use the selected component commands. Returning to editing ends and resets the run.")
                    : Hint("Pause", "진행 중인 운전을 일시정지한 뒤 수동 시운전을 시작하세요.", "Pause the current run before starting manual commissioning.");
            if (Projection.IsRuntimeDefinitionDirty)
                return Projection.IsRunMode
                    ? Hint("DirtyRun", "편집으로 돌아가 장비 설정을 적용한 뒤 다시 시도하세요.", "Return to editing and apply the equipment settings before trying again.")
                    : Hint("Prepare", "수동 시운전 시작 시 적용된 장비 구성을 준비합니다. 자동 운전·시퀀스는 시작하지 않습니다.", "Starting manual commissioning prepares the applied equipment definition. It does not start an automatic run or sequence.");
            if (!IsSelectedTargetAvailable)
                return Hint("MissingTarget", "장치의 구동 연결과 장비 설정을 확인하고 적용한 뒤 다시 시도하세요.", "Check and apply the component drive connection and equipment settings before trying again.");
            if (Projection.ControlOwner == SimulationControlOwner.Manual)
                return Projection.SelectedComponentKind == LayoutComponentKind.Camera
                    ? Hint("CameraReady", "수동 제어가 준비됐습니다. 선택 촬영부의 촬영 명령을 실행하세요. 편집 복귀 시 운전을 종료·초기화합니다.", "Manual control is ready. Trigger the selected camera. Returning to editing ends and resets the run.")
                    : Hint("Paused", "수동 운전이 일시정지됐습니다. 수동 시운전 시작으로 재개하세요. 편집 복귀 시 운전을 종료·초기화합니다.", "Manual control is paused. Start manual commissioning to resume. Returning to editing ends and resets the run.");
            return Hint("Ready", "수동 시운전을 시작한 뒤 선택 장치의 명령을 실행하세요. 자동 운전·시퀀스는 시작하지 않습니다.", "Start manual commissioning, then use the selected component commands. No automatic run or sequence will start.");
        }
    }

    private bool IsManualAdmissionReady => Projection.IsSelectionEligible
        && !Projection.IsApplyingProject
        && !Projection.IsValidationBusy
        && !Projection.IsModeTransitioning
        && !Projection.IsRunControlBusy
        && !Projection.IsRunning
        && !Projection.IsAutomaticRunActive
        && Projection.ActiveSequenceStatus != SequenceExecutionStatus.Running;

    private bool IsSelectedTargetAvailable => Projection.SelectedComponentKind switch
    {
        LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage => Projection.HasCurrentAxis,
        LayoutComponentKind.Camera => Projection.HasCurrentCamera,
        LayoutComponentKind.DigitalSensor => HasSelectedDigitalSensor,
        LayoutComponentKind.PneumaticCylinder => HasSelectedPneumaticCylinder,
        LayoutComponentKind.Conveyor => HasSelectedConveyor,
        LayoutComponentKind.DigitalIo or LayoutComponentKind.FeedPosition => !string.IsNullOrEmpty(Projection.SelectedComponentId),
        _ => false
    };

    private bool IsSelectedTargetInterlocked => Projection.SelectedComponentKind switch
    {
        LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage => Projection.IsCurrentAxisInterlocked,
        LayoutComponentKind.DigitalSensor => IsCurrentSensorFaulted,
        LayoutComponentKind.PneumaticCylinder => IsCurrentCylinderInterlocked,
        _ => false
    };

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

    private static string Hint(string key, string korean, string english) =>
        OpenVisionLanguageService.T($"Equipment.ManualHint.{key}", korean, english);

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
