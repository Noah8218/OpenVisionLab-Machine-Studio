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
using OpenVisionLab.Machine.Core.Devices;
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
public sealed class R40CameraAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyCameraTemplateKeepsItsDefaultTargetAndExplicitAdd(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
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
                var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition { Id: "trigger-camera" });
                Invoke(add); Pump(window);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("camera", created.TargetId);
            Assert.Equal("default", created.Parameter);
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.True(main.HasUnsavedChanges);
            Assert.False(main.IsRunning);
            Assert.Empty(main.SequenceEditor.ValidationIssues);
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Fact]
    public Task CameraPreviewAndExplicitAddUseTheChosenActualIdAndFactoryDefaults() => ui.InvokeAsync(() =>
    {
        var project = CreateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            string before = JsonSerializer.Serialize(project);
            var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            int changed = 0, added = 0, axisAdded = 0;
            editor.DefinitionChanged += (_, _) => changed++;
            editor.CameraStepAdded += (_, _) => added++;
            editor.AxisStepAdded += (_, _) => axisAdded++;
            Assert.Equal(new[] { "camera", "camera-2" }, editor.CameraStepTargets.Select(target => target.Id));
            Assert.Null(editor.CameraStepPreview); Assert.False(editor.AddCameraStepCommand.CanExecute(null));
            editor.CameraStepTargetId = "camera-2";
            var preview = editor.CameraStepPreview!;
            Assert.Equal(SequenceStepAction.TriggerCamera, preview.Action);
            Assert.Equal("camera-2", preview.TargetId); Assert.Equal("default", preview.Parameter); Assert.Equal(0, preview.TimeoutMs);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
            Assert.False(editor.HasPendingStepDraft); Assert.Equal(0, changed + added + axisAdded);
            Assert.True(editor.AddCameraStepCommand.CanExecute(null));
            editor.AddCameraStepCommand.Execute(null);
            var step = project.Sequences[0].Steps.Single(item => item.Id == "step-1");
            Assert.Equal(SequenceStepAction.TriggerCamera, step.Action);
            Assert.Equal("camera-2", step.TargetId); Assert.Equal("default", step.Parameter); Assert.Equal(0, step.TimeoutMs);
            Assert.Equal("step-1", project.Sequences[0].Steps[0].NextStepId);
            Assert.Equal("done", step.NextStepId); Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.Empty(editor.ValidationIssues); Assert.Equal(1, changed); Assert.Equal(1, added); Assert.Equal(0, axisAdded);
            Assert.Equal(string.Empty, editor.CameraStepTargetId); Assert.Null(editor.CameraStepPreview);
            Assert.False(editor.AddCameraStepCommand.CanExecute(null));
            editor.AddCameraStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("removed-camera")]
    [InlineData("kind-changed")]
    [InlineData("duplicate-camera")]
    [InlineData("empty-id")]
    [InlineData("template-removed")]
    [InlineData("removed-sequence")]
    [InlineData("foreign-target")]
    [InlineData("layout-id")]
    [InlineData("foreign-sequence")]
    [InlineData("disposed")]
    public Task CameraAuthoringRejectsUnavailableOrStaleContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.CameraStepTargetId = "camera-2";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "removed-camera": project.Devices.RemoveAt(1); break;
                case "kind-changed": project.Devices[1].Kind = DeviceKind.Sensor; break;
                case "duplicate-camera": project.Devices.Add(new DeviceDefinition { Id = "camera-2", Kind = DeviceKind.Camera }); break;
                case "template-removed": editor.Templates.Clear(); break;
                case "empty-id": project.Devices[1].Id = string.Empty; break;
                case "removed-sequence": project.Sequences.Clear(); break;
                case "foreign-target": editor.CameraStepTargetId = "axis"; break;
                case "layout-id": editor.CameraStepTargetId = "camera-part-2"; break;
                case "foreign-sequence": editor.SelectedSequence = new SequenceDefinition { Id = "sequence", Steps = [new() { Id = "end", Action = SequenceStepAction.Complete }] }; break;
                case "disposed": editor.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddCameraStepCommand.CanExecute(null));
            editor.AddCameraStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task CameraSelectionRefreshLoadAndPromptReentryKeepTheCurrentProjectBoundary() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.CameraStepTargetId = "camera-2";
            project.Layouts[0].Components[1].Name = "실제 두 번째 카메라";
            editor.RefreshAuthoringTargets(); Assert.Equal("실제 두 번째 카메라 (camera-2)", editor.CameraStepTarget!.Name);
            var second = new SequenceDefinition { Id = "other", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] };
            project.Sequences.Add(second); editor.Load(project); editor.CameraStepTargetId = "camera-2";
            editor.SelectedSequence = second; Assert.Equal(string.Empty, editor.CameraStepTargetId); Assert.Null(editor.CameraStepPreview);
            var replacement = JsonSerializer.Deserialize<MachineProjectDocument>(JsonSerializer.Serialize(project))!;
            editor.Load(replacement); Assert.Null(editor.CameraStepPreview);
            editor.CameraStepTargetId = "camera-2"; editor.SelectedStepDraft!.Parameter = "0.2";
            string before = JsonSerializer.Serialize(replacement);
            editor.PendingStepDraftPrompt = () => { editor.Load(project); return PlacementDraftDecision.Discard; };
            editor.AddCameraStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(replacement)); Assert.Equal(2, project.Sequences[0].Steps.Count);
            editor.CameraStepTargetId = "camera-2"; editor.SelectedStepDraft!.Parameter = "0.3";
            editor.PendingStepDraftPrompt = () => { project.Devices.RemoveAt(1); return PlacementDraftDecision.Discard; };
            editor.AddCameraStepCommand.Execute(null);
            Assert.Equal(2, project.Sequences[0].Steps.Count); Assert.Null(editor.CameraStepPreview); Assert.Equal(string.Empty, editor.CameraStepTargetId);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedCameraChoicePreviewAndExplicitAddDoNotAcquireOrApplyAnExistingStep(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            string before = JsonSerializer.Serialize(project);
            var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            var clock = main.SceneSnapshots.Latest!.SimulationTime;
            var cameras = JsonSerializer.Serialize(main.SceneSnapshots.Latest.Cameras);
            var positions = main.SceneSnapshots.Latest.Axes.Select(axis => axis.Position).ToArray();
            Capture(window, $"camera-input-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            WithDialog(window, dialog =>
            {
                var expander = (Expander)dialog.FindName("CameraStepAuthoring"); Assert.False(expander.IsExpanded);
                expander.SetCurrentValue(Expander.IsExpandedProperty, true); WaitForCameraContent(dialog, expander);
                var camera = (ComboBox)dialog.FindName("CameraStepTarget"); var add = (Button)dialog.FindName("AddCameraStepButton");
                Assert.False(add.IsEnabled); Assert.Equal(-1, camera.SelectedIndex);
                camera.SetCurrentValue(Selector.SelectedValueProperty, "camera-2"); Pump(dialog);
                Assert.Equal("camera-2", editor.CameraStepTargetId);
                editor.CameraStepTargetId = "camera"; Pump(dialog); Assert.Equal("camera", camera.SelectedValue);
                camera.SetCurrentValue(Selector.SelectedValueProperty, "camera-2"); Pump(dialog);
                Assert.Equal("검사 카메라 (camera-2)", ((TextBlock)dialog.FindName("CameraStepPreviewTarget")).Text.Replace("\u2060", string.Empty));
                Assert.Equal("default", ((TextBlock)dialog.FindName("CameraStepPreviewRecipe")).Text);
                Assert.Equal("0", ((TextBlock)dialog.FindName("CameraStepPreviewTimeout")).Text);
                Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
                Assert.False(editor.HasPendingStepDraft || main.HasUnsavedChanges || main.IsRunning);
                Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(cameras, JsonSerializer.Serialize(main.SceneSnapshots.Latest.Cameras));
                Assert.Equal(positions, main.SceneSnapshots.Latest.Axes.Select(axis => axis.Position));
                Capture(dialog, $"camera-preview-{language}-{fullArea}", false);
                editor.IsEditable = false; Pump(dialog); Assert.False(camera.IsEnabled || add.IsEnabled);
                Assert.Equal("default", ((TextBlock)dialog.FindName("CameraStepPreviewRecipe")).Text);
                Capture(dialog, $"camera-readonly-{language}-{fullArea}", false);
                editor.IsEditable = true; Pump(dialog);
                Assert.Same(editor.AddCameraStepCommand, add.Command); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("camera-2", created.TargetId); Assert.Equal("default", created.Parameter); Assert.Equal("done", created.NextStepId);
            Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning); Assert.Empty(editor.ValidationIssues);
            Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.All(main.SceneSnapshots.Latest.Cameras, camera => Assert.Equal(0, camera.AcquisitionOrdinal));
            Capture(window, $"camera-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
            editor.SelectedStepDraft!.Parameter = "different-recipe"; Pump(window);
            Assert.Equal("default", created.Parameter); Assert.True(editor.HasPendingStepDraft);
            editor.DiscardStepDraft(); Assert.Equal("default", editor.SelectedStepDraft!.Parameter);
            editor.SelectedStepDraft.Parameter = "inspection-recipe";
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
            Assert.Equal("inspection-recipe", created.Parameter); Assert.False(editor.HasPendingStepDraft || main.IsRunning);
            Assert.All(main.SceneSnapshots.Latest!.Cameras, camera => Assert.Equal(0, camera.AcquisitionOrdinal));
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task CameraDialogCancelRetainsTheDraftAndSuccessClosesOnlyTheOwnedDialog(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2"; editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
            string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                var expander = (Expander)dialog.FindName("CameraStepAuthoring"); expander.IsExpanded = true; WaitForCameraContent(dialog, expander);
                ((ComboBox)dialog.FindName("CameraStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "camera-2"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddCameraStepButton")); Pump(window);
                Assert.True(dialog.IsVisible); Assert.True(editor.HasPendingStepDraft); Assert.Equal("camera-2", editor.CameraStepTargetId);
                Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.False(main.HasUnsavedChanges);
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
                Invoke((Button)dialog.FindName("AddCameraStepButton")); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.True(window.IsVisible); Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongCameraNamesScrollAndContextReplacementDetachesSuccessResults(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject();
        project.Layouts[0].Components[1].Name = language == OpenVisionLanguage.Korean
            ? string.Join(" ", Enumerable.Repeat("두 번째 촬영 위치의 상면 검사 카메라 설정", 7))
            : string.Join(" ", Enumerable.Repeat("Second capture position top inspection camera configuration", 7));
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var expander = (Expander)dialog.FindName("CameraStepAuthoring"); expander.IsExpanded = true; WaitForCameraContent(dialog, expander);
                ((ComboBox)dialog.FindName("CameraStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "camera-2"); Pump(dialog);
                var target = (TextBlock)dialog.FindName("CameraStepPreviewTarget");
                Assert.Equal(project.Layouts[0].Components[1].Name + " (camera-2)", target.Text.Replace("\u2060", string.Empty));
                target.Measure(new Size(target.ActualWidth, double.PositiveInfinity));
                Assert.True(target.DesiredSize.Height <= target.ActualHeight + target.Margin.Top + target.Margin.Bottom + 1);
                Capture(dialog, $"camera-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.CameraStepTargetId = "camera-2";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog);
                    main.SequenceEditor.CameraStepTargetId = "camera-2"; main.SequenceEditor.AddCameraStepCommand.Execute(null); Pump(window);
                    Assert.True(dialog.IsVisible);
                    var add = (Button)dialog.FindName("AddCameraStepButton"); add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                    replacement.CameraStepTargetId = "camera-2"; replacement.AddCameraStepCommand.Execute(null);
                    Assert.Equal(4, replacement.SelectedSequence!.Steps.Count);
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
    public Task EmptyCameraCandidatesDisableAddAndRecoverAfterTargetRefresh(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); var cameraDefinition = project.Devices[1]; project.Devices.Clear();
        var cameraPart = project.Layouts[0].Components[1]; project.Layouts[0].Components.Remove(cameraPart);
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var expander = (Expander)dialog.FindName("CameraStepAuthoring"); expander.IsExpanded = true; WaitForCameraContent(dialog, expander);
                var camera = (ComboBox)dialog.FindName("CameraStepTarget"); var add = (Button)dialog.FindName("AddCameraStepButton");
                Assert.Empty(camera.Items); Assert.False(add.IsEnabled); Assert.Equal("—", ((TextBlock)dialog.FindName("CameraStepPreviewRecipe")).Text);
                Capture(dialog, $"camera-empty-{language}", false);
                project.Devices.Add(cameraDefinition); project.Layouts[0].Components.Add(cameraPart); main.SequenceEditor.RefreshAuthoringTargets(); Pump(dialog);
                camera.SetCurrentValue(Selector.SelectedValueProperty, "camera-2"); Pump(dialog); Assert.True(add.IsEnabled);
                Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal("camera-2", project.Sequences[0].Steps.Single(step => step.Id == "step-1").TargetId);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Devices.Add(new DeviceDefinition { Id = "camera-2", Name = "검사 카메라", Kind = DeviceKind.Camera,
            Camera = new VirtualCameraDefinition { Geometry = new CameraGeometryDefinition(40, 30, 640, 480, "axis", 0, 0) } });
        project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "camera-part-2", Name = "검사 카메라", Kind = LayoutComponentKind.Camera, BehaviorBindingId = "camera-2" });
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

    private static void CloseFixture(MainViewModel main, Window window, OpenVisionLanguage previous)
    {
        main.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close();
        Pump(window);
        var remaining = Application.Current.Windows.Cast<Window>().ToArray();
        var folder = Path.Combine(TestStorage.RootPath, "camera-authoring"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window),
            window.IsVisible, dirty = main.HasUnsavedChanges, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible);
        OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static void WaitForCameraContent(Window window, Expander expander)
    {
        var names = new[] { "CameraStepTarget", "CameraStepPreviewTarget", "CameraStepPreviewRecipe", "CameraStepPreviewTimeout", "CameraStepHint", "AddCameraStepButton" };
        var probes = Descendants((DependencyObject)expander.Content).OfType<FrameworkElement>().Where(element => names.Contains(element.Name)).ToArray();
        bool Ready(out string signature)
        {
            signature = string.Empty;
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
        Assert.True(stable >= 2 && Ready(out _), "Camera section must finish revealing before capture; no product animation or native-input acceptance change.");
        probes.Single(probe => probe.Name == "AddCameraStepButton").BringIntoView(); Pump(window);
        var scroll = Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is Panel panel && panel.Children.Contains(expander));
        var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (var probe in probes)
        {
            if (probe is TextBlock { Text.Length: 0 }) continue;
            Assert.True(viewport.Contains(probe.TransformToAncestor(presenter).TransformBounds(new Rect(probe.RenderSize))), probe.Name + " must be fully visible after scrolling to the camera action.");
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        if (window is SequenceStepTemplateDialog dialog && dialog.FindName("CameraStepAuthoring") is Expander { IsExpanded: true } expander) WaitForCameraContent(window, expander);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "camera-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor, JsonSerializer.Serialize(monitor));
        var area = monitor.WorkingArea.Split(',').Select(int.Parse).ToArray(); var rect = monitor.WindowRect.Split(',').Select(int.Parse).ToArray();
        Assert.True(new Rect(area[0], area[1], area[2], area[3]).Contains(new Rect(rect[0], rect[1], rect[2], rect[3])), "Owned capture must stay inside the actual work area: " + JsonSerializer.Serialize(monitor));
    }
}
