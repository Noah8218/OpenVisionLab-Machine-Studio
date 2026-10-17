using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 640, 480);
            window.Show();

            try
            {
                Pump(window);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
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

    [Fact]
    public async Task NameEntryMovesKeyboardFocusOnEveryOpenAndReturnsItOnCancel()
    {
        await _ui.InvokeAsync(() =>
        {
            using var model = new MainViewModel(new MachineProjectDocument { Name = "Keep current recipe" });
            var launcher = new Button { Content = "New recipe" };
            var view = new NewProjectNameDialogView { DataContext = model };
            var content = new Grid();
            content.Children.Add(launcher);
            content.Children.Add(view);
            var window = new Window { Content = content, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 640, 480);
            window.Show();
            try
            {
                Pump(window);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(monitor));
                var name = Assert.IsType<TextBox>(view.FindName("ProjectNameTextBox"));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    launcher.Focus();
                    Assert.Same(launcher, Keyboard.FocusedElement);
                    model.NewProjectCommand.Execute(null);
                    Pump(window);
                    Assert.Same(name, Keyboard.FocusedElement);
                    for (var tab = 0; tab < 6; tab++)
                    {
                        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        Assert.Contains((DependencyObject)Keyboard.FocusedElement, Descendants(view));
                    }
                    model.CancelNewProjectNameCommand.Execute(null);
                    Pump(window);
                    Assert.Same(launcher, Keyboard.FocusedElement);
                    Assert.Equal("Keep current recipe", model.ProjectTree.Roots.Single().DisplayName);
                    Assert.False(model.HasUnsavedChanges);
                }
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, 13)]
    [InlineData(1280, 900, 12.5)]
    public async Task R24NewRecipeOverlayRendersActualCaptionInputAndButtonGlyphsWithRecoverableNames(int width, int height, double commandSize)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            using var model = new MainViewModel(new MachineProjectDocument { Name = "Keep current recipe" });
            model.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump(window);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(monitor));
                model.NewProjectCommand.Execute(null);
                Pump(window);
                var view = Assert.Single(Descendants(window).OfType<NewProjectNameDialogView>());
                var input = Assert.IsType<TextBox>(view.FindName("ProjectNameTextBox"));
                var panel = Assert.IsType<Border>(view.FindName("NameDialogPanel"));
                Assert.Same(model, view.DataContext);
                Assert.True(view.IsVisible);
                Assert.Equal(640, panel.ActualWidth);
                Assert.Equal(602, input.ActualWidth);
                Assert.Equal(28, input.ActualHeight);
                Assert.Equal(60, input.MaxLength);
                var inputViewport = Assert.Single(Descendants(input).OfType<ScrollContentPresenter>());
                Assert.Equal(18, inputViewport.ActualHeight, 2);
                Assert.False(model.NewProjectCommand.CanExecute(null));
                var caption = Assert.Single(Descendants(view).OfType<TextBlock>().Where(t => t.Text == "새 레시피"));
                var captionGlyphs = RenderedGlyphs(caption);
                Assert.NotEmpty(captionGlyphs);
                Assert.All(captionGlyphs, glyph => Assert.Equal(16, glyph.GlyphRun.FontRenderingEmSize));
                Assert.All(captionGlyphs, glyph => Assert.Equal(700, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()));
                foreach (var button in Descendants(view).OfType<Button>())
                {
                    Assert.True(button.Command.CanExecute(button.CommandParameter));
                    var text = Assert.Single(Descendants(button).OfType<TextBlock>().Where(t => t.IsVisible));
                    var glyphs = RenderedGlyphs(text);
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.Equal(commandSize, glyph.GlyphRun.FontRenderingEmSize));
                    Assert.Equal(commandSize * 1.45, text.LineHeight, 4);
                }

                var initialValue = input.Text;
                input.SetCurrentValue(TextBox.TextProperty, new string('장', 60));
                Pump(window);
                Assert.Equal(input.Text, model.NewProjectNameDraft);
                var values = RenderedGlyphs(input);
                Assert.NotEmpty(values);
                Assert.All(values, glyph => Assert.Equal(12, glyph.GlyphRun.FontRenderingEmSize));
                Assert.All(values, glyph => Assert.Equal(400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()));
                Assert.All(values, glyph => Assert.Equal("#FF52687C", glyph.ForegroundBrush.ToString()));
                Assert.Equal(640, panel.ActualWidth);
                input.SetCurrentValue(TextBox.TextProperty, "   ");
                Pump(window);
                Assert.False(await model.ConfirmNewProjectNameAsync());
                Pump(window);
                Assert.True(model.HasNewProjectNameValidationError);
                Assert.Equal("이름을 1~60자로 입력하세요.", model.NewProjectNameValidationText);
                Assert.Equal("#FFFFF0F3", input.Background.ToString());
                Assert.Equal("#FFBD3C50", input.BorderBrush.ToString());
                var error = Assert.Single(Descendants(view).OfType<TextBlock>().Where(t => t.IsVisible && t.Text == model.NewProjectNameValidationText));
                Assert.NotEmpty(RenderedGlyphs(error));
                Assert.All(RenderedGlyphs(error), glyph => Assert.Equal(11.5, glyph.GlyphRun.FontRenderingEmSize));
                input.SetCurrentValue(TextBox.TextProperty, initialValue);
                Pump(window);
                Assert.False(model.HasNewProjectNameValidationError);
                Assert.Equal("#FFFFFFFF", input.Background.ToString());
                model.CancelNewProjectNameCommand.Execute(null);
                Pump(window);
                Assert.False(view.IsVisible);
                Assert.Equal("Keep current recipe", model.ProjectTree.Roots.Single().DisplayName);
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
                model.NewProjectCommand.Execute(null);
                Pump(window);
                Assert.Equal(initialValue, input.Text);
                Assert.False(model.HasNewProjectNameValidationError);
                model.CancelNewProjectNameCommand.Execute(null);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    [Fact]
    public async Task ConfirmationDisablesNameAndCancelUntilCompletionAndRejectsDuplicateEntry()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = new MachineProjectDocument { Name = "Current recipe" };
            using var model = new MainViewModel(project);
            var view = new NewProjectNameDialogView { DataContext = model };
            var launcher = new Button { Content = "New recipe" };
            var content = new Grid();
            content.Children.Add(launcher);
            content.Children.Add(view);
            var window = new Window { Content = content, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 800, 600);
            var barrier = new TaskCompletionSource();
            var lifecycle = typeof(MainViewModel).GetField("_projectLifecycle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model)!;
            var unsaved = lifecycle.GetType().GetField("_projectUnsavedChangesWorkflow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lifecycle)!;
            var focusedEditor = unsaved.GetType().GetField("_commitFocusedEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var previousCallback = focusedEditor.GetValue(unsaved);
            focusedEditor.SetValue(unsaved, (Func<Task>)(() => barrier.Task));
            project.Name = "Changed current recipe";
            ((ProjectLifecycleCoordinator)lifecycle).MarkChanged();
            model.UnsavedProjectPrompt = () => OpenVisionLab.MachineStudio.Model.UnsavedProjectDecision.Cancel;
            window.Show();
            try
            {
                Pump(window);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                launcher.Focus();
                Assert.Same(launcher, Keyboard.FocusedElement);
                model.NewProjectCommand.Execute(null);
                model.NewProjectNameDraft = "Confirmed recipe";
                var confirmation = model.ConfirmNewProjectNameAsync();
                Pump(window);
                var input = Assert.IsType<TextBox>(view.FindName("ProjectNameTextBox"));
                Assert.False(input.IsEnabled);
                Assert.False(model.IsNewProjectNameEntryEnabled);
                Assert.False(model.CancelNewProjectNameCommand.CanExecute(null));
                Assert.False(model.ConfirmNewProjectNameCommand.CanExecute(null));
                model.CancelNewProjectNameCommand.Execute(null);
                Assert.True(model.IsNewProjectNameDialogOpen);
                Assert.False(await model.ConfirmNewProjectNameAsync());
                Assert.Equal("Current recipe", model.ProjectTree.Roots.Single().DisplayName);
                barrier.SetResult();
                Assert.False(await confirmation);
                Pump(window);
                Assert.True(model.IsNewProjectNameDialogOpen);
                Assert.True(model.IsNewProjectNameEntryEnabled);
                Assert.Same(input, Keyboard.FocusedElement);
                Assert.Equal("Confirmed recipe", input.Text);
                Assert.True(model.CancelNewProjectNameCommand.CanExecute(null));
                focusedEditor.SetValue(unsaved, previousCallback);
                model.UnsavedProjectPrompt = () => OpenVisionLab.MachineStudio.Model.UnsavedProjectDecision.Discard;
                Assert.True(await model.ConfirmNewProjectNameAsync());
                Pump(window);
                Assert.False(model.IsNewProjectNameDialogOpen);
                Assert.Same(launcher, Keyboard.FocusedElement);
                Assert.Equal("Confirmed recipe", model.ProjectTree.Roots.Single().DisplayName);
                Assert.Empty(model.ProjectStations);
                Assert.False(model.IsRunning);
                model.NewProjectCommand.Execute(null);
                Pump(window);
                Assert.True(input.IsEnabled);
                Assert.True(model.CancelNewProjectNameCommand.CanExecute(null));
                model.CancelNewProjectNameCommand.Execute(null);
            }
            finally
            {
                barrier.TrySetResult();
                focusedEditor.SetValue(unsaved, previousCallback);
                window.DataContext = null;
                window.Close();
            }
        });
    }

    private static GlyphRunDrawing[] RenderedGlyphs(DependencyObject root) => Descendants(root).Prepend(root).OfType<Visual>()
        .SelectMany(visual => Glyphs(VisualTreeHelper.GetDrawing(visual))).ToArray();

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
        : drawing is DrawingGroup group ? group.Children.SelectMany(Glyphs) : [];

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
