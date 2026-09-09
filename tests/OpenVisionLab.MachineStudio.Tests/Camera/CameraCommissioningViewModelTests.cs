using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraCommissioningViewModelTests
{
    [Fact]
    public void LoadProjectKeepsSelectionAndImageEditorInsideCameraOwner()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        using var viewModel = CreateViewModel(project, CreateProjection());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.LoadProject(project, null);

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Equal(["alpha", "zeta"], viewModel.CurrentCameraRecipes);
        Assert.NotNull(viewModel.ImageSourceEditor);

        viewModel.SelectedCameraId = "camera-2";

        Assert.Equal("camera-2", viewModel.SelectedCameraId);
        Assert.Contains(nameof(CameraCommissioningViewModel.SelectedCameraId), changedProperties);
        Assert.Contains(nameof(CameraCommissioningViewModel.CurrentCameraRecipes), changedProperties);
    }

    [Fact]
    public void SessionCloseAdmissionAndDisposeGateCameraCommands()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        using var viewModel = CreateViewModel(project, CreateProjection());
        viewModel.LoadProject(project, null);
        viewModel.RefreshProjection();

        Assert.True(viewModel.CanStartManualCameraControl);
        viewModel.SetSessionCloseAdmission(true);
        Assert.False(viewModel.CanStartManualCameraControl);
        Assert.False(viewModel.CanTriggerCamera);

        viewModel.SetSessionCloseAdmission(false);
        Assert.True(viewModel.CanStartManualCameraControl);

        viewModel.Dispose();
        Assert.True(viewModel.IsDisposed);
        Assert.False(viewModel.CanStartManualCameraControl);
        Assert.False(viewModel.CanTriggerCamera);
    }

    private static CameraCommissioningViewModel CreateViewModel(
        MachineProjectDocument project,
        CameraCommissioningProjection projection)
    {
        var snapshot = CreateSnapshot();
        return new(
            () => project,
            () => projection,
            () => new VisionEvidenceContext(
                project.Id,
                "{}",
                "test-build",
                null,
                "camera-1",
                "alpha"),
            () => new ManualCameraSessionIdentity("session-1", 1),
            () => new ManualCameraTriggerRequestInput(
                false,
                "session-1",
                1,
                project.Id,
                project.Name,
                null,
                "{}",
                "test-build",
                TimeSpan.FromMilliseconds(5),
                snapshot,
                null,
                "alpha",
                null,
                null,
                project.Simulation.Seed),
            () => snapshot,
            () => CancellationToken.None,
            (_, _) => Task.FromResult(
                new SimulationCommandResult(
                    "test-command",
                    true,
                    0,
                    TimeSpan.Zero,
                    SimulationCommandErrorCode.None,
                    null)),
            _ => { },
            () => { },
            () => Task.CompletedTask,
            () => { },
            _ => { },
            (_, _) => { },
            () => { },
            OpenVisionLanguageService.T,
            _ => { });
    }

    private static CameraCommissioningProjection CreateProjection() => new(
        new VirtualCameraSnapshot(
            "camera-1",
            "Camera 1",
            VirtualCameraState.Idle,
            0,
            null,
            null,
            0,
            0,
            null),
        HasCameraDefinition: true,
        FallbackCameraName: "Camera 1",
        ImageSource: new VirtualSingleImageSourceDefinition
        {
            SourceRelativePath = "images/part.png",
            Width = 10,
            Height = 10,
            PixelFormat = "Mono8"
        },
        ProjectPath: @"D:\OpenVisionLab-TestData\Machine\camera-commissioning-workspace\machine.ovmachine",
        SelectedCameraRecipe: "alpha",
        RuntimeRunMode: SimulationRunMode.Paused,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        ControlOwner: SimulationControlOwner.Definition,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: null);

    private static SimulationSnapshot CreateSnapshot() => new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Definition,
        1,
        Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot>(),
        0,
        Array.Empty<DigitalSignalSnapshot>(),
        Array.Empty<SequenceExecutionSnapshot>(),
        Array.Empty<VirtualCameraSnapshot>());

    private static MachineProjectDocument CreateProject()
    {
        var project = new MachineProjectDocument { Name = "Camera workspace" };
        project.Devices.AddRange(
        [
            new DeviceDefinition
            {
                Id = "camera-1",
                Name = "Camera 1",
                Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition()
            },
            new DeviceDefinition
            {
                Id = "camera-2",
                Name = "Camera 2",
                Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition()
            }
        ]);
        project.Sequences.Add(
            new SequenceDefinition
            {
                Id = "sequence-1",
                Steps =
                [
                    new SequenceStepDefinition
                    {
                        Id = "trigger-zeta",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-1",
                        Parameter = "zeta"
                    },
                    new SequenceStepDefinition
                    {
                        Id = "trigger-alpha",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-1",
                        Parameter = "alpha"
                    },
                    new SequenceStepDefinition
                    {
                        Id = "trigger-beta",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-2",
                        Parameter = "beta"
                    }
                ]
            });
        return project;
    }
}
