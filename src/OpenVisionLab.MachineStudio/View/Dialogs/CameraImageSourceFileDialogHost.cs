using Microsoft.Win32;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns native image-source file-dialog creation and returns only the selected path.
/// </summary>
internal sealed class CameraImageSourceFileDialogHost
{
    internal static string? SelectSourceFile(string projectRoot)
    {
        var dialog = new OpenFileDialog
        {
            Title = OpenVisionLanguageService.T("Camera.SelectSourceFile"),
            Filter = "Image files|*.bmp;*.jpg;*.jpeg;*.png;*.pgm;*.ppm;*.tif;*.tiff;*.raw|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = projectRoot
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
