using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeCheckpointTemplateViewModelTests
{
    [Fact]
    public void PreviewOwnsPresentationAndLeavesProjectUnchangedUntilApply()
    {
        var project = LoadRecipe();
        ClearCheckpoints(project);
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);
        var clearCount = 0;
        var applyCount = 0;
        var viewModel = new RecipeCheckpointTemplateViewModel(
            _ =>
            {
                applyCount++;
                return 0;
            },
            () => clearCount++);

        viewModel.Load(project);
        viewModel.PreviewCommand.Execute(null);

        Assert.Equal(1, clearCount);
        Assert.True(viewModel.IsPreviewVisible);
        Assert.Equal(5, viewModel.ProposedCount);
        Assert.Equal(5, viewModel.Items.Count);
        Assert.All(viewModel.Items, item => Assert.True(item.IsProposed));
        Assert.True(viewModel.ApplyCommand.CanExecute(null));
        Assert.Equal(before, store.Serialize(project));

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.IsPreviewVisible);
        Assert.Empty(viewModel.Items);
        Assert.Equal(0, applyCount);
        Assert.Equal(before, store.Serialize(project));
    }

    [Fact]
    public void ApplyDelegatesPreviewAndReloadRestoresExistingRows()
    {
        var project = LoadRecipe();
        ClearCheckpoints(project);
        var applied = 0;
        var viewModel = new RecipeCheckpointTemplateViewModel(
            preview =>
            {
                applied = new RepresentativeRecipeCheckpointTemplate().Apply(project, preview);
                return applied;
            },
            () => { });

        viewModel.Load(project);
        viewModel.PreviewCommand.Execute(null);
        viewModel.ApplyCommand.Execute(null);

        Assert.Equal(5, applied);
        Assert.True(project.Sequences.SelectMany(sequence => sequence.Steps).Count(step =>
            !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
            && !string.IsNullOrWhiteSpace(step.ExpectedState)) == 5);

        viewModel.Load(project);
        viewModel.PreviewCommand.Execute(null);

        Assert.Equal(0, viewModel.ProposedCount);
        Assert.Equal(5, viewModel.Items.Count(item => item.IsAlreadyConfigured));
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
    }

    private static MachineProjectDocument LoadRecipe() =>
        new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "01-FoupLoadPort.ovmachine")));

    private static void ClearCheckpoints(MachineProjectDocument project)
    {
        foreach (var step in project.Sequences.SelectMany(sequence => sequence.Steps))
        {
            step.ExpectedTargetId = null;
            step.ExpectedState = null;
        }
    }
}
