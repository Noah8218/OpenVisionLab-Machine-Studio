using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch043InterlockRepairLabTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task FaultSample_StopsAtWrongInterlock_RepairRevalidatesAndPreservesSource()
    {
        const string sampleRelativePath =
            "MCH043-InterlockRepair/AutomaticTransferCell-MCH043-InterlockFault.ovmachine";
        string samplePath = Path.Combine(
            AppContext.BaseDirectory,
            sampleRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string originalSamplePath = Path.Combine(AppContext.BaseDirectory, "AutomaticTransferCell.ovmachine");
        byte[] sourceBytes = File.ReadAllBytes(samplePath);
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        string originalSampleHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalSamplePath)));
        var store = new ProjectDocumentStore();
        MachineProjectDocument project = store.Load(Encoding.UTF8.GetString(sourceBytes));
        var stationReadyCommand = project.Channels.Single(channel =>
            channel.Id == "do.station-ready");

        Assert.Equal(new[] { "di.cylinder-1.retracted" }, stationReadyCommand.InterlockIds);
        MachineProjectRuntimeCompilationResult faultyCompilation = Compile(project);
        Assert.True(faultyCompilation.IsSuccess, ErrorSummary(faultyCompilation));

        RecipeDryRunResult faultyRun = await new DeterministicRecipeDryRunRunner().RunAsync(
            project,
            "auto-transfer-cycle",
            maximumTicks: 1000);

        Assert.Equal(RecipeDryRunOutcome.Faulted, faultyRun.Outcome);
        Assert.Equal("station-ready-on", faultyRun.FirstIssue?.StepId);
        Assert.Equal("SignalWriteFailed", faultyRun.FirstIssue?.Code);
        Assert.Contains(
            "Signal write failed",
            faultyRun.FirstIssue?.Detail ?? string.Empty,
            StringComparison.Ordinal);
        SimulationSnapshot faultySnapshot = Assert.IsType<SimulationSnapshot>(faultyRun.FinalSnapshot);
        SequenceExecutionSnapshot faultySequence = Assert.Single(faultySnapshot.Sequences);
        Assert.Equal(SequenceContextErrorCode.Rejected, faultySequence.LastError?.ContextError?.Code);
        Assert.Contains(
            "InterlockNotSatisfied",
            faultySequence.LastError?.ContextError?.Message ?? string.Empty,
            StringComparison.Ordinal);

        stationReadyCommand.InterlockIds.Clear();
        stationReadyCommand.InterlockIds.Add("di.cylinder-1.extended");
        MachineProjectRuntimeCompilationResult repairedCompilation = Compile(project);
        Assert.True(repairedCompilation.IsSuccess, ErrorSummary(repairedCompilation));

        RecipeDryRunResult repairedRun = await new DeterministicRecipeDryRunRunner().RunAsync(
            project,
            "auto-transfer-cycle",
            maximumTicks: 2000);

        SimulationSnapshot repairedSnapshot = Assert.IsType<SimulationSnapshot>(repairedRun.FinalSnapshot);
        var repairedCylinder = repairedSnapshot.LayoutComponents.Single(component => component.Id == "cylinder-1");
        var repairedExtended = repairedSnapshot.Signals.Single(signal => signal.Id == "di.cylinder-1.extended");
        var repairedRetracted = repairedSnapshot.Signals.Single(signal => signal.Id == "di.cylinder-1.retracted");
        Assert.True(
            repairedRun.Outcome == RecipeDryRunOutcome.Completed,
            $"{repairedRun.Outcome} at {repairedRun.FirstIssue?.StepId}: {repairedRun.FirstIssue?.Detail}; " +
            $"tick={repairedSnapshot.TickIndex}, cylinder={repairedCylinder.CylinderState}, " +
            $"extended={repairedExtended.Value}, retracted={repairedRetracted.Value}");
        Assert.Null(repairedRun.FirstIssue);
        Assert.InRange(repairedRun.ExecutedTicks, 1, repairedRun.MaximumTicks - 1);
        Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(samplePath))));
        Assert.Equal(originalSampleHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalSamplePath))));

        string evidenceDirectory = Path.Combine(
            TestStorage.RootPath,
            "mch-043-interlock-repair-20260916");
        Directory.CreateDirectory(evidenceDirectory);
        string evidencePath = Path.Combine(evidenceDirectory, "mch-043-repair-observation.json");
        var evidence = new
        {
            schemaVersion = 1,
            sample = sampleRelativePath,
            sourceSha256 = sourceHash,
            originalSampleSha256 = originalSampleHash,
            faultyInterlock = "di.cylinder-1.retracted",
            expectedInterlock = "di.cylinder-1.extended",
            faultyOutcome = faultyRun.Outcome.ToString(),
            faultyStep = faultyRun.FirstIssue?.StepId,
            faultyDetail = faultyRun.FirstIssue?.Detail,
            repairedOutcome = repairedRun.Outcome.ToString(),
            repairedExecutedTicks = repairedRun.ExecutedTicks,
            sourcePreserved = sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(samplePath))),
            originalSamplePreserved = originalSampleHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalSamplePath))),
            runOrSaveStartedByTest = false
        };
        File.WriteAllText(
            evidencePath,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static MachineProjectRuntimeCompilationResult Compile(MachineProjectDocument project) =>
        new MachineProjectRuntimeCompiler(FixedStep).Compile(project);

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(
            Environment.NewLine,
            compilation.Errors.Select(error =>
                $"{error.Code} [{error.TargetId}]: {error.Message}"));
}
