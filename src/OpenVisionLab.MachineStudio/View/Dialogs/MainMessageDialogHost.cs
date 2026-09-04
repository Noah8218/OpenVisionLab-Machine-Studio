using System.Globalization;
using System.Windows;
using OpenVisionLab;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.Wpf.MessageDialogs;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns the project workflow message dialogs and their WPF owner boundary.
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
