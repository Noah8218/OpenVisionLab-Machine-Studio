using System.Reflection;
using System.Text;

namespace OpenVisionLab;

public enum OpenVisionLanguage
{
    Korean,
    English
}

public sealed class OpenVisionLanguageOption
{
    public OpenVisionLanguageOption(OpenVisionLanguage language, string displayName)
    {
        Language = language;
        DisplayName = displayName;
    }

    public OpenVisionLanguage Language { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public static class OpenVisionLanguageService
{
    private const string ConfigDirectoryName = "CONFIG";
    private const string CompanyDirectoryName = "OpenVisionLab";
    private const string ProductDirectoryName = "MachineStudio";
    private const string CatalogFileName = "localization_catalog.tsv";
    private const string LanguageFileName = "language.txt";
    private static readonly Dictionary<string, (string Korean, string English)[]> LegacyWorkspaceDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Simulation.ScheduledFaultRecovery"] = [("예약 장애 및 복구", "Scheduled fault and recovery")],
            ["Simulation.EnableScheduledFault"] = [("예약 장애 사용", "Enable scheduled fault")],
            ["Simulation.FaultKind"] = [("장애 종류", "Fault type")],
            ["Simulation.FaultTarget"] = [("장애 대상", "Fault target")],
            ["Simulation.RestartSequence"] = [("장애 해제 후 시퀀스 재시작", "Restart sequence after clear")],
            ["Simulation.TestWorkspaceRunHint"] = [("조건을 설정한 뒤 상단에서 운전 모드로 전환하여 시험을 실행하세요.", "Configure the conditions, then switch to Run mode above to execute the test.")],
            ["Simulation.RequireNoActiveFaults"] = [("마지막에 활성 장애 없음", "No active faults at finish")],
            ["Simulation.RunBatch"] = [("반복 실행", "Run batch")],
            ["Camera.InputKind"] = [("입력 종류", "Input kind")],
            ["Connections.AddSensorTooltip"] = [("선택한 스테이지 또는 워크피스를 감지하는 DI 센서를 추가합니다.", "Add a DI sensor that detects the selected stage or workpiece.")],
            ["Connections.AddWorkpiece"] = [("워크피스", "Workpiece")],
            ["Connections.AddWorkpieceTooltip"] = [("프로젝트의 컨베이어에 연결된 워크피스를 추가합니다.", "Add a workpiece connected to a project conveyor.")],
            ["Connections.Behavior"] = [("동작 바인딩", "Behavior binding")],
            ["Connections.CheckReadiness"] = [("시뮬레이션 준비 확인", "Check simulation readiness")],
            ["Connections.NoSequenceUse"] = [("직접 사용하는 단계 없음", "No directly linked step")],
            ["Connections.OpenStep"] = [("단계 열기", "Open step")],
            ["Connections.OpenStepRejectedStatus"] = [("연결된 시퀀스 단계를 찾을 수 없습니다.", "The linked sequence step could not be found.")],
            ["Connections.OpenStepStatus"] = [("시퀀스 단계 '{0}'을(를) 열었습니다.", "Opened sequence step '{0}'.")],
            ["Connections.OpenStepTooltip"] = [("이 연결을 사용하는 첫 번째 시퀀스 단계를 선택합니다.", "Select the first sequence step that uses this connection.")],
            ["Connections.QuickAddHint"] = [("기존 프로젝트 모델에 축·장치·I/O가 함께 생성됩니다. 센서는 스테이지 또는 워크피스 카드를 먼저 선택하면 해당 대상을 연결하며, 상세 연결은 오른쪽 속성에서 편집합니다.", "Axis, device, and I/O definitions are created together in the existing project model. For a sensor, first select its stage or workpiece target; edit detailed links in Properties.")],
            ["Connections.QuickAddTitle"] = [("구성요소 추가 및 자동 연결", "Add and connect equipment")],
            ["Connections.ReadinessFailedStatus"] = [("시뮬레이션 준비 확인에서 오류가 발견되었습니다.", "Simulation readiness found an error.")],
            ["Connections.ReadinessNotCheckedDetail"] = [("운전 전에 시뮬레이션 준비 확인을 실행하십시오.", "Check simulation readiness before Run.")],
            ["Connections.ReadinessPassedStatus"] = [("시뮬레이션 준비 확인을 통과했습니다. 시뮬레이션은 시작되지 않았습니다.", "Simulation readiness passed. Simulation was not started.")],
            ["Connections.ReturnToProcessPlan"] = [("공정 계획으로 돌아가기", "Return to process plan")],
            ["Connections.SequenceUse"] = [("시퀀스 사용", "Sequence use")],
            ["Connections.ValidationSequenceLocationFormat"] = [("시퀀스 {0} · 단계 {1}", "sequence {0} · step {1}")],
            ["Connections.CheckReadinessTooltip"] = [("현재 프로젝트 전체를 컴파일해 실행 가능 여부를 확인하며 시뮬레이션은 시작하지 않습니다.", "Compile the current project to check whether it can run without starting simulation.")],
            ["Connections.ReadinessPassedDetail"] = [("현재 연결과 시퀀스가 컴파일되었습니다. 이 확인은 시뮬레이션을 시작하지 않습니다.", "The current connections and sequence compile successfully. This check does not start simulation.")],
            ["Connections.ReadinessStale"] = [("만료됨", "Stale")],
            ["Connections.ReadinessStaleDetail"] = [("프로젝트 revision이 {0}에서 {1}(으)로 변경되어 준비 확인이 만료되었습니다. 다시 준비 확인을 실행하십시오.", "Readiness is stale because the project revision changed from {0} to {1}. Run readiness again.")],
            ["Connections.ReadinessComparisonInitial"] = [("이번 준비 확인 결과를 기준으로 삼았습니다.", "This readiness result is the baseline.")],
            ["Connections.ReadinessComparisonUnchanged"] = [("이전 준비 확인과 동일합니다.", "Readiness is unchanged from the previous check.")],
            ["Connections.ReadinessComparisonImproved"] = [("이전 준비 확인보다 개선되었습니다.", "Readiness improved from the previous check.")],
            ["Connections.ReadinessComparisonRegressed"] = [("이전 준비 확인보다 악화되었습니다.", "Readiness regressed from the previous check.")],
            ["Connections.ReadinessComparisonChanged"] = [("준비 확인 결과가 변경되었습니다.", "Readiness result changed.")],
            ["Connections.ReadinessComparisonRerunRequired"] = [("편집 후 준비 확인을 다시 실행해야 합니다.", "Run readiness again after editing.")],
            ["Camera.CaptureStation"] = [("촬영부", "Capture station")],
            ["Camera.RunInputs"] = [("운전 입력 보기", "View run inputs")],
            ["Camera.EquipmentLocation"] = [("장비 위치", "Equipment location")],
            ["Camera.Variation"] = [("편차", "Variation")],
            ["Camera.Generation"] = [("생성", "Generation")],
            ["Camera.InspectionConnection"] = [("검사 연결", "Inspection connection")],
            ["Camera.ReferenceAxis"] = [("편차 기준 축", "Reference axis")],
            ["Camera.CaptureWidth"] = [("촬영 폭 (LU)", "Capture width (LU)")],
            ["Camera.CaptureSetupHint"] = [("지정 위치에서 촬영 / 위치·기울기·흔들림 편차 허용", "Capture at the configured position with position, tilt and shake variation.")],
            ["Camera.CaptureScopeHint"] = [("선택한 촬영부에 저장합니다. 운전 입력에는 당시 작업물·촬영 단계를 함께 남깁니다.", "Saved per capture station. Run inputs retain their workpiece and capture step.")],
            ["Camera.AxisDeviationHint"] = [("X 편차 = 설정 X + 기준 축 위치. Y·회전·기울기는 검사판의 예제 편차입니다.", "X deviation = configured X + reference axis position. Y, rotation and tilt are numerical example variations.")],
            ["Camera.VariationHint"] = [("장비 장면의 확대·회전과 다른 촬영 조건입니다.", "Capture conditions are separate from equipment scene zoom and rotation.")],
            ["Camera.UseNumericalInput"] = [("수치 모의 입력 사용", "Use numerical mock input")],
            ["Camera.NumericalInputHint"] = [("수치 모의 입력입니다. 실제 AI 생성·물리 측정·외부 검사를 수행하지 않습니다.", "Numerical mock input. Does not perform AI generation, physical measurement or external inspection.")],
            ["Camera.MockDefect"] = [("모의 불량 입력", "Mock defect input")],
            ["Camera.ApplySettings"] = [("설정 적용", "Apply settings")],
            ["Camera.TwoDImage"] = [("2D 이미지", "2D image")],
            ["Camera.EmptyInput"] = [("조건을 적용한 뒤 입력을 생성하세요.", "Apply the conditions, then generate an input.")],
            ["Camera.GenerateRecipeInput"] = [("레시피로 입력 생성", "Generate via recipe")],
            ["Camera.GenerationExecutionHint"] = [("설정 적용 후 선택한 실행 대상의 레시피를 시작해 촬영 입력을 생성합니다.", "Apply the settings, then start the selected run target’s recipe to generate camera inputs.")],
            ["Camera.RecipeVersion"] = [("버전", "Version")],
            ["Camera.ConfirmVersion"] = [("현재 버전 확인", "Confirm current version")],
            ["Camera.GenerateInput"] = [("입력 생성", "Generate input")],
            ["Camera.CaptureInput"] = [("촬영 입력", "Capture input")],
            ["Camera.HeightData"] = [("높이 데이터", "Height data")],
            ["Camera.NumericalHeightLegend"] = [("높이 색상: 파랑(낮음) → 빨강(높음) · 수치 모의", "Height colors: blue (low) → red (high) · numerical mock")],
            ["Shell.Defaults"] = [("기본값", "Defaults")],
            ["Equipment.OutlineNoMatches"] = [("일치하는 부품이 없습니다.", "No matching parts")],
            ["Equipment.OutlineSearch"] = [("이름 · ID · 종류 · 유닛 검색", "Search name · ID · kind · unit")],
            ["Layout.Add.ActiveLayoutRequired"] = [("부품을 추가하기 전에 활성 레이아웃을 선택하세요", "Select an active layout before adding components")],
            ["Layout.Add.SensorTargetRequired"] = [("디지털 센서를 추가하기 전에 워크피스 또는 스테이지를 추가하세요", "Add a Workpiece or Stage before adding a Digital Sensor")],
            ["Layout.Add.WorkpieceCarrierRequired"] = [("워크피스를 추가하기 전에 컨베이어를 추가하세요", "Add a Conveyor before adding a Workpiece")],
            ["Layout.Add.InvalidDefinition"] = [("정의가 올바르지 않아 부품을 추가하지 못했습니다", "Component was not added because its definition is invalid")],
            ["Layout.Library.Conveyor.Description"] = [("운전 및 방향 출력에 따라 연결된 워크피스를 일정 속도로 이송합니다.", "Run and direction outputs move bound workpieces at fixed speed")],
            ["Layout.Library.LinearStage.Name"] = [("리니어 스테이지", "Linear Stage")],
            ["Layout.Library.Workpiece.Description"] = [("명시적으로 연결된 컨베이어가 이송하는 유형 지정 부품입니다.", "Typed part transported by an explicitly bound conveyor")],
            ["Layout.Library.Workpiece.Name"] = [("워크피스", "Workpiece")],
            ["Scene.EmptyEquipmentHint"] = [("스테이션·유닛을 만든 뒤, 선택한 유닛에 부품을 추가하세요.", "Add a station and unit, then add components to the selected unit.")],
            ["Equipment.StationUnitAddButton"] = [("스테이션·유닛 추가", "Add station / unit")],
            ["Equipment.StationUnitDialogTitle"] = [("스테이션·유닛 설정", "Station and unit setup")],
            ["Scene.EquipmentEditHint"] = [("부품 선택 → 설정·단품 확인 / 끌어서 이동", "Select a part → properties or individual check / drag to move")],
            ["Equipment.AddComponentButton"] = [("+ 부품", "+ Components")],
            ["Equipment.AddPartButton"] = [("+ 부품", "+ Part")],
            ["Equipment.OutlineUnitAutomationName"] = [("{0} / 부품 수: {1}", "{0} / component count: {1}")],
            ["Equipment.ComponentCountLabel"] = [("부품", "components")],
            ["Equipment.LibrarySearchLabel"] = [("부품 종류 검색", "Search component types")],
            ["Equipment.LibraryNoMatches"] = [("일치하는 부품이 없습니다.", "No matching components")],
            ["Equipment.LibraryUnitRequired"] = [("부품을 추가할 스테이션·유닛을 먼저 선택하거나 만드세요.", "Select or create the station and unit that will receive the component.")],
            ["Equipment.LibraryDialogTitle"] = [("부품 추가", "Add component")],
            ["Equipment.UnitRemovalMessage"] = [("'{0}' 스테이션의 '{1}' 유닛을 프로젝트에서 삭제합니다. 연결된 부품은 삭제하지 않습니다. 변경을 보존하려면 프로젝트를 저장하세요.", "Remove unit '{1}' from station '{0}'. Assigned components are never deleted. Save the project separately to keep this change.")],
            ["Equipment.UnitRemovalBlocked"] = [("'{0}' 유닛에 연결된 부품 {1}개가 있어 삭제할 수 없습니다. 다른 유닛으로 재배정하거나 미배정으로 바꾸세요.", "Cannot delete unit '{0}' while {1} assigned components remain. Reassign them or set them to Unassigned first.")],
            ["Equipment.LargeOverviewCount"] = [("{0}개 부품 / {1}개 동작", "{0} components / {1} actions")],
            ["Sequence.GraphView"] = [("동작 흐름", "Action flow")],
            ["Sequence.TableView"] = [("단계 표", "Step table")],
            ["Sequence.NewStep"] = [("새 단계", "New step")],
            ["Sequence.AddTooltip"] = [("완료 단계 앞에 새 단계를 추가합니다.", "Insert a new step before the final Complete step.")],
            ["Sequence.DeleteTooltip"] = [("선택한 종료 단계가 아닌 단계를 삭제합니다.", "Delete the selected non-terminal step.")],
            ["Sequence.Step"] = [("단계", "Step")],
            ["Sequence.Action"] = [("동작", "Action")],
            ["Sequence.WorkpieceTarget"] = [("촬영 대상 작업물", "Target workpiece")],
            ["Sequence.NoWorkpieceAssociation"] = [("작업물 연결 안 함", "No workpiece association")],
            ["Sequence.Timeout"] = [("시간 제한", "Timeout")],
            ["Sequence.StepId"] = [("단계 ID", "Step ID")],
            ["Sequence.ValidationIssueLocationFormat"] = [("시퀀스 {0} · 단계 {1} · 대상 {2} · 속성 {3}", "Sequence {0} · step {1} · target {2} · property {3}")],
            ["Sequence.ExpectedCheckpointTooltip"] = [("이 단계가 끝날 때 선택한 장비 상태를 검증합니다.", "Validate the selected equipment state when this step ends.")],
            ["Sequence.SelectSequenceHint"] = [("시퀀스를 선택해 단계를 편집하세요.", "Select a sequence to edit steps.")],
            ["Sequence.ValidSummary"] = [("유효 · {0}개 단계", "VALID · {0} steps")],
            ["Sequence.Action.MoveAxis"] = [("축 이동", "Move axis")],
            ["Sequence.Action.TriggerCamera"] = [("카메라 트리거", "Trigger camera")],
            ["Sequence.Action.WaitAxisDone"] = [("축 완료 대기", "Wait for axis")],
            ["Sequence.Action.FeedWorkpiece"] = [("작업물 투입", "Feed workpiece")],
            ["Sequence.Action.EjectWorkpiece"] = [("작업물 배출", "Eject workpiece")],
            ["Sequence.Action.Delay"] = [("시간 지연", "Delay")],
            ["Sequence.Action.FeedUnitWorkpiece"] = [("유닛 작업물 투입", "Feed unit workpiece")],
            ["Sequence.Action.ReceiveUnitWorkpiece"] = [("유닛 작업물 수신", "Receive unit workpiece")],
            ["Sequence.Template.set-output-on"] = [("출력 ON 설정", "Set output ON")],
            ["Sequence.Template.set-output-off"] = [("출력 OFF 설정", "Set output OFF")],
            ["Sequence.Template.move-axis-home"] = [("축 원점 이동", "Move axis to home"), ("원점 위치 이동", "Move axis to home")],
            ["Sequence.Template.feed-workpiece"] = [("작업물 투입", "Feed workpiece")],
            ["Sequence.Template.eject-workpiece"] = [("작업물 배출", "Eject workpiece")],
            ["Sequence.Template.delay"] = [("시간 지연 (초)", "Delay (seconds)")],
            ["Sequence.Template.wait-axis-done"] = [("축 완료 대기", "Wait for axis done")],
            ["Sequence.Template.trigger-camera"] = [("가상 카메라 트리거", "Trigger virtual camera")],
            ["Sequence.Template.feed-unit"] = [("유닛 작업물 투입", "Feed unit workpiece")],
            ["Sequence.Template.receive-unit"] = [("유닛 작업물 수신", "Receive unit workpiece")],
            ["Equipment.DriveTabHint"] = [("구동·입출력 값은 변경 즉시 반영됩니다. 표시할 항목이 없으면 별도 구동 설정이 없는 부품입니다.", "Drive and I/O changes take effect immediately. If no fields appear, this component has no separate drive settings.")],
            ["Equipment.SinglePartCheck"] = [("단품 확인", "Single-part check")],
            ["Properties.Value.LinearStage"] = [("리니어 스테이지", "Linear stage")],
            ["Properties.Workpiece"] = [("워크피스", "Workpiece")],
            ["Properties.Value.Passed"] = [("통과", "Passed")],
            ["Properties.Value.Failed"] = [("실패", "Failed")],
            ["Properties.Value.Workpiece"] = [("워크피스", "Workpiece")],
            ["Equipment.RemovalWorkpiece"] = [("작업물", "Workpiece")],
            ["Equipment.RemovalNoImpacts"] = [("참조하는 동작 연결이 없습니다.", "No sequence connections reference these components.")],
            ["Equipment.RemovalImpacts"] = [("영향받는 동작 연결", "Affected sequence connections")],
            ["Equipment.RemovalTitleSingle"] = [("선택 부품 {0}개를 삭제할까요?", "Delete {0} selected component?")],
            ["Equipment.RemovalTitle"] = [("선택 부품 {0}개를 삭제할까요?", "Delete {0} selected components?")],
            ["Equipment.RemovalWarning"] = [("부품을 삭제해도 동작과 장비 정의의 연결은 자동으로 지워지지 않습니다. 영향을 확인하고 필요한 연결을 수정하세요.", "Deleting components does not silently remove sequence or device definitions. Review and repair affected connections.")],
            ["Equipment.LinearStageMotor"] = [("리니어 스테이지 / 모터", "Linear stage / motor")],
            ["Equipment.State.Error"] = [("오류", "ERROR")],
            ["Equipment.State.Retracted"] = [("후퇴", "RETRACTED")],
            ["Equipment.State.Retracting"] = [("후퇴 중", "RETRACTING")],
            ["Equipment.AxisDriveDraftInvalid"] = [("최소·최대 범위, 원점과 양수 속도를 확인하세요.", "Check the travel range, home position, and positive speed.")],
            ["Equipment.ManualCommissioning"] = [("수동 커미셔닝", "Manual commissioning"), ("수동 커미셔닝", "MANUAL COMMISSIONING")],
            ["Equipment.StartManualControl"] = [("수동 운전 시작", "Start manual control")],
            ["Axis.SoftLimits"] = [("소프트 한계", "Soft limits")],
            ["Axis.SoftLimitMin"] = [("최소 소프트 한계", "Minimum soft limit")],
            ["Axis.SoftLimitMax"] = [("최대 소프트 한계", "Maximum soft limit")],
            ["Axis.TuningTravelInvalid"] = [("소프트 한계와 원점 위치의 범위를 확인하세요.", "Check the soft-limit and home-position range.")],
            ["Axis.Move"] = [("이동", "Move")],
            ["Axis.JogNegative"] = [("조그 −", "Jog −")],
            ["Axis.JogPositive"] = [("조그 +", "Jog +")],
            ["Axis.Home"] = [("원점", "Home")],
            ["Axis.HoldJogNegativeTooltip"] = [("누르고 있는 동안 음의 소프트 한계 방향으로 이동합니다.", "Move toward the negative soft limit while held.")],
            ["Axis.HoldJogPositiveTooltip"] = [("누르고 있는 동안 양의 소프트 한계 방향으로 이동합니다.", "Move toward the positive soft limit while held.")],
            ["Axis.NoAxisHint"] = [("커미셔닝할 축이 없습니다.", "No axis is available for commissioning.")],
            ["Axis.StartManualHint"] = [("수동 운전을 시작한 뒤 원점 또는 조그 명령을 사용하세요.", "Start manual control, then use Home or Jog.")],
            ["Axis.MoveStartManualHint"] = [("수동 운전을 시작한 뒤 목표 위치 이동, 원점 또는 조그 명령을 사용하세요.", "Start manual control, then use Move, Home, or Jog.")],
            ["Axis.RelativeMoveStartManualHint"] = [("수동 운전을 시작한 뒤 절대·상대 이동, 원점 또는 조그 명령을 사용하세요.", "Start manual control, then use absolute or relative Move, Home, or Jog.")],
            ["Axis.ManualMoveHint"] = [("절대·상대 이동을 실행하거나 조그 버튼을 누르는 동안 이동하세요.", "Use absolute or relative Move, or hold a Jog button to move.")],
            ["Axis.VelocityMoveStartManualHint"] = [("수동 운전을 시작한 뒤 절대·상대·속도 운전, 원점 또는 조그 명령을 사용하세요.", "Start manual control, then use absolute, relative, or velocity motion, Home, or Jog.")],
            ["Axis.ManualVelocityMoveHint"] = [("절대·상대·속도 운전을 실행하거나 조그 버튼을 누르는 동안 이동하세요.", "Use absolute, relative, or velocity motion, or hold a Jog button to move.")],
            ["Axis.HoldJogHint"] = [("조그 버튼은 누르는 동안만 이동하며 놓으면 정지합니다.", "A Jog button moves only while held and stops when released.")],
            ["Axis.ActionMove"] = [("목표 위치 이동", "Move to target")],
            ["Axis.ActionHome"] = [("원점 이동", "Home")],
            ["Axis.ActionJogNegative"] = [("음의 방향 조그", "Jog negative")],
            ["Axis.ActionJogPositive"] = [("양의 방향 조그", "Jog positive")],
            ["Cylinder.Commissioning"] = [("실린더 커미셔닝", "Cylinder commissioning"), ("실린더 커미셔닝", "CYLINDER COMMISSIONING")],
            ["Cylinder.Retract"] = [("후퇴", "Retract")],
            ["Cylinder.RetractTooltip"] = [("선택한 실린더의 작성된 출력으로 후퇴를 명령합니다.", "Command the selected cylinder to retract through its authored output.")],
            ["Cylinder.NoCylinderHint"] = [("커미셔닝할 실린더를 선택하세요.", "Select a cylinder to commission.")],
            ["Cylinder.StartManualHint"] = [("수동 운전을 시작한 뒤 전진 또는 후퇴 명령을 사용하세요.", "Start manual control, then use Extend or Retract.")],
            ["Cylinder.ManualRunningHint"] = [("전진·후퇴 동작은 엔진의 고정 Tick과 피드백으로 진행됩니다.", "Extend and Retract advance through fixed engine ticks and feedback.")],
            ["Cylinder.ManualPausedHint"] = [("전진 또는 후퇴를 선택한 뒤 단계 실행으로 정확히 한 Tick씩 진행하세요.", "Choose Extend or Retract, then use Step to advance exactly one tick.")],
            ["Cylinder.ClearInterlockHint"] = [("이동 차단 고장을 해제한 뒤 전진 또는 후퇴를 다시 명령하세요.", "Clear the travel fault, then explicitly command Extend or Retract again.")],
            ["Cylinder.ActionRetract"] = [("실린더 후퇴", "Retract cylinder")],
            ["Conveyor.Commissioning"] = [("컨베이어 커미셔닝", "Conveyor commissioning"), ("컨베이어 커미셔닝", "CONVEYOR COMMISSIONING")],
            ["Conveyor.NoConveyorHint"] = [("커미셔닝할 컨베이어를 선택하세요.", "Select a conveyor to commission.")],
            ["Sensor.Commissioning"] = [("센서 커미셔닝", "Sensor commissioning"), ("센서 커미셔닝", "SENSOR COMMISSIONING")],
            ["Sensor.FaultOverride"] = [("고장 Override", "Fault override")],
            ["Sensor.ClearFaultHint"] = [("Stuck-input 고장을 해제하거나 초기화한 뒤 수동 Force를 사용하세요.", "Clear the stuck-input fault or Reset before using manual force.")],
            ["Sensor.NoSensorHint"] = [("커미셔닝할 디지털 센서를 선택하세요.", "Select a digital sensor to commission.")],
            ["Properties.RetractedSensor"] = [("후퇴 센서", "Retracted sensor")],
            ["Properties.RetractDuration"] = [("후퇴 시간", "Retract duration")],
            ["Properties.RetractedSensorDelay"] = [("후퇴 센서 지연", "Retracted sensor delay")],
            ["Properties.ExtendedSensor"] = [("전진 센서", "Extended sensor")],
            ["Properties.ExtendedSensorDelay"] = [("전진 센서 지연", "Extended sensor delay")],
            ["Equipment.ManualHint.CameraReady"] = [("수동 제어가 준비됐습니다. 선택 촬영부의 촬영 명령을 실행하세요. 편집 복귀 시 실행을 종료·초기화합니다.", "Manual control is ready. Trigger the selected camera. Returning to editing ends and resets the run."), ("수동 제어가 준비됐습니다. 선택 촬영부의 촬영 명령을 실행하세요. 편집 복귀 시 운전을 종료·초기화합니다.", "Manual control is ready. Trigger the selected camera. Returning to editing ends and resets the run.")],
            ["Equipment.ManualHint.Interlock"] = [("선택 부품의 오류 또는 인터록을 해제한 뒤 다시 시도하세요.", "Clear the selected component fault or interlock before trying again.")],
            ["Equipment.ManualHint.ManualPauseRejected"] = [("수동 제어는 시작됐지만 일시정지가 거부됐습니다. 운전을 일시정지한 뒤 현재 부품을 확인하세요.", "Manual control started, but pause was rejected. Pause the run and check the current component.")],
            ["Equipment.ManualHint.MissingTarget"] = [("부품의 구동 연결과 장비 설정을 확인하고 적용한 뒤 다시 시도하세요.", "Check and apply the component drive connection and equipment settings before trying again.")],
            ["Equipment.ManualHint.Paused"] = [("수동 운전이 일시정지됐습니다. 수동 시운전 시작으로 재개하세요. 편집 복귀 시 실행을 종료·초기화합니다.", "Manual control is paused. Start manual commissioning to resume. Returning to editing ends and resets the run.")],
            ["Equipment.ManualHint.Ready"] = [("수동 시운전을 시작한 뒤 선택 부품의 명령을 실행하세요. 자동 운전·시퀀스는 시작하지 않습니다.", "Start manual commissioning, then use the selected component commands. No automatic run or sequence will start.")],
            ["Equipment.ManualHint.Rejected"] = [("수동 제어 요청이 거부됐습니다. 현재 운전 상태와 부품 연결을 확인한 뒤 다시 시도하세요.", "Manual control was rejected. Check the run state and component connection before trying again.")],
            ["Equipment.ManualHint.Running"] = [("수동 운전 중입니다. 선택 부품의 명령을 실행할 수 있습니다. 편집 복귀 시 실행을 종료·초기화합니다.", "Manual control is running. Use the selected component commands. Returning to editing ends and resets the run.")],
            ["Equipment.ManualHint.Selection"] = [("장비 화면에서 부품 하나를 선택하고 현재 편집을 마친 뒤 수동 시운전을 시작하세요.", "Select one component in Equipment and finish the current edit before starting manual commissioning.")],
            ["Equipment.ManualHint.SelectionChanged"] = [("선택이 바뀌어 수동 운전을 일시정지했습니다. 현재 부품을 확인한 뒤 다시 시작하거나 편집으로 돌아가세요.", "Manual control was paused because selection changed. Check the current component, then restart or return to editing.")],
            ["Equipment.CoordinateX"] = [("X (LU)", "X (LU)")],
            ["Equipment.CoordinateHeightY"] = [("높이 Y (LU)", "Height Y (LU)")],
            ["Equipment.CoordinateDepthZ"] = [("깊이 Z (LU)", "Depth Z (LU)")],
            ["Properties.Width"] = [("너비", "Width")],
            ["Properties.Depth"] = [("깊이 (Z)", "Depth (Z)")],
            ["Properties.VerticalHeight"] = [("수직 크기 (LU)", "Vertical size (LU)")],
            ["Inspector.VerticalEnvelopeHint"] = [("사선 장면의 개략 체적입니다. 기존 X/Y 바닥 크기는 X/Z에 대응하며 고도와 수직 크기는 LU입니다. 실측 mm 치수가 아닙니다.", "Schematic volume for the oblique view. Existing X/Y footprint maps to X/Z; elevation and vertical size use LU. These are not measured millimetres.")],
            ["Workspace.Teaching"] = [("동작·검사 티칭", "Motion & inspection teaching"), ("시뮬레이션", "Simulation")],
            ["Workspace.Execution"] = [("실행·검증", "Run & validate"), ("검사 연결", "Inspection connection")],
            ["Workspace.Results"] = [("결과·비교", "Results & comparison"), ("실행 기록", "Run records")],
            ["Integration.MmiShowInputWithSettings"] = [("입력과 함께 보기", "Show input with settings"), ("입력 넓게", "Widen input")],
            ["Integration.SetupStepConsumer"] = [("3/6 2D 소비자 버전과 40자리 source commit을 입력하세요.", "3/6 Enter the 2D consumer version and 40-character source commit.")],
            ["Integration.SetupStepSave"] = [("6/6 변경 사항을 저장한 뒤 명시적 연결 또는 Handoff를 실행하세요.", "6/6 Save the changes before an explicit connection or Handoff.")],
            ["Integration.SetupReady"] = [("설정 준비 완료. 수신 시작, 상대 Ping 또는 명시적 Handoff 중 하나를 선택하세요.", "Setup ready. Choose Start listening, Ping peer, or an explicit Handoff.")],
            ["Integration.ConsumerVersion"] = [("2D 소비자 버전", "2D consumer version")],
            ["Integration.ConsumerCommit"] = [("2D 소비자 source commit", "2D consumer source commit")],
            ["Integration.ConsumerCommitTooltip"] = [("2D 소비자 clean build의 40자리 source commit을 입력합니다.", "Enter the 40-character source commit from the 2D consumer clean build.")],
            ["Integration.ThreeDConsumerVersion"] = [("3D consumer 버전", "3D consumer version")],
            ["Integration.ThreeDConsumerCommit"] = [("3D consumer source commit", "3D consumer source commit")],
            ["Integration.TransactionHistoryFilterPendingReview"] = [("검토 대기", "Pending review")],
            ["Integration.TransactionHistoryFilterReviewed"] = [("검토됨", "Reviewed")],
            ["Integration.TransactionHistoryFilterRejected"] = [("거절됨", "Rejected")],
            ["Integration.TransactionHistoryFilterPublished"] = [("결과 게시됨", "Result published")],
            ["Integration.MmiApplyInspectionResult"] = [("검사 결과 적용", "Apply inspection result")],
            ["Integration.MmiApplyInspectionResultTooltip"] = [("현재 프로세스가 만든 Handoff와 일치하는 검증된 Result만 일시정지된 시뮬레이션에 적용합니다.", "Apply only a validated Result matching a Handoff created by this process to the paused Simulation.")],
            ["Gallery.EquipmentFocus"] = [("고유 장비", "Distinctive equipment")],
            ["Gallery.ProjectSchema"] = [("프로젝트 스키마", "Project schema")],
            ["Gallery.ValidationPending"] = [("대기", "Pending")],
            ["Gallery.Open"] = [("반도체 레시피 갤러리...", "Semiconductor recipe gallery...")],
            ["Gallery.Title"] = [("반도체 레시피 갤러리", "Semiconductor recipe gallery")],
            ["Gallery.Subtitle"] = [("장비 구성을 한눈에 비교하고 복사본을 만들어 편집하십시오.", "Compare equipment configurations at a glance, then create an editable copy.")],
            ["Gallery.CopyHint"] = [("원본 템플릿은 변경되지 않습니다.", "The original template remains unchanged.")],
            ["Gallery.SafePreview"] = [("레시피를 선택해도 시뮬레이션은 시작되지 않습니다. 복사본은 설계 모드로 열립니다.", "Selecting a recipe does not start simulation. The copy opens in Design mode.")],
            ["Gallery.ValidateAll"] = [("10개 모두 검증", "Validate all 10")],
            ["Gallery.ValidateAllTooltip"] = [("내장 레시피 10개를 순서대로 컴파일하고 격리 드라이런합니다.", "Compile and isolated-dry-run all 10 bundled recipes in order.")],
            ["Gallery.SaveCompatibilityReport"] = [("호환성 보고서 저장", "Save compatibility report")],
            ["Gallery.ValidationFailedSummaryFormat"] = [("{0}개 실패 · 첫 실패: {1} · 단계 {2} · {3}", "{0} failed · First failure: {1} · Step {2} · {3}")],
            ["Gallery.ValidationSequenceMissing"] = [("자동 실행 시퀀스가 구성되지 않았습니다.", "No automatic-run sequence is configured.")],
            ["Gallery.SaveCompatibilityReportTooltip"] = [("각 레시피의 스키마, 검증 결과, 실행 빌드와 커밋을 JSON으로 저장합니다.", "Save each recipe schema, validation result, executable build, and commit as JSON.")],
            ["Gallery.Comparison.SafeHint"] = [("보고서 비교는 프로젝트를 열거나 레시피를 실행·저장하지 않습니다.", "Comparing reports does not open a project or run or save a recipe.")],
            ["Gallery.TopologySummaryCompact"] = [("축 {0} · 센서 {1} · 실린더 {2} · 컨베이어 {3} · 워크 {4}", "Axes {0} · Sensors {1} · Cyl {2} · Conv {3} · Wafers {4}")],
            ["Gallery.Steps"] = [("시퀀스 단계", "Sequence steps")],
            ["Shell.New"] = [("새 프로젝트", "New")],
            ["Shell.NewTooltip"] = [("새 프로젝트를 만듭니다. (Ctrl+N)", "Create a new project (Ctrl+N)")],
            ["Shell.OpenTooltip"] = [("프로젝트를 엽니다. (Ctrl+O)", "Open a project (Ctrl+O)")],
            ["Shell.SaveTooltip"] = [("프로젝트를 저장합니다. (Ctrl+S)", "Save the project (Ctrl+S)")],
            ["Shell.SimulationOn"] = [("시뮬레이션 시작", "Simulation ON")],
            ["Shell.SimulationOnTooltip"] = [("작성한 머신을 빌드하고 자동 시뮬레이션을 시작합니다. (F5)", "Build the authored machine and start automatic simulation (F5)")],
            ["Shell.ContinueRun"] = [("계속 운전", "Continue run")],
            ["Shell.ContinueRunTooltip"] = [("현재 실행을 초기화하지 않고 이어서 실행합니다. (F5)", "Continue the current run without resetting it (F5)")],
            ["Shell.AbortSequence"] = [("시퀀스 중단", "Abort sequence")],
            ["Shell.AbortSequenceTooltip"] = [("활성 시퀀스를 중단합니다. Reset 후 다시 시작할 수 있습니다.", "Abort the active sequence. Reset is required before starting again.")],
            ["Shell.SequenceAbortedStatus"] = [("시퀀스가 중단되었습니다. Reset 후 다시 시작할 수 있습니다.", "The sequence was aborted. Reset is required before starting again.")],
            ["Shell.SequenceAbortRejectedStatus"] = [("시퀀스 중단이 거부되었습니다.", "Sequence abort was rejected.")],
            ["Shell.RetrySequenceTooltip"] = [("오류 원인을 해제한 뒤 시퀀스를 처음부터 재시도합니다. 자동 반복은 다시 시작되지 않습니다.", "Retry the faulted sequence from its entry step after clearing the cause. Automatic continuation remains stopped.")],
            ["Shell.Step"] = [("단계 실행", "Step")],
            ["Shell.SingleTick"] = [("1 Tick 진행", "Advance one tick")],
            ["Shell.StepTooltip"] = [("5 ms 단위로 한 Tick 진행합니다. (F10)", "Advance one 5 ms tick (F10)")],
            ["Shell.Runtime"] = [("실행 상태", "Runtime")],
            ["Shell.ProjectStatus"] = [("프로젝트: {0}", "Project: {0}")],
            ["Shell.ProjectStatusUnsaved"] = [("프로젝트: {0} · 미저장", "Project: {0} · Unsaved")],
            ["Project.UnsavedTitle"] = [("저장하지 않은 프로젝트", "Unsaved project")],
            ["Project.UnsavedMessage"] = [("현재 프로젝트의 변경 내용을 저장하시겠습니까?", "Save changes to the current project?")],
            ["Project.NewRecipeEmptyHint"] = [("빈 장비에서 스테이션과 부품을 추가합니다.", "Start from an empty machine and add stations and components.")],
            ["Project.NewRecipeNameRequired"] = [("레시피 이름을 입력하세요.", "Enter a recipe name.")],
            ["Project.NewRecipeNameTooLong"] = [("레시피 이름은 60자 이하여야 합니다.", "Recipe name must be 60 characters or fewer.")],
            ["Shell.ControlOwnerAutomatic"] = [("시뮬레이션 시작 시 자동 시퀀스를 시작하고 반복합니다.", "Simulation ON starts and repeats the authored automatic sequence.")],
            ["Shell.AutomaticRunRunning"] = [("자동 사이클 실행 중", "Automatic cycle running")],
            ["Shell.RunTarget"] = [("전체 장비 실행: {0} / {1}", "Whole-machine run: {0} / {1}")],
            ["Shell.Running"] = [("실행 중", "RUNNING")],
            ["Shell.FixedStep"] = [("고정 5 ms", "Fixed 5 ms")],
            ["Debugger.NextSequenceStep"] = [("다음 시퀀스 단계", "Next sequence step")],
            ["RecipeEntry.Title"] = [("레시피로 시작하기", "Start with a recipe")],
            ["RecipeEntry.New"] = [("새 레시피 만들기", "Create a new recipe")],
            ["RecipeEntry.NewDescription"] = [("빈 레시피에서 가상 장비 구성을 시작합니다.", "Start a virtual machine layout from a blank recipe.")],
            ["RecipeEntry.Open"] = [("레시피 열기", "Open a recipe")],
            ["RecipeEntry.InspectionTitle"] = [("같은 작업물 2D·높이 판정 흐름 (모의)", "Same-workpiece 2D and height-verdict flow (mock)")],
            ["RecipeEntry.InspectionDescription"] = [("상면 검사 뒤 고정 모의 높이 판정을 진행합니다. 입력 이미지와 수치 HeightMap은 생성하지 않습니다.", "Runs a fixed mock height verdict after top inspection. No input image or numeric HeightMap is generated.")],
            ["RecipeEntry.LargeTitle"] = [("500개 부품 탐색", "Explore 500 components")],
            ["RecipeEntry.LargeHint"] = [("탐색용 예제입니다. 이 규모의 실행 성능은 검증되지 않았습니다.", "Exploration only. Runtime performance at this scale has not been verified.")],
            ["Workspace.EndAndEdit"] = [("종료 후 편집", "End run and edit")],
            ["Workspace.EndAndEditHint"] = [("실행을 끝내고 초기 상태로 되돌린 뒤 편집합니다. 일시정지 후 계속하기와 다릅니다.", "End and reset the run before editing. This is different from pause and resume.")],
            ["Workspace.SaveSettings"] = [("설정 저장", "Save settings")],
            ["Equipment.FooterStatus"] = [("설정 r{0} / {1}개 선택", "Settings r{0} / {1} selected")],
            ["Equipment.SimulationOnly"] = [("모의 동작 / 실제 장비 제어 없음", "Simulation only / no physical device control")],
            ["Workspace.SessionRecordsHint"] = [("최근 실행 이벤트를 확인하고 오류 위치로 이동하세요. 현재 세션의 최대 200개 이벤트를 표시하며 재시작 후 복원하지 않습니다.", "Review recent runtime events and navigate to their error location. Shows up to 200 events from the current session; they are not restored after restart.")],
            ["Workspace.NavigateRuntimeError"] = [("오류 위치로 이동", "Go to error location")],
            ["Debugger.NextSequenceStepTooltip"] = [("일시정지된 실행을 정확히 다음 시퀀스 전이 경계까지 진행합니다.", "Advance a paused run to exactly the next sequence transition boundary.")],
            ["Diagnostics.Toggle"] = [("실행 기록 펼치기 또는 접기", "Expand or collapse run records")],
            ["Diagnostics.Resize"] = [("실행 기록 높이 조절", "Resize run records")],
            ["Connections.InputOverview"] = [("촬영 입력 정보", "Capture input information")],
            ["Connections.ConnectionOverview"] = [("검사 연결 요약", "Inspection connection summary")],
            ["Scene.LegendCamera"] = [("▣ 촬영부", "▣ Camera")],
            ["Camera.SourceWidth"] = [("폭 (px)", "Width (px)")],
            ["Camera.SourceHeight"] = [("높이 (px)", "Height (px)")],
            ["Camera.ApplySource"] = [("설정 적용", "Apply settings")],
            ["Camera.ApplySourceTooltip"] = [("검증된 설정을 프로젝트에 적용합니다. 획득은 자동으로 시작하지 않습니다.", "Apply validated settings to the project without starting acquisition.")],
            ["Camera.SourceDraftReady"] = [("유효한 변경입니다. 설정 적용을 선택하세요.", "Valid changes. Select Apply settings.")],
            ["Camera.WorkpieceAssociation"] = [("연결 작업물 ID", "Associated workpiece ID")],
            ["Camera.WorkpieceInstanceId"] = [("실행 작업물 ID", "Workpiece instance ID")],
            ["Integration.Hint"] = [("교환 설정을 저장한 뒤 Handoff 게시, Result 새로고침, 시뮬레이션 적용을 각각 명시적으로 실행합니다.", "Save the exchange setup, then explicitly publish the Handoff, refresh Result, and apply it to Simulation.")],
            ["Integration.WaitForExternalResult"] = [("카메라 전송 후 외부 Result 대기", "Wait for external Result after camera transfer")],
            ["Integration.WaitForExternalResultCompactLabel"] = [("Result 대기", "Wait for Result")],
            ["Integration.PublishTwoD"] = [("2D/Image 게시", "Publish 2D/Image")],
            ["Integration.PublishTwoDShort"] = [("2D/Image 게시", "Publish 2D/Image")],
            ["Integration.RefreshResults"] = [("Result 새로고침", "Refresh Result")],
            ["Sequence.MoveUp"] = [("위로 이동", "Move up")],
            ["Sequence.MoveDown"] = [("아래로 이동", "Move down")],
            ["Equipment.InspectorDraftLeaveMessage"] = [("설정 변경을 적용하거나 버린 뒤 계속하세요. 취소하면 현재 화면에서 편집을 계속합니다.", "Apply or discard settings changes before continuing. Cancel keeps this editor open.")],
            ["Equipment.PlacementDraftDiscard"] = [("변경 버리기", "Discard changes")],
            ["Camera.CurrentInputs"] = [("현재 입력", "Current inputs")],
            ["Camera.InputRecordedStep"] = [("기록된 촬영 단계: {0}", "Recorded capture step: {0}")],
            ["Camera.InputUnrecordedStep"] = [("촬영 단계 미기록", "Capture step not recorded")],
            ["Camera.InputContext"] = [("작업물: {0} · 레시피: {1}", "Workpiece: {0} · recipe: {1}")],
            ["Camera.Capture.Parameter.Tilt"] = [("기울기", "Tilt")],
            ["Camera.Capture.Parameter.Shake"] = [("흔들림", "Shake")],
            ["Camera.Capture.Parameter.Light"] = [("조명", "Light")],
            ["Camera.Capture.Loaded"] = [("촬영 설정을 불러왔습니다. 실행은 별도 버튼입니다.", "Capture settings loaded. Start acquisition separately.")],
            ["Camera.Capture.AppliedNeedsSave"] = [("촬영 설정 적용 완료 · 프로젝트 저장 필요 · 실행은 별도 버튼입니다.", "Capture settings applied · save the project · start acquisition separately.")],
            ["Camera.Capture.AppliedSaved"] = [("적용한 촬영 설정을 프로젝트에 저장했습니다. 실행은 별도 버튼입니다.", "Capture settings saved to the project. Start acquisition separately.")],
            ["Shell.ProjectRevision"] = [("설정 r{0}", "Settings r{0}")],
        };
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, OpenVisionLocalizationEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static bool loaded;

    public static event EventHandler? LanguageChanged;

    public static OpenVisionLanguage CurrentLanguage { get; private set; } = OpenVisionLanguage.Korean;

    public static IReadOnlyList<OpenVisionLanguageOption> LanguageOptions { get; } =
    [
        new(OpenVisionLanguage.Korean, "한국어"),
        new(OpenVisionLanguage.English, "English")
    ];

    public static string CatalogPath => Path.Combine(GetConfigDirectory(), CatalogFileName);

    public static void Load()
    {
        lock (SyncRoot)
        {
            if (loaded)
            {
                return;
            }

            EnsureCatalogFile();
            LoadCatalog();
            LoadLanguage();
            loaded = true;
        }
    }

    public static void SetLanguage(OpenVisionLanguage language, bool save = true)
    {
        EnsureLoaded();
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        if (save)
        {
            SaveLanguage(language);
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) => T(key, key, key);

    public static string T(string key, string korean, string english)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        EnsureLoaded();
        lock (SyncRoot)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                var localized = CurrentLanguage == OpenVisionLanguage.English
                    ? entry.English
                    : entry.Korean;
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    return localized;
                }
            }

