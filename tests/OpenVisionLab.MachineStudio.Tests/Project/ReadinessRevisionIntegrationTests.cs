using System.IO;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class ReadinessRevisionIntegrationTests
{
    [Fact]
    public void AxisAndAnalogDefinitionEditsInvalidateReadiness()
    {
        var project = LoadProject();
        project.Channels.Add(new ChannelDefinition
        {
            Id = "ai.readiness-probe",
            Name = "Readiness Probe",
            Kind = ChannelKind.AnalogInput,
            InitialValue = 1.0
        });
        using var viewModel = new MainViewModel(project);

        viewModel.RecipeConnections.DryRun.ValidateSimulationReadinessCommand.Execute(null);
        Assert.True(viewModel.RecipeConnections.DryRun.ReadinessPassed);

        var axesNode = viewModel.ProjectTree.Roots
            .Single()
            .Children
            .Single(node => node.Kind == TreeNodeKind.Axes);
        viewModel.ProjectTree.SelectedNode = axesNode.Children[0];
        var axisEditor = Assert.IsType<AxisDriveTuningEditorViewModel>(viewModel.AxisDriveTuningEditor);
        axisEditor.MaxVelocity += 1;

        Assert.True(viewModel.RecipeConnections.DryRun.IsReadinessStale);
        Assert.Null(viewModel.RecipeConnections.DryRun.ReadinessPassed);

        viewModel.RecipeConnections.DryRun.ValidateSimulationReadinessCommand.Execute(null);
        Assert.True(viewModel.RecipeConnections.DryRun.ReadinessPassed);
        Assert.False(viewModel.RecipeConnections.DryRun.IsReadinessStale);

        var channelsNode = viewModel.ProjectTree.Roots
            .Single()
            .Children
            .Single(node => node.Kind == TreeNodeKind.Channels);
        var analogNode = channelsNode.Children.Single(node =>
            project.Channels.Any(channel =>
                string.Equals(channel.Id, node.Id, StringComparison.Ordinal)
                && channel.Kind is ChannelKind.AnalogInput or ChannelKind.AnalogOutput));
        viewModel.ProjectTree.SelectedNode = analogNode;
        var analogEditor = Assert.IsType<AnalogIoAuthoringViewModel>(viewModel.AnalogIoAuthoring);
        analogEditor.InitialValue = analogEditor.InitialValue + 0.5;

        Assert.True(viewModel.RecipeConnections.DryRun.IsReadinessStale);
        Assert.Null(viewModel.RecipeConnections.DryRun.ReadinessPassed);
    }

    private static MachineProjectDocument LoadProject() =>
        new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "01-FoupLoadPort.ovmachine")));
}
