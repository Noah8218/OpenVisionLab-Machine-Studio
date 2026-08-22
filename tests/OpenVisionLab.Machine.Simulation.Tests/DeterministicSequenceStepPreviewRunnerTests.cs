using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Sequences;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSequenceStepPreviewRunnerTests
{
    public static TheoryData<string> SemiconductorRecipeFiles => new()
    {
        "01-FoupLoadPort.ovmachine",
        "02-CassetteMapper.ovmachine",
        "03-WaferPrealigner.ovmachine",
        "04-WaferOcrInspection.ovmachine",
        "05-LoadLockEntry.ovmachine",
        "06-SpinCoatTrack.ovmachine",
        "07-DevelopTrack.ovmachine",
        "08-DryEtchTransfer.ovmachine",
        "09-CmpTransfer.ovmachine",
        "10-MetrologySorter.ovmachine"
    };

    [Theory]
    [MemberData(nameof(SemiconductorRecipeFiles))]
    public async Task EverySemiconductorRecipe_PreviewsConnectedCylinder(string fileName)
    {
        var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(
            LoadRecipe(fileName),
            "automatic-cycle",
            "extend-cylinder",
            "process-cylinder");

        Assert.True(result.IsCompleted, $"{fileName}: {result.Outcome} - {result.Detail}");
        Assert.Equal(
            PneumaticCylinderState.Extended,
            result.FinalSnapshot!.LayoutComponents.Single(component =>
                component.Id == "process-cylinder").CylinderState);
    }

    [Fact]
    public async Task SetSignalPreview_CompletesCylinderMotionWithoutChangingProject()
    {
        var project = LoadRecipe();
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);

        var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(
            project,
            "automatic-cycle",
            "extend-cylinder",
            "process-cylinder");

        Assert.Equal(SequenceStepPreviewOutcome.Completed, result.Outcome);
        Assert.InRange(result.ExecutedTicks, 2, result.MaximumTicks - 1);
        Assert.True(result.FinalSnapshot!.Signals.Single(signal => signal.Id == "do.cylinder.extend").Value);
        var cylinder = result.FinalSnapshot.LayoutComponents.Single(component =>
            component.Id == "process-cylinder");
        Assert.Equal(PneumaticCylinderState.Extended, cylinder.CylinderState);
        Assert.Equal(before, store.Serialize(project));
    }

    [Fact]
    public async Task WaitSignalPreview_StopsAtHardTickLimit()
    {
        var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(
            LoadRecipe(),
            "automatic-cycle",
            "wait-cylinder-extended",
            "process-cylinder",
            maximumTicks: 10);

        Assert.Equal(SequenceStepPreviewOutcome.LimitReached, result.Outcome);
        Assert.Equal(10, result.ExecutedTicks);
        Assert.Equal(10, result.FinalSnapshot!.TickIndex);
    }

    [Fact]
    public async Task MoveAxisPreview_CompletesAtAuthoredPosition()
    {
        var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(
            LoadRecipe(),
            "automatic-cycle",
            "move-process-axis",
            "process-stage");

        Assert.Equal(SequenceStepPreviewOutcome.Completed, result.Outcome);
        var axis = Assert.Single(result.FinalSnapshot!.Axes);
        Assert.Equal(AxisState.Idle, axis.State);
        Assert.Equal(160, axis.Position, precision: 6);
    }

    [Fact]
    public async Task UnsupportedStep_IsRejectedWithoutStartingRuntime()
    {
        var result = await new DeterministicSequenceStepPreviewRunner().RunAsync(
            LoadRecipe(),
            "automatic-cycle",
            "complete",
            "frame");

        Assert.Equal(SequenceStepPreviewOutcome.Rejected, result.Outcome);
        Assert.Equal(0, result.ExecutedTicks);
        Assert.Null(result.FinalSnapshot);
    }

    private static MachineProjectDocument LoadRecipe() =>
        LoadRecipe("01-FoupLoadPort.ovmachine");

    private static MachineProjectDocument LoadRecipe(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            fileName);
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }
}
