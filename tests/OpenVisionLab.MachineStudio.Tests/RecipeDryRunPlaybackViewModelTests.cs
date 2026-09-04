using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeDryRunPlaybackViewModelTests
{
    [Fact]
    public async Task PlaybackOwnsIsolatedBoundaryNavigationAndExitRestoration()
    {
        var steps = await LoadStepsAsync();
        Assert.True(steps.Length > 1);

        var layoutEditable = true;
        var selectedTab = -1;
        RecipeDryRunStepPresentation? selectedStep = null;
        string? status = null;
        var viewModel = new RecipeDryRunPlaybackViewModel(
            () => true,
            value => layoutEditable = value,
            value => selectedTab = value,
            step => selectedStep = step,
            value => status = value);

        Assert.False(viewModel.IsActive);
        Assert.False(viewModel.PreviousStepCommand.CanExecute(null));
        Assert.False(viewModel.NextStepCommand.CanExecute(null));
        Assert.False(viewModel.ExitCommand.CanExecute(null));

        viewModel.Show(steps[0], steps);

        Assert.True(viewModel.IsActive);
        Assert.False(layoutEditable);
        Assert.Equal(0, selectedTab);
        Assert.Same(steps[0], viewModel.CurrentStep);
        Assert.Same(steps[0].BoundarySnapshot, viewModel.PlaybackSnapshots.Latest);
        Assert.Equal(steps[0].Name, viewModel.CurrentStep?.Name);
        Assert.NotEmpty(viewModel.TitleText);
        Assert.False(string.IsNullOrWhiteSpace(status));
        Assert.False(viewModel.PreviousStepCommand.CanExecute(null));
        Assert.True(viewModel.NextStepCommand.CanExecute(null));
        Assert.True(viewModel.ExitCommand.CanExecute(null));

        viewModel.NextStepCommand.Execute(null);

        Assert.Same(steps[1], selectedStep);
        Assert.Same(steps[1], viewModel.CurrentStep);
        Assert.Same(steps[1].BoundarySnapshot, viewModel.PlaybackSnapshots.Latest);
        Assert.True(viewModel.PreviousStepCommand.CanExecute(null));

        viewModel.ExitCommand.Execute(null);

        Assert.False(viewModel.IsActive);
        Assert.True(layoutEditable);
        Assert.Null(viewModel.CurrentStep);
        Assert.Empty(viewModel.TitleText);
        Assert.False(viewModel.ExitCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShowRejectsAStaleStepWithoutChangingPlaybackState()
    {
        var steps = await LoadStepsAsync();
        var staleStep = steps[0] with { StepId = "stale-step" };
        var showCalls = 0;
        var viewModel = new RecipeDryRunPlaybackViewModel(
            () => true,
            _ => showCalls++,
            _ => { },
            _ => { },
            _ => { });

        viewModel.Show(staleStep, steps);

        Assert.False(viewModel.IsActive);
        Assert.Null(viewModel.CurrentStep);
        Assert.Null(viewModel.PlaybackSnapshots.Latest);
        Assert.Equal(0, showCalls);
    }

    private static async Task<RecipeDryRunStepPresentation[]> LoadStepsAsync()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine")));
        var sequenceId = project.Sequences[0].Id;
        var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequenceId);

        return result.Timeline
            .Select((trace, index) => new RecipeDryRunStepPresentation(
                result.SequenceId,
                trace.StepId,
                null,
                $"#{index + 1}",
                trace.Name,
                $"tick {trace.StartedTick}–{trace.EndedTick}",
                trace.HasIssue,
                trace.HasCheckpoint,
                trace.HasCheckpointMismatch,
                trace.Checkpoint is null
                    ? string.Empty
                    : $"{trace.Checkpoint.TargetId}: {trace.Checkpoint.ExpectedState} → {trace.Checkpoint.ActualState}",
                trace.BoundarySnapshot))
            .ToArray();
    }
}
