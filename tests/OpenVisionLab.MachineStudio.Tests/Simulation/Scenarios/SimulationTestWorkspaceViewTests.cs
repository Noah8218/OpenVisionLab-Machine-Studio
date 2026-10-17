using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SimulationTestWorkspaceViewTests
{
    private readonly StudioUiTestHost _ui;
    public SimulationTestWorkspaceViewTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task LongScenarioChoicesStayReadableAndPreserveSelectionContracts(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var longName = new string('W', 80);
            var project = new MachineProjectDocument
            {
                Name = "Owned scenario selectors", Simulation = new SimulationDefinition { ActiveLayoutId = "layout" },
                Axes = [new VirtualAxisDefinition { Id = "axis-a", Name = longName, HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 }, new VirtualAxisDefinition { Id = "axis-b", Name = "Other axis", HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 }],
                Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "Owned scene", Components = [new LayoutComponentDefinition { Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame }] }],
                Sequences = [new SequenceDefinition { Id = longName, Name = "Long recovery ID", Steps = [new SequenceStepDefinition { Id = "long-complete", Name = "Complete", Action = SequenceStepAction.Complete }] }, new SequenceDefinition { Id = "recovery-short", Name = "Other recovery", Steps = [new SequenceStepDefinition { Id = "short-complete", Name = "Complete", Action = SequenceStepAction.Complete }] }]
            };
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)window.Width, (int)window.Height);
            window.Show();
            RecordMonitor(window, $"{window.Width}-{language}-{nameof(LongScenarioChoicesStayReadableAndPreserveSelectionContracts)}");
            var failures = new List<string>();
            var measurements = new List<object>();
            try
            {
                await WaitFor(() => model.SceneSnapshots.Latest?.Axes.Count > 0);
                model.Navigation.IsSimulationWorkspace = true;
                Pump(window);
                model.Navigation.OpenTestScenarioCommand.Execute(null);
                Pump(window);
                var view = Descendants(window).OfType<SimulationTestWorkspaceView>().Single();
                Assert.True(view.IsVisible);
                Assert.Equal(width, window.ActualWidth);
                Assert.Equal(language, OpenVisionLanguageService.CurrentLanguage);
                Directory.CreateDirectory(EvidenceRoot());
                var profilePath = Path.Combine(EvidenceRoot(), $"profile-{width}-{language}.json");
                await File.WriteAllTextAsync(profilePath, "{\"schemaVersion\":2,\"profileId\":\"owned-long-profile\",\"name\":\"" + longName + "\",\"mode\":\"normal\",\"initialState\":\"normal\",\"minimumStateTicks\":8,\"jitterTicks\":0}");
                var config = model.SimulationWorkspace;
                config.ScenarioProfilePath = profilePath;
                config.LoadScenarioProfileCommand.Execute(null);
                Assert.Null(config.ScenarioProfileLoadError);
                Assert.Equal(longName, config.SelectedScenarioProfile.Name);
                config.ScenarioTargetId = "axis-a";
                config.IsScheduledFaultEnabled = true;
                config.ScheduledFaultTargetId = "axis-a";
                config.RestartSequenceAfterFault = true;
                config.RecoverySequenceId = longName;
                config.RequireFinalEquipmentState = true;
                config.FinalEquipmentTargetId = "axis-a";
                config.FinalEquipmentExpectedState = "Idle";
                Pump(window);
                var longProfile = config.SelectedScenarioProfile;
                var originalTick = model.SceneSnapshots.Latest!.TickIndex;
                foreach (var name in new[] { "ScenarioProfileComboBox", "ScenarioTargetComboBox", "ScheduledFaultTargetComboBox", "RecoverySequenceComboBox", "FinalEquipmentTargetComboBox" })
                {
                    var combo = Assert.IsType<ComboBox>(view.FindName(name));
                    combo.BringIntoView();
                    Pump(window);
                    Assert.NotNull(combo.SelectedItem);
                    Assert.Equal(longName, ChoiceName(combo.SelectedItem));
                    CheckGlyphs(combo, combo, name + "-selected", longName, failures, measurements);
                    Capture(window, $"{width}-{language}-{name}-selected");
                    var selectedId = ChoiceId(combo.SelectedItem);
                    var payload = JsonSerializer.Serialize(project);
                    combo.IsDropDownOpen = true;
                    Pump(window);
                    var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromItem(combo.SelectedItem));
                    CheckGlyphs(item, item, name + "-popup", longName, failures, measurements);
                    combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(combo), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                    Pump(window);
                    Assert.False(combo.IsDropDownOpen);
                    Assert.Equal(selectedId, ChoiceId(combo.SelectedItem!));
                    Assert.Equal(payload, JsonSerializer.Serialize(project));
                    combo.IsDropDownOpen = true;
                    Pump(window);
                    var alternate = combo.Items.Cast<object>().First(x => ChoiceId(x) != selectedId);
                    var choicePeers = new ComboBoxAutomationPeer(combo).GetChildren().Where(peer => peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider).ToArray();
                    Assert.Equal(combo.Items.Count, choicePeers.Length);
                    Assert.IsAssignableFrom<ISelectionItemProvider>(choicePeers[combo.Items.IndexOf(alternate)].GetPattern(PatternInterface.SelectionItem)).Select();
                    combo.IsDropDownOpen = false;
                    Pump(window);
                    Assert.Equal(ChoiceId(alternate), BoundId(config, name));
                    switch (name)
                    {
                        case "ScenarioProfileComboBox": config.SelectedScenarioProfile = longProfile; break;
                        case "ScenarioTargetComboBox": config.ScenarioTargetId = selectedId; break;
                        case "ScheduledFaultTargetComboBox": config.ScheduledFaultTargetId = selectedId; break;
                        case "RecoverySequenceComboBox": config.RecoverySequenceId = selectedId; break;
                        case "FinalEquipmentTargetComboBox": config.FinalEquipmentTargetId = selectedId; break;
                    }
                    Pump(window);
                    Assert.Equal(selectedId, ChoiceId(combo.SelectedItem!));
                }
                var kind = Assert.IsType<ComboBox>(view.FindName("ScheduledFaultKindComboBox"));
                Assert.NotNull(kind.SelectedItem);
                config.IsScheduledFaultEnabled = false;
                Pump(window);
                Assert.False(kind.IsEnabled);
                Assert.False(Assert.IsType<ComboBox>(view.FindName("ScheduledFaultTargetComboBox")).IsEnabled);
                Capture(window, $"{width}-{language}-fault-disabled");
                config.IsScheduledFaultEnabled = true;
                Pump(window);
                Assert.True(kind.IsEnabled);
                Assert.False(model.IsRunning);
                Assert.Equal(originalTick, model.SceneSnapshots.Latest!.TickIndex);
                var ownedFile = Path.Combine(EvidenceRoot(), $"selectors-{width}-{language}.ovmachine");
                await model.SaveProjectAsync(ownedFile);
                var bytes = await File.ReadAllBytesAsync(ownedFile);
                var saved = new ProjectDocumentStore().Load(await File.ReadAllTextAsync(ownedFile));
                Assert.Equal(longName, saved.Axes.Single(x => x.Id == "axis-a").Name);
                Assert.Contains(saved.Sequences, x => x.Id == longName);
                using var reopened = new MainViewModel();
                await reopened.OpenProjectAsync(ownedFile);
                Assert.Equal("axis-a", reopened.SimulationWorkspace.ScenarioTargetId);
                Assert.Equal("axis-a", reopened.SimulationWorkspace.ScheduledFaultTargetId);
                Assert.Equal(longName, reopened.SimulationWorkspace.RecoverySequenceId);
                Assert.Equal("axis-a", reopened.SimulationWorkspace.FinalEquipmentTargetId);
                Assert.False(reopened.IsRunning);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(ownedFile));
            }
            finally
            {
                Directory.CreateDirectory(EvidenceRoot());
                await File.WriteAllTextAsync(Path.Combine(EvidenceRoot(), $"measurements-{width}-{language}.json"), JsonSerializer.Serialize(new { failures, measurements }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
                OpenVisionLanguageService.SetLanguage(previous, save: false);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task ShortFaultKindTemplateShowsActualChoicesAndDisabledState(OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var model = new MainViewModel(new MachineProjectDocument
            {
                Name = "Owned short selector", Simulation = new SimulationDefinition { ActiveLayoutId = "layout" },
                Axes = [new VirtualAxisDefinition { Id = "axis-a", Name = "Axis", HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 }],
                Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "Owned scene", Components = [new LayoutComponentDefinition { Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame }] }]
            });
            model.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = model, Width = 1280, Height = 760, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)window.Width, (int)window.Height);
            window.Show();
            RecordMonitor(window, $"{window.Width}-{language}-{nameof(ShortFaultKindTemplateShowsActualChoicesAndDisabledState)}");
            var failures = new List<string>();
            var measurements = new List<object>();
            try
            {
                await WaitFor(() => model.SceneSnapshots.Latest?.Axes.Count > 0);
                model.Navigation.IsSimulationWorkspace = true;
                Pump(window);
                model.Navigation.OpenTestScenarioCommand.Execute(null);
                model.SimulationWorkspace.IsScheduledFaultEnabled = true;
                Pump(window);
                var view = Descendants(window).OfType<SimulationTestWorkspaceView>().Single();
                var combo = Assert.IsType<ComboBox>(view.FindName("ScheduledFaultKindComboBox"));
                combo.BringIntoView();
                Pump(window);
                var kind = model.SimulationWorkspace.ScheduledFaultKind;
                var selectedName = model.SimulationWorkspace.AvailableScheduledFaultKinds.Single(x => x.Kind == kind).Name;
                CheckGlyphs(combo, combo, "kind-selected", selectedName, failures, measurements);
                Capture(window, $"short-{language}-enabled");
                combo.IsDropDownOpen = true;
                Pump(window);
                foreach (var option in model.SimulationWorkspace.AvailableScheduledFaultKinds)
                {
                    var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromItem(option));
                    CheckGlyphs(item, item, "kind-popup-" + option.Kind, option.Name, failures, measurements);
                }
                var popup = Descendants(combo).OfType<Popup>().Single();
                var root = Assert.IsAssignableFrom<FrameworkElement>(PresentationSource.FromVisual(popup.Child).RootVisual);
                CaptureElement(root, $"short-{language}-popup");
                combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(combo), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                Pump(window);
                Assert.False(combo.IsDropDownOpen);
                Assert.Equal(kind, model.SimulationWorkspace.ScheduledFaultKind);
                model.SimulationWorkspace.IsScheduledFaultEnabled = false;
                Pump(window);
                Assert.False(combo.IsEnabled);
                CheckGlyphs(combo, combo, "kind-disabled", selectedName, failures, measurements);
                Capture(window, $"short-{language}-disabled");
                model.SimulationWorkspace.IsScheduledFaultEnabled = true;
                Pump(window);
                Assert.True(combo.IsEnabled);
                Assert.Equal(selectedName, Descendants(combo).OfType<TextBlock>().Single(x => x.Text == selectedName).Text);
                Capture(window, $"short-{language}-restored");
                Assert.False(model.IsRunning);
                Assert.Equal(0, model.SceneSnapshots.Latest!.TickIndex);
            }
            finally
            {
                Directory.CreateDirectory(EvidenceRoot());
                await File.WriteAllTextAsync(Path.Combine(EvidenceRoot(), $"short-measurements-{language}.json"), JsonSerializer.Serialize(new { failures, measurements }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
                OpenVisionLanguageService.SetLanguage(previous, save: false);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task ViewMenuOverlayRetainsOneViewAndDraftWithoutRuntimeOrProjectActions(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new MachineProjectDocument
            {
                Name = "Owned scenario navigation", Simulation = new SimulationDefinition { ActiveLayoutId = "layout" },
                Axes = [new VirtualAxisDefinition { Id = "axis-a", Name = "Owned axis", SoftLimitMin = -10, SoftLimitMax = 10 }],
                Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "Owned scene", Components = [new LayoutComponentDefinition { Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame }] }]
            };
            using var model = new MainViewModel(project);
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)window.Width, (int)window.Height);
            window.Show();
            RecordMonitor(window, $"{window.Width}-{language}-{nameof(ViewMenuOverlayRetainsOneViewAndDraftWithoutRuntimeOrProjectActions)}");
            try
            {
                await WaitFor(() => model.SceneSnapshots.Latest?.Axes.Count > 0);
                Pump(window);
                var view = Assert.IsType<SimulationTestWorkspaceView>(window.FindName("TestScenarioOverlay"));
                var workspace = model.SimulationWorkspace;
                Assert.Same(model, view.DataContext);
                Assert.False(view.IsVisible);
                var menu = Descendants(window).OfType<MainMenuView>().Single();
                var item = Assert.IsType<MenuItem>(menu.FindName("TestScenarioMenuItem"));
                Assert.Same(model.Navigation.OpenTestScenarioCommand, item.Command);
                Assert.True(item.IsEnabled);
                var owner = Assert.IsType<MenuItem>(ItemsControl.ItemsControlFromItemContainer(item));
                owner.IsSubmenuOpen = true;
                Pump(window);
                ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
                Assert.True(view.IsVisible && model.Navigation.IsTestScenarioOpen);
                Assert.False(model.Navigation.OpenTestScenarioCommand.CanExecute(null));
                model.Navigation.OpenTestScenarioCommand.Execute(null);
                Assert.Single(Descendants(window).OfType<SimulationTestWorkspaceView>());
                var input = Assert.IsType<TextBox>(view.FindName("BatchRepetitionCountTextBox"));
                input.SetCurrentValue(TextBox.TextProperty, "7");
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Pump(window);
                Assert.Equal(7, workspace.BatchRepetitionCount);
                var faultCheck = Assert.IsType<CheckBox>(view.FindName("ScheduledFaultEnabledCheckBox"));
                CheckCheckboxAppearance(faultCheck);
                Keyboard.Focus(faultCheck);
                Assert.True(faultCheck.IsKeyboardFocused);
                var faultToggle = (IToggleProvider)new CheckBoxAutomationPeer(faultCheck).GetPattern(PatternInterface.Toggle);
                var checkboxPreview = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(faultCheck), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                faultCheck.RaiseEvent(checkboxPreview);
                Assert.False(checkboxPreview.Handled);
                faultCheck.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(faultCheck), 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                faultCheck.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(faultCheck), 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
                Pump(window);
                Assert.True(workspace.IsScheduledFaultEnabled && faultCheck.IsChecked == true);
                await Task.Delay(250); // Allow the retained Wpf.Ui checkmark's 250 ms animation to finish.
                Assert.True(Assert.IsType<ComboBox>(view.FindName("ScheduledFaultKindComboBox")).IsEnabled);
                Capture(window, $"navigation-{width}-{language}-checkbox-checked-focused");
                faultToggle.Toggle();
                Pump(window);
                Assert.False(workspace.IsScheduledFaultEnabled || faultCheck.IsChecked == true);
                Assert.False(Assert.IsType<ComboBox>(view.FindName("ScheduledFaultKindComboBox")).IsEnabled);
                var automaticCheck = Assert.IsType<CheckBox>(view.FindName("AutomaticCycleAssertionCheckBox"));
                automaticCheck.BringIntoView();
                Pump(window);
                CheckCheckboxAppearance(automaticCheck);
                var automaticToggle = (IToggleProvider)new CheckBoxAutomationPeer(automaticCheck).GetPattern(PatternInterface.Toggle);
                automaticToggle.Toggle();
                Pump(window);
                var minimum = Assert.IsType<TextBox>(view.FindName("MinimumCompletedCyclesTextBox"));
                Assert.True(workspace.RequireAutomaticCycleCompleted && minimum.IsEnabled);
                Assert.Equal(((StackPanel)minimum.Parent).ActualWidth, minimum.ActualWidth);
                Assert.Equal(((StackPanel)input.Parent).ActualWidth, input.ActualWidth);
                automaticToggle.Toggle();
                Pump(window);
                Assert.False(workspace.RequireAutomaticCycleCompleted || minimum.IsEnabled);
                Capture(window, $"navigation-{width}-{language}-checkbox-dependent-disabled");
                var snapshot = model.SceneSnapshots.Latest;
                var payload = new ProjectDocumentStore().Serialize(project);
                var dirty = model.HasUnsavedChanges;
                var saved = model.HasSavedProjectFile;
                var running = model.IsRunning;
                var mode = model.IsRunMode;
                var selected = model.Layout.SelectedItem;
                var workspaceIndex = model.Navigation.SelectedWorkspaceIndex;
                var close = Assert.IsType<Button>(view.FindName("CloseTestScenarioButton"));
                Assert.Same(model.Navigation.CloseTestScenarioCommand, close.Command);
                ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
                Assert.False(view.IsVisible || model.Navigation.IsTestScenarioOpen);
                model.Navigation.OpenTestScenarioCommand.Execute(null);
                Pump(window);
                Assert.Same(workspace, model.SimulationWorkspace);
                Assert.Same(view, window.FindName("TestScenarioOverlay"));
                Assert.Equal("7", input.Text);
                Assert.Equal(snapshot!.TickIndex, model.SceneSnapshots.Latest!.TickIndex);
                var scroll = Assert.IsType<ScrollViewer>(view.FindName("TestWorkspaceScrollViewer"));
                var header = Assert.IsType<Border>(view.FindName("TestScenarioDialogHeader"));
                var footer = Assert.IsType<Border>(view.FindName("TestScenarioDialogFooter"));
                var body = Assert.IsType<StackPanel>(scroll.Content);
                var columns = body.Children.OfType<Grid>().Single();
                Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
                Assert.Equal(new Thickness(18, 14, 33, 14), body.Margin);
                Assert.Equal(1047, body.ActualWidth);
                Assert.Equal(model.Navigation.IsCompactLayout ? 1047 : 517.5, columns.Children.OfType<Border>().First().ActualWidth);
                var conditionColumn = columns.Children.OfType<Border>().First();
                var repeatCard = columns.Children.OfType<Border>().Last();
                var assertionCard = ((StackPanel)conditionColumn.Child).Children.OfType<Border>().Last();
                var conditionBounds = conditionColumn.TransformToAncestor(columns).TransformBounds(new Rect(conditionColumn.RenderSize));
                var assertionBounds = assertionCard.TransformToAncestor(columns).TransformBounds(new Rect(assertionCard.RenderSize));
                var repeatBounds = repeatCard.TransformToAncestor(columns).TransformBounds(new Rect(repeatCard.RenderSize));
                double sectionGap = model.Navigation.IsCompactLayout ? repeatBounds.Top - assertionBounds.Bottom : repeatBounds.Left - conditionBounds.Right;
                Assert.Equal(12, sectionGap);
                double bodyFont = model.Navigation.IsCompactLayout ? 12.5 : 13;
                double bodyLine = model.Navigation.IsCompactLayout ? 18.125 : 18.85;
                var status = Assert.IsType<TextBlock>(view.FindName("ScheduledFaultValidationText"));
                var previousTarget = workspace.ScheduledFaultTargetId;
                workspace.IsScheduledFaultEnabled = true;
                workspace.ScheduledFaultTargetId = null;
                Pump(window);
                Assert.True(status.IsVisible);
                status.BringIntoView();
                Pump(window);
                Assert.All(GlyphRuns(VisualTreeHelper.GetDrawing(status)), run => Assert.Equal(bodyFont, run.GlyphRun.FontRenderingEmSize));
                Assert.NotEmpty(GlyphRuns(VisualTreeHelper.GetDrawing(status)));
                Capture(window, $"navigation-{width}-{language}-default-error-font");
                workspace.IsScheduledFaultEnabled = false;
                workspace.ScheduledFaultTargetId = previousTarget;
                Pump(window);
                Assert.Equal(bodyFont, status.FontSize);
                Assert.Equal(bodyLine, status.LineHeight);
                var buttonText = Descendants(close).OfType<TextBlock>().Single(text => text.Text == close.Content?.ToString());
                Assert.Equal(bodyFont, close.FontSize);
                Assert.Equal(bodyFont, buttonText.FontSize);
                Assert.Equal(bodyLine, buttonText.LineHeight);
                Assert.All(GlyphRuns(VisualTreeHelper.GetDrawing(buttonText)), run => Assert.Equal(bodyFont, run.GlyphRun.FontRenderingEmSize));
                Assert.NotEmpty(GlyphRuns(VisualTreeHelper.GetDrawing(buttonText)));
                Assert.Equal(12, input.FontSize);
                var selectedText = Descendants(Assert.IsType<ComboBox>(view.FindName("ScenarioProfileComboBox"))).OfType<TextBlock>().Single(text => text.Text == workspace.SelectedScenarioProfile.Name);
                Assert.Equal(12, selectedText.FontSize);
                Assert.Equal(17.4, selectedText.LineHeight);
                var title = header.Child as TextBlock;
                Assert.NotNull(title);
                Assert.Equal(16, title.FontSize);
                Assert.Equal(23.2, title.LineHeight);
                var section = Assert.IsType<TextBlock>(view.FindName("RepeatValidationSectionAnchor"));
                Assert.Equal(12.5, section.FontSize);
                Assert.Equal(18.125, section.LineHeight);
                section.BringIntoView();
                Pump(window);
                Assert.NotEmpty(GlyphRuns(VisualTreeHelper.GetDrawing(section)));
                Assert.All(GlyphRuns(VisualTreeHelper.GetDrawing(section)), run => Assert.Equal(12.5, run.GlyphRun.FontRenderingEmSize));
                columns.Visibility = Visibility.Collapsed; // Owned short-body fixture exercises Auto without overflow.
                Pump(window);
                Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
                Assert.Equal(new Thickness(18, 14, 18, 14), body.Margin);
                Assert.Equal(1062, body.ActualWidth);
                Capture(window, $"navigation-{width}-{language}-short-body-no-gutter");
                columns.Visibility = Visibility.Visible;
                Pump(window);
                Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
                Assert.Equal(new Thickness(18, 14, 33, 14), body.Margin);
                Assert.Equal(1047, body.ActualWidth);
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"sn03-{width}-{language}.json"), JsonSerializer.Serialize(new { width, height, language, body.Margin, BodyWidth = body.ActualWidth, CardWidth = columns.Children.OfType<Border>().First().ActualWidth, conditionBounds, assertionBounds, repeatBounds, sectionGap, scroll.ComputedVerticalScrollBarVisibility, BodyFont = status.FontSize, BodyLine = status.LineHeight, ButtonFont = buttonText.FontSize, ButtonLine = buttonText.LineHeight, Checks = new[] { "overflow gutter15", "short-body Auto collapsed gutter0", "restored content gutter15", "Compact default font and button content", "Compact sections gap12 / Wide columns gap12 actual rendered bounds" } }));
                var headerTop = header.TranslatePoint(new Point(), view).Y;
                var footerTop = footer.TranslatePoint(new Point(), view).Y;
                scroll.ScrollToEnd();
                Pump(window);
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
                Assert.Equal(headerTop, header.TranslatePoint(new Point(), view).Y);
                Assert.Equal(footerTop, footer.TranslatePoint(new Point(), view).Y);
                Assert.True(close.IsVisible && close.IsEnabled);
                Capture(window, $"navigation-{width}-{language}-bottom");
                var profile = Assert.IsType<ComboBox>(view.FindName("ScenarioProfileComboBox"));
                profile.BringIntoView();
                Pump(window);
                profile.IsDropDownOpen = true;
                Pump(window);
                var popupItem = Assert.IsType<ComboBoxItem>(profile.ItemContainerGenerator.ContainerFromItem(profile.SelectedItem));
                Keyboard.Focus(popupItem);
                var firstEscape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popupItem), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(firstEscape);
                Assert.False(firstEscape.Handled);
                Assert.True(model.Navigation.IsTestScenarioOpen && profile.IsDropDownOpen);
                profile.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(profile), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                Pump(window);
                Assert.False(profile.IsDropDownOpen);
                Assert.True(model.Navigation.IsTestScenarioOpen);
                Keyboard.Focus(input);
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                input.RaiseEvent(escape);
                Pump(window);
                Assert.True(escape.Handled);
                Assert.False(view.IsVisible || model.Navigation.IsTestScenarioOpen);
                Assert.Same(owner, Keyboard.FocusedElement);
                Assert.Same(model, view.DataContext);
                Assert.Equal(workspaceIndex, model.Navigation.SelectedWorkspaceIndex);
                Assert.Same(selected, model.Layout.SelectedItem);
                Assert.Equal(payload, new ProjectDocumentStore().Serialize(project));
                Assert.Equal(dirty, model.HasUnsavedChanges);
                Assert.Equal(saved, model.HasSavedProjectFile);
                Assert.Equal(running, model.IsRunning);
                Assert.Equal(mode, model.IsRunMode);
                Assert.Equal(7, workspace.BatchRepetitionCount);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    private static void CheckCheckboxAppearance(CheckBox checkbox)
    {
        var icon = Assert.IsType<Border>(checkbox.Template.FindName("ControlBorderIconPresenter", checkbox));
        Assert.Equal(13, icon.ActualWidth);
        Assert.Equal(13, icon.ActualHeight);
        var label = Descendants(checkbox).OfType<TextBlock>().Single(text => text.Text == checkbox.Content?.ToString());
        Assert.Equal(11.5, label.FontSize);
        Assert.Equal(FontWeights.Normal, label.FontWeight);
        Assert.Equal(16.675, label.LineHeight);
        Assert.Equal("#FF243448", label.Foreground.ToString());
    }

    private static IEnumerable<GlyphRunDrawing> GlyphRuns(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var run in GlyphRuns(child)) yield return run;
    }

    private static void RecordMonitor(Window window, string name)
    {
        var evidence = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(evidence.WindowIntersectsMonitor);
        Directory.CreateDirectory(EvidenceRoot());
        File.WriteAllText(Path.Combine(EvidenceRoot(), "monitor-" + name + ".json"), JsonSerializer.Serialize(new { evidence, dpi = VisualTreeHelper.GetDpi(window) }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CaptureElement(FrameworkElement element, string name)
    {
        Directory.CreateDirectory(EvidenceRoot());
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(EvidenceRoot(), name + ".png"));
        encoder.Save(stream);
    }

    private static string ChoiceName(object item) => item switch { SimulationScenarioProfile profile => profile.Name, SimulationScenarioTargetOption target => target.Name, _ => throw new InvalidOperationException(item.GetType().FullName) };
    private static string ChoiceId(object item) => item switch { SimulationScenarioProfile profile => profile.ProfileId, SimulationScenarioTargetOption target => target.Id, _ => throw new InvalidOperationException(item.GetType().FullName) };
    private static string? BoundId(SimulationWorkspaceViewModel config, string name) => name switch { "ScenarioProfileComboBox" => config.SelectedScenarioProfile.ProfileId, "ScenarioTargetComboBox" => config.ScenarioTargetId, "ScheduledFaultTargetComboBox" => config.ScheduledFaultTargetId, "RecoverySequenceComboBox" => config.RecoverySequenceId, "FinalEquipmentTargetComboBox" => config.FinalEquipmentTargetId, _ => throw new InvalidOperationException(name) };
    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Owned selector fixture did not settle within 15 seconds.");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }
    private static void CheckGlyphs(FrameworkElement root, FrameworkElement parent, string name, string expected, List<string> failures, List<object> measurements)
    {
        var text = Descendants(root).OfType<TextBlock>().FirstOrDefault(x => x.Text == expected);
        if (text is null) { failures.Add(name + ": selected text missing"); return; }
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        if (text.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = Math.Max(1, text.ActualWidth);
        var bounds = text.TransformToAncestor(parent).TransformBounds(new Rect(text.RenderSize));
        measurements.Add(new { name, text = text.Text, textWidth = text.ActualWidth, textHeight = text.ActualHeight, glyphWidth = formatted.Width, glyphHeight = formatted.Height, parentWidth = parent.ActualWidth, parentHeight = parent.ActualHeight, bounds, wrapping = text.TextWrapping.ToString() });
        if (formatted.Width > text.ActualWidth + 1 || formatted.Height > text.ActualHeight + 1 || bounds.Left < -1 || bounds.Right > parent.ActualWidth + 1 || bounds.Top < -1 || bounds.Bottom > parent.ActualHeight + 1)
            failures.Add($"{name}: glyph {formatted.Width}x{formatted.Height}, text {text.ActualWidth}x{text.ActualHeight}, bounds {bounds}, parent {parent.ActualWidth}x{parent.ActualHeight}");
    }
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR") ?? @"D:/OpenVisionLab-TestData/Machine/r24-overnight-20261005/b07-test-selectors/ui";
    private static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(EvidenceRoot());
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(EvidenceRoot(), name + ".png"));
        encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
