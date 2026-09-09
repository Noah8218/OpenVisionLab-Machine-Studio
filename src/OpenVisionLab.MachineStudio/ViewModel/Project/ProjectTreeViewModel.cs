using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class ProjectTreeViewModel : ViewModelBase
{
    private TreeNodeViewModel? _selectedNode;

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = new();

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    public ICommand NewProjectCommand => new RelayCommand(_ => LoadProject(new MachineProjectDocument()));

    public void LoadProject(MachineProjectDocument document)
    {
        SelectedNode = null;
        Roots.Clear();

        var root = new TreeNode(document.Id, document.Name, TreeNodeKind.Project, document);
        var layouts = new TreeNode("layouts", "Layouts", TreeNodeKind.Layouts);
        foreach (var layout in document.Layouts)
        {
            var layoutNode = new TreeNode(layout.Id, layout.Name, TreeNodeKind.Layout, layout);
            foreach (var component in layout.Components.OrderBy(item => item.ZIndex).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                layoutNode.Children.Add(new TreeNode(
                    component.Id,
                    component.Name,
                    TreeNodeKind.LayoutComponent,
                    component));
            }
            layouts.Children.Add(layoutNode);
        }
        root.Children.Add(layouts);

        var axes = new TreeNode("axes", "Axes", TreeNodeKind.Axes);
        foreach (var axis in document.Axes)
        {
            axes.Children.Add(new TreeNode(axis.Id, axis.Name, TreeNodeKind.Axis, axis));
        }
        root.Children.Add(axes);

        var devices = new TreeNode("devices", "Devices", TreeNodeKind.Devices);
        foreach (var device in document.Devices)
        {
            devices.Children.Add(new TreeNode(device.Id, device.Name, TreeNodeKind.Device, device));
        }
        root.Children.Add(devices);

        var channels = new TreeNode("channels", "Channels", TreeNodeKind.Channels);
        foreach (var channel in document.Channels)
        {
            channels.Children.Add(new TreeNode(channel.Id, channel.Name, TreeNodeKind.Channel, channel));
        }
        root.Children.Add(channels);

        var sequences = new TreeNode("sequences", "Sequences", TreeNodeKind.Sequences);
        foreach (var sequence in document.Sequences)
        {
            var seqNode = new TreeNode(sequence.Id, sequence.Name, TreeNodeKind.Sequence, sequence);
            foreach (var step in sequence.Steps)
            {
                seqNode.Children.Add(new TreeNode(step.Id, step.Name, TreeNodeKind.Step, step));
            }
            sequences.Children.Add(seqNode);
        }
        root.Children.Add(sequences);

        Roots.Add(new TreeNodeViewModel(root, null) { IsExpanded = true });
    }
}
