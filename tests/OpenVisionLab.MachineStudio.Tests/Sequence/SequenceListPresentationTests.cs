using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SequenceListPresentationTests
{
    private readonly StudioUiTestHost _ui;
    public SequenceListPresentationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(705, OpenVisionLanguage.Korean)]
    [InlineData(1325, OpenVisionLanguage.Korean)]
    [InlineData(705, OpenVisionLanguage.English)]
    [InlineData(1325, OpenVisionLanguage.English)]
    public async Task DockedListUsesSourceHeaderColumnsAndRealSelectionWithoutDraftApply(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var main = new MainViewModel(project);
            var vm = main.SequenceEditor;
            main.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width < 1000 ? 1280 : 1920, width < 1000 ? 900 : 1032);
            window.Show();
            main.SequenceEditor.ProcessViewIndex = 1; main.SequenceEditor.IsProcessSceneVisible = true;
            main.Navigation.OpenSequenceEditorCommand.Execute(null);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            string original = JsonSerializer.Serialize(project);
            try
            {
                Arrange(view, width, 250);
                Assert.True(view.DockedStepListHeader.IsVisible);
                Assert.Null(view.InlineStepEditor.DataContext);
                var sceneGrid = Assert.IsType<Grid>(view.Parent);
                Assert.Equal(200, sceneGrid.RowDefinitions[1].MinHeight);
                Assert.Equal(150, sceneGrid.RowDefinitions[3].MinHeight);
                var previousStepHeight = sceneGrid.RowDefinitions[3].Height;
                sceneGrid.RowDefinitions[3].Height = new GridLength(75);
                window.UpdateLayout();
                Assert.True(sceneGrid.RowDefinitions[3].ActualHeight >= 150);
                Assert.True(sceneGrid.RowDefinitions[1].ActualHeight >= 200);
                sceneGrid.RowDefinitions[3].Height = previousStepHeight;
                window.UpdateLayout();
                Assert.Equal("Unit", view.DockedStepUnitName.Text);
                Assert.Equal(width < 1000 ? 41.125 : 41.85, view.DockedStepListHeader.ActualHeight - view.StepSearchToolbar.ActualHeight - view.StepSearchToolbar.Margin.Top - view.ProcessViewToolbar.ActualHeight - view.ProcessViewToolbar.Margin.Top, 1);
                Assert.Empty(Descendants(view).OfType<TabItem>());
                Assert.Equal(new[] { "#", OpenVisionLanguageService.T("Sequence.Name"), OpenVisionLanguageService.T("Sequence.Action"), OpenVisionLanguageService.T("Sequence.Target"), OpenVisionLanguageService.T("R24.StepListValue"), OpenVisionLanguageService.T("Sequence.Next"), OpenVisionLanguageService.T("R24.StepListStatus") }, Descendants(view.StepListDataGrid).OfType<DataGridColumnHeader>().Where(header => header.Column is not null).OrderBy(header => header.Column.DisplayIndex).Select(header => string.Concat(Descendants(header).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text))).ToArray());
                Assert.Equal(12, view.StepListDataGrid.FontSize);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                string? evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                if (evidence is not null)
                {
                    Assert.StartsWith("D:\\", Path.GetFullPath(evidence), StringComparison.OrdinalIgnoreCase);
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, $"list-{width}-{language}-monitor.json"), JsonSerializer.Serialize(monitor));
                }
                Assert.Equal("#FFFFFFFF", view.StepListDataGrid.Background.ToString());
                Assert.All(Descendants(view.StepListDataGrid).OfType<DataGridRow>(), row => Assert.Equal(new Thickness(0), row.BorderThickness));
                Assert.All(Descendants(view.StepListDataGrid).OfType<DataGridColumnHeader>(), header => Assert.Equal(12, header.FontSize));
                Assert.All(Descendants(view.StepListDataGrid).OfType<DataGridColumnHeader>().Where(header => header.Column is not null), header =>
                {
                    var caption = Descendants(header).OfType<TextBlock>().Single(text => text.IsVisible);
                    Assert.Equal("Segoe UI, Malgun Gothic", caption.FontFamily.Source);
                    Assert.Equal(FontWeights.SemiBold, caption.FontWeight);
                    Assert.Equal("#FF243448", caption.Foreground.ToString());
                    Assert.Equal(17.4, caption.LineHeight);
                    Assert.Equal(27.9, header.ActualHeight, 1);
                });
                Assert.Same(vm.TestSelectedStepCommand, view.DockedTestStepButton.Command);
                Assert.Same(main.RecipeConnections.DryRun.ValidateSimulationReadinessCommand, view.DockedReadinessButton.Command);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(view.FindNextStepButton.IsEnabled);
                view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "DELAY");
                Arrange(view, width, 250);
                Assert.Equal("DELAY", vm.StepSearchText);
                Assert.Equal(1, vm.StepSearchMatchCount);
                Assert.True(view.FindNextStepButton.IsEnabled);
                Assert.Equal(2, view.StepListDataGrid.Items.Count);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                vm.StepSearchText = "complete";
                Arrange(view, width, 250);
                Assert.Equal("complete", view.StepSearchTextBox.Text);
                Assert.Same(vm.FindNextStepCommand, view.FindNextStepButton.Command);
                vm.FindNextStepCommand.Execute(null);
                Arrange(view, width, 250);
                Assert.Same(vm.Steps.Last(), view.StepListDataGrid.SelectedItem);
                Assert.Contains(Descendants(view.StepListDataGrid).OfType<TextBlock>(), text => text.Text == vm.Steps.Last().DisplayName && text.FontWeight == FontWeights.Bold);
                vm.StepSearchText = new string('찾', 120);
                Arrange(view, width, 250);
                Assert.False(view.FindNextStepButton.IsEnabled);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.SearchNoMatches"), view.StepSearchStatus.Text);
                Assert.Same(vm.Steps.Last(), vm.SelectedStep);
                Assert.True(view.StepSearchTextBox.ActualWidth >= 70);
                Assert.True(view.StepSearchStatus.TranslatePoint(new Point(view.StepSearchStatus.ActualWidth, 0), view).X <= width);
                vm.ClearStepSearchCommand.Execute(null);
                Arrange(view, width, 250);
                Assert.Empty(view.StepSearchTextBox.Text);
                Assert.All(vm.Steps, step => Assert.False(step.IsSearchMatch));
                Assert.Equal(original, JsonSerializer.Serialize(project));
                view.StepListDataGrid.SetCurrentValue(Selector.SelectedItemProperty, vm.Steps.Last());
                Arrange(view, width, 250);
                Assert.Same(vm.Steps.Last(), vm.SelectedStep);
                vm.SelectedStep = vm.Steps.First();
                Arrange(view, width, 250);
                Assert.Same(vm.Steps.First(), view.StepListDataGrid.SelectedItem);
                Assert.Empty(vm.Steps.First().StepListStatusText);
                vm.ApplyRuntimeStep("sequence", "delay");
                Arrange(view, width, 250);
                Assert.Equal(OpenVisionLanguageService.T("R24.StepExecuting"), vm.Steps.First().StepListStatusText);
                Assert.Contains(Descendants(view.StepListDataGrid).OfType<TextBlock>(), text => text.Text == vm.Steps.First().StepListStatusText);
                vm.ApplyRuntimeStep(null, null);
                Assert.Empty(vm.Steps.First().StepListStatusText);
                vm.Steps.First().Parameter = "NaN";
                Arrange(view, width, 250);
                Assert.NotEmpty(vm.Steps.First().StepListStatusText);
                vm.Steps.First().Parameter = "1";
                Arrange(view, width, 250);
                Assert.Empty(vm.Steps.First().StepListStatusText);
                Assert.Contains(Descendants(view.StepListDataGrid).OfType<TextBlock>(), text => text.Text == "1 s");
                Assert.Contains(Descendants(view.StepListDataGrid).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("R24.CycleEnd"));
                var originalStep = vm.Steps.First().CopyDefinition();
                vm.Steps.First().Action = SequenceStepAction.SetSignal;
                vm.Steps.First().Parameter = "false";
                Arrange(view, width, 250);
                Assert.Contains(Descendants(view.StepListDataGrid).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("R24.OutputOff"));
                Assert.Empty(vm.Steps.First().StepListStatusText);
                vm.Steps.First().Parameter = "true";
                Arrange(view, width, 250);
                Assert.Contains(Descendants(view.StepListDataGrid).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("R24.OutputOn"));
                vm.Steps.First().ApplyDraft(originalStep);
                Arrange(view, width, 250);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasStepTestStatus);
                view.IsR24Docked = false;
                Arrange(view, width, 500);
                Assert.False(view.DockedStepListHeader.IsVisible);
                Assert.Equal(2, Descendants(view).OfType<TabItem>().Count());
                Assert.Equal(42, view.StepListDataGrid.Columns[0].ActualWidth);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Step"), view.StepListDataGrid.Columns[1].Header);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Value"), view.StepListDataGrid.Columns[4].Header);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Validation"), view.StepListDataGrid.Columns[6].Header);
                view.IsR24Docked = true;
                Arrange(view, width, 250);
                Assert.True(view.DockedStepListHeader.IsVisible);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                string previousUnitName = project.Stations[0].Units[0].Name;
                project.Stations[0].Units[0].Name = new string('장', 96);
                vm.ReloadUnitRepeatDraft();
                Arrange(view, width, 250);
                window.UpdateLayout();
                Assert.Equal(TextWrapping.Wrap, view.DockedStepUnitName.TextWrapping);
                Assert.True(view.DockedStepUnitName.ActualHeight > view.DockedStepUnitName.LineHeight);
                Assert.True(view.DockedStepUnitName.ActualWidth <= view.DockedHeaderTextRegion.ActualWidth);
                var actionEnd = view.DockedTestStepButton.TranslatePoint(new Point(view.DockedTestStepButton.ActualWidth, 0), view);
                Assert.True(actionEnd.X <= width);
                project.Stations[0].Units[0].Name = previousUnitName;
                vm.ReloadUnitRepeatDraft();
                Assert.Equal(original, JsonSerializer.Serialize(project));
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateDialogCancelAndCatalogCommandsPreserveExistingAuthoringPolicy(bool readOnly)
    {
        await _ui.InvokeAsync(() =>
        {
            var vm = new SequenceEditorViewModel();
            try
            {
            var project = CreateProject();
            vm.Load(project);
            vm.IsEditable = !readOnly;
            string original = JsonSerializer.Serialize(project);
            var dialog = new SequenceStepTemplateDialog { DataContext = vm };
            var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
            Arrange(content, 898, 640);
            var choices = Descendants(content).OfType<Button>().Where(button => button.CommandParameter is not null).ToArray();
            Assert.Equal(vm.Templates.Count, choices.Length);
            Assert.All(choices, button => Assert.Same(vm.AddStepCommand, button.Command));
            Assert.All(choices, button => Assert.Equal(!readOnly, button.IsEnabled));
            dialog.Close();
            Assert.Equal(original, JsonSerializer.Serialize(project));
            Assert.False(vm.AddStepCommand.CanExecute("foreign-template"));
            vm.AddStepCommand.Execute("foreign-template");
            Assert.Equal(original, JsonSerializer.Serialize(project));
            var template = vm.Templates.Single(item => item.Id == "delay");
            vm.SelectedTemplate = null;
            Assert.Equal(!readOnly, vm.AddStepCommand.CanExecute(template));
            vm.AddStepCommand.Execute(template);
            if (readOnly) Assert.Equal(original, JsonSerializer.Serialize(project));
            else
            {
                Assert.Equal(3, project.Sequences.Single().Steps.Count);
                Assert.Equal(SequenceStepAction.Delay, project.Sequences.Single().Steps[1].Action);
                Assert.Equal(SequenceStepAction.Complete, project.Sequences.Single().Steps.Last().Action);
                Assert.Equal(template.DefaultParameter, vm.SelectedStep!.Parameter);
                Assert.Equal(OpenVisionLanguageService.T("R24.StepCount").Replace("{0}", "3"), vm.StepListCountText);
            }
            string beforeDispose = JsonSerializer.Serialize(project);
            vm.Dispose();
            Assert.False(vm.AddStepCommand.CanExecute(template));
            vm.AddStepCommand.Execute(template);
            Assert.Equal(beforeDispose, JsonSerializer.Serialize(project));
            }
            finally { vm.Dispose(); }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, OpenVisionLanguage.Korean)]
    [InlineData(1280, OpenVisionLanguage.Korean)]
    [InlineData(1920, OpenVisionLanguage.English)]
    [InlineData(1280, OpenVisionLanguage.English)]
    public async Task TemplateDialogUsesOriginalTypographyAndKeepsCatalogState(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis", Name = "Travel" });
            var vm = new SequenceEditorViewModel();
            vm.Load(project);
            string original = JsonSerializer.Serialize(project);
            var owner = new Window { Style = new Style(typeof(Window)), WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(owner, width, width == 1280 ? 900 : 1032);
            ShellWindow.SetIsCompactLayout(owner, width == 1280);
            try
            {
                owner.Show();
                foreach (bool readOnly in new[] { false, true })
                {
                    vm.IsEditable = !readOnly;
                    var dialog = new SequenceStepTemplateDialog { Owner = owner, DataContext = vm, MaxHeight = owner.ActualHeight - 40, ShowActivated = false };
                    try
                    {
                        dialog.Show();
                        dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(dialog.UpdateLayout));
                        double fontSize = width == 1280 ? 12.5 : 13;
                        double lineHeight = width == 1280 ? 18.125 : 18.85;
                        var title = Descendants(dialog).OfType<TextBlock>().Single(text => text.FontSize == 16);
                        Assert.Equal(23.2, title.LineHeight);
                        AssertGlyphs(title, 16, 700, "#FF243448");
                        var rows = Descendants(dialog).OfType<Button>().Where(button => button.CommandParameter is not null).ToArray();
                        Assert.Equal(vm.Templates.Count, rows.Length);
                        var hint = Descendants(dialog).OfType<TextBlock>().Single(text => text.Text == OpenVisionLanguageService.T("R24.AddStepHint"));
                        Assert.Equal(11.5, hint.FontSize);
                        Assert.Equal(16.675, hint.LineHeight);
                        Assert.Equal("#FF52687C", hint.Foreground.ToString());
                        Assert.All(rows, button =>
                        {
                            Assert.Same(vm.AddStepCommand, button.Command);
                            Assert.Equal(!readOnly, button.IsEnabled);
                            Assert.Equal(new Thickness(6, 4, 6, 4), button.Padding);
                            var name = Descendants(button).OfType<TextBlock>().First();
                            Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
                            Assert.Equal(TextTrimming.None, name.TextTrimming);
                            var role = Descendants(button).OfType<TextBlock>().Last();
                            Assert.Equal(TextWrapping.Wrap, role.TextWrapping);
                            Assert.False(string.IsNullOrWhiteSpace(role.Text));
                            Assert.Equal(OpenVisionLanguageService.T($"R24.StepTemplateDescription.{((OpenVisionLab.Machine.Sequence.Authoring.SequenceStepTemplateDefinition)button.CommandParameter).Id}"), role.Text);
                            Assert.Equal(lineHeight, name.LineHeight);
                            AssertGlyphs(name, fontSize, 400, readOnly ? "#FF718398" : "#FF243448");
                            if (readOnly)
                            {
                                var border = Assert.IsType<Border>(button.Template.FindName("ButtonBorder", button));
                                Assert.Equal("#FFF3F5F8", border.Background.ToString());
                                Assert.Equal("#FFE0E5ED", border.BorderBrush.ToString());
                            }
                        });
                        var move = rows.Single(button => vm.Templates.Single(template => ReferenceEquals(template, button.CommandParameter)).Id == "move-axis-home");
                        var description = Descendants(move).OfType<TextBlock>().Last();
                        Assert.Equal(lineHeight, description.LineHeight);
                        AssertGlyphs(description, 10.5, 400, "#FF52687C");
                        var close = Descendants(dialog).OfType<Button>().Single(button => button.IsCancel);
                        Assert.True(close.IsEnabled);
                        Assert.Equal("#FFEAF0F6", close.Background.ToString());
                        Assert.Equal("#FFCBD6E2", close.BorderBrush.ToString());
                        AssertGlyphs(Descendants(close).OfType<TextBlock>().Single(), fontSize, 400, "#FF243448");
                        var footer = Descendants(dialog).OfType<Border>().Single(border => border.BorderThickness == new Thickness(0, 1, 0, 0));
                        Assert.Equal(new Thickness(18, 10, 18, 10), footer.Padding);
                        Assert.True(close.TranslatePoint(new Point(0, close.ActualHeight), dialog).Y <= dialog.ActualHeight);
                        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(dialog);
                        Assert.True(monitor.WindowIntersectsMonitor);
                        string? evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                        if (evidence is not null)
                        {
                            Assert.StartsWith("D:\\", Path.GetFullPath(evidence), StringComparison.OrdinalIgnoreCase);
                            Directory.CreateDirectory(evidence);
                            File.WriteAllText(Path.Combine(evidence, $"template-{width}-{language}-{readOnly}-monitor.json"), JsonSerializer.Serialize(monitor));
                        }
                    }
                    finally { dialog.DataContext = null; dialog.Close(); }
                    Assert.Equal(original, JsonSerializer.Serialize(project));
                }
            }
            finally { owner.Close(); vm.Dispose(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData("mm")]
    [InlineData("deg")]
    [InlineData("")]
    public async Task AxisUnitsUseActualMetadataWithoutChangingParametersOrGenericBindings(string unit)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            var axis = new VirtualAxisDefinition { Id = "axis", Name = "Travel", Unit = unit };
            project.Axes.Add(axis);
            project.Sequences[0].Steps[0].Action = SequenceStepAction.MoveAxis;
            project.Sequences[0].Steps[0].TargetId = axis.Id;
            project.Sequences[0].Steps[0].Parameter = "150";
            var vm = new SequenceEditorViewModel();
            try
            {
                vm.Load(project);
                var row = vm.Steps.First();
                string original = JsonSerializer.Serialize(project);
                Assert.Equal(unit.Length == 0 ? "150" : $"150 {unit}", row.StepListParameterText);
                Assert.Equal("Travel (axis)", row.StepListTargetText);
                Assert.Equal("150", row.Parameter);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.Equal(row.StepListParameterText, vm.SelectedStepDraft!.StepListParameterText);
                axis.Unit = "rad";
                vm.RefreshLocalization();
                Assert.Equal("150 rad", row.StepListParameterText);
                Assert.Equal("150", row.Parameter);
                row.TargetId = "missing-axis";
                Assert.Equal("150", row.StepListParameterText);
                row.TargetId = axis.Id;
                Assert.Equal("150 rad", row.StepListParameterText);
                var changed = new List<string?>();
                row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
                row.Parameter = "75";
                Assert.Contains(nameof(SequenceStepEditorItem.StepListParameterText), changed);
                Assert.Equal("75 rad", row.StepListParameterText);
            }
            finally { vm.Dispose(); }
            return true;
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task SignalAndWaitLabelsPreserveStoredValuesAndDisabledTimeout(OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            var vm = new SequenceEditorViewModel();
            try
            {
                vm.Load(project);
                var row = vm.Steps.First();
                row.Action = SequenceStepAction.SetSignal;
                var changes = new List<string?>();
                row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
                foreach (var pair in new[] { ("true", true), ("1", true), ("false", false), ("0", false) })
                {
                    row.Parameter = pair.Item1;
                    string stored = JsonSerializer.Serialize(project);
                    Assert.Equal(OpenVisionLanguageService.T(pair.Item2 ? "R24.OutputOn" : "R24.OutputOff"), row.StepListActionText);
                    Assert.Empty(row.StepListParameterText);
                    Assert.Equal(pair.Item1, row.Parameter);
                    Assert.Equal(stored, JsonSerializer.Serialize(project));
                }
                Assert.Contains(nameof(SequenceStepEditorItem.StepListActionText), changes);
                row.Parameter = "unsupported";
                Assert.Equal("unsupported", row.StepListParameterText);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Action.SetSignal"), row.StepListActionText);
                row.ParameterChoice = null;
                Assert.Equal("unsupported", row.Parameter);
                row.ParameterChoice = "unsupported";
                Assert.Equal("unsupported", row.Parameter);
                row.ParameterChoice = "true";
                Assert.Equal("true", row.Parameter);
                project.Sequences[0].Steps[0].Parameter = null!;
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Action.SetSignal"), row.StepListActionText);
                _ = row.StepListParameterText;
                row.Action = SequenceStepAction.WaitSignal;
                row.Parameter = "false";
                row.TimeoutMs = 1001;
                Assert.Equal(language == OpenVisionLanguage.Korean ? "OFF · 타임아웃 1.001 s" : "OFF · Timeout 1.001 s", row.StepListParameterText);
                changes.Clear();
                row.TimeoutMs = 0;
                Assert.Contains(nameof(SequenceStepEditorItem.StepListParameterText), changes);
                Assert.Equal($"OFF · {OpenVisionLanguageService.T("R24.NoTimeout")}", row.StepListParameterText);
                row.Action = SequenceStepAction.WaitAxisDone;
                row.Parameter = "axisDone";
                Assert.Equal(OpenVisionLanguageService.T("R24.NoTimeout"), row.StepListParameterText);
                row.Parameter = "unsupported";
                Assert.Equal("unsupported", row.StepListParameterText);
                Assert.NotEmpty(row.StepListStatusText);
                project.Sequences[0].Steps.Last().Action = SequenceStepAction.None;
                vm.Load(project);
                Assert.Equal(OpenVisionLanguageService.T("R24.CycleEnd"), vm.Steps.Last().StepListNextText);
            }
            finally { vm.Dispose(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(705)]
    [InlineData(1325)]
    public async Task DockedContentColumnsResizeFromRealValuesAndRestoreLegacyWidths(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width < 1000 ? 1280 : 1920, width < 1000 ? 900 : 1032);
            window.Show();
            main.SequenceEditor.ProcessViewIndex = 1; main.SequenceEditor.IsProcessSceneVisible = true;
            main.Navigation.OpenSequenceEditorCommand.Execute(null);
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            var grid = view.StepListDataGrid;
            var vm = main.SequenceEditor;
            var stored = JsonSerializer.Serialize(project);
            var step = vm.Steps.First();
            var originalStep = step.CopyDefinition();
            var evidence = new List<object>();
            try
            {
                Arrange(view, width, 250);
                CheckColumns("short");
                var shortWidths = grid.Columns.Select(column => column.ActualWidth).ToArray();
                step.Parameter = "100000000";
                Arrange(view, width, 250);
                CheckColumns("long-number");
                Assert.True(grid.Columns[4].ActualWidth > shortWidths[4] + 15);
                step.Parameter = "-100000000";
                Arrange(view, width, 250);
                Assert.Equal("-100000000", project.Sequences[0].Steps[0].Parameter);
                Assert.NotEmpty(step.StepListStatusText);
                Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.IsVisible && text.Text == "-100000000 s");
                Assert.DoesNotContain('\u200B', step.StepListParameterText);
                evidence.Add(new { State = "signed-invalid-value", step.StepListParameterText, step.StepListStatusText, Columns = grid.Columns.Select(column => column.ActualWidth).ToArray() });
                step.Parameter = "100000000";
                step.Name = new string('W', 120);
                Arrange(view, width, 250);
                var scroll = Descendants(grid).OfType<ScrollViewer>().First();
                Assert.True(grid.Columns[1].MinWidth > scroll.ViewportWidth);
                Assert.True(scroll.ExtentWidth > scroll.ViewportWidth);
                Assert.Equal(Visibility.Visible, scroll.ComputedHorizontalScrollBarVisibility);
                evidence.Add(new { State = "unbreakable-name", scroll.ExtentWidth, scroll.ViewportWidth, Columns = grid.Columns.Select(column => column.ActualWidth).ToArray() });
                step.Name = "ITEM-" + new string('1', 80);
                Arrange(view, width, 250);
                var displayedName = Descendants(grid).OfType<TextBlock>().Single(text => text.IsVisible && AutomationProperties.GetName(text) == step.DisplayName);
                Assert.Equal(step.DisplayName, displayedName.Text.Replace("\u200B", ""));
                Assert.Contains('\u200B', displayedName.Text);
                Assert.Equal(step.Name, project.Sequences[0].Steps[0].Name);
                if (width < 1000) Assert.True(displayedName.ActualHeight > displayedName.LineHeight);
                else Assert.InRange(displayedName.ActualHeight, 0, displayedName.LineHeight + 0.1);
                evidence.Add(new { State = "numeric-hyphen-name", displayedName.Text, displayedName.ActualHeight, displayedName.LineHeight, Columns = grid.Columns.Select(column => column.ActualWidth).ToArray() });
                step.ApplyDraft(originalStep);
                Arrange(view, width, 250);
                CheckColumns("recovered");
                Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.IsVisible && text.Text == step.DisplayName && AutomationProperties.GetName(text) == step.DisplayName);
                Assert.Equal(stored, JsonSerializer.Serialize(project));
                Assert.All(grid.Columns.Select((column, index) => (column, index)), pair => Assert.Equal(shortWidths[pair.index], pair.column.ActualWidth, 1));
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
                Arrange(view, width, 250);
                CheckColumns("english");
                Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.IsVisible && text.Text == OpenVisionLanguageService.T("R24.StepListValue"));
                view.IsR24Docked = false;
                Arrange(view, width, 500);
                Assert.Equal(DataGridLengthUnitType.Star, grid.Columns[1].Width.UnitType);
                Assert.Equal(2, grid.Columns[1].Width.Value);
                Assert.Equal(120, grid.Columns[1].MinWidth);
                Assert.Equal(90, grid.Columns[2].MinWidth);
                Assert.Equal(64, grid.Columns[4].MinWidth);
                Assert.True(grid.CanUserSortColumns);
                Assert.True(grid.CanUserResizeColumns);
                step.Name = "ITEM-001";
                Arrange(view, width, 500);
                Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.IsVisible && text.Text == "ITEM-001");
                step.ApplyDraft(originalStep);
                Arrange(view, width, 500);
                view.IsR24Docked = true;
                Arrange(view, width, 250);
                CheckColumns("redocked");
                var savedSteps = project.Sequences[0].Steps.ToArray();
                project.Sequences[0].Steps.Clear();
                vm.Load(project); vm.ProcessViewIndex = 1;
                Arrange(view, width, 250);
                CheckColumns("empty");
                Assert.Empty(vm.Steps);
                Assert.True(view.DockedEmptySteps.IsVisible);
                var emptyCaption = Descendants(view.DockedEmptySteps).OfType<TextBlock>().Single();
                Assert.Equal(OpenVisionLanguageService.T("R24.EmptyUnitSteps"), emptyCaption.Text);
                Assert.Equal(12, emptyCaption.FontSize);
                Assert.Equal(17.4, emptyCaption.LineHeight);
                project.Sequences[0].Steps.AddRange(savedSteps);
                vm.Load(project); vm.ProcessViewIndex = 1;
                Arrange(view, width, 250);
                CheckColumns("reloaded");
                Assert.False(view.DockedEmptySteps.IsVisible);
                Assert.Equal(stored, JsonSerializer.Serialize(project));
                Assert.False(main.IsRunning);
                Assert.False(main.HasUnsavedChanges);
                string? evidencePath = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                if (evidencePath is not null) File.WriteAllText(Path.Combine(evidencePath, $"content-columns-{width}.json"), JsonSerializer.Serialize(evidence));

                void CheckColumns(string state)
                {
                    Assert.False(grid.CanUserSortColumns);
                    Assert.False(grid.CanUserResizeColumns);
                    var scroll = Descendants(grid).OfType<ScrollViewer>().First();
                    if (state == "empty") Assert.True(grid.Columns[0].ActualWidth > 30);
                    else Assert.Equal(30, grid.Columns[0].ActualWidth, 1);
                    Assert.Equal(170, grid.Columns[6].ActualWidth, 1);
                    Assert.InRange(Math.Abs(grid.Columns.Sum(column => column.ActualWidth) - scroll.ViewportWidth), 0, 1);
                    Assert.All(grid.Columns, column => Assert.True(double.IsFinite(column.ActualWidth) && column.ActualWidth > 0));
                    evidence.Add(new { State = state, scroll.ViewportWidth, Columns = grid.Columns.Select(column => new { column.ActualWidth, column.MinWidth }).ToArray() });
                }
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, OpenVisionLanguage.Korean)]
    [InlineData(1280, OpenVisionLanguage.Korean)]
    [InlineData(1920, OpenVisionLanguage.English)]
    [InlineData(1280, OpenVisionLanguage.English)]
    public async Task SequenceUnitNavigationUsesActualAssociationAndPreservesUnassignedSequences(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Stations[0].Units[0].Name = "Same name";
            project.Stations[0].Units.Add(new() { Id = "empty-unit", Name = "Same name" });
            for (int i = 0; i < 24; i++) project.Stations[0].Units.Add(new() { Id = $"tail-{i}", Name = "Long unit " + new string('N', 90) });
            project.Sequences.Add(new() { Id = "common", Name = "Common sequence", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] });
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.SequenceEditor.ProcessViewIndex = 1; main.SequenceEditor.IsProcessSceneVisible = true;
            main.Navigation.OpenSequenceEditorCommand.Execute(null);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, 900);
            window.Show();
            try
            {
                SettleNavigation(window);
                var left = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Project.LeftToolRegionView>().Single(item => item.IsVisible);
                var list = Assert.IsType<ListBox>(left.FindName("SequenceUnitList"));
                var original = JsonSerializer.Serialize(project);
                Assert.True(list.IsVisible);
                Assert.Equal(27, list.Items.Count);
                Assert.Equal("unit:unit", list.SelectedValue);
                var first = main.SequenceNavigationItems.Single(item => item.UnitId == "unit");
                Assert.Equal(2, first.StepCount);
                Assert.Equal(main.SequenceEditor.StepListCountText, first.StepCountText);
                Assert.Contains(Descendants(list).OfType<TextBlock>(), text => text.Text == first.StepCountText && text.IsVisible);

                SelectNavigation(list, "unit:empty-unit"); SettleNavigation(window);
                Assert.Null(main.SequenceEditor.SelectedSequence);
                Assert.Empty(main.SequenceEditor.Steps);
                Assert.Equal("empty-unit", main.Layout.ActiveUnitId);
                Assert.Equal("unit:empty-unit", list.SelectedValue);
                Assert.Contains(Descendants(list).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("Sequence.NoSequenceSelected"));

                SelectNavigation(list, "sequence:common"); SettleNavigation(window);
                Assert.Same(project.Sequences[1], main.SequenceEditor.SelectedSequence);
                Assert.Single(main.SequenceEditor.Steps);
                main.SequenceEditor.SelectSequence("sequence"); SettleNavigation(window);
                Assert.Equal("unit:unit", list.SelectedValue);
                var admission = typeof(MainViewModel).GetMethod("SetSessionCloseAdmission", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                admission.Invoke(main, [true]); SettleNavigation(window);
                Assert.False(list.IsEnabled);
                var surface = Assert.IsType<Border>(list.Template.FindName("SequenceListSurface", list));
                Assert.Equal("#00FFFFFF", surface.Background.ToString());
                Assert.DoesNotContain(Descendants(list).OfType<Border>(), border => border.Background is SolidColorBrush brush && brush.Color == Colors.White);
                var disabledRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var disabledBorder = Assert.IsType<Border>(disabledRow.Template.FindName("SequenceUnitBorder", disabledRow));
                Assert.Equal(0.55, disabledBorder.Opacity);
                main.SelectedSequenceNavigationKey = "unit:empty-unit"; SettleNavigation(window);
                Assert.Equal("unit:unit", list.SelectedValue);
                Assert.Same(project.Sequences[0], main.SequenceEditor.SelectedSequence);
                admission.Invoke(main, [false]); SettleNavigation(window);
                Assert.True(list.IsEnabled);
                Assert.Equal(1, disabledBorder.Opacity);
                var draft = main.SequenceEditor.SelectedStepDraft!;
                draft.Parameter = "8";
                main.SequenceEditor.PendingStepDraftPrompt = () => throw new InvalidOperationException("Repeated selection must not prompt");
                SelectNavigation(list, "unit:unit"); SettleNavigation(window);
                Assert.Same(draft, main.SequenceEditor.SelectedStepDraft);
                Assert.Equal("8", draft.Parameter);
                main.SequenceEditor.DiscardStepDraftCommand.Execute(null);

                SelectNavigation(list, "unit:tail-23"); SettleNavigation(window);
                Assert.Equal("tail-23", main.Layout.ActiveUnitId);
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(25);
                Assert.True(row.IsVisible);
                Assert.True(row.Focus()); SettleNavigation(window);
                Assert.True(row.IsKeyboardFocused);
                Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == project.Stations[0].Units.Last().Name && text.TextWrapping == TextWrapping.Wrap && text.TextTrimming == TextTrimming.None);
                var scroll = Descendants(list).OfType<ScrollViewer>().First();
                Assert.True(scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0);
                Assert.True(new Rect(list.RenderSize).Contains(row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize))));

                foreach (int workspace in new[] { 0, 2, 3, 1 })
                {
                    main.Navigation.SelectedWorkspaceIndex = workspace; SettleNavigation(window);
                    Assert.Equal(workspace == 1, list.IsVisible);
                }
                Assert.Equal("unit:tail-23", list.SelectedValue);
                main.Navigation.SelectedExecutionTabIndex = 0; SettleNavigation(window);
                Assert.False(list.IsVisible);
                main.SequenceEditor.ProcessViewIndex = 1; main.SequenceEditor.IsProcessSceneVisible = true;
                main.Navigation.OpenSequenceEditorCommand.Execute(null); SettleNavigation(window);
                Assert.True(list.IsVisible);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges);
                Assert.False(main.IsRunning);

                SelectNavigation(list, "unit:unit"); SettleNavigation(window);
                main.SequenceEditor.AddStepCommand.Execute(main.SequenceEditor.Templates.First(template => template.Action == SequenceStepAction.Delay));
                SettleNavigation(window);
                Assert.Equal(3, project.Sequences[0].Steps.Count);
                Assert.Equal(3, Assert.IsType<SequenceUnitNavigationItem>(list.Items[0]).StepCount);
                Assert.Equal(main.SequenceEditor.StepListCountText, Assert.IsType<SequenceUnitNavigationItem>(list.Items[0]).StepCountText);
                Assert.Equal("unit", project.Sequences[0].UnitId);
                Assert.Null(project.Sequences[1].UnitId);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Fact]
    public async Task SequenceUnitNavigationCancelPreservesDraftAndDiscardRecoversWithoutImplicitApply()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            project.Stations[0].Units.Add(new() { Id = "empty", Name = "Empty unit" });
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.AddLayoutComponentCommand.Execute(OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.MachineFrame);
            var componentId = main.Layout.SelectedItem!.Id;
            main.SequenceEditor.ProcessViewIndex = 1; main.SequenceEditor.IsProcessSceneVisible = true;
            main.Navigation.OpenSequenceEditorCommand.Execute(null);
            main.Layout.Select(componentId);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900);
            window.Show();
            try
            {
                SettleNavigation(window);
                var left = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Project.LeftToolRegionView>().Single(item => item.IsVisible);
                var list = Assert.IsType<ListBox>(left.FindName("SequenceUnitList"));
                var original = JsonSerializer.Serialize(project);
                var draft = main.SequenceEditor.SelectedStepDraft!;
                draft.Parameter = "8";
                main.SequenceEditor.PendingStepDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Cancel;
                SelectNavigation(list, "unit:empty"); SettleNavigation(window);
                Assert.Equal("unit:unit", list.SelectedValue);
                Assert.Same(draft, main.SequenceEditor.SelectedStepDraft);
                Assert.Equal("8", draft.Parameter);
                Assert.Equal(componentId, main.Layout.SelectedItem!.Id);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                main.SequenceEditor.PendingStepDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Discard;
                SelectNavigation(list, "unit:empty"); SettleNavigation(window);
                Assert.Null(main.SequenceEditor.SelectedSequence);
                Assert.False(main.SequenceEditor.HasPendingStepDraft);
                Assert.Equal("empty", main.Layout.ActiveUnitId);

                SelectNavigation(list, "unit:unit"); SettleNavigation(window);
                main.Layout.Select(componentId);
                var placement = main.Layout.SelectedComponentEditor!;
                placement.DraftXText = "777";
                main.PlacementDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Cancel;
                SelectNavigation(list, "unit:empty"); SettleNavigation(window);
                Assert.Equal("unit:unit", list.SelectedValue);
                Assert.Equal("777", placement.DraftXText);
                Assert.True(placement.HasPendingPlacementDraft);
                Assert.Equal(componentId, main.Layout.SelectedItem!.Id);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                main.PlacementDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Discard;
                SelectNavigation(list, "unit:empty"); SettleNavigation(window);
                Assert.Null(main.SequenceEditor.SelectedSequence);
                Assert.False(placement.HasPendingPlacementDraft);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(main.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); }
            return true;
        });
    }

    [Fact]
    public async Task SequenceUnitNavigationReopenClearsStaleRowsAndLoadsNewAssociationWithoutRun()
    {
        await _ui.InvokeAsync(async () =>
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT") ?? @"D:\OpenVisionLab-TestData\Machine\sequence-navigation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var empty = new MachineProjectDocument { Name = "Empty replacement" };
            var replacement = CreateProject();
            replacement.Stations[0].Units.Add(new() { Id = "other", Name = "Unit" });
            replacement.Sequences.Add(new() { Id = "other-sequence", Name = "Other", UnitId = "other", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] });
            var store = new OpenVisionLab.Machine.Persistence.Projects.ProjectDocumentFileStore();
            var emptyPath = Path.Combine(root, "empty.ovmachine");
            var validPath = Path.Combine(root, "replacement.ovmachine");
            await store.SaveAsync(empty, emptyPath);
            await store.SaveAsync(replacement, validPath);
            using var main = new MainViewModel(CreateProject());
            main.Navigation.HideStartupChoice();
            main.SequenceEditor.ProcessViewIndex = 1; main.SequenceEditor.IsProcessSceneVisible = true;
            main.Navigation.OpenSequenceEditorCommand.Execute(null);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900);
            window.Show();
            try
            {
                SettleNavigation(window);
                var left = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Project.LeftToolRegionView>().Single(item => item.IsVisible);
                var list = Assert.IsType<ListBox>(left.FindName("SequenceUnitList"));
                Assert.Single(list.Items);
                Assert.True(await main.OpenProjectAsync(emptyPath));
                SettleNavigation(window);
                Assert.Empty(list.Items);
                Assert.Null(list.SelectedValue);
                Assert.Null(main.SelectedSequenceNavigationKey);
                Assert.Empty(main.SequenceEditor.Steps);
                Assert.False(main.IsRunning);
                Assert.True(await main.OpenProjectAsync(validPath));
                SettleNavigation(window);
                Assert.Equal(2, list.Items.Count);
                SelectNavigation(list, "unit:other"); SettleNavigation(window);
                Assert.Equal("other-sequence", main.SequenceEditor.SelectedSequence?.Id);
                Assert.Equal("other", main.Layout.ActiveUnitId);
                Assert.Single(main.SequenceEditor.Steps);
                Assert.Equal("unit:other", list.SelectedValue);
                Assert.False(main.HasUnsavedChanges);
                Assert.False(main.IsRunning);
                Assert.Equal("unit", Assert.IsType<SequenceUnitNavigationItem>(list.Items[0]).UnitId);
                Assert.Equal("other", Assert.IsType<SequenceUnitNavigationItem>(list.Items[1]).UnitId);
            }
            finally { window.DataContext = null; window.Close(); }
            return true;
        });
    }

    private static void SelectNavigation(ListBox list, string key)
    {
        var item = list.Items.Cast<SequenceUnitNavigationItem>().Single(item => item.Key == key);
        list.ScrollIntoView(item); list.UpdateLayout();
        var peer = new System.Windows.Automation.Peers.ListBoxItemAutomationPeer(item, new System.Windows.Automation.Peers.ListBoxAutomationPeer(list));
        ((System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
    }

    private static void SettleNavigation(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static MachineProjectDocument CreateProject() => new()
    {
        Channels = [new() { Id = "output", Name = "Output", Kind = ChannelKind.DigitalOutput }, new() { Id = "input", Name = "Input", Kind = ChannelKind.DigitalInput }],
        Stations = [new() { Id = "station", Name = "Station", Units = [new() { Id = "unit", Name = "Unit" }] }],
        Sequences = [new() { Id = "sequence", Name = "Sequence", UnitId = "unit", Steps = [new() { Id = "delay", Name = "Delay", Action = SequenceStepAction.Delay, Parameter = "1", NextStepId = "complete" }, new() { Id = "complete", Name = "Complete", Action = SequenceStepAction.Complete }] }]
    };

    private static void AssertGlyphs(Visual visual, double size, int weight, string foreground)
    {
        static IEnumerable<GlyphRunDrawing> Runs(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing run) yield return run;
            else if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var item in Runs(child)) yield return item;
        }
        var glyphs = Runs(Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(visual))).ToArray();
        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, glyph =>
        {
            Assert.Equal(size, glyph.GlyphRun.FontRenderingEmSize);
            Assert.Equal(weight, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
            Assert.Equal(foreground, glyph.ForegroundBrush.ToString());
        });
    }

    private static void Arrange(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(view.UpdateLayout));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
