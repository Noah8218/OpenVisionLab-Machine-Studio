using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicRecipeDryRunRunnerTests
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
    public async Task SemiconductorRecipe_CompletesWithOrderedTimeline(string fileName)
    {
        var project = LoadRecipe(fileName);
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);
        var sequence = Assert.Single(project.Sequences);

        var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequence.Id);

        Assert.True(
            result.Outcome == RecipeDryRunOutcome.Completed,
            $"{fileName}: {result.FirstCheckpointMismatch?.StepId} expected " +
            $"{result.FirstCheckpointMismatch?.ExpectedState}, actual " +
            $"{result.FirstCheckpointMismatch?.ActualState}");
        Assert.Null(result.FirstIssue);
        Assert.Null(result.FirstCheckpointMismatch);
        Assert.InRange(result.ExecutedTicks, 1, result.MaximumTicks - 1);
        Assert.Equal(ExpectedExecutedStepIds(project, sequence), result.Timeline.Select(trace => trace.StepId));
        Assert.All(result.Timeline, trace => Assert.True(trace.EndedTick >= trace.StartedTick));
        Assert.All(result.Timeline, trace => Assert.Equal(trace.EndedTick, trace.BoundarySnapshot.TickIndex));
        var checkpointSteps = sequence.Steps.Where(step =>
            !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
            && !string.IsNullOrWhiteSpace(step.ExpectedState)).ToArray();
        Assert.Contains(checkpointSteps, step => IsCheckpoint(step, "wait-cylinder-extended", "process-cylinder", "Extended"));
        Assert.Contains(checkpointSteps, step => IsCheckpoint(step, "wait-process-position", "sensor-process", "Detected"));
        Assert.Contains(checkpointSteps, step => IsCheckpoint(step, "wait-process-axis", "axis.process", "Idle"));
        Assert.Contains(checkpointSteps, step => IsCheckpoint(step, "wait-cylinder-retracted", "process-cylinder", "Retracted"));
        Assert.Contains(checkpointSteps, step => IsCheckpoint(step, "cycle-done", "transport", "Stopped"));
        Assert.Equal(checkpointSteps.Length, result.Timeline.Count(trace => trace.HasCheckpoint));
        Assert.All(
            result.Timeline.Where(trace => trace.HasCheckpoint),
            trace => Assert.True(trace.Checkpoint?.IsPassed, $"{fileName}: {trace.StepId}"));
        var reloadedSequence = Assert.Single(store.Load(before).Sequences);
        Assert.Equal(
            checkpointSteps.Select(step => (step.Id, step.ExpectedTargetId, step.ExpectedState)),
            reloadedSequence.Steps
                .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
                    && !string.IsNullOrWhiteSpace(step.ExpectedState))
                .Select(step => (step.Id, step.ExpectedTargetId, step.ExpectedState)));
        Assert.True(result.Timeline.Zip(result.Timeline.Skip(1)).All(pair =>
            pair.First.BoundarySnapshot.TickIndex <= pair.Second.BoundarySnapshot.TickIndex));
        var snapshot = Assert.IsType<SimulationSnapshot>(result.FinalSnapshot);
        Assert.All(snapshot.Axes, axis => Assert.Equal(AxisState.Idle, axis.State));
        Assert.All(
            snapshot.LayoutComponents.Where(component =>
                component.Kind == LayoutComponentKind.PneumaticCylinder),
            component => Assert.Equal(PneumaticCylinderState.Retracted, component.CylinderState));
        Assert.All(
            snapshot.LayoutComponents.Where(component =>
                component.Kind == LayoutComponentKind.Conveyor),
            component => Assert.False(component.ConveyorRunning));
        Assert.Equal(before, store.Serialize(project));
    }

    [Fact]
    public async Task DryRun_StopsAtHardLimitAndNamesActiveStep()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        var result = await new DeterministicRecipeDryRunRunner().RunAsync(
            project,
            Assert.Single(project.Sequences).Id,
            maximumTicks: 3);

        Assert.Equal(RecipeDryRunOutcome.LimitReached, result.Outcome);
        Assert.Equal(3, result.ExecutedTicks);
        Assert.Equal("LimitReached", result.FirstIssue?.Code);
        Assert.NotEmpty(result.FirstIssue?.StepId ?? string.Empty);
        Assert.Contains(result.Timeline, trace => trace.HasIssue);
    }

    [Fact]
    public async Task DryRun_ReportsFirstTimedOutStep()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        var sequence = Assert.Single(project.Sequences);
        var wait = sequence.Steps.Single(step => step.Id == "wait-process-position");
        wait.TargetId = "di.cylinder.retracted";
        wait.TimeoutMs = 20;

        var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequence.Id);

        Assert.Equal(RecipeDryRunOutcome.Faulted, result.Outcome);
        Assert.Equal("wait-process-position", result.FirstIssue?.StepId);
        Assert.Equal("StepTimedOut", result.FirstIssue?.Code);
        Assert.Contains(result.Timeline, trace =>
            trace.StepId == "wait-process-position" && trace.HasIssue);
    }

    [Fact]
    public async Task DryRun_PassesAuthoredStepEndCheckpoint()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        var sequence = Assert.Single(project.Sequences);
        var wait = sequence.Steps.Single(step => step.Id == "wait-cylinder-extended");
        var cylinder = Assert.Single(project.Layouts).Components.Single(component =>
            component.Kind == LayoutComponentKind.PneumaticCylinder);
        wait.ExpectedTargetId = cylinder.Id;
        wait.ExpectedState = PneumaticCylinderState.Extended.ToString();

        var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequence.Id);

        Assert.Equal(RecipeDryRunOutcome.Completed, result.Outcome);
        Assert.Null(result.FirstCheckpointMismatch);
        var trace = result.Timeline.Single(item => item.StepId == wait.Id);
        Assert.True(trace.Checkpoint?.IsPassed);
        Assert.Equal("Extended", trace.Checkpoint?.ActualState);
    }

    [Fact]
    public async Task DryRun_ReportsAndOrdersFirstAuthoredStepEndMismatch()
    {
        var project = LoadRecipe("01-FoupLoadPort.ovmachine");
        var sequence = Assert.Single(project.Sequences);
        var cylinder = Assert.Single(project.Layouts).Components.Single(component =>
            component.Kind == LayoutComponentKind.PneumaticCylinder);
        var first = sequence.Steps.Single(step => step.Id == "wait-cylinder-extended");
        first.ExpectedTargetId = cylinder.Id;
        first.ExpectedState = PneumaticCylinderState.Retracted.ToString();
        var later = sequence.Steps.Single(step => step.Id == "wait-process-position");
        later.ExpectedTargetId = cylinder.Id;
        later.ExpectedState = PneumaticCylinderState.Retracted.ToString();

        var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequence.Id);

        Assert.Equal(RecipeDryRunOutcome.CompletedWithMismatch, result.Outcome);
        Assert.Null(result.FirstIssue);
        Assert.Equal(first.Id, result.FirstCheckpointMismatch?.StepId);
        Assert.Equal(cylinder.Id, result.FirstCheckpointMismatch?.TargetId);
        Assert.Equal("Retracted", result.FirstCheckpointMismatch?.ExpectedState);
        Assert.Equal("Extended", result.FirstCheckpointMismatch?.ActualState);
        Assert.Equal(
            result.Timeline.Single(trace => trace.StepId == first.Id).EndedTick,
            result.FirstCheckpointMismatch?.Tick);
        Assert.True(result.Timeline.Single(trace => trace.StepId == first.Id).HasCheckpointMismatch);
        Assert.True(result.Timeline.Single(trace => trace.StepId == later.Id).HasCheckpointMismatch);
    }

    private static MachineProjectDocument LoadRecipe(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", fileName);
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static bool IsCheckpoint(
        SequenceStepDefinition step,
        string expectedStepId,
        string expectedTargetId,
        string expectedState) =>
        step.Id == expectedStepId
        && step.ExpectedTargetId == expectedTargetId
        && step.ExpectedState == expectedState;

    private static IReadOnlyList<string> ExpectedExecutedStepIds(
        MachineProjectDocument project,
        SequenceDefinition sequence)
    {
        var stepsById = sequence.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        var cameraDecisions = project.Devices
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .ToDictionary(
                device => device.Id,
                device => device.Camera!.PlaceholderDecision,
                StringComparer.Ordinal);
        var result = new List<string>();
        SequenceStepDefinition? current = sequence.Steps.FirstOrDefault();
        while (current is not null)
        {
            result.Add(current.Id);
            string? nextId = current.Action == SequenceStepAction.WaitVisionResult
                && cameraDecisions.TryGetValue(current.TargetId, out PlaceholderInspectionDecision decision)
                && decision == PlaceholderInspectionDecision.Fail
                    ? current.FailureStepId
                    : current.NextStepId;
            current = string.IsNullOrWhiteSpace(nextId) ? null : stepsById[nextId];
        }

        return result;
    }
}
