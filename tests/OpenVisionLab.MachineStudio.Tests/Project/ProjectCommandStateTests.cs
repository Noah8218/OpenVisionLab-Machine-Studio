using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectCommandStateTests
{
    [Fact]
    public void UnitRunCaptionSeparatesUnitCyclesFromWholeMachineRepeat()
    {
        var project = new MachineProjectDocument
        {
            Stations = [new() { Id = "station", Name = "Station", Units = [new() { Id = "feed", Name = "Feed", RepeatCount = 3 }, new() { Id = "inspection", Name = "Inspection", RepeatCount = 3 }, new() { Id = "eject", Name = "Eject", RepeatCount = 3 }] }],
            Sequences = new[] { "feed", "inspection", "eject" }.Select(id => new SequenceDefinition
            {
                Id = "sequence." + id, Name = id, UnitId = id,
                Steps = [new() { Id = "complete", Action = SequenceStepAction.Complete }]
            }).ToList()
        };
        using var viewModel = new MainViewModel(project);
        Assert.Contains(string.Format(OpenVisionLanguageService.T("Shell.UnitSequencesRunTarget"), 3), viewModel.RunTargetText);
        Assert.Contains(string.Format(OpenVisionLanguageService.T("Shell.UnitRepeatRunTarget"), 3), viewModel.RunTargetText);
        Assert.Contains(OpenVisionLanguageService.T("Shell.SingleRunTarget"), viewModel.RunTargetText);
        project.Stations[0].Units[1].RepeatCount = 2;
        Assert.Contains(OpenVisionLanguageService.T("Shell.MixedUnitRepeatRunTarget"), viewModel.RunTargetText);
        project.Sequences[0].UnitId = null;
        Assert.DoesNotContain(OpenVisionLanguageService.T("Shell.MixedUnitRepeatRunTarget"), viewModel.RunTargetText);
    }

    [Fact]
    public void ProjectTreeNewProjectCommandKeepsOneBindingCommandInstance()
    {
        var tree = new ProjectTreeViewModel();

        Assert.Same(tree.NewProjectCommand, tree.NewProjectCommand);
    }

    [Fact]
    public void SaveProjectAsCommandRaisesCanExecuteChangedWhenProjectChanges()
    {
        using var viewModel = new MainViewModel(
            new MachineProjectDocument { Name = "Save As notification" });
        var command = viewModel.SaveProjectAsCommand;
        var invalidationCount = 0;
        EventHandler handler = (_, _) => invalidationCount++;

        command.CanExecuteChanged += handler;
        try
        {
            Assert.True(viewModel.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
            Assert.True(invalidationCount > 0);
        }
        finally
        {
            command.CanExecuteChanged -= handler;
        }
    }

    [Fact]
    public void ModeChangeInvalidatesSequenceEditorCommandsOnce()
    {
        using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Mode notification" });
        var command = viewModel.SequenceEditor.AddStepCommand;
        var invalidationCount = 0;
        EventHandler handler = (_, _) => invalidationCount++;

        command.CanExecuteChanged += handler;
        try
        {
            viewModel.IsRunMode = true;

            Assert.False(viewModel.SequenceEditor.IsEditable);
            Assert.Equal(1, invalidationCount);
        }
        finally
        {
            command.CanExecuteChanged -= handler;
        }
    }

    [Fact]
    public void SynchronousModeChangeDoesNotNotifyModeIndependentScenarioConfigurationState()
    {
        using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Scenario configuration notification" });
        var initialValue = viewModel.IsScenarioConfigurationEnabled;
        var notificationCount = 0;
        var transitionThreadId = Environment.CurrentManagedThreadId;
        var isModeTransitionActive = false;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (isModeTransitionActive
                && Environment.CurrentManagedThreadId == transitionThreadId
                && args.PropertyName == nameof(MainViewModel.IsScenarioConfigurationEnabled))
            {
                notificationCount++;
            }
        };

        viewModel.PropertyChanged += handler;
        try
        {
            isModeTransitionActive = true;
            try
            {
                viewModel.IsRunMode = true;
            }
            finally
            {
                isModeTransitionActive = false;
            }

            Assert.Equal(initialValue, viewModel.IsScenarioConfigurationEnabled);
            Assert.Equal(0, notificationCount);
        }
        finally
        {
            viewModel.PropertyChanged -= handler;
        }
    }
}
