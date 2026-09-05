using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class UnifiedCommissioningEvidenceViewModelTests
{
    [Fact]
    public void ExportWithoutReadyInputsDoesNotWriteOrChangeArtifact()
    {
        OpenVisionLanguageService.Load();
        var path = CreateArtifactPath("export");
        var statuses = new List<string>();
        var logs = new List<string>();
        var viewModel = CreateViewModel(
            canExport: false,
            canImport: false,
            statuses,
            logs,
            () => null);

        try
        {
            Assert.False(viewModel.CanExport);
            Assert.False(viewModel.TryExport(path));
            Assert.Null(viewModel.LatestEvidence);
            Assert.False(File.Exists(path));
            Assert.Empty(statuses);
            Assert.Empty(logs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImportMalformedFileRejectsBeforeApplyingChildArtifacts()
    {
        OpenVisionLanguageService.Load();
        var path = CreateArtifactPath("malformed");
        var statuses = new List<string>();
        var logs = new List<string>();
        var appliedCount = 0;
        var viewModel = CreateViewModel(
            canExport: false,
            canImport: true,
            statuses,
            logs,
            () => null,
            () => appliedCount++);

        try
        {
            File.WriteAllText(path, "{ not valid json");

            Assert.False(viewModel.TryImport(path));
            Assert.Null(viewModel.LatestEvidence);
            Assert.Equal(0, appliedCount);
            Assert.Contains(statuses, status =>
                status.Contains(
                    OpenVisionLanguageService.T("Simulation.UnifiedEvidenceImportRejected"),
                    StringComparison.Ordinal));
            Assert.Contains(logs, log =>
                log.Contains("import rejected", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static UnifiedCommissioningEvidenceViewModel CreateViewModel(
        bool canExport,
        bool canImport,
        List<string> statuses,
        List<string> logs,
        Func<DeterministicVisionExecutionEvidencePackage?> getVisionEvidence,
        Action? applyImported = null) =>
        new(
            () => canExport,
            () => canImport,
            () => null,
            () => null,
            getVisionEvidence,
            () => new UnifiedCommissioningEvidenceContext(
                "project",
                "{}",
                TimeSpan.FromMilliseconds(5),
                new DeterministicConditionScenarioProfile(
                    SchemaVersion: DeterministicConditionScenarioProfile.CurrentSchemaVersion,
                    ScenarioId: "scenario",
                    Name: "Scenario",
                    Description: "Test scenario",
                    TargetId: "target",
                    Seed: 1,
                    DurationTicks: 1,
                    Assertions: []),
                "build",
                "project.ovmachine"),
            (_, _, _) => applyImported?.Invoke(),
            statuses.Add,
            logs.Add,
            () => { });

    private static string CreateArtifactPath(string name)
    {
        var root = Directory.Exists("D:\\")
            ? @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\unified-evidence-refactor-tests"
            : Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-Studio", "unified-evidence-refactor-tests");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{name}-{Guid.NewGuid():N}.json");
    }
}
