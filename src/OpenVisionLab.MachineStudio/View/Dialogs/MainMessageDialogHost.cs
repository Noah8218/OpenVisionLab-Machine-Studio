using System.Globalization;
using System.Windows;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.Wpf.MessageDialogs;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns shell workflow message dialogs and their WPF owner boundary.
/// </summary>
internal sealed class MainMessageDialogHost
{
    internal UnsavedProjectDecision ShowUnsavedProjectPrompt()
    {
        var result = Show(CreateUnsavedProjectDialogOptions());
        return result switch
        {
            WpfMessageDialogResult.Yes => UnsavedProjectDecision.Save,
            WpfMessageDialogResult.No => UnsavedProjectDecision.Discard,
            _ => UnsavedProjectDecision.Cancel
        };
    }

    internal void ShowProjectOpenFailure(string details) => Show(CreateProjectOpenFailureDialogOptions(details));

    internal void ShowProjectSaveFailure(string details) => Show(CreateProjectSaveFailureDialogOptions(details));

    internal PlacementDraftDecision ShowPlacementDraftPrompt() => Show(CreatePlacementDraftDialogOptions()) switch
    {
        WpfMessageDialogResult.Yes => PlacementDraftDecision.Apply,
        WpfMessageDialogResult.No => PlacementDraftDecision.Discard,
        _ => PlacementDraftDecision.Cancel
    };

    internal static WpfMessageDialogOptions CreatePlacementDraftDialogOptions() => new()
    {
        Title = OpenVisionLanguageService.T("Equipment.InspectorDraftTitle", "적용하지 않은 설정 변경", "Unapplied settings changes"),
        Message = OpenVisionLanguageService.T(
            "Equipment.InspectorDraftLeaveMessage",
            "설정 변경을 적용하거나 버린 뒤 계속하세요. 취소하면 현재 화면에서 편집을 계속합니다.",
            "Apply or discard settings changes before continuing. Cancel keeps this editor open."),
        Kind = WpfMessageDialogKind.Question,
        DefaultResult = WpfMessageDialogResult.Cancel,
        PrimaryButtonText = OpenVisionLanguageService.T("Equipment.PlacementDraftApply", "변경 적용", "Apply changes"),
        SecondaryButtonText = OpenVisionLanguageService.T("Equipment.PlacementDraftDiscard", "변경 버리기", "Discard changes"),
        TertiaryButtonText = OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel")
    };

    internal bool ConfirmLayoutComponentRemoval(
        IReadOnlyList<LayoutComponentDefinition> components,
        IReadOnlyList<LayoutComponentRemovalImpact> impacts) =>
        Show(CreateLayoutRemovalDialogOptions(components, impacts)) == WpfMessageDialogResult.Yes;

    internal bool ConfirmEquipmentUnitRemoval(MachineStationDefinition station, MachineUnitDefinition unit) =>
        Show(CreateEquipmentUnitRemovalDialogOptions(station, unit)) == WpfMessageDialogResult.Yes;

    internal bool ConfirmEquipmentStationRemoval(MachineStationDefinition station) =>
        Show(CreateEquipmentStationRemovalDialogOptions(station)) == WpfMessageDialogResult.Yes;

    internal static WpfMessageDialogOptions CreateEquipmentUnitRemovalDialogOptions(
        MachineStationDefinition station,
        MachineUnitDefinition unit)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(unit);

