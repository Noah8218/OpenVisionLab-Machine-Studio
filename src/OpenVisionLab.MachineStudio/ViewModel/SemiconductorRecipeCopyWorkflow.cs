using System.IO;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the file-backed creation of a new project from a semiconductor recipe.
/// Dialogs, unsaved-change policy, project activation, and presentation remain
/// with the shell.
/// </summary>
internal sealed class SemiconductorRecipeCopyWorkflow
{
    private readonly ProjectDocumentStore _projectStore;
    private readonly Func<string> _getOverwriteRejectedMessage;

    internal SemiconductorRecipeCopyWorkflow(
        ProjectDocumentStore projectStore,
        Func<string> getOverwriteRejectedMessage)
    {
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _getOverwriteRejectedMessage = getOverwriteRejectedMessage
            ?? throw new ArgumentNullException(nameof(getOverwriteRejectedMessage));
    }

    internal async Task<string> CopyAsync(string sourcePath, string destinationPath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(
                fullSourcePath,
                fullDestinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(_getOverwriteRejectedMessage());
        }

        var project = await _projectStore.LoadAsync(fullSourcePath);
        var now = DateTimeOffset.UtcNow;
        project.Id = Guid.NewGuid().ToString("n");
        project.CreatedAt = now;
        project.ModifiedAt = now;
        await _projectStore.SaveAsync(project, fullDestinationPath);
        return fullDestinationPath;
    }
}
