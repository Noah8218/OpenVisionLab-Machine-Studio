using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraImageSourceEditorViewModelTests
{
    [Fact]
    public void BrowseUsesInjectedSelectorAndKeepsProjectRelativePath()
    {
        var root = CreateTestDirectory();
        var projectPath = Path.Combine(root, "machine.ovmachine");
        var imagePath = Path.Combine(root, "images", "inspection.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        var selectedProjectRoot = string.Empty;
        var project = CreateProject();
        var viewModel = new CameraImageSourceEditorViewModel(
            (_, _) => { },
            projectRoot =>
            {
                selectedProjectRoot = projectRoot;
                return imagePath;
            });

        try
        {
            viewModel.Load(project, projectPath, "camera-1");
            viewModel.BrowseCommand.Execute(null);

            Assert.Equal(root, selectedProjectRoot);
            Assert.Equal("images/inspection.png", viewModel.PathText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BrowseCancelLeavesDraftUnchanged()
    {
        var root = CreateTestDirectory();
        var projectPath = Path.Combine(root, "machine.ovmachine");
        var project = CreateProject();
        var viewModel = new CameraImageSourceEditorViewModel(
            (_, _) => { },
            _ => null);

        try
        {
            viewModel.Load(project, projectPath, "camera-1");
            viewModel.PathText = "existing.png";
            viewModel.BrowseCommand.Execute(null);

            Assert.Equal("existing.png", viewModel.PathText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MachineProjectDocument CreateProject()
    {
        var project = new MachineProjectDocument { Name = "Camera source" };
        project.Devices.Add(new DeviceDefinition
        {
            Id = "camera-1",
            Name = "Camera 1",
            Kind = DeviceKind.Camera,
            Camera = new VirtualCameraDefinition()
        });
        return project;
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\camera-image-source-editor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
