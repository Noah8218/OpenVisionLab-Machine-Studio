using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectDirtyStateTests
{
    [Fact]
    public void InitialEvidenceIsDirtyUntilAcceptedAsSaved()
    {
        var evidence = "initial";
        var state = new ProjectDirtyState(() => evidence);

        Assert.True(state.Refresh());
        Assert.True(state.HasUnsavedChanges);
        Assert.True(state.AcceptAsSaved());
        Assert.False(state.HasUnsavedChanges);
        Assert.False(state.Refresh());
    }

    [Fact]
    public void RefreshOnlyChangesStateWhenEvidenceChanges()
    {
        var evidence = "saved";
        var state = new ProjectDirtyState(() => evidence);

        state.AcceptAsSaved();
        Assert.False(state.Refresh());

        evidence = "changed";
        Assert.True(state.Refresh());
        Assert.True(state.HasUnsavedChanges);
        Assert.False(state.Refresh());
    }

    [Fact]
    public void AcceptAsSavedCapturesTheLatestEvidenceAndClearsDirtyState()
    {
        var evidence = "one";
        var state = new ProjectDirtyState(() => evidence);
        state.AcceptAsSaved();

        evidence = "two";
        Assert.True(state.Refresh());
        Assert.True(state.AcceptAsSaved());
        Assert.False(state.HasUnsavedChanges);
        Assert.False(state.Refresh());
    }
}
