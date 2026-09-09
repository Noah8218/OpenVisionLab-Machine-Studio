using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineLayoutViewModelTests
{
    [Fact]
    public void DisposeDetachesLayoutAndSelectedEditorSubscriptions()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1",
            Name = "Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 20, Y = 30 },
            Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            Components = { component }
        };
        var project = new MachineProjectDocument();
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);

        var item = Assert.Single(layout.Items);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var definitionChangedCount = 0;
        var editorPropertyChangedCount = 0;
        layout.DefinitionChanged += (_, _) => definitionChangedCount++;
        editor.PropertyChanged += (_, _) => editorPropertyChangedCount++;

        layout.Dispose();
        item.CurrentName = "Disposed Frame";

        Assert.Equal("Disposed Frame", item.CurrentName);
        Assert.Null(layout.SelectedComponentEditor);
        Assert.Equal(0, definitionChangedCount);
        Assert.Equal(0, editorPropertyChangedCount);
        Assert.Throws<ObjectDisposedException>(() => layout.Load(project));
        layout.Dispose();
    }
}
