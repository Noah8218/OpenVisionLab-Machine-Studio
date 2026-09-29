using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class ProjectTreeViewModel : ViewModelBase
{
    private ICommand? _newProjectCommand;
    private RelayCommand? _clearSearchCommand;
    private TreeNodeViewModel? _selectedNode;
    private string _searchText = string.Empty;
    private int _searchResultCount;
    private Dictionary<TreeNodeViewModel, bool>? _expandedBeforeSearch;

    internal Func<bool>? ResolvePendingPlacementDraft { get; set; }

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = new();

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            if (ResolvePendingPlacementDraft?.Invoke() == false)
            {
                return;
            }
            SetProperty(ref _selectedNode, value);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            ApplySearchFilter();
            OnPropertyChanged(nameof(IsSearchActive));
            OnPropertyChanged(nameof(SearchResultCount));
            OnPropertyChanged(nameof(HasSearchMatches));
            OnPropertyChanged(nameof(HasNoSearchMatches));
            _clearSearchCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchText);
    public int SearchResultCount => _searchResultCount;
    public bool HasSearchMatches => IsSearchActive && _searchResultCount > 0;
    public bool HasNoSearchMatches => IsSearchActive && _searchResultCount == 0;

    public ICommand NewProjectCommand => _newProjectCommand ??= new RelayCommand(_ => LoadProject(new MachineProjectDocument()));
    public ICommand ClearSearchCommand => _clearSearchCommand ??= new RelayCommand(
        _ => SearchText = string.Empty,
        _ => IsSearchActive);

    public void LoadProject(MachineProjectDocument document)
    {
        SearchText = string.Empty;
        SelectedNode = null;
        Roots.Clear();

        var root = new TreeNode(document.Id, document.Name, TreeNodeKind.Project, document);
        if (document.Stations.Count > 0)
        {
            var stations = new TreeNode(
                "stations",
                "Stations",
                TreeNodeKind.Stations);
            foreach (var station in document.Stations)
            {
                var stationNode = new TreeNode(station.Id, station.Name, TreeNodeKind.Station, station);
                foreach (var unit in station.Units)
                {
                    stationNode.Children.Add(new TreeNode(unit.Id, unit.Name, TreeNodeKind.Unit, unit));
                }
                stations.Children.Add(stationNode);
            }
            root.Children.Add(stations);
        }

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

    private void ApplySearchFilter()
    {
        if (!IsSearchActive)
        {
            foreach (var node in EnumerateNodes())
            {
                node.IsVisible = true;
            }

            if (_expandedBeforeSearch is { } expandedStates)
            {
                foreach (var (node, isExpanded) in expandedStates)
                {
                    node.IsExpanded = isExpanded;
                }
            }

            _expandedBeforeSearch = null;
            _searchResultCount = 0;
            return;
        }

        _expandedBeforeSearch ??= EnumerateNodes().ToDictionary(node => node, node => node.IsExpanded);
        _searchResultCount = 0;
        var searchText = _searchText.Trim();
        foreach (var root in Roots)
        {
            ApplySearchFilter(root, searchText, inheritedMatch: false, isRoot: true);
        }
    }

    private IEnumerable<TreeNodeViewModel> EnumerateNodes(IEnumerable<TreeNodeViewModel>? nodes = null)
    {
        foreach (var node in nodes ?? Roots)
        {
            yield return node;
            foreach (var descendant in EnumerateNodes(node.Children))
            {
                yield return descendant;
            }
        }
    }

    private bool ApplySearchFilter(
        TreeNodeViewModel node,
        string searchText,
        bool inheritedMatch,
        bool isRoot)
    {
        var directMatch = !inheritedMatch
            && (node.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || node.Id.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        if (directMatch)
        {
            _searchResultCount++;
        }

        var descendantVisible = false;
        foreach (var child in node.Children)
        {
            descendantVisible |= ApplySearchFilter(child, searchText, inheritedMatch || directMatch, isRoot: false);
        }

        node.IsVisible = isRoot || inheritedMatch || directMatch || descendantVisible;
        if (descendantVisible)
        {
            node.IsExpanded = true;
        }

        return node.IsVisible;
    }
}
