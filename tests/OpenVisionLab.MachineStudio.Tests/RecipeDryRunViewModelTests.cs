using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeDryRunViewModelTests
{
    [Fact]
    public void ReadinessAndEditabilityOwnCommandGates()
    {
        var project = LoadProject("AutomaticTransferCell.ovmachine");
        var readinessCalls = 0;
        var runCalls = 0;
        var sequenceId = project.Sequences[0].Id;
        var viewModel = CreateViewModel(
            () =>
            {
                readinessCalls++;
                return null;
            },
            _ =>
            {
                runCalls++;
                return Task.FromResult(CreateResult(project, sequenceId));
            });

        viewModel.Load(project);

        Assert.False(viewModel.RunRecipeDryRunCommand.CanExecute(null));

        viewModel.ValidateSimulationReadinessCommand.Execute(null);

        Assert.Equal(1, readinessCalls);
        Assert.True(viewModel.ReadinessPassed);
        Assert.True(viewModel.RunRecipeDryRunCommand.CanExecute(null));

        viewModel.IsEditable = false;

        Assert.False(viewModel.ValidateSimulationReadinessCommand.CanExecute(null));
        Assert.False(viewModel.RunRecipeDryRunCommand.CanExecute(null));
        Assert.Equal(0, runCalls);
    }

    [Fact]
    public async Task RunProjectsResultAndRoutesStepActionsWithoutMutatingProject()
    {
        var project = LoadProject("AutomaticTransferCell.ovmachine");
        var store = new ProjectDocumentStore();
        var projectBefore = store.Serialize(project);
        var sequenceId = project.Sequences[0].Id;
        var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequenceId);
        var resolveCalls = 0;
        string? openedSequenceId = null;
        string? openedStepId = null;
        RecipeDryRunStepPresentation? playedStep = null;
        var viewModel = CreateViewModel(
            () => null,
            _ => Task.FromResult(result),
            componentId =>
            {
                resolveCalls++;
                return componentId is null ? null : $"component:{componentId}";
            },
            (openedSequence, openedStep) =>
            {
                openedSequenceId = openedSequence;
                openedStepId = openedStep;
            },
            step => playedStep = step);

        viewModel.Load(project);
        viewModel.ValidateSimulationReadinessCommand.Execute(null);
        viewModel.RunRecipeDryRunCommand.Execute(null);
        await Task.Yield();

        Assert.Same(result, viewModel.RecipeDryRunResult);
        Assert.Equal(result.Timeline.Count, viewModel.Timeline.Count);
        Assert.Equal(result.Timeline.Count, resolveCalls);
        Assert.Equal(projectBefore, store.Serialize(project));
        if (result.FinalSnapshot is not null)
        {
            Assert.NotEmpty(viewModel.FinalStates);
        }

        Assert.NotEmpty(viewModel.Timeline);
        var step = viewModel.Timeline[0];
        viewModel.OpenRecipeDryRunStepCommand.Execute(step);
        viewModel.PlayRecipeDryRunStepCommand.Execute(step);

        Assert.Same(step, viewModel.SelectedRecipeDryRunStep);
        Assert.Equal(step.SequenceId, openedSequenceId);
        Assert.Equal(step.StepId, openedStepId);
        Assert.Same(step, playedStep);
    }

    [Fact]
    public async Task LoadInvalidatesAnInFlightResult()
    {
        var project = LoadProject("AutomaticTransferCell.ovmachine");
        var sequenceId = project.Sequences[0].Id;
        var result = CreateResult(project, sequenceId);
        var resultSource = new TaskCompletionSource<RecipeDryRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(
            () => null,
            _ => resultSource.Task);

        viewModel.Load(project);
        viewModel.ValidateSimulationReadinessCommand.Execute(null);
        viewModel.RunRecipeDryRunCommand.Execute(null);
        viewModel.Load(project);
        resultSource.SetResult(result);
        await Task.Yield();

        Assert.False(viewModel.HasRecipeDryRunResult);
        Assert.False(viewModel.IsRecipeDryRunRunning);
        Assert.Null(viewModel.ReadinessPassed);
    }

    private static RecipeDryRunViewModel CreateViewModel(
        Func<string?> validateReadiness,
        Func<string, Task<RecipeDryRunResult>> runDryRun,
        Func<string?, string?>? resolveComponentId = null,
        Action<string, string>? openStep = null,
        Action<RecipeDryRunStepPresentation>? playStep = null) =>
        new(
            validateReadiness,
            runDryRun,
            (sequenceId, stepId) => openStep?.Invoke(sequenceId, stepId),
            step => playStep?.Invoke(step),
            _ => { },
            resolveComponentId ?? (componentId => componentId));

    private static MachineProjectDocument LoadProject(string fileName) =>
        new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            fileName)));

    private static RecipeDryRunResult CreateResult(
        MachineProjectDocument project,
        string sequenceId) => new(
            RecipeDryRunOutcome.Completed,
            sequenceId,
            project.Sequences.First(sequence => sequence.Id == sequenceId).Name,
            0,
            1,
            Array.Empty<RecipeDryRunStepTrace>(),
            null,
            null,
            null,
            string.Empty);
}
