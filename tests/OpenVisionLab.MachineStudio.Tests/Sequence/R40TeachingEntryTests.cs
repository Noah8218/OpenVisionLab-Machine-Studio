using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40TeachingEntryTests(StudioUiTestHost ui)
{
    private const string LongName = "가장 긴 티칭 위치 이름을 조회하는 검사 위치 이름을 끝까지 확인하는 시험 위치";

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyWorkspaceTabsReachBoundAxisSettingsAndReturnWithoutExecuting(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2"); var window = Show(main, fullArea);
        try
        {
            Assert.Equal("stage-2", main.Layout.SelectedItem!.Id); string before = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); bool dirty = main.HasUnsavedChanges;
            Capture(window, $"legacy-step-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            ((RadioButton)window.FindName("EquipmentWorkspaceButton")).SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(window);
            Assert.True(main.Navigation.IsEquipmentWorkspace);
            var drive = Descendants(window).OfType<Button>().Single(button => ReferenceEquals(button.Command, main.OpenEquipmentDriveTabCommand) && button.IsVisible);
            Invoke(drive); Pump(window); Assert.True(main.IsEquipmentDriveTabOpen); Assert.Equal("stage-2", main.Layout.SelectedItem!.Id);
            Assert.NotNull(main.Layout.SelectedComponentEditor); Assert.Single(main.Layout.SelectedComponentEditor!.SettingsFields, field => field.Id == "teach");
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.Equal(dirty, main.HasUnsavedChanges); Assert.False(main.IsRunning);
            Capture(window, $"legacy-settings-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            ReturnToProcess(window); Assert.True(main.Navigation.IsSequenceWorkspace); Assert.Equal("move-2", editor.SelectedStep!.Id); Assert.Equal("0", editor.SelectedStepDraft!.Parameter);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.Equal(dirty, main.HasUnsavedChanges);
            return true;
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task EntrySettingsApplyReturnAndNumericCopyRemainSeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2"); var window = Show(main, fullArea);
        try
        {
            string before = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); bool dirty = main.HasUnsavedChanges;
            var inspector = Inspector(window); var entry = (Button)inspector.FindName("OpenStepAxisSettingsButton"); Assert.Same(main.OpenStepAxisSettingsCommand, entry.Command); Assert.True(entry.IsEnabled);
            AssertEntryVisible(window, inspector); Capture(window, $"entry-step-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            Invoke(entry); Pump(window); Assert.True(main.Navigation.IsEquipmentWorkspace && main.IsEquipmentDriveTabOpen); Assert.Equal("stage-2", main.Layout.SelectedItem!.Id);
            Assert.False(main.IsEquipmentSinglePartCheckOpen || main.OpenStepAxisSettingsCommand.CanExecute(null)); Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.Equal(dirty, main.HasUnsavedChanges);
            Capture(window, $"entry-settings-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            var settings = main.Layout.SelectedComponentEditor!; var field = settings.SettingsFields.Single(item => item.Id == "teach");
            var text = Descendants(window).OfType<TextBox>().Single(box => box.IsVisible && ReferenceEquals(box.DataContext, field));
            string value = "같은 위치명=-12.75; 회전 검사 위치=42.25"; text.SetCurrentValue(TextBox.TextProperty, value); Pump(window);
            Assert.Equal(value, field.Value); Assert.True(settings.HasPendingSettingsDraft); Assert.Equal(35.5, project.Axes[1].TeachingPositions!["회전 검사 위치"]); Assert.Equal("0", project.Sequences[0].Steps[2].Parameter);
            var apply = Descendants(window).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, settings.ApplySettingsDraftCommand)); Invoke(apply); Pump(window);
            Assert.Equal(42.25, project.Axes[1].TeachingPositions!["회전 검사 위치"]); Assert.Equal("0", project.Sequences[0].Steps[2].Parameter); Assert.True(main.HasUnsavedChanges); Assert.Equal(runtime, RuntimeState(main));
            ReturnToProcess(window); Assert.True(main.Navigation.IsSequenceWorkspace); Assert.Equal("move-2", editor.SelectedStep!.Id); Assert.Equal("0", editor.SelectedStepDraft!.Parameter); Assert.False(editor.HasPendingStepDraft);
            inspector = Inspector(window); var lookup = (Expander)inspector.FindName("TeachingPositionLookup"); lookup.IsExpanded = true; Pump(window);
            var list = (ListBox)inspector.FindName("TeachingPositionList"); list.SetCurrentValue(Selector.SelectedValueProperty, "회전 검사 위치"); Pump(window);
            Assert.Equal("42.25 deg", editor.TeachingPreviewValue); Assert.Equal("0", editor.SelectedStepDraft.Parameter); Assert.False(editor.HasPendingStepDraft);
            Capture(window, $"entry-return-preview-{language}-{fullArea}", fullArea); Invoke((Button)inspector.FindName("CopyTeachingPreviewButton")); Pump(window);
            Assert.Equal("42.25", editor.SelectedStepDraft.Parameter); Assert.Equal("0", project.Sequences[0].Steps[2].Parameter); Assert.True(editor.HasPendingStepDraft);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window); Assert.Equal("42.25", project.Sequences[0].Steps[2].Parameter); Assert.False(editor.HasPendingStepDraft || main.IsRunning); Assert.Equal(runtime, RuntimeState(main)); return true;
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("other-action")]
    [InlineData("removed-axis")]
    [InlineData("duplicate-axis")]
    [InlineData("missing-stage")]
    [InlineData("duplicate-binding")]
    [InlineData("duplicate-component-id")]
    [InlineData("wrong-kind")]
    [InlineData("changed-binding")]
    [InlineData("inactive-layout")]
    [InlineData("removed-layout")]
    [InlineData("removed-caller")]
    [InlineData("removed-step")]
    [InlineData("duplicate-step-id")]
    [InlineData("foreign-caller")]
    [InlineData("modal")]
    [InlineData("other-workspace")]
    [InlineData("disposed")]
    public Task EntryRejectsUnavailableAmbiguousOrBlockedContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var main = new MainViewModel(project); var editor = main.SequenceEditor; main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2");
        try
        {
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "other-action": editor.SelectedStep = editor.Steps.Single(step => step.Id == "prepare"); break;
                case "removed-axis": project.Axes.RemoveAt(1); break;
                case "duplicate-axis": project.Axes.Add(new() { Id = "axis-2" }); break;
                case "missing-stage": project.Layouts[0].Components.RemoveAll(component => component.Id == "stage-2"); break;
                case "duplicate-binding": project.Layouts[0].Components.Add(new() { Id = "duplicate-stage", Kind = LayoutComponentKind.RotaryStage, BehaviorBindingId = "axis-2" }); break;
                case "duplicate-component-id": project.Layouts[0].Components.Add(new() { Id = "stage-2", Kind = LayoutComponentKind.DigitalIo }); break;
                case "wrong-kind": project.Layouts[0].Components.Single(component => component.Id == "stage-2").Kind = LayoutComponentKind.DigitalIo; break;
                case "changed-binding": project.Layouts[0].Components.Single(component => component.Id == "stage-2").BehaviorBindingId = "axis"; break;
                case "inactive-layout": project.Simulation.ActiveLayoutId = "missing"; break;
                case "removed-layout": project.Layouts.Clear(); break;
                case "removed-caller": project.Sequences.Clear(); break;
                case "removed-step": project.Sequences[0].Steps.RemoveAll(step => step.Id == "move-2"); break;
                case "duplicate-step-id": project.Sequences[0].Steps.Add(new() { Id = "move-2", Action = SequenceStepAction.Complete }); break;
                case "foreign-caller": editor.SelectedSequence = new() { Id = "sequence", Steps = [new() { Id = "move-2", Action = SequenceStepAction.MoveAxis, TargetId = "axis-2" }] }; break;
                case "modal": main.Navigation.OpenTestScenarioCommand.Execute(null); break;
                case "other-workspace": main.Navigation.IsInspectionWorkspace = true; break;
                case "disposed": main.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project); int workspace = main.Navigation.SelectedWorkspaceIndex; var selected = main.Layout.SelectedItem;
            Assert.False(main.OpenStepAxisSettingsCommand.CanExecute(null)); main.OpenStepAxisSettingsCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Equal(workspace, main.Navigation.SelectedWorkspaceIndex); Assert.Same(selected, main.Layout.SelectedItem); return true;
        }
        finally { main.Dispose(); }
    });

    [Theory]
    [InlineData("removed-axis")]
    [InlineData("missing-stage")]
    [InlineData("duplicate-binding")]
    [InlineData("inactive-layout")]
    [InlineData("choice-changed")]
    [InlineData("step-changed")]
    [InlineData("caller-reloaded")]
    [InlineData("readonly")]
    [InlineData("modal")]
    public Task EntryRechecksContextAfterDraftAdmissionAndRecovers(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); using var main = new MainViewModel(project); var editor = main.SequenceEditor; main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2");
        editor.SelectedStepDraft!.Parameter = "1"; int prompts = 0;
        try
        {
            main.PlacementDraftPrompt = () =>
            {
                prompts++;
                switch (boundary)
                {
                    case "removed-axis": project.Axes.RemoveAt(1); break;
                    case "missing-stage": project.Layouts[0].Components.RemoveAll(component => component.Id == "stage-2"); break;
                    case "duplicate-binding": project.Layouts[0].Components.Add(new() { Id = "duplicate-stage", Kind = LayoutComponentKind.RotaryStage, BehaviorBindingId = "axis-2" }); break;
                    case "inactive-layout": project.Simulation.ActiveLayoutId = "missing"; break;
                    case "choice-changed": project.Sequences[0].Steps[2].TargetId = "axis"; break;
                    case "step-changed": editor.DiscardStepDraft(); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); break;
                    case "caller-reloaded": editor.Load(CreateProject()); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2"); break;
                    case "readonly": editor.IsEditable = false; break;
                    case "modal": main.Navigation.OpenTestScenarioCommand.Execute(null); break;
                }
                return PlacementDraftDecision.Discard;
            };
            Assert.True(main.OpenStepAxisSettingsCommand.CanExecute(null)); main.OpenStepAxisSettingsCommand.Execute(null); Assert.Equal(1, prompts); Assert.False(main.Navigation.IsEquipmentWorkspace); Assert.Equal("0", project.Sequences[0].Steps[2].Parameter);
            using var recovered = new MainViewModel(CreateProject()); recovered.Navigation.HideStartupChoice(); recovered.Navigation.IsProcessWorkspace = true; recovered.SequenceEditor.SelectedStep = recovered.SequenceEditor.Steps.Single(step => step.Id == "move-2");
            recovered.OpenStepAxisSettingsCommand.Execute(null); Assert.True(recovered.Navigation.IsEquipmentWorkspace && recovered.IsEquipmentDriveTabOpen); Assert.Equal("stage-2", recovered.Layout.SelectedItem!.Id); return true;
        }
        finally { editor.DiscardStepDraft(); }
    });

    [Fact]
    public Task EntryCancelReentryAndExplicitApplyKeepDraftAdmission() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); using var main = new MainViewModel(project); var editor = main.SequenceEditor; main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2");
        editor.SelectedStepDraft!.Parameter = "1.5"; string before = JsonSerializer.Serialize(project); int prompts = 0;
        main.PlacementDraftPrompt = () => { prompts++; main.OpenStepAxisSettingsCommand.Execute(null); return PlacementDraftDecision.Cancel; };
        main.OpenStepAxisSettingsCommand.Execute(null); Assert.Equal(1, prompts); Assert.True(main.Navigation.IsSequenceWorkspace && editor.HasPendingStepDraft); Assert.Equal(before, JsonSerializer.Serialize(project));
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Apply; main.OpenStepAxisSettingsCommand.Execute(null);
        Assert.True(main.Navigation.IsEquipmentWorkspace && main.IsEquipmentDriveTabOpen); Assert.Equal("1.5", project.Sequences[0].Steps[2].Parameter); Assert.False(editor.HasPendingStepDraft || main.IsRunning); return true;
    });

    [Fact]
    public Task ReturningFromSettingsHonorsCancelInvalidApplyAndDiscard() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps.Single(step => step.Id == "move-2");
        main.OpenStepAxisSettingsCommand.Execute(null); var settings = main.Layout.SelectedComponentEditor!; settings.SettingsFields.Single(field => field.Id == "teach").Value = "malformed";
        string before = JsonSerializer.Serialize(project); main.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel; main.Navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(main.Navigation.IsEquipmentWorkspace && settings.HasPendingSettingsDraft); Assert.Equal(before, JsonSerializer.Serialize(project));
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Apply; main.Navigation.OpenSequenceEditorCommand.Execute(null); Assert.True(main.Navigation.IsEquipmentWorkspace); Assert.True(settings.HasSettingsValidationErrors); Assert.Equal(before, JsonSerializer.Serialize(project));
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Discard; main.Navigation.OpenSequenceEditorCommand.Execute(null); Assert.True(main.Navigation.IsSequenceWorkspace); Assert.Equal("move-2", main.SequenceEditor.SelectedStep!.Id); Assert.False(settings.HasPendingSettingsDraft); Assert.Equal(before, JsonSerializer.Serialize(project)); return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task EntryLongIdentitiesReadonlyAndMissingMappingStayVisible(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Axes[1].Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "두 번째 검사 위치의 회전 목표 축" : "Rotary target axis for the second inspection position", 5));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps.Single(step => step.Id == "move-2"); var window = Show(main, false);
        try
        {
            var inspector = Inspector(window); var button = (Button)inspector.FindName("OpenStepAxisSettingsButton"); AssertEntryVisible(window, inspector); Assert.True(button.IsEnabled); Capture(window, $"entry-long-{language}", false);
            main.SequenceEditor.IsEditable = false; Pump(window); Assert.False(button.IsEnabled); Capture(window, $"entry-readonly-{language}", false);
            main.SequenceEditor.IsEditable = true; Pump(window); Assert.True(button.IsEnabled);
            var component = project.Layouts[0].Components.Single(item => item.Id == "stage-2"); component.BehaviorBindingId = "missing"; main.SequenceEditor.RefreshAuthoringTargets(); Pump(window); Assert.False(button.IsEnabled); Capture(window, $"entry-missing-{language}", false);
            component.BehaviorBindingId = "axis-2"; main.SequenceEditor.RefreshAuthoringTargets(); Pump(window); Assert.True(button.IsEnabled); Invoke(button); Pump(window); Assert.True(main.Navigation.IsEquipmentWorkspace && main.IsEquipmentDriveTabOpen); return true;
        }
        finally { Close(main, window, previous); }
    });

    [Fact]
    public Task EntryIsLockedDuringStepTestAndRecoversAfterActualCompletion() => ui.InvokeTaskAsync(async () =>
    {
        using var main = new MainViewModel(CreateProject()); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var editor = main.SequenceEditor; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2"); int busyEntries = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(editor.IsStepTestBusy) || !editor.IsStepTestBusy) return;
            busyEntries++; Assert.False(main.OpenStepAxisSettingsCommand.CanExecute(null)); main.OpenStepAxisSettingsCommand.Execute(null); Assert.True(main.Navigation.IsSequenceWorkspace);
        };
        try { editor.PropertyChanged += handler; await editor.TestSelectedStepAsync(); Assert.Equal(1, busyEntries); Assert.False(editor.IsStepTestBusy); Assert.True(editor.StepTestResult!.IsCompleted); main.OpenStepAxisSettingsCommand.Execute(null); Assert.True(main.Navigation.IsEquipmentWorkspace); return true; }
        finally { editor.PropertyChanged -= handler; }
    });

    [Fact]
    public Task EntryUsesCurrentDraftAxisAndPreservesNonlinearFlowView() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var editor = main.SequenceEditor; editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2");
        project.Sequences[0].Steps[2].NextStepId = "move-1"; editor.ProcessViewIndex = 1; editor.FlowZoom = 1.2;
        Assert.True(main.OpenStepAxisSettingsCommand.CanExecute(null)); main.OpenStepAxisSettingsCommand.Execute(null); Assert.True(main.Navigation.IsEquipmentWorkspace); Assert.Equal("stage-2", main.Layout.SelectedItem!.Id);
        main.Navigation.OpenSequenceEditorCommand.Execute(null); Assert.True(main.Navigation.IsSequenceWorkspace); Assert.Equal("move-2", editor.SelectedStep!.Id); Assert.Equal(1, editor.ProcessViewIndex); Assert.Equal(1.2, editor.FlowZoom); Assert.Equal("move-1", project.Sequences[0].Steps[2].NextStepId); return true;
    });

    private static void AssertEntryVisible(Window window, SequenceStepInspectorView inspector)
    {
        var entry = (FrameworkElement)inspector.FindName("StepAxisSettingsEntry"); entry.BringIntoView(); Pump(window);
        DependencyObject? ancestor = entry;
        while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
        var scroll = Assert.IsType<ScrollViewer>(ancestor); var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (string name in new[] { "OpenStepAxisSettingsButton", "StepAxisSettingsHint" })
        {
            var item = (FrameworkElement)inspector.FindName(name); Assert.True(item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0);
            Assert.True(viewport.Contains(item.TransformToAncestor(presenter).TransformBounds(new Rect(item.RenderSize))), name);
            if (item is TextBlock text) { text.Measure(new Size(text.ActualWidth, double.PositiveInfinity)); Assert.True(text.DesiredSize.Height <= text.ActualHeight + text.Margin.Top + text.Margin.Bottom + 1); }
        }
    }

    private static string RuntimeState(MainViewModel main) => JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.SimulationTime, main.SceneSnapshots.Latest.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.LayoutComponents, main.SceneSnapshots.Latest.Workpieces, main.SceneSnapshots.Latest.UnitMaterialSlots, main.SceneSnapshots.Latest.UnitMaterialExits });
    private static void ReturnToProcess(Window window) { ((RadioButton)window.FindName("ProcessWorkspaceButton")).SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(window); }
    private static void Close(MainViewModel main, Window window, OpenVisionLanguage previous)
    {
        main.SequenceEditor.DiscardStepDraft(); main.Layout.SelectedComponentEditor?.DiscardInspectorDraft(); window.DataContext = null; window.Close(); Pump(window);
        var remaining = Application.Current.Windows.Cast<Window>().ToArray(); var folder = Path.Combine(TestStorage.RootPath, "teaching-entry"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window), window.IsVisible, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible); OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Axes[0].TeachingPositions = new Dictionary<string, double>
        {
            ["원점"] = 0, ["검사 위치"] = 80, ["검사 위치 보정"] = 83.125, ["같은 위치명"] = 44.5, [LongName] = 120
        };
        project.Axes.Add(new VirtualAxisDefinition
        {
            Id = "axis-2", Name = "회전 축 B", Kind = AxisKind.Rotary, Unit = "deg", HomePosition = 0, SoftLimitMin = -100, SoftLimitMax = 100,
            TeachingPositions = new Dictionary<string, double> { ["같은 위치명"] = -12.75, ["회전 검사 위치"] = 35.5 }
        });
        project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "stage-2", Name = "회전 축 B", Kind = LayoutComponentKind.RotaryStage, BehaviorBindingId = "axis-2" });
        project.Sequences[0].Steps =
        [
            new() { Id = "prepare", Name = "준비 대기", Action = SequenceStepAction.Delay, Parameter = "0.1" },
            new() { Id = "move-1", Name = "첫 촬영 위치", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "20" },
            new() { Id = "move-2", Name = "회전 검사", Action = SequenceStepAction.MoveAxis, TargetId = "axis-2", Parameter = "0" },
            new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }
        ];
        project.Simulation.ActiveLayoutId = "layout";
        project.Name = "축 위치 설정 진입";
        foreach (var axis in project.Axes) axis.Name = "위치 축";
        foreach (var component in project.Layouts[0].Components) component.Name = "위치 축 장치";
        return project;
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760); window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray();
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX; window.Top = area[1] / dpi.DpiScaleY;
            window.Width = area[2] / dpi.DpiScaleX; window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window); return window;
    }

    private static SequenceStepInspectorView Inspector(Window window) => Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        var lookup = Descendants(window).OfType<Expander>().SingleOrDefault(expander => expander.Name == "TeachingPositionLookup" && expander.IsExpanded);
        if (lookup is not null) WaitForTeachingContent(window, lookup);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window); var dpi = VisualTreeHelper.GetDpi(window);
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor);
        var folder = Path.Combine(TestStorage.RootPath, "teaching-entry"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none", method = "Actual in-process WPF RenderTargetBitmap; original full window, no composite/upscale." }));
    }

    private static void WaitForTeachingContent(Window window, Expander lookup)
    {
        var names = new[] { "TeachingAxisContext", "TeachingPositionSearch", "TeachingPositionList", "TeachingPreviewName", "TeachingPreviewValue", "TeachingLookupHint", "CopyTeachingPreviewButton" };
        var probes = Descendants((DependencyObject)lookup.Content).OfType<FrameworkElement>().Where(element => names.Contains(element.Name)).ToArray();
        bool Ready(out string signature)
        {
            signature = string.Empty;
            if (probes.Length != names.Length) return false;
            var outer = new Rect(lookup.RenderSize); outer.Inflate(1e-6, 1e-6);
            foreach (var probe in probes)
            {
                if (probe is TextBlock { Text.Length: 0 }) continue;
                Rect bounds = probe.TransformToAncestor(lookup).TransformBounds(new Rect(probe.RenderSize));
                if (!probe.IsVisible || probe.ActualHeight <= 0 || !outer.Contains(bounds) || bounds.Height < probe.ActualHeight - 1e-6) return false;
                signature += bounds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (DependencyObject? item = probe; item is not null && item != lookup; item = VisualTreeHelper.GetParent(item))
                {
                    if (item is UIElement element)
                    {
                        if (lookup.IsEnabled && element.Opacity < 0.99) return false;
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
            window.UpdateLayout();
            bool ready = Ready(out string signature);
            stable = ready && signature == previous ? stable + 1 : 0; previous = signature;
            if (stable >= 2 || ++ticks >= 125) frame.Continue = false;
        };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(stable >= 2 && Ready(out _), "Teaching lookup children must be fully revealed and stable before capture; native input acceptance is separate.");
    }
}
