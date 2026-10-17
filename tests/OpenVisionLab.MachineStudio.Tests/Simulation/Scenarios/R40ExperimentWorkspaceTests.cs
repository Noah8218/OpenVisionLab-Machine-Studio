using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40ExperimentWorkspaceTests(StudioUiTestHost ui)
{
    [Fact]
    public async Task RenderNewWorkspaceWithOriginalDefaultCaptureConfigurationAtWorkArea()
    {
        await ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = R40ProcessWorkspaceTests.CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsExperimentWorkspace = true;
            string before = JsonSerializer.Serialize(project);
            var window = Show(main, true);
            try
            {
                Capture(window, "experiment-default-source-work-area");
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.English)]
    public async Task EmptyCameraGuidanceAndLongValuesRemainReachable(bool empty, OpenVisionLanguage language)
    {
        await ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = empty ? new MachineProjectDocument() : R40ProcessWorkspaceTests.CreateProject();
            if (!empty)
            {
                project.Devices[0].Name = string.Join(" ", Enumerable.Repeat("촬영 위치 검사 카메라", 8));
                project.Devices[0].Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = new string('R', 80), AxisId = "axis", FieldOfView = 40 };
            }
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsExperimentWorkspace = true;
            string before = JsonSerializer.Serialize(project);
            var window = Show(main, false);
            try
            {
                var view = Assert.Single(Descendants(window).OfType<ExperimentWorkspaceView>());
                if (empty)
                {
                    Assert.False(view.ExperimentCameraSelector.IsEnabled);
                    Assert.True(view.ExperimentOpenEquipmentButton.IsVisible && view.ExperimentOpenEquipmentButton.IsEnabled);
                    Capture(window, "empty-camera-compact");
                    Invoke(view.ExperimentOpenEquipmentButton);
                    Pump(window);
                    Assert.True(main.Navigation.IsEquipmentWorkspace);
                    Assert.Empty(project.Devices);
                }
                else
                {
                    Assert.InRange(view.ExperimentCameraSelector.ActualWidth, 180, 480);
                    var button = view.ExperimentVariationEditor.CaptureApplySettingsButton;
                    Assert.True(button.IsVisible && button.IsEnabled);
                    Assert.True(new Rect(window.RenderSize).Contains(button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize))));
                    Capture(window, $"long-camera-{language}-compact");
                }
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Fact]
    public async Task NavigationKeepsOldIndicesAndCancelsAWorkspaceChangeWithAPendingDraft()
    {
        await ui.InvokeAsync(() =>
        {
            using var main = new MainViewModel(R40ProcessWorkspaceTests.CreateProject());
            main.Navigation.HideStartupChoice();
            main.Navigation.IsProcessWorkspace = true;
            main.SequenceEditor.SelectedStepDraft!.Parameter = "0.3";
            main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
            main.Navigation.IsExperimentWorkspace = true;
            Assert.True(main.Navigation.IsProcessWorkspace);
            Assert.Equal("0.3", main.SequenceEditor.SelectedStepDraft.Parameter);
            main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
            main.Navigation.IsExperimentWorkspace = true;
            Assert.Equal(4, main.Navigation.SelectedWorkspaceIndex);
            Assert.Equal(4, main.Navigation.SelectedDocumentTabIndex);
            Assert.Equal(3, main.Navigation.SelectedDocumentContentIndex);
            main.Navigation.SelectedExperimentTabIndex = 2;
            main.Navigation.SelectedExperimentCaptureSectionIndex = 0;
            main.Navigation.IsResultsWorkspace = true;
            Assert.Equal(3, main.Navigation.SelectedWorkspaceIndex);
            Assert.Equal(2, main.Navigation.SelectedDocumentContentIndex);
            main.Navigation.IsExperimentWorkspace = true;
            Assert.Equal(2, main.Navigation.SelectedExperimentTabIndex);
            Assert.Equal(0, main.Navigation.SelectedExperimentCaptureSectionIndex);
            main.Navigation.SelectedExperimentTabIndex = 8;
            Assert.Equal(2, main.Navigation.SelectedExperimentTabIndex);
            Assert.False(main.HasUnsavedChanges || main.IsRunning);
            return true;
        });
    }

    [Theory]
    [InlineData(true, 0, OpenVisionLanguage.Korean)]
    [InlineData(true, 1, OpenVisionLanguage.Korean)]
    [InlineData(true, 2, OpenVisionLanguage.Korean)]
    [InlineData(false, 0, OpenVisionLanguage.Korean)]
    [InlineData(false, 1, OpenVisionLanguage.Korean)]
    [InlineData(false, 2, OpenVisionLanguage.Korean)]
    [InlineData(true, 0, OpenVisionLanguage.English)]
    [InlineData(false, 0, OpenVisionLanguage.English)]
    public async Task NewWorkspaceUsesExistingOwnersAndDoesNotApplyOrStartFromSelection(bool fullArea, int tab, OpenVisionLanguage language)
    {
        await ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = R40ProcessWorkspaceTests.CreateProject();
            project.Devices[0].Camera!.NumericalInput = new NumericalCameraSettings { AxisId = "axis", FieldOfView = 40, GenerationDelayMilliseconds = 0, ResponseDelayMilliseconds = 0 };
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsExperimentWorkspace = true;
            main.Navigation.SelectedExperimentTabIndex = tab;
            string before = JsonSerializer.Serialize(project);
            var window = Show(main, fullArea);
            try
            {
                var view = Assert.Single(Descendants(window).OfType<ExperimentWorkspaceView>());
                Assert.Same(main, view.DataContext);
                Assert.Equal(tab, view.ExperimentTabs.SelectedIndex);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RightToolRegionView")).Visibility);
                if (tab == 0) Assert.Same(main.Camera, view.ExperimentVariationEditor.DataContext);
                if (tab == 1)
                {
                    Assert.Same(main.FaultManager, view.ExperimentFaultEditor.DataContext);
                    Assert.True(view.ExperimentFaultEditor.IsEmbedded);
                    Assert.True(double.IsNaN(view.ExperimentFaultEditor.Width));
                    Assert.False(Assert.IsType<Button>(view.ExperimentFaultEditor.FindName("CloseFaultDrawerButton")).IsVisible);
                }
                Capture(window, $"experiment-{tab}-{language}-{(fullArea ? "work-area" : "compact")}");
                Invoke(view.ExperimentScenarioButton);
                Pump(window);
                Assert.True(main.Navigation.IsTestScenarioOpen && main.Navigation.IsExperimentWorkspace);
                Assert.Equal(tab, main.Navigation.SelectedExperimentTabIndex);
                main.Navigation.CloseTestScenarioCommand.Execute(null);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning || main.Camera.HasCurrentCameraInputs);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Fact]
    public async Task VariationRejectsInvalidValuesThenAppliesSavesAndRestoresTheExistingCameraSettings()
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var project = R40ProcessWorkspaceTests.CreateProject();
            project.Devices[0].Camera!.NumericalInput = new NumericalCameraSettings { AxisId = "axis", FieldOfView = 40 };
            project.Devices.Add(new DeviceDefinition { Id = "camera-2", Name = "Other camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition() });
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsExperimentWorkspace = true;
            var window = Show(main, false);
            try
            {
                var view = Assert.Single(Descendants(window).OfType<ExperimentWorkspaceView>());
                var editor = main.Camera.CaptureEditor;
                var field = Descendants(view.ExperimentVariationEditor).OfType<TextBox>().Single(box => ReferenceEquals(box.DataContext, editor.VariationParameters[0]));
                string before = JsonSerializer.Serialize(project);
                SetText(field, "NaN");
                Invoke(view.ExperimentVariationEditor.CaptureApplySettingsButton);
                Pump(window);
                Assert.True(editor.HasError);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges);
                Capture(window, "variation-invalid-compact");
                SetText(field, "4.5");
                Invoke(view.ExperimentVariationEditor.CaptureApplySettingsButton);
                Pump(window);
                Assert.False(editor.HasError);
                Assert.Equal(4.5, project.Devices[0].Camera!.NumericalInput!.OffsetX);
                Assert.True(main.HasUnsavedChanges);
                SelectCamera(view.ExperimentCameraSelector, "camera-2", window);
                Pump(window);
                Assert.Equal("camera-2", main.Camera.SelectedCameraId);
                SelectCamera(view.ExperimentCameraSelector, "camera", window);
                Pump(window);
                Assert.Equal("4.5", main.Camera.CaptureEditor.VariationParameters[0].Value);
                var path = Path.Combine(TestStorage.RootPath, "experiment-settings.ovmachine");
                await main.SaveProjectAsync(path);
                Assert.False(main.HasUnsavedChanges);
                var loaded = new ProjectDocumentStore().Load(File.ReadAllText(path));
                Assert.Equal(4.5, loaded.Devices[0].Camera!.NumericalInput!.OffsetX);
                using var reopened = new MainViewModel(loaded);
                reopened.Navigation.IsExperimentWorkspace = true;
                Assert.Equal("4.5", reopened.Camera.CaptureEditor.VariationParameters[0].Value);
                Assert.False(reopened.IsRunning || reopened.Camera.HasCurrentCameraInputs || main.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Fact]
    public async Task EmbeddedFaultControlsInjectAndClearThroughTheExistingRuntimeWithoutChangingTheRecipe()
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var project = R40ProcessWorkspaceTests.CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsExperimentWorkspace = true;
            main.Navigation.SelectedExperimentTabIndex = 1;
            main.IsRunMode = true;
            await WaitForAsync(() => main.FaultManager.IsEnabled);
            var window = Show(main, false);
            try
            {
                var view = Assert.Single(Descendants(window).OfType<ExperimentWorkspaceView>()).ExperimentFaultEditor;
                string before = JsonSerializer.Serialize(project);
                Assert.IsType<ComboBox>(view.FindName("GlobalFaultKindComboBox")).SelectedItem = main.FaultManager.AvailableKinds.Single(item => item.Kind == SimulationFaultKind.AxisMotionBlocked);
                Pump(window);
                var targets = Assert.IsType<ComboBox>(view.FindName("GlobalFaultTargetComboBox"));
                Assert.NotEmpty(main.FaultManager.Targets);
                targets.SelectedIndex = 0;
                Pump(window);
                Invoke(Assert.IsType<Button>(view.FindName("GlobalInjectFaultButton")));
                await WaitForAsync(() => main.FaultManager.ActiveFaults.Count == 1 && main.FaultManager.ClearAllCommand.CanExecute(null));
                Pump(window);
                Capture(window, "fault-active-compact");
                Invoke(Assert.IsType<Button>(view.FindName("GlobalClearAllFaultsButton")));
                await WaitForAsync(() => main.FaultManager.ActiveFaults.Count == 0);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Fact]
    public async Task CurrentInputPreviewAndInspectionNavigationUseTheAcquiredInputAndRejectForeignOrStaleRows()
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var definition = project.Devices.First(item => item.Kind == DeviceKind.Camera);
            string recipe = project.Sequences.SelectMany(sequence => sequence.Steps).First(step => step.Action == SequenceStepAction.TriggerCamera && step.TargetId == definition.Id).Parameter;
            definition.Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = recipe, GenerationDelayMilliseconds = 0, ResponseDelayMilliseconds = 0 };
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsExperimentWorkspace = true;
            main.Navigation.SelectedExperimentTabIndex = 2;
            main.Camera.SelectedCameraId = definition.Id;
            var window = Show(main, true);
            try
            {
                Directory.CreateDirectory(TestStorage.RootPath);
                await main.SaveProjectAsync(Path.Combine(TestStorage.RootPath, "current-input-project.ovmachine"));
                string before = JsonSerializer.Serialize(project);
                main.IsRunMode = true;
                await WaitForAsync(() => main.Camera.StartManualCameraControlCommand.CanExecute(null));
                main.Camera.StartManualCameraControlCommand.Execute(null);
                await WaitForAsync(() => main.IsRunning && main.PauseCommand.CanExecute(null));
                main.PauseCommand.Execute(null);
                await WaitForAsync(() => !main.IsRunning);
                await WaitForAsync(() => main.Camera.TriggerCameraCommand.CanExecute(null));
                main.Camera.TriggerCameraCommand.Execute(null);
                await WaitForAsync(() => main.Camera.CurrentCameraStateText != OpenVisionLanguageService.T("Equipment.State.Idle"));
                int ticks = (int)Math.Ceiling((double)definition.Camera.ExposureDelayMilliseconds / project.Simulation.FixedStepMilliseconds)
                    + (int)Math.Ceiling((double)definition.Camera.TransferDelayMilliseconds / project.Simulation.FixedStepMilliseconds) + 2;
                for (int i = 0; i < ticks && !main.Camera.HasSelectedCameraInput; i++)
                {
                    await WaitForAsync(() => main.StepCommand.CanExecute(null));
                    main.StepCommand.Execute(null);
                    await WaitForAsync(() => main.StepCommand.CanExecute(null));
                }
                await WaitForAsync(() => main.Camera.HasSelectedCameraInput && main.Camera.CurrentNumericalInput is not null);
                Pump(window);
                var view = Assert.Single(Descendants(window).OfType<ExperimentWorkspaceView>());
                var input = Assert.IsType<CameraExecutionInputItem>(view.ExperimentOpenInputButton.CommandParameter);
                Assert.Equal(project.Id, input.ProjectId);
                Assert.True(input.RuntimeGeneration > 0);
                Assert.NotNull(view.ExperimentNumericalPreview.Source);
                Assert.True(view.ExperimentNumericalPreview.ActualWidth >= 300 && view.ExperimentNumericalPreview.ActualHeight >= 200);
                main.Camera.SelectCameraCommand.Execute(project.Devices.First(item => item.Kind == DeviceKind.Camera && item.Id != input.Id).Id);
                Pump(window);
                Assert.Null(view.ExperimentNumericalPreview.Source);
                var card = Assert.Single(Descendants(view.ExperimentInputCards).OfType<Button>());
                Assert.Same(main.Camera.SelectCameraCommand, card.Command);
                Invoke(card);
                Pump(window);
                Assert.Equal(input.Id, main.Camera.SelectedCameraId);
                Assert.NotNull(view.ExperimentNumericalPreview.Source);
                Assert.False(main.OpenExperimentCameraInputCommand.CanExecute(input with { ProjectId = "foreign-project" }));
                Assert.False(main.OpenExperimentCameraInputCommand.CanExecute(input with { RuntimeGeneration = input.RuntimeGeneration + 1 }));
                Assert.False(main.OpenExperimentCameraInputCommand.CanExecute(input with { AcquisitionText = "stale acquisition" }));
                Capture(window, "current-input-work-area");
                Invoke(view.ExperimentOpenInputButton);
                Pump(window);
                Assert.True(main.Navigation.IsInspectionWorkspace);
                Assert.Equal(input.Id, main.Camera.SelectedCameraId);
                main.Navigation.IsExperimentWorkspace = true;
                Assert.True(main.OpenExperimentCameraInputCommand.CanExecute(input));
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges);
                await WaitForAsync(() => main.ResetCommand.CanExecute(null));
                main.ResetCommand.Execute(null);
                await WaitForAsync(() => !main.Camera.HasCurrentCameraInputs);
                Assert.False(main.OpenExperimentCameraInputCommand.CanExecute(input));
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RenderCurrentExperimentEntryPoints(bool fullArea, bool scenario)
    {
        await ui.InvokeAsync(() =>
        {
            var language = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = R40ProcessWorkspaceTests.CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.SelectedInspectionSettingsTabIndex = 1;
            main.Navigation.IsInspectionSettingsExpanded = true;
            if (scenario) main.Navigation.OpenTestScenarioCommand.Execute(null);
            string before = JsonSerializer.Serialize(project);
            var window = Show(main, fullArea);
            try
            {
                Capture(window, $"existing-{(scenario ? "scenario" : "variation")}-{(fullArea ? "work-area" : "compact")}");
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                return true;
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(language, save: false);
            }
        });
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray();
            var scale = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / scale.DpiScaleX;
            window.Top = area[1] / scale.DpiScaleY;
            window.Width = area[2] / scale.DpiScaleX;
            window.Height = area[3] / scale.DpiScaleY;
        }
        Pump(window);
        return window;
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

    private static void Invoke(Button button)
    {
        Assert.True(button.IsEnabled);
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void SetText(TextBox field, string value) => Assert.IsAssignableFrom<IValueProvider>(new TextBoxAutomationPeer(field).GetPattern(PatternInterface.Value)).SetValue(value);

    private static void SelectCamera(ComboBox combo, string cameraId, Window window)
    {
        combo.IsDropDownOpen = true;
        Pump(window);
        combo.SelectedValue = cameraId;
        combo.IsDropDownOpen = false;
        Pump(window);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "Expected experiment state was not observed within the bounded wait.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static void Capture(Window window, string name)
    {
        Pump(window);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(monitor.WindowIntersectsMonitor);
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        Assert.True(monitor.WindowContainedByMonitor);
        var directory = Path.Combine(TestStorage.RootPath, "experiment-workspace");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(output);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            monitor, nativeDpi = GetDpiForWindow(new WindowInteropHelper(window).Handle), wpfDpi = dpi.PixelsPerInchX,
            width, height, method = "In-process WPF RenderTargetBitmap; no physical pointer/keyboard qualification", crop = "none"
        }));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
