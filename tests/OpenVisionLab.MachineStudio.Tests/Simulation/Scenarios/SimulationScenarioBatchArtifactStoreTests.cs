using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioBatchArtifactStoreTests
{
    [Fact]
    public void PersistWithoutArtifactsDoesNotBuildContext()
    {
        var store = new SimulationScenarioBatchArtifactStore();
        var contextBuilds = 0;

        var error = store.Persist(
            null,
            () =>
            {
                contextBuilds++;
                return null;
            });

        Assert.Null(error);
        Assert.Equal(0, contextBuilds);
        Assert.Equal(SimulationScenarioBatchArtifactState.None, store.State);
    }

    [Fact]
    public void RestoreWithoutSidecarsDoesNotBuildContext()
    {
        var projectPath = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\scenario-batch-artifact-store-tests",
            Guid.NewGuid().ToString("N"),
            "project.ovmachine");
        var store = new SimulationScenarioBatchArtifactStore();
        var contextBuilds = 0;

        store.Restore(
            projectPath,
            () =>
            {
                contextBuilds++;
                return null;
            });

        Assert.Equal(0, contextBuilds);
        Assert.Equal(SimulationScenarioBatchArtifactState.None, store.State);
        Assert.Null(store.LatestBatchResult);
        Assert.Null(store.AcceptedBatchBaseline);
    }

    [Fact]
    public void ImportMissingEvidenceReturnsStableRejectionDetail()
    {
        var store = new SimulationScenarioBatchArtifactStore();
        var contextBuilds = 0;

        var imported = store.TryImportEvidence(
            Path.Combine(
                @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\scenario-batch-artifact-store-tests",
                Guid.NewGuid().ToString("N"),
                "missing.json"),
            null,
            () =>
            {
                contextBuilds++;
                return null;
            },
            out var evidenceHash,
            out var rejectionDetail);

        Assert.False(imported);
        Assert.Equal(0, contextBuilds);
        Assert.Empty(evidenceHash);
        Assert.Equal("file could not be loaded", rejectionDetail);
        Assert.Equal(SimulationScenarioBatchArtifactState.None, store.State);
    }

    [Fact]
    public void PlanRetention_UsesStoreSidecarsAndLeavesFixedFiles()
    {
        var directory = Path.Combine(
            TestStorage.RootPath,
            "scenario-batch-artifact-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "project.ovmachine");
        var sidecarPath = $"{projectPath}.batch-result.json";
        var exportPath = Path.Combine(directory, "old-export.json");
        File.WriteAllText(sidecarPath, "fixed-sidecar");
        File.WriteAllText(exportPath, "old-export");

        try
        {
            var now = DateTimeOffset.UtcNow;
            File.SetLastWriteTimeUtc(exportPath, now.AddDays(-2).UtcDateTime);
            var plan = new SimulationScenarioBatchArtifactStore().PlanRetention(
                projectPath,
                [exportPath],
                new SimulationEvidenceRetentionPolicy(
                    MaximumAppendOnlyExportAge: TimeSpan.FromDays(1)),
                now,
                availableFreeBytes: 1_000);

            Assert.True(plan.IsSatisfiable);
            Assert.Equal(Path.GetFullPath(exportPath), Assert.Single(plan.DeletionCandidates).Path);
            var retained = Assert.Single(plan.RetainedArtifacts);
            Assert.Equal(Path.GetFullPath(sidecarPath), retained.Path);
            Assert.Equal(SimulationEvidenceArtifactStorageMode.FixedSidecar, retained.StorageMode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
