using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSimulationEvidenceExchangePackageTests
{
    private const string ProjectJson =
        "{\"schema\":\"1.2\",\"id\":\"exchange-fixture\",\"name\":\"Exchange fixture\"}";
    private const string ProjectPath =
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0025-portable-evidence\\exchange-fixture.ovmachine";

    [Fact]
    public async Task Create_SaveLoad_RoundTripsPortableEvidenceWithoutSessionState()
    {
        var profile = CreateProfile(seed: 42);
        var baseline = await RunPackageAsync(profile);
        var batch = await new DeterministicSimulationBatchRunner().RunAsync(
            new DeterministicSimulationBatchDefinition(
                "exchange-fixture:exchange-condition",
                RepetitionCount: 2,
                BuildIdentity: "test-build"),
            (_, cancellationToken) => RunPackageAsync(profile, cancellationToken),
            baseline);

        var exchange = DeterministicSimulationEvidenceExchangePackage.Create(batch, baseline);
        var json = DeterministicSimulationEvidenceExchangePackage.SaveToJson(exchange);

        Assert.True(exchange.HasValidEvidenceHash());
        Assert.DoesNotContain("exchange-fixture.ovmachine", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeDebugger", json, StringComparison.Ordinal);
        Assert.DoesNotContain("acknowledg", json, StringComparison.OrdinalIgnoreCase);

        var artifactPath = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0025-portable-evidence",
            "simulation-exchange-roundtrip.ovsim-evidence.json");
        DeterministicSimulationEvidenceExchangePackage.SaveToJson(exchange, artifactPath);
        var loaded = Assert.IsType<DeterministicSimulationEvidenceExchangePackage>(
            DeterministicSimulationEvidenceExchangePackage.LoadFromJson(artifactPath));

        Assert.True(loaded.HasValidEvidenceHash());
        Assert.Equal(json, DeterministicSimulationEvidenceExchangePackage.SaveToJson(loaded));
        Assert.True(loaded.IsForContext(
            "exchange-fixture",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            profile,
            "test-build"));
        Assert.True(loaded.TryGetPackages(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0025-portable-evidence\\imported.ovmachine",
            out var importedBatch,
            out var importedBaseline));
        Assert.True(batch.IsEquivalentTo(importedBatch));
        Assert.True(baseline.IsEquivalentTo(importedBaseline));
        Assert.All(importedBatch.Runs, run => Assert.EndsWith("imported.ovmachine", run.Result.ProjectPath));
        Assert.Equal(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0025-portable-evidence\\imported.ovmachine",
            importedBaseline!.ProjectPath);
    }

    [Fact]
    public async Task Validation_RejectsTamperedSchemaPayloadAndContext()
    {
        var profile = CreateProfile(seed: 42);
        var baseline = await RunPackageAsync(profile);
        var batch = await new DeterministicSimulationBatchRunner().RunAsync(
            new DeterministicSimulationBatchDefinition(
                "exchange-fixture:exchange-condition",
                RepetitionCount: 1,
                BuildIdentity: "test-build"),
            (_, cancellationToken) => RunPackageAsync(profile, cancellationToken),
            baseline);
        var exchange = DeterministicSimulationEvidenceExchangePackage.Create(batch, baseline);

        Assert.False((exchange with { SchemaVersion = 99 }).HasValidEvidenceHash());
        Assert.False((exchange with { EvidenceHash = new string('0', 64) }).HasValidEvidenceHash());

        var tamperedPayload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { schemaVersion = 2, batchId = "tampered" });
        Assert.False((exchange with { BatchResult = tamperedPayload }).HasValidEvidenceHash());

        Assert.False(exchange.IsForContext(
            "exchange-fixture",
            ProjectJson.Replace("Exchange fixture", "Changed fixture", StringComparison.Ordinal),
            TimeSpan.FromMilliseconds(5),
            profile,
            "test-build"));
        Assert.False(exchange.IsForContext(
            "exchange-fixture",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            profile,
            "changed-build"));
        Assert.False(exchange.IsForContext(
            "exchange-fixture",
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            CreateProfile(seed: 43),
            "test-build"));
    }

    private static DeterministicConditionScenarioProfile CreateProfile(int seed) =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "exchange-condition",
            "Exchange condition",
            "Portable exchange fixture.",
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
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync(cancellationToken);
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                    Array.Empty<ChannelDefinition>(),
                    Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>(),
                    Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
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
            "exchange-fixture",
            "Exchange fixture",
            ProjectPath,
            ProjectJson,
            TimeSpan.FromMilliseconds(5),
            profile,
            replay);
    }
}
