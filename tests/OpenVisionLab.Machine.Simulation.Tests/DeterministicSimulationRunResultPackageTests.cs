using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSimulationRunResultPackageTests
{
    [Fact]
    public async Task FromReplay_IsDeterministicAndRoundTripsAsOneRunPackage()
    {
        var profile = CreateProfile(seed: 42);
        var first = await RunAsync(profile);
        var second = await RunAsync(profile);
        var projectJson = "{\"schema\":\"1.2\",\"id\":\"package-fixture\",\"name\":\"Package fixture\"}";

        var firstPackage = DeterministicSimulationRunResultPackage.FromReplay(
            "package-fixture",
            "Package fixture",
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\goal2-fixture.ovmachine",
            projectJson,
            TimeSpan.FromMilliseconds(5),
            profile,
            first);
        var secondPackage = DeterministicSimulationRunResultPackage.FromReplay(
            "package-fixture",
            "Package fixture",
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\goal2-fixture.ovmachine",
            projectJson,
            TimeSpan.FromMilliseconds(5),
            profile,
            second);

        Assert.True(first.IsSuccess, first.FailureReason);
        Assert.True(second.IsSuccess, second.FailureReason);
        Assert.True(firstPackage.IsEquivalentTo(secondPackage));
        Assert.Equal(firstPackage.ConditionHash, secondPackage.ConditionHash);
        Assert.Equal(firstPackage.FaultHash, secondPackage.FaultHash);
        Assert.Equal(firstPackage.WorkpieceHash, secondPackage.WorkpieceHash);
        Assert.Equal(firstPackage.SnapshotHash, secondPackage.SnapshotHash);
        Assert.Equal(firstPackage.EventHash, secondPackage.EventHash);
        Assert.NotEqual(firstPackage.ConditionHash, string.Empty);
        Assert.NotEqual(firstPackage.FaultHash, string.Empty);
        Assert.NotEqual(firstPackage.WorkpieceHash, string.Empty);
        Assert.NotEqual(firstPackage.SnapshotHash, string.Empty);
        Assert.NotEqual(firstPackage.EventHash, string.Empty);

        var artifactPath = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260812-goal2-closeout",
            "deterministic-run-result-package.json");
        DeterministicSimulationRunResultPackage.SaveToJson(firstPackage, artifactPath);
        var loaded = Assert.IsType<DeterministicSimulationRunResultPackage>(
            DeterministicSimulationRunResultPackage.LoadFromJson(artifactPath));
        Assert.True(firstPackage.IsEquivalentTo(loaded));
    }

    [Fact]
    public async Task CompareTo_ReportsScenarioMismatchWhenSeedChanges()
    {
        var firstProfile = CreateProfile(seed: 42);
        var changedProfile = CreateProfile(seed: 43);
        var firstReplay = await RunAsync(firstProfile);
        var changedReplay = await RunAsync(changedProfile);
        var projectJson = "{\"schema\":\"1.2\",\"id\":\"package-fixture\",\"name\":\"Package fixture\"}";
        var firstPackage = DeterministicSimulationRunResultPackage.FromReplay(
            "package-fixture", "Package fixture", "goal2-fixture.ovmachine", projectJson,
            TimeSpan.FromMilliseconds(5), firstProfile, firstReplay);
        var changedPackage = DeterministicSimulationRunResultPackage.FromReplay(
            "package-fixture", "Package fixture", "goal2-fixture.ovmachine", projectJson,
            TimeSpan.FromMilliseconds(5), changedProfile, changedReplay);

        var comparison = firstPackage.CompareTo(changedPackage);

        Assert.False(comparison.IsMatch);
        Assert.Equal("ScenarioMismatch", comparison.MismatchCode);
        Assert.Contains("seed", comparison.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompareTo_LocatesFirstTickForEachEvidenceKind()
    {
        var profile = CreateProfile(seed: 42);
        var replay = await RunAsync(profile);
        var baseline = CreatePackage(profile, replay);
        var firstCommand = replay.CommandResults[0];
        var commandReplay = replay with
        {
            CommandResults = replay.CommandResults
                .Select((result, index) => index == 0
                    ? result with { Detail = $"{result.Detail} changed" }
                    : result)
                .ToArray()
        };
        AssertMismatch(
            baseline,
            CreatePackage(profile, commandReplay),
            "Command",
            firstCommand.AppliedTick);

        var conditionSample = replay.ConditionHistory[3];
        var conditionReplay = replay with
        {
            ConditionHistory = replay.ConditionHistory
                .Select(sample => sample.TickIndex == conditionSample.TickIndex
                    ? sample with { HealthScore = sample.HealthScore - 1 }
                    : sample)
                .ToArray()
        };
        AssertMismatch(
            baseline,
            CreatePackage(profile, conditionReplay),
            "Condition",
            conditionSample.TickIndex);

        var sourceSnapshot = replay.SnapshotHistory[3];
        var faultReplay = ReplaceSnapshot(
            replay,
            sourceSnapshot,
            CloneSnapshot(
                sourceSnapshot,
                faults: sourceSnapshot.Faults.Append(new SimulationFaultSnapshot(
                    SimulationFaultKind.CylinderTravelBlocked,
                    "equipment-1",
                    null,
                    sourceSnapshot.TickIndex,
                    sourceSnapshot.SimulationTime))));
        AssertMismatch(
            baseline,
            CreatePackage(profile, faultReplay),
            "Fault",
            sourceSnapshot.TickIndex);

        var workpieceReplay = ReplaceSnapshot(
            replay,
            sourceSnapshot,
            CloneSnapshot(
                sourceSnapshot,
                workpieces: sourceSnapshot.Workpieces.Select(item => item with { X = item.X + 1 })));
        AssertMismatch(
            baseline,
            CreatePackage(profile, workpieceReplay),
            "Workpiece",
            sourceSnapshot.TickIndex);

        var signalReplay = ReplaceSnapshot(
            replay,
            sourceSnapshot,
            CloneSnapshot(
                sourceSnapshot,
                signals: sourceSnapshot.Signals.Append(
                    new OpenVisionLab.Machine.IO.Channels.DigitalSignalSnapshot(
                        "signal.test",
                        "Test",
                        OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput,
                        true))));
        AssertMismatch(
            baseline,
            CreatePackage(profile, signalReplay),
            "Signal",
            sourceSnapshot.TickIndex);

        var snapshotReplay = ReplaceSnapshot(
            replay,
            sourceSnapshot,
            CloneSnapshot(sourceSnapshot, timeScale: sourceSnapshot.TimeScale + 1));
        AssertMismatch(
            baseline,
            CreatePackage(profile, snapshotReplay),
            "Snapshot",
            sourceSnapshot.TickIndex);

        var sourceEvent = replay.EventHistory[0];
        var eventReplay = replay with
        {
            EventHistory = replay.EventHistory
                .Select((item, index) => index == 0
                    ? item with { Message = $"{item.Message} changed" }
                    : item)
                .ToArray()
        };
        AssertMismatch(
            baseline,
            CreatePackage(profile, eventReplay),
            "Event",
            sourceEvent.TickIndex);

        var missingTick = replay.ConditionHistory[5].TickIndex;
        var missingTickReplay = replay with
        {
            CommandResults = replay.CommandResults
                .Where(result => result.AppliedTick != missingTick)
                .ToArray(),
            ConditionHistory = replay.ConditionHistory
                .Where(sample => sample.TickIndex != missingTick)
                .ToArray(),
            Transitions = replay.Transitions
                .Where(transition => transition.TickIndex != missingTick)
                .ToArray(),
            SnapshotHistory = replay.SnapshotHistory
                .Where(snapshot => snapshot.TickIndex != missingTick)
                .ToArray(),
            EventHistory = replay.EventHistory
                .Where(item => item.TickIndex != missingTick)
                .ToArray()
        };
        AssertMismatch(
            baseline,
            CreatePackage(profile, missingTickReplay),
            "Tick",
            missingTick);
    }

    private static DeterministicConditionScenarioProfile CreateProfile(int seed) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "package-condition",
            "Package condition",
            "Single-run package fixture.",
            "equipment-1",
            seed,
            10,
            MinimumStateTicks: 2,
            JitterTicks: 0);

    [Fact]
    public async Task ContextValidation_RejectsChangedProjectScenarioAndEvidence()
    {
        var profile = CreateProfile(seed: 42);
        var package = CreatePackage(profile, await RunAsync(profile));
        const string projectJson =
            "{\"schema\":\"1.2\",\"id\":\"package-fixture\",\"name\":\"Package fixture\"}";

        Assert.True(package.HasValidEvidenceHash());
        Assert.True(package.IsForContext(
            "package-fixture",
            projectJson,
            TimeSpan.FromMilliseconds(5),
            profile));
        Assert.False(package.IsForContext(
            "package-fixture",
            projectJson.Replace("Package fixture", "Changed fixture", StringComparison.Ordinal),
            TimeSpan.FromMilliseconds(5),
            profile));
        Assert.False(package.IsForContext(
            "package-fixture",
            projectJson,
            TimeSpan.FromMilliseconds(5),
            CreateProfile(seed: 43)));
        Assert.False((package with { EvidenceHash = new string('0', 64) }).HasValidEvidenceHash());
        Assert.False((package with { WorkpieceHash = new string('0', 64) }).HasValidEvidenceHash());
        Assert.False((package with { IsSuccess = !package.IsSuccess }).HasValidEvidenceHash());
        Assert.False((package with { ExecutedTicks = package.ExecutedTicks + 1 }).HasValidEvidenceHash());
        Assert.False((package with { FailureReason = "tampered" }).HasValidEvidenceHash());
    }

    private static DeterministicSimulationRunResultPackage CreatePackage(
        DeterministicConditionScenarioProfile profile,
        DeterministicConditionScenarioReplayResult replay) =>
        DeterministicSimulationRunResultPackage.FromReplay(
            "package-fixture",
            "Package fixture",
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\goal2-fixture.ovmachine",
            "{\"schema\":\"1.2\",\"id\":\"package-fixture\",\"name\":\"Package fixture\"}",
            TimeSpan.FromMilliseconds(5),
            profile,
            replay);

    private static void AssertMismatch(
        DeterministicSimulationRunResultPackage baseline,
        DeterministicSimulationRunResultPackage changed,
        string evidenceKind,
        long tickIndex)
    {
        var comparison = baseline.CompareTo(changed);

        Assert.False(comparison.IsMatch);
        Assert.Equal($"{evidenceKind}EvidenceMismatch", comparison.MismatchCode);
        var mismatch = Assert.IsType<DeterministicSimulationEvidenceMismatch>(comparison.FirstMismatch);
        Assert.Equal(evidenceKind, mismatch.EvidenceKind);
        Assert.Equal(tickIndex, mismatch.TickIndex);
        Assert.Equal("equipment-1", mismatch.TargetId);
        Assert.NotEqual(mismatch.ExpectedHash, mismatch.ActualHash);
    }

    private static DeterministicConditionScenarioReplayResult ReplaceSnapshot(
        DeterministicConditionScenarioReplayResult replay,
        SimulationSnapshot source,
        SimulationSnapshot replacement) =>
        replay with
        {
            SnapshotHistory = replay.SnapshotHistory
                .Select(snapshot => ReferenceEquals(snapshot, source) ? replacement : snapshot)
                .ToArray()
        };

    private static SimulationSnapshot CloneSnapshot(
        SimulationSnapshot source,
        double? timeScale = null,
        IEnumerable<OpenVisionLab.Machine.IO.Channels.DigitalSignalSnapshot>? signals = null,
        IEnumerable<SimulationFaultSnapshot>? faults = null,
        IEnumerable<PickPlaceWorkpieceSnapshot>? workpieces = null) =>
        new(
            source.SimulationTime,
            source.TickIndex,
            source.RunMode,
            source.ControlOwner,
            timeScale ?? source.TimeScale,
            source.Axes,
            source.SignalRevision,
            signals ?? source.Signals,
            source.Sequences,
            source.Cameras,
            source.AutomaticRun,
            source.LayoutComponents,
            faults ?? source.Faults,
            source.ConditionScenario,
            workpieces ?? source.Workpieces);

    private static async Task<DeterministicConditionScenarioReplayResult> RunAsync(
        DeterministicConditionScenarioProfile profile)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    new[]
                    {
                        new AxisConfiguration { Id = "x", Name = "X", MaximumPosition = 500 },
                        new AxisConfiguration { Id = "y", Name = "Y", MaximumPosition = 500 }
                    },
                    new[]
                    {
                        new OpenVisionLab.Machine.Core.Channels.ChannelDefinition
                        {
                            Id = "do.gripper",
                            Name = "Gripper",
                            Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput
                        }
                    },
                    Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>(),
                    Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
                    automaticRun: null,
                    new MachineLayoutRuntimeConfiguration(
                        "main",
                        "Main",
                        new LayoutComponentRuntimeConfiguration[]
                        {
                            new MachineFrameRuntimeConfiguration(
                                "equipment-1",
                                "Equipment",
                                new LayoutRuntimeTransform(0, 0),
                                new LayoutRuntimeSize(10, 10))
                        }),
                    new PickPlaceWorkpieceRuntimeConfiguration(
                        "part-1",
                        "Part",
                        "x",
                        "y",
                        "do.gripper",
                        240,
                        120))));
        Assert.True(configured.IsAccepted, configured.Detail);

        var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(engine, profile);
        await engine.StopAsync();
        return replay;
    }
}
