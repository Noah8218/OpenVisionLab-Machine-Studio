using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Sequences;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class RepresentativeRecipeCheckpointTemplateTests
{
    public static TheoryData<string> SemiconductorRecipeFiles =>
        DeterministicRecipeDryRunRunnerTests.SemiconductorRecipeFiles;

    [Theory]
    [MemberData(nameof(SemiconductorRecipeFiles))]
    public async Task Template_AppliesFivePersistedPassingChecks(string fileName)
    {
        var store = new ProjectDocumentStore();
        var project = LoadRecipe(fileName);
        ClearCheckpoints(project);
        var sequence = Assert.Single(project.Sequences);
        var template = new RepresentativeRecipeCheckpointTemplate();
        var beforePreview = store.Serialize(project);

        var preview = template.Preview(project, sequence.Id);

        Assert.Equal(beforePreview, store.Serialize(project));
        Assert.Equal(5, preview.ProposedCount);
        Assert.Equal(0, preview.ExistingCount);
        Assert.Equal(0, preview.UnavailableCount);
        Assert.Equal(5, template.Apply(project, preview));

        var reloaded = store.Load(store.Serialize(project));
        Assert.Equal(5, Assert.Single(reloaded.Sequences).Steps.Count(step =>
            !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
            && !string.IsNullOrWhiteSpace(step.ExpectedState)));
        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(reloaded, sequence.Id);
        Assert.Equal(RecipeDryRunOutcome.Completed, dryRun.Outcome);
        Assert.Equal(5, dryRun.Timeline.Count(step => step.HasCheckpoint));
        Assert.All(
            dryRun.Timeline.Where(step => step.HasCheckpoint),
            step => Assert.True(step.Checkpoint?.IsPassed, $"{fileName}: {step.StepId}"));
    }

    [Fact]
    public void Template_RecognizesTheFiveBundledChecksWithoutDuplicatingThem()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        var sequence = Assert.Single(project.Sequences);
        var template = new RepresentativeRecipeCheckpointTemplate();

        var preview = template.Preview(project, sequence.Id);

        Assert.Equal(0, preview.ProposedCount);
        Assert.Equal(5, preview.ExistingCount);
        Assert.Equal(0, preview.UnavailableCount);
        Assert.Equal(0, template.Apply(project, preview));
    }

    [Fact]
    public void Template_PreservesAnExistingCheckOnAProposedBoundary()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        ClearCheckpoints(project);
        var sequence = Assert.Single(project.Sequences);
        var occupied = sequence.Steps.Single(step => step.Id == "wait-process-position");
        occupied.ExpectedTargetId = "process-cylinder";
        occupied.ExpectedState = "Fault";
        var template = new RepresentativeRecipeCheckpointTemplate();

        var preview = template.Preview(project, sequence.Id);

        var sensor = Assert.Single(preview.Entries, entry =>
            entry.Role == RepresentativeCheckpointRole.SensorDetected);
        Assert.Equal(RepresentativeCheckpointTemplateStatus.Unavailable, sensor.Status);
        Assert.Equal(
            RepresentativeCheckpointUnavailableReason.StepAlreadyHasCheckpoint,
            sensor.UnavailableReason);
        Assert.Equal(4, template.Apply(project, preview));
        Assert.Equal("process-cylinder", occupied.ExpectedTargetId);
        Assert.Equal("Fault", occupied.ExpectedState);
    }

    private static MachineProjectDocument LoadRecipe(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", fileName);
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static void ClearCheckpoints(MachineProjectDocument project)
    {
        foreach (var step in project.Sequences.SelectMany(sequence => sequence.Steps))
        {
            step.ExpectedTargetId = null;
            step.ExpectedState = null;
        }
    }
}
