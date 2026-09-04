using Microsoft.Win32;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.View.Dialogs;

/// <summary>
/// Owns native file-dialog creation for simulation evidence and command traces.
/// It returns a selected path and does not perform application I/O.
/// </summary>
internal sealed class SimulationEvidenceFileDialogHost
{
    internal string? SelectSimulationEvidenceExport(string projectDisplayName)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".ovsim-evidence.json",
            Filter = "Machine Studio evidence (*.ovsim-evidence.json)|*.ovsim-evidence.json|JSON (*.json)|*.json",
            FileName = $"{projectDisplayName}-simulation-evidence.ovsim-evidence.json",
            OverwritePrompt = true,
            Title = OpenVisionLanguageService.T(
                "Simulation.ExportEvidence",
                "증거 내보내기",
                "Export evidence")
        };
        return Show(dialog);
    }

    internal string? SelectSimulationEvidenceImport()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Machine Studio evidence (*.ovsim-evidence.json)|*.ovsim-evidence.json|JSON (*.json)|*.json",
            Multiselect = false,
            Title = OpenVisionLanguageService.T(
                "Simulation.ImportEvidence",
                "증거 가져오기",
                "Import evidence")
        };
        return Show(dialog);
    }

    internal string? SelectCommandTraceExport(string projectDisplayName)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".ovsim-trace.json",
            Filter = "Machine Studio command trace (*.ovsim-trace.json)|*.ovsim-trace.json|JSON (*.json)|*.json",
            FileName = $"{projectDisplayName}-command-trace.ovsim-trace.json",
            OverwritePrompt = true,
            Title = OpenVisionLanguageService.T(
                "Simulation.ExportCommandTrace",
                "명령 trace 내보내기",
                "Export command trace")
        };
        return Show(dialog);
    }

    internal string? SelectCommandTraceReplay()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Machine Studio command trace (*.ovsim-trace.json)|*.ovsim-trace.json|JSON (*.json)|*.json",
            Multiselect = false,
            Title = OpenVisionLanguageService.T(
                "Simulation.ReplayCommandTrace",
                "명령 trace 재생",
                "Replay command trace")
        };
        return Show(dialog);
    }

    internal string? SelectUnifiedEvidenceExport(string projectDisplayName)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".ovsim-commissioning.json",
            Filter = "Machine Studio commissioning evidence (*.ovsim-commissioning.json)|*.ovsim-commissioning.json|JSON (*.json)|*.json",
            FileName = $"{projectDisplayName}-commissioning-evidence.ovsim-commissioning.json",
            OverwritePrompt = true,
            Title = OpenVisionLanguageService.T(
                "Simulation.ExportUnifiedCommissioningEvidence",
                "커미셔닝 증거 내보내기",
                "Export commissioning evidence")
        };
        return Show(dialog);
    }

    internal string? SelectUnifiedEvidenceImport()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Machine Studio commissioning evidence (*.ovsim-commissioning.json)|*.ovsim-commissioning.json|JSON (*.json)|*.json",
            Multiselect = false,
            Title = OpenVisionLanguageService.T(
                "Simulation.ImportUnifiedCommissioningEvidence",
                "커미셔닝 증거 가져오기",
                "Import commissioning evidence")
        };
        return Show(dialog);
    }

    private static string? Show(FileDialog dialog) =>
        dialog.ShowDialog() == true ? dialog.FileName : null;
}
