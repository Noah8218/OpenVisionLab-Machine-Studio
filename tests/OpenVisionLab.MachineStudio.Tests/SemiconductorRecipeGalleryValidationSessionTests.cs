using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SemiconductorRecipeGalleryValidationSessionTests
{
    [Fact]
    public async Task ValidationSessionPreservesItemOrderAndProgressCallbacks()
    {
        var validations = new Queue<SemiconductorRecipeGalleryValidationResult>(
        [
            new(
                new RecipeDryRunResult(
                    RecipeDryRunOutcome.Completed,
                    "sequence",
                    "Sequence",
                    0,
                    1,
                    Array.Empty<RecipeDryRunStepTrace>(),
                    null,
                    null,
                    null,
                    "Completed"),
                SemiconductorRecipeGalleryValidationFailureStage.None,
                null,
                "Completed"),
            new(
                null,
                SemiconductorRecipeGalleryValidationFailureStage.SequenceMissing,
                null,
                string.Empty)
        ]);
        var session = new SemiconductorRecipeGalleryValidationSession(
            _ => Task.FromResult(validations.Dequeue()));
        var items = new[] { CreateItem("first"), CreateItem("second") };
        var validated = new List<string>();
        var progress = new List<(int Count, int Total)>();

        await session.ValidateAsync(
            items,
            outcome => validated.Add(outcome.Item.FileName),
            (count, total) => progress.Add((count, total)));

        Assert.Equal(new[] { "first", "second" }, validated);
        Assert.Equal(new[] { (1, 2), (2, 2) }, progress);
        Assert.True(items[0].IsValidationRunning);
        Assert.True(items[1].IsValidationRunning);
    }

    private static SemiconductorRecipeGalleryItemViewModel CreateItem(string fileName) => new()
    {
        SourcePath = fileName,
        FileName = fileName,
        DisplayName = fileName,
        ProjectSchema = "1",
        SequenceName = "sequence",
        EquipmentFocus = "equipment",
        TopologySummary = "topology",
        AxisCount = 0,
        SensorCount = 0,
        CylinderCount = 0,
        ConveyorCount = 0,
        WorkpieceCount = 0,
        DeviceCount = 0,
        ChannelCount = 0,
        ComponentCount = 0,
        StepCount = 0
    };
}
