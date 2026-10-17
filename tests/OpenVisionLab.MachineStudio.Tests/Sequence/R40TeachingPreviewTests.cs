using System.IO;
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
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40TeachingPreviewTests(StudioUiTestHost ui)
{
    private const string LongName = "가장 긴 티칭 위치 이름을 조회하는 검사 위치 이름을 끝까지 확인하는 시험 위치";

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyTeachingChoiceStillFillsOnlyDraft(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, fullArea);
        try
        {
            var editor = main.SequenceEditor;
            editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); Pump(window);
            var inspector = Inspector(window);
            string before = JsonSerializer.Serialize(project);
            ((ComboBox)inspector.FindName("StepDraftTeaching")).SetCurrentValue(Selector.SelectedValueProperty, "검사 위치"); Pump(window);
            Assert.Equal("80", editor.SelectedStepDraft!.Parameter);
            Assert.Equal("20", project.Sequences[0].Steps[1].Parameter);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.True(editor.HasPendingStepDraft);
            Assert.False(main.HasUnsavedChanges || main.IsRunning);
            editor.DiscardStepDraft(); Pump(window);
            Capture(window, $"legacy-teaching-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { main.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Fact]
    public Task LookupAndCopyKeepDefinitionWritesAtExistingApply() => ui.InvokeAsync(() =>
    {
        var project = CreateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1");
            int writes = 0; editor.DefinitionChanged += (_, _) => writes++;
            string before = JsonSerializer.Serialize(project);
            var selected = editor.SelectedStep;
            editor.TeachingSearchText = "검사 위치"; editor.TeachingPreviewName = "검사 위치";
            Assert.Equal(new[] { "검사 위치", "검사 위치 보정", LongName }, editor.TeachingPositions.Select(position => position.Key));
            Assert.Equal("80 mm", editor.TeachingPreviewValue);
            Assert.Equal(string.Empty, editor.StepTeachingName);
            Assert.False(editor.HasPendingStepDraft);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Equal(0, writes);
            Assert.True(editor.CopyTeachingPreviewCommand.CanExecute(null)); editor.CopyTeachingPreviewCommand.Execute(null);
            Assert.Equal("80", editor.SelectedStepDraft!.Parameter); Assert.Equal("검사 위치", editor.StepTeachingName);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Equal(0, writes);
            editor.SelectedStepDraft.Parameter = "81"; editor.CopyTeachingPreviewCommand.Execute(null);
            Assert.Equal("80", editor.SelectedStepDraft.Parameter);
            editor.DiscardStepDraft();
            Assert.Equal(string.Empty, editor.TeachingSearchText); Assert.Equal(string.Empty, editor.TeachingPreviewName);
            Assert.False(editor.CopyTeachingPreviewCommand.CanExecute(null)); Assert.False(editor.HasPendingStepDraft);
            editor.TeachingPreviewName = "검사 위치"; editor.CopyTeachingPreviewCommand.Execute(null);
            Assert.True(editor.TryApplyStepDraft());
            Assert.Equal("80", project.Sequences[0].Steps[1].Parameter); Assert.Equal(1, writes);
            Assert.Equal(80, project.Axes[0].TeachingPositions!["검사 위치"]);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("target")]
    [InlineData("step")]
    [InlineData("load")]
    [InlineData("dispose")]
    public Task LookupResetsAtTheExistingDraftLifetimeBoundary(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1");
            editor.TeachingSearchText = "같은 위치명"; editor.TeachingPreviewName = "같은 위치명";
            Assert.Equal("44.5 mm", editor.TeachingPreviewValue);
            string before = JsonSerializer.Serialize(project);
            if (boundary == "target") editor.SelectedStepDraft!.TargetId = "axis-2";
            if (boundary == "step") editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-2");
            if (boundary == "load") editor.Load(JsonSerializer.Deserialize<MachineProjectDocument>(before)!);
            if (boundary == "dispose") editor.Dispose();
            Assert.Equal(string.Empty, editor.TeachingSearchText); Assert.Equal(string.Empty, editor.TeachingPreviewName);
            Assert.False(editor.CopyTeachingPreviewCommand.CanExecute(null));
            Assert.Equal(before, JsonSerializer.Serialize(project));
            if (boundary is "target" or "step")
            {
                editor.TeachingPreviewName = "같은 위치명";
                Assert.Equal("-12.75 deg", editor.TeachingPreviewValue);
                Assert.Contains("axis-2", editor.TeachingAxisText);
                editor.CopyTeachingPreviewCommand.Execute(null);
                Assert.Equal("-12.75", editor.SelectedStepDraft!.Parameter);
                Assert.Equal(before, JsonSerializer.Serialize(project));
            }
            if (boundary == "dispose")
            {
                Assert.Empty(editor.TeachingPositions); editor.TeachingSearchText = "ignored"; editor.TeachingPreviewName = "같은 위치명";
                Assert.Equal(string.Empty, editor.TeachingSearchText); Assert.Equal(string.Empty, editor.TeachingPreviewName);
            }
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("removed-position")]
    [InlineData("changed-position")]
    [InlineData("removed-axis")]
    [InlineData("removed-sequence")]
    [InlineData("unsupported-action")]
    [InlineData("empty")]
    public Task UnavailableOrChangedLookupCannotCopyAnOldValue(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); editor.TeachingPreviewName = "검사 위치";
            if (boundary == "readonly") editor.IsEditable = false;
            if (boundary == "removed-position") project.Axes[0].TeachingPositions!.Remove("검사 위치");
            if (boundary == "changed-position") project.Axes[0].TeachingPositions!["검사 위치"] = 81;
            if (boundary == "removed-axis") project.Axes.RemoveAt(0);
            if (boundary == "removed-sequence") project.Sequences.Clear();
            if (boundary == "unsupported-action") editor.SelectedStepDraft!.Action = SequenceStepAction.Delay;
            if (boundary == "empty") project.Axes[0].TeachingPositions!.Clear();
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.CopyTeachingPreviewCommand.CanExecute(null)); editor.CopyTeachingPreviewCommand.Execute(null);
            Assert.Equal("20", editor.SelectedStepDraft!.Parameter); Assert.Equal(before, JsonSerializer.Serialize(project));
            var recovery = CreateProject(); editor.IsEditable = true; editor.Load(recovery);
            editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); editor.TeachingPreviewName = "검사 위치";
            editor.CopyTeachingPreviewCommand.Execute(null); Assert.Equal("80", editor.SelectedStepDraft!.Parameter);
            Assert.True(editor.TryApplyStepDraft()); Assert.Equal("80", recovery.Sequences[0].Steps[1].Parameter);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task LookupUsesTheExistingFiniteNamedPositionContract() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var positions = project.Axes[0].TeachingPositions!;
        positions[""] = 1; positions[" "] = 2; positions["NaN"] = double.NaN; positions["positive infinity"] = double.PositiveInfinity; positions["negative infinity"] = double.NegativeInfinity;
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1");
            Assert.Equal(5, editor.TeachingPositions.Count);
            editor.TeachingSearchText = "83.125"; Assert.Single(editor.TeachingPositions); Assert.Equal("검사 위치 보정", editor.TeachingPositions[0].Key);
            editor.TeachingPreviewName = "NaN"; Assert.False(editor.CopyTeachingPreviewCommand.CanExecute(null));
            Assert.Equal("20", editor.SelectedStepDraft!.Parameter);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("en-US", "83.125 mm")]
    [InlineData("de-DE", "83,125 mm")]
    public Task LookupDisplaysCurrentCultureAndCopiesTheInvariantNumber(string cultureName, string expected) => ui.InvokeAsync(() =>
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(CreateProject()); editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); editor.TeachingPreviewName = "검사 위치 보정";
            Assert.Equal(expected, editor.TeachingPreviewValue); editor.CopyTeachingPreviewCommand.Execute(null);
            Assert.Equal("83.125", editor.SelectedStepDraft!.Parameter);
            return true;
        }
        finally { editor.Dispose(); System.Globalization.CultureInfo.CurrentCulture = previous; }
    });

    [Fact]
    public Task LookupDoesNotUseAForeignSequenceWithTheSameIds() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.SelectedSequence = JsonSerializer.Deserialize<SequenceDefinition>(JsonSerializer.Serialize(project.Sequences[0]))!;
            editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); editor.TeachingPreviewName = "검사 위치";
            Assert.Empty(editor.TeachingPositions); Assert.False(editor.CopyTeachingPreviewCommand.CanExecute(null));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedLookupPreviewCopyAndApplyRemainSeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, fullArea); var editor = main.SequenceEditor;
        try
        {
            editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); Pump(window);
            var inspector = Inspector(window); var lookup = (Expander)inspector.FindName("TeachingPositionLookup");
            lookup.IsExpanded = true; Pump(window);
            var search = (TextBox)inspector.FindName("TeachingPositionSearch"); var list = (ListBox)inspector.FindName("TeachingPositionList");
            var copy = (Button)inspector.FindName("CopyTeachingPreviewButton");
            Assert.Same(editor.CopyTeachingPreviewCommand, copy.Command);
            string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep;
            Assert.NotNull(main.SceneSnapshots.Latest);
            var positions = main.SceneSnapshots.Latest!.Axes.ToDictionary(axis => axis.Id, axis => axis.Position); var clock = main.SceneSnapshots.Latest.SimulationTime;
            search.SetCurrentValue(TextBox.TextProperty, "검사 위치"); Pump(window);
            Assert.Equal(new[] { "검사 위치", "검사 위치 보정", LongName }, list.Items.Cast<KeyValuePair<string, string>>().Select(position => position.Key));
            list.SetCurrentValue(Selector.SelectedValueProperty, "검사 위치"); Pump(window);
            Assert.Equal("검사 위치", editor.TeachingPreviewName); Assert.Equal("80 mm", ((TextBlock)inspector.FindName("TeachingPreviewValue")).Text);
            editor.TeachingPreviewName = "검사 위치 보정"; Pump(window); Assert.Equal("검사 위치 보정", list.SelectedValue);
            list.SetCurrentValue(Selector.SelectedValueProperty, "검사 위치"); Pump(window);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep);
            Assert.Equal("20", editor.SelectedStepDraft!.Parameter); Assert.False(editor.HasPendingStepDraft || main.HasUnsavedChanges || main.IsRunning);
            Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime);
            foreach (var position in positions) Assert.Equal(position.Value, main.SceneSnapshots.Latest.Axes.Single(axis => axis.Id == position.Key).Position);
            lookup.BringIntoView(); Pump(window);
            Capture(window, $"teaching-preview-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            editor.IsEditable = false; Pump(window); Assert.False(copy.IsEnabled || search.IsEnabled);
            Assert.Equal("80 mm", ((TextBlock)inspector.FindName("TeachingPreviewValue")).Text);
            Capture(window, $"teaching-readonly-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            editor.IsEditable = true; Pump(window); Assert.True(copy.IsEnabled); Invoke(copy); Pump(window);
            Assert.Equal("80", editor.SelectedStepDraft.Parameter); Assert.Equal("20", project.Sequences[0].Steps[1].Parameter);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.True(editor.HasPendingStepDraft); Assert.False(main.HasUnsavedChanges || main.IsRunning);
            Capture(window, $"teaching-draft-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
            Assert.Equal("80", project.Sequences[0].Steps[1].Parameter); Assert.False(editor.HasPendingStepDraft || main.IsRunning);
            Assert.True(main.HasUnsavedChanges); Assert.Equal(80, project.Axes[0].TeachingPositions!["검사 위치"]);
            return true;
        }
        finally { editor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongNamesNoMatchesAndRecoveryStayReadableWithoutApplying(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, true); var editor = main.SequenceEditor;
        try
        {
            editor.SelectedStep = editor.Steps.Single(step => step.Id == "move-1"); Pump(window);
            var inspector = Inspector(window); var lookup = (Expander)inspector.FindName("TeachingPositionLookup"); lookup.IsExpanded = true;
            var search = (TextBox)inspector.FindName("TeachingPositionSearch"); var list = (ListBox)inspector.FindName("TeachingPositionList");
            string before = JsonSerializer.Serialize(project);
            search.SetCurrentValue(TextBox.TextProperty, "가장 긴"); Pump(window); Assert.Single(list.Items.Cast<object>());
            list.SetCurrentValue(Selector.SelectedValueProperty, LongName); Pump(window); lookup.BringIntoView(); Pump(window);
            var name = (TextBlock)inspector.FindName("TeachingPreviewName");
            Assert.Equal(LongName, name.Text.Replace("\u2060", string.Empty)); Assert.Contains("\u2060", name.Text);
            Assert.True(name.ActualHeight > name.LineHeight);
            var formatted = new FormattedText(name.Text, System.Globalization.CultureInfo.CurrentCulture, name.FlowDirection,
                new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch), name.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(name).PixelsPerDip)
                { MaxTextWidth = name.ActualWidth, LineHeight = name.LineHeight };
            Assert.True(name.ActualHeight + 1e-6 >= formatted.Height);
            Capture(window, $"teaching-long-{language}-work-area", true);
            search.SetCurrentValue(TextBox.TextProperty, "존재하지 않는 위치"); Pump(window);
            Assert.Empty(list.Items.Cast<object>()); Assert.Equal(string.Empty, editor.TeachingPreviewName);
            Assert.False(((Button)inspector.FindName("CopyTeachingPreviewButton")).IsEnabled);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.False(editor.HasPendingStepDraft || main.HasUnsavedChanges || main.IsRunning);
            Capture(window, $"teaching-empty-{language}-work-area", true);
            search.SetCurrentValue(TextBox.TextProperty, string.Empty); Pump(window); Assert.Equal(5, list.Items.Count);
            list.SetCurrentValue(Selector.SelectedValueProperty, "원점"); Pump(window);
            Invoke((Button)inspector.FindName("CopyTeachingPreviewButton")); Pump(window); Assert.Equal("0", editor.SelectedStepDraft!.Parameter);
            editor.DiscardStepDraft(); Assert.Equal("20", editor.SelectedStepDraft!.Parameter); Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

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
        var folder = Path.Combine(TestStorage.RootPath, "teaching-preview"); Directory.CreateDirectory(folder);
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
