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
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40CaptureWorkspaceTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyCaptureWorkspaceKeepsActualCameraAndImage(OpenVisionLanguage language, bool fullArea) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true;
        var window = Show(main, fullArea);
        try
        {
            await PrepareCaptured(main); Pump(window);
            string definition = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); bool dirty = main.HasUnsavedChanges;
            var view = View(window); var frame = (Border)view.FindName("MmiVirtualCameraImageFrame");
            Assert.True(frame.ActualWidth > 200 && frame.ActualHeight > 200); Assert.NotNull(((Image)view.FindName("MmiNumericalCameraImageViewer")).Source);
            Assert.Equal("camera-a", main.Camera.SelectedCameraId); Capture(window, $"legacy-{language}-{fullArea}", fullArea);
            Named<ListBox>(window, "InspectionCameraList").SetCurrentValue(Selector.SelectedValueProperty, "camera-b"); Pump(window);
            Assert.Equal("camera-b", main.Camera.SelectedCameraId); Assert.NotNull(main.Camera.CurrentNumericalInput); Assert.True(main.Camera.CanShowHeightData);
            Assert.Equal(main.SceneSnapshots.Latest!.Cameras.Single(camera => camera.Id == "camera-b").Result!.NumericalInput!.Frame.FrameId, main.Camera.CurrentNumericalInput!.Frame.FrameId);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.Equal(dirty, main.HasUnsavedChanges);
            Capture(window, $"legacy-height-camera-{language}-{fullArea}", fullArea);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task PreparationDisplaysActualCaptureAndOpensOnlyTheExplicitStep(OpenVisionLanguage language, bool fullArea) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true; var window = Show(main, fullArea);
        try
        {
            await PrepareCaptured(main); Pump(window); var view = View(window); var prep = Named<Expander>(view, "MmiCameraPreparation"); Assert.False(prep.IsExpanded);
            string definition = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); bool dirty = main.HasUnsavedChanges; Rect image = FrameBounds(window);
            Capture(window, $"preparation-collapsed-{language}-{fullArea}", fullArea); await Expand(window);
            AssertPreparation(window, main, "camera-a"); AssertFrame(image, FrameBounds(window));
            Named<ScrollViewer>(view, "MmiPreparationDetails").ScrollToTop(); Pump(window);
            string hint = language == OpenVisionLanguage.Korean ? "설정만 엽니다. 촬영하지 않습니다." : "Opens settings without capturing.";
            Assert.Contains(Descendants(prep).OfType<TextBlock>(), item => item.Text == hint);
            Capture(window, $"preparation-open-{language}-{fullArea}", fullArea);
            var settingsToggle = (ToggleButton)view.FindName("MmiInspectionSettingsToggle"); settingsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(window); await Expand(window);
            Assert.True(main.Navigation.IsInspectionSettingsExpanded && settingsToggle.IsChecked == true); Assert.True(FrameBounds(window).Width > 0);
            Capture(window, $"preparation-settings-wide-{language}-{fullArea}", fullArea);
            settingsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Pump(window); await Expand(window); Assert.False(main.Navigation.IsInspectionSettingsExpanded); AssertFrame(image, FrameBounds(window));
            var source = Named<TextBlock>(view, "MmiPreparationSource"); source.BringIntoView(); Pump(window); Assert.Equal(main.Camera.CurrentCameraInputKindText, source.Text);
            Named<ListBox>(window, "InspectionCameraList").SetCurrentValue(Selector.SelectedValueProperty, "camera-b"); Pump(window); await Expand(window);
            AssertPreparation(window, main, "camera-b"); Assert.Equal("camera-b", ((ComboBox)view.FindName("MmiCameraSelectionComboBox")).SelectedValue);
            Assert.Equal("recipe-b", Named<TextBlock>(view, "MmiPreparationRecipe").Text); Assert.True(main.Camera.CanShowHeightData);
            main.Camera.ShowInputOutputCommand.Execute("height"); Pump(window); Assert.True(((RadioButton)view.FindName("MmiHeightOutputButton")).IsChecked);
            Assert.NotNull(((Image)view.FindName("MmiHeightDataImageViewer")).Source); Assert.Equal(Stretch.Uniform, ((Image)view.FindName("MmiHeightDataImageViewer")).Stretch);
            var scroll = Named<ScrollViewer>(view, "MmiPreparationDetails"); scroll.ScrollToEnd(); Pump(window);
            Assert.Equal(main.Camera.SelectedInputAcquisitionText, Named<TextBlock>(view, "MmiPreparationAcquisition").Text);
            Assert.Equal(main.Camera.CurrentCameraResultText, Named<TextBlock>(view, "MmiPreparationResult").Text); Assert.Equal(main.Camera.CurrentCameraResultSourceText, Named<TextBlock>(view, "MmiPreparationResultSource").Text);
            Capture(window, $"preparation-height-result-{language}-{fullArea}", fullArea);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.Equal(dirty, main.HasUnsavedChanges);
            var button = StepButton(window); Assert.Same(main.OpenCameraAcquisitionStepCommand, button.Command); var reference = Assert.IsType<CameraSequenceStepReference>(button.CommandParameter);
            Assert.Equal((project.Id, "camera-b", "capture-sequence", "capture-b"), (reference.ProjectId, reference.CameraId, reference.SequenceId, reference.StepId));
            button.BringIntoView(); Pump(window); Assert.True(button.IsEnabled); Invoke(button); Pump(window);
            Assert.True(main.Navigation.IsSequenceWorkspace); Assert.Equal("capture-sequence", main.SequenceEditor.SelectedSequence!.Id); Assert.Equal("capture-b", main.SequenceEditor.SelectedStep!.Id);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.Equal(dirty, main.HasUnsavedChanges);
            main.Navigation.IsInspectionWorkspace = true; Pump(window); await Expand(window); Rect expandedFrame = FrameBounds(window); prep.SetCurrentValue(Expander.IsExpandedProperty, false); await Wait(() => !prep.IsExpanded); Pump(window);
            AssertFrame(expandedFrame, FrameBounds(window)); Assert.Equal(runtime, RuntimeState(main));
            Capture(window, $"preparation-return-collapsed-{language}-{fullArea}", fullArea);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongPreparationScrollsAndKeepsAppliedValuesSeparateFromDrafts(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Devices[0].Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "후공정 표면 검사의 긴 카메라 이름" : "Long camera name for surface inspection", 6));
        project.Sequences[0].Steps[0].Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "표면 촬영 확인" : "Surface capture", 4));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true; var window = Show(main, false);
        try
        {
            string definition = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); Rect frame = FrameBounds(window); await Expand(window);
            var view = View(window); var name = Named<TextBlock>(view, "MmiPreparationCameraName"); Assert.Equal(project.Devices[0].Name, name.Text.Replace("\u2060", "")); Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
            var scroll = Named<ScrollViewer>(view, "MmiPreparationDetails"); Assert.InRange(scroll.ActualHeight, 1, 220); Assert.True(scroll.ScrollableHeight > 0);
            Capture(window, $"preparation-long-{language}", false); scroll.ScrollToEnd(); Pump(window); Assert.True(scroll.VerticalOffset > 0); AssertFrame(frame, FrameBounds(window));
            string applied = Named<TextBlock>(view, "MmiPreparationSource").Text; main.Camera.CaptureEditor.RecipeName = "pending-recipe"; Pump(window);
            Assert.Equal(applied, Named<TextBlock>(view, "MmiPreparationSource").Text); Assert.Equal("recipe-a", Named<TextBlock>(view, "MmiPreparationRecipe").Text); Assert.Equal("recipe-a", project.Devices[0].Camera!.NumericalInput!.RecipeName);
            var tabs = (TabControl)view.FindName("MmiInspectionSettingsTabs"); tabs.SetCurrentValue(Selector.SelectedIndexProperty, 3); Pump(window); Assert.False(Named<Grid>(view, "MmiPreparationPanel").IsVisible);
            tabs.SetCurrentValue(Selector.SelectedIndexProperty, 0); Pump(window); await Expand(window); Assert.True(Named<Grid>(view, "MmiPreparationPanel").IsVisible);
            main.Camera.CaptureEditor.RevertCommand.Execute(null); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.False(main.HasUnsavedChanges);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ResetClearsCurrentCaptureWithoutReinterpretingAppliedSource(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true; var window = Show(main, false);
        try
        {
            await PrepareCaptured(main); await Expand(window); long generation = main.SceneSnapshots.Latest!.RuntimeGeneration; string definition = JsonSerializer.Serialize(project);
            Assert.True(main.Camera.HasSelectedCameraInput); Assert.True(main.ResetCommand.CanExecute(null)); main.ResetCommand.Execute(null);
            await Wait(() => !main.Camera.HasSelectedCameraInput && main.SceneSnapshots.Latest!.RuntimeGeneration > generation); Pump(window);
            Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Camera.InputAcquisition"), "—", 0), Named<TextBlock>(View(window), "MmiPreparationAcquisition").Text); Assert.All(main.SceneSnapshots.Latest!.Cameras, camera => Assert.Null(camera.CurrentAcquisitionId)); Assert.Equal(main.Camera.CurrentInputSummaryText, Named<TextBlock>(View(window), "MmiPreparationOrigin").Text);
            Assert.Null(main.Camera.CurrentNumericalInput); Assert.NotNull(project.Devices[0].Camera!.NumericalInput); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.False(main.HasUnsavedChanges);
            Capture(window, $"preparation-reset-{language}", false);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ReplacedDataContextClearsPreviousCameraAndEmptyProject(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        using var first = new MainViewModel(CreateProject()); first.Navigation.HideStartupChoice(); first.Navigation.IsInspectionWorkspace = true; var window = Show(first, false);
        var replacement = CreateProject(); replacement.Id = "replacement-project"; replacement.Devices[0].Name = "새 프로젝트 카메라"; replacement.Sequences[0].Steps.RemoveAll(step => step.Action == SequenceStepAction.TriggerCamera && step.TargetId == "camera-a");
        using var second = new MainViewModel(replacement); second.Navigation.HideStartupChoice(); second.Navigation.IsInspectionWorkspace = true;
        using var empty = new MainViewModel(new MachineProjectDocument()); empty.Navigation.HideStartupChoice(); empty.Navigation.IsInspectionWorkspace = true;
        try
        {
            await Expand(window); window.DataContext = second; first.Dispose(); Pump(window); await Expand(window);
            var view = View(window); Assert.Equal("새 프로젝트 카메라", Named<TextBlock>(view, "MmiPreparationCameraName").Text.Replace("\u2060", "")); Assert.Equal("0", Named<TextBlock>(view, "MmiPreparationStepCount").Text);
            Assert.True(Named<TextBlock>(view, "MmiPreparationNoSteps").IsVisible); Assert.Empty(Named<ItemsControl>(view, "MmiPreparationSteps").Items); Assert.False(second.Camera.HasSelectedCameraInput);
            Capture(window, $"preparation-no-steps-{language}", false);
            window.DataContext = empty; Pump(window); Assert.False(empty.Camera.HasVirtualCamera); Assert.DoesNotContain(Descendants(View(window)).OfType<Expander>(), item => item.Name == "MmiCameraPreparation" && item.IsVisible);
            Capture(window, $"preparation-no-camera-{language}", false);
            window.DataContext = second; Pump(window); await Expand(window); Assert.Equal("0", Named<TextBlock>(View(window), "MmiPreparationStepCount").Text); Assert.False(second.HasUnsavedChanges || empty.HasUnsavedChanges);
        }
        finally { Close(second, window, previous); }
    });

    [Theory]
    [InlineData("removed")]
    [InlineData("changed-target")]
    public Task StaleStepButtonUsesExistingAdmissionAndRecovers(string boundary) => ui.InvokeTaskAsync(async () =>
    {
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true; var window = Show(main, false); var previous = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            await Expand(window); var button = StepButton(window); var reference = Assert.IsType<CameraSequenceStepReference>(button.CommandParameter); var step = project.Sequences[0].Steps[0];
            if (boundary == "removed") project.Sequences[0].Steps.Remove(step); else step.TargetId = "camera-b";
            string definition = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); Assert.False(button.Command!.CanExecute(reference)); button.Command.Execute(reference); Assert.True(main.Navigation.IsInspectionWorkspace);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main));
            if (boundary == "removed") project.Sequences[0].Steps.Insert(0, step); else step.TargetId = "camera-a";
            main.Camera.RefreshProjection(); Pump(window); await Expand(window); button = StepButton(window); Assert.True(button.Command!.CanExecute(button.CommandParameter));
            Invoke(button); Pump(window); Assert.Equal("capture-a", main.SequenceEditor.SelectedStep!.Id); Assert.True(main.Navigation.IsSequenceWorkspace);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ExplicitStepOpenRetainsCancelReentryAndChangedTargetChecks(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false); var project = CreateProject();
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true; main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps.Single(step => step.Id == "capture-a"); var window = Show(main, false);
        try
        {
            await Expand(window); main.SequenceEditor.SelectedStepDraft!.Name = "pending capture name"; string definition = JsonSerializer.Serialize(project); int prompts = 0; var button = StepButton(window);
            main.PlacementDraftPrompt = () => { prompts++; button.Command!.Execute(button.CommandParameter); return PlacementDraftDecision.Cancel; };
            Invoke(button); Pump(window); Assert.Equal(1, prompts); Assert.True(main.Navigation.IsInspectionWorkspace && main.SequenceEditor.HasPendingStepDraft); Assert.Equal(definition, JsonSerializer.Serialize(project));
            main.PlacementDraftPrompt = () => PlacementDraftDecision.Apply; Invoke(button); Pump(window); Assert.True(main.Navigation.IsInspectionWorkspace); Assert.Equal("pending capture name", project.Sequences[0].Steps[0].Name);
            await Expand(window); button = StepButton(window); Invoke(button); Pump(window); Assert.True(main.Navigation.IsSequenceWorkspace); Assert.Equal("capture-a", main.SequenceEditor.SelectedStep!.Id);
            main.Navigation.IsInspectionWorkspace = true; Pump(window); await Expand(window); main.SequenceEditor.SelectedStepDraft!.Name = "second pending name"; button = StepButton(window);
            main.PlacementDraftPrompt = () => { project.Sequences[0].Steps[0].TargetId = "camera-b"; return PlacementDraftDecision.Discard; };
            Invoke(button); Pump(window); Assert.True(main.Navigation.IsInspectionWorkspace); Assert.Equal("pending capture name", project.Sequences[0].Steps[0].Name); Assert.False(main.IsRunning);
        }
        finally { Close(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task SelectedRecipeAndCapturedRecipeKeepTheirOwnIdentity(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false); var project = CreateProject();
        project.Sequences.Add(new() { Id = "alternate-sequence", Name = "대체 촬영 공정", Steps = [
            new() { Id = "alternate-capture-b", Name = "대체 레시피 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera-b", Parameter = "recipe-c" },
            new() { Id = "alternate-done", Name = "완료", Action = SequenceStepAction.Complete }
        ] });
        project.Simulation.AutomaticRun = new() { SequenceId = "capture-sequence", Repeat = false };
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsInspectionWorkspace = true; var window = Show(main, true);
        try
        {
            await PrepareCaptured(main); Named<ListBox>(window, "InspectionCameraList").SetCurrentValue(Selector.SelectedValueProperty, "camera-b"); Pump(window); await Expand(window);
            Assert.Equal(new[] { "recipe-b", "recipe-c" }, main.Camera.CurrentCameraRecipes); Assert.Equal("recipe-b", main.Camera.SelectedCameraRecipe);
            string definition = JsonSerializer.Serialize(project); string runtime = RuntimeState(main); var view = View(window);
            Assert.Equal("recipe-b", Named<TextBlock>(view, "MmiPreparationRecipe").Text); Assert.Contains("recipe-b", Named<TextBlock>(view, "MmiPreparationCaptureContext").Text);
            main.Camera.SelectedCameraRecipe = "recipe-c"; Pump(window); Assert.Equal("recipe-c", Named<TextBlock>(view, "MmiPreparationRecipe").Text);
            Assert.Contains("recipe-b", Named<TextBlock>(view, "MmiPreparationCaptureContext").Text); Assert.DoesNotContain("recipe-c", Named<TextBlock>(view, "MmiPreparationCaptureContext").Text);
            Assert.Contains("camera-b/frame/00000001", Named<TextBlock>(view, "MmiPreparationAcquisition").Text);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, RuntimeState(main)); Assert.False(main.HasUnsavedChanges);
            var context = Named<TextBlock>(view, "MmiPreparationCaptureContext"); context.BringIntoView(); Pump(window);
            Capture(window, $"preparation-recipe-identity-{language}", true);
        }
        finally { Close(main, window, previous); }
    });

    private static Button StepButton(Window window) => Descendants(Named<ItemsControl>(View(window), "MmiPreparationSteps")).OfType<Button>().Single(button => button.DataContext is CameraSequenceStepReference);
    private static Rect FrameBounds(Window window)
    {
        var frame = (Border)View(window).FindName("MmiVirtualCameraImageFrame"); return frame.TransformToAncestor(window).TransformBounds(new Rect(frame.RenderSize));
    }
    private static void AssertFrame(Rect before, Rect after, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Assert.Equal(before.X, after.X, 3); Assert.Equal(before.Y, after.Y, 3); Assert.Equal(before.Width, after.Width, 3); Assert.Equal(before.Height, after.Height, 3);
        var folder = Path.Combine(TestStorage.RootPath, "capture-workspace"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "frame-bounds.jsonl"), JsonSerializer.Serialize(new { caller, language = OpenVisionLanguageService.CurrentLanguage.ToString(), before = before.ToString(System.Globalization.CultureInfo.InvariantCulture), after = after.ToString(System.Globalization.CultureInfo.InvariantCulture), toleranceDigits = 3 }) + Environment.NewLine);
    }
    private static void AssertPreparation(Window window, MainViewModel main, string cameraId)
    {
        var view = View(window); Assert.Equal(cameraId, main.Camera.SelectedCameraId); Assert.Equal(cameraId, Named<TextBlock>(view, "MmiPreparationCameraId").Text);
        Assert.Equal(main.Camera.SelectedVirtualCamera!.Name, Named<TextBlock>(view, "MmiPreparationCameraName").Text.Replace("\u2060", "")); Assert.Equal("1", Named<TextBlock>(view, "MmiPreparationStepCount").Text);
        Assert.False(Named<TextBlock>(view, "MmiPreparationNoSteps").IsVisible); Assert.Single(Named<ItemsControl>(view, "MmiPreparationSteps").Items);
        Assert.Equal(Stretch.Uniform, ((Image)view.FindName("MmiNumericalCameraImageViewer")).Stretch);
    }
    private static async Task Expand(Window window)
    {
        var expander = Named<Expander>(View(window), "MmiCameraPreparation"); expander.SetCurrentValue(Expander.IsExpandedProperty, true);
        var content = Assert.IsType<ScrollViewer>(expander.Content); await Wait(() => content.IsVisible && content.ActualHeight > 0); Pump(window);
        WaitForPreparation(window, expander); Assert.InRange(content.ActualHeight, 1, 220);
        if (((TabControl)View(window).FindName("MmiInspectionSettingsTabs")).SelectedIndex == 0)
        {
            var apply = Named<Button>(View(window), "CaptureApplySettingsButton"); Assert.True(apply.IsVisible);
            var bounds = apply.TransformToAncestor(window).TransformBounds(new Rect(apply.RenderSize)); Assert.True(new Rect(window.RenderSize).Contains(bounds));
        }
    }

    private static void WaitForPreparation(Window window, Expander expander)
    {
        string Signature() => string.Join("|", Descendants(expander).OfType<UIElement>().Where(item => item.IsVisible).Select(item =>
            FormattableString.Invariant($"{item.RenderSize.Width:R}/{item.RenderSize.Height:R}/{item.Opacity:R}/{item.RenderTransform.Value}/{VisualTreeHelper.GetClip(item)?.Bounds}")))
            + ((ScrollViewer)expander.Content).VerticalOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        string previous = ""; int stable = 0, ticks = 0;
        EventHandler handler = (_, _) =>
        {
            window.UpdateLayout(); string current = Signature(); stable = current == previous ? stable + 1 : 0; previous = current;
            if (stable >= 2 || ++ticks >= 125) frame.Continue = false;
        };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(stable >= 2, "Preparation bounds, rendered transforms, clip and opacity must settle before capture; no product animation or presentation pacing change.");
    }

    private static MachineProjectDocument CreateProject()
    {
        return new MachineProjectDocument
        {
            Id = "r40-capture-preparation", Name = "촬영 준비 확인",
            Devices = [
                new() { Id = "camera-a", Name = "표면 검사 카메라", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { RecipeName = "recipe-a", Version = 1, ConfirmedVersion = 1 } } },
                new() { Id = "camera-b", Name = "표면 검사 카메라", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { InputKind = NumericalCameraInputKind.HeightMap3D, RecipeName = "recipe-b", Version = 1, ConfirmedVersion = 1 } } }
            ],
            Layouts = [new() { Id = "layout", Name = "촬영 배치" }],
            Sequences = [new() { Id = "capture-sequence", Name = "표면 검사 공정", Steps = [
                new() { Id = "capture-a", Name = "표면 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera-a", Parameter = "recipe-a" },
                new() { Id = "wait-a", Name = "판정 대기", Action = SequenceStepAction.WaitVisionResult, TargetId = "camera-a", TimeoutMs = 5000, FailureStepId = "done" },
                new() { Id = "capture-b", Name = "높이 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera-b", Parameter = "recipe-b" },
                new() { Id = "wait-b", Name = "높이 판정 대기", Action = SequenceStepAction.WaitVisionResult, TargetId = "camera-b", TimeoutMs = 5000, FailureStepId = "done" },
                new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }
            ] }]
        };
    }

    private static async Task PrepareCaptured(MainViewModel main)
    {
        Assert.True(main.RunCommand.CanExecute(null)); main.RunCommand.Execute(null);
        await Wait(() => main.SceneSnapshots.Latest!.Cameras.Count(camera => camera.Result is not null) == 2);
        Assert.True(main.PauseCommand.CanExecute(null)); main.PauseCommand.Execute(null); await Wait(() => !main.IsRunning);
    }

    private static async Task Wait(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static string RuntimeState(MainViewModel main) => JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.SimulationTime, main.SceneSnapshots.Latest.TickIndex, main.SceneSnapshots.Latest.RuntimeGeneration, main.SceneSnapshots.Latest.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.LayoutComponents, main.SceneSnapshots.Latest.Workpieces, main.SceneSnapshots.Latest.UnitMaterialSlots, main.SceneSnapshots.Latest.UnitMaterialExits });
    private static MmiOperatorLayoutView View(Window window) => Descendants(window).OfType<MmiOperatorLayoutView>().Single(view => view.IsVisible);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().Single(item => item.Name == name);
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760); window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray(); var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX; window.Top = area[1] / dpi.DpiScaleY; window.Width = area[2] / dpi.DpiScaleX; window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window); return window;
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        var preparation = Descendants(window).OfType<Expander>().SingleOrDefault(item => item.Name == "MmiCameraPreparation" && item.IsVisible);
        if (preparation is not null) WaitForPreparation(window, preparation);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window); var dpi = VisualTreeHelper.GetDpi(window);
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor); Assert.Equal(96, dpi.PixelsPerInchX);
        var folder = Path.Combine(TestStorage.RootPath, "capture-workspace"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none", method = "Actual in-process WPF RenderTargetBitmap; original full window, no composite/upscale." }));
    }

    private static void Close(MainViewModel main, Window window, OpenVisionLanguage previous)
    {
        main.SequenceEditor.DiscardStepDraft(); main.Layout.SelectedComponentEditor?.DiscardInspectorDraft(); window.DataContext = null; window.Close(); Pump(window);
        var remaining = Application.Current.Windows.Cast<Window>().ToArray(); var folder = Path.Combine(TestStorage.RootPath, "capture-workspace"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window), window.IsVisible, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible); OpenVisionLanguageService.SetLanguage(previous, save: false);
    }
}
