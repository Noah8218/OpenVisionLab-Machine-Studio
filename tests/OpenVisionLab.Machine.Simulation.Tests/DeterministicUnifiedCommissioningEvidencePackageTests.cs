using System.Text.Json;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicUnifiedCommissioningEvidencePackageTests
{
    private const string ProjectId = "vision-project";
    private const string ProjectName = "Vision Project";
    private const string ProjectJson = "{\"id\":\"vision-project\",\"name\":\"Vision Project\"}";
    private const string ProjectPath =
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0029-unified-evidence\\vision-project.ovmachine";
    private const string BuildIdentity = "0.1.0-test+abc123";
    private const string CameraId = "camera.top";
    private const string RecipeId = "presence-check";
    private const string AcquisitionId = "camera.top/frame/00000001";
    private const string FrameId = "frame-001";
    private const string InspectionId = "inspection-001";
    private const string CommandId = "command-001";
    private const string FrameHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task Create_ValidatesNestedHashesAndSeparatesReplayableArtifacts()
    {
        var bundle = await CreateBundleAsync(includeVision: true);
        var json = DeterministicUnifiedCommissioningEvidencePackage.SaveToJson(bundle);

        Assert.True(bundle.HasValidEvidenceHash());
        Assert.True(bundle.SimulationEvidence.HasValidEvidenceHash());
        Assert.True(bundle.CommandTrace.HasValidTraceHash());
        Assert.True(bundle.VisionEvidence!.HasValidEvidenceHash());
        Assert.True(bundle.CanReplayCommandTrace);
        Assert.True(bundle.ContainsNonReplayableVisionEvidence);
        Assert.Equal(string.Empty, bundle.VisionEvidence.ProjectPath);
        Assert.DoesNotContain(ProjectPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeDebugger", json, StringComparison.Ordinal);

        var profile = CreateProfile(seed: 42);
        Assert.True(bundle.IsForContext(
            ProjectId,
            ProjectJson,
            FixedStep,
            profile,
            BuildIdentity));
    }

    [Fact]
    public async Task SaveLoadAndImport_RebindsPathsOnlyAtExplicitBoundary_AndProtectsDestination()
    {
        var bundle = await CreateBundleAsync(includeVision: true);
        var root = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0029-unified-evidence",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bundlePath = Path.Combine(root, "commissioning.ovcommissioning-evidence.json");
        var importedProjectPath = Path.Combine(root, "imported.ovmachine");
        try
        {
            DeterministicUnifiedCommissioningEvidencePackage.SaveToJson(bundle, bundlePath);
            var originalJson = File.ReadAllText(bundlePath);
            var loaded = Assert.IsType<DeterministicUnifiedCommissioningEvidencePackage>(
                DeterministicUnifiedCommissioningEvidencePackage.LoadFromJson(bundlePath));

            Assert.Equal(originalJson, DeterministicUnifiedCommissioningEvidencePackage.SaveToJson(loaded));
            Assert.True(loaded.TryGetArtifacts(
                importedProjectPath,
                out var importedBatch,
                out var importedBaseline,
                out var importedTrace,
                out var importedVision));
            Assert.All(importedBatch.Runs, run =>
                Assert.Equal(Path.GetFullPath(importedProjectPath), run.Result.ProjectPath));
            Assert.Equal(Path.GetFullPath(importedProjectPath), importedBaseline!.ProjectPath);
            Assert.Equal(bundle.CommandTrace.TraceHash, importedTrace.TraceHash);
            Assert.Equal(Path.GetFullPath(importedProjectPath), importedVision!.ProjectPath);

            var invalid = bundle with { EvidenceHash = new string('0', 64) };
            Assert.Throws<InvalidOperationException>(() =>
                DeterministicUnifiedCommissioningEvidencePackage.SaveToJson(invalid, bundlePath));
            Assert.Equal(originalJson, File.ReadAllText(bundlePath));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
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
    public async Task Validation_RejectsContextMismatchNestedTamperingAndSourcePaths()
    {
        var bundle = await CreateBundleAsync(includeVision: true);
        var profile = CreateProfile(seed: 42);

        Assert.False((bundle with { ProjectId = "other-project" }).HasValidEvidenceHash());
        Assert.False((bundle with
        {
            CommandTrace = bundle.CommandTrace with { TraceHash = new string('0', 64) }
        }).HasValidEvidenceHash());
        Assert.False((bundle with
        {
            VisionEvidence = bundle.VisionEvidence! with { ProjectPath = ProjectPath }
        }).HasValidEvidenceHash());
        Assert.False(bundle.IsForContext(
            ProjectId,
            ProjectJson.Replace("Vision Project", "Changed Project", StringComparison.Ordinal),
            FixedStep,
            profile,
            BuildIdentity));
        Assert.False(bundle.IsForContext(
            ProjectId,
            ProjectJson,
            FixedStep,
            profile,
            "changed-build"));

        var mismatchedTrace = DeterministicSimulationCommandTracePackage.Create(
            TimeSpan.FromMilliseconds(10),
            []);
        Assert.Throws<InvalidOperationException>(() =>
            DeterministicUnifiedCommissioningEvidencePackage.Create(
                bundle.SimulationEvidence,
                mismatchedTrace,
                bundle.VisionEvidence));

        var mismatchedVision = CreateVisionPackage(
            "other-project",
            "{\"id\":\"other-project\"}",
            "Other Project");
        Assert.Throws<InvalidOperationException>(() =>
            DeterministicUnifiedCommissioningEvidencePackage.Create(
                bundle.SimulationEvidence,
                bundle.CommandTrace,
                mismatchedVision));
    }

    [Fact]
    public async Task Create_AllowsSimulationAndTraceBundleWithoutOptionalVisionEvidence()
    {
        var bundle = await CreateBundleAsync(includeVision: false);

        Assert.True(bundle.HasValidEvidenceHash());
        Assert.Null(bundle.VisionEvidence);
        Assert.False(bundle.ContainsNonReplayableVisionEvidence);
        Assert.True(bundle.CanReplayCommandTrace);
    }

    private static async Task<DeterministicUnifiedCommissioningEvidencePackage> CreateBundleAsync(
        bool includeVision)
    {
        var profile = CreateProfile(seed: 42);
        var baseline = await RunPackageAsync(profile);
        var batch = await new DeterministicSimulationBatchRunner().RunAsync(
            new DeterministicSimulationBatchDefinition(
                $"{ProjectId}:{profile.ScenarioId}",
                RepetitionCount: 1,
                BuildIdentity),
            (_, cancellationToken) => RunPackageAsync(profile, cancellationToken),
            baseline);
        var exchange = DeterministicSimulationEvidenceExchangePackage.Create(batch, baseline);
        var trace = DeterministicSimulationCommandTracePackage.Create(
            FixedStep,
            [
                new(
                    1,
                    nameof(PauseCommand),
                    1,
                    FixedStep.Ticks,
                    true,
                    SimulationCommandErrorCode.None,
                    null,
                    JsonSerializer.SerializeToElement(new { }),
                    true,
                    null)
            ]);
        var vision = includeVision
            ? CreateVisionPackage(ProjectId, ProjectJson, ProjectName)
            : null;
        return DeterministicUnifiedCommissioningEvidencePackage.Create(exchange, trace, vision);
    }

    private static DeterministicConditionScenarioProfile CreateProfile(int seed) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "commissioning-condition",
            "Commissioning condition",
            "Unified evidence fixture.",
            "equipment-1",
            seed,
            10,
            MinimumStateTicks: 2,
            JitterTicks: 0);

    private static async Task<DeterministicSimulationRunResultPackage> RunPackageAsync(
        DeterministicConditionScenarioProfile profile,
        CancellationToken cancellationToken = default)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync(cancellationToken);
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                    Array.Empty<ChannelDefinition>(),
                    Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>(),
                    Array.Empty<VirtualCameraConfiguration>(),
                    automaticRun: null,
                    new MachineLayoutRuntimeConfiguration(
                        "main",
                        "Main",
                        new[]
                        {
                            new MachineFrameRuntimeConfiguration(
                                "equipment-1",
                                "Equipment",
                                new LayoutRuntimeTransform(0, 0),
                                new LayoutRuntimeSize(10, 10))
                        }))),
            cancellationToken);
        Assert.True(configured.IsAccepted, configured.Detail);

        var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(
            engine,
            profile,
            cancellationToken);
        await engine.StopAsync(cancellationToken);
        return DeterministicSimulationRunResultPackage.FromReplay(
            ProjectId,
            ProjectName,
            ProjectPath,
            ProjectJson,
            FixedStep,
            profile,
            replay);
    }

    private static DeterministicVisionExecutionEvidencePackage CreateVisionPackage(
        string projectId,
        string projectJson,
        string projectName) =>
        DeterministicVisionExecutionEvidencePackage.Create(
            projectId,
            projectName,
            ProjectPath,
            projectJson,
            BuildIdentity,
            FixedStep,
            triggerTick: 20,
            Snapshot(30),
            Camera(),
            Events(100, 20));

    private static SimulationSnapshot Snapshot(long tick) => new(
        TimeSpan.FromTicks(tick * FixedStep.Ticks),
        tick,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [],
        [],
        [Camera()]);

    private static VirtualCameraSnapshot Camera()
    {
        var frame = new VirtualCameraFrameEvidence(
            FrameId,
            "images/source.png",
            FrameHash,
            42,
            16,
            12,
            "Gray8");
        var inspection = new VirtualCameraInspectionEvidence(
            InspectionId,
            AcquisitionId,
            CameraId,
            RecipeId,
            FrameId,
            PlaceholderInspectionDecision.Pass,
            "Deterministic inspection completed.",
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["ContentLengthBytes"] = 42,
                ["PixelCount"] = 192
            });
        var result = new VirtualCameraAcquisitionResult(
            AcquisitionId,
            CameraId,
            RecipeId,
            1,
            PlaceholderInspectionDecision.Pass,
            frame,
            inspection);
        return new(
            CameraId,
            "Top Camera",
            VirtualCameraState.FrameReady,
            1,
            AcquisitionId,
            RecipeId,
            0,
            0,
            result,
            frame);
    }

    private static IReadOnlyList<SimulationEvent> Events(
        long firstEventIndex,
        long triggerTick) =>
    [
        Event(firstEventIndex, triggerTick, "Camera", "CameraTriggered",
            $"{CameraId} started {AcquisitionId} with inspection {InspectionId}.", CommandId),
        Event(firstEventIndex + 1, triggerTick + 4, "Camera", "CameraExposureCompleted",
            $"{CameraId} exposure completed for {AcquisitionId}."),
        Event(firstEventIndex + 2, triggerTick + 10, "Camera", "CameraFrameReady",
            $"{CameraId} frame {AcquisitionId} is ready."),
        Event(firstEventIndex + 3, triggerTick + 10, "Vision", "VisionResultReady",
            $"{AcquisitionId} inspection {InspectionId} result = PASS.")
    ];

    private static SimulationEvent Event(
        long eventIndex,
        long tick,
        string category,
        string code,
        string message,
        string? commandId = null) =>
        new(
            eventIndex,
            tick,
            TimeSpan.FromTicks(tick * FixedStep.Ticks),
            category,
            code,
            message,
            commandId);
}
