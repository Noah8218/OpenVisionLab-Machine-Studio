using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Sequence.Authoring;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class ShellCompositionAutomationTests
{
    private readonly StudioUiTestHost _ui;
    public ShellCompositionAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1000, 720, OpenVisionLanguage.Korean)]
    [InlineData(1000, 720, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    public async Task WorkspaceNavigationAndRunControlsFitWithoutReplacingTheirInstances(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            project.Name = new string('가', 60);
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                var toolbar = Assert.IsType<Grid>(window.FindName("CommonWorkspaceToolbar"));
                var commandBar = Assert.IsType<GlobalCommandBarView>(window.FindName("CommonCommandBar"));
                var title = SmokeVisualTreeQuery.FindVisualDescendants<StudioTitleBar>(window).Single();
                var menu = SmokeVisualTreeQuery.FindVisualDescendants<MainMenuView>(title).Single();
                var buttons = new[] { window.EquipmentWorkspaceButton, window.SimulationWorkspaceButton,
                    window.InspectionWorkspaceButton, window.ResultsWorkspaceButton };
                var titleText = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(title).Single(text => text.Text == model.RecipeTitleText);
                Assert.Equal("Segoe UI, Malgun Gothic", titleText.FontFamily.Source);
                var dirtyMarker = Assert.IsType<TextBlock>(title.FindName("TitleSaveStateText"));
                Assert.Equal(project.Name, titleText.Text);
                Assert.True(dirtyMarker.IsVisible);
                Assert.Equal(model.ProjectSaveStateText, dirtyMarker.Text);
                Assert.False(model.IsRunning);
                void AssertTitleBounds()
                {
                    foreach (var button in SmokeVisualTreeQuery.FindVisualDescendants<System.Windows.Controls.Primitives.ButtonBase>(title).Where(button => button.IsVisible))
                    {
                        var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(new Rect(window.RenderSize).Contains(bounds), $"Title button {button.Name} escaped the window: {bounds}");
                    }
                }
                AssertTitleBounds();
                Assert.InRange(toolbar.ActualHeight, 44, width <= 1400 ? 110 : 66);
                foreach (var element in buttons.Cast<FrameworkElement>().Append(commandBar).Append(menu))
                    Assert.True(new Rect(window.RenderSize).Contains(element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize))));

                foreach (var workspace in new[] { 1, 2, 3, 0 })
                {
                    model.Navigation.SelectedWorkspaceIndex = workspace;
                    Pump(window);
                    Assert.Same(commandBar, window.FindName("CommonCommandBar"));
                    Assert.Same(menu, SmokeVisualTreeQuery.FindVisualDescendants<MainMenuView>(title).Single());
                    Assert.All(buttons, button => Assert.True(button.IsVisible));
                    Assert.False(model.IsRunning);
                }

                Assert.True(model.AddLayoutComponentCommand.CanExecute(LayoutComponentKind.MachineFrame));
                model.AddLayoutComponentCommand.Execute(LayoutComponentKind.MachineFrame);
                Pump(window);
                Assert.True(model.HasUnsavedChanges);
                Assert.Equal(project.Name, titleText.Text);
                Assert.True(dirtyMarker.IsVisible);
                Assert.Equal(model.ProjectSaveStateText, dirtyMarker.Text);
                var markerBounds = dirtyMarker.TransformToAncestor(window).TransformBounds(new Rect(dirtyMarker.RenderSize));
                Assert.True(new Rect(window.RenderSize).Contains(markerBounds));
                Assert.True(markerBounds.Width > 0 && markerBounds.Height > 0);
                Assert.Equal(TextTrimming.None, dirtyMarker.TextTrimming);
                AssertTitleBounds();
                var editor = model.Layout.SelectedComponentEditor!;
                editor.DraftName = "Unapplied header state";
                Pump(window);
                AssertTitleBounds();
                Assert.Equal(OpenVisionLanguageService.T("Sequence.StepDraftPending"), dirtyMarker.Text);
                editor.DiscardPlacementDraftCommand.Execute(null);
                Assert.True(model.UndoLayoutEditCommand.CanExecute(null));
                model.UndoLayoutEditCommand.Execute(null);
                Pump(window);
                Assert.Equal(model.RecipeTitleText, titleText.Text);
                Assert.True(dirtyMarker.IsVisible);
                Assert.Equal(model.ProjectSaveStateText, dirtyMarker.Text);
                Assert.False(model.IsRunning);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, OpenVisionLanguage.Korean)]
    [InlineData(1280, OpenVisionLanguage.Korean)]
    [InlineData(1920, OpenVisionLanguage.English)]
    [InlineData(1280, OpenVisionLanguage.English)]
    public async Task R24WorkbarHasNoSpacerAndRetainsLocalizedControlsAndFullTarget(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.MaterialFlow.Enabled = true;
            project.MaterialFlow.TotalFeedCount = 17;
            project.Sequences = [
                new OpenVisionLab.Machine.Core.Sequences.SequenceDefinition { Id = "review-sequence", Name = new string('N', 80), Steps = [new() { Id = "complete", Action = OpenVisionLab.Machine.Core.Sequences.SequenceStepAction.Complete }] },
                new OpenVisionLab.Machine.Core.Sequences.SequenceDefinition { Id = "review-feed", UnitId = "unit-feed", Steps = [
                    new() { Id = "feed", Action = OpenVisionLab.Machine.Core.Sequences.SequenceStepAction.FeedUnitWorkpiece, TargetId = "unit-feed", NextStepId = "complete" },
                    new() { Id = "complete", Action = OpenVisionLab.Machine.Core.Sequences.SequenceStepAction.Complete }
                ] }
            ];
            using var model = new MainViewModel(project);
            model.Navigation.OpenSequenceEditorCommand.Execute(null);
            model.Navigation.SelectedWorkspaceIndex = 0;
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, 900);
            window.Show();
            try
            {
                Pump(window);
                var toolbar = Assert.IsType<Grid>(window.FindName("CommonWorkspaceToolbar"));
                var bar = Assert.IsType<GlobalCommandBarView>(window.FindName("CommonCommandBar"));
                var title = SmokeVisualTreeQuery.FindVisualDescendants<StudioTitleBar>(window).Single();
                var buttons = new[] { window.EquipmentWorkspaceButton, window.SimulationWorkspaceButton, window.InspectionWorkspaceButton, window.ResultsWorkspaceButton };
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                foreach (var workspace in new[] { 0, 1, 2, 3 })
                {
                    model.Navigation.SelectedWorkspaceIndex = workspace;
                    Pump(window);
                    Assert.Equal(44, toolbar.ActualHeight);
                    Assert.Equal(Bounds(title).Bottom, Bounds(toolbar).Top, precision: 2);
                    Assert.Equal(Bounds(toolbar).Top, Bounds(bar).Top, precision: 2);
                    Assert.Equal(Bounds(toolbar).Top, Bounds(buttons[0]).Top, precision: 2);
                    foreach (var button in buttons)
                    {
                        Assert.Equal(width <= 1400 ? 10 : 14, button.Padding.Left);
                        Assert.Equal(width <= 1400 ? 12.5 : 13, button.FontSize);
                        var rendered = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(button).Single();
                        Assert.Equal(button.FontSize, rendered.FontSize);
                        Assert.Equal("Segoe UI, Malgun Gothic", rendered.FontFamily.Source);
                    }
                    Assert.True(Bounds(buttons[^1]).Right <= Bounds(bar).Left);
                    foreach (var name in new[] { "RunPauseCaption", "RunStepCaption", "RunStopCaption", "RunResetCaption" })
                        Assert.Equal(width <= 1400 ? Visibility.Collapsed : Visibility.Visible, Assert.IsType<TextBlock>(bar.FindName(name)).Visibility);
                    var commands = Assert.IsType<StackPanel>(bar.FindName("CommonRunCommands"));
                    Assert.True(new Rect(bar.RenderSize).Contains(commands.TransformToAncestor(bar).TransformBounds(new Rect(commands.RenderSize))));
                    Assert.Same(model.RunCommand, Assert.IsType<Button>(bar.FindName("RunStartButton")).Command);
                    Assert.Same(model.AbortSequenceCommand, Assert.IsType<Button>(bar.FindName("RunStopButton")).Command);
                    Assert.Same(model.ResetCommand, Assert.IsType<Button>(bar.FindName("RunResetButton")).Command);
                    foreach (var name in new[] { "RunStartCaption", "RunPauseCaption", "RunStepCaption", "RunStopGlyph", "RunStopCaption", "RunResetCaption" })
                        Assert.Equal(width <= 1400 ? 12.5 : 13, Assert.IsType<TextBlock>(bar.FindName(name)).FontSize);
                    Assert.Equal("■", Assert.IsType<TextBlock>(bar.FindName("RunStopGlyph")).Text);
                    Assert.Equal(language == OpenVisionLanguage.Korean ? "정지" : "Stop", Assert.IsType<TextBlock>(bar.FindName("RunStopCaption")).Text);
                    Assert.Equal(language == OpenVisionLanguage.Korean ? "편집" : "Editing", Assert.IsType<TextBlock>(bar.FindName("CommonRunStateText")).Text);
                    var summary = Assert.IsType<TextBlock>(bar.FindName("CommonRunSummaryText"));
                    Assert.Equal(model.RunTargetSummaryText, summary.Text);
                    Assert.Contains(language == OpenVisionLanguage.Korean ? "투입 17개" : "Feed 17 items", summary.Text);
                    Assert.Contains(language == OpenVisionLanguage.Korean ? "편차 조건: 미지정" : "Variation condition: Not set", summary.Text);
                    var fullSummary = Assert.IsType<TextBlock>(bar.FindName("CommonRunSummaryDetailsText"));
                    Assert.Equal(model.RunTargetSummaryText, fullSummary.Text);
                    Assert.Equal(TextTrimming.None, fullSummary.TextTrimming);
                    var fullTarget = Assert.IsType<TextBlock>(bar.FindName("CommonRunTargetText"));
                    Assert.Equal(model.RunTargetText, fullTarget.Text);
                    Assert.Contains(project.Sequences[0].Name, fullTarget.Text);
                    Assert.False(model.IsRunning || model.HasUnsavedChanges);
                }
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Fact]
    public async Task OpeningRecipeFromStartupRecoversKeyboardFocusAfterTheLauncherIsHidden()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var model = new MainViewModel();
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
            window.Show();
            try
            {
                Pump(window);
                Assert.True(model.IsStartupChoiceVisible);
                var launcher = Assert.IsType<Button>(window.FindName("OpenRecipeButton"));
                launcher.Focus();
                Assert.Same(launcher, Keyboard.FocusedElement);
                var sample = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine");
                Assert.True(await model.OpenProjectReplacingCurrentAsync(sample));
                Pump(window);
                Assert.False(model.IsStartupChoiceVisible);
                Assert.False(launcher.IsVisible);
                var focus = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                Assert.True(focus.IsVisible);
                Assert.True(window.IsKeyboardFocusWithin);
                Assert.True(model.RunCommand.CanExecute(null));
                Assert.False(model.IsRunning);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(monitor));
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 900)]
    public async Task R24InspectorDefaultsAndManualEquipmentWidthSurviveWorkspaceRoundTrips(int width, int height)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                var columns = Assert.IsType<Grid>(window.FindName("WorkspaceColumnsGrid")).ColumnDefinitions;
                Assert.Equal(360, columns[4].ActualWidth);
                columns[4].Width = new GridLength(410);
                Pump(window);
                model.Navigation.OpenSequenceEditorCommand.Execute(null);
                Pump(window);
                Assert.True(model.Navigation.IsSequenceWorkspace);
                Assert.Equal(360, columns[4].ActualWidth);
                var scene = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("SceneDocumentView"));
                var inspector = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RightToolRegionView"));
                Assert.True(new Rect(window.RenderSize).Contains(inspector.TransformToAncestor(window).TransformBounds(new Rect(inspector.RenderSize))));
                Assert.InRange(scene.ActualWidth, 620, width);
                model.Navigation.IsEquipmentWorkspace = true;
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.Equal(410, columns[4].ActualWidth);
                model.Navigation.IsInspectorOpen = false;
                Pump(window);
                Assert.Equal(0, columns[4].ActualWidth);
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.Equal(410, columns[4].ActualWidth);
                Assert.False(model.IsRunning);
                Assert.False(model.HasUnsavedChanges);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SmokeDpiTestHook.CaptureMonitorEvidence(window)));
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, 1040, false)]
    [InlineData(1280, 760, false)]
    [InlineData(1200, 760, false)]
    [InlineData(1100, 720, true)]
    [InlineData(1000, 720, true)]
    public async Task EquipmentInspectorPreservesSceneAndRestoresNavigationAfterClosing(int width, int height, bool navigationHidden)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            model.Navigation.IsInspectorOpen = false;
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump(window);
                var scene = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("SceneDocumentView"));
                var inspector = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RightToolRegionView"));
                var navigation = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LeftToolRegionView"));
                var columns = Assert.IsType<Grid>(window.FindName("WorkspaceColumnsGrid")).ColumnDefinitions;
                var sceneWidthBefore = scene.ActualWidth;
                var navigationWidthBefore = columns[0].Width;
                var selectedBefore = model.Layout.SelectedItem;
                model.Navigation.IsInspectorOpen = true;
                Pump(window);

                Assert.True(inspector.IsVisible);
                Assert.Equal(navigationHidden, !navigation.IsVisible);
                Assert.InRange(scene.ActualWidth, 620, width);
                var sceneBounds = scene.TransformToAncestor(window).TransformBounds(new Rect(scene.RenderSize));
                var inspectorBounds = inspector.TransformToAncestor(window).TransformBounds(new Rect(inspector.RenderSize));
                Assert.True(sceneBounds.Right <= inspectorBounds.Left);
                Assert.True(new Rect(window.RenderSize).Contains(sceneBounds));
                Assert.True(new Rect(window.RenderSize).Contains(inspectorBounds));
                foreach (var toolbar in SmokeVisualTreeQuery.FindVisualDescendants<WrapPanel>(scene)
                    .Where(panel => panel.Name is "EquipmentAuthoringToolbar" or "EquipmentViewToolbar"))
                {
                    foreach (var control in toolbar.Children.OfType<FrameworkElement>().Where(control => control.IsVisible))
                        Assert.True(new Rect(scene.RenderSize).Contains(control.TransformToAncestor(scene).TransformBounds(new Rect(control.RenderSize))));
                }

                var closeButton = SmokeVisualTreeQuery.FindVisualDescendants<Button>(inspector)
                    .Single(button => button.Name == "CloseInspectorButton");
                Assert.False(closeButton.IsVisible);
                var mainMenu = SmokeVisualTreeQuery.FindVisualDescendants<MainMenuView>(window).Single();
                var menu = SmokeVisualTreeQuery.FindVisualDescendants<Menu>(mainMenu).Single();
                var inspectorMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, OpenVisionLanguageService.T("Shell.View")));
                var inspectorToggle = inspectorMenu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Command, model.Navigation.ToggleInspectorCommand));
                Assert.Equal(OpenVisionLanguageService.T("R24.ViewToggleInspector"), inspectorToggle.Header);
                inspectorMenu.IsSubmenuOpen = true;
                Pump(window);
                Assert.True(inspectorMenu.IsSubmenuOpen);
                Assert.True(inspectorToggle.IsVisible);
                Assert.True(inspectorToggle.IsEnabled);
                Assert.Same(model.Navigation.ToggleInspectorCommand, inspectorToggle.Command);
                Assert.True(inspectorToggle.Command.CanExecute(inspectorToggle.CommandParameter));
                inspectorToggle.Command.Execute(inspectorToggle.CommandParameter);
                inspectorMenu.IsSubmenuOpen = false;
                Pump(window);
                Assert.False(inspector.IsVisible);
                Assert.True(navigation.IsVisible);
                Assert.Equal(0, columns[4].ActualWidth);
                Assert.Equal(0, columns[3].ActualWidth);
                Assert.Equal(navigationWidthBefore, columns[0].Width);
                Assert.Equal(sceneWidthBefore, scene.ActualWidth, 3);
                Assert.Same(selectedBefore, model.Layout.SelectedItem);
                Assert.False(model.IsRunning);

                model.Navigation.IsInspectorOpen = true;
                model.Navigation.IsSimulationWorkspace = true;
                Pump(window);
                Assert.False(model.Navigation.IsInspectorOpen);
                Assert.True(navigation.IsVisible);
                Assert.Equal(0, columns[4].ActualWidth);
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.True(closeButton.IsVisible);
                Assert.True(closeButton.IsEnabled);
                closeButton.Command.Execute(closeButton.CommandParameter);
                model.Navigation.IsEquipmentWorkspace = true;
                Pump(window);
                Assert.Same(scene, window.FindName("SceneDocumentView"));
                Assert.False(model.IsRunning);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SmokeDpiTestHook.CaptureMonitorEvidence(window)));
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
            return true;
        });
    }

    [Fact]
    public async Task EquipmentInspectorFitsAcrossResizesWithARetainedSplitterWidth()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
            window.Show();
            try
            {
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                var scene = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("SceneDocumentView"));
                var inspector = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RightToolRegionView"));
                var columns = Assert.IsType<Grid>(window.FindName("WorkspaceColumnsGrid")).ColumnDefinitions;
                columns[4].Width = new GridLength(410);
                foreach (var width in new[] { 1000, 1100, 1200, 1280, 1920, 1280 })
                {
                    window.Width = width;
                    Pump(window);
                    Assert.InRange(scene.ActualWidth, 620, width);
                    var sceneBounds = scene.TransformToAncestor(window).TransformBounds(new Rect(scene.RenderSize));
                    var inspectorBounds = inspector.TransformToAncestor(window).TransformBounds(new Rect(inspector.RenderSize));
                    Assert.True(sceneBounds.Right <= inspectorBounds.Left);
                    Assert.True(new Rect(window.RenderSize).Contains(inspectorBounds),
                        $"Requested width {width}; window {window.RenderSize}; scene {sceneBounds}; inspector {inspectorBounds}; columns {string.Join(",", columns.Select(column => column.ActualWidth))}");
                }
                model.Navigation.IsInspectorOpen = false;
                Pump(window);
                Assert.Equal(0, columns[4].ActualWidth);
                Assert.Equal(0, columns[3].ActualWidth);
                Assert.True(Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LeftToolRegionView")).IsVisible);
                Assert.False(model.IsRunning);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 760)]
    public async Task HelpLanguageMenuFitsCaptionSelectionAndPopupWithoutChangingProject(double width, double height)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var before = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project);
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                Pump(window);
                var menuEvidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(menuEvidence))
                {
                    Directory.CreateDirectory(menuEvidence);
                    File.WriteAllText(Path.Combine(menuEvidence, $"menu-language-monitor-{width}.json"), System.Text.Json.JsonSerializer.Serialize(SmokeDpiTestHook.CaptureMonitorEvidence(window)));
                }
                var menuView = SmokeVisualTreeQuery.FindVisualDescendants<MainMenuView>(window).Single();
                var menu = SmokeVisualTreeQuery.FindVisualDescendants<Menu>(menuView).Single();
                var help = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, OpenVisionLanguageService.T("Shell.Help")));
                help.IsSubmenuOpen = true;
                Pump(window);
                var languageItem = Assert.IsType<MenuItem>(menuView.FindName("LanguageMenuItem"));
                var header = Assert.IsType<StackPanel>(languageItem.Header);
                var caption = header.Children.OfType<TextBlock>().Single();
                var choice = header.Children.OfType<ComboBox>().Single();
                Assert.True(double.IsNaN(languageItem.Height));
                foreach (var language in new[] { OpenVisionLanguage.English, OpenVisionLanguage.Korean })
                {
                    choice.SelectedItem = model.Navigation.LanguageOptions.Single(option => option.Language == language);
                    Pump(window);
                    Assert.Equal(language, OpenVisionLanguageService.CurrentLanguage);
                    Assert.Same(model.Navigation.SelectedLanguageOption, choice.SelectedItem);
                    Assert.Equal(OpenVisionLanguageService.T("Shell.Language"), caption.Text);
                    Assert.True(languageItem.ActualHeight >= header.ActualHeight);
                    Assert.True(new Rect(header.RenderSize).Contains(choice.TransformToAncestor(header).TransformBounds(new Rect(choice.RenderSize))));
                    choice.IsDropDownOpen = true;
                    Pump(window);
                    var popup = SmokeVisualTreeQuery.FindVisualDescendants<System.Windows.Controls.Primitives.Popup>(choice).Single();
                    Assert.True(popup.IsOpen);
                    Assert.Equal(2, choice.Items.Count);
                    Assert.All(SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(popup.Child).Where(text => text.IsVisible), text => Assert.True(text.ActualHeight >= text.FontSize));
                    choice.IsDropDownOpen = false;
                    Pump(window);
                }
                help.IsSubmenuOpen = false;
                Pump(window);
                Assert.False(help.IsSubmenuOpen);
                Assert.Equal(before, new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                var options = OpenVisionLab.MachineStudio.View.Dialogs.MainMessageDialogHost.CreatePlacementDraftDialogOptions();
                Assert.Equal("적용 후 계속", options.PrimaryButtonText);
                Assert.Equal("적용 안 함", options.SecondaryButtonText);
                Assert.Equal("취소", options.TertiaryButtonText);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }


    [Theory]
    [InlineData(1920)]
    [InlineData(1280)]
    public async Task R24CommonBottomTabsKeepTheSameHeaderAndBodyAcrossWorkspaces(int width)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, 900);
            window.Show();
            try
            {
                var journal = SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Diagnostics.EventJournalView>(window).Single(view => !view.IsInline);
                var panel = Assert.IsType<Grid>(journal.FindName("EquipmentBottomPanel"));
                var legacy = Assert.IsType<Grid>(journal.FindName("LegacyJournal"));
                var header = Assert.IsType<Grid>(journal.FindName("EquipmentBottomHeader"));
                var body = Assert.IsType<Border>(journal.FindName("EquipmentBottomBody"));
                var row = Assert.IsType<RowDefinition>(window.FindName("BottomWorkspaceRow"));
                foreach (var workspace in new[] { 0, 1, 2, 3 })
                {
                    model.Navigation.SelectedWorkspaceIndex = workspace;
                    model.Navigation.IsEvidenceExpanded = false;
                    Pump(window);
                    Assert.True(panel.IsVisible);
                    Assert.False(legacy.IsVisible || body.IsVisible);
                    Assert.Equal(30, header.ActualHeight);
                    Assert.Equal(31, row.ActualHeight);
                    Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                    model.Navigation.SelectEquipmentEvidenceTabCommand.Execute("1");
                    Pump(window);
                    Assert.True(body.IsVisible);
                    Assert.Equal(221, row.ActualHeight);
                    Assert.Equal(190, body.ActualHeight);
                    model.Navigation.SelectEquipmentEvidenceTabCommand.Execute("1");
                    Pump(window);
                    Assert.False(body.IsVisible);
                    Assert.Equal(31, row.ActualHeight);
                }
                return true;
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, OpenVisionLanguage.Korean)]
    [InlineData(1280, OpenVisionLanguage.Korean)]
    [InlineData(1920, OpenVisionLanguage.English)]
    [InlineData(1280, OpenVisionLanguage.English)]
    public async Task R24EquipmentTreeAndBottomTabsRenderAtTheirOwnSizesWithoutRecipeActions(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            project.Layouts[0].Components.Reverse();
            var before = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project);
            using var model = new MainViewModel(project);
            model.Navigation.IsEquipmentWorkspace = true;
            model.Layout.Select("belt");
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, 900);
            window.Show();
            try
            {
                Pump(window);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                var descendants = SmokeVisualTreeQuery.FindVisualDescendants<FrameworkElement>(window).ToArray();
                var journal = descendants.OfType<OpenVisionLab.MachineStudio.View.Diagnostics.EventJournalView>().Single(view => !view.IsInline);
                var panel = Assert.IsType<Grid>(journal.FindName("EquipmentBottomPanel"));
                var header = Assert.IsType<Grid>(journal.FindName("EquipmentBottomHeader"));
                var body = Assert.IsType<Border>(journal.FindName("EquipmentBottomBody"));
                var legacy = Assert.IsType<Grid>(journal.FindName("LegacyJournal"));
                Assert.True(panel.IsVisible);
                Assert.False(legacy.IsVisible || body.IsVisible);
                var row = Assert.IsType<RowDefinition>(window.FindName("BottomWorkspaceRow"));
                Assert.Equal(31, row.ActualHeight);
                Assert.Equal(30, header.ActualHeight);
                var status = descendants.OfType<StudioStatusBarView>().Single();
                Assert.Equal(24, status.ActualHeight);
                var simulationTime = Assert.IsType<TextBlock>(status.FindName("StatusSimulationTimeText"));
                var simulationSpeed = Assert.IsType<TextBlock>(status.FindName("StatusSimulationSpeedText"));
                Assert.Equal(language == OpenVisionLanguage.Korean ? "시뮬레이션 0.00 s" : "Simulation 0.00 s", simulationTime.Text);
                Assert.Equal("×1", simulationSpeed.Text);
                Assert.Equal(11.5, simulationTime.FontSize);
                Assert.Equal("#FF52687C", simulationTime.Foreground.ToString());
                Assert.True(simulationTime.IsVisible && simulationSpeed.IsVisible);
                Assert.False(Assert.IsType<TextBlock>(status.FindName("CommonSaveStateText")).IsVisible);
                Assert.False(Assert.IsType<TextBlock>(status.FindName("StatusRecipeSummaryText")).IsVisible);
                Assert.InRange(simulationTime.TranslatePoint(new Point(), status).X, 9.5, 10.5);
                var statusSelection = Assert.IsType<TextBlock>(status.FindName("StatusEquipmentSelectionText"));
                Assert.True(statusSelection.IsVisible);
                Assert.Equal(model.EquipmentSelectionStatusText, statusSelection.Text);
                Assert.Equal(11.5, statusSelection.FontSize);
                Assert.Equal("#FF52687C", statusSelection.Foreground.ToString());
                Assert.True(statusSelection.TranslatePoint(new Point(), status).X > simulationSpeed.TranslatePoint(new Point(simulationSpeed.ActualWidth, 0), status).X);
                Assert.True(statusSelection.TranslatePoint(new Point(statusSelection.ActualWidth, 0), status).X <= status.ActualWidth);
                var left = descendants.OfType<OpenVisionLab.MachineStudio.View.Project.LeftToolRegionView>().Single();
                var surface = Assert.IsType<Border>(left.FindName("NavigationPanelSurface"));
                Assert.Equal(width == 1920 ? 230 : 210, left.ActualWidth);
                Assert.Equal(new Thickness(0, 0, 1, 0), surface.BorderThickness);
                Assert.Equal("#FFF8FAFC", surface.Background.ToString());
                Assert.Equal("#FFDCE3EB", surface.BorderBrush.ToString());
                Assert.False(Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LeftWorkspaceSplitter")).IsVisible);
                var addUnit = Assert.IsType<Button>(left.FindName("OutlineAddUnitButton"));
                var addDevice = Assert.IsType<Button>(left.FindName("OutlineAddDeviceButton"));
                Assert.InRange(addDevice.TranslatePoint(new Point(), left).X - addUnit.TranslatePoint(new Point(addUnit.ActualWidth, 0), left).X, 5.9, 6.1);
                var outline = descendants.OfType<OpenVisionLab.MachineStudio.View.Project.EquipmentOutlineView>().Single();
                var expectedParts = model.Layout.Definition!.Components.Where(component => component.Kind != OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.Workpiece && component.UnitId == model.Layout.EquipmentOutlineStations[0].Units[0].UnitId).Select(component => component.Id);
                Assert.Equal(expectedParts, model.Layout.EquipmentOutlineStations[0].Units[0].Parts.Select(part => part.Item.Id));
                Assert.Equal(model.Layout.Definition.Components.OrderBy(component => component.ZIndex).ThenBy(component => component.Id, StringComparer.Ordinal).Select(component => component.Id), model.Layout.Items.Select(item => item.Id));
                var overview = Assert.IsType<Button>(outline.FindName("OutlineOverviewButton"));
                var overviewIcon = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(overview).Single(text => text.Text == "▦");
                Assert.Equal("Malgun Gothic", overviewIcon.FontFamily.Source);
                Assert.InRange(overviewIcon.TranslatePoint(new Point(), overview).X, 6.9, 7.1);
                var outlineScroll = Assert.IsType<ScrollViewer>(outline.FindName("OutlineListScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, outlineScroll.VerticalScrollBarVisibility);
                Assert.All(SmokeVisualTreeQuery.FindVisualDescendants<System.Windows.Controls.Primitives.ScrollBar>(outlineScroll), bar => Assert.Equal(0, bar.Opacity));
                var search = Assert.IsType<TextBox>(outline.FindName("OutlineSearchTextBox"));
                Assert.Equal(width == 1920 ? 13 : 12.5, search.FontSize);
                Assert.Equal("#FFFFFFFF", search.Background.ToString());
                var selected = SmokeVisualTreeQuery.FindVisualDescendants<Button>(outline).Single(button => button.DataContext is EquipmentOutlinePartItem part && part.Item.Id == "belt");
                var selectedName = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(selected).Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "DisplayName");
                Assert.Equal(width == 1920 ? 13 : 12.5, selectedName.FontSize);
                Assert.Equal("#FF087F73", selectedName.Foreground.ToString());
                Assert.Equal("#FFE4F4EF", selected.Background.ToString());
                var selectedIcon = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(selected).Single(text => text.Text == "▭");
                Assert.Equal("#FF52687C", selectedIcon.Foreground.ToString());
                Assert.Equal(width == 1920 ? 13 : 12.5, selectedIcon.FontSize);
                var detail = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(selected).Single(text => text.Text.Contains(" · ", StringComparison.Ordinal));
                Assert.Equal(TextTrimming.None, detail.TextTrimming);
                Assert.True(detail.ClipToBounds);
                var names = new[] { "BottomEventsTab", "BottomRequestsTab", "BottomGenerationTab", "BottomAlarmsTab" };
                var keys = new[] { "Events", "Requests", "Generation", "Alarms" };
                var tabs = names.Select(name => Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(journal.FindName(name))).ToArray();
                foreach (var (tab, index) in tabs.Select((tab, index) => (tab, index)))
                {
                    Assert.Same(model.Navigation.SelectEquipmentEvidenceTabCommand, tab.Command);
                    Assert.Equal(index.ToString(), tab.CommandParameter);
                    Assert.Equal(24, tab.ActualHeight);
                    var caption = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(tab).Single(text => text.Text == OpenVisionLanguageService.T("R24.Bottom." + keys[index]));
                    Assert.Equal(12, caption.FontSize);
                    Assert.Equal(17.4, caption.LineHeight);
                    Assert.Equal("#FF243448", caption.Foreground.ToString());
                    var border = Assert.IsType<Border>(tab.Template.FindName("BottomButtonBorder", tab));
                    Assert.Equal("#FFEAF0F6", border.Background.ToString());
                    Assert.Equal(new Thickness(1), border.BorderThickness);
                    Assert.Equal(new CornerRadius(5), border.CornerRadius);
                    if (index > 0) Assert.True(tab.TransformToAncestor(header).Transform(new Point()).X > tabs[index - 1].TransformToAncestor(header).Transform(new Point()).X);
                }
                for (var index = 0; index < tabs.Length; index++)
                {
                    tabs[index].Command!.Execute(tabs[index].CommandParameter);
                    Pump(window);
                    Assert.True(model.Navigation.IsEvidenceExpanded && body.IsVisible);
                    Assert.Equal(index, model.Navigation.SelectedEquipmentEvidenceTabIndex);
                    if (index == 2)
                    {
                        var guidance = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(body).Single(text => text.IsVisible);
                        Assert.Equal(language == OpenVisionLanguage.Korean
                            ? "AI 생성 작업이 없습니다. AI 생성 이미지 세트나 \"촬영 시마다 생성\" 소스를 쓰면 여기에 표시됩니다."
                            : "No AI generation jobs. Jobs appear here when using an AI-generated image set or a \"generate on every capture\" source.", guidance.Text);
                    }
                    Assert.Equal(221, row.ActualHeight);
                    Assert.Equal(190, body.ActualHeight);
                    Assert.Single(tabs.Where(tab => tab.IsChecked == true));
                    var selectedBorder = Assert.IsType<Border>(tabs[index].Template.FindName("BottomButtonBorder", tabs[index]));
                    Assert.Equal("#FFE4F4EF", selectedBorder.Background.ToString());
                    Assert.Equal("#FF087F73", selectedBorder.BorderBrush.ToString());
                    var caption = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(tabs[index]).Single(text => text.Text == OpenVisionLanguageService.T("R24.Bottom." + keys[index]));
                    Assert.Equal("#FF087F73", caption.Foreground.ToString());
                    var drawer = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(journal.FindName("BottomOpenCloseButton"));
                    Assert.True(drawer.IsChecked);
                    Assert.Equal("#FFEAF0F6", Assert.IsType<Border>(drawer.Template.FindName("BottomButtonBorder", drawer)).Background.ToString());
                    drawer.IsChecked = false;
                    Pump(window);
                    Assert.False(model.Navigation.IsEvidenceExpanded || body.IsVisible);
                    Assert.DoesNotContain(tabs, tab => tab.IsChecked == true);
                    drawer.IsChecked = true;
                    Pump(window);
                    Assert.Equal(index, model.Navigation.SelectedEquipmentEvidenceTabIndex);
                    tabs[index].Command.Execute(tabs[index].CommandParameter);
                    Pump(window);
                    Assert.False(model.Navigation.IsEvidenceExpanded);
                }
                model.Navigation.IsSimulationWorkspace = true;
                model.Navigation.IsEvidenceExpanded = true;
                Pump(window);
                Assert.True(panel.IsVisible && body.IsVisible);
                Assert.False(legacy.IsVisible || journal.EvidenceTabs.IsVisible);
                Assert.Equal(30, header.ActualHeight);
                Assert.Equal(190, body.ActualHeight);
                model.Navigation.IsEquipmentWorkspace = true;
                model.Navigation.IsEvidenceExpanded = false;
                Pump(window);
                Assert.True(panel.IsVisible);
                Assert.False(legacy.IsVisible);
                Assert.Equal(before, new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task R24BottomAlarmsAggregateReadinessAndRecoverThroughExistingOwners(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            var store = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore();
            var before = store.Serialize(project);
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                model.Navigation.SelectEquipmentEvidenceTabCommand.Execute("3");
                var journal = SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Diagnostics.EventJournalView>(window).Single();
                var row = Assert.IsType<Border>(journal.FindName("BottomReadinessRow"));
                var chip = Assert.IsType<Border>(journal.FindName("BottomReadinessChip"));
                var caption = Assert.IsType<TextBlock>(journal.FindName("BottomReadinessCaption"));
                var detail = Assert.IsType<TextBlock>(journal.FindName("BottomReadinessDetail"));
                var count = Assert.IsType<TextBlock>(journal.FindName("BottomAlarmCountText"));
                var empty = Assert.IsType<TextBlock>(journal.FindName("BottomAlarmsEmpty"));
                var tab = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(journal.FindName("BottomAlarmsTab"));
                var dryRun = model.RecipeConnections.DryRun;
                void AssertState(int expectedCount, bool visible, string? border = null, string? foreground = null)
                {
                    Pump(window);
                    Assert.Equal(expectedCount, model.BottomAlarmCount);
                    Assert.Equal(expectedCount.ToString(), count.Text);
                    Assert.Equal(expectedCount > 0, count.IsVisible);
                    Assert.Equal(expectedCount == 0, empty.IsVisible);
                    Assert.Equal(visible, row.IsVisible);
                    if (!visible) return;
                    Assert.Equal(dryRun.ReadinessDetailText, detail.Text);
                    Assert.Equal(border, chip.BorderBrush.ToString());
                    Assert.Equal(foreground, caption.Foreground.ToString());
                    Assert.Equal(new Thickness(1), chip.BorderThickness);
                    Assert.Equal(new CornerRadius(12), chip.CornerRadius);
                    Assert.InRange(chip.ActualHeight, 22, 22.5);
                    Assert.Equal(11.5, caption.FontSize);
                    Assert.Equal(16.675, caption.LineHeight);
                    Assert.Equal("Segoe UI, Malgun Gothic", caption.FontFamily.Source);
                    Assert.True(new Rect(row.RenderSize).Contains(detail.TransformToAncestor(row).TransformBounds(new Rect(detail.RenderSize))));
                }
                AssertState(0, false);
                Assert.True(dryRun.ValidateSimulationReadinessCommand.CanExecute(null));
                var deviceId = project.Devices[1].Id;
                try
                {
                    project.Devices[1].Id = project.Devices[0].Id;
                    dryRun.ValidateSimulationReadinessCommand.Execute(null);
                    Assert.False(dryRun.ReadinessPassed);
                    Assert.Contains("DuplicateDeviceId", dryRun.ReadinessDetailText, StringComparison.Ordinal);
                    AssertState(1, true, "#FFEABEC8", "#FFBD3C50");
                    Assert.Equal(OpenVisionLanguageService.T("R24.Bottom.Alarms"), caption.Text);
                }
                finally { project.Devices[1].Id = deviceId; }
                model.RuntimeDebugger.ApplySnapshot(new OpenVisionLab.Machine.Simulation.Snapshots.SimulationSnapshot(TimeSpan.Zero, 1,
                    OpenVisionLab.Machine.Simulation.Engine.SimulationRunMode.Paused, OpenVisionLab.Machine.Simulation.Engine.SimulationControlOwner.Definition,
                    1, [], 0, [], [], [], OpenVisionLab.Machine.Simulation.Snapshots.AutomaticRunSnapshot.NotConfigured, [],
                    [new OpenVisionLab.Machine.Simulation.Faults.SimulationFaultSnapshot(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisMotionBlocked, "axis-review", null, 1, TimeSpan.Zero)]));
                AssertState(2, true, "#FFEABEC8", "#FFBD3C50");
                dryRun.SetProjectRevision(2);
                AssertState(2, true, "#FFE5CF9A", "#FF986610");
                Assert.Equal(OpenVisionLanguageService.T("Equipment.State.Warning"), caption.Text);
                Assert.DoesNotContain("DuplicateDeviceId", detail.Text, StringComparison.Ordinal);
                dryRun.ValidateSimulationReadinessCommand.Execute(null);
                Assert.True(dryRun.ReadinessPassed);
                AssertState(1, false);
                model.RuntimeDebugger.LoadProject(project, resetSession: true);
                dryRun.Load(EquipmentMaterialFlowViewModelTests.CreateProject());
                AssertState(0, false);
                tab.Command!.Execute(tab.CommandParameter);
                Pump(window);
                Assert.False(model.Navigation.IsEvidenceExpanded);
                Assert.Equal(before, store.Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task R24RequestArtifactContextRendersReadOnlyValuesAndKeepsFullHistory(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            var before = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project);
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                Pump(window);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                var evidenceRoot = Path.Combine(OpenVisionLab.TestSupport.TestStorage.RootPath, "request-stages-render-monitor");
                Directory.CreateDirectory(evidenceRoot);
                File.WriteAllText(Path.Combine(evidenceRoot, $"{language}-{width}-{height}.json"), System.Text.Json.JsonSerializer.Serialize(monitor));
                model.Navigation.SelectEquipmentEvidenceTabCommand.Execute("1");
                var source = "artifacts/" + new string('a', 96) + "-inspection-source.png";
                var recipe = "artifacts/inspection-recipe.json";
                var consumer = new IntegrationApplicationIdentity(IntegrationApplicationIds.TwoDStudio, "review-only", new string('2', 40), IntegrationSourceState.Clean);
                var context = new IntegrationInspectionContextV2("review-project", "1.0", "review-sequence", "review-step", "review-camera", "review-acquisition", "review-frame", "mm",
                    IntegrationInspectionModality.TwoD, IntegrationInspectionInputKind.Image, new string('1', 64), new string('2', 64), consumer,
                    [new IntegrationArtifactReference(IntegrationArtifactRoles.InspectionSource, "source", source, 1, new string('1', 64)),
                     new IntegrationArtifactReference(IntegrationArtifactRoles.InspectionRecipe, "recipe", recipe, 1, new string('2', 64))]);
                var handoff = new IntegrationHandoffV2(IntegrationContractSchema.V2, IntegrationMessageKind.Handoff, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                    new IntegrationApplicationIdentity(IntegrationApplicationIds.MachineStudio, "review-only", new string('1', 40), IntegrationSourceState.Clean), context);
                var row = new MachineIntegrationTransactionHistoryItem(handoff, "Pending") { HistoryText = "Owned read-only display fixture" };
                var journal = SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Diagnostics.EventJournalView>(window).Single();
                var list = Assert.IsType<ItemsControl>(journal.FindName("BottomRequestsList"));
                list.ItemsSource = new[] { row };
                Pump(window);
                var text = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(list).Single(item => item.Name == "BottomRequestContextText");
                var runs = text.Inlines.OfType<Run>().ToArray();
                var visible = string.Concat(runs.Select(run => run.Text));
                Assert.Contains(source, visible, StringComparison.Ordinal);
                Assert.Contains(recipe, visible, StringComparison.Ordinal);
                Assert.Contains(context.CameraId, visible, StringComparison.Ordinal);
                Assert.Contains(language == OpenVisionLanguage.Korean ? "이미지" : "Image", visible, StringComparison.Ordinal);
                Assert.Equal("Consolas", runs.Single(run => run.Name == "BottomRequestInspectionSource").FontFamily.Source);
                Assert.Equal(11.5, text.FontSize);
                Assert.Equal(16.675, text.LineHeight);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                var history = SmokeVisualTreeQuery.FindVisualDescendants<Expander>(list).Single();
                history.IsExpanded = true;
                Pump(window);
                var detail = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(history).Single(item => item.Name == "BottomRequestHistoryText");
                Assert.Contains(context.AcquisitionId, detail.Text, StringComparison.Ordinal);
                Assert.Contains(context.FrameId, detail.Text, StringComparison.Ordinal);
                history.IsExpanded = false;
                Pump(window);
                var namedRow = row with { PublishedCameraName = "Owned camera " + new string('C', 80), PublishedStepName = "Owned step " + new string('S', 80) };
                list.ItemsSource = new[] { namedRow }; Pump(window);
                text = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(list).Single(item => item.Name == "BottomRequestContextText");
                visible = string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text));
                Assert.Contains(namedRow.PublishedCameraName, visible, StringComparison.Ordinal);
                Assert.Contains(namedRow.PublishedStepName, visible, StringComparison.Ordinal);
                Assert.Contains(source, visible, StringComparison.Ordinal); Assert.Contains(recipe, visible, StringComparison.Ordinal);
                foreach (var run in text.Inlines.OfType<Run>())
                    if (BindingOperations.GetBinding(run, Run.TextProperty) is { } binding) Assert.Equal(BindingMode.OneWay, binding.Mode);
                new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32).Render(window);
                IEnumerable<GlyphRunDrawing> ReadGlyphs(Drawing? drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(ReadGlyphs) : [];
                var glyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                Assert.NotEmpty(glyphs);
                Assert.All(glyphs, glyph => { Assert.Equal(11.5, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); });
                var namesEvidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_SOURCE_NAMES_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(namesEvidence))
                {
                    Directory.CreateDirectory(namesEvidence);
                    var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(namesEvidence, $"request-names-{(int)width}-{language}.png")); encoder.Save(output);
                }
                history = SmokeVisualTreeQuery.FindVisualDescendants<Expander>(list).Single(); history.IsExpanded = true; Pump(window);
                detail = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(history).Single(item => item.Name == "BottomRequestHistoryText");
                Assert.Contains(context.CameraId, detail.Text, StringComparison.Ordinal); Assert.Contains(context.StepId, detail.Text, StringComparison.Ordinal);
                history.IsExpanded = false; Pump(window);
                foreach (var badgeRow in new[]
                {
                    row,
                    row with { IsAutomaticRequest = false, HasValidatedResult = true },
                    row with { IsAutomaticRequest = true, HasValidatedResult = true, IsResultApplied = true },
                    row with { IsAutomaticRequest = true, HasValidatedResult = true, IsLateResult = true, LateResultText = language == OpenVisionLanguage.Korean ? "지연 수신 OK · 미반영" : "Late result Pass · not applied" }
                })
                {
                    list.ItemsSource = new[] { badgeRow };
                    Pump(window);
                    var purpose = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(list).Single(item => item.Name == "BottomRequestPurposeText");
                    var application = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(list).Single(item => item.Name == "BottomRequestApplicationText");
                    var late = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(list).Single(item => item.Name == "BottomRequestLateText");
                    Assert.Equal(badgeRow.HasKnownPurpose, purpose.IsVisible);
                    Assert.Equal(badgeRow.HasApplicationStatus, application.IsVisible);
                    Assert.Equal(badgeRow.IsLateResult, late.IsVisible);
                    Assert.Equal(badgeRow.PurposeText, purpose.Text);
                    Assert.Equal(badgeRow.ApplicationText, application.Text);
                    Assert.Equal(badgeRow.LateResultText, late.Text);
                    foreach (var textBlock in new[] { purpose, application, late }.Where(item => item.IsVisible))
                    {
                        Assert.Equal(11.5, textBlock.FontSize);
                        Assert.Equal(16.675, textBlock.LineHeight);
                        Assert.True(textBlock.ActualWidth >= textBlock.DesiredSize.Width - 1);
                        var bounds = textBlock.TransformToAncestor(list).TransformBounds(new Rect(textBlock.RenderSize));
                        Assert.True(bounds.Right <= list.ActualWidth + 1 && bounds.Left >= 0);
                    }
                    if (badgeRow.IsResultApplied) Assert.Equal("#FF147854", application.Foreground.ToString());
                    if (badgeRow.IsLateResult) Assert.Equal("#FFBD3C50", late.Foreground.ToString());
                }
                // These records verify bindings/rendering only; publisher/validator coverage belongs to the integration tests.
                foreach (var stageRow in new[]
                {
                    row,
                    row with { HasReceivedAcknowledgement = true },
                    row with { HasReceivedAcknowledgement = true, HasReceivedResult = true, HasValidatedResult = true, InspectionCompleted = true },
                    row with { HasReceivedAcknowledgement = true, HasReceivedResult = true, HasValidatedResult = true, StatusTone = "bad" },
                    row with { HasReceivedAcknowledgement = true, StatusTone = "bad" },
                    row with { HasReceivedResult = true }
                })
                {
                    list.ItemsSource = new[] { stageRow }; Pump(window);
                    var captions = new[] { stageRow.SentCaption, stageRow.AcknowledgementCaption, stageRow.ResultCaption };
                    var expected = new[] { stageRow.ShowTransferStage, stageRow.ShowAcknowledgementStage, stageRow.ShowResultStage };
                    var stages = SmokeVisualTreeQuery.FindVisualDescendants<Border>(list).Where(border => border.Tag is bool).ToArray();
                    Assert.Equal(3, stages.Length);
                    for (var index = 0; index < stages.Length; index++)
                    {
                        Assert.Equal(expected[index], stages[index].Tag);
                        var caption = Assert.IsType<TextBlock>(stages[index].Child);
                        Assert.Equal(captions[index], caption.Text);
                        Assert.Equal(expected[index] ? "#FF087F73" : "#FF52687C", caption.Foreground.ToString());
                        Assert.True(caption.ActualWidth >= caption.DesiredSize.Width - 1);
                    }
                    var hint = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(list).Single(item => item.Name == "BottomRequestExecutionStatusHint");
                    Assert.Equal(stageRow.ExecutionStatusHint, hint.Text);
                    Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
                    Assert.True(hint.ActualHeight >= hint.DesiredSize.Height - hint.Margin.Top - hint.Margin.Bottom - 1);
                    var hintBounds = hint.TransformToAncestor(list).TransformBounds(new Rect(hint.RenderSize));
                    Assert.True(hintBounds.Left >= 0 && hintBounds.Right <= list.ActualWidth + 1, $"Hint bounds={hintBounds}; list width={list.ActualWidth}; desired={hint.DesiredSize}; actual={hint.RenderSize}");
                }
                Assert.Equal(source, row.InspectionSourcePath);
                Assert.Equal(before, new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, 230, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, 210, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, 230, OpenVisionLanguage.English)]
    [InlineData(1280, 900, 210, OpenVisionLanguage.English)]
    public async Task R24SequenceContainersUseSourceWidthsAndRestoreOtherWorkspaceGeometry(int width, int height, int leftWidth, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            var before = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project);
            using var model = new MainViewModel(project, integrationSettingsPath: Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT") ?? Path.GetTempPath(), "sidebar-frame-empty-integration.json"));
            model.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                model.SequenceEditor.ProcessViewIndex = 1;
                model.SequenceEditor.IsProcessSceneVisible = true;
                model.Navigation.OpenSequenceEditorCommand.Execute(null);
                Pump(window);
                var scene = Assert.IsType<OpenVisionLab.MachineStudio.View.Scene.SceneDocumentView>(window.FindName("SceneDocumentView"));
                var columns = Assert.IsType<Grid>(window.FindName("WorkspaceColumnsGrid")).ColumnDefinitions;
                var rightSplitter = Assert.IsType<GridSplitter>(window.FindName("RightWorkspaceSplitter"));
                var stepsSplitter = Assert.IsType<GridSplitter>(scene.FindName("SequenceSceneStepsSplitter"));
                void CheckSequence()
                {
                    Pump(window);
                    Assert.True(model.Navigation.IsSequenceWorkspace);
                    Assert.Equal(leftWidth, columns[0].ActualWidth);
                    Assert.Equal(0, columns[1].ActualWidth);
                    Assert.Equal(5, columns[3].ActualWidth);
                    Assert.Equal(360, columns[4].ActualWidth);
                    Assert.False(Assert.IsType<GridSplitter>(window.FindName("LeftWorkspaceSplitter")).IsVisible);
                    Assert.Equal(leftWidth, scene.TransformToAncestor(window).Transform(new Point()).X);
                    Assert.Equal(width - leftWidth - 365, scene.ActualWidth);
                    Assert.Equal(new Thickness(1, 0, 1, 0), rightSplitter.BorderThickness);
                    Assert.Equal("#FFDCE3EB", rightSplitter.BorderBrush.ToString());
                    Assert.Equal(5, stepsSplitter.ActualHeight);
                    Assert.Equal(new Thickness(0, 1, 0, 1), stepsSplitter.BorderThickness);
                    Assert.Equal("#FFFFFFFF", stepsSplitter.Background.ToString());
                    var editor = SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Sequence.SequenceEditorView>(scene).Single(view => view.IsVisible);
                    Assert.Equal(250, editor.ActualHeight);
                    Assert.Equal(24, window.CaptureLayoutMetrics().StatusHeight);
                    Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                    new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32).Render(window);
                }
                CheckSequence();
                var sceneInstance = scene;
                model.Navigation.IsInspectorOpen = false;
                Pump(window);
                Assert.Equal(0, columns[3].ActualWidth);
                Assert.Equal(0, columns[4].ActualWidth);
                Assert.Equal(width - leftWidth, scene.ActualWidth);
                model.Navigation.IsInspectorOpen = true;
                CheckSequence();
                void CheckDocumentSidebar(int documentIndex, RadioButton button)
                {
                    Pump(window);
                    Assert.Equal(documentIndex, model.Navigation.SelectedDocumentTabIndex);
                    Assert.True(button.IsChecked);
                    Assert.Equal(documentIndex == 1, ShellWindow.GetIsInspectionWorkspace(window));
                    Assert.Equal(documentIndex == 3, ShellWindow.GetIsResultsWorkspace(window));
                    Assert.Equal(leftWidth, columns[0].ActualWidth);
                    Assert.Equal(0, columns[1].ActualWidth);
                    Assert.False(Assert.IsType<GridSplitter>(window.FindName("LeftWorkspaceSplitter")).IsVisible);
                    Assert.Equal(0, columns[3].ActualWidth);
                    Assert.Equal(0, columns[4].ActualWidth);
                    Assert.Equal(24, window.CaptureLayoutMetrics().StatusHeight);
                }
                foreach (var (documentIndex, buttonName) in new[] { (1, "InspectionWorkspaceButton"), (3, "ResultsWorkspaceButton") })
                {
                    var button = Assert.IsType<RadioButton>(window.FindName(buttonName));
                    model.Navigation.SelectedDocumentTabIndex = documentIndex;
                    CheckDocumentSidebar(documentIndex, button);
                    model.Navigation.OpenSequenceEditorCommand.Execute(null);
                    CheckSequence();
                    button.SetCurrentValue(RadioButton.IsCheckedProperty, true);
                    button.GetBindingExpression(RadioButton.IsCheckedProperty)!.UpdateSource();
                    CheckDocumentSidebar(documentIndex, button);
                }
                model.Navigation.OpenSequenceEditorCommand.Execute(null);
                model.Navigation.OpenTestScenarioCommand.Execute(null);
                Pump(window);
                CheckSequence();
                Assert.True(SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Simulation.SimulationTestWorkspaceView>(window).Single().IsVisible);
                model.Navigation.CloseTestScenarioCommand.Execute(null);
                model.Navigation.OpenSequenceEditorCommand.Execute(null);
                CheckSequence();
                model.Navigation.IsEquipmentWorkspace = true;
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.Equal(leftWidth, columns[0].ActualWidth);
                Assert.Equal(0, columns[1].ActualWidth);
                Assert.Equal(5, columns[3].ActualWidth);
                Assert.Equal(24, window.CaptureLayoutMetrics().StatusHeight);
                model.Navigation.OpenSequenceEditorCommand.Execute(null);
                CheckSequence();
                Assert.Same(sceneInstance, window.FindName("SceneDocumentView"));
                Assert.Equal(before, new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project));
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
                var evidenceRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidenceRoot))
                {
                    Directory.CreateDirectory(evidenceRoot);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(evidenceRoot, $"sequence-container-{width}-{language}.png")); encoder.Save(file);
                    File.WriteAllText(Path.Combine(evidenceRoot, $"sequence-container-{width}-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window), metrics = window.CaptureLayoutMetrics(), columns = columns.Select(column => column.ActualWidth), model.HasUnsavedChanges, model.IsRunning }));
                }
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task MainMenuR24LocalTemplatesRenderActualCaptionsShortcutsAndPreserveCommands(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var before = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project);
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                Pump(window);
                var view = SmokeVisualTreeQuery.FindVisualDescendants<MainMenuView>(window).Single();
                var menu = SmokeVisualTreeQuery.FindVisualDescendants<Menu>(view).Single();
                Assert.Same(model, view.DataContext);
                Assert.Equal(5, menu.Items.Count);
                Assert.Equal(new Thickness(0), menu.Padding);
                double size = width == 1920 ? 13 : 12.5;
                double line = size * 1.45;
                var observations = new List<object>();
                IEnumerable<GlyphRunDrawing> ReadGlyphs(Drawing? drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(ReadGlyphs) : [];
                string RenderedText(TextBlock text) => new(ReadGlyphs(VisualTreeHelper.GetDrawing(text)).SelectMany(run => run.GlyphRun.Characters ?? Array.Empty<char>()).ToArray());
                void CheckGlyph(TextBlock text, double fontSize, string foreground)
                {
                    var runs = ReadGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                    Assert.NotEmpty(runs);
                    Assert.All(runs, run => { Assert.Equal(fontSize, run.GlyphRun.FontRenderingEmSize); Assert.Equal(400, run.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); Assert.Equal(foreground, run.ForegroundBrush.ToString()); });
                    Assert.True(text.ActualHeight >= fontSize);
                    Assert.True(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= text.ActualWidth + 0.1, RenderedText(text));
                }
                foreach (var top in menu.Items.OfType<MenuItem>())
                {
                    var commands = top.Items.OfType<MenuItem>().Select(item => (item, item.Command, item.CommandParameter, enabled: item.Command?.CanExecute(item.CommandParameter))).ToArray();
                    top.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, true);
                    Pump(window);
                    var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(top.Template.FindName("PART_Popup", top));
                    Assert.True(popup.IsOpen);
                    var panel = Assert.IsType<Border>(top.Template.FindName("MenuPanel", top));
                    Assert.Equal(250, panel.MinWidth);
                    Assert.Equal(new Thickness(5), panel.Padding);
                    Assert.Equal(new CornerRadius(6), panel.CornerRadius);
                    Assert.Equal("#FFFFFFFF", panel.Background.ToString());
                    Assert.Equal("#FFCBD6E2", panel.BorderBrush.ToString());
                    var point = panel.PointToScreen(new Point());
                    var headerPoint = top.PointToScreen(new Point());
                    Assert.InRange(point.X - headerPoint.X, -1.01, 1.01);
                    Assert.InRange(point.Y - headerPoint.Y, 30.99, 33.01);
                    var headerBorder = Assert.IsType<Border>(top.Template.FindName("HeaderBorder", top));
                    Assert.Equal(new CornerRadius(4), headerBorder.CornerRadius);
                    Assert.Equal("#FFEDF3F7", headerBorder.Background.ToString());
                    var topCaption = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(top).Single(text => text.IsVisible && RenderedText(text) == top.Header.ToString());
                    CheckGlyph(topCaption, size, "#FF243448");
                    foreach (var item in top.Items.OfType<MenuItem>().Where(item => item.Header is string))
                    {
                        Assert.Equal(28, item.ActualHeight);
                        Assert.Equal(1, item.Opacity);
                        var header = SmokeVisualTreeQuery.FindVisualDescendants<ContentPresenter>(item).Single(presenter => ReferenceEquals(presenter.TemplatedParent, item) && presenter.ContentSource == "Header");
                        var caption = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(header).Single(text => text.IsVisible && RenderedText(text) == item.Header.ToString());
                        CheckGlyph(caption, size, item.IsEnabled ? "#FF243448" : "#FF718398");
                        Assert.InRange(caption.LineHeight, line - 0.01, line + 0.01);
                        if (!string.IsNullOrEmpty(item.InputGestureText))
                        {
                            var key = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(item).Single(text => text.Text == item.InputGestureText);
                            CheckGlyph(key, 11, "#FF52687C");
                            Assert.Equal(15.95, key.LineHeight);
                        }
                    }
                    foreach (var command in commands)
                    {
                        Assert.Same(command.Command, command.item.Command);
                        Assert.Same(command.CommandParameter, command.item.CommandParameter);
                        Assert.Equal(command.enabled, command.item.Command?.CanExecute(command.item.CommandParameter));
                    }
                    observations.Add(new { header = top.Header, panel.ActualWidth, panel.ActualHeight, x = point.X - headerPoint.X, y = point.Y - headerPoint.Y, captions = top.Items.OfType<MenuItem>().Where(item => item.Header is string).Select(item => new { item.Header, item.IsEnabled, item.InputGestureText, texts = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(item).Where(text => text.IsVisible).Select(text => new { text.Text, rendered = RenderedText(text), text.LineHeight, glyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(text)).Select(run => new { run.GlyphRun.FontRenderingEmSize, weight = run.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight(), font = run.GlyphRun.GlyphTypeface.FontUri.ToString(), color = run.ForegroundBrush.ToString() }).ToArray() }).ToArray() }).ToArray() });
                    top.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false);
                    Pump(window);
                    Assert.False(popup.IsOpen);
                }
                Assert.Equal(before, new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                var evidenceRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidenceRoot))
                {
                    Directory.CreateDirectory(evidenceRoot);
                    File.WriteAllText(Path.Combine(evidenceRoot, $"menu-actual-glyphs-{width}-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { method = "Actual Main/TitleBar/Menu.Popup/CanExecute/child GlyphRun; programmatic open-close, no leaf command/physical input/photo/full shutdown", monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window), observations, model.HasUnsavedChanges, model.IsRunning }));
                }
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task StartupSurfaceRendersR24TypographyAndKeepsNewCancelAboveItsBackdrop(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var model = new MainViewModel(integrationSettingsPath: Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT") ?? Path.GetTempPath(), "startup-empty-integration.json"));
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump(window);
                IEnumerable<GlyphRunDrawing> ReadGlyphs(Drawing? drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(ReadGlyphs) : [];
                Assert.True(model.IsStartupChoiceVisible);
                var overlay = Assert.IsType<Border>(window.FindName("StartupChoiceOverlay"));
                var content = Assert.IsType<Border>(window.FindName("StartupContent"));
                Assert.Equal("#FFF3F5F8", overlay.Background.ToString());
                Assert.Equal(0, Grid.GetRow(overlay));
                Assert.Equal(4, Grid.GetRowSpan(overlay));
                Assert.Equal(50, Panel.GetZIndex(overlay));
                Assert.InRange(content.ActualWidth, Math.Min(1040, width * 0.92) - 0.01, Math.Min(1040, width * 0.92) + 0.01);
                Assert.InRange(content.Margin.Top, height * 0.07 - 0.01, height * 0.07 + 0.01);
                Assert.Null(content.Background);
                var title = Assert.IsType<TextBlock>(window.FindName("StartupTitleText"));
                var hint = Assert.IsType<TextBlock>(window.FindName("StartupHintText"));
                double bodySize = width <= 1400 ? 12.5 : 13;
                double bodyLine = width <= 1400 ? 18.125 : 18.85;
                Assert.Equal(language == OpenVisionLanguage.Korean ? "레시피를 열어 시작하세요" : "Open a recipe to start", title.Text);
                var cardButtons = new[] { "StartBlankLayoutButton", "StartSampleButton", "StartLargeLayoutSampleButton" }.Select(name => Assert.IsType<Button>(window.FindName(name))).ToArray();
                double actionY = cardButtons[0].TranslatePoint(new Point(), window).Y;
                Assert.All(cardButtons, button => Assert.InRange(button.TranslatePoint(new Point(), window).Y, actionY - 0.01, actionY + 0.01));
                Assert.Equal(31.9, title.LineHeight);
                Assert.Equal(bodyLine, hint.LineHeight);
                foreach (var (text, size, weight) in new[] { (title, 22.0, 700), (hint, bodySize, 400) })
                {
                    var runs = ReadGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                    Assert.NotEmpty(runs);
                    Assert.All(runs, run =>
                    {
                        Assert.Equal(size, run.GlyphRun.FontRenderingEmSize);
                        Assert.Equal(weight, run.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
                    });
                }
                var cards = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(window.FindName("StartupCardsPanel"));
                Assert.Equal(3, cards.Columns);
                Assert.All(cards.Children.Cast<Border>(), card =>
                {
                    Assert.Equal("#FFFFFFFF", card.Background.ToString());
                    Assert.Equal("#FFDCE3EB", card.BorderBrush.ToString());
                    Assert.Equal(new Thickness(1), card.BorderThickness);
                });
                foreach (var name in new[] { "StartBlankLayoutButton", "StartSampleButton", "StartLargeLayoutSampleButton", "OpenRecipeButton" })
                {
                    var button = Assert.IsType<Button>(window.FindName(name));
                    Assert.InRange(button.ActualHeight, bodyLine + 10 - 0.01, bodyLine + 10 + 0.01);
                    var caption = Assert.Single(SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(button));
                    var runs = ReadGlyphs(VisualTreeHelper.GetDrawing(caption)).ToArray();
                    Assert.NotEmpty(runs);
                    Assert.All(runs, run =>
                    {
                        Assert.Equal(bodySize, run.GlyphRun.FontRenderingEmSize);
                        int expectedWeight = name == "StartBlankLayoutButton" ? 600 : 400;
                        if (name == "StartBlankLayoutButton" && run.GlyphRun.Characters?.Any(character => character is >= '\uAC00' and <= '\uD7A3') == true)
                        {
                            var fallback = new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                            Assert.True(fallback.TryGetGlyphTypeface(out var expectedTypeface));
                            Assert.Equal(expectedTypeface.FontUri, run.GlyphRun.GlyphTypeface.FontUri);
                            expectedWeight = expectedTypeface.Weight.ToOpenTypeWeight();
                        }
                        Assert.Equal(expectedWeight, run.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
                    });
                    Assert.Equal(button.Command!.CanExecute(button.CommandParameter), button.IsEnabled);
                }
                var newButton = Assert.IsType<Button>(window.FindName("StartBlankLayoutButton"));
                Assert.Equal(600, newButton.FontWeight.ToOpenTypeWeight());
                Assert.True(newButton.Focus());
                Pump(window);
                Assert.True(newButton.IsKeyboardFocused);
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(newButton.Template.FindName("StartupButtonFocusCue", newButton)).Visibility);
                newButton.Command!.Execute(newButton.CommandParameter);
                Pump(window);
                var nameView = Assert.Single(SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Project.NewProjectNameDialogView>(window));
                Assert.True(nameView.IsVisible);
                Assert.True(Panel.GetZIndex(nameView) > Panel.GetZIndex(overlay));
                Assert.Same(model, nameView.DataContext);
                model.CancelNewProjectNameCommand.Execute(null);
                Pump(window);
                Assert.False(nameView.IsVisible);
                Assert.True(overlay.IsVisible);
                Assert.False(model.HasUnsavedChanges);
                Assert.False(model.IsRunning);
                model.Navigation.HideStartupChoice();
                Pump(window);
                Assert.False(overlay.IsVisible);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 900)]
    public async Task TitleHeaderRemovesEmptyIconPaddingAndRefreshesRealIdentityForSameNames(int width, int height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var model = new MainViewModel(integrationSettingsPath: Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT") ?? Path.GetTempPath(), "titlebar-empty-integration.json"));
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                const string name = "R24 비교 레시피";
                var view = SmokeVisualTreeQuery.FindVisualDescendants<StudioTitleBar>(window).Single();
                var titlebar = SmokeVisualTreeQuery.FindVisualDescendants<global::Wpf.Ui.Controls.TitleBar>(view).Single();
                var title = Assert.IsType<TextBlock>(view.FindName("RecipeTitleText"));
                string? priorIdentity = null;
                int identityNotifications = 0;
                System.ComponentModel.PropertyChangedEventHandler handler = (_, e) => { if (e.PropertyName == nameof(MainViewModel.RecipeIdentityText)) identityNotifications++; };
                model.PropertyChanged += handler;
                try
                {
                    for (int index = 0; index < 2; index++)
                    {
                        model.NewProjectCommand.Execute(null);
                        Pump(window);
                        var dialog = SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Project.NewProjectNameDialogView>(window).Single();
                        var input = Assert.IsType<TextBox>(dialog.FindName("ProjectNameTextBox"));
                        input.SetCurrentValue(TextBox.TextProperty, name);
                        input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                        Assert.True(await model.ConfirmNewProjectNameAsync());
                        Pump(window);
                        var project = Assert.IsType<OpenVisionLab.Machine.Core.Projects.MachineProjectDocument>(typeof(MainViewModel).GetProperty("CurrentProject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model));
                        string expectedIdentity = $"{name} ({project.Id})";
                        Assert.Equal(expectedIdentity, title.ToolTip);
                        Assert.Equal(expectedIdentity, model.RecipeIdentityText);
                        if (priorIdentity is not null) Assert.NotEqual(priorIdentity, expectedIdentity);
                        priorIdentity = expectedIdentity;
                        Assert.False(model.IsRunning || model.HasSavedProjectFile);
                    }
                    Assert.True(identityNotifications >= 2);
                }
                finally { model.PropertyChanged -= handler; }
                Assert.Equal(new Thickness(10, 0, 0, 0), titlebar.Padding);
                Assert.Equal(39, titlebar.ActualHeight);
                Assert.Equal(40, view.ActualHeight);
                var brand = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(view).Single(text => text.IsVisible && text.Text == "OVL");
                var brandBorder = Assert.IsType<Border>(VisualTreeHelper.GetParent(brand));
                Assert.Equal(10, brandBorder.TransformToAncestor(window).Transform(new Point()).X, 3);
                double expectedTitleX = width == 1920 ? 160.359375 : 156.703125;
                Assert.InRange(title.TransformToAncestor(window).Transform(new Point()).X, expectedTitleX - 0.1, expectedTitleX + 0.1);
                var chip = Assert.IsType<Border>(view.FindName("TitleSaveStateChip"));
                Assert.Equal(22, chip.ActualHeight);
                var chipBounds = chip.TransformToAncestor(view).TransformBounds(new Rect(chip.RenderSize));
                Assert.InRange(chipBounds.Top, 0, view.ActualHeight - chip.ActualHeight);
                Assert.True(titlebar.ShowMinimize && titlebar.ShowMaximize && titlebar.ShowClose);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 900)]
    public async Task R24RunToolbarUsesMeasuredLineHeightsAndKeepsItsPopupCommandContracts(int width, int height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var model = new MainViewModel(integrationSettingsPath: Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT") ?? Path.GetTempPath(), "run-toolbar-empty-integration.json"));
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            Popup? popup = null;
            try
            {
                model.NewProjectCommand.Execute(null);
                Pump(window);
                model.NewProjectNameDraft = "R24 운전 막대 비교";
                Assert.True(await model.ConfirmNewProjectNameAsync());
                Pump(window);
                var bar = Assert.IsType<GlobalCommandBarView>(window.FindName("CommonCommandBar"));
                var start = Assert.IsType<Button>(bar.FindName("RunStartButton"));
                var caption = Assert.IsType<TextBlock>(bar.FindName("RunStartCaption"));
                double measuredButtonHeight = width == 1920 ? 28.85 : 28.125;
                double measuredLineHeight = width == 1920 ? 18.85 : 18.125;
                Assert.Equal(measuredButtonHeight, start.ActualHeight, 3);
                Assert.Equal(measuredLineHeight, caption.LineHeight, 3);
                Assert.Equal(measuredLineHeight, caption.ActualHeight, 3);
                Assert.Same(model.RunCommand, start.Command);
                Assert.Same(model.AbortSequenceCommand, Assert.IsType<Button>(bar.FindName("RunStopButton")).Command);
                foreach (string name in new[] { "CommonRunStateText", "CommonRunSummaryText" })
                {
                    var text = Assert.IsType<TextBlock>(bar.FindName(name));
                    Assert.Equal(16.675, text.LineHeight, 3);
                    Assert.Equal(16.675, text.ActualHeight, 3);
                    Assert.Equal(new Thickness(), text.Margin);
                }
                Assert.False(model.IsRunning || model.HasUnsavedChanges || model.HasSavedProjectFile);
                var toggle = Assert.IsType<ToggleButton>(bar.FindName("RunOptionsButton"));
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Pump(window);
                popup = Assert.IsType<Popup>(bar.FindName("RunOptionsPopup"));
                Assert.True(popup.IsOpen);
                var body = Assert.IsType<Border>(popup.Child);
                Assert.Equal(310, body.ActualWidth);
                IEnumerable<GlyphRunDrawing> ReadGlyphs(Drawing? drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(ReadGlyphs) : [];
                var options = SmokeVisualTreeQuery.FindVisualDescendants<ContentControl>(body).Where(control => control is Button or RadioButton).ToArray();
                Assert.Equal(5, options.Length);
                foreach (var option in options)
                {
                    var text = Assert.Single(SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(option).Where(block => block.IsVisible));
                    var glyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.Equal(width == 1920 ? 13 : 12.5, text.FontSize);
                    Assert.Equal(measuredLineHeight, text.LineHeight, 3);
                    Assert.All(glyphs, glyph => Assert.Equal(width == 1920 ? 13 : 12.5, glyph.GlyphRun.FontRenderingEmSize));
                }
                Assert.Same(model.RetrySequenceCommand, SmokeVisualTreeQuery.FindVisualDescendants<Button>(body).Single(b => ReferenceEquals(b.Command, model.RetrySequenceCommand)).Command);
                Assert.Same(model.StepCommand, SmokeVisualTreeQuery.FindVisualDescendants<Button>(body).Single(b => ReferenceEquals(b.Command, model.StepCommand)).Command);
                popup.SetCurrentValue(Popup.IsOpenProperty, false);
                Pump(window);
                Assert.False(popup.IsOpen);
                Assert.False(toggle.IsChecked);
                Assert.False(model.IsRunning || model.HasUnsavedChanges || model.HasSavedProjectFile);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { if (popup is not null) popup.SetCurrentValue(Popup.IsOpenProperty, false); window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 900)]
    public async Task R24SpeedChoiceRendersRealValuesAndKeepsExistingCommandAdmission(int width, int height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var model = new MainViewModel(integrationSettingsPath: Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT") ?? Path.GetTempPath(), "speed-choice-empty-integration.json"));
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            MenuItem? menu = null;
            try
            {
                var bar = Assert.IsType<GlobalCommandBarView>(window.FindName("CommonCommandBar"));
                menu = Assert.IsType<MenuItem>(bar.FindName("SimulationSpeedMenu"));
                var choices = menu.Items.Cast<MenuItem>().ToArray();
                Assert.Equal(new[] { "1", "2", "4", "8" }, choices.Select(choice => Assert.IsType<string>(choice.CommandParameter)));
                Assert.All(choices, choice => Assert.Same(model.SetSimulationSpeedCommand, choice.Command));
                Assert.All(choices, choice => Assert.False(choice.Command!.CanExecute(choice.CommandParameter)));
                Assert.Equal("×1", model.SimulationSpeedText);
                model.NewProjectCommand.Execute(null);
                Pump(window);
                model.NewProjectNameDraft = "R24 배속 비교";
                Assert.True(await model.ConfirmNewProjectNameAsync());
                Pump(window);
                Assert.Equal(50, menu.ActualWidth);
                var arrow = Assert.IsType<System.Windows.Shapes.Path>(bar.FindName("SimulationSpeedArrow"));
                Assert.Equal(8, arrow.ActualWidth);
                Assert.Equal(5, arrow.ActualHeight);
                menu.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, true);
                Pump(window);
                var popup = Assert.Single(SmokeVisualTreeQuery.FindVisualDescendants<Popup>(menu));
                Assert.True(popup.IsOpen);
                IEnumerable<GlyphRunDrawing> ReadGlyphs(Drawing? drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(ReadGlyphs) : [];
                foreach (var choice in choices)
                {
                    Assert.True(choice.IsEnabled && choice.Command!.CanExecute(choice.CommandParameter));
                    var text = Assert.Single(SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(choice).Where(block => block.IsVisible && block.Text.StartsWith("×")));
                    var glyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.Equal(width == 1920 ? 18.85 : 18.125, text.LineHeight, 3);
                    Assert.All(glyphs, glyph => Assert.Equal(width == 1920 ? 13 : 12.5, glyph.GlyphRun.FontRenderingEmSize));
                }
                menu.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false);
                Pump(window);
                var caption = Assert.IsType<TextBlock>(bar.FindName("SimulationSpeedCaption"));
                foreach (var factor in new[] { "2", "4", "8", "1" })
                {
                    var choice = choices.Single(item => Equals(item.CommandParameter, factor));
                    choice.Command!.Execute(choice.CommandParameter);
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while ((!Equals(model.SimulationSpeedText, "×" + factor) || !choice.Command.CanExecute(choice.CommandParameter)) && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(10);
                        Pump(window);
                    }
                    Pump(window);
                    Assert.Equal("×" + factor, model.SimulationSpeedText);
                    Assert.Equal(model.SimulationSpeedText, caption.Text);
                    Assert.False(model.IsRunning || model.HasUnsavedChanges || model.HasSavedProjectFile);
                }
                Assert.False(menu.IsSubmenuOpen);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { menu?.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false); window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task RecipeConnectionsMenuRetainsDraftAndOpensSequenceWithoutHidingDestination(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            const string longName = "2호기 검사 투입 스테이션 — 제품 위치 확인 및 카메라 높이 보정용 이송 장치 / Line 2 inspection infeed and camera-height adjustment assembly";
            project.Layouts[0].Components[0].Name = longName;
            project.Devices[0].Name = longName;
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump(window);
                var before = store.SerializeForEvidence(project);
                var dirty = model.HasUnsavedChanges;
                var connections = model.RecipeConnections;
                var overlay = window.RecipeConnectionsOverlay;
                var menu = SmokeVisualTreeQuery.FindVisualDescendants<MenuItem>(window).Single(item => item.Name == "ViewMenuItem");
                menu.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, true);
                Pump(window);
                var item = Assert.IsType<MenuItem>(menu.Items.Cast<object>().Single(candidate => candidate is MenuItem { Name: "RecipeConnectionsMenuItem" }));
                Assert.Equal(OpenVisionLanguageService.T("Shell.RecipeConnectionsMenu"), item.Header);
                Assert.True(item.Command!.CanExecute(item.CommandParameter));
                item.Command.Execute(item.CommandParameter);
                menu.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false);
                Pump(window);
                Assert.True(overlay.IsVisible);
                Assert.Same(model, overlay.DataContext);
                Assert.Same(connections, model.RecipeConnections);
                Assert.False(model.Navigation.OpenTestScenarioCommand.CanExecute(null));
                Assert.True(new Rect(window.RenderSize).Contains(overlay.ConnectionsDialogBorder.TransformToAncestor(window).TransformBounds(new Rect(overlay.ConnectionsDialogBorder.RenderSize))));
                Assert.True(overlay.CloseRecipeConnectionsButton.IsVisible && overlay.CloseRecipeConnectionsButton.IsEnabled);
                Assert.True(overlay.ConnectionRowsListBox.ActualHeight > 100);
                var longCaption = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(overlay).Single(text => text.IsVisible && text.Text == longName);
                Assert.Equal(TextWrapping.Wrap, longCaption.TextWrapping);
                var longRow = model.RecipeConnections.Rows.Single(row => row.Name == longName);
                foreach (string required in new[] { longRow.ComponentId, longRow.KindText, longRow.StatusText })
                {
                    var caption = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(overlay).First(text => ReferenceEquals(text.DataContext, longRow) && text.Text == required);
                    Assert.True(caption.IsVisible && caption.ActualWidth > 0);
                    Assert.True(new Rect(overlay.ConnectionsDialogBorder.RenderSize).Contains(caption.TransformToAncestor(overlay.ConnectionsDialogBorder).TransformBounds(new Rect(caption.RenderSize))));
                }
                void Invoke(Button button)
                {
                    Assert.True(button.IsEnabled && button.Command!.CanExecute(button.CommandParameter));
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                    Pump(window);
                }
                var previewButton = SmokeVisualTreeQuery.FindVisualDescendants<Button>(overlay).Single(button => button.Name == "PreviewProcessBlockComposerButton");
                Invoke(previewButton);
                Assert.True(connections.ProcessBlocks.IsProcessBlockPreviewVisible);
                connections.ProcessBlocks.IsLoadBlockSelected = true;
                var selected = connections.ProcessBlocks.IsLoadBlockSelected;
                Invoke(overlay.CloseRecipeConnectionsButton);
                Assert.False(overlay.IsVisible);
                model.Navigation.OpenRecipeConnectionsCommand.Execute(null);
                Pump(window);
                Assert.Same(overlay, window.RecipeConnectionsOverlay);
                Assert.True(connections.ProcessBlocks.IsProcessBlockPreviewVisible);
                Assert.Equal(selected, connections.ProcessBlocks.IsLoadBlockSelected);
                Assert.Equal(before, store.SerializeForEvidence(project));
                Assert.Equal(dirty, model.HasUnsavedChanges);
                Assert.False(model.IsRunning || connections.DryRun.IsRecipeDryRunRunning);
                var row = connections.Rows.First(candidate => candidate.HasSequenceUse && connections.OpenSequenceStepCommand.CanExecute(candidate));
                overlay.ConnectionRowsListBox.ScrollIntoView(row);
                Pump(window);
                var openStep = SmokeVisualTreeQuery.FindVisualDescendants<Button>(overlay).Single(button => button.Name == "OpenConnectionSequenceStepButton" && ReferenceEquals(button.CommandParameter, row));
                Invoke(openStep);
                Assert.False(overlay.IsVisible);
                Assert.True(model.Navigation.IsSequenceWorkspace);
                Assert.Equal(row.FirstSequenceStepId, model.SequenceEditor.SelectedStep?.Id);
                Assert.Equal(before, store.SerializeForEvidence(project));
                Assert.False(model.IsRunning);
                model.Navigation.OpenRecipeConnectionsCommand.Execute(null);
                Pump(window);
                var unchanged = store.SerializeForEvidence(project);
                model.SequenceEditor.SelectedStepDraft!.Name = "Unapplied quick-add cancellation";
                model.SequenceEditor.PendingStepDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Cancel;
                model.AddLayoutComponentCommand.Execute(OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.RotaryStage);
                Pump(window);
                Assert.True(overlay.IsVisible);
                Assert.Equal("Unapplied quick-add cancellation", model.SequenceEditor.SelectedStepDraft.Name);
                Assert.Equal(unchanged, store.SerializeForEvidence(project));
                model.SequenceEditor.PendingStepDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Discard;
                model.AddLayoutComponentCommand.Execute(null);
                model.AddLayoutComponentCommand.Execute("invalid");
                model.AddLayoutComponentCommand.Execute((OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind)int.MaxValue);
                Pump(window);
                Assert.True(overlay.IsVisible);
                Assert.Equal(unchanged, store.SerializeForEvidence(project));
                var count = model.Layout.Definition!.Components.Count;
                var addButton = SmokeVisualTreeQuery.FindVisualDescendants<Button>(overlay).Single(button => button.Name == "AddConnectionRotaryStageButton");
                Invoke(addButton);
                Assert.False(overlay.IsVisible);
                Assert.Equal(0, model.Navigation.SelectedDocumentTabIndex);
                Assert.Equal(count + 1, model.Layout.Definition!.Components.Count);
                Assert.Equal(OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.RotaryStage, model.Layout.SelectedItem!.Component!.Kind);
                model.Navigation.SelectedDocumentTabIndex = 2;
                model.AddLayoutComponentCommand.Execute(OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.RotaryStage);
                Assert.Equal(2, model.Navigation.SelectedDocumentTabIndex);
                Assert.False(overlay.IsVisible);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return true;
        });
    }

    [Fact]
    public async Task ConnectionProcessPreviewStepReturnKeepsRenderedDraftWithoutApplying()
    {
        await _ui.InvokeAsync(() =>
        {
            var store = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", "10-MetrologySorter.ovmachine")));
            new RecipeConnectionProjectApplier().ApplyProcessBlocks(project, Enum.GetValues<SemiconductorProcessBlockKind>());
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900);
            window.Show();
            try
            {
                Pump(window);
                var before = store.SerializeForEvidence(project);
                var dirty = model.HasUnsavedChanges;
                model.Navigation.OpenRecipeConnectionsCommand.Execute(null);
                Pump(window);
                void Invoke(Button button)
                {
                    button.BringIntoView();
                    Pump(window);
                    Assert.True(button.IsVisible && button.IsEnabled);
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                    Pump(window);
                }
                var overlay = window.RecipeConnectionsOverlay;
                Invoke(SmokeVisualTreeQuery.FindVisualDescendants<Button>(overlay).Single(button => button.Name == "PreviewProcessBlockComposerButton"));
                var blocks = model.RecipeConnections.ProcessBlocks;
                blocks.IsLoadBlockSelected = true;
                var draft = blocks.IsLoadBlockSelected;
                var stepButton = SmokeVisualTreeQuery.FindVisualDescendants<Button>(overlay).First(button => button.Name == "OpenProcessBlockSequenceStepButton" && button.IsVisible && button.IsEnabled);
                Invoke(stepButton);
                Assert.False(overlay.IsVisible);
                Assert.Equal(2, model.Navigation.SelectedDocumentTabIndex);
                var returnButton = SmokeVisualTreeQuery.FindVisualDescendants<Button>(window).Single(button => button.Name == "ReturnToProcessPlanButton");
                Assert.Equal(OpenVisionLanguageService.T("Connections.ReturnToProcessPlan"), System.Windows.Automation.AutomationProperties.GetName(returnButton));
                Invoke(returnButton);
                Assert.True(overlay.IsVisible);
                Assert.True(blocks.IsProcessBlockPreviewVisible);
                Assert.Equal(draft, blocks.IsLoadBlockSelected);
                Assert.Equal(before, store.SerializeForEvidence(project));
                Assert.Equal(dirty, model.HasUnsavedChanges);
                Assert.False(model.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); }
            return true;
        });
    }

    [Fact]
    public async Task ConnectionSmokeCallersUseOverlayForReadinessDryRunAndEmptyCamera()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT")!, "connection-smoke-callers");
            Directory.CreateDirectory(root);
            var project = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900);
            window.Show();
            try
            {
                Pump(window);
                var report = await SmokeConnectionWorkbenchVerifier.VerifyAsync(window, model, project, Path.Combine(root, "roundtrip.ovmachine"),
                    (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendants<TextBox>(view).FirstOrDefault(predicate));
                report.Save(Path.Combine(root, "connection-report.json"));
                Assert.True(report.IsValid, string.Join(", ", report.Failures));
                var interaction = new SmokeUiInteraction
                {
                    FindTextBlock = (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(view).FirstOrDefault(predicate),
                    FindButton = (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendants<Button>(view).FirstOrDefault(predicate),
                    ActivateWindow = () => { }, MovePointerToCenter = _ => throw new InvalidOperationException("Pointer path outside focused smoke scope"),
                    MouseEvent = (_, _, _, _, _) => throw new InvalidOperationException("Pointer path outside focused smoke scope"),
                    SetCursorPosition = (_, _) => { }, GetCursorPosition = () => (0, 0), SetPopupContent = _ => { },
                    MarkSmokePointerHeld = () => { }, ReleaseSmokePointer = () => { }, CheckPointerOwnership = _ => (true, "No native pointer input in readiness path")
                };
                await DirectExeConnectionWorkbenchWorkflow.ApplyAsync(window, model, project, null, "readiness", null, null, interaction);
                Assert.True(window.RecipeConnectionsOverlay.IsVisible);
                Assert.False(model.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); }
            using var empty = new MainViewModel(new OpenVisionLab.Machine.Core.Projects.MachineProjectDocument { Name = "Empty camera smoke" });
            var emptyWindow = new ShellWindow { DataContext = empty, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(emptyWindow, 1280, 900);
            emptyWindow.Show();
            try
            {
                Pump(emptyWindow);
                var report = await SmokeCameraFirstUseVerifier.VerifyAsync(emptyWindow, empty, "normal", null,
                    view => SmokeVisualTreeQuery.FindVisualDescendants<OpenVisionLab.MachineStudio.View.Project.RecipeConnectionWorkbenchView>(view).FirstOrDefault(),
                    (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendants<Button>(view).FirstOrDefault(predicate),
                    (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendants<Border>(view).FirstOrDefault(predicate),
                    () => { }, _ => throw new InvalidOperationException("No native pointer path"), (_, _, _, _, _) => throw new InvalidOperationException("No native pointer path"),
                    () => { }, _ => { }, () => { });
                report.Save(Path.Combine(root, "camera-empty-report.json"));
                Assert.True(report.IsValid, string.Join(", ", report.Failures));
                Assert.True(emptyWindow.RecipeConnectionsOverlay.IsVisible);
            }
            finally { emptyWindow.DataContext = null; emptyWindow.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task ManualCommissioningMenuUsesExistingAxisOwnerAndDoesNotStartRun(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                var before = store.SerializeForEvidence(project);
                var axis = project.Layouts.SelectMany(layout => layout.Components).First(component => component.BehaviorBindingId == project.Axes[0].Id);
                model.Layout.Select(axis.Id);
                model.Navigation.OpenSequenceEditorCommand.Execute(null);
                Pump(window);
                var menu = SmokeVisualTreeQuery.FindVisualDescendants<MenuItem>(window).Single(item => item.Name == "ViewMenuItem");
                var item = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ManualCommissioningMenuItem");
                Assert.Equal(OpenVisionLanguageService.T("Shell.ManualRunTarget"), item.Header);
                Assert.False(item.IsEnabled);
                Assert.False(item.Command!.CanExecute(item.CommandParameter));
                var toggle = SmokeVisualTreeQuery.FindVisualDescendants<ToggleButton>(window).Single(control => control.Name == "RunOptionsButton");
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                Pump(window);
                var options = SmokeVisualTreeQuery.FindVisualDescendants<Popup>(window).Single(control => control.Name == "RunOptionsPopup");
                var radio = SmokeVisualTreeQuery.FindVisualDescendants<RadioButton>(options.Child).Single(control =>
                    BindingOperations.GetBindingExpression(control, ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "IsRunMode");
                ((System.Windows.Automation.Provider.ISelectionItemProvider)new System.Windows.Automation.Peers.RadioButtonAutomationPeer(radio)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
                toggle.IsChecked = false;
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (!model.IsRunMode || model.IsModeTransitioning)
                {
                    Assert.True(DateTime.UtcNow < deadline, "Run mode transition did not complete.");
                    await Task.Delay(10);
                }
                menu.IsSubmenuOpen = true;
                Pump(window);
                Assert.True(item.IsEnabled && item.Command!.CanExecute(item.CommandParameter));
                var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                menu.IsSubmenuOpen = false;
                Pump(window);
                var right = Assert.IsType<OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>(window.FindName("RightToolRegionView"));
                Assert.True(model.Navigation.IsSimulationWorkspace && model.Navigation.IsInspectorOpen);
                Assert.Equal(0, model.Navigation.SelectedExecutionTabIndex);
                Assert.True(right.AxisCommissioningPanel.IsVisible);
                Assert.Same(model.AxisCommissioning, right.AxisCommissioningPanel.DataContext);
                Assert.Equal(axis.Id, model.Layout.SelectedItem?.Id);
                Assert.True(model.IsRunMode);
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                Assert.Equal(before, store.SerializeForEvidence(project));
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowContainedByMonitor);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
