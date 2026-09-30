using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class LeftToolRegionSearchAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public LeftToolRegionSearchAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task ProjectSearchInputAndEmptyStateStayBoundToTheLargeLayoutTree()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            var samplePath = Path.Combine(AppContext.BaseDirectory, "Samples", "LargeLayoutExploration.ovmachine");
            var project = new ProjectDocumentStore().Load(File.ReadAllText(samplePath));
            using var viewModel = new MainViewModel(project);
            viewModel.Navigation.SelectedWorkspaceIndex = 1;
            viewModel.Navigation.SelectedLeftToolTabIndex = 0;
            var root = Assert.Single(viewModel.ProjectTree.Roots);
            var stations = Assert.Single(root.Children.Where(node => node.Kind == TreeNodeKind.Stations));
            var station = stations.Children.Single(node => node.Id == "station-assembly");
            var selectedUnit = station.Children.Single(node => node.Id == "unit-01");
            viewModel.ProjectTree.SelectedNode = selectedUnit;
            var serializedBeforeSearch = new ProjectDocumentStore().Serialize(project);

            var view = new LeftToolRegionView { DataContext = viewModel };
            var window = new Window
            {
                Width = 420,
                Height = 540,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };

            window.Show();
            try
            {
                Pump(window);
                var controls = Descendants(view).ToArray();
                var search = controls.OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "ProjectTree.SearchText");
                var clear = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, viewModel.ProjectTree.ClearSearchCommand));
                var noMatches = controls.OfType<TextBlock>().Single(textBlock =>
                    textBlock.GetBindingExpression(TextBlock.VisibilityProperty)?.ParentBinding.Path.Path
                    == "ProjectTree.HasNoSearchMatches");

                search.Text = "uNiT 01";
                Pump(window);
                var matchCount = viewModel.ProjectTree.SearchResultCount;
                var matchingTreeNodeCount = viewModel.ProjectTree.Roots
                    .SelectMany(Enumerate)
                    .Count(node => node.IsVisible && node.Kind == TreeNodeKind.LayoutComponent);
                var noMatchHiddenForResults = noMatches.Visibility == Visibility.Collapsed;
                var clearEnabledForResults = clear.IsEnabled;

                search.Text = "no-such-project-item";
                Pump(window);
                var noMatchesVisible = noMatches.Visibility == Visibility.Visible;
                var clearEnabledForEmpty = clear.IsEnabled;

                clear.Command!.Execute(clear.CommandParameter);
                Pump(window);

                return (
                    MatchCount: matchCount,
                    MatchingTreeNodeCount: matchingTreeNodeCount,
                    NoMatchHiddenForResults: noMatchHiddenForResults,
                    ClearEnabledForResults: clearEnabledForResults,
                    NoMatchesVisible: noMatchesVisible,
                    ClearEnabledForEmpty: clearEnabledForEmpty,
                    SearchAfterClear: search.Text,
                    SelectionPreserved: ReferenceEquals(selectedUnit, viewModel.ProjectTree.SelectedNode),
                    DocumentPreserved: serializedBeforeSearch == new ProjectDocumentStore().Serialize(project),
                    ProjectRemainsClean: !viewModel.HasUnsavedChanges);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(26, result.MatchCount);
        Assert.Equal(25, result.MatchingTreeNodeCount);
        Assert.True(result.NoMatchHiddenForResults);
        Assert.True(result.ClearEnabledForResults);
        Assert.True(result.NoMatchesVisible);
        Assert.True(result.ClearEnabledForEmpty);
        Assert.Empty(result.SearchAfterClear);
        Assert.True(result.SelectionPreserved);
        Assert.True(result.DocumentPreserved);
        Assert.True(result.ProjectRemainsClean);
    }

    private static IEnumerable<OpenVisionLab.MachineStudio.ViewModel.TreeNodeViewModel> Enumerate(
        OpenVisionLab.MachineStudio.ViewModel.TreeNodeViewModel node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Enumerate(child)) yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

}
