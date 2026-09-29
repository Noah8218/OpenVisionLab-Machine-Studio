using System.Collections.Immutable;
using System.Globalization;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch026SemanticHashBoundaryTests
{
    private const string ProjectJson = "{\"schema\":\"1.2\",\"id\":\"mch-026\",\"name\":\"Semantic fixture\"}";
    private const string FrameHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void RunEvidenceHash_IgnoresContainerLabelsAndPathButDetectsAxisIoAndSequenceState()
    {
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "mch-026-scenario",
            "Scenario label",
            "Scenario description",
            "axis-x",
            26,
            1,
            MinimumStateTicks: 1,
            JitterTicks: 0);
        var snapshot = CreateSnapshot(
            new AxisSnapshot("axis-x", "X axis", AxisState.Idle, 10, 0),
            new DigitalSignalSnapshot("input.ready", "Ready", ChannelKind.DigitalInput, false),
            new SequenceExecutionSnapshot(
                "sequence-main",
                SequenceExecutionStatus.Ready,
                null,
                0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                null,
                TimeSpan.FromSeconds(1)));
        var baseline = CreateRunPackage(
            profile,
            snapshot,
            "Visible scenario",
            Path.Combine(TestStorage.RootPath, "mch-026", "first.ovmachine"));
        var renamed = CreateRunPackage(
            profile with
            {
                Name = "시나리오 표시명",
                Description = "Localized description"
            },
            snapshot,
            "Renamed scenario",
            Path.Combine(TestStorage.RootPath, "mch-026", "second.ovmachine"));

        Assert.True(baseline.HasValidEvidenceHash());
        Assert.True(renamed.HasValidEvidenceHash());
        Assert.Equal(baseline.EvidenceHash, renamed.EvidenceHash);
        Assert.Equal(baseline.ConditionHash, renamed.ConditionHash);
        Assert.False((baseline with { ProjectId = string.Empty }).HasValidEvidenceHash());
        Assert.False((baseline with
        {
            SchemaVersion = DeterministicSimulationRunResultPackage.CurrentSchemaVersion + 1
        }).HasValidEvidenceHash());

        var changedAxis = CreateSnapshot(
            new AxisSnapshot("axis-x", "X axis", AxisState.Moving, 11, 2),
            snapshot.Signals.Single(),
            snapshot.Sequences.Single());
        var changedIo = CreateSnapshot(
            snapshot.Axes.Single(),
            new DigitalSignalSnapshot("input.ready", "Ready", ChannelKind.DigitalInput, true),
            snapshot.Sequences.Single());
        var changedSequence = CreateSnapshot(
            snapshot.Axes.Single(),
            snapshot.Signals.Single(),
            snapshot.Sequences.Single() with
            {
                Status = SequenceExecutionStatus.Running,
                CurrentStepId = "step-1",
                TickCount = 1
            });

        var baselineHash = DeterministicSimulationRunEvidenceHasher.HashSnapshots([snapshot]);
        Assert.NotEqual(
            baselineHash,
            DeterministicSimulationRunEvidenceHasher.HashSnapshots([changedAxis]));
        Assert.NotEqual(
            baselineHash,
            DeterministicSimulationRunEvidenceHasher.HashSnapshots([changedIo]));
        Assert.NotEqual(
            baselineHash,
            DeterministicSimulationRunEvidenceHasher.HashSnapshots([changedSequence]));
    }

    [Fact]
    public void CommandTraceCodec_UsesInvariantNumericArgumentsAcrossCultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var serializedValues = new List<string>();
        try
        {
            foreach (var cultureName in new[] { "fr-FR", "ko-KR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                Assert.True(
                    DeterministicSimulationCommandTraceCommandCodec.TrySerializeArguments(
                        new MoveAbsoluteCommand("axis-x", 12.5),
                        out var arguments,
                        out var reason),
                    reason);
                serializedValues.Add(arguments.GetProperty("targetPosition").GetString()!);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        Assert.Equal(new[] { "12.5", "12.5" }, serializedValues);
    }

    [Fact]
    public void VisionEvidenceHash_NormalizesMetricOrderAndRejectsIdentityAndIntegrityChanges()
    {
        var first = CreateVisionPackage(
            FrameHash,
            new Dictionary<string, double>
            {
                ["PixelCount"] = 192,
                ["ContentLengthBytes"] = 42
            });
        var reordered = CreateVisionPackage(
            FrameHash,
            new Dictionary<string, double>
            {
                ["ContentLengthBytes"] = 42,
                ["PixelCount"] = 192
            });
        var changedFrame = CreateVisionPackage(
            new string('B', 64),
            new Dictionary<string, double>
            {
                ["PixelCount"] = 192,
                ["ContentLengthBytes"] = 42
            });

        Assert.True(first.HasValidEvidenceHash());
        Assert.Equal(first.EvidenceHash, reordered.EvidenceHash);
        Assert.NotEqual(first.EvidenceHash, changedFrame.EvidenceHash);
        Assert.True(changedFrame.HasValidEvidenceHash());
        Assert.False((first with
        {
            Metrics = [new DeterministicVisionMetricEvidence("Score", double.NaN)]
        }).HasValidEvidenceHash());
        Assert.False((first with
        {
            Events = first.Events
                .Select((item, index) => index == 1 ? item with { Order = 0 } : item)
                .ToImmutableArray()
        }).HasValidEvidenceHash());
        Assert.False((first with
        {
            SchemaVersion = DeterministicVisionExecutionEvidencePackage.CurrentSchemaVersion + 1
        }).HasValidEvidenceHash());
    }

    private static SimulationSnapshot CreateSnapshot(
        AxisSnapshot axis,
        DigitalSignalSnapshot signal,
        SequenceExecutionSnapshot sequence) =>
        new(
            FixedStep,
            1,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [axis],
            1,
            [signal],
            [sequence]);

    private static DeterministicSimulationRunResultPackage CreateRunPackage(
        DeterministicConditionScenarioProfile profile,
        SimulationSnapshot snapshot,
        string projectName,
        string projectPath) =>
        DeterministicSimulationRunResultPackage.Create(
            "mch-026",
            projectName,
            projectPath,
            ProjectJson,
            FixedStep,
            profile,
            true,
            1,
            Array.Empty<SimulationCommandResult>(),
            Array.Empty<DeterministicConditionSample>(),
            Array.Empty<DeterministicConditionTransition>(),
            [snapshot],
            Array.Empty<SimulationEvent>());

    private static DeterministicVisionExecutionEvidencePackage CreateVisionPackage(
        string frameHash,
        IReadOnlyDictionary<string, double> metrics)
    {
        var camera = CreateCamera(frameHash, metrics);
        var snapshot = new SimulationSnapshot(
            TimeSpan.FromTicks(30 * FixedStep.Ticks),
            30,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [],
            0,
            [],
            [],
            [camera]);
        return DeterministicVisionExecutionEvidencePackage.Create(
            "mch-026-vision",
            "Vision display name",
            Path.Combine(TestStorage.RootPath, "mch-026", "vision.ovmachine"),
            "{\"id\":\"mch-026-vision\"}",
            "mch-026-test-build",
            FixedStep,
            20,
            snapshot,
            camera,
            CreateVisionEvents());
    }

    private static VirtualCameraSnapshot CreateCamera(
        string frameHash,
        IReadOnlyDictionary<string, double> metrics)
    {
        var frame = new VirtualCameraFrameEvidence(
            "frame-001",
            "images/source.png",
            frameHash,
            42,
            16,
            12,
            "Gray8");
        var inspection = new VirtualCameraInspectionEvidence(
            "inspection-001",
            "camera.top/frame/00000001",
            "camera.top",
            "presence-check",
            "frame-001",
            PlaceholderInspectionDecision.Pass,
            "Deterministic inspection completed.",
            metrics);
        var result = new VirtualCameraAcquisitionResult(
            "camera.top/frame/00000001",
            "camera.top",
            "presence-check",
            1,
            PlaceholderInspectionDecision.Pass,
            frame,
            inspection);
        return new(
            "camera.top",
            "Top Camera",
            VirtualCameraState.FrameReady,
            1,
            "camera.top/frame/00000001",
            "presence-check",
            0,
            0,
            result,
            frame);
    }

    private static IReadOnlyList<SimulationEvent> CreateVisionEvents() =>
    [
        new SimulationEvent(
            100,
            20,
            TimeSpan.FromTicks(20 * FixedStep.Ticks),
            "Camera",
            "CameraTriggered",
            "camera.top started camera.top/frame/00000001.",
            "command-001"),
        new SimulationEvent(
            101,
            24,
            TimeSpan.FromTicks(24 * FixedStep.Ticks),
            "Camera",
            "CameraFrameReady",
            "camera.top frame camera.top/frame/00000001 is ready."),
        new SimulationEvent(
            102,
            30,
            TimeSpan.FromTicks(30 * FixedStep.Ticks),
            "Vision",
            "VisionResultReady",
            "camera.top/frame/00000001 inspection inspection-001 result = PASS.")
    ];
}
