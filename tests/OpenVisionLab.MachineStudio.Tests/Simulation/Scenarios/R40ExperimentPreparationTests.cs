using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40ExperimentPreparationTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task RenderInputSourceWithoutApplyingOrStarting(bool fullArea, OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); var window = Show(main, fullArea);
        try
        {
            string definition = JsonSerializer.Serialize(project), runtime = Runtime(main);
            var summary = Descendants(window).OfType<Expander>().SingleOrDefault(item => item.Name == "ExperimentPreparation");
            if (summary is not null)
            {
                WaitForSummary(window, summary);
                Capture(window, $"source-{language}-{fullArea}", fullArea);
                Assert.Equal(!main.Navigation.IsCompactLayout, summary.IsExpanded);
                if (main.Navigation.IsCompactLayout)
                {
                    var editor = View(window).ExperimentVariationEditor;
                    var last = Descendants(editor).OfType<TextBox>().Single(item => ReferenceEquals(item.DataContext, main.Camera.CaptureEditor.VariationParameters[4]));
                    var viewport = new Rect(editor.CameraSourceFieldsScrollViewer.RenderSize); var field = last.TransformToAncestor(editor.CameraSourceFieldsScrollViewer).TransformBounds(new Rect(last.RenderSize));
                    var folder = Path.Combine(TestStorage.RootPath, "experiment-preparation");
                    File.WriteAllText(Path.Combine(folder, $"default-viewport-{language}.json"), JsonSerializer.Serialize(new { summary.IsExpanded, panel = Bounds(window, Named<Border>(View(window), "ExperimentPreparationPanel")).ToString(), summary = summary.RenderSize.ToString(), viewport = viewport.ToString(), field = field.ToString(), editor.CameraSourceFieldsScrollViewer.ScrollableHeight }));
                    Assert.True(viewport.Contains(field));
                }
            }
            Capture(window, $"source-{language}-{fullArea}", fullArea);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main));
            Assert.False(main.IsRunning || main.HasUnsavedChanges || main.Camera.HasCurrentCameraInputs);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task AppliedCameraAndScenarioSummaryFollowActualIdsWithoutActions(bool fullArea, OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); var window = Show(main, fullArea);
        try
        {
            var view = View(window); string definition = JsonSerializer.Serialize(project), runtime = Runtime(main); var selectedStep = main.SequenceEditor.SelectedStepDraft;
            Assert.Same(main, view.DataContext); Assert.Same(main.Camera, view.ExperimentVariationEditor.DataContext);
            AssertSummary(window, main, "camera-a"); Assert.Equal(5, main.Camera.CaptureEditor.VariationParameters.Count);
            Assert.Equal(new[] { "LU", "LU", "°", "°", "LU" }, main.Camera.CaptureEditor.VariationParameters.Select(item => item.Unit));
            Assert.Equal(main.SimulationWorkspace.ScenarioSummary, Text(window, "ExperimentScenarioSummary"));
            Assert.Equal("axis-a", Text(window, "ExperimentScenarioTarget")); Assert.Equal("4", Text(window, "ExperimentRepetitions"));
            Assert.Equal(main.ConditionScenarioStateText, Text(window, "ExperimentScenarioState"));
            Assert.Equal(main.BatchStatusText, Text(window, "ExperimentBatchState"));
            var panel = Named<Border>(view, "ExperimentPreparationPanel"); var editor = view.ExperimentVariationEditor;
            var panelBounds = Bounds(window, panel); var editorBounds = Bounds(window, editor);
            if (main.Navigation.IsCompactLayout) { Assert.True(panelBounds.Top >= editorBounds.Bottom); Assert.InRange(panel.ActualHeight, 1, 180); }
            else Assert.True(panelBounds.Left >= editorBounds.Right);
            AssertVisible(window, editor.CaptureApplySettingsButton); Capture(window, $"summary-{language}-{fullArea}", fullArea);
            SelectCamera(view.ExperimentCameraSelector, "camera-b", window); AssertSummary(window, main, "camera-b");
            main.Camera.SelectedCameraId = "camera-a"; Pump(window); Assert.Equal("camera-a", view.ExperimentCameraSelector.SelectedValue); AssertSummary(window, main, "camera-a");
            foreach (int section in new[] { 0, 2, 1 }) { view.ExperimentCaptureSections.SetCurrentValue(Selector.SelectedIndexProperty, section); Pump(window); Assert.Equal(section, main.Navigation.SelectedExperimentCaptureSectionIndex); }
            Invoke(view.ExperimentScenarioButton); Pump(window); Assert.True(main.Navigation.IsTestScenarioOpen);
            main.Navigation.CloseTestScenarioCommand.Execute(null); Pump(window);
            main.Navigation.IsResultsWorkspace = true; main.Navigation.IsExperimentWorkspace = true; Pump(window); AssertSummary(window, main, "camera-a");
            Named<Expander>(View(window), "ExperimentPreparation").SetCurrentValue(Expander.IsExpandedProperty, false); Pump(window);
            Assert.Same(selectedStep, main.SequenceEditor.SelectedStepDraft); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || main.Camera.HasCurrentCameraInputs);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task PendingFullCaptureDraftStaysSeparateAndInvalidApplyRecovers(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); var window = Show(main, false);
        try
        {
            Expand(window); var view = View(window); var editor = main.Camera.CaptureEditor;
            string definition = JsonSerializer.Serialize(project), scenario = JsonSerializer.Serialize(project.Simulation), runtime = Runtime(main), applied = Text(window, "ExperimentAppliedSource");
            var field = Descendants(view.ExperimentVariationEditor).OfType<TextBox>().Single(item => ReferenceEquals(item.DataContext, editor.VariationParameters[0]));
            SetText(field, "NaN"); Assert.Equal("NaN", editor.VariationParameters[0].Value);
            editor.RecipeName = "pending-capture"; editor.Version = "2"; editor.Parameters[0].Value = "55"; editor.ConfirmVersionCommand.Execute(null); Pump(window);
            Assert.Equal(applied, Text(window, "ExperimentAppliedSource")); Assert.Contains("capture-a", applied); Assert.DoesNotContain("pending-capture", applied);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.False(main.HasUnsavedChanges);
            Invoke(view.ExperimentVariationEditor.CaptureApplySettingsButton); Pump(window);
            Assert.True(editor.HasError); Assert.Equal(definition, JsonSerializer.Serialize(project)); Capture(window, $"invalid-{language}", false);
            SetText(field, "4.5"); Invoke(view.ExperimentVariationEditor.CaptureApplySettingsButton); Pump(window);
            Assert.False(editor.HasError); var saved = project.Devices[0].Camera!.NumericalInput!;
            Assert.Equal(4.5, saved.OffsetX); Assert.Equal(55, saved.FieldOfView); Assert.Equal("pending-capture", saved.RecipeName); Assert.Equal(2, saved.Version);
            Assert.Contains("pending-capture", Text(window, "ExperimentAppliedSource")); Assert.Equal(main.Camera.CurrentCameraInputKindText, Text(window, "ExperimentAppliedSource"));
            Assert.True(main.HasUnsavedChanges); Assert.Equal(scenario, JsonSerializer.Serialize(project.Simulation)); Assert.Equal(runtime, Runtime(main));
            var payload = new ProjectDocumentStore().Save(project); var loaded = new ProjectDocumentStore().Load(payload);
            Assert.Equal(saved, loaded.Devices[0].Camera!.NumericalInput); Assert.False(main.Camera.HasCurrentCameraInputs || main.IsRunning);
            Capture(window, $"applied-{language}", false);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongAppliedNamesAndScenarioDetailsScrollIndependently(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = CreateProject(); project.Devices[0].Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "장시간 편차 확인 카메라" : "Long variation capture camera", 6));
        project.Devices[0].Camera!.NumericalInput = project.Devices[0].Camera!.NumericalInput! with { RecipeName = string.Join("-", Enumerable.Repeat("capture-reference", 4)) };
        using var main = CreateMain(project); var window = Show(main, false);
        try
        {
            string definition = JsonSerializer.Serialize(project), runtime = Runtime(main); Expand(window); var view = View(window); var scroll = Named<ScrollViewer>(view, "ExperimentPreparationScroll");
            Assert.Equal(project.Devices[0].Name, Text(window, "ExperimentAppliedCameraName").Replace("\u2060", "")); Assert.Equal("camera-a", Text(window, "ExperimentAppliedCameraId"));
            Assert.InRange(scroll.ActualHeight, 1, 180); Assert.True(scroll.ScrollableHeight > 0); AssertVisible(window, view.ExperimentVariationEditor.CaptureApplySettingsButton);
            Capture(window, $"long-top-{language}", false); scroll.ScrollToBottom(); Pump(window);
            Assert.True(scroll.VerticalOffset > 0); AssertVisible(window, Named<TextBlock>(view, "ExperimentBatchState")); Capture(window, $"long-bottom-{language}", false);
            scroll.ScrollToTop(); Pump(window); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task EmptyCameraSummaryDoesNotInventAnAppliedTarget(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = new MachineProjectDocument { Id = "empty-experiment" }; using var main = CreateMain(project); var window = Show(main, false);
        try
        {
            string definition = JsonSerializer.Serialize(project), runtime = Runtime(main); Expand(window); var view = View(window);
            Assert.False(view.ExperimentCameraSelector.IsEnabled); Assert.False(Named<StackPanel>(view, "ExperimentAppliedCamera").IsVisible);
            Assert.True(Named<TextBlock>(view, "ExperimentNoAppliedCamera").IsVisible); AssertVisible(window, view.ExperimentOpenEquipmentButton);
            Capture(window, $"empty-{language}", false); Invoke(view.ExperimentOpenEquipmentButton); Pump(window); Assert.True(main.Navigation.IsEquipmentWorkspace);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ReplacingMainDataContextRestoresOnlyTheNewModelsFacts(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); using var empty = CreateMain(new MachineProjectDocument { Id = "replacement-empty" });
        var otherProject = CreateProject(); otherProject.Id = "replacement-valid"; otherProject.Devices[0].Id = "replacement-camera"; otherProject.Sequences.Clear();
        using var other = CreateMain(otherProject); var window = Show(main, false);
        try
        {
            string original = JsonSerializer.Serialize(project), replacement = JsonSerializer.Serialize(otherProject), runtime = Runtime(main);
            window.DataContext = empty; Pump(window); Expand(window); Assert.False(Named<StackPanel>(View(window), "ExperimentAppliedCamera").IsVisible);
            window.DataContext = other; Pump(window); AssertSummary(window, other, "replacement-camera");
            window.DataContext = main; Pump(window); AssertSummary(window, main, "camera-a");
            Assert.Equal(original, JsonSerializer.Serialize(project)); Assert.Equal(replacement, JsonSerializer.Serialize(otherProject)); Assert.Equal(runtime, Runtime(main));
            Assert.False(main.HasUnsavedChanges || empty.HasUnsavedChanges || other.HasUnsavedChanges || main.IsRunning || other.IsRunning);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ExistingScenarioEditingUpdatesOnlyItsOwnSummaryAndProjectContract(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); var window = Show(main, false);
        try
        {
            Expand(window); string cameras = JsonSerializer.Serialize(project.Devices), runtime = Runtime(main), source = Text(window, "ExperimentAppliedSource");
            Invoke(View(window).ExperimentScenarioButton); Pump(window); var overlay = Descendants(window).OfType<SimulationTestWorkspaceView>().Single(item => item.IsVisible);
            var profiles = Assert.IsType<ComboBox>(overlay.FindName("ScenarioProfileComboBox"));
            profiles.SetCurrentValue(Selector.SelectedIndexProperty, 1); Pump(window);
            Assert.Same(profiles.SelectedItem, main.SimulationWorkspace.SelectedScenarioProfile);
            SetText(Assert.IsType<TextBox>(overlay.FindName("BatchRepetitionCountTextBox")), "7"); Pump(window);
            Assert.Equal(7, main.SimulationWorkspace.BatchRepetitionCount); Assert.Equal(7, project.Simulation.TestScenarioBatchRepetitions);
            Assert.Equal(main.SimulationWorkspace.SelectedScenarioProfile.ProfileId, project.Simulation.TestScenarioProfileId); Assert.True(main.HasUnsavedChanges);
            Invoke(Assert.IsType<Button>(overlay.FindName("CloseTestScenarioButton"))); Pump(window);
            Assert.Equal(main.SimulationWorkspace.ScenarioSummary, Text(window, "ExperimentScenarioSummary")); Assert.Equal("7", Text(window, "ExperimentRepetitions"));
            Assert.Equal(source, Text(window, "ExperimentAppliedSource")); Assert.Equal(cameras, JsonSerializer.Serialize(project.Devices)); Assert.Equal(runtime, Runtime(main));
            Assert.False(main.IsRunning || main.Camera.HasCurrentCameraInputs); Capture(window, $"scenario-config-{language}", false);
        }
        finally { Close(main, window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task RunModeShowsReadOnlyFactsWithoutStartingValidation(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); var window = Show(main, false);
        try
        {
            main.IsRunMode = true; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (main.IsModeTransitioning || !main.IsRunMode || main.Camera.CaptureEditor.CanEdit) await Task.Delay(10, timeout.Token); Pump(window);
            string definition = JsonSerializer.Serialize(project), runtime = Runtime(main);
            AssertSummary(window, main, "camera-a"); Assert.False(View(window).ExperimentVariationEditor.CaptureApplySettingsButton.IsEnabled);
            Capture(window, $"readonly-{language}", false); Named<ScrollViewer>(View(window), "ExperimentPreparationScroll").ScrollToBottom(); Pump(window);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning || main.IsBatchRunning || main.ConditionScenario.IsActive);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ExistingExplicitScenarioAndBatchCommandsUpdateTheReadOnlyStatus(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject(); project.Simulation.TestScenarioProfileId = "normal"; project.Simulation.TestScenarioDurationCycles = 10000;
        using var main = CreateMain(project); var window = Show(main, true);
        try
        {
            main.IsRunMode = true; await WaitUntil(() => !main.IsModeTransitioning && main.StartTestScenarioCommand.CanExecute(null)); Expand(window);
            string unconfigured = Text(window, "ExperimentScenarioState"), cameras = JsonSerializer.Serialize(project.Devices);
            main.StartTestScenarioCommand.Execute(null); await WaitUntil(() => main.ConditionScenario.IsActive && main.StopTestScenarioCommand.CanExecute(null)); Pump(window);
            Assert.Equal(main.ConditionScenarioStateText, Text(window, "ExperimentScenarioState")); Assert.NotEqual(unconfigured, Text(window, "ExperimentScenarioState"));
            Capture(window, $"scenario-active-{language}", true);
            main.StopTestScenarioCommand.Execute(null); await WaitUntil(() => !main.ConditionScenario.IsActive && !main.IsRunning);
            main.SimulationWorkspace.ScenarioDurationCycles = 20; main.SimulationWorkspace.BatchRepetitionCount = 2; Pump(window);
            string definition = JsonSerializer.Serialize(project); await WaitUntil(() => main.RunScenarioBatchCommand.CanExecute(null));
            main.RunScenarioBatchCommand.Execute(null); await WaitUntil(() => !main.IsBatchRunning && main.BatchCompletedRuns == 2); Pump(window);
            Assert.Equal(main.BatchStatusText, Text(window, "ExperimentBatchState")); Assert.Equal("2", Text(window, "ExperimentRepetitions")); Assert.True(main.CanAcceptBatchBaseline);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(cameras, JsonSerializer.Serialize(project.Devices)); Assert.False(main.Camera.HasCurrentCameraInputs || main.IsRunning);
            Capture(window, $"batch-complete-{language}", true);
            string runtime = Runtime(main); Named<Expander>(View(window), "ExperimentPreparation").SetCurrentValue(Expander.IsExpandedProperty, false); Pump(window); Expand(window);
            Assert.Equal(runtime, Runtime(main)); Assert.Equal(definition, JsonSerializer.Serialize(project));
        }
        finally { Close(main, window, previous); }
    });

    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static MachineProjectDocument CreateProject() => new()
    {
        Id = "r40-experiment-preparation", Name = "편차 확인",
        Simulation = new() { TestScenarioProfileId = "recovery", TestScenarioSeed = 37, TestScenarioDurationCycles = 80, TestScenarioTargetId = "axis-a", TestScenarioBatchRepetitions = 4 },
        Axes = [new() { Id = "axis-a", Name = "기준 축", HomePosition = 0, SoftLimitMin = -100, SoftLimitMax = 100 }],
        Layouts = [new() { Id = "layout", Name = "편차 배치" }],
        Devices = [new() { Id = "camera-a", Name = "편차 확인 카메라", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { RecipeName = "capture-a", Version = 1, ConfirmedVersion = 1, FieldOfView = 40, AxisId = "axis-a" } } },
            new() { Id = "camera-b", Name = "편차 확인 카메라", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { RecipeName = "capture-b", Version = 1, ConfirmedVersion = 1, InputKind = NumericalCameraInputKind.HeightMap3D, FieldOfView = 40 } } }]
    };
    private static MainViewModel CreateMain(MachineProjectDocument project)
    {
        var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsExperimentWorkspace = true; main.Navigation.SelectedExperimentCaptureSectionIndex = 1; return main;
    }
    private static OpenVisionLanguage Language(OpenVisionLanguage language)
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false); return previous;
    }
    private static void AssertSummary(Window window, MainViewModel main, string cameraId)
    {
        Expand(window);
        Assert.Equal(cameraId, main.Camera.SelectedCameraId); Assert.Equal(cameraId, Text(window, "ExperimentAppliedCameraId"));
        Assert.Equal(main.Camera.SelectedVirtualCamera!.Name, Text(window, "ExperimentAppliedCameraName").Replace("\u2060", ""));
        Assert.Equal(main.Camera.CurrentCameraInputKindText, Text(window, "ExperimentAppliedSource"));
    }
    private static string Text(Window window, string name) => Named<TextBlock>(View(window), name).Text.Replace("\u2060", "");
    private static void Expand(Window window)
    {
        var expander = Named<Expander>(View(window), "ExperimentPreparation"); expander.SetCurrentValue(Expander.IsExpandedProperty, true); Pump(window); WaitForSummary(window, expander);
    }
    private static void WaitForSummary(Window window, Expander expander)
    {
        string Signature() => string.Join("|", Descendants(expander).OfType<UIElement>().Where(item => item.IsVisible).Select(item =>
            FormattableString.Invariant($"{item.RenderSize.Width:R}/{item.RenderSize.Height:R}/{item.Opacity:R}/{item.RenderTransform.Value}/{VisualTreeHelper.GetClip(item)?.Bounds}")))
            + ((ScrollViewer)expander.Content).VerticalOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        string previous = ""; int stable = 0, ticks = 0;
        EventHandler handler = (_, _) => { window.UpdateLayout(); string current = Signature(); stable = current == previous ? stable + 1 : 0; previous = current; if (stable >= 2 || ++ticks >= 125) frame.Continue = false; };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(stable >= 2, "Rendered bounds, transforms, opacity, clip and scroll offset must settle before measuring; no product animation change.");
    }
    private static ExperimentWorkspaceView View(Window window) => Descendants(window).OfType<ExperimentWorkspaceView>().Single(item => item.IsVisible);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().Single(item => item.Name == name);
    private static Rect Bounds(Window window, FrameworkElement item) => item.TransformToAncestor(window).TransformBounds(new Rect(item.RenderSize));
    private static void AssertVisible(Window window, FrameworkElement item) { Assert.True(item.IsVisible); Assert.True(new Rect(window.RenderSize).Contains(Bounds(window, item)), item.Name); }
    private static string Runtime(MainViewModel main) => JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.TickIndex, main.SceneSnapshots.Latest.SimulationTime, main.SceneSnapshots.Latest.RuntimeGeneration, main.SceneSnapshots.Latest.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.LayoutComponents, main.SceneSnapshots.Latest.Workpieces });
    private static void SetText(TextBox field, string value) => Assert.IsAssignableFrom<IValueProvider>(new TextBoxAutomationPeer(field).GetPattern(PatternInterface.Value)).SetValue(value);
    private static void Invoke(Button button) { Assert.True(button.IsEnabled); Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); }
    private static void SelectCamera(ComboBox combo, string id, Window window) { combo.IsDropDownOpen = true; Pump(window); combo.SetCurrentValue(Selector.SelectedValueProperty, id); combo.IsDropDownOpen = false; Pump(window); }
    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false }; SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760); window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray(); var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX; window.Top = area[1] / dpi.DpiScaleY; window.Width = area[2] / dpi.DpiScaleX; window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window); return window;
    }
    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window); var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window); var dpi = VisualTreeHelper.GetDpi(window);
        var summary = Descendants(window).OfType<Expander>().SingleOrDefault(item => item.Name == "ExperimentPreparation" && item.IsVisible);
        if (summary is not null) WaitForSummary(window, summary);
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor); Assert.Equal(96, dpi.PixelsPerInchX);
        var folder = Path.Combine(TestStorage.RootPath, "experiment-preparation"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none", method = "In-process WPF full-window RenderTargetBitmap; no crop/composite/resize/native input acceptance." }));
    }
    private static void Close(MainViewModel main, Window window, OpenVisionLanguage previous)
    {
        main.SequenceEditor.DiscardStepDraft(); main.Layout.SelectedComponentEditor?.DiscardInspectorDraft(); window.DataContext = null; window.Close(); Pump(window);
        var remaining = Application.Current.Windows.Cast<Window>().ToArray(); var folder = Path.Combine(TestStorage.RootPath, "experiment-preparation"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window), window.IsVisible, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible); OpenVisionLanguageService.SetLanguage(previous, save: false);
    }
}
