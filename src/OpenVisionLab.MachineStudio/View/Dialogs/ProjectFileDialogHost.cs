using Microsoft.Win32;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns native project-file dialog creation and returns only the selected path.
/// </summary>
internal sealed class ProjectFileDialogHost
{
    internal string? SelectProjectToOpen()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Machine projects (*.ovmachine)|*.ovmachine|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return Show(dialog);
    }

    internal string? SelectProjectSaveAs(string? projectName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Machine projects (*.ovmachine)|*.ovmachine|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(projectName)
                ? "machine-project.ovmachine"
                : $"{projectName}.ovmachine"
        };
        return Show(dialog);
    }

    internal string? SelectRecipeCopyDestination(string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Machine projects (*.ovmachine)|*.ovmachine|All files (*.*)|*.*",
            FileName = fileName,
            DefaultExt = ".ovmachine",
            AddExtension = true,
            OverwritePrompt = true
        };
        return Show(dialog);
    }

    private static string? Show(FileDialog dialog) => dialog.ShowDialog() == true ? dialog.FileName : null;
}
