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

    [Fact]
    public void LegacyCallbackReceivesAppliedCameraIdAndProjectRelativePath()
    {
        var root = CreateTestDirectory();
        var projectPath = Path.Combine(root, "machine.ovmachine");
        var imagePath = Path.Combine(root, "images", "inspection.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        string? appliedCameraId = null;
        string? appliedDetail = null;
        var viewModel = new CameraImageSourceEditorViewModel(
            (cameraId, detail) =>
            {
                appliedCameraId = cameraId;
                appliedDetail = detail;
            },
            _ => imagePath);

        try
        {
            viewModel.Load(CreateProject(), projectPath, "camera-1");
            viewModel.PathText = "images/inspection.png";
            viewModel.PixelFormatText = "Mono8";
            viewModel.ApplyCommand.Execute(null);

            Assert.Equal("camera-1", appliedCameraId);
            Assert.Equal("images/inspection.png", appliedDetail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CameraSelectionRoundTripRestoresEachUnsavedDraft()
    {
        var viewModel = new CameraImageSourceEditorViewModel((_, _) => { }, _ => null);
        viewModel.Load(CreateProject(), null, "camera-1");
        viewModel.PathText = "images/camera-1.raw";
        viewModel.Width = 640;
        viewModel.Height = 480;
        viewModel.PixelFormatText = "Mono8";

        viewModel.SelectCamera("camera-2");
        viewModel.PathText = "images/camera-2.raw";
        viewModel.Width = 1280;
        viewModel.Height = 720;
        viewModel.PixelFormatText = "Mono16";

        viewModel.SelectCamera("camera-1");

        Assert.Equal("images/camera-1.raw", viewModel.PathText);
        Assert.Equal(640, viewModel.Width);
        Assert.Equal(480, viewModel.Height);
        Assert.Equal("Mono8", viewModel.PixelFormatText);

        viewModel.SelectCamera("camera-2");

        Assert.Equal("images/camera-2.raw", viewModel.PathText);
        Assert.Equal(1280, viewModel.Width);
        Assert.Equal(720, viewModel.Height);
        Assert.Equal("Mono16", viewModel.PixelFormatText);
    }

    [Fact]
    public void RevertClearsPreservedDraftForCurrentCamera()
    {
        var viewModel = new CameraImageSourceEditorViewModel((_, _) => { }, _ => null);
        viewModel.Load(CreateProject(), null, "camera-1");
        viewModel.Width = 640;
        viewModel.SelectCamera("camera-2");
        viewModel.SelectCamera("camera-1");

        viewModel.RevertCommand.Execute(null);
        viewModel.SelectCamera("camera-2");
        viewModel.SelectCamera("camera-1");

        Assert.Equal(1, viewModel.Width);
        Assert.False(viewModel.IsDirty);
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
        project.Devices.Add(new DeviceDefinition
        {
            Id = "camera-2",
            Name = "Camera 2",
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
