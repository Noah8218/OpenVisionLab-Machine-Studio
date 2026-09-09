using System.IO;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio;

internal sealed record DirectExeSmokeProjectLoadResult(
    MachineProjectDocument? Project,
    string? InitialProjectPath,
    string? StartupSamplePath);

internal static class DirectExeSmokeProjectLoader
{
    private const string BundledSampleRelativePath = "Samples\\AutomaticTransferCell.ovmachine";

    public static DirectExeSmokeProjectLoadResult Load(
        string? projectPath,
        bool cameraFirstUseRequested,
        string? startupChoiceState)
    {
        if (cameraFirstUseRequested)
        {
            return new DirectExeSmokeProjectLoadResult(
                new MachineProjectDocument { Name = "Untitled" },
                InitialProjectPath: null,
                StartupSamplePath: null);
        }

        var bundledSamplePath = GetBundledSamplePath();
        if (!string.IsNullOrWhiteSpace(startupChoiceState))
        {
            return new DirectExeSmokeProjectLoadResult(
                Project: null,
                InitialProjectPath: null,
                StartupSamplePath: File.Exists(bundledSamplePath) ? bundledSamplePath : null);
        }

        if (!string.IsNullOrEmpty(projectPath) && File.Exists(projectPath))
        {
            var store = new ProjectDocumentStore();
            return new DirectExeSmokeProjectLoadResult(
                store.Load(File.ReadAllText(projectPath)),
                InitialProjectPath: projectPath,
                StartupSamplePath: null);
        }

        if (string.IsNullOrEmpty(projectPath) && File.Exists(bundledSamplePath))
        {
            var store = new ProjectDocumentStore();
            return new DirectExeSmokeProjectLoadResult(
                store.Load(File.ReadAllText(bundledSamplePath)),
                InitialProjectPath: null,
                StartupSamplePath: null);
        }

        return new DirectExeSmokeProjectLoadResult(
            Project: null,
            InitialProjectPath: null,
            StartupSamplePath: null);
    }

    private static string GetBundledSamplePath() =>
        Path.Combine(AppContext.BaseDirectory, BundledSampleRelativePath);
}
