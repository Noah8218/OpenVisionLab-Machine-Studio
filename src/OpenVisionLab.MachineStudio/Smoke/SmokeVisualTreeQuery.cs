using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeVisualTreeQuery
{
    public static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    public static T? FindVisualDescendant<T>(DependencyObject parent, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    public static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
