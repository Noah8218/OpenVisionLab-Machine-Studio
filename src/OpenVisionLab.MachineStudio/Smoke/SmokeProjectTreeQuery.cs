using System;
using System.Collections.Generic;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeProjectTreeQuery
{
    public static TreeNodeViewModel? SelectNode(ProjectTreeViewModel tree, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        TreeNodeViewModel? current = FindByName(tree.Roots, parts[0]);
        if (current is null)
            return null;

        for (var i = 1; i < parts.Length; i++)
        {
            current = FindByName(current.Children, parts[i]);
            if (current is null)
                return null;
        }

        tree.SelectedNode = current;
        return current;
    }

    private static TreeNodeViewModel? FindByName(IEnumerable<TreeNodeViewModel> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindByName(node.Children, name);
            if (child is not null)
                return child;
        }

        return null;
    }
}
