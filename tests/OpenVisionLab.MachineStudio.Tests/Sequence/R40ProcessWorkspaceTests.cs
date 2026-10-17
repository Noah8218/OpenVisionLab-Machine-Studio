using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40ProcessWorkspaceTests(StudioUiTestHost ui)
{
    [Fact]
    public async Task EmptyOverviewHasGuidanceAndRecoversWhenTheProjectIsReloaded()
    {
        await ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            var editor = new SequenceEditorViewModel();
            var steps = project.Sequences[0].Steps.ToArray();
            project.Sequences[0].Steps.Clear();
            editor.Load(project);
            var view = new SequenceTaskOverviewView { DataContext = editor };
            view.Measure(new Size(705, 400));
            view.Arrange(new Rect(0, 0, 705, 400));
            view.UpdateLayout();
            Assert.Equal(Visibility.Visible, view.ProcessOverviewEmptyHint.Visibility);
            Assert.Equal(OpenVisionLanguageService.T("R24.EmptyUnitSteps"), view.ProcessOverviewEmptyHint.Text);
            project.Sequences[0].Steps.AddRange(steps);
            editor.Load(project);
            view.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, view.ProcessOverviewEmptyHint.Visibility);
            Assert.Equal(7, editor.TaskGroups.Count);
            editor.Dispose();
            return true;
        });
    }

    [Fact]
    public async Task ApplyingADraftThatChangesCameraDoesNotNavigateThroughTheOldCameraReference()
    {
        await ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            project.Devices.Add(new DeviceDefinition { Id = "other-camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition() });
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsInspectionWorkspace = true;
            main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps[3];
            var row = main.Camera.CurrentAcquisitionSteps.Single(item => item.StepId == "capture-1");
            main.SequenceEditor.SelectedStepDraft!.TargetId = "other-camera";
            main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Apply;
            main.OpenCameraAcquisitionStepCommand.Execute(row);
            Assert.True(main.Navigation.IsInspectionWorkspace);
            Assert.Equal("other-camera", project.Sequences[0].Steps[3].TargetId);
            Assert.True(main.HasUnsavedChanges);
            Assert.False(main.IsRunning);
            return true;
        });
    }

    [Fact]
    public async Task CameraStepReferencesFilterByCameraRejectStaleRowsAndResolveDraftBeforeNavigation()
    {
        await ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            project.Devices.Add(new DeviceDefinition { Id = "other-camera", Name = "다른 카메라", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition() });
            project.Sequences[0].Steps.Insert(10, new SequenceStepDefinition { Id = "other-shot", Name = "다른 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "other-camera", Parameter = "other-recipe" });
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsInspectionWorkspace = true;
            main.Camera.SelectedCameraId = "camera";
            var rows = main.Camera.CurrentAcquisitionSteps;
            Assert.Equal(new[] { "capture-1", "capture-2" }, rows.Select(row => row.StepId));
            Assert.Same(rows, main.Camera.CurrentAcquisitionSteps);
            string before = JsonSerializer.Serialize(project);
            main.Camera.SelectedCameraId = "other-camera";
            Assert.Single(main.Camera.CurrentAcquisitionSteps);
            Assert.False(main.OpenCameraAcquisitionStepCommand.CanExecute(rows[0]));
            main.OpenCameraAcquisitionStepCommand.Execute(rows[0]);
            Assert.True(main.Navigation.IsInspectionWorkspace);
            main.Camera.SelectedCameraId = "camera";
            main.SequenceEditor.SelectedStepDraft!.Parameter = "0.3";
            main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
            main.OpenCameraAcquisitionStepCommand.Execute(rows[1]);
            Assert.True(main.Navigation.IsInspectionWorkspace);
            Assert.Equal("feed", main.SequenceEditor.SelectedStep!.Id);
            Assert.Equal("0.3", main.SequenceEditor.SelectedStepDraft.Parameter);
            main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
            main.OpenCameraAcquisitionStepCommand.Execute(rows[1]);
            Assert.True(main.Navigation.IsProcessWorkspace);
            Assert.Equal("capture-2", main.SequenceEditor.SelectedStep!.Id);
            main.Navigation.IsInspectionWorkspace = true;
            Assert.Equal("camera", main.Camera.SelectedCameraId);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning);
            Assert.False(main.OpenCameraAcquisitionStepCommand.CanExecute(rows[0] with { ProjectId = "other-project" }));
            return true;
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 760)]
    public async Task RenderedCameraStepsOpenTheExactExistingStepWithoutCaptureOrApply(int width, int height)
    {
        await ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsInspectionWorkspace = true;
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            try
            {
                window.Show();
                Pump();
                var steps = Descendants(window).OfType<ItemsControl>().Single(control => control.Name == "InspectionCaptureSteps");
                Assert.Equal(2, steps.Items.Count);
                Assert.True(steps.IsVisible);
                var button = Descendants(steps).OfType<Button>().Single(item => item.CommandParameter is CameraSequenceStepReference { StepId: "capture-2" });
                Assert.Same(main.OpenCameraAcquisitionStepCommand, button.Command);
                Assert.Equal(Assert.IsType<CameraSequenceStepReference>(button.CommandParameter).Caption, AutomationProperties.GetName(button));
                Assert.True(button.IsEnabled && button.IsVisible);
                Assert.True(new Rect(window.RenderSize).Contains(button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize))));
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                Pump();
                Assert.True(main.Navigation.IsProcessWorkspace);
                Assert.Equal("capture-2", main.SequenceEditor.SelectedStep!.Id);
                Assert.False(button.IsEnabled);
                main.Navigation.IsInspectionWorkspace = true;
                Pump();
                Assert.True(steps.IsVisible);
                Assert.True(button.IsEnabled);
                Assert.Equal("camera", main.Camera.SelectedCameraId);
                Assert.False(main.HasUnsavedChanges || main.IsRunning || main.SequenceEditor.IsStepTestBusy);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                return true;
                void Pump() { window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Fact]
    public async Task OverviewSelectionKeepsDraftOnCancelAndDistinguishesEditingFromExecution()
    {
        await ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            using var main = new MainViewModel(project);
            var editor = main.SequenceEditor;
            Assert.Equal(7, editor.TaskGroups.Count);
            string before = JsonSerializer.Serialize(project);
            editor.SelectedStep = editor.Steps[1];
            editor.SelectedStepDraft!.Parameter = "35";
            editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
            editor.SelectOverviewStepCommand.Execute(editor.Steps[3]);
            Assert.Equal("move-1", editor.SelectedStep!.Id);
            Assert.Equal("35", editor.SelectedStepDraft.Parameter);
            Assert.True(editor.TaskGroups[1].IsSelected && editor.TaskGroups[1].IsExpanded);
            editor.IsProcessTable = true;
            editor.IsProcessSceneVisible = true;
            editor.ApplyRuntimeStep("sequence", "capture-2");
            Assert.True(editor.TaskGroups[1].IsSelected);
            Assert.False(editor.TaskGroups[1].IsExecuting);
            Assert.True(editor.TaskGroups[2].IsExecuting);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || editor.IsStepTestBusy);
            editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
            editor.SelectOverviewStepCommand.Execute(editor.Steps[3]);
            Assert.Equal("capture-1", editor.SelectedStep.Id);
            Assert.False(editor.HasPendingStepDraft);
            editor.SelectedStep = editor.Steps[1];
            editor.SelectedStepDraft!.Parameter = "35";
            editor.ApplyStepDraftCommand.Execute(null);
            Assert.Equal("35", project.Sequences[0].Steps[1].Parameter);
            Assert.True(main.HasUnsavedChanges);
            var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
            editor.Load(reopened);
            Assert.Equal("35", reopened.Sequences[0].Steps[1].Parameter);
            Assert.Equal(0, editor.ProcessViewIndex);
            Assert.False(editor.IsProcessSceneVisible);
            return true;
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 760)]
    public async Task RenderedOverviewRoutesExistingSelectionAndViewBindings(int width, int height)
    {
        await ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsProcessWorkspace = true;
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            try
            {
                window.Show();
                Pump();
                var editor = main.SequenceEditor;
                var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
                Assert.True(Descendants(view).OfType<SequenceTaskOverviewView>().Single().IsVisible);
                var parent = Assert.IsType<Grid>(view.Parent);
                Assert.Equal(0, parent.RowDefinitions[1].ActualHeight);
                Assert.True(view.ActualHeight > 400);
                editor.TaskGroups[1].IsExpanded = true;
                Pump();
                var button = Descendants(view).OfType<Button>().First(item => ReferenceEquals(item.CommandParameter, editor.Steps[3]));
                Assert.True(button.IsVisible && button.IsEnabled);
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                Pump();
                Assert.Equal("capture-1", editor.SelectedStep!.Id);
                Assert.True(editor.Steps[3].IsEditingSelection);
                var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
                Assert.Same(editor, inspector.DataContext);
                Assert.Contains(Descendants(inspector).OfType<TextBox>(), field => field.IsVisible && field.Text == editor.SelectedStepDraft!.Name);
                var table = Descendants(view.ProcessViewToolbar).OfType<RadioButton>().Last();
                table.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Pump();
                Assert.True(editor.IsProcessTable && view.StepListDataGrid.IsVisible);
                editor.IsProcessOverview = true;
                editor.IsProcessSceneVisible = true;
                Pump();
                Assert.True(parent.RowDefinitions[1].ActualHeight >= 200);
                Assert.Equal(250, view.ActualHeight);
                editor.IsProcessSceneVisible = false;
                Pump();
                Assert.True(view.ActualHeight > 400);
                Assert.False(main.HasUnsavedChanges);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                return true;

                void Pump() { window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            }
            finally { window.DataContext = null; window.Close(); }
        });
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

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, false)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, false)]
    [InlineData(1280, 760, OpenVisionLanguage.English, false)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, true)]
    public Task RenderProcessWorkspaceWithoutChangingDefinition(int width, int height, OpenVisionLanguage language, bool longName) => RenderWorkspace(width, height, inspection: false, language, longName);

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, false)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, false)]
    [InlineData(1280, 760, OpenVisionLanguage.English, false)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, true)]
    public Task RenderInspectionWorkspaceWithoutChangingDefinition(int width, int height, OpenVisionLanguage language, bool longName) => RenderWorkspace(width, height, inspection: true, language, longName);

    private async Task RenderWorkspace(int width, int height, bool inspection, OpenVisionLanguage language, bool longName)
    {
        await ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            if (longName)
            {
                const string name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 공정 설정";
                project.Name = name;
                project.Sequences[0].Name = name;
                project.Sequences[0].Steps[3].Name = name;
                project.Sequences[0].Steps[6].Name = name;
                project.Devices[0].Name = name;
            }
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            if (inspection) main.Navigation.IsInspectionWorkspace = true;
            else main.Navigation.OpenSequenceEditorCommand.Execute(null);
            string before = JsonSerializer.Serialize(project);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var folder = Path.Combine(TestStorage.RootPath, "process-workspace");
                Directory.CreateDirectory(folder);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = File.Create(Path.Combine(folder, $"{(inspection ? "inspection" : "process")}-{width}x{height}-{language}-{longName}.png"))) encoder.Save(output);
                File.WriteAllText(Path.Combine(folder, $"{(inspection ? "inspection" : "process")}-monitor-{width}-{language}-{longName}.json"), JsonSerializer.Serialize(monitor));
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges);
                return true;
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    internal static MachineProjectDocument CreateProject()
    {
        var steps = new List<SequenceStepDefinition>
        {
            new() { Id = "feed", Name = "투입 대기", Action = SequenceStepAction.Delay, Parameter = "0.1" },
            new() { Id = "move-1", Name = "첫 촬영 위치", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "20" },
            new() { Id = "wait-1", Name = "위치 완료", Action = SequenceStepAction.WaitAxisDone, TargetId = "axis", TimeoutMs = 5000 },
            new() { Id = "capture-1", Name = "첫 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera", Parameter = "inspection-recipe" },
            new() { Id = "move-2", Name = "둘째 촬영 위치", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "80" },
            new() { Id = "wait-2", Name = "위치 완료", Action = SequenceStepAction.WaitAxisDone, TargetId = "axis", TimeoutMs = 5000 },
            new() { Id = "capture-2", Name = "둘째 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera", Parameter = "inspection-recipe" },
            new() { Id = "inspection", Name = "검사 대기", Action = SequenceStepAction.WaitVisionResult, TargetId = "camera", TimeoutMs = 5000, FailureStepId = "unload" },
            new() { Id = "unload", Name = "배출 대기", Action = SequenceStepAction.Delay, Parameter = "0.2" },
            new() { Id = "return", Name = "복귀", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "0" },
            new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }
        };
        return new MachineProjectDocument
        {
            Name = "두 위치 촬영 공정",
            Axes = [new VirtualAxisDefinition { Id = "axis", Name = "촬영 축" }],
            Devices = [new DeviceDefinition { Id = "camera", Name = "검사 카메라", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition { Geometry = new CameraGeometryDefinition(40, 30, 640, 480, "axis", 0, 0) } }],
            Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "촬영 장비", Components = [new LayoutComponentDefinition { Id = "stage", Name = "촬영 축", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = "axis", Size = new Size2D { Width = 200, Height = 40 } }] }],
            Sequences = [new SequenceDefinition { Id = "sequence", Name = "두 위치 촬영 공정", Steps = steps }]
        };
    }
}
