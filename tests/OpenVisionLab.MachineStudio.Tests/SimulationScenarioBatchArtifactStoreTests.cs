using OpenVisionLab.MachineStudio.ViewModel;
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
}
