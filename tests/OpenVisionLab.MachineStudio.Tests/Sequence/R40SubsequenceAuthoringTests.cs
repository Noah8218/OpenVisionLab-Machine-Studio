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
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40SubsequenceAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacySubsequenceTemplateKeepsItsDefaultTargetAndExplicitAdd(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
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
                var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition { Id: "call-subsequence" });
                add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal(SequenceStepAction.CallSubsequence, created.Action);
            Assert.Equal("child-a", created.TargetId); Assert.Equal(string.Empty, created.Parameter); Assert.Equal(0, created.TimeoutMs);
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning); Assert.Empty(main.SequenceEditor.ValidationIssues);
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Fact]
    public Task SubsequencePreviewAndExplicitAddKeepActualIdsAndFactoryDefaults() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); string before = JsonSerializer.Serialize(project);
            var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            int changed = 0, added = 0, otherAdded = 0;
            editor.DefinitionChanged += (_, _) => changed++;
            editor.SubsequenceStepAdded += (_, _) => added++;
            editor.AxisStepAdded += (_, _) => otherAdded++; editor.CameraStepAdded += (_, _) => otherAdded++; editor.SignalStepAdded += (_, _) => otherAdded++;
            Assert.Equal(new[] { "child-a", "child-b" }, editor.SubsequenceStepTargets.Select(target => target.Id));
            Assert.Null(editor.SubsequenceStepPreview); Assert.False(editor.AddSubsequenceStepCommand.CanExecute(null));
            editor.SubsequenceStepTargetId = "child-b";
            var preview = editor.SubsequenceStepPreview!;
            Assert.Equal(SequenceStepAction.CallSubsequence, preview.Action);
            Assert.Equal("child-b", preview.TargetId); Assert.Equal(string.Empty, preview.Parameter); Assert.Equal(0, preview.TimeoutMs);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
            Assert.False(editor.HasPendingStepDraft); Assert.Equal(0, changed + added + otherAdded);
            Assert.True(editor.AddSubsequenceStepCommand.CanExecute(null)); editor.AddSubsequenceStepCommand.Execute(null);
            var step = project.Sequences[0].Steps.Single(item => item.Id == "step-1");
            Assert.Equal(SequenceStepAction.CallSubsequence, step.Action);
            Assert.Equal("child-b", step.TargetId); Assert.Equal(string.Empty, step.Parameter); Assert.Equal(0, step.TimeoutMs);
            Assert.Equal("step-1", project.Sequences[0].Steps[0].NextStepId); Assert.Equal("done", step.NextStepId);
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id); Assert.Empty(editor.ValidationIssues);
            Assert.Equal(1, changed); Assert.Equal(1, added); Assert.Equal(0, otherAdded);
            Assert.Equal(string.Empty, editor.SubsequenceStepTargetId); Assert.Null(editor.SubsequenceStepPreview);
            Assert.False(editor.AddSubsequenceStepCommand.CanExecute(null)); editor.AddSubsequenceStepCommand.Execute(null);
            Assert.Equal(3, project.Sequences[0].Steps.Count);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("removed-target")]
    [InlineData("renamed-id")]
    [InlineData("duplicate-target")]
    [InlineData("blank-id")]
    [InlineData("self")]
    [InlineData("foreign-axis")]
    [InlineData("removed-caller")]
    [InlineData("foreign-caller")]
    [InlineData("template-removed")]
    [InlineData("disposed")]
    public Task SubsequenceAddRejectsUnavailableOrAmbiguousContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SubsequenceStepTargetId = "child-b";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "removed-target": project.Sequences.RemoveAt(2); break;
                case "renamed-id": project.Sequences[2].Id = "new-child"; break;
                case "duplicate-target": project.Sequences.Add(new SequenceDefinition { Id = "child-b" }); break;
                case "blank-id": project.Sequences[2].Id = " "; editor.SubsequenceStepTargetId = " "; break;
                case "self": editor.SubsequenceStepTargetId = project.Sequences[0].Id; break;
                case "foreign-axis": editor.SubsequenceStepTargetId = "axis"; break;
                case "removed-caller": project.Sequences.RemoveAt(0); break;
                case "foreign-caller": editor.SelectedSequence = new SequenceDefinition { Id = "sequence", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; editor.SubsequenceStepTargetId = "child-b"; break;
                case "template-removed": editor.Templates.Clear(); break;
                case "disposed": editor.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddSubsequenceStepCommand.CanExecute(null)); editor.AddSubsequenceStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task SubsequenceRefreshSequenceAndSameIdLoadResetOnlyTransientChoices() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SubsequenceStepTargetId = "child-b";
            project.Sequences[2].Name = "새 호출 대상"; editor.RefreshAuthoringTargets();
            Assert.Equal("새 호출 대상 (child-b)", editor.SubsequenceStepTarget!.Name); Assert.Equal("child-b", editor.SubsequenceStepPreview!.TargetId);
            editor.SelectedSequence = project.Sequences[1]; Assert.Equal(string.Empty, editor.SubsequenceStepTargetId);
            Assert.DoesNotContain(editor.SubsequenceStepTargets, target => target.Id == "child-a");
            editor.SubsequenceStepTargetId = "child-b";
            var replacement = JsonSerializer.Deserialize<MachineProjectDocument>(JsonSerializer.Serialize(project))!;
            string before = JsonSerializer.Serialize(replacement); editor.Load(replacement);
            Assert.Equal(string.Empty, editor.SubsequenceStepTargetId); Assert.Null(editor.SubsequenceStepPreview);
            Assert.Equal(before, JsonSerializer.Serialize(replacement));
            editor.SubsequenceStepTargetId = "child-b"; replacement.Sequences.RemoveAt(2); editor.RefreshAuthoringTargets();
            Assert.Equal(string.Empty, editor.SubsequenceStepTargetId); Assert.False(editor.AddSubsequenceStepCommand.CanExecute(null));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("removed-target")]
    [InlineData("duplicate-target")]
    [InlineData("renamed-id")]
    [InlineData("sequence-switch")]
    [InlineData("same-id-load")]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("template-removed")]
    public Task SubsequenceAddRevalidatesContextAfterExistingDraftAdmission(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        var replacement = CreateProject();
        try
        {
            editor.Load(project); editor.SubsequenceStepTargetId = "child-b"; editor.SelectedStepDraft!.Parameter = "0.2";
            int added = 0; editor.SubsequenceStepAdded += (_, _) => added++;
            editor.PendingStepDraftPrompt = () =>
            {
                switch (boundary)
                {
                    case "removed-target": project.Sequences.RemoveAt(2); break;
                    case "duplicate-target": project.Sequences.Add(new SequenceDefinition { Id = "child-b" }); break;
                    case "renamed-id": project.Sequences[2].Id = "new-child"; break;
                    case "sequence-switch": editor.DiscardStepDraft(); editor.SelectedSequence = project.Sequences[1]; editor.SubsequenceStepTargetId = "child-b"; break;
                    case "same-id-load": editor.Load(replacement); editor.SubsequenceStepTargetId = "child-b"; break;
                    case "readonly": editor.IsEditable = false; break;
                    case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                    case "template-removed": editor.Templates.Clear(); break;
                }
                return PlacementDraftDecision.Discard;
            };
            Assert.True(editor.AddSubsequenceStepCommand.CanExecute(null)); editor.AddSubsequenceStepCommand.Execute(null);
            Assert.Equal(0, added); Assert.Equal(2, project.Sequences[0].Steps.Count); Assert.Equal(2, replacement.Sequences[0].Steps.Count);
            Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter);
            return true;
        }
        finally { editor.DiscardStepDraft(); editor.Dispose(); }
    });

    [Fact]
    public Task AuthoredCallLeavesCycleDiagnosisWithExistingProjectCompiler() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            project.Sequences[2].Steps.Insert(0, new() { Id = "return-call", Action = SequenceStepAction.CallSubsequence, TargetId = "sequence" });
            editor.Load(project); editor.SubsequenceStepTargetId = "child-b"; editor.AddSubsequenceStepCommand.Execute(null);
            var result = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project);
            Assert.False(result.IsSuccess); Assert.Contains(result.Errors, error => error.Code == MachineProjectRuntimeCompilationErrorCode.SubsequenceCompositionInvalid && error.Message.Contains("SubsequenceCycle"));
            editor.DeleteStepCommand.Execute(null);
            Assert.True(new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project).IsSuccess);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedSubsequenceSelectionPreviewAddAndInspectorApplyStaySeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            var clock = main.SceneSnapshots.Latest!.SimulationTime; var signals = JsonSerializer.Serialize(main.SceneSnapshots.Latest.Signals);
            var positions = main.SceneSnapshots.Latest.Axes.Select(axis => axis.Position).ToArray();
            var captures = JsonSerializer.Serialize(main.SceneSnapshots.Latest.Cameras);
            string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft; bool dirtyBefore = main.HasUnsavedChanges;
            WithDialog(window, dialog =>
            {
                Assert.Same(editor, dialog.DataContext);
                var area = (Expander)dialog.FindName("SubsequenceStepAuthoring"); Assert.False(area.IsExpanded); area.IsExpanded = true; WaitForSubsequenceContent(dialog, area);
                var target = (ComboBox)dialog.FindName("SubsequenceStepTarget"); var add = (Button)dialog.FindName("AddSubsequenceStepButton");
                Assert.Equal(2, target.Items.Count); Assert.False(add.IsEnabled); Assert.Null(target.SelectedValue);
                target.SetCurrentValue(Selector.SelectedValueProperty, "child-b"); Pump(dialog);
                Assert.Equal("child-b", editor.SubsequenceStepTargetId); Assert.True(add.IsEnabled);
                editor.SubsequenceStepTargetId = "child-a"; Pump(dialog); Assert.Equal("child-a", target.SelectedValue);
                target.SetCurrentValue(Selector.SelectedValueProperty, "child-b"); Pump(dialog);
                Assert.Equal("검사 준비 (child-b)", ((TextBlock)dialog.FindName("SubsequenceStepPreviewTarget")).Text.Replace("\u2060", string.Empty));
                Assert.Equal("0", ((TextBlock)dialog.FindName("SubsequenceStepPreviewTimeout")).Text);
                Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
                Assert.False(editor.HasPendingStepDraft || main.IsRunning); Assert.Equal(dirtyBefore, main.HasUnsavedChanges);
                Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(signals, JsonSerializer.Serialize(main.SceneSnapshots.Latest.Signals));
                Assert.Equal(positions, main.SceneSnapshots.Latest.Axes.Select(axis => axis.Position)); Assert.Equal(captures, JsonSerializer.Serialize(main.SceneSnapshots.Latest.Cameras));
                editor.IsEditable = false; Pump(dialog); Assert.False(target.IsEnabled || add.IsEnabled); Capture(dialog, $"subsequence-readonly-{language}-{fullArea}", false);
                editor.IsEditable = true; Pump(dialog);
                var camera = (Expander)dialog.FindName("CameraStepAuthoring"); double cameraHeight = camera.ActualHeight;
                camera.IsExpanded = true; WaitForSubsequenceContent(dialog, area); Capture(dialog, $"subsequence-both-{language}-{fullArea}", false);
                camera.IsExpanded = false; WaitForSubsequenceContent(dialog, area, cameraHeight);
                Capture(dialog, $"subsequence-preview-{language}-{fullArea}", false);
                Assert.Same(editor.AddSubsequenceStepCommand, add.Command); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("child-b", created.TargetId); Assert.Equal(string.Empty, created.Parameter); Assert.Equal(0, created.TimeoutMs);
            Assert.Equal("done", created.NextStepId); Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Empty(editor.ValidationIssues);
            Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime);
            Capture(window, $"subsequence-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            editor.SelectedStepDraft!.TargetId = "child-a"; Assert.Equal("child-b", created.TargetId); Assert.True(editor.HasPendingStepDraft);
            editor.DiscardStepDraft(); Assert.Equal("child-b", editor.SelectedStepDraft!.TargetId);
            editor.SelectedStepDraft.TargetId = "child-a";
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
            Assert.Equal("child-a", created.TargetId); Assert.False(editor.HasPendingStepDraft || main.IsRunning);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task SubsequenceDialogCancelRetainsChoiceAndDraftUntilExplicitSuccess(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2"; editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel; string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("SubsequenceStepAuthoring"); area.IsExpanded = true; WaitForSubsequenceContent(dialog, area);
                ((ComboBox)dialog.FindName("SubsequenceStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "child-b"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddSubsequenceStepButton")); Pump(window);
                Assert.True(dialog.IsVisible && editor.HasPendingStepDraft); Assert.Equal("child-b", editor.SubsequenceStepTargetId); Assert.Equal(before, JsonSerializer.Serialize(project));
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
                Invoke((Button)dialog.FindName("AddSubsequenceStepButton")); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter); Assert.True(window.IsVisible);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongSubsequenceNamesAndContextReplacementKeepContentAndResultLifetime(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Sequences[2].Name = language == OpenVisionLanguage.Korean
            ? string.Join(" ", Enumerable.Repeat("두 번째 검사 위치의 제품 감지와 촬영 준비 시퀀스", 7)) : string.Join(" ", Enumerable.Repeat("Second inspection position detection and camera preparation sequence", 7));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("SubsequenceStepAuthoring"); area.IsExpanded = true; WaitForSubsequenceContent(dialog, area);
                ((ComboBox)dialog.FindName("SubsequenceStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "child-b"); Pump(dialog);
                var target = (TextBlock)dialog.FindName("SubsequenceStepPreviewTarget"); Assert.Equal(project.Sequences[2].Name + " (child-b)", target.Text.Replace("\u2060", string.Empty));
                target.Measure(new Size(target.ActualWidth, double.PositiveInfinity)); Assert.True(target.DesiredSize.Height <= target.ActualHeight + target.Margin.Top + target.Margin.Bottom + 1);
                Capture(dialog, $"subsequence-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.SubsequenceStepTargetId = "child-b";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog);
                    main.SequenceEditor.AddSubsequenceStepCommand.Execute(null); Pump(window); Assert.True(dialog.IsVisible);
                    var add = (Button)dialog.FindName("AddSubsequenceStepButton"); add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                    replacement.SubsequenceStepTargetId = "child-b"; replacement.AddSubsequenceStepCommand.Execute(null); Assert.Equal(4, replacement.SelectedSequence!.Steps.Count);
                }
                finally { replacement.Dispose(); }
            });
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task EmptySubsequenceCandidatesDisableAddAndRecoverAfterExplicitSelection(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); var child = project.Sequences[2]; project.Sequences.RemoveRange(1, 2);
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("SubsequenceStepAuthoring"); area.IsExpanded = true; WaitForSubsequenceContent(dialog, area);
                var target = (ComboBox)dialog.FindName("SubsequenceStepTarget"); var add = (Button)dialog.FindName("AddSubsequenceStepButton");
                Assert.Empty(target.Items); Assert.False(add.IsEnabled); Capture(dialog, $"subsequence-empty-{language}", false);
                project.Sequences.Add(child); main.SequenceEditor.RefreshAuthoringTargets(); Pump(dialog); Assert.False(add.IsEnabled);
                target.SetCurrentValue(Selector.SelectedValueProperty, "child-b"); Pump(dialog); Assert.True(add.IsEnabled); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal("child-b", project.Sequences[0].Steps.Single(step => step.Id == "step-1").TargetId);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Sequences[0].Steps = [new() { Id = "feed", Name = "준비 대기", Action = SequenceStepAction.Delay, Parameter = "0.1" }, new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }];
        project.Sequences.Add(new SequenceDefinition { Id = "child-a", Name = "검사 준비", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] });
        project.Sequences.Add(new SequenceDefinition { Id = "child-b", Name = "검사 준비", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] });
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

    private static void CloseFixture(MainViewModel main, Window window, OpenVisionLanguage previous)
    {
        main.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close();
        Pump(window);
        var remaining = Application.Current.Windows.Cast<Window>().ToArray();
        var folder = Path.Combine(TestStorage.RootPath, "subsequence-authoring"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window),
            window.IsVisible, dirty = main.HasUnsavedChanges, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible);
        OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static void WaitForSubsequenceContent(Window window, Expander expander, double? cameraCollapsedHeight = null)
    {
        var names = new[] { "SubsequenceStepTarget", "SubsequenceStepAction", "SubsequenceStepPreviewTarget", "SubsequenceStepPreviewTimeout", "SubsequenceStepHint", "AddSubsequenceStepButton" };
        var probes = Descendants((DependencyObject)expander.Content).OfType<FrameworkElement>().Where(element => names.Contains(element.Name)).ToArray();
        bool Ready(out string signature)
        {
            signature = expander.TransformToAncestor(window).TransformBounds(new Rect(expander.RenderSize)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + window.ActualHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + window.Top.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (window is SequenceStepTemplateDialog dialog && dialog.FindName("CameraStepAuthoring") is Expander camera)
            {
                if (cameraCollapsedHeight is { } height && (camera.IsExpanded || Math.Abs(camera.ActualHeight - height) > 1e-6)) return false;
                signature += camera.ActualHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                if (camera.IsExpanded && camera.Content is FrameworkElement content)
                {
                    var cameraBounds = new Rect(camera.RenderSize); cameraBounds.Inflate(1e-6, 1e-6);
                    if (!cameraBounds.Contains(content.TransformToAncestor(camera).TransformBounds(new Rect(content.RenderSize)))) return false;
                    for (DependencyObject? item = content; item is not null && item != camera; item = VisualTreeHelper.GetParent(item))
                    {
                        if (item is UIElement element && element.Opacity < 0.99) return false;
                    }
                }
            }
            if (probes.Length != names.Length) return false;
            var outer = new Rect(expander.RenderSize); outer.Inflate(1e-6, 1e-6);
            foreach (var probe in probes)
            {
                if (probe is TextBlock { Text.Length: 0 }) continue;
                Rect bounds = probe.TransformToAncestor(expander).TransformBounds(new Rect(probe.RenderSize));
                if (!probe.IsVisible || probe.ActualHeight <= 0 || !outer.Contains(bounds)) return false;
                signature += bounds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (DependencyObject? item = probe; item is not null && item != expander; item = VisualTreeHelper.GetParent(item))
                {
                    if (item is UIElement element)
                    {
                        if (probe.IsEnabled && element.Opacity < 0.99) return false;
                        signature += element.Opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (item is not Visual visual || VisualTreeHelper.GetClip(visual) is not { } clip) continue;
                    var clipBounds = clip.Bounds; clipBounds.Inflate(1e-6, 1e-6);
                    if (!clipBounds.Contains(probe.TransformToAncestor(visual).TransformBounds(new Rect(probe.RenderSize)))) return false;
                }
            }
            return true;
        }
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        string previous = string.Empty; int stable = 0, ticks = 0;
        EventHandler handler = (_, _) =>
        {
            window.UpdateLayout(); bool ready = Ready(out string signature);
            stable = ready && signature == previous ? stable + 1 : 0; previous = signature;
            if (stable >= 2 || ++ticks >= 125) frame.Continue = false;
        };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(stable >= 2 && Ready(out _), "Subsequence section must finish revealing before capture; no product animation or native-input acceptance change.");
        probes.Single(probe => probe.Name == "AddSubsequenceStepButton").BringIntoView(); Pump(window);
        var scroll = Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is Panel panel && panel.Children.Contains(expander));
        var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (var probe in probes)
        {
            if (probe is TextBlock { Text.Length: 0 }) continue;
            Assert.True(viewport.Contains(probe.TransformToAncestor(presenter).TransformBounds(new Rect(probe.RenderSize))), probe.Name + " must be fully visible after scrolling to the subsequence action.");
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        if (window is SequenceStepTemplateDialog dialog && dialog.FindName("SubsequenceStepAuthoring") is Expander { IsExpanded: true } expander) WaitForSubsequenceContent(window, expander);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "subsequence-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor, JsonSerializer.Serialize(monitor));
        var area = monitor.WorkingArea.Split(',').Select(int.Parse).ToArray(); var rect = monitor.WindowRect.Split(',').Select(int.Parse).ToArray();
        Assert.True(new Rect(area[0], area[1], area[2], area[3]).Contains(new Rect(rect[0], rect[1], rect[2], rect[3])), "Owned capture must stay inside the actual work area: " + JsonSerializer.Serialize(monitor));
    }
}