            return CurrentLanguage == OpenVisionLanguage.English
                ? english
                : korean;
        }
    }

    /// <summary>
    /// Resolves project-authored text by stable domain/id while preserving the
    /// authored source text when no catalog entry exists.
    /// </summary>
    public static string TUserText(string scope, string id, string fallback)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(id))
        {
            return fallback ?? string.Empty;
        }

        string source = fallback ?? string.Empty;
        return T($"UserText.{scope}.{id}", source, source);
    }

    private static void EnsureLoaded()
    {
        if (!loaded)
        {
            Load();
        }
    }

    private static void EnsureCatalogFile() => EnsureCatalogFile(CatalogPath, ReadEmbeddedCatalog());

    internal static void EnsureCatalogFile(string catalogPath, string embeddedCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(embeddedCatalog);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!);
        var defaults = ParseCatalog(embeddedCatalog)
            .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(catalogPath))
        {
            File.WriteAllText(catalogPath, BuildCatalog(defaults.Values), Encoding.UTF8);
            return;
        }

        var current = ParseCatalog(File.ReadAllText(catalogPath, Encoding.UTF8))
            .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var entry in defaults.Values)
        {
            if (current.TryGetValue(entry.Key, out var existing))
            {
                if (LegacyWorkspaceDefaults.TryGetValue(entry.Key, out var previousDefaults)
                    && previousDefaults.Any(previous => existing.Korean == previous.Korean && existing.English == previous.English))
                {
                    current[entry.Key] = entry;
                    changed = true;
                }
                continue;
            }

            current[entry.Key] = entry;
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(catalogPath, BuildCatalog(current.Values), Encoding.UTF8);
        }
    }

    private static string BuildCatalog(IEnumerable<OpenVisionLocalizationEntry> entries)
    {
        var builder = new StringBuilder("Key\tKorean\tEnglish\r\n");
        foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(entry.Key)
                .Append('\t')
                .Append(entry.Korean)
                .Append('\t')
                .Append(entry.English)
                .Append("\r\n");
        }

        return builder.ToString();
    }

    private static void LoadCatalog()
    {
        var catalog = File.Exists(CatalogPath)
            ? File.ReadAllText(CatalogPath, Encoding.UTF8)
            : ReadEmbeddedCatalog();

        Entries.Clear();
        foreach (var entry in ParseCatalog(catalog))
        {
            Entries[entry.Key] = entry;
        }
    }

    private static IEnumerable<OpenVisionLocalizationEntry> ParseCatalog(string catalog)
    {
        foreach (var line in catalog.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            yield return new OpenVisionLocalizationEntry(
                parts[0].Trim(),
                parts.Length > 1 ? parts[1].Trim() : string.Empty,
                parts.Length > 2 ? parts[2].Trim() : string.Empty);
        }
    }

    private static string ReadEmbeddedCatalog()
    {
        var resourceName = typeof(OpenVisionLanguageService).Assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return "Key\tKorean\tEnglish\r\n";
        }

        using var stream = typeof(OpenVisionLanguageService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Localization catalog resource was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void LoadLanguage()
    {
        CurrentLanguage = OpenVisionLanguage.Korean;
        try
        {
            var path = Path.Combine(GetConfigDirectory(), LanguageFileName);
            if (!File.Exists(path))
            {
                return;
            }

            var value = File.ReadAllText(path).Trim();
            CurrentLanguage = value.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("english", StringComparison.OrdinalIgnoreCase)
                ? OpenVisionLanguage.English
                : OpenVisionLanguage.Korean;
        }
        catch
        {
            CurrentLanguage = OpenVisionLanguage.Korean;
        }
    }

    private static void SaveLanguage(OpenVisionLanguage language)
    {
        try
        {
            Directory.CreateDirectory(GetConfigDirectory());
            File.WriteAllText(
                Path.Combine(GetConfigDirectory(), LanguageFileName),
                language == OpenVisionLanguage.English ? "en" : "ko",
                Encoding.UTF8);
        }
        catch
        {
            // Language changes remain active for the current process when persistence is unavailable.
        }
    }

    private static string GetConfigDirectory()
    {
        var smokeConfigDirectory = Environment.GetEnvironmentVariable(
            "OPENVISIONLAB_MACHINE_STUDIO_SMOKE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(smokeConfigDirectory)
            ? Path.GetFullPath(smokeConfigDirectory)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                CompanyDirectoryName,
                ProductDirectoryName,
                ConfigDirectoryName);
    }

    private sealed record OpenVisionLocalizationEntry(string Key, string Korean, string English);
}
