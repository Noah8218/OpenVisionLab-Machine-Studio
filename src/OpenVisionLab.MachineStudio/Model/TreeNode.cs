using System.Collections.ObjectModel;

namespace OpenVisionLab.MachineStudio.Model;

public sealed class TreeNode
{
    public string Id { get; }
    public string DisplayName { get; set; }
    public TreeNodeKind Kind { get; }
    public object? Model { get; }
    public ObservableCollection<TreeNode> Children { get; } = new();

    public TreeNode(string id, string displayName, TreeNodeKind kind, object? model = null)
    {
        Id = id;
        DisplayName = displayName;
        Kind = kind;
        Model = model;
    }
}
