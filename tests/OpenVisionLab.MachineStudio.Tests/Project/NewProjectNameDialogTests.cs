using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.View.Project;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests.Project;

[Collection(StudioUiTestCollection.Name)]
public sealed class NewProjectNameDialogTests
{
    private readonly StudioUiTestHost _ui;

    public NewProjectNameDialogTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task NewCommandOpensNameEntryAndCancelLeavesCurrentProjectUntouched()
    {
        using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Current recipe" });
        var currentName = viewModel.ProjectTree.Roots.Single().DisplayName;

        viewModel.NewProjectCommand.Execute(null);

        Assert.True(viewModel.IsNewProjectNameDialogOpen);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.NewProjectNameDraft));
        viewModel.NewProjectNameDraft = "   ";
        Assert.False(await viewModel.ConfirmNewProjectNameAsync());
        Assert.True(viewModel.IsNewProjectNameDialogOpen);
        Assert.True(viewModel.HasNewProjectNameValidationError);
        Assert.Equal(currentName, viewModel.ProjectTree.Roots.Single().DisplayName);

        viewModel.CancelNewProjectNameCommand.Execute(null);

        Assert.False(viewModel.IsNewProjectNameDialogOpen);
        Assert.Equal(currentName, viewModel.ProjectTree.Roots.Single().DisplayName);
    }

    [Fact]
    public async Task NewRecipeNameRejectsOverSixtyCharactersThenAcceptsSixtyOnRetry()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Current recipe" });
            var currentName = viewModel.ProjectTree.Roots.Single().DisplayName;

            viewModel.NewProjectCommand.Execute(null);
            viewModel.NewProjectNameDraft = new string('x', 61);

            Assert.False(await viewModel.ConfirmNewProjectNameAsync());
            Assert.True(viewModel.IsNewProjectNameDialogOpen);
            Assert.True(viewModel.HasNewProjectNameValidationError);
            Assert.Equal(currentName, viewModel.ProjectTree.Roots.Single().DisplayName);

            var acceptedName = new string('a', 60);
            viewModel.NewProjectNameDraft = acceptedName;

            Assert.False(viewModel.HasNewProjectNameValidationError);
            Assert.True(await viewModel.ConfirmNewProjectNameAsync());
            Assert.False(viewModel.IsNewProjectNameDialogOpen);
            Assert.Equal(acceptedName, viewModel.ProjectTree.Roots.Single().DisplayName);
        });
    }

    [Fact]
    public async Task NewProjectNameViewBindsInputAndKeepsRejectedEntryRecoverable()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Current recipe" });
            var currentName = viewModel.ProjectTree.Roots.Single().DisplayName;
            var view = new NewProjectNameDialogView { DataContext = viewModel };
            var window = new Window
            {
                Width = 640,
                Height = 480,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();

            try
            {
                Pump(window);
                viewModel.NewProjectCommand.Execute(null);
                Pump(window);

                var nameTextBox = Assert.IsType<TextBox>(view.FindName("ProjectNameTextBox"));
                var validationText = Assert.Single(Descendants(view).OfType<TextBlock>().Where(text =>
                    BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path
                    == nameof(MainViewModel.NewProjectNameValidationText)));
                var createButton = Assert.Single(Descendants(view).OfType<Button>().Where(button => button.IsDefault));
                var cancelButton = Assert.Single(Descendants(view).OfType<Button>().Where(button =>
                    ReferenceEquals(button.Command, viewModel.CancelNewProjectNameCommand)));

                Assert.Equal(Visibility.Visible, view.Visibility);
                Assert.Equal(60, nameTextBox.MaxLength);
                Assert.Equal(viewModel.NewProjectNameDraft, nameTextBox.Text);
                Assert.Same(viewModel.ConfirmNewProjectNameCommand, createButton.Command);
                Assert.Equal("NewProjectNameDraft", nameTextBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path);

                nameTextBox.Text = "   ";
                Pump(window);
                Assert.Equal("   ", viewModel.NewProjectNameDraft);
                Assert.False(await viewModel.ConfirmNewProjectNameAsync());
                Pump(window);

                Assert.Equal(Visibility.Visible, view.Visibility);
                Assert.Equal(Visibility.Visible, validationText.Visibility);
                Assert.Equal(currentName, viewModel.ProjectTree.Roots.Single().DisplayName);

                cancelButton.Command.Execute(cancelButton.CommandParameter);
                Pump(window);
                Assert.Equal(Visibility.Collapsed, view.Visibility);
                Assert.Equal(currentName, viewModel.ProjectTree.Roots.Single().DisplayName);

                viewModel.NewProjectCommand.Execute(null);
                Pump(window);
                var acceptedName = new string('a', 60);
                nameTextBox.Text = acceptedName;
                Pump(window);

                Assert.Equal(acceptedName, viewModel.NewProjectNameDraft);
                Assert.Equal(Visibility.Collapsed, validationText.Visibility);
                Assert.True(await viewModel.ConfirmNewProjectNameAsync());
                Pump(window);

                Assert.Equal(Visibility.Collapsed, view.Visibility);
                Assert.Equal(acceptedName, viewModel.ProjectTree.Roots.Single().DisplayName);
            }
            finally
            {
                window.Close();
            }
        });
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
