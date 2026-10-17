using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns native project-file dialog creation and returns only the selected path.
/// </summary>
internal sealed class ProjectFileDialogHost
{
    private readonly Func<FileDialog, Window?, bool?> _showDialog;

    internal ProjectFileDialogHost(Func<FileDialog, Window?, bool?>? showDialog = null)
    {
        _showDialog = showDialog ?? ((dialog, owner) => owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner));
    }

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

    private string? Show(FileDialog dialog)
    {
        var previousFocus = Keyboard.FocusedElement as UIElement;
        var owner = previousFocus is null ? null : Window.GetWindow(previousFocus);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        try
        {
            return _showDialog(dialog, owner) == true ? dialog.FileName : null;
        }
        finally
        {
            if (owner is { IsVisible: true })
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (owner.IsVisible && owner.IsActive)
                    {
                        if (previousFocus is { IsVisible: true, IsEnabled: true }) previousFocus.Focus();
                        else owner.Focus();
                    }
                }));
        }
    }
}
