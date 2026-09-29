using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSimulationBatchResultReportTests
{
    [Fact]
    public async Task CreateMarkdown_IsDeterministicAndIncludesScenarioHashesAndAssertions()
    {
        var package = await CreateBatchAsync(seed: 42, acceptedSeed: 42);

        var first = DeterministicSimulationBatchResultReport.CreateMarkdown(package);
        var second = DeterministicSimulationBatchResultReport.CreateMarkdown(package);

        Assert.Equal(first, second);
        Assert.Contains("# OpenVisionLab Machine Studio Simulation Result", first, StringComparison.Ordinal);
        Assert.Contains($"`{package.BatchId}`", first, StringComparison.Ordinal);
        Assert.Contains($"`{package.ReferenceEvidenceHash}`", first, StringComparison.Ordinal);
        Assert.Contains($"`{package.EvidenceHash}`", first, StringComparison.Ordinal);
        Assert.Contains("package-project", first, StringComparison.Ordinal);
        Assert.Contains("report-condition", first, StringComparison.Ordinal);
        Assert.Contains("NoActiveFaults", first, StringComparison.Ordinal);
        Assert.Contains("PASS", first, StringComparison.Ordinal);
        Assert.Contains("## Result provenance", first, StringComparison.Ordinal);
        Assert.Contains("Deterministic local simulation (no external inspection executed)", first, StringComparison.Ordinal);
        Assert.Contains("Model/logic evidence only (not a real inspection)", first, StringComparison.Ordinal);
        Assert.Contains("Fixed-step simulation · 5 ms/tick", first, StringComparison.Ordinal);
        Assert.Contains($"Input/project SHA-256: `{package.Runs[0].Result.ProjectHash}`", first, StringComparison.Ordinal);
        Assert.Contains("Deterministic scenario model (no inspection model)", first, StringComparison.Ordinal);
        Assert.Contains("No mismatch recorded.", first, StringComparison.Ordinal);
        Assert.DoesNotContain("project.ovmachine", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateMarkdown_IncludesFirstMismatchWithoutMachineSpecificPath()
    {
        var package = await CreateBatchAsync(seed: 43, acceptedSeed: 42);

        Assert.False(package.IsSuccess);
        Assert.NotNull(package.FirstMismatch);
        var report = DeterministicSimulationBatchResultReport.CreateMarkdown(package);

        Assert.Contains("## First mismatch", report, StringComparison.Ordinal);
        Assert.Contains($"`{package.FirstMismatch!.Code}`", report, StringComparison.Ordinal);
        Assert.Contains($"`{package.FirstMismatch.ObservedTickIndex}`", report, StringComparison.Ordinal);
        Assert.Contains("MISMATCH", report, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveToMarkdown_WritesUtf8WithoutBomAndRejectsInvalidPackage()
    {
        var package = await CreateBatchAsync(seed: 42, acceptedSeed: 42);
        var directory = Path.Combine(TestStorage.RootPath, "simulation-batch-report-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "result.ovsim-report.md");
        try
        {
            DeterministicSimulationBatchResultReport.SaveToMarkdown(package, path);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal(
                DeterministicSimulationBatchResultReport.CreateMarkdown(package),
                File.ReadAllText(path));

            var originalBytes = bytes.ToArray();
            var invalid = package with { EvidenceHash = new string('0', 64) };
            var invalidDirectory = Path.Combine(directory, "invalid-output");
            var invalidPath = Path.Combine(invalidDirectory, "result.ovsim-report.md");
            Assert.Throws<InvalidOperationException>(() =>
                DeterministicSimulationBatchResultReport.SaveToMarkdown(invalid, invalidPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(invalidDirectory));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<DeterministicSimulationBatchResultPackage> CreateBatchAsync(
        int seed,
        int acceptedSeed)
    {
        var accepted = await CreateRunAsync(acceptedSeed);
        var result = await CreateRunAsync(seed);
        return await new DeterministicSimulationBatchRunner().RunAsync(
            new DeterministicSimulationBatchDefinition(
                "package-project:report-condition",
                RepetitionCount: 1,
                BuildIdentity: "report-test-build"),
            (_, _) => Task.FromResult(result),
            accepted);
    }

    private static async Task<DeterministicSimulationRunResultPackage> CreateRunAsync(int seed)
    {
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "report-condition",
            "Report condition",
            "Report formatter fixture.",
            "equipment-1",
            seed,
            DurationTicks: 10,
            MinimumStateTicks: 2,
            JitterTicks: 0,
            Assertions:
            [
                new DeterministicScenarioAssertion(
                    "no-faults",
                    DeterministicScenarioAssertionKind.NoActiveFaults)
            ]);
        var fixedStep = TimeSpan.FromMilliseconds(5);
        const string projectJson = "{\"id\":\"package-project\",\"name\":\"Package project\"}";
        var projectPath = Path.Combine(TestStorage.RootPath, "report-fixture", "project.ovmachine");
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = fixedStep });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                    Array.Empty<ChannelDefinition>(),
                    Array.Empty<CompiledSequence>(),
                    Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
                    automaticRun: null,
                    new MachineLayoutRuntimeConfiguration(
                        "main",
                        "Main",
                        [
                            new MachineFrameRuntimeConfiguration(
                                "equipment-1",
                                "Equipment",
                                new LayoutRuntimeTransform(0, 0),
                                new LayoutRuntimeSize(10, 10))
                        ]))),
            CancellationToken.None);
        Assert.True(configured.IsAccepted, configured.Detail);
        var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(
            engine,
            profile);
        await engine.StopAsync();
        return DeterministicSimulationRunResultPackage.FromReplay(
            "package-project",
            "Package project",
            projectPath,
            projectJson,
            fixedStep,
            profile,
            replay);
    }
}
