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
    public void CameraRemovalThenEmptyProjectAndReloadRecoverSelectionWithoutEditingTheProject()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        using var viewModel = CreateViewModel(project, CreateProjection());
        var store = new ProjectDocumentStore();
        viewModel.LoadProject(project, null);
        viewModel.SelectedCameraId = "camera-2";

        project.Devices.RemoveAll(device => device.Id == "camera-2");
        var removedCameraProject = store.SerializeForEvidence(project);
        viewModel.LoadProject(project, null);

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Same(project.Devices[0], viewModel.SelectedVirtualCamera);
        Assert.Equal("alpha", viewModel.SelectedCameraRecipe);
        Assert.Equal(removedCameraProject, store.SerializeForEvidence(project));

        project.Devices.Clear();
        var emptyProject = store.SerializeForEvidence(project);
        viewModel.LoadProject(project, null);

        Assert.False(viewModel.HasVirtualCamera);
        Assert.Null(viewModel.SelectedCameraId);
        Assert.Null(viewModel.SelectedVirtualCamera);
        Assert.Empty(viewModel.CurrentCameraRecipes);
        Assert.Null(viewModel.SelectedCameraRecipe);
        Assert.False(viewModel.ImageSourceEditor.CanBrowse);
        Assert.Equal(emptyProject, store.SerializeForEvidence(project));

        project.Devices = CreateProject().Devices;
        var restoredProject = store.SerializeForEvidence(project);
        viewModel.LoadProject(project, null);
        viewModel.SelectedCameraId = "camera-2";
        viewModel.SelectedCameraId = "missing-camera";

        Assert.True(viewModel.HasVirtualCamera);
        Assert.Equal("camera-2", viewModel.SelectedCameraId);
        Assert.Same(project.Devices[1], viewModel.SelectedVirtualCamera);
        Assert.Equal(restoredProject, store.SerializeForEvidence(project));
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

    [Fact]
    public void ModeOnlyProjectionRefreshRaisesOnlyCameraCommandGates()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var projection = CreateProjection();
        using var viewModel = CreateViewModel(project, projection, () => projection);
        viewModel.LoadProject(project, null);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        projection = projection with { IsRunMode = false };
        viewModel.RefreshProjection(invalidateCommands: false);

        Assert.Contains(nameof(CameraCommissioningViewModel.CanStartManualCameraControl), changedProperties);
        Assert.Contains(nameof(CameraCommissioningViewModel.CanTriggerCamera), changedProperties);
        Assert.DoesNotContain(nameof(CameraCommissioningViewModel.CurrentCameraName), changedProperties);
        Assert.DoesNotContain(nameof(CameraCommissioningViewModel.CurrentCameraSourceText), changedProperties);
    }

    [Fact]
    public void ResultToPendingToResetClearsStaleCameraDetailsWithoutChangingProject()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var projection = CreateProjection(CreateResultSnapshot());
        using var viewModel = CreateViewModel(project, projection, () => projection);
        viewModel.LoadProject(project, null);
        var projectBefore = new ProjectDocumentStore().SerializeForEvidence(project);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Equal("inspection-1", viewModel.CurrentCameraInspectionIdText);
        Assert.Contains("workpiece-component-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.Contains("run-1/WP-001", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.Equal(OpenVisionLanguageService.T("Camera.ResultSourceMock"), viewModel.CurrentCameraResultSourceText);

        changedProperties.Clear();
        projection = projection with { Snapshot = CreatePendingSnapshot() };
        viewModel.RefreshProjection();

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Equal(OpenVisionLanguageService.T("Shell.ResultPending"), viewModel.CurrentCameraResultText);
        Assert.Equal(OpenVisionLanguageService.T("Camera.ResultSourcePending"), viewModel.CurrentCameraResultSourceText);
        Assert.Equal("—", viewModel.CurrentCameraInspectionIdText);
        Assert.DoesNotContain("acquisition-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("inspection-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("workpiece-component-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("run-1/WP-001", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        AssertContainsResultProjectionNotifications(changedProperties);

        changedProperties.Clear();
        projection = projection with
        {
            Snapshot = new VirtualCameraSnapshot(
                "camera-1",
                "Camera 1",
                VirtualCameraState.Idle,
                0,
                null,
                null,
                0,
                0,
                null,
                null)
        };
        viewModel.RefreshProjection();

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Equal("—", viewModel.CurrentCameraResultText);
        Assert.Equal(OpenVisionLanguageService.T("Camera.ResultSourceNone"), viewModel.CurrentCameraResultSourceText);
        Assert.Equal("—", viewModel.CurrentCameraInspectionIdText);
        Assert.DoesNotContain("acquisition-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("inspection-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("workpiece-component-1", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("run-1/WP-001", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
        AssertContainsResultProjectionNotifications(changedProperties);
        Assert.Equal(projectBefore, new ProjectDocumentStore().SerializeForEvidence(project));
    }

    private static void AssertContainsResultProjectionNotifications(IEnumerable<string?> changedProperties)
    {
        Assert.Contains(nameof(CameraCommissioningViewModel.CurrentCameraResultText), changedProperties);
        Assert.Contains(nameof(CameraCommissioningViewModel.CurrentCameraResultSourceText), changedProperties);
        Assert.Contains(nameof(CameraCommissioningViewModel.CurrentCameraInspectionIdText), changedProperties);
        Assert.Contains(nameof(CameraCommissioningViewModel.CurrentCameraEvidenceDetailsText), changedProperties);
    }

    [Fact]
    public void DisposeClosesImageSourceEditorAdmission()
    {
        var root = Path.Combine(
            @"D:\OpenVisionLab-TestData\Machine\p2-camera-source-editor-disposal-admission-20260911",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        var projectPath = Path.Combine(root, "machine.ovmachine");
        var imagePath = Path.Combine(root, "images", "inspection.png");
        File.WriteAllText(projectPath, string.Empty);
        File.WriteAllBytes(imagePath, [1, 2, 3]);

        try
        {
            var project = CreateProject();
            using var viewModel = CreateViewModel(project, CreateProjection());
            viewModel.LoadProject(project, projectPath);
            var editor = viewModel.ImageSourceEditor;
            editor.PathText = "images/inspection.png";
            editor.PixelFormatText = "Mono8";

            Assert.True(editor.ApplyCommand.CanExecute(null));

            viewModel.Dispose();

            Assert.False(editor.ApplyCommand.CanExecute(null));
            editor.ApplyCommand.Execute(null);
            Assert.Null(project.Devices.Single(device => device.Id == "camera-1").Camera?.SingleImageSource);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DisposeRejectsCameraSelectionChanges()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        using var viewModel = CreateViewModel(project, CreateProjection());
        viewModel.LoadProject(project, null);

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Equal("alpha", viewModel.SelectedCameraRecipe);

        viewModel.Dispose();

        viewModel.SelectedCameraId = "camera-2";
        viewModel.SelectedCameraRecipe = "zeta";

        Assert.Equal("camera-1", viewModel.SelectedCameraId);
        Assert.Equal("alpha", viewModel.SelectedCameraRecipe);
    }

    [Fact]
    public void DisposeRejectsDirectLocalizationRefresh()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        using var viewModel = CreateViewModel(project, CreateProjection());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.Dispose();
        changedProperties.Clear();

        viewModel.RefreshLocalization();

        Assert.Empty(changedProperties);
    }

    internal static CameraCommissioningViewModel CreateViewModel(
        MachineProjectDocument project,
        CameraCommissioningProjection projection,
        Func<CameraCommissioningProjection>? projectionAccessor = null)
    {
        var snapshot = CreateSnapshot();
        return new(
            () => project,
            projectionAccessor ?? (() => projection),
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

    private static CameraCommissioningProjection CreateProjection(VirtualCameraSnapshot? snapshot = null) => new(
        snapshot ?? new VirtualCameraSnapshot(
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
        SimulationFixedStep: TimeSpan.FromMilliseconds(5),
        RuntimeRunMode: SimulationRunMode.Paused,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        ControlOwner: SimulationControlOwner.Definition,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: null);

    private static VirtualCameraSnapshot CreateResultSnapshot()
    {
        var frame = new VirtualCameraFrameEvidence(
            "frame-camera-1",
            "images/part.pgm",
            new string('A', 64),
            4,
            2,
            2,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            "inspection-1",
            "acquisition-1",
            "camera-1",
            "alpha",
            frame.FrameId,
            PlaceholderInspectionDecision.Pass,
            "Pass result",
            new Dictionary<string, double> { ["score"] = 0.9 });
        var result = new VirtualCameraAcquisitionResult(
            "acquisition-1",
            "camera-1",
            "alpha",
            1,
            PlaceholderInspectionDecision.Pass,
            frame,
            inspection,
            WorkpieceComponentId: "workpiece-component-1",
            WorkpieceInstanceId: "run-1/WP-001");

        return new VirtualCameraSnapshot(
            "camera-1",
            "Camera 1",
            VirtualCameraState.FrameReady,
            1,
            result.AcquisitionId,
            result.RecipeId,
            0,
            0,
            result,
            frame);
    }

    private static VirtualCameraSnapshot CreatePendingSnapshot() => new(
        "camera-1",
        "Camera 1",
        VirtualCameraState.AwaitingExternalResult,
        2,
        "acquisition-2",
        "alpha",
        0,
        0,
        null,
        null);

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

    internal static MachineProjectDocument CreateProject()
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
