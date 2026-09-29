using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectSidecarPartialSaveTests
{
    private const string ProjectId = "partial-save-project";
    private const string CameraId = "camera.top";
    private const string RecipeId = "presence-check";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task Mch028_PartialSidecarSavePreservesSuccessfulArtifactAndRejectsOlderEvidence()
    {
        var directory = CreateTestDirectory();
        var projectPath = Path.Combine(directory, "partial-save.ovmachine");
        var project = new MachineProjectDocument { Id = ProjectId, Name = "Version one" };
        var documentStore = new ProjectDocumentStore();
        var fileStore = new ProjectDocumentFileStore();
        var profile = CreateProfile();
        var projectJsonV1 = documentStore.SerializeForSaveEvidence(project);
        var originalVisionPath = $"{projectPath}.vision-result.json";

        try
        {
            await fileStore.SaveAsync(project, projectPath);

            var batchV1 = await CreateBatchAsync(projectPath, projectJsonV1, profile);
            var batchStoreV1 = new SimulationScenarioBatchArtifactStore();
            batchStoreV1.SetLatestBatchResult(batchV1);
            Assert.Null(batchStoreV1.Persist(
                projectPath,
                () => CreateBatchContext(projectJsonV1, profile)));

            var visionV1 = CreateVisionEvidence(projectPath, projectJsonV1, "Version one");
            DeterministicVisionExecutionEvidencePackage.SaveToJson(visionV1, originalVisionPath);
            var visionBytesBeforeFailure = await File.ReadAllBytesAsync(originalVisionPath);

            var restoredBatchV1 = new SimulationScenarioBatchArtifactStore();
            restoredBatchV1.Restore(projectPath, () => CreateBatchContext(projectJsonV1, profile));
            Assert.Equal(SimulationScenarioBatchArtifactState.Restored, restoredBatchV1.State);
            Assert.NotNull(restoredBatchV1.LatestBatchResult);

            OpenVisionLanguageService.Load();
            using (var restoredVisionV1 = new VisionExecutionEvidenceViewModel(
                () => new VisionEvidenceContext(
                    ProjectId,
                    projectJsonV1,
                    BuildIdentity.Current,
                    projectPath,
                    CameraId,
                    RecipeId),
                _ => { },
                _ => { }))
            {
                restoredVisionV1.Restore();
                Assert.NotNull(restoredVisionV1.LatestEvidence);
                Assert.NotNull(restoredVisionV1.GetCurrentEvidence());
            }

            project.Name = "Version two";
            var projectJsonV2 = documentStore.SerializeForSaveEvidence(project);
            var batchV2 = await CreateBatchAsync(projectPath, projectJsonV2, profile);
            var batchStoreV2 = new SimulationScenarioBatchArtifactStore();
            batchStoreV2.SetLatestBatchResult(batchV2);
            var visionCallbackCalled = false;
            var expectedFailure = new IOException("controlled multi-axis sidecar failure");
            var workflow = new ProjectSaveWorkflow(
                fileStore,
                documentStore,
                () => project,
                _ => { },
                path => Assert.Null(batchStoreV2.Persist(
                    path,
                    () => CreateBatchContext(projectJsonV2, profile))),
                _ => throw expectedFailure,
                _ => visionCallbackCalled = true);

            var actualFailure = await Assert.ThrowsAsync<IOException>(() =>
                workflow.SaveWithReceiptAsync(projectPath, "partial-save-session", 1));

            Assert.Same(expectedFailure, actualFailure);
            Assert.Equal("Version two", fileStore.Load(projectPath).Name);
            Assert.False(visionCallbackCalled);
            Assert.Equal(visionBytesBeforeFailure, await File.ReadAllBytesAsync(originalVisionPath));

            var restoredBatch = new SimulationScenarioBatchArtifactStore();
            restoredBatch.Restore(projectPath, () => CreateBatchContext(projectJsonV2, profile));
            Assert.Equal(SimulationScenarioBatchArtifactState.Restored, restoredBatch.State);
            Assert.NotNull(restoredBatch.LatestBatchResult);
            Assert.True(restoredBatch.LatestBatchResult!.IsForContext(
                CreateBatchContext(projectJsonV2, profile).BatchId,
                BuildIdentity.Current,
                2,
                ProjectId,
                projectJsonV2,
                FixedStep,
                profile));

            using var restoredVision = new VisionExecutionEvidenceViewModel(
                () => new VisionEvidenceContext(
                    ProjectId,
                    projectJsonV2,
                    BuildIdentity.Current,
                    projectPath,
                    CameraId,
                    RecipeId),
                _ => { },
                _ => { });
            restoredVision.Restore();
            Assert.NotNull(restoredVision.LatestEvidence);
            Assert.Null(restoredVision.GetCurrentEvidence());
            Assert.Equal(
                OpenVisionLanguageService.T("Camera.EvidenceStale"),
                restoredVision.StatusText);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Mch028_MalformedVisionSidecarIsRejectedWithoutCurrentEvidence()
    {
        var directory = CreateTestDirectory();
        var projectPath = Path.Combine(directory, "malformed-sidecar.ovmachine");
        File.WriteAllText($"{projectPath}.vision-result.json", "{ malformed sidecar");
        OpenVisionLanguageService.Load();

        try
        {
            using var vision = new VisionExecutionEvidenceViewModel(
                () => new VisionEvidenceContext(
                    ProjectId,
                    "{\"id\":\"partial-save-project\"}",
                    BuildIdentity.Current,
                    projectPath,
                    CameraId,
                    RecipeId),
                _ => { },
                _ => { });

            vision.Restore();

            Assert.Null(vision.LatestEvidence);
            Assert.Null(vision.GetCurrentEvidence());
            Assert.Equal(OpenVisionLanguageService.T("Camera.EvidenceStale"), vision.StatusText);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SimulationScenarioBatchArtifactContext CreateBatchContext(
        string projectJson,
        DeterministicConditionScenarioProfile profile) =>
        new(
            ProjectId,
            projectJson,
            FixedStep,
            profile,
            2,
            BuildIdentity.Current);

    private static DeterministicConditionScenarioProfile CreateProfile() =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "partial-save-scenario",
            "Partial save scenario",
            "MCH-028 partial sidecar save fixture.",
            "equipment-1",
            17,
            DurationTicks: 2,
            MinimumStateTicks: 1,
            JitterTicks: 0,
            Assertions: ImmutableArray<DeterministicScenarioAssertion>.Empty);

    private static async Task<DeterministicSimulationBatchResultPackage> CreateBatchAsync(
        string projectPath,
        string projectJson,
        DeterministicConditionScenarioProfile profile)
    {
        var run = await CreateRunAsync(projectPath, projectJson, profile);
        return await new DeterministicSimulationBatchRunner().RunAsync(
            new DeterministicSimulationBatchDefinition(
                $"{ProjectId}:{profile.ScenarioId}",
                2,
                BuildIdentity.Current),
            (_, _) => Task.FromResult(run));
    }

    private static async Task<DeterministicSimulationRunResultPackage> CreateRunAsync(
        string projectPath,
        string projectJson,
        DeterministicConditionScenarioProfile profile)
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync();
        try
        {
            var configured = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(CreateRuntime()));
            Assert.True(configured.IsAccepted, configured.Detail);
            var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(engine, profile);
            return DeterministicSimulationRunResultPackage.FromReplay(
                ProjectId,
                "Partial save project",
                projectPath,
                projectJson,
                FixedStep,
                profile,
                replay);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private static SimulationRuntimeConfiguration CreateRuntime() =>
        new(
            Array.Empty<AxisConfiguration>(),
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>(),
            Array.Empty<VirtualCameraConfiguration>(),
            automaticRun: null,
            layout: new MachineLayoutRuntimeConfiguration(
                "main",
                "Main",
                [
                    new MachineFrameRuntimeConfiguration(
                        "equipment-1",
                        "Equipment",
                        new LayoutRuntimeTransform(0, 0),
                        new LayoutRuntimeSize(10, 10))
                ]));

    private static DeterministicVisionExecutionEvidencePackage CreateVisionEvidence(
        string projectPath,
        string projectJson,
        string projectName)
    {
        var frameId = $"{CameraId}/frame/00000001";
        var frameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("mch-028-frame")));
        var frame = new VirtualCameraFrameEvidence(frameId, "input.raw", frameHash, 4, 2, 2, "Mono8");
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
            projectName,
            projectPath,
            projectJson,
            BuildIdentity.Current,
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

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            TestStorage.RootPath,
            "mch-028-sidecar-partial-save",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
