using Microsoft.Win32;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns native compatibility-report path selection and returns only selected paths.
/// Report creation, persistence, loading, and comparison remain with the gallery workflow.
/// </summary>
internal sealed class SemiconductorRecipeCompatibilityDialogHost
{
    internal static string? SelectSavePath()
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json",
            FileName = $"OpenVisionLab-MachineStudio-recipe-pack-compatibility-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = OpenVisionLanguageService.T("Gallery.SaveCompatibilityReport")
        };
        return Show(dialog);
    }

    internal static string? SelectBaselinePath() => SelectOpenPath(
        OpenVisionLanguageService.T("Gallery.CompareBaselineReport"));

    internal static string? SelectCurrentPath() => SelectOpenPath(
        OpenVisionLanguageService.T("Gallery.CompareCurrentReport"));

    private static string? SelectOpenPath(string title)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json",
            Multiselect = false,
            Title = title
        };
        return Show(dialog);
    }

    private static string? Show(FileDialog dialog) => dialog.ShowDialog() == true ? dialog.FileName : null;
}
