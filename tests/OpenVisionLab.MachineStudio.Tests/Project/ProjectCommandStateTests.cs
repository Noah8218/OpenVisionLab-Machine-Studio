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
}
