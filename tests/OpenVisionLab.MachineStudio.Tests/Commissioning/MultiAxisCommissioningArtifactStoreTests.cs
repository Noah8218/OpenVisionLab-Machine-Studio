using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MultiAxisCommissioningArtifactStoreTests
{
    [Fact]
    public void PersistWithoutArtifactsDoesNotBuildContext()
    {
        var store = new MultiAxisCommissioningArtifactStore();
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
        Assert.Equal(MultiAxisCommissioningArtifactState.None, store.State);
    }

    [Fact]
    public void RestoreWithoutSidecarsDoesNotBuildContext()
    {
        var projectPath = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\multi-axis-artifact-store-tests",
            Guid.NewGuid().ToString("N"),
            "project.ovmachine");
        var store = new MultiAxisCommissioningArtifactStore();
        var contextBuilds = 0;

        store.Restore(
            "project-id",
            projectPath,
            () =>
            {
                contextBuilds++;
                return null;
            });

        Assert.Equal(0, contextBuilds);
        Assert.Equal(MultiAxisCommissioningArtifactState.None, store.State);
        Assert.Null(store.LatestResult);
        Assert.Null(store.AcceptedBaseline);
        Assert.Empty(store.History.Entries);
    }

    [Fact]
    public void ClearBaselineWithoutProjectPathClearsMemoryState()
    {
        var store = new MultiAxisCommissioningArtifactStore();
        store.Reset("project-id");

        var error = store.ClearBaseline(null);

        Assert.Null(error);
        Assert.Null(store.AcceptedBaseline);
        Assert.Equal(MultiAxisCommissioningArtifactState.None, store.State);
    }
}