        return new WpfMessageDialogOptions
        {
            Title = string.Format(CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Equipment.UnitRemovalTitle"), unit.Name),
            Message = string.Format(CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Equipment.UnitRemovalMessage"), station.Name, unit.Name),
            Kind = WpfMessageDialogKind.Warning,
            DefaultResult = WpfMessageDialogResult.No,
            PrimaryButtonText = OpenVisionLanguageService.T("Shell.Delete", "삭제", "Delete"),
            SecondaryButtonText = OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel")
        };
    }

    internal static WpfMessageDialogOptions CreateEquipmentStationRemovalDialogOptions(
        MachineStationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);

        return new WpfMessageDialogOptions
        {
            Title = string.Format(CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Equipment.StationRemovalTitle"), station.Name),
            Message = string.Format(CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Equipment.StationRemovalMessage"), station.Name),
            Kind = WpfMessageDialogKind.Warning,
            DefaultResult = WpfMessageDialogResult.No,
            PrimaryButtonText = OpenVisionLanguageService.T("Shell.Delete", "삭제", "Delete"),
            SecondaryButtonText = OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel")
        };
    }

    internal static WpfMessageDialogOptions CreateLayoutRemovalDialogOptions(
        IReadOnlyList<LayoutComponentDefinition> components,
        IReadOnlyList<LayoutComponentRemovalImpact> impacts)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(impacts);

        var componentNames = components.ToDictionary(component => component.Id, component => component.Name, StringComparer.Ordinal);
        var impactLines = impacts.Select((impact, index) =>
        {
            var role = impact.ReferenceKind switch
            {
                LayoutComponentRemovalReferenceKind.Workpiece => OpenVisionLanguageService.T(
                    "Equipment.RemovalWorkpiece", "작업물", "Workpiece"),
                LayoutComponentRemovalReferenceKind.ExpectedTarget => OpenVisionLanguageService.T(
                    "Equipment.RemovalExpectedTarget", "기대 대상", "Expected target"),
                _ => OpenVisionLanguageService.T("Equipment.RemovalTarget", "대상", "Target")
            };
            var name = componentNames.GetValueOrDefault(impact.ComponentId, impact.ComponentId);
            return $"{index + 1}. {impact.SequenceName} / {impact.StepName} / {role}: {name} ({impact.ComponentId})";
        });
        var impactText = impacts.Count == 0
            ? OpenVisionLanguageService.T("Equipment.RemovalNoImpacts", "참조하는 동작 연결이 없습니다.", "No sequence connections reference these components.")
            : OpenVisionLanguageService.T("Equipment.RemovalImpacts", "영향받는 동작 연결", "Affected sequence connections")
                + $" ({impacts.Count}){Environment.NewLine}{string.Join(Environment.NewLine, impactLines)}";

        return new WpfMessageDialogOptions
        {
            Title = string.Format(CultureInfo.CurrentCulture,
                components.Count == 1
                    ? OpenVisionLanguageService.T("Equipment.RemovalTitleSingle", "선택 부품 {0}개를 삭제할까요?", "Delete {0} selected component?")
                    : OpenVisionLanguageService.T("Equipment.RemovalTitle", "선택 부품 {0}개를 삭제할까요?", "Delete {0} selected components?"),
                components.Count),
            Message = OpenVisionLanguageService.T(
                "Equipment.RemovalWarning",
                "부품을 삭제해도 동작과 장비 정의의 연결은 자동으로 지워지지 않습니다. 영향을 확인하고 필요한 연결을 수정하세요.",
                "Deleting components does not silently remove sequence or device definitions. Review and repair affected connections.")
                + $"{Environment.NewLine}{Environment.NewLine}{impactText}",
            Kind = WpfMessageDialogKind.Warning,
            DefaultResult = WpfMessageDialogResult.No,
            PrimaryButtonText = OpenVisionLanguageService.T("Shell.Delete", "삭제", "Delete"),
            SecondaryButtonText = OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel")
        };
    }

    internal static WpfMessageDialogOptions CreateUnsavedProjectDialogOptions() => new()
    {
        Title = OpenVisionLanguageService.T(
            "Project.UnsavedTitle",
            "저장하지 않은 프로젝트",
            "Unsaved project"),
        Message = OpenVisionLanguageService.T(
            "Project.UnsavedMessage",
            "현재 프로젝트의 변경 내용을 저장하시겠습니까?",
            "Save changes to the current project?"),
        Kind = WpfMessageDialogKind.Question,
        DefaultResult = WpfMessageDialogResult.Yes,
        PrimaryButtonText = OpenVisionLanguageService.T("Project.Save", "저장", "Save"),
        SecondaryButtonText = OpenVisionLanguageService.T(
            "Project.DontSave",
            "저장 안 함",
            "Don't save"),
        TertiaryButtonText = OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel")
    };

    internal static WpfMessageDialogOptions CreateProjectOpenFailureDialogOptions(string details) => new()
    {
        Title = OpenVisionLanguageService.T(
            "Project.OpenFailedTitle",
            "프로젝트 열기 실패",
            "Project open failed"),
        Message = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Project.OpenFailedMessage",
                "프로젝트 파일을 열지 못했습니다. 현재 프로젝트는 그대로 유지됩니다.{0}{0}{1}",
                "The project file could not be opened. The current project remains unchanged.{0}{0}{1}"),
            Environment.NewLine,
            details),
        Kind = WpfMessageDialogKind.Warning,
        DefaultResult = WpfMessageDialogResult.OK,
        PrimaryButtonText = OpenVisionLanguageService.T(
            "MessageBox.OK",
            "확인",
            "OK")
    };

    internal static WpfMessageDialogOptions CreateProjectSaveFailureDialogOptions(string details) => new()
    {
        Title = OpenVisionLanguageService.T(
            "Project.SaveFailedTitle",
            "프로젝트 저장 실패",
            "Project save failed"),
        Message = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Project.SaveFailedMessage",
                "프로젝트 파일을 안전하게 저장하지 못했습니다.{0}{0}{1}",
                "The project file could not be saved safely.{0}{0}{1}"),
            Environment.NewLine,
            details),
        Kind = WpfMessageDialogKind.Warning,
        DefaultResult = WpfMessageDialogResult.OK,
        PrimaryButtonText = OpenVisionLanguageService.T(
            "MessageBox.OK",
            "확인",
            "OK")
    };

    private static WpfMessageDialogResult Show(WpfMessageDialogOptions options) =>
        WpfMessageDialog.Show(Application.Current?.MainWindow, options);
}
