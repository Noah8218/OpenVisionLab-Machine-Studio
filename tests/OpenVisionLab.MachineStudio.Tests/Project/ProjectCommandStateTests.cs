using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectCommandStateTests
{
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
