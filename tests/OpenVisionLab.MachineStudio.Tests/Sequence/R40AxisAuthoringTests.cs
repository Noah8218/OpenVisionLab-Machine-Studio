using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40AxisAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyTemplateKeepsItsDefaultAxisAndExplicitAdd(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, fullArea);
        try
        {
            string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                Assert.Same(main.SequenceEditor, dialog.DataContext);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Capture(dialog, $"legacy-dialog-{language}-{fullArea}", false);
                var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition { Id: "move-axis-home" });
                Invoke(add); Pump(window);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("axis", created.TargetId);
            Assert.Equal("0", created.Parameter);
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.True(main.HasUnsavedChanges);
            Assert.False(main.IsRunning);
            Assert.Empty(main.SequenceEditor.ValidationIssues);
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { main.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData("move-axis-home", SequenceStepAction.MoveAxis, "35.5", 0)]
    [InlineData("wait-axis-done", SequenceStepAction.WaitAxisDone, "", 5000)]
    public Task AxisPreviewAndExplicitAddUseTheChosenActualAxis(string template, SequenceStepAction action, string parameter, int timeout) => ui.InvokeAsync(() =>
    {
        var project = CreateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            string before = JsonSerializer.Serialize(project);
            var selected = editor.SelectedStep;
            int changed = 0, added = 0;
            editor.DefinitionChanged += (_, _) => changed++;
            editor.AxisStepAdded += (_, _) => added++;
            editor.AxisStepTargetId = "axis-2";
            editor.AxisStepTemplateId = template;
            Assert.Equal(new[] { "move-axis-home", "wait-axis-done" }, editor.AxisStepTemplates.Select(item => item.Id));
            Assert.Equal("axis-2", editor.AxisStepPreview!.TargetId);
            Assert.Equal(parameter, editor.AxisStepPreview.Parameter);
            Assert.Equal(timeout, editor.AxisStepPreview.TimeoutMs);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.Same(selected, editor.SelectedStep);
            Assert.False(editor.HasPendingStepDraft);
            Assert.Equal(0, changed + added);
            Assert.True(editor.AddAxisStepCommand.CanExecute(null));
            editor.AddAxisStepCommand.Execute(null);
            var step = project.Sequences[0].Steps.Single(item => item.Id == "step-1");
            Assert.Equal(action, step.Action);
            Assert.Equal("axis-2", step.TargetId);
            Assert.Equal(parameter, step.Parameter);
            Assert.Equal(timeout, step.TimeoutMs);
            Assert.Equal("done", step.NextStepId);
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.Empty(editor.ValidationIssues);
            Assert.Equal(1, changed); Assert.Equal(1, added);
            Assert.Equal(string.Empty, editor.AxisStepTargetId);
            Assert.False(editor.AddAxisStepCommand.CanExecute(null));
            editor.AddAxisStepCommand.Execute(null);
            Assert.Equal(3, project.Sequences[0].Steps.Count);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("removed-axis")]
    [InlineData("duplicate-axis")]
    [InlineData("removed-sequence")]
    [InlineData("foreign-target")]
    [InlineData("disposed")]
    public Task AxisAuthoringRejectsUnavailableOrStaleContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.AxisStepTargetId = "axis-2";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "removed-axis": project.Axes.RemoveAt(1); break;
                case "duplicate-axis": project.Axes.Add(new VirtualAxisDefinition { Id = "axis-2" }); break;
                case "removed-sequence": project.Sequences.Clear(); break;
                case "foreign-target": editor.AxisStepTargetId = "camera"; break;
                case "disposed": editor.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddAxisStepCommand.CanExecute(null));
            editor.AddAxisStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task AxisAuthoringRefreshLoadAndPromptReentryKeepTheCurrentProjectBoundary() => ui.InvokeAsync(() =>
    {
        var project = CreateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.AxisStepTargetId = "axis-2";
            project.Axes[1].HomePosition = 42.25;
            editor.RefreshAuthoringTargets();
            Assert.Equal("42.25", editor.AxisStepPreview!.Parameter);
            var replacement = JsonSerializer.Deserialize<MachineProjectDocument>(JsonSerializer.Serialize(project))!;
            editor.Load(replacement);
            Assert.Equal(string.Empty, editor.AxisStepTargetId);
            Assert.Null(editor.AxisStepPreview);
            editor.AxisStepTargetId = "axis-2";
            editor.SelectedStepDraft!.Parameter = "0.2";
            string before = JsonSerializer.Serialize(replacement);
            editor.PendingStepDraftPrompt = () => { editor.Load(project); return PlacementDraftDecision.Discard; };
            editor.AddAxisStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(replacement));
            Assert.Equal(2, project.Sequences[0].Steps.Count);
            editor.AxisStepTargetId = "axis-2";
            editor.SelectedStepDraft!.Parameter = "0.3";
            editor.PendingStepDraftPrompt = () => { project.Axes.RemoveAt(1); return PlacementDraftDecision.Discard; };
            editor.AddAxisStepCommand.Execute(null);
            Assert.Equal(2, project.Sequences[0].Steps.Count);
            Assert.Null(editor.AxisStepPreview);
            Assert.Equal(string.Empty, editor.AxisStepTargetId);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedAxisChoicePreviewAddAndExistingTeachingApplyStaySeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor;
        var window = Show(main, fullArea);
        try
        {
            string before = JsonSerializer.Serialize(project);
            var selected = editor.SelectedStep;
            Assert.NotNull(main.SceneSnapshots.Latest);
            var positions = main.SceneSnapshots.Latest!.Axes.ToDictionary(axis => axis.Id, axis => axis.Position);
            var clock = main.SceneSnapshots.Latest.SimulationTime;
            WithDialog(window, dialog =>
            {
                var axis = (ComboBox)dialog.FindName("AxisStepTarget");
                axis.SetCurrentValue(Selector.SelectedValueProperty, "axis-2");
                ((ComboBox)dialog.FindName("AxisStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "move-axis-home");
                Pump(dialog);
                Assert.Equal("axis-2", editor.AxisStepTargetId);
                Assert.Equal("35.5 deg", ((TextBlock)dialog.FindName("AxisStepPreviewValue")).Text);
                var template = (ComboBox)dialog.FindName("AxisStepTemplate");
                template.SetCurrentValue(Selector.SelectedValueProperty, "wait-axis-done"); Pump(dialog);
                Assert.Equal("5000", ((TextBlock)dialog.FindName("AxisStepPreviewTimeout")).Text);
                Assert.EndsWith("(ms)", ((TextBlock)dialog.FindName("AxisStepTimeoutLabel")).Text);
                Capture(dialog, $"axis-wait-preview-{language}-{fullArea}", false);
                template.SetCurrentValue(Selector.SelectedValueProperty, "move-axis-home"); Pump(dialog);
                Assert.Equal("35.5 deg", ((TextBlock)dialog.FindName("AxisStepPreviewValue")).Text);
                Assert.Same(selected, editor.SelectedStep);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning || editor.HasPendingStepDraft);
                Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime);
                foreach (var position in positions) Assert.Equal(position.Value, main.SceneSnapshots.Latest.Axes.Single(axis => axis.Id == position.Key).Position);
                editor.IsEditable = false; Pump(dialog);
                Assert.False(axis.IsEnabled || ((Button)dialog.FindName("AddAxisStepButton")).IsEnabled);
                Assert.Equal("35.5 deg", ((TextBlock)dialog.FindName("AxisStepPreviewValue")).Text);
                Capture(dialog, $"axis-readonly-{language}-{fullArea}", false);
                editor.IsEditable = true; Pump(dialog);
                Capture(dialog, $"axis-preview-{language}-{fullArea}", false);
                var add = (Button)dialog.FindName("AddAxisStepButton");
                Assert.Same(editor.AddAxisStepCommand, add.Command);
                Assert.True(add.IsEnabled);
                Invoke(add); Pump(window);
                Assert.False(dialog.IsVisible);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("axis-2", created.TargetId); Assert.Equal("35.5", created.Parameter);
            Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning);
            Assert.Empty(editor.ValidationIssues);
            Capture(window, $"axis-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
            ((ComboBox)inspector.FindName("StepDraftTeaching")).SetCurrentValue(Selector.SelectedValueProperty, "검사 위치 B");
            Pump(window);
            Assert.Equal("60.25", editor.SelectedStepDraft!.Parameter);
            Assert.Equal("35.5", created.Parameter);
            Assert.True(editor.HasPendingStepDraft);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
            Assert.Equal("60.25", created.Parameter);
            Assert.False(editor.HasPendingStepDraft || main.IsRunning);
            Assert.Equal(60.25, project.Axes[1].TeachingPositions!["검사 위치 B"]);
            return true;
        }
        finally { editor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task AxisDialogCancelKeepsDraftAndSuccessClosesOnlyTheOwnedDialog(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor;
        var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2";
            editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
            string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                ((ComboBox)dialog.FindName("AxisStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "axis-2"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddAxisStepButton")); Pump(window);
                Assert.True(dialog.IsVisible);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.True(editor.HasPendingStepDraft);
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
                Invoke((Button)dialog.FindName("AddAxisStepButton")); Pump(window);
                Assert.False(dialog.IsVisible);
            });
            Assert.Equal(3, project.Sequences[0].Steps.Count);
            Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter);
            return true;
        }
        finally { editor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongAxisNamesRemainReadableInThePreviewAndContextReplacementDetachesTheView(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject();
        project.Layouts[0].Components[1].Name = string.Join(" ", Enumerable.Repeat("회전 축의 두 번째 검사 위치 설정", 9));
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                ((ComboBox)dialog.FindName("AxisStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "axis-2"); Pump(dialog);
                var target = (TextBlock)dialog.FindName("AxisStepPreviewTarget");
                Assert.Equal(project.Layouts[0].Components[1].Name + " (axis-2)", target.Text.Replace("\u2060", string.Empty));
                Assert.Contains("\u2060", target.Text);
                target.Measure(new Size(target.ActualWidth, double.PositiveInfinity));
                Assert.True(target.DesiredSize.Height <= target.ActualHeight + target.Margin.Top + target.Margin.Bottom + 1);
                Capture(dialog, $"axis-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.AxisStepTargetId = "axis-2";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog);
                    main.SequenceEditor.AxisStepTargetId = "axis-2";
                    main.SequenceEditor.AddAxisStepCommand.Execute(null); Pump(window);
                    Assert.True(dialog.IsVisible);
                    Invoke((Button)dialog.FindName("AddAxisStepButton")); Pump(window);
                    Assert.False(dialog.IsVisible);
                }
                finally { replacement.Dispose(); }
            });
            Assert.NotEqual(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { main.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Axes[0].HomePosition = 0;
        project.Axes.Add(new VirtualAxisDefinition { Id = "axis-2", Name = "회전 축 B", Kind = AxisKind.Rotary, Unit = "deg", HomePosition = 35.5, SoftLimitMin = -100, SoftLimitMax = 100,
            TeachingPositions = new Dictionary<string, double> { ["검사 위치 B"] = 60.25 } });
        project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "stage-2", Name = "회전 축 B", Kind = LayoutComponentKind.RotaryStage, BehaviorBindingId = "axis-2" });
        project.Sequences[0].Steps = [new() { Id = "feed", Name = "준비 대기", Action = SequenceStepAction.Delay, Parameter = "0.1" }, new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }];
        return project;
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray();
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX; window.Top = area[1] / dpi.DpiScaleY;
            window.Width = area[2] / dpi.DpiScaleX; window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window);
        return window;
    }

    private static void WithDialog(Window window, Action<SequenceStepTemplateDialog> action)
    {
        Exception? failure = null;
        bool completed = false;
        var operation = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<SequenceStepTemplateDialog>().Single(item => ReferenceEquals(item.Owner, window));
            try { dialog.UpdateLayout(); action(dialog); completed = true; }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        try
        {
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            Assert.True(view.DockedAddStepButton.IsEnabled);
            view.DockedAddStepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { operation.Abort(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(completed, "The existing owned template-dialog entry must execute the inspection callback.");
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "axis-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
    }
}
