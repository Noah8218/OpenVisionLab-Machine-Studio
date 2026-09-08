using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
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
}
