using System.IO;
using Microsoft.Win32;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns native path selection for the Machine Studio integration setup.
/// It returns selected paths and does not save settings or start integration work.
/// </summary>
internal sealed class MachineIntegrationFileDialogHost
{
    internal static string? SelectExchangeRoot(string currentPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Localize(
                "Machine Studio와 소비자가 공유할 교환 폴더 선택",
                "Choose the exchange folder shared by Machine Studio and the consumer"),
            InitialDirectory = Directory.Exists(currentPath) ? currentPath : null
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    internal static string? SelectInspectionRecipe(string currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = Localize("2D 소비자 검사 레시피 선택", "Choose the 2D consumer inspection recipe"),
            Filter = "Inspection recipes (*.xml;*.json;*.ovrecipe)|*.xml;*.json;*.ovrecipe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = File.Exists(currentPath)
                ? Path.GetDirectoryName(Path.GetFullPath(currentPath))
                : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string Localize(string korean, string english) =>
        OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.English ? english : korean;
}
