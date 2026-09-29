using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(EquipmentOutlineViewAutomationTestCollection.Name)]
public sealed class TreeViewSelectedItemBehaviorTests
{
    [Fact]
    public async Task TwoWaySelectionBindingSynchronizesTreeAndProjectState()
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            var projectTree = new ProjectTreeViewModel();
            projectTree.LoadProject(CreateProject());
            var root = Assert.Single(projectTree.Roots);
            var stations = Assert.Single(root.Children.Where(node => node.Kind == TreeNodeKind.Stations));
            var station = Assert.Single(stations.Children);
            var unit = Assert.Single(station.Children);
            var layouts = Assert.Single(root.Children.Where(node => node.Kind == TreeNodeKind.Layouts));
            var component = Assert.Single(Assert.Single(layouts.Children).Children);

            var tree = new TreeView { ItemsSource = projectTree.Roots };
            tree.ItemTemplate = new HierarchicalDataTemplate(typeof(TreeNodeViewModel))
            {
                ItemsSource = new Binding(nameof(TreeNodeViewModel.Children))
            };
            tree.ItemContainerStyle = new Style(typeof(TreeViewItem));
            tree.ItemContainerStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                new Binding(nameof(TreeNodeViewModel.IsExpanded)) { Mode = BindingMode.TwoWay }));
            tree.ItemContainerStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty,
                new Binding(nameof(TreeNodeViewModel.IsSelected)) { Mode = BindingMode.TwoWay }));

            var behavior = new TreeViewSelectedItemBehavior();
            Interaction.GetBehaviors(tree).Add(behavior);
            BindingOperations.SetBinding(behavior, TreeViewSelectedItemBehavior.SelectedItemProperty,
                new Binding(nameof(ProjectTreeViewModel.SelectedNode))
                {
                    Source = projectTree,
                    Mode = BindingMode.TwoWay
                });

            var window = new Window
            {
                Width = 420,
                Height = 360,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = tree
            };

            window.Show();
            try
            {
                Pump(window);
                projectTree.SelectedNode = unit;
                Pump(window);
                var selectedFromViewModel = tree.SelectedItem;

                var selectedComponentContainer = FindContainer(tree, component);
                selectedComponentContainer.IsSelected = true;
                Pump(window);

                return (
                    SelectedFromViewModel: selectedFromViewModel as TreeNodeViewModel,
                    ViewModelNodeAfterTreeSelection: projectTree.SelectedNode,
                    TreeNodeAfterTreeSelection: tree.SelectedItem as TreeNodeViewModel,
                    ComponentSelectionAfterTreeSelection: component.IsSelected,
                    UnitSelectionAfterComponentSelection: unit.IsSelected);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("unit-01", result.SelectedFromViewModel?.Id);
        Assert.Equal("component-01", result.ViewModelNodeAfterTreeSelection?.Id);
        Assert.Same(result.ViewModelNodeAfterTreeSelection, result.TreeNodeAfterTreeSelection);
        Assert.True(result.ComponentSelectionAfterTreeSelection);
        Assert.False(result.UnitSelectionAfterComponentSelection);
    }

    private static TreeViewItem FindContainer(ItemsControl parent, TreeNodeViewModel target) =>
        FindContainerIfGenerated(parent, target)
        ?? throw new InvalidOperationException($"TreeView container for '{target.Id}' was not generated.");

    private static TreeViewItem? FindContainerIfGenerated(ItemsControl parent, TreeNodeViewModel target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem item)
        {
            return item;
        }

        foreach (var node in parent.Items.OfType<TreeNodeViewModel>())
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem child)
            {
                continue;
            }

            child.IsExpanded = true;
            child.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            if (ReferenceEquals(node, target))
            {
                return child;
            }

            if (FindContainerIfGenerated(child, target) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static MachineProjectDocument CreateProject()
    {
        var unit = new MachineUnitDefinition { Id = "unit-01", Name = "Unit 01" };
        var station = new MachineStationDefinition { Id = "station-01", Name = "Station 01", Units = [unit] };
        var layout = new MachineLayoutDefinition { Id = "layout-01", Name = "Layout 01" };
        layout.Components.Add(new LayoutComponentDefinition
        {
            Id = "component-01",
            Name = "Machine Frame 01",
            Kind = LayoutComponentKind.MachineFrame
        });

        var project = new MachineProjectDocument
        {
            Name = "Selection binding test",
            Stations = [station],
            Layouts = [layout]
        };
        project.Simulation.ActiveLayoutId = layout.Id;
        return project;
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
