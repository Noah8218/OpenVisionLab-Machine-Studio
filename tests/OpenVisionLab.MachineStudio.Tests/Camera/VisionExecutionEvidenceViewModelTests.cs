using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using System.Security.Cryptography;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class VisionExecutionEvidenceViewModelTests
{
    private const string ProjectId = "vision-project";
    private const string ProjectJson = "{\"id\":\"vision-project\"}";
    private const string CameraId = "camera.top";
    private const string RecipeId = "presence-check";
    private const string BuildIdentity = "0.1.0-test+abc123";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void Restore_MissingArtifactLeavesExecutionStateEmpty()
    {
        OpenVisionLanguageService.Load();
        var projectPath = CreateProjectPath();
        var logMessages = new List<string>();
        var notificationCount = 0;
        var viewModel = CreateViewModel(
            projectPath,
            logMessages,
            () => notificationCount++);

        try
        {
            viewModel.Restore();

            Assert.False(viewModel.IsCapturing);
            Assert.Null(viewModel.LatestEvidence);
            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceNone"),
                viewModel.StatusText);
            Assert.Contains(logMessages, message =>
                message.Contains("No saved execution evidence found", StringComparison.Ordinal));
            Assert.NotEqual(0, notificationCount);
        }
        finally
        {
            File.Delete($"{projectPath}.vision-result.json");
        }
    }

    [Fact]
    public void BeginAndCancelCapture_DoesNotPersistOrRetainRecorder()
    {
        OpenVisionLanguageService.Load();
        var projectPath = CreateProjectPath();
        var viewModel = CreateViewModel(projectPath, [], () => { });
        var recorder = new DeterministicVisionExecutionRecorder(
            ProjectId,
            "Vision Project",
            projectPath,
            ProjectJson,
            BuildIdentity,
            FixedStep,
            0,
            "command-001",
            CameraId,
            RecipeId,
            "camera.top/frame/00000001",
            "frame-001",
            "inspection-001");

        try
        {
            viewModel.BeginCapture(recorder);
            Assert.True(viewModel.IsCapturing);
            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceCapturing"),
                viewModel.StatusText);

            viewModel.CancelCapture();

            Assert.False(viewModel.IsCapturing);
            Assert.Null(viewModel.LatestEvidence);
            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceNone"),
                viewModel.StatusText);
            Assert.False(File.Exists($"{projectPath}.vision-result.json"));
        }
        finally
        {
            File.Delete($"{projectPath}.vision-result.json");
        }
    }

    [Fact]
    public void RefreshContext_CancelsCaptureBeforeLateContextEventCanCompleteEvidence()
    {
        OpenVisionLanguageService.Load();
        var projectPath = CreateProjectPath();
        var cameraId = CameraId;
        var recipeId = RecipeId;
        using var viewModel = new VisionExecutionEvidenceViewModel(
            () => new VisionEvidenceContext(
                ProjectId,
                ProjectJson,
                BuildIdentity,
                projectPath,
                cameraId,
                recipeId),
            _ => { },
            _ => { });
        var recorder = new DeterministicVisionExecutionRecorder(
            ProjectId,
            "Vision Project",
            projectPath,
            ProjectJson,
            BuildIdentity,
            FixedStep,
            20,
            "command-001",
            CameraId,
            RecipeId,
            "acquisition-001",
            "frame-001",
            "inspection-001");
        var snapshot = new SimulationSnapshot(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [],
            0,
            [],
            []);

        try
        {
            viewModel.BeginCapture(recorder);
            cameraId = "camera.changed";
            viewModel.RefreshContext();

            Assert.False(viewModel.IsCapturing);
            Assert.Null(viewModel.LatestEvidence);

            viewModel.RecordEvent(
                new SimulationEvent(
                    1,
                    20,
                    TimeSpan.FromTicks(FixedStep.Ticks * 20),
                    "Vision",
                    "CameraTriggered",
                    "late event",
                    "command-001"),
                snapshot);

            Assert.False(viewModel.TryComplete(snapshot));
            Assert.Null(viewModel.GetCurrentEvidence());
            Assert.False(File.Exists($"{projectPath}.vision-result.json"));
        }
        finally
        {
            File.Delete($"{projectPath}.vision-result.json");
        }
    }

    [Fact]
    public void Dispose_SuppressesLateRuntimeEvidenceAndParentNotification()
    {
        OpenVisionLanguageService.Load();
        var projectPath = CreateProjectPath();
        var notificationCount = 0;
        using var viewModel = CreateViewModel(projectPath, [], () => notificationCount++);
        var recorder = new DeterministicVisionExecutionRecorder(
            ProjectId,
            "Vision Project",
            projectPath,
            ProjectJson,
            BuildIdentity,
            FixedStep,
            20,
            "command-001",
            CameraId,
            RecipeId,
            "acquisition-001",
            "frame-001",
            "inspection-001");
        var snapshot = new SimulationSnapshot(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [],
            0,
            [],
            []);

        viewModel.BeginCapture(recorder);
        var notificationCountBeforeDispose = notificationCount;
        viewModel.Dispose();
        viewModel.Dispose();

        viewModel.RecordEvent(
            new SimulationEvent(
                1,
                20,
                TimeSpan.FromTicks(FixedStep.Ticks * 20),
                "Vision",
                "VisionResultReady",
                "late result",
                null),
            snapshot);

        Assert.False(viewModel.IsCapturing);
        Assert.False(viewModel.TryComplete(snapshot));
        Assert.Null(viewModel.GetCurrentEvidence());
        Assert.Equal(notificationCountBeforeDispose, notificationCount);
        Assert.False(File.Exists($"{projectPath}.vision-result.json"));
    }

    [Fact]
    public async Task Restore_WhenFrameSourceChanges_RejectsCurrentEvidence()
    {
        OpenVisionLanguageService.Load();
        var projectPath = CreateProjectPath();
        var sourcePath = Path.Combine(Path.GetDirectoryName(projectPath)!, "input.raw");
        await File.WriteAllBytesAsync(sourcePath, [0x10, 0x20, 0x30, 0x40]);
        var package = CreateEvidencePackage(
            projectPath,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
        DeterministicVisionExecutionEvidencePackage.SaveToJson(
            package,
            $"{projectPath}.vision-result.json");
        await File.WriteAllBytesAsync(sourcePath, [0x10, 0x20, 0x30, 0x41]);
        var logMessages = new List<string>();
        using var viewModel = CreateSourceValidatedViewModel(projectPath, logMessages);

        try
        {
            viewModel.Restore();

            Assert.NotNull(viewModel.LatestEvidence);
            Assert.Null(viewModel.GetCurrentEvidence());
            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceStale"),
                viewModel.StatusText);
            Assert.Contains(logMessages, message =>
                message.Contains("frame source context changed", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete($"{projectPath}.vision-result.json");
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task RefreshContext_WhenFrameSourceChanges_MarksImportedEvidenceStale()
    {
        OpenVisionLanguageService.Load();
        var projectPath = CreateProjectPath();
        var sourcePath = Path.Combine(Path.GetDirectoryName(projectPath)!, "input.raw");
        var original = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await File.WriteAllBytesAsync(sourcePath, original);
        using var viewModel = CreateSourceValidatedViewModel(projectPath, []);
        var package = CreateEvidencePackage(
            projectPath,
            Convert.ToHexString(SHA256.HashData(original)));

        try
        {
            viewModel.SetImportedEvidence(package);
            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceImported"),
                viewModel.StatusText);

            await File.WriteAllBytesAsync(sourcePath, [0x10, 0x20, 0x30, 0x41]);
            viewModel.RefreshContext();

            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceStale"),
                viewModel.StatusText);
            Assert.Null(viewModel.GetCurrentEvidence());
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static VisionExecutionEvidenceViewModel CreateViewModel(
        string projectPath,
        List<string> logMessages,
        Action notify) =>
        new(
            () => new VisionEvidenceContext(
                ProjectId,
                ProjectJson,
                BuildIdentity,
                projectPath,
                CameraId,
                RecipeId),
            logMessages.Add,
            _ => notify());

    private static string CreateProjectPath()
    {
        var root = Directory.Exists("D:\\")
            ? @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\vision-evidence-refactor-tests"
            : Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-Studio", "vision-evidence-refactor-tests");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"project-{Guid.NewGuid():N}.ovmachine");
    }

    private static VisionExecutionEvidenceViewModel CreateSourceValidatedViewModel(
        string projectPath,
        List<string> logMessages) =>
        new(
            () => new VisionEvidenceContext(
                ProjectId,
                ProjectJson,
                BuildIdentity,
                projectPath,
                CameraId,
                RecipeId,
                frameHash => new ProjectRelativeSingleImageSource(
                    Path.GetDirectoryName(projectPath)!,
                    "input.raw",
                    2,
                    2,
                    "Mono8").MatchesContentHash(frameHash)),
            logMessages.Add,
            _ => { });

    private static DeterministicVisionExecutionEvidencePackage CreateEvidencePackage(
        string projectPath,
        string frameHash)
    {
        var frameId = $"{CameraId}/frame/00000001";
        var frame = new VirtualCameraFrameEvidence(
            frameId,
            "input.raw",
            frameHash,
            4,
            2,
            2,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            "inspection-001",
            frameId,
            CameraId,
            RecipeId,
            frameId,
            PlaceholderInspectionDecision.Pass,
            "pass",
            new Dictionary<string, double> { ["Score"] = 1 });
        var camera = new VirtualCameraSnapshot(
            CameraId,
            "Top camera",
            VirtualCameraState.FrameReady,
            1,
            frameId,
            RecipeId,
            0,
            0,
            new VirtualCameraAcquisitionResult(
                frameId,
                CameraId,
                RecipeId,
                1,
                PlaceholderInspectionDecision.Pass,
                frame,
                inspection),
            frame);
        var snapshot = new SimulationSnapshot(
            FixedStep,
            1,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [],
            0,
            [],
            [],
            [camera],
            AutomaticRunSnapshot.NotConfigured,
            [],
            projectId: ProjectId,
            runtimeGeneration: 1);
        return DeterministicVisionExecutionEvidencePackage.Create(
            ProjectId,
            "Vision Project",
            projectPath,
            ProjectJson,
            BuildIdentity,
            FixedStep,
            0,
            snapshot,
            camera,
            [
                new SimulationEvent(0, 0, TimeSpan.Zero, "Camera", "CameraTriggered", "trigger"),
                new SimulationEvent(1, 1, FixedStep, "Camera", "CameraFrameReady", "frame"),
                new SimulationEvent(2, 1, FixedStep, "Vision", "VisionResultReady", "result")
            ]);
    }
}
