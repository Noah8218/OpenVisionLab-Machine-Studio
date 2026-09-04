using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class AnalogIoAuthoringViewModelTests
{
    [Fact]
    public void InitialValueEditorCommitsOnlyFiniteValues()
    {
        var channel = new ChannelDefinition
        {
            Id = "ai.pressure",
            Name = "Pressure",
            Kind = ChannelKind.AnalogInput,
            InitialValue = 1.5
        };
        var changedCount = 0;
        var editor = new AnalogIoAuthoringViewModel(channel, () => changedCount++);

        Assert.Equal("ai.pressure", editor.Id);
        Assert.Equal("Pressure", editor.DisplayName);
        Assert.True(editor.IsInput);
        Assert.Equal(1.5, editor.InitialValue);
        Assert.False(editor.HasValidationErrors);

        editor.InitialValueText = "2.75";

        Assert.Equal(2.75, channel.InitialValue);
        Assert.Equal(2.75, editor.InitialValue);
        Assert.Equal(1, changedCount);
        Assert.False(editor.HasValidationErrors);

        editor.InitialValueText = "NaN";

        Assert.Equal(2.75, channel.InitialValue);
        Assert.Equal(1, changedCount);
        Assert.True(editor.HasValidationErrors);

        editor.InitialValueText = "Infinity";

        Assert.Equal(2.75, channel.InitialValue);
        Assert.Equal(1, changedCount);
        Assert.True(editor.HasValidationErrors);

        editor.InitialValueText = "3.125";

        Assert.Equal(3.125, channel.InitialValue);
        Assert.Equal(2, changedCount);
        Assert.False(editor.HasValidationErrors);
    }

    [Fact]
    public void RejectsDigitalChannelsAtTheAuthoringBoundary()
    {
        var channel = new ChannelDefinition
        {
            Id = "di.ready",
            Kind = ChannelKind.DigitalInput
        };

        Assert.Throws<ArgumentException>(() => new AnalogIoAuthoringViewModel(channel, () => { }));
    }

    [Fact]
    public void MainViewModelRoutesAnalogSelectionAndSerializationWithoutRuntimeAction()
    {
        var project = new MachineProjectDocument
        {
            Name = "Analog authoring",
            Channels =
            [
                new ChannelDefinition
                {
                    Id = "ai.pressure",
                    Name = "Pressure",
                    Kind = ChannelKind.AnalogInput,
                    InitialValue = 1.5
                },
                new ChannelDefinition
                {
                    Id = "ao.setpoint",
                    Name = "Setpoint",
                    Kind = ChannelKind.AnalogOutput,
                    InitialValue = 2.5
                },
                new ChannelDefinition
                {
                    Id = "di.ready",
                    Name = "Ready",
                    Kind = ChannelKind.DigitalInput,
                    InitialValue = 1
                }
            ]
        };

        using var viewModel = new MainViewModel(project);
        var projectNode = viewModel.ProjectTree.Roots.Single();
        var channelsNode = projectNode.Children.Single(node => node.Kind == TreeNodeKind.Channels);
        var analogInputNode = channelsNode.Children.Single(node => node.Id == "ai.pressure");
        var analogOutputNode = channelsNode.Children.Single(node => node.Id == "ao.setpoint");
        var digitalInputNode = channelsNode.Children.Single(node => node.Id == "di.ready");

        viewModel.ProjectTree.SelectedNode = analogInputNode;

        Assert.NotNull(viewModel.AnalogIoAuthoring);
        Assert.True(viewModel.HasSelectedAnalogChannel);
        Assert.Equal(1.5, viewModel.AnalogIoAuthoring!.InitialValue);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.AnalogIoAuthoring.InitialValueText = "42.25";

        Assert.Equal(42.25, project.Channels.Single(channel => channel.Id == "ai.pressure").InitialValue);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.IsDesignMode);

        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        Assert.Equal(
            42.25,
            reopened.Channels.Single(channel => channel.Id == "ai.pressure").InitialValue);

        viewModel.ProjectTree.SelectedNode = digitalInputNode;

        Assert.Null(viewModel.AnalogIoAuthoring);
        Assert.False(viewModel.HasSelectedAnalogChannel);

        viewModel.ProjectTree.SelectedNode = analogOutputNode;

        Assert.NotNull(viewModel.AnalogIoAuthoring);
        Assert.Equal(2.5, viewModel.AnalogIoAuthoring!.InitialValue);
    }
}
