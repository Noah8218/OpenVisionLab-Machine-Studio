using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class TreeNodeViewModel : ViewModelBase
{
    private readonly TreeNode _node;
    private bool _isSelected;
    private bool _isExpanded = true;

    public TreeNodeViewModel(TreeNode node, TreeNodeViewModel? parent)
    {
        _node = node;
        Parent = parent;
        foreach (var child in node.Children)
        {
            Children.Add(new TreeNodeViewModel(child, this));
        }
    }

    public TreeNodeViewModel? Parent { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public string Id => _node.Id;
    public string DisplayName => _node.DisplayName;
    public TreeNodeKind Kind => _node.Kind;
    public object? Model => _node.Model;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ICommand AddChildCommand => new RelayCommand(_ =>
    {
        // Placeholder for future add-child operations.
    });
}
