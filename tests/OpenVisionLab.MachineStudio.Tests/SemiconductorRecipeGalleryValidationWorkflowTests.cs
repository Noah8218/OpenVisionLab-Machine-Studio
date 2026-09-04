using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SemiconductorRecipeGalleryValidationWorkflowTests
{
    [Fact]
    public async Task ValidateAsyncRunsBundledRecipeThroughDeterministicDryRun()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "01-FoupLoadPort.ovmachine");

        var result = await new SemiconductorRecipeGalleryValidationWorkflow()
            .ValidateAsync(sourcePath);

        Assert.True(File.Exists(sourcePath));
        Assert.True(result.IsPassed, result.Detail);
        Assert.NotNull(result.DryRunResult);
        Assert.Equal(SemiconductorRecipeGalleryValidationFailureStage.None, result.FailureStage);
    }

    [Fact]
    public async Task ValidateAsyncReportsMissingAutomaticSequenceWithoutRunningDryRun()
    {
        var sourcePath = CreateProjectFile(new MachineProjectDocument { Name = "Missing sequence" });
        try
        {
            var result = await new SemiconductorRecipeGalleryValidationWorkflow()
                .ValidateAsync(sourcePath);

            Assert.False(result.IsPassed);
            Assert.Null(result.DryRunResult);
            Assert.Equal(
                SemiconductorRecipeGalleryValidationFailureStage.SequenceMissing,
                result.FailureStage);
            Assert.Null(result.FailureStepId);
            Assert.Empty(result.Detail);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(sourcePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsyncReportsCompileRejectionAndPreservesRunnerDetail()
    {
        var project = new MachineProjectDocument { Name = "Rejected recipe" };
        project.Simulation.FixedStepMilliseconds = 0;
        project.Simulation.AutomaticRun = new AutomaticRunDefinition
        {
            SequenceId = "sequence.load"
        };
        project.Sequences.Add(new OpenVisionLab.Machine.Core.Sequences.SequenceDefinition
        {
            Id = "sequence.load",
            Name = "Load sequence"
        });
        var sourcePath = CreateProjectFile(project);
        try
        {
            var result = await new SemiconductorRecipeGalleryValidationWorkflow()
                .ValidateAsync(sourcePath);

            Assert.False(result.IsPassed);
            Assert.NotNull(result.DryRunResult);
            Assert.Equal(
                SemiconductorRecipeGalleryValidationFailureStage.Compile,
                result.FailureStage);
            Assert.Null(result.FailureStepId);
            Assert.Contains("fixed step", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(sourcePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsyncConvertsLoadFailureToLoadStage()
    {
        var result = await new SemiconductorRecipeGalleryValidationWorkflow()
            .ValidateAsync(Path.Combine(
                @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\semiconductor-recipe-gallery-validation-tests",
                Guid.NewGuid().ToString("N"),
                "missing.ovmachine"));

        Assert.False(result.IsPassed);
        Assert.Null(result.DryRunResult);
        Assert.Equal(SemiconductorRecipeGalleryValidationFailureStage.Load, result.FailureStage);
        Assert.Null(result.FailureStepId);
        Assert.NotEmpty(result.Detail);
    }

    private static string CreateProjectFile(MachineProjectDocument project)
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\semiconductor-recipe-gallery-validation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "recipe.ovmachine");
        File.WriteAllText(path, new ProjectDocumentStore().Serialize(project));
        return path;
    }
}
