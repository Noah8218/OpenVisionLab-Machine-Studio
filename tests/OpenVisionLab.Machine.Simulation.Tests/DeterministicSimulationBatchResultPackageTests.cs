using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSimulationBatchResultPackageTests
{
    private const string ProjectJson =
        "{\"schema\":\"1.2\",\"id\":\"batch-fixture\",\"name\":\"Batch fixture\"}";

    [Fact]
    public async Task RunAsync_RepeatedBatchIsDeterministicAndRoundTrips()
    {
        var profile = CreateProfile(seed: 42);
        var definition = new DeterministicSimulationBatchDefinition(
            "batch-repeat",
            RepetitionCount: 3,
            BuildIdentity: "test-build");
        var runner = new DeterministicSimulationBatchRunner();
        var accepted = await RunPackageAsync(profile);

        var first = await runner.RunAsync(
            definition,
            (_, cancellationToken) => RunPackageAsync(profile, cancellationToken),
            accepted);
        var second = await runner.RunAsync(
            definition,
            (_, cancellationToken) => RunPackageAsync(profile, cancellationToken),
            accepted);

        Assert.True(first.IsComplete);
        Assert.True(first.IsSuccess);
        Assert.Equal(3, first.CompletedRuns);
        Assert.All(first.Runs, run => Assert.True(run.ReferenceComparison.IsMatch));
        Assert.True(first.IsEquivalentTo(second));
        Assert.Equal(first.EvidenceHash, second.EvidenceHash);

        var artifactPath = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260810-goal3-batch",
            "deterministic-batch-result-package.json");
        DeterministicSimulationBatchResultPackage.SaveToJson(first, artifactPath);
        var loaded = Assert.IsType<DeterministicSimulationBatchResultPackage>(
            DeterministicSimulationBatchResultPackage.LoadFromJson(artifactPath));
        Assert.True(first.IsEquivalentTo(loaded));
    }

    [Fact]
    public async Task RunAsync_AcceptedBaselineReportsFirstScenarioMismatch()
    {
        var accepted = await RunPackageAsync(CreateProfile(seed: 42));
        var changed = CreateProfile(seed: 43);
        var definition = new DeterministicSimulationBatchDefinition(
            "batch-mismatch",
            RepetitionCount: 2,
            BuildIdentity: "test-build");

        var result = await new DeterministicSimulationBatchRunner().RunAsync(
            definition,
            (_, cancellationToken) => RunPackageAsync(changed, cancellationToken),
            accepted);

        Assert.True(result.IsComplete);
        Assert.False(result.IsSuccess);
        var mismatch = Assert.IsType<DeterministicSimulationBatchMismatch>(result.FirstMismatch);
        Assert.Equal(1, mismatch.RunIndex);
        Assert.Equal("ScenarioMismatch", mismatch.Code);
        Assert.Equal("equipment-1", mismatch.TargetId);
        Assert.Equal(changed.DurationTicks, mismatch.ObservedTickIndex);
        Assert.All(result.Runs, run => Assert.Equal("ScenarioMismatch", run.ReferenceComparison.MismatchCode));
    }

    [Fact]
    public async Task RunAsync_PropagatesExactFirstEvidenceTick()
    {
        var profile = CreateProfile(seed: 42);
        var accepted = await RunPackageAsync(profile);
        long changedTick = -1;
        var changed = await RunPackageAsync(
            profile,
            transform: replay =>
            {
                var sample = replay.ConditionHistory[4];
                changedTick = sample.TickIndex;
                return replay with
                {
                    ConditionHistory = replay.ConditionHistory
                        .Select(item => item.TickIndex == sample.TickIndex
                            ? item with { HealthScore = item.HealthScore - 1 }
                            : item)
                        .ToArray()
                };
            });
        var definition = new DeterministicSimulationBatchDefinition(
            "batch-evidence-mismatch",
            RepetitionCount: 1,
            BuildIdentity: "test-build");

        var result = await new DeterministicSimulationBatchRunner().RunAsync(
            definition,
            (_, _) => Task.FromResult(changed),
            accepted);

        var mismatch = Assert.IsType<DeterministicSimulationBatchMismatch>(result.FirstMismatch);
        Assert.Equal("ConditionEvidenceMismatch", mismatch.Code);
        Assert.Equal("Condition", mismatch.EvidenceKind);
        Assert.Equal(changedTick, mismatch.ObservedTickIndex);
        Assert.Equal("equipment-1", mismatch.TargetId);

        DeterministicSimulationBatchResultPackage.SaveToJson(
            result,
            Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260810-goal3-batch",
                "exact-first-mismatch.json"));
    }

    [Fact]
    public async Task RunAsync_CancellationReturnsNoPartialCompletedPackage()
    {
        var package = await RunPackageAsync(CreateProfile(seed: 42));
        using var cancellation = new CancellationTokenSource();
        var definition = new DeterministicSimulationBatchDefinition(
            "batch-cancel",
            RepetitionCount: 3,
            BuildIdentity: "test-build");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DeterministicSimulationBatchRunner().RunAsync(
                definition,
                (runIndex, _) =>
                {
                    if (runIndex == 1)
                    {
                        cancellation.Cancel();
                    }

                    return Task.FromResult(package);
                },
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task SaveToJson_IncompleteBatchDoesNotOverwriteAcceptedArtifact()
    {
        var definition = new DeterministicSimulationBatchDefinition(
            "batch-atomic-save",
            RepetitionCount: 1,
            BuildIdentity: "test-build");
        var complete = await new DeterministicSimulationBatchRunner().RunAsync(
            definition,
            (_, cancellationToken) => RunPackageAsync(CreateProfile(seed: 42), cancellationToken));
        var incomplete = complete with { IsComplete = false, IsSuccess = false };
        var artifactPath = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260810-goal3-batch",
            "accepted-batch.json");

        DeterministicSimulationBatchResultPackage.SaveToJson(complete, artifactPath);
        Assert.Throws<InvalidOperationException>(() =>
            DeterministicSimulationBatchResultPackage.SaveToJson(incomplete, artifactPath));

        var loaded = Assert.IsType<DeterministicSimulationBatchResultPackage>(
            DeterministicSimulationBatchResultPackage.LoadFromJson(artifactPath));
        Assert.True(complete.IsEquivalentTo(loaded));
    }

    private static DeterministicConditionScenarioProfile CreateProfile(int seed) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "batch-condition",
            "Batch condition",
            "Sequential batch fixture.",
            "equipment-1",
            seed,
            10,
            MinimumStateTicks: 2,
            JitterTicks: 0);

    [Fact]
    public async Task ContextValidation_RejectsChangedScenarioBuildAndEvidence()
    {
        var profile = CreateProfile(seed: 42);
        var definition = new DeterministicSimulationBatchDefinition(
            "batch-fixture:batch-condition",
            RepetitionCount: 2,
            BuildIdentity: "test-build");
        var package = await new DeterministicSimulationBatchRunner().RunAsync(
            definition,
            (_, cancellationToken) => RunPackageAsync(profile, cancellationToken));

        Assert.True(package.HasValidEvidenceHash());
        Assert.True(package.IsForContext(
            definition.BatchId,
            definition.BuildIdentity,
            definition.RepetitionCount,
            "batch-fixture",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            profile));
        Assert.False(package.IsForContext(
            definition.BatchId,
            "changed-build",
            definition.RepetitionCount,
            "batch-fixture",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            profile));
        Assert.False(package.IsForContext(
            definition.BatchId,
            definition.BuildIdentity,
            definition.RepetitionCount,
            "batch-fixture",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            CreateProfile(seed: 43)));
        Assert.False((package with { EvidenceHash = new string('0', 64) }).HasValidEvidenceHash());
        Assert.False((package with { IsSuccess = !package.IsSuccess }).HasValidEvidenceHash());
        Assert.False((package with
        {
            Runs = package.Runs.SetItem(
                0,
                package.Runs[0] with
                {
                    ReferenceComparison = package.Runs[0].ReferenceComparison with
                    {
                        Detail = "tampered"
                    }
                })
        }).HasValidEvidenceHash());
    }

    private static async Task<DeterministicSimulationRunResultPackage> RunPackageAsync(
        DeterministicConditionScenarioProfile profile,
        CancellationToken cancellationToken = default,
        Func<DeterministicConditionScenarioReplayResult, DeterministicConditionScenarioReplayResult>? transform = null)
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync(cancellationToken);
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                    Array.Empty<OpenVisionLab.Machine.Core.Channels.ChannelDefinition>(),
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
                        }))),
            cancellationToken);
        Assert.True(configured.IsAccepted, configured.Detail);

        var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(
            engine,
            profile,
            cancellationToken);
        await engine.StopAsync(cancellationToken);
        replay = transform?.Invoke(replay) ?? replay;
        return DeterministicSimulationRunResultPackage.FromReplay(
            "batch-fixture",
            "Batch fixture",
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\batch-fixture.ovmachine",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            profile,
            replay);
    }
}
