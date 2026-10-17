using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using System.Windows.Media.Imaging;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class CameraResultInspectorViewAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public CameraResultInspectorViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task SelectingEachCameraBindsItsOwnResultAndSharedWorkpieceAssociation()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                var project = CameraCommissioningViewModelTests.CreateProject();
                var projections = new Dictionary<string, CameraCommissioningProjection>(StringComparer.Ordinal)
                {
                    ["camera-1"] = CreateProjection(CreateCameraSnapshot(
                        "camera-1",
                        "Camera 1",
                        "acquisition-1",
                        "inspection-1",
                        PlaceholderInspectionDecision.Pass,
                        "workpiece-1")),
                    ["camera-2"] = CreateProjection(CreateCameraSnapshot(
                        "camera-2",
                        "Camera 2",
                        "acquisition-2",
                        "inspection-2",
                        PlaceholderInspectionDecision.Fail,
                        "workpiece-1"))
                };
                CameraCommissioningViewModel? camera = null;
                camera = CameraCommissioningViewModelTests.CreateViewModel(
                    project,
                    projections["camera-1"],
                    () => projections[camera?.SelectedCameraId ?? "camera-1"]);
                using (camera)
                {
                    camera.LoadProject(project, null);
                    var view = new RightToolRegionView
                    {
                        DataContext = new CameraInspectorContext(camera)
                    };
                    var window = new Window
                    {
                        Content = view,
                        Height = 900,
                        Left = -2000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Top = -2000,
                        Width = 540,
                        WindowStyle = WindowStyle.None
                    };

                    window.Show();
                    try
                    {
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var cameraSelector = Assert.IsType<ComboBox>(view.FindName("CameraSelectionComboBox"));
                        var evidenceDetails = Assert.IsType<TextBlock>(view.FindName("CameraExecutionEvidenceDetailsTextBlock"));
                        var resultText = Descendants<TextBlock>(view).Single(text =>
                            text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path
                                == nameof(CameraCommissioningViewModel.CurrentCameraResultText));
                        var cameraOne = CaptureProjection(cameraSelector, resultText, evidenceDetails);

                        cameraSelector.IsDropDownOpen = true;
                        cameraSelector.SelectedValue = "camera-2";
                        cameraSelector.IsDropDownOpen = false;
                        window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var cameraTwo = CaptureProjection(cameraSelector, resultText, evidenceDetails);

                        return new CameraSwitchResult(
                            cameraOne,
                            cameraTwo,
                            OpenVisionLanguageService.T("Shell.ResultPass"),
                            OpenVisionLanguageService.T("Shell.ResultFail"),
                            OpenVisionLanguageService.T("Camera.ResultSourceMock"));
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });

        Assert.Equal("camera-1", result.First.SelectedCameraId);
        Assert.Equal("camera-2", result.Second.SelectedCameraId);
        Assert.Equal(result.PassText, result.First.ResultText);
        Assert.Equal(result.FailText, result.Second.ResultText);
        Assert.Contains("workpiece-1", result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("workpiece-1", result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("workpiece-instance-1", result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("workpiece-instance-1", result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("inspection-1", result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("inspection-2", result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains(result.MockSourceText, result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains(result.MockSourceText, result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.NotEqual(result.First.EvidenceDetails, result.Second.EvidenceDetails);
    }

    [Theory]
    [InlineData("camera-1", false)]
    [InlineData("camera-2", false)]
    [InlineData("camera-1", true)]
    [InlineData("camera-2", true)]
    public async Task ReloadedCameraDefinitionsRestoreTheSelectedCameraInTheBoundSelector(string selectedId, bool inspectionWorkspace)
    {
        await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var project = CameraCommissioningViewModelTests.CreateProject();
            var projection = CreateProjection(CreateCameraSnapshot(selectedId, selectedId, "acquisition-1", "inspection-1", PlaceholderInspectionDecision.Pass, "workpiece-1"));
            using var camera = CameraCommissioningViewModelTests.CreateViewModel(project, projection);
            camera.LoadProject(project, null);
            camera.SelectedCameraId = selectedId;
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, exception => throw exception);
            UserControl view = inspectionWorkspace
                ? new MmiOperatorLayoutView { DataContext = new CameraInspectorContext(camera, navigation) }
                : new RightToolRegionView { DataContext = new CameraInspectorContext(camera) };
            var window = new Window { Content = view, Width = 540, Height = 900, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var selector = Assert.IsType<ComboBox>(view.FindName(inspectionWorkspace ? "MmiCameraSelectionComboBox" : "CameraSelectionComboBox"));
                Assert.Equal(selectedId, selector.SelectedValue);

                project.Devices = CameraCommissioningViewModelTests.CreateProject().Devices;
                camera.LoadProject(project, null);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

                Assert.Equal(selectedId, camera.SelectedCameraId);
                Assert.Equal(selectedId, selector.SelectedValue);
                Assert.Same(camera.SelectedVirtualCamera, selector.SelectedItem);
                selector.IsDropDownOpen = true;
                selector.SelectedValue = selectedId == "camera-1" ? "camera-2" : "camera-1";
                selector.IsDropDownOpen = false;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(selector.SelectedValue, camera.SelectedCameraId);
                var sourceView = Assert.IsType<CameraImageSourceView>(view.FindName(inspectionWorkspace ? "MmiCameraSourceSettings" : "CameraSourceSettings"));
                Assert.Same(camera, sourceView.DataContext);
                Assert.Same(camera.ImageSourceEditor.ApplyCommand, sourceView.ApplyCameraSourceButton.Command);
                Assert.Same(camera.ImageSourceEditor.RevertCommand, sourceView.RevertCameraSourceButton.Command);
                Assert.Same(camera.ImageSourceEditor.BrowseCommand, sourceView.BrowseCameraSourceButton.Command);
                var recipeSelector = Assert.IsType<ComboBox>(sourceView.FindName("CameraRecipeComboBox"));
                Assert.Equal(selectedId == "camera-1" ? new[] { "beta" } : new[] { "alpha", "zeta" }, recipeSelector.Items.Cast<string>());
                Assert.Equal(camera.SelectedCameraRecipe, recipeSelector.SelectedItem);
                camera.SelectedCameraId = "camera-1";
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var originalRecipe = camera.SelectedCameraRecipe;
                var otherRecipe = camera.CurrentCameraRecipes.First(recipe => recipe != originalRecipe);
                recipeSelector.SetCurrentValue(ComboBox.SelectedItemProperty, otherRecipe);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(otherRecipe, camera.SelectedCameraRecipe);
                camera.SelectedCameraRecipe = originalRecipe;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(originalRecipe, recipeSelector.SelectedItem);
                if (inspectionWorkspace)
                {
                    var tabs = Assert.IsType<TabControl>(view.FindName("MmiInspectionSettingsTabs"));
                    tabs.SelectedIndex = 1;
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.Equal(1, navigation.SelectedInspectionSettingsTabIndex);
                    navigation.SelectedInspectionSettingsTabIndex = 0;
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.Equal(0, tabs.SelectedIndex);
                }
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task OpeningSavedProjectThroughMainWorkflowRestoresTheVisibleCameraSelection()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var directory = Path.Combine(@"D:\OpenVisionLab-TestData\Machine\camera-selector-reopen", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "reopen.ovmachine");
            await new ProjectDocumentFileStore().SaveAsync(project, path);
            using var main = new MainViewModel(project, path);
            main.UnsavedProjectPrompt = () => throw new InvalidOperationException("Camera selection must not dirty the project.");
            main.ProjectOpenFailurePresenter = message => throw new InvalidOperationException(message);
            var readyUntil = DateTime.UtcNow.AddSeconds(10);
            while (main.SceneSnapshots.Latest?.ProjectId != project.Id && DateTime.UtcNow < readyUntil)
            {
                await Task.Delay(10);
            }
            Assert.Equal(project.Id, main.SceneSnapshots.Latest?.ProjectId);
            main.Camera.SelectedCameraId = project.Devices.Last(device => device.Kind == DeviceKind.Camera).Id;
            var selectedId = main.Camera.SelectedCameraId;
            var view = new MmiOperatorLayoutView { DataContext = main };
            var window = new Window { Content = view, Width = 1280, Height = 760, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var selector = Assert.IsType<ComboBox>(view.FindName("MmiCameraSelectionComboBox"));
                Assert.Equal(selectedId, selector.SelectedValue);
                await main.SaveProjectAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(await main.OpenProjectReplacingCurrentAsync(path).WaitAsync(TimeSpan.FromSeconds(10)));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(selectedId, main.Camera.SelectedCameraId);
                Assert.Equal(selectedId, selector.SelectedValue);
                Assert.Same(main.Camera.SelectedVirtualCamera, selector.SelectedItem);
                Assert.False(main.HasUnsavedChanges);
                var settingsToggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("MmiInspectionSettingsToggle"));
                foreach (var expanded in new[] { true, false })
                {
                    main.Navigation.IsInspectionSettingsExpanded = expanded;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var origin = settingsToggle.TranslatePoint(new Point(), view);
                    Assert.True(origin.X >= 0 && origin.X + settingsToggle.ActualWidth <= view.ActualWidth);
                    var hit = view.InputHitTest(settingsToggle.TranslatePoint(new Point(settingsToggle.ActualWidth / 2, settingsToggle.ActualHeight / 2), view)) as DependencyObject;
                    while (hit != null && hit != settingsToggle) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
                    Assert.Same(settingsToggle, hit);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1034, false, OpenVisionLanguage.Korean)]
    [InlineData(1034, false, OpenVisionLanguage.English)]
    [InlineData(794, true, OpenVisionLanguage.Korean)]
    [InlineData(794, true, OpenVisionLanguage.English)]
    public async Task CompactSettingsTabsAndExpandedReturnPreserveCameraAndProject(double width, bool narrow, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var store = new ProjectDocumentStore();
                var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
                using var main = new MainViewModel(project);
                main.Navigation.IsCompactLayout = true;
                main.Navigation.IsNarrowLayout = narrow;
                main.Camera.SelectedCameraId = project.Devices.Last(device => device.Kind == DeviceKind.Camera).Id;
                var selectedId = main.Camera.SelectedCameraId;
                var serialized = store.Serialize(project);
                var workspace = new SimulationIntegrationWorkspaceView { DataContext = main };
                var view = Assert.Single(Descendants<MmiOperatorLayoutView>(workspace));
                Assert.IsType<Grid>(workspace.Content);
                var window = new Window
                {
                    Content = workspace, Width = width, Height = 1032, WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false, ShowActivated = false
                };
                window.Show();
                try
                {
                    var tabs = Assert.IsType<TabControl>(view.FindName("MmiInspectionSettingsTabs"));
                    var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("MmiInspectionSettingsToggle"));
                    var setup = Assert.IsType<Grid>(view.FindName("MmiInspectionSetupPanel"));
                    var preview = Assert.IsType<Border>(view.FindName("MmiInspectionPreviewPanel"));
                    var selector = Assert.IsType<ComboBox>(view.FindName("MmiCameraSelectionComboBox"));
                    foreach (var expanded in new[] { false, true, false })
                    {
                        toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, expanded);
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        Assert.Equal(expanded, main.Navigation.IsInspectionSettingsExpanded);
                        Assert.Equal(Visibility.Visible, preview.Visibility);
                        Assert.True(new Rect(view.RenderSize).Contains(preview.TransformToAncestor(view).TransformBounds(new Rect(preview.RenderSize))));
                        Assert.Equal(narrow && !expanded ? 0 : 2, Grid.GetColumn(setup));
                        Assert.Equal(1, Grid.GetColumnSpan(setup));
                        var bounds = new Rect(view.RenderSize);
                        foreach (var control in new FrameworkElement[] { toggle, tabs })
                        {
                            if (control == tabs && narrow && !expanded)
                            {
                                Assert.False(control.IsVisible);
                                continue;
                            }
                            Assert.True(control.IsVisible);
                            Assert.True(bounds.Contains(control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize))));
                        }
                        var hit = view.InputHitTest(toggle.TranslatePoint(new Point(toggle.ActualWidth / 2, toggle.ActualHeight / 2), view)) as DependencyObject;
                        while (hit != null && hit != toggle) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
                        Assert.Same(toggle, hit);
                        Assert.Equal(OpenVisionLanguageService.T(expanded ? "Integration.MmiShowInputWithSettings" : "Integration.MmiSettingsWide"), toggle.Content);
                        tabs.SelectedIndex = 1;
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        Assert.Equal(1, main.Navigation.SelectedInspectionSettingsTabIndex);
                        main.Navigation.SelectedInspectionSettingsTabIndex = 0;
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        Assert.Equal(0, tabs.SelectedIndex);
                        Assert.Equal(selectedId, selector.SelectedValue);
                        if (narrow && !expanded) continue;
                        var source = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraSourceSettings"));
                        var scroll = Assert.IsType<ScrollViewer>(view.FindName("MmiCaptureSetupScrollViewer"));
                        var viewport = VisualDescendants<ScrollContentPresenter>(scroll).First();
                        source.BrowseCameraSourceButton.BringIntoView();
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var browseBounds = source.BrowseCameraSourceButton.TransformToAncestor(viewport).TransformBounds(new Rect(source.BrowseCameraSourceButton.RenderSize));
                        Assert.True(Math.Round(browseBounds.Top) >= 0 && Math.Round(browseBounds.Bottom) <= Math.Round(viewport.ActualHeight));
                        toggle.BringIntoView();
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var toggleBounds = toggle.TransformToAncestor(view).TransformBounds(new Rect(toggle.RenderSize));
                        Assert.True(new Rect(view.RenderSize).Contains(toggleBounds));
                    }
                    Assert.Equal(serialized, store.Serialize(project));
                    Assert.False(main.HasUnsavedChanges);
                    Assert.False(main.IsRunning);
                    return true;
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(1280, 760, true, false, OpenVisionLanguage.Korean, false)]
    [InlineData(1920, 1040, false, false, OpenVisionLanguage.Korean, false)]
    [InlineData(794, 1032, true, true, OpenVisionLanguage.English, false)]
    [InlineData(794, 760, true, true, OpenVisionLanguage.English, false)]
    [InlineData(1280, 450, true, false, OpenVisionLanguage.English, true)]
    [InlineData(794, 510, true, true, OpenVisionLanguage.English, true)]
    [InlineData(794, 450, true, true, OpenVisionLanguage.English, true)]
    public async Task CapturedImageBindingClearsStalePixelsAndRecoversAfterMalformedInput(double width, double height, bool compact, bool narrow, OpenVisionLanguage language, bool longName)
    {
        await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            var root = Path.Combine(TestStorage.RootPath, "camera-preview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "images"));
            var projectPath = Path.Combine(root, "machine.ovmachine");
            File.WriteAllText(projectPath, "{}");
            File.WriteAllBytes(Path.Combine(root, "images", "part.pgm"), "P5\n2 2\n255\n"u8.ToArray().Concat(new byte[] { 0, 64, 128, 255 }).ToArray());
            File.WriteAllBytes(Path.Combine(root, "images", "truncated.png"), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 });
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CameraCommissioningViewModelTests.CreateProject();
                var cameraName = longName ? "상면 촬영부 · 장시간 검사 장비의 카메라 입력과 검사 결과 확인 Camera source and inspection result verification" : "Camera 1";
                project.Devices.Single(device => device.Id == "camera-1").Name = cameraName;
                var frameReady = CreateCameraSnapshot("camera-1", cameraName, "acquisition-1", "inspection-1", PlaceholderInspectionDecision.Pass, "workpiece-1");
                var projection = CreateProjection(frameReady with { State = VirtualCameraState.Idle, CurrentAcquisitionId = null }) with { ProjectPath = projectPath };
                using var camera = CameraCommissioningViewModelTests.CreateViewModel(project, projection, () => projection);
                camera.LoadProject(project, projectPath);
                using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                    () => Task.CompletedTask, () => { }, exception => throw exception)
                { IsCompactLayout = compact, IsNarrowLayout = narrow };
                var view = new MmiOperatorLayoutView { DataContext = new CameraInspectorContext(camera, navigation) };
                var window = new Window
                {
                    Content = view, Width = width, Height = height, WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false, ShowActivated = false
                };
                window.Show();
                try
                {
                    var image = Assert.IsType<Image>(view.FindName("MmiVirtualCameraImageViewer"));
                    var error = Assert.IsType<TextBlock>(view.FindName("MmiVirtualCameraImageError"));
                    var frame = Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame"));
                    var preview = Assert.IsType<ScrollViewer>(view.FindName("MmiInspectionPreviewScrollViewer"));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.Null(image.Source);
                    Assert.Equal(Visibility.Collapsed, error.Visibility);

                    projection = CreateProjection(frameReady) with { ProjectPath = projectPath };
                    camera.RefreshProjection();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.IsAssignableFrom<BitmapSource>(image.Source);
                    Assert.Equal(Visibility.Collapsed, error.Visibility);
                    var frameBounds = frame.TransformToAncestor(preview).TransformBounds(new Rect(frame.RenderSize));
                    Assert.True(frame.ActualHeight > 0);
                    Assert.True(frameBounds.Top >= 0 && frameBounds.Bottom <= preview.ViewportHeight,
                        $"Whole input image must fit the initial preview: frame={frameBounds}, viewportHeight={preview.ViewportHeight}.");

                    projection = projection with
                    {
                        Snapshot = frameReady with
                        {
                            FrameEvidence = new VirtualCameraFrameEvidence("frame-corrupt", "images/truncated.png", new string('A', 64), 10, 2, 2, "Mono8")
                        }
                    };
                    camera.RefreshProjection();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.True(camera.HasCurrentCameraImage);
                    Assert.Null(image.Source);
                    Assert.True(error.IsVisible);
                    Assert.Equal(OpenVisionLanguageService.T("Integration.MmiVirtualCameraImageError"), error.Text);
                    Assert.Same(view.FindResource("State.Fault"), error.Foreground);
                    Assert.True(new Rect(frame.RenderSize).Contains(error.TransformToAncestor(frame).TransformBounds(new Rect(error.RenderSize))));

                    projection = CreateProjection(frameReady) with { ProjectPath = projectPath };
                    camera.RefreshProjection();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                    var actual = new byte[4];
                    bitmap.CopyPixels(actual, 2, 0);
                    Assert.Equal(new byte[] { 0, 64, 128, 255 }, actual);
                    Assert.Equal(Visibility.Collapsed, error.Visibility);
                    return true;
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public async Task AppliedImageSourceSurvivesLayoutUndoRedoAndSaveReopen()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var root = Path.Combine(TestStorage.RootPath, "camera-source-layout-undo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "images"));
            var path = Path.Combine(root, "machine.ovmachine");
            var pixels = "P5\n2 2\n255\n"u8.ToArray().Concat(new byte[] { 0, 64, 128, 255 }).ToArray();
            File.WriteAllBytes(Path.Combine(root, "images", "first.pgm"), pixels);
            File.WriteAllBytes(Path.Combine(root, "images", "second.pgm"), pixels);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
                await new ProjectDocumentFileStore().SaveAsync(project, path);
                using var main = new MainViewModel(project, path);
                var readyUntil = DateTime.UtcNow.AddSeconds(10);
                while (main.SceneSnapshots.Latest?.ProjectId != project.Id && DateTime.UtcNow < readyUntil) await Task.Delay(10);
                Assert.Equal(project.Id, main.SceneSnapshots.Latest?.ProjectId);
                main.Camera.SelectedCameraId = "device.camera-1";
                main.Layout.Select("camera-height-verdict");
                var originalX = main.Layout.SelectedItem!.CurrentX;
                var editor = main.Camera.ImageSourceEditor;
                editor.PathText = "images/first.pgm";
                editor.Width = 2;
                editor.Height = 2;
                editor.PixelFormatText = "Mono8";
                Assert.True(editor.ApplyCommand.CanExecute(null));
                editor.ApplyCommand.Execute(null);
                Assert.True(main.HasUnsavedChanges);
                Assert.True(main.Layout.MoveSelectionBy(40, 20));
                main.UndoLayoutEditCommand.Execute(null);
                Assert.Equal(originalX, main.Layout.SelectedItem!.CurrentX);
                Assert.Equal("images/first.pgm", project.Devices.Single(device => device.Id == "device.camera-1").Camera?.SingleImageSource?.SourceRelativePath);
                Assert.Equal("images/first.pgm", editor.PathText);
                Assert.Equal("images/first.pgm", main.Camera.CurrentCameraSourceText);
                Assert.True(main.HasUnsavedChanges);
                main.RedoLayoutEditCommand.Execute(null);
                Assert.Equal(originalX + 40, main.Layout.SelectedItem!.CurrentX);
                Assert.Equal("images/first.pgm", main.Camera.CurrentCameraSourceText);
                main.UndoLayoutEditCommand.Execute(null);

                editor.PathText = "images/second.pgm";
                Assert.True(editor.ApplyCommand.CanExecute(null));
                editor.ApplyCommand.Execute(null);
                Assert.False(main.UndoLayoutEditCommand.CanExecute(null));
                Assert.True(main.Layout.MoveSelectionBy(40, 20));
                main.UndoLayoutEditCommand.Execute(null);
                Assert.Equal(originalX, main.Layout.SelectedItem!.CurrentX);
                Assert.Equal("images/second.pgm", main.Camera.CurrentCameraSourceText);
                Assert.Equal("images/second.pgm", editor.PathText);
                Assert.True(main.HasUnsavedChanges);
                await main.SaveProjectAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(main.HasUnsavedChanges);
                Assert.True(await main.OpenProjectReplacingCurrentAsync(path).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("images/second.pgm", main.Camera.CurrentCameraSourceText);
                Assert.Equal("images/second.pgm", main.Camera.ImageSourceEditor.PathText);
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                Assert.Equal(0, main.SceneSnapshots.Latest!.TickIndex);
                Assert.All(main.SceneSnapshots.Latest.Cameras, camera => Assert.Equal(0, camera.AcquisitionOrdinal));
                return true;
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    [Theory]
    [InlineData(1920, false, 1032)]
    [InlineData(1920, false, 760)]
    [InlineData(1280, false, 760)]
    [InlineData(1000, true, 760)]
    [InlineData(1000, true, 720)]
    public async Task NumericalCaptureFieldsAreReachableAndBindingsRecoverBeforeSaveReopen(double width, bool narrow, double height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var definition = project.Devices.First(device => device.Kind == DeviceKind.Camera);
            definition.Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = "수치 입력 검증", Version = 1, ConfirmedVersion = 1 };
            var root = Path.Combine(TestStorage.RootPath, "capture-binding-reopen", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "capture.ovmachine");
            using var main = new MainViewModel(project, path);
            main.Navigation.IsCompactLayout = width < 1200;
            main.Navigation.IsNarrowLayout = narrow;
            main.Camera.SelectedCameraId = definition.Id;
            var window = new ShellWindow { DataContext = main, Width = width, Height = height, Left = -3000, Top = -3000, UseLayoutRounding = true, SnapsToDevicePixels = true, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                main.Navigation.IsInspectionWorkspace = true;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var view = VisualDescendants<MmiOperatorLayoutView>(window).Single();
                var source = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraSourceSettings"));
                var outerScroll = Assert.IsType<ScrollViewer>(view.FindName("MmiCaptureSetupScrollViewer"));
                var scroll = Assert.IsType<ScrollViewer>(source.FindName("CameraSourceFieldsScrollViewer"));
                var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("MmiInspectionSettingsToggle"));
                if (narrow)
                {
                    Assert.False(source.IsVisible);
                    ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                    await Task.Delay(250);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.True(source.IsVisible && main.Navigation.IsInspectionSettingsExpanded);
                }
                var viewport = VisualDescendants<ScrollContentPresenter>(scroll).First();
                var editor = main.Camera.CaptureEditor;
                var field = Assert.IsType<TextBox>(source.FindName("CaptureFieldOfViewTextBox"));
                var apply = VisualDescendants<Button>(source).Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.ApplyCommand));
                foreach (var expanded in new[] { false, true })
                {
                    if (expanded) ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                    else main.Navigation.IsInspectionSettingsExpanded = false;
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    await Task.Delay(250);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(expanded, main.Navigation.IsInspectionSettingsExpanded);
                    if (narrow && !expanded)
                    {
                        Assert.False(source.IsVisible);
                        var preview = Assert.IsType<ScrollViewer>(view.FindName("MmiInspectionPreviewScrollViewer"));
                        Assert.True(preview.ViewportHeight > field.ActualHeight);
                        Assert.True(toggle.IsVisible && toggle.IsEnabled);
                        continue;
                    }
                    Assert.Equal(0, outerScroll.ScrollableHeight, precision: 0);
                    Assert.Equal(outerScroll.ViewportHeight, source.ActualHeight, precision: 0);
                    Assert.True(scroll.ViewportHeight >= field.ActualHeight);
                    var evidenceDirectory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR");
                    if (!string.IsNullOrWhiteSpace(evidenceDirectory))
                    {
                        Directory.CreateDirectory(evidenceDirectory);
                        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        image.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using var output = File.Create(Path.Combine(evidenceDirectory, $"capture-shell-{width}x{height}-{expanded}.png"));
                        encoder.Save(output);
                        var expander = VisualDescendants<Expander>(source).Single();
                        File.WriteAllText(Path.Combine(evidenceDirectory, $"capture-shell-{width}x{height}-{expanded}.json"), System.Text.Json.JsonSerializer.Serialize(new { expanded = expander.IsExpanded, sourceHeight = source.ActualHeight, sourceHeightBinding = source.GetBindingExpression(FrameworkElement.HeightProperty)?.Status.ToString(), outerViewport = outerScroll.ViewportHeight, outerScroll = outerScroll.ScrollableHeight, innerViewport = scroll.ViewportHeight, fieldHeight = field.ActualHeight }));
                    }
                    Assert.True(new Rect(source.RenderSize).Contains(apply.TransformToAncestor(source).TransformBounds(new Rect(apply.RenderSize))), "Capture Apply must fit the fixed settings footer.");
                    Assert.DoesNotContain(apply, VisualDescendants<Button>(scroll));
                    foreach (var control in new FrameworkElement[] { field })
                    {
                        Assert.True(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0);
                        control.BringIntoView();
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        var targetBounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                        double requestedOffset = scroll.VerticalOffset + targetBounds.Top - Math.Max(0, (scroll.ViewportHeight - targetBounds.Height) / 2);
                        scroll.ScrollToVerticalOffset(requestedOffset);
                        await Task.Delay(250);
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(viewport).DpiScaleY;
                        Assert.True(Math.Round(bounds.Top * scale) >= 0 && Math.Round(bounds.Bottom * scale) <= Math.Round(viewport.ActualHeight * scale), $"{control.GetType().Name}: {bounds}, viewport={viewport.RenderSize}, extent={scroll.ExtentHeight}, offset={scroll.VerticalOffset}/{scroll.ScrollableHeight}, requested={requestedOffset}, source={source.RenderSize}/{source.DesiredSize}, visible={control.IsVisible}, expanded={expanded}, tab={((TabControl)view.FindName("MmiInspectionSettingsTabs")).SelectedIndex}");
                        Assert.True(bounds.Left >= 0 && bounds.Right <= viewport.ActualWidth);
                        Assert.True(new Rect(window.RenderSize).Contains(control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize))), control.GetType().Name);
                    }
                }
                field.SetCurrentValue(TextBox.TextProperty, "0");
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.Equal("0", editor.Parameters[0].Value);
                ((IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(120, definition.Camera.NumericalInput!.FieldOfView);
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                Assert.Contains("10–10000", editor.Status);
                var revert = VisualDescendants<Button>(source).Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.RevertCommand));
                ((IInvokeProvider)new ButtonAutomationPeer(revert).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                field = Assert.IsType<TextBox>(source.FindName("CaptureFieldOfViewTextBox"));
                Assert.Equal("120", field.Text);
                Assert.DoesNotContain("10–10000", editor.Status);
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                field.SetCurrentValue(TextBox.TextProperty, "130");
                main.Navigation.SelectedInspectionSettingsTabIndex = 1;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var variation = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraVariationSettings"));
                var offset = VisualDescendants<TextBox>(variation).Single(control => control.DataContext is CaptureParameterEditor parameter && parameter.Name == "X 편차");
                offset.SetCurrentValue(TextBox.TextProperty, "5");
                Assert.Equal("5", editor.Parameters[1].Value);
                Assert.Equal(0, definition.Camera.NumericalInput.OffsetX);
                main.Navigation.SelectedInspectionSettingsTabIndex = 2;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var generation = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraGenerationSettings"));
                var generationDelay = VisualDescendants<TextBox>(generation).Single(control => control.DataContext is CaptureParameterEditor parameter && parameter.Name == "생성 지연");
                generationDelay.SetCurrentValue(TextBox.TextProperty, "1.2");
                Assert.Equal("1.2", editor.Parameters[8].Value);
                var kind = VisualDescendants<ComboBox>(generation).Single(control => control.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(CameraCaptureEditorViewModel.InputKind));
                kind.SetCurrentValue(ComboBox.SelectedItemProperty, NumericalCameraInputKind.HeightMap3D);
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.Equal(NumericalCameraInputKind.HeightMap3D, editor.InputKind);
                Assert.Contains("확인 필요", editor.ConfirmationText);
                main.Navigation.SelectedInspectionSettingsTabIndex = 3;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var confirm = VisualDescendants<Button>(view).Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.ConfirmVersionCommand));
                apply = VisualDescendants<Button>(view).Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.ApplyCommand));
                ((IInvokeProvider)new ButtonAutomationPeer(confirm).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                ((IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(130, definition.Camera.NumericalInput.FieldOfView);
                Assert.Equal(5, definition.Camera.NumericalInput.OffsetX);
                Assert.Equal(NumericalCameraInputKind.HeightMap3D, definition.Camera.NumericalInput.InputKind);
                Assert.True(main.HasUnsavedChanges);
                Assert.False(main.IsRunning);
                await main.SaveProjectAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(await main.OpenProjectReplacingCurrentAsync(path).WaitAsync(TimeSpan.FromSeconds(10)));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                main.Navigation.SelectedInspectionSettingsTabIndex = 2;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(NumericalCameraInputKind.HeightMap3D, kind.SelectedItem);
                main.Navigation.SelectedInspectionSettingsTabIndex = 0;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var restoredField = Assert.IsType<TextBox>(source.FindName("CaptureFieldOfViewTextBox"));
                Assert.Equal("130", restoredField.Text);
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                Assert.Equal(0, main.SceneSnapshots.Latest!.TickIndex);
                return true;
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
                window.DataContext = null;
                window.Close();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public async Task NumericalFramePixelsRenderInThePrimaryPreviewAndClearBeforeFileRecovery(NumericalCameraInputKind kind)
    {
        await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var root = Path.Combine(TestStorage.RootPath, "numerical-preview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "images"));
            var path = Path.Combine(root, "machine.ovmachine");
            File.WriteAllText(path, "{}");
            File.WriteAllBytes(Path.Combine(root, "images", "part.pgm"), "P5\n2 2\n255\n"u8.ToArray().Concat(new byte[] { 0, 64, 128, 255 }).ToArray());
            var project = CameraCommissioningViewModelTests.CreateProject();
            var settings = new NumericalCameraSettings { InputKind = kind, Version = 1, ConfirmedVersion = 1 };
            project.Devices[0].Camera!.NumericalInput = settings;
            var generated = NumericalCameraInput.Generate(settings, "camera-1", "numerical-acquisition", 1);
            var fileSnapshot = CreateCameraSnapshot("camera-1", "Camera 1", "file-acquisition", "inspection", PlaceholderInspectionDecision.Pass, "workpiece-1");
            var numericalSnapshot = fileSnapshot with { FrameEvidence = generated.Frame, Result = fileSnapshot.Result! with { FrameEvidence = generated.Frame, NumericalInput = generated } };
            var projection = CreateProjection(numericalSnapshot) with { ProjectPath = path };
            using var camera = CameraCommissioningViewModelTests.CreateViewModel(project, projection, () => projection);
            camera.LoadProject(project, path);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false, () => Task.CompletedTask, () => { }, exception => throw exception);
            var view = new MmiOperatorLayoutView { DataContext = new CameraInspectorContext(camera, navigation) };
            var window = new Window { Content = view, Width = 1280, Height = 538, Left = -2000, Top = -2000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var numericalImage = Assert.IsType<Image>(view.FindName("MmiNumericalCameraImageViewer"));
                var fileImage = Assert.IsType<Image>(view.FindName("MmiVirtualCameraImageViewer"));
                var error = Assert.IsType<TextBlock>(view.FindName("MmiVirtualCameraImageError"));
                var image = Assert.IsAssignableFrom<BitmapSource>(numericalImage.Source);
                var pixels = new byte[NumericalCameraInput.Width * NumericalCameraInput.Height];
                image.CopyPixels(pixels, NumericalCameraInput.Width, 0);
                Assert.Equal(generated.CopyPreview(), pixels);
                Assert.True(camera.HasCurrentCameraImage && camera.HasCurrentNumericalInput && numericalImage.IsVisible);
                Assert.False(error.IsVisible);
                var heightButton = Assert.IsType<RadioButton>(view.FindName("MmiHeightOutputButton"));
                Assert.Equal(kind == NumericalCameraInputKind.HeightMap3D, heightButton.Command!.CanExecute("height"));
                if (kind == NumericalCameraInputKind.HeightMap3D)
                {
                    var originalBytes = generated.CopyBytes();
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(heightButton).GetPattern(PatternInterface.SelectionItem)).Select();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.True(camera.IsHeightDataView && heightButton.IsChecked == true);
                    var heightImage = Assert.IsType<Image>(view.FindName("MmiHeightDataImageViewer"));
                    var colored = Assert.IsAssignableFrom<BitmapSource>(heightImage.Source);
                    Assert.Equal(System.Windows.Media.PixelFormats.Bgra32, colored.Format);
                    Assert.True(heightImage.IsVisible && !numericalImage.IsVisible);
                    var colors = new byte[generated.Samples.Count * 4];
                    colored.CopyPixels(colors, NumericalCameraInput.Width * 4, 0);
                    int lowest = generated.Samples.Select((sample, index) => (sample, index)).MinBy(item => item.sample).index * 4;
                    int highest = generated.Samples.Select((sample, index) => (sample, index)).MaxBy(item => item.sample).index * 4;
                    Assert.Equal(new byte[] { 255, 0, 0, 255 }, colors.Skip(lowest).Take(4));
                    Assert.Equal(new byte[] { 0, 0, 255, 255 }, colors.Skip(highest).Take(4));
                    Assert.Equal(originalBytes, generated.CopyBytes());
                    var imageButton = Assert.IsType<RadioButton>(view.FindName("MmiImageOutputButton"));
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(imageButton).GetPattern(PatternInterface.SelectionItem)).Select();
                    imageButton.Command!.Execute("image");
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.True(camera.IsImageDataView && imageButton.IsChecked == true && heightButton.IsChecked == false && numericalImage.IsVisible);
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(heightButton).GetPattern(PatternInterface.SelectionItem)).Select();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.True(camera.IsHeightDataView && heightButton.IsChecked == true && imageButton.IsChecked == false);
                }
                projection = projection with { Snapshot = fileSnapshot with { State = VirtualCameraState.Idle, CurrentAcquisitionId = null, CurrentRecipeId = null, Result = null, FrameEvidence = null } };
                camera.RefreshProjection();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Null(numericalImage.Source);
                Assert.False(camera.HasCurrentNumericalInput || camera.HasCurrentCameraImage || error.IsVisible);
                projection = CreateProjection(fileSnapshot) with { ProjectPath = path };
                camera.RefreshProjection();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Null(numericalImage.Source);
                Assert.True(camera.HasCurrentCameraImage);
                var recoveredPixels = new byte[4];
                Assert.IsAssignableFrom<BitmapSource>(fileImage.Source).CopyPixels(recoveredPixels, 2, 0);
                Assert.Equal(new byte[] { 0, 64, 128, 255 }, recoveredPixels);
                Assert.False(error.IsVisible);
                return true;
            }
            finally { window.Close(); Directory.Delete(root, recursive: true); }
        });
    }

    [Theory]
    [InlineData(1920, false, false, OpenVisionLanguage.Korean)]
    [InlineData(1280, true, false, OpenVisionLanguage.English)]
    [InlineData(794, true, true, OpenVisionLanguage.Korean)]
    public async Task R24CaptureToolbarTabsModesAndEquipmentLocationPreserveExplicitExecution(double width, bool compact, bool narrow, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var mappedCameraId = project.Devices.First(device => device.Kind == DeviceKind.Camera).Id;
            project.Devices.Add(new DeviceDefinition { Id = "unplaced-camera", Name = "Unplaced camera", Kind = DeviceKind.Camera, Camera = new() });
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = "unplaced-camera";
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.IsCompactLayout = compact;
            main.Navigation.IsNarrowLayout = narrow;
            var view = new MmiOperatorLayoutView { DataContext = main };
            var window = new Window { Content = view, Width = width, Height = 760, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, 760);
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var before = new ProjectDocumentStore().Serialize(project);
                var header = Assert.IsType<Grid>(view.FindName("MmiInspectionHeader"));
                var camera = Assert.IsType<ComboBox>(view.FindName("MmiCameraSelectionComboBox"));
                var actions = Assert.IsType<WrapPanel>(view.FindName("MmiInspectionHeaderActions"));
                Assert.Equal(new[] { "MmiPublishButton", "MmiCameraHeader", "MmiInspectionStatusPill", "MmiInspectionSettingsToggle", "MmiRunInputsButton", "MmiInspectionStatusButton", "MmiEquipmentLocationButton" }, actions.Children.Cast<FrameworkElement>().Select(element => element.Name));
                var previewPanel = Assert.IsType<Border>(view.FindName("MmiInspectionPreviewPanel"));
                var settingsPanel = Assert.IsType<Grid>(view.FindName("MmiInspectionSetupPanel"));
                Rect Region(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                Assert.True(Region(previewPanel).Bottom <= Region(header).Top);
                Assert.True(Region(header).Right <= Region(previewPanel).Right + 0.1);
                if (!narrow)
                {
                    Assert.Equal(Region(previewPanel).Top, Region(settingsPanel).Top, 2);
                    Assert.True(Region(settingsPanel).Bottom >= Region(header).Bottom);
                }
                var statusScroll = Assert.IsType<ScrollViewer>(view.FindName("MmiCurrentInputStateScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, statusScroll.VerticalScrollBarVisibility);
                foreach (var control in actions.Children.Cast<FrameworkElement>().Append(camera))
                    Assert.True(new Rect(view.RenderSize).Contains(control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize))), control.Name);
                main.Navigation.IsInspectionSettingsExpanded = true;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var tabs = Assert.IsType<TabControl>(view.FindName("MmiInspectionSettingsTabs"));
                Assert.Equal(language == OpenVisionLanguage.Korean ? new[] { "촬영 구성", "편차", "생성", "검사 연동" } : new[] { "Capture setup", "Variation", "Generation", "Inspection connection" }, tabs.Items.Cast<TabItem>().Select(tab => tab.Header));
                var frame = Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame"));
                var imageBounds = frame.TransformToAncestor(view).TransformBounds(new Rect(frame.RenderSize));
                for (int index = 0; index < 4; index++)
                {
                    tabs.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, index);
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.Equal(index, main.Navigation.SelectedInspectionSettingsTabIndex);
                    Assert.Equal(imageBounds, frame.TransformToAncestor(view).TransformBounds(new Rect(frame.RenderSize)));
                }
                main.Navigation.OpenInspectionStatusCommand.Execute(null);
                Assert.Equal(3, tabs.SelectedIndex);
                var generate = Assert.IsType<Button>(view.FindName("MmiGenerateInputButton"));
                Assert.Same(main.RunCommand, generate.Command);
                var publish = Assert.IsType<Button>(view.FindName("MmiPublishButton"));
                Assert.Same(main.Integration.PublishInspectionHandoffCommand, publish.Command);
                main.Integration.Setup.UseThreeDHeightMap = true;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(language == OpenVisionLanguage.Korean ? "3D 검사 요청 만들기" : "Create 3D inspection request", publish.Content);
                Assert.False(publish.IsEnabled);
                main.Integration.Setup.UseThreeDHeightMap = false;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(OpenVisionLanguageService.T("Integration.MmiCreateTwoDRequest"), publish.Content);
                var mode = Assert.IsType<RadioButton>(view.FindName("MmiHeightOutputButton"));
                Assert.Same(main.Camera.ShowInputOutputCommand, mode.Command);
                Assert.False(mode.Command!.CanExecute("height"));
                Assert.False(main.Camera.IsHeightDataView);
                var location = Assert.IsType<Button>(view.FindName("MmiEquipmentLocationButton"));
                Assert.False(location.IsEnabled);
                camera.IsDropDownOpen = true;
                camera.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, mappedCameraId);
                camera.IsDropDownOpen = false;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(mappedCameraId, main.Camera.SelectedCameraId);
                Assert.True(location.IsEnabled);
                camera.IsDropDownOpen = true;
                camera.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "unplaced-camera");
                camera.IsDropDownOpen = false;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.False(location.IsEnabled);
                camera.IsDropDownOpen = true;
                camera.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, mappedCameraId);
                camera.IsDropDownOpen = false;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.True(location.IsEnabled);
                var selectedCamera = main.Camera.SelectedCameraId;
                ((IInvokeProvider)new ButtonAutomationPeer(location).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.True(main.Navigation.IsEquipmentWorkspace);
                Assert.Equal(selectedCamera, main.Layout.SelectedItem!.BehaviorBindingId);
                Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                return true;
            }
            finally { window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1034, 570, OpenVisionLanguage.Korean)]
    [InlineData(1034, 570, OpenVisionLanguage.English)]
    [InlineData(1674, 850, OpenVisionLanguage.Korean)]
    [InlineData(1674, 850, OpenVisionLanguage.English)]
    public async Task IntegrationRequestSummaryRemainsVisibleWithImageAndExplicitCommands(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR");
            var ownedRoot = Path.Combine(TestStorage.RootPath, "integration-summary", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ownedRoot);
            using var main = new MainViewModel(project, integrationSettingsPath: Path.Combine(ownedRoot, "settings.json"));
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.IsCompactLayout = width < 1280;
            var workspace = new SimulationIntegrationWorkspaceView { DataContext = main };
            var window = new Window { Content = workspace, Width = width, Height = height, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
                window.Show();
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                var monitorRoot = Path.Combine(TestStorage.RootPath, "integration-monitor");
                Directory.CreateDirectory(monitorRoot);
                File.WriteAllText(Path.Combine(monitorRoot, $"{width}-{height}-{language}-{Guid.NewGuid():N}.json"), System.Text.Json.JsonSerializer.Serialize(monitor));
                for (var attempt = 0; attempt < 200 && main.SceneSnapshots.Latest?.ProjectId != project.Id; attempt++) await Task.Delay(10);
                Assert.Equal(project.Id, main.SceneSnapshots.Latest?.ProjectId);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var payload = store.Serialize(project);
                var dirty = main.HasUnsavedChanges;
                var mmi = Assert.Single(Descendants<MmiOperatorLayoutView>(workspace));
                var preview = Assert.IsType<Border>(mmi.FindName("MmiInspectionPreviewPanel"));
                var details = mmi.IntegrationExchangeDetailsExpanderControl;
                Assert.False(details.IsExpanded);
                CaptureSummary("initial");
                Assert.Null(workspace.FindName("IntegrationRequestSummary"));
                var previewBounds = preview.TransformToAncestor(workspace).TransformBounds(new Rect(preview.RenderSize));
                var entry = Assert.IsType<Button>(mmi.FindName("MmiInspectionStatusButton"));
                ((IInvokeProvider)new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                details.IsExpanded = true;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var summary = Assert.IsAssignableFrom<FrameworkElement>(mmi.FindName("IntegrationRequestSummary"));
                summary.BringIntoView();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var summaryBounds = summary.TransformToAncestor(workspace).TransformBounds(new Rect(summary.RenderSize));
                Assert.True(summaryBounds.Left >= 0 && summaryBounds.Right <= workspace.ActualWidth, $"Summary crossed horizontal workspace bounds: {summaryBounds}.");
                var rightScroll = Assert.IsType<ScrollViewer>(mmi.FindName("MmiExternalImagesScrollViewer"));
                Assert.True(rightScroll.ExtentHeight >= summary.ActualHeight);
                Assert.False(summaryBounds.IntersectsWith(previewBounds));
                Assert.True(preview.ActualHeight >= 240, $"Primary image area was reduced to {preview.ActualHeight}.");
                var statusControls = new[] { mmi.IntegrationHandoffStatusTextBlockControl, mmi.IntegrationAcknowledgementStatusTextBlockControl, mmi.IntegrationResultStatusTextBlockControl };
                var expected = new[] { main.Integration.HandoffStatusText, main.Integration.AcknowledgementStatusText, main.Integration.ResultStatusText };
                for (var index = 0; index < statusControls.Length; index++)
                {
                    var text = statusControls[index];
                    Assert.True(text.IsVisible, $"Current request state {index} must render in the project fold.");
                    Assert.Equal(expected[index], text.Text);
                    Assert.True(summaryBounds.Contains(text.TransformToAncestor(workspace).TransformBounds(new Rect(text.RenderSize))));
                    Assert.NotNull(text.GetBindingExpression(TextBlock.TextProperty));
                }
                for (var index = 0; index < statusControls.Length; index++)
                {
                    var text = statusControls[index];
                    var scroll = Assert.IsType<ScrollViewer>(mmi.FindName("MmiExternalImagesScrollViewer"));
                    await AssertWrappedTextReachableAsync(text, scroll, window, CaptureSummary, "long-state-" + index);
                    Assert.Equal(previewBounds, preview.TransformToAncestor(workspace).TransformBounds(new Rect(preview.RenderSize)));
                    text.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                    scroll.ScrollToTop();
                }
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var alternateLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
                main.Integration.RefreshLocalization();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(main.Integration.HandoffStatusText, statusControls[0].Text);
                Assert.NotEqual(expected[0], statusControls[0].Text);
                OpenVisionLanguageService.SetLanguage(language, save: false);
                main.Integration.RefreshLocalization();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(expected[0], statusControls[0].Text);
                var historyFilter = mmi.IntegrationTransactionHistoryFilterControl;
                var diagnosticFilter = mmi.IntegrationTransactionDiagnosticsFilterControl;
                Assert.Equal(6, historyFilter.Items.Count);
                Assert.Equal(5, diagnosticFilter.Items.Count);
                historyFilter.SetCurrentValue(ComboBox.SelectedIndexProperty, 1);
                diagnosticFilter.SetCurrentValue(ComboBox.SelectedIndexProperty, 2);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Same(historyFilter.SelectedItem, main.Integration.SelectedTransactionHistoryFilter);
                Assert.Same(diagnosticFilter.SelectedItem, main.Integration.SelectedTransactionDiagnosticFilter);
                main.Integration.SelectedTransactionHistoryFilter = main.Integration.TransactionHistoryFilters[0];
                main.Integration.SelectedTransactionDiagnosticFilter = main.Integration.TransactionDiagnosticFilters[0];
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, historyFilter.SelectedIndex);
                Assert.Equal(0, diagnosticFilter.SelectedIndex);
                Assert.Same(main, details.DataContext);
                Assert.Single(Descendants<Button>(mmi).Where(button => ReferenceEquals(button.Command, main.Integration.RefreshResultsCommand)));
                Assert.Single(Descendants<Button>(mmi).Where(button => ReferenceEquals(button.Command, main.Integration.ApplyResultToSimulationCommand)));
                Assert.Equal(previewBounds, preview.TransformToAncestor(workspace).TransformBounds(new Rect(preview.RenderSize)));
                var publish = Assert.IsType<Button>(mmi.FindName("MmiPublishButton"));
                var refresh = Assert.IsType<Button>(mmi.FindName("MmiRefreshButton"));
                var apply = Assert.IsType<Button>(mmi.FindName("MmiApplyResultButton"));
                Assert.Same(main.Integration.PublishInspectionHandoffCommand, publish.Command);
                Assert.Same(main.Integration.RefreshResultsCommand, refresh.Command);
                Assert.Same(main.Integration.ApplyResultToSimulationCommand, apply.Command);
                Assert.False(apply.IsEnabled);
                main.Integration.Setup.ExchangeRoot = Path.Combine(ownedRoot, new string('x', 80), "missing");
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(refresh.IsEnabled);
                Assert.Equal(previewBounds, preview.TransformToAncestor(workspace).TransformBounds(new Rect(preview.RenderSize)));
                CaptureSummary("unprepared");
                main.Integration.Setup.ExchangeRoot = ownedRoot;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(refresh.IsEnabled);
                refresh.BringIntoView();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                ((IInvokeProvider)new ButtonAutomationPeer(refresh).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                for (var attempt = 0; attempt < 200 && main.Integration.IsBusy; attempt++) await Task.Delay(10);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(main.Integration.IsBusy);
                Assert.Empty(main.Integration.TransactionHistory);
                Assert.False(main.Integration.IsTcpListening);
                Assert.False(publish.IsEnabled);
                Assert.False(apply.IsEnabled);
                Assert.Equal(previewBounds, preview.TransformToAncestor(workspace).TransformBounds(new Rect(preview.RenderSize)));
                Assert.Equal(payload, store.Serialize(project));
                Assert.Equal(dirty, main.HasUnsavedChanges);
                Assert.False(main.IsRunMode);
                Assert.Equal(TimeSpan.Zero, main.SceneSnapshots.Latest!.SimulationTime);
                CaptureSummary("restored");
            }
            finally { window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }

            void CaptureSummary(string phase)
            {
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(evidence, $"integration-summary-{width}-{height}-{language}-{phase}.png"));
                encoder.Save(file);
            }
        });
    }

    [Theory]
    [InlineData(1034, 570, OpenVisionLanguage.Korean)]
    [InlineData(1034, 570, OpenVisionLanguage.English)]
    [InlineData(1674, 850, OpenVisionLanguage.Korean)]
    [InlineData(1674, 850, OpenVisionLanguage.English)]
    public async Task IntegrationPreparationEntryShowsExistingRequirementWithoutExecuting(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var ownedRoot = Path.Combine(TestStorage.RootPath, "integration-preparation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ownedRoot);
            using var main = new MainViewModel(project, integrationSettingsPath: Path.Combine(ownedRoot, "settings.json"));
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.IsCompactLayout = width < 1280;
            main.Navigation.IsNarrowLayout = width < 1280;
            main.Integration.Setup.ExchangeRoot = Path.Combine(ownedRoot, "missing");
            var workspace = new SimulationIntegrationWorkspaceView { DataContext = main };
            var window = new Window { Content = workspace, Width = width, Height = height, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
                window.Show();
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                var monitorRoot = Path.Combine(TestStorage.RootPath, "integration-monitor");
                Directory.CreateDirectory(monitorRoot);
                File.WriteAllText(Path.Combine(monitorRoot, $"{width}-{height}-{language}-{Guid.NewGuid():N}.json"), System.Text.Json.JsonSerializer.Serialize(monitor));
                for (var attempt = 0; attempt < 200 && main.SceneSnapshots.Latest?.ProjectId != project.Id; attempt++) await Task.Delay(10);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                CapturePreparation("initial");
                var payload = store.Serialize(project);
                var dirty = main.HasUnsavedChanges;
                var camera = main.Camera.SelectedCameraId;
                var mmi = Assert.Single(Descendants<MmiOperatorLayoutView>(workspace));
                var details = mmi.IntegrationExchangeDetailsExpanderControl;
                Assert.False(details.IsExpanded);
                var entry = Assert.IsType<Button>(mmi.FindName("MmiInspectionStatusButton"));
                var requirement = Assert.IsType<TextBlock>(mmi.FindName("IntegrationSetupStatusTextBlock"));
                Assert.Same(main.Navigation.OpenInspectionStatusCommand, entry.Command);
                Assert.Equal(OpenVisionLanguageService.T("Integration.MmiInspectionStatus"), entry.Content);
                Assert.Equal(entry.Content, System.Windows.Automation.AutomationProperties.GetName(entry));
                Assert.True(entry.IsVisible && entry.IsEnabled);
                ((IInvokeProvider)new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                details.IsExpanded = true;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                requirement.BringIntoView();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(requirement.IsVisible);
                Assert.Equal(main.Integration.SetupWorkflowStatusText, requirement.Text);
                Assert.StartsWith("1/6", requirement.Text);
                var entryBounds = entry.TransformToAncestor(workspace).TransformBounds(new Rect(entry.RenderSize));
                Assert.True(new Rect(workspace.RenderSize).Contains(entryBounds));
                var scroll = Assert.IsType<ScrollViewer>(mmi.FindName("MmiExternalImagesScrollViewer"));
                await AssertWrappedTextReachableAsync(requirement, scroll, window, CapturePreparation, "long-guidance");
                requirement.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                scroll.ScrollToTop();
                ((IInvokeProvider)new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var tabs = Assert.IsType<TabControl>(mmi.FindName("MmiInspectionSettingsTabs"));
                Assert.Equal(3, tabs.SelectedIndex);
                Assert.True(tabs.IsVisible);
                if (width < 1280) Assert.True(main.Navigation.IsInspectionSettingsExpanded);
                var setupView = Assert.IsType<RightToolRegionView>(mmi.FindName("MmiIntegrationSetupView"));
                Assert.True(setupView.IntegrationSetupOnly && setupView.IsVisible);
                Assert.Same(main, setupView.DataContext);
                Assert.False(Assert.IsType<StackPanel>(setupView.FindName("InspectorNonIntegrationBeforeSetup")).IsVisible);
                Assert.False(Assert.IsType<StackPanel>(setupView.FindName("InspectorNonIntegrationAfterSetup")).IsVisible);
                Assert.False(Assert.IsType<Button>(setupView.FindName("CloseInspectorButton")).IsVisible);
                var exchangeField = Assert.IsType<TextBox>(setupView.FindName("IntegrationExchangeRootTextBox"));
                var saveSetup = Assert.IsType<Button>(setupView.FindName("SaveIntegrationSetupButton"));
                Assert.Equal(main.Integration.Setup.ExchangeRoot, exchangeField.Text);
                Assert.True(exchangeField.IsEnabled);
                Assert.Same(main.Integration.Setup.SaveSetupCommand, saveSetup.Command);
                Assert.True(details.IsExpanded);
                Assert.Equal(payload, store.Serialize(project));
                Assert.Equal(dirty, main.HasUnsavedChanges);
                Assert.Equal(camera, main.Camera.SelectedCameraId);
                Assert.False(main.Integration.IsBusy || main.Integration.IsTcpListening || main.IsRunMode);
                Assert.Empty(main.Integration.TransactionHistory);
                Assert.False(main.Integration.PublishInspectionHandoffCommand.CanExecute(null));
                Assert.False(main.Integration.ApplyResultToSimulationCommand.CanExecute(null));
                Assert.Equal(TimeSpan.Zero, main.SceneSnapshots.Latest!.SimulationTime);
                CapturePreparation("existing-tab-open");
                exchangeField.BringIntoView();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(exchangeField.IsVisible);
                var fieldBounds = exchangeField.TransformToAncestor(setupView).TransformBounds(new Rect(exchangeField.RenderSize));
                Assert.True(new Rect(setupView.RenderSize).Contains(fieldBounds), $"Exchange field {fieldBounds} outside setup {setupView.RenderSize}.");
                CapturePreparation("reachable-form-field");
                exchangeField.SetCurrentValue(TextBox.TextProperty, ownedRoot);
                exchangeField.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(ownedRoot, main.Integration.Setup.ExchangeRoot);
                Assert.True(main.Integration.Setup.HasUnsavedChanges);
                saveSetup.BringIntoView();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(saveSetup.IsVisible && saveSetup.IsEnabled);
                var saveBounds = saveSetup.TransformToAncestor(setupView).TransformBounds(new Rect(saveSetup.RenderSize));
                Assert.True(new Rect(setupView.RenderSize).Contains(saveBounds), $"Save setup {saveBounds} outside setup {setupView.RenderSize}.");
                ((IInvokeProvider)new ButtonAutomationPeer(saveSetup).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(main.Integration.Setup.HasUnsavedChanges);
                Assert.True(File.Exists(Path.Combine(ownedRoot, "settings.json")));
                Assert.False(main.Integration.IsTcpListening || main.Integration.IsBusy || main.IsRunMode);
                Assert.Empty(main.Integration.TransactionHistory);
                CapturePreparation("reachable-form-save");
                main.Navigation.IsInspectionSettingsExpanded = false;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.StartsWith("2/6", requirement.Text);
                Assert.Equal(main.Integration.SetupWorkflowStatusText, requirement.Text);
                var preview = Assert.IsType<Border>(mmi.FindName("MmiInspectionPreviewPanel"));
                Assert.True(preview.ActualHeight >= 240, $"Primary image height is {preview.ActualHeight}.");
                OpenVisionLanguageService.SetLanguage(language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, save: false);
                main.Integration.RefreshLocalization();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(main.Integration.SetupWorkflowStatusText, requirement.Text);
                Assert.Equal(OpenVisionLanguageService.T("Integration.MmiInspectionStatus"), entry.Content);
                Assert.Equal(payload, store.Serialize(project));
                Assert.Equal(dirty, main.HasUnsavedChanges);
                CapturePreparation("locale-and-recovery");
            }
            finally { window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            void CapturePreparation(string phase)
            {
                var evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR");
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(evidence, $"integration-preparation-{width}-{height}-{language}-{phase}.png"));
                encoder.Save(file);
            }
        });
    }

    [Theory]
    [InlineData(1034, 570, OpenVisionLanguage.Korean)]
    [InlineData(1034, 570, OpenVisionLanguage.English)]
    [InlineData(1674, 850, OpenVisionLanguage.Korean)]
    [InlineData(1674, 850, OpenVisionLanguage.English)]
    public async Task ProjectIntegrationIsReachableWithoutCameraAndRecoversWhenSampleOpens(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var sample = Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine");
            var ownedRoot = Path.Combine(TestStorage.RootPath, "integration-camera0", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ownedRoot);
            var project = new MachineProjectDocument { Name = "Integration without camera" };
            using var main = new MainViewModel(project, startupSamplePath: sample, integrationSettingsPath: Path.Combine(ownedRoot, "settings.json"));
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.IsCompactLayout = width < 1280;
            var workspace = new SimulationIntegrationWorkspaceView { DataContext = main };
            var window = new Window { Content = workspace, Width = width, Height = height, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
                window.Show();
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                var monitorRoot = Path.Combine(TestStorage.RootPath, "integration-monitor");
                Directory.CreateDirectory(monitorRoot);
                File.WriteAllText(Path.Combine(monitorRoot, $"{width}-{height}-{language}-{Guid.NewGuid():N}.json"), System.Text.Json.JsonSerializer.Serialize(monitor));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var mmi = Assert.Single(Descendants<MmiOperatorLayoutView>(workspace));
                var entry = Assert.IsType<Button>(mmi.FindName("MmiEmptyCameraIntegrationButton"));
                Assert.False(main.Camera.HasVirtualCamera);
                Assert.True(entry.IsVisible && entry.IsEnabled);
                Assert.Same(main.Navigation.OpenInspectionStatusCommand, entry.Command);
                var payload = new ProjectDocumentStore().Serialize(project);
                var dirty = main.HasUnsavedChanges;
                ((IInvokeProvider)new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var tabs = Assert.IsType<TabControl>(mmi.FindName("MmiInspectionSettingsTabs"));
                Assert.Equal(3, tabs.SelectedIndex);
                Assert.True(tabs.IsVisible);
                var details = mmi.IntegrationExchangeDetailsExpanderControl;
                Assert.True(details.IsVisible);
                details.IsExpanded = true;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(mmi.IntegrationTransactionHistoryFilterControl.IsEnabled);
                Assert.True(mmi.IntegrationTransactionDiagnosticsFilterControl.IsEnabled);
                Assert.Same(main, details.DataContext);
                Assert.Equal(main.Integration.SetupWorkflowStatusText, mmi.IntegrationSetupWorkflowStatusTextBlockControl.Text);
                Assert.Equal(payload, new ProjectDocumentStore().Serialize(project));
                Assert.Equal(dirty, main.HasUnsavedChanges);
                Assert.False(main.IsRunning || main.Integration.IsBusy || main.Integration.IsTcpListening);
                Assert.True(await main.OpenProjectReplacingCurrentAsync(sample).WaitAsync(TimeSpan.FromSeconds(10)));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(main.Camera.HasVirtualCamera);
                Assert.False(entry.IsVisible);
                Assert.NotNull(main.Camera.SelectedCameraId);
                main.Navigation.OpenInspectionStatusCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(details.IsVisible);
                Assert.Same(main.Integration.TransactionHistoryFilters, mmi.IntegrationTransactionHistoryFilterControl.ItemsSource);
                Assert.False(main.IsRunning || main.Integration.IsTcpListening);
            }
            finally { window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    private static async Task AssertWrappedTextReachableAsync(TextBlock text, ScrollViewer scroll, Window window, Action<string> capture, string phase)
    {
        var value = "TOP" + Environment.NewLine + string.Join(" / ", Enumerable.Repeat(new string('W', 80), 12)) + Environment.NewLine + "END";
        text.SetCurrentValue(TextBlock.TextProperty, value);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        Assert.True(scroll.ScrollableHeight > 0 && text.ActualWidth <= scroll.ViewportWidth + 1);
        var viewport = VisualDescendants<ScrollContentPresenter>(scroll).First();
        foreach (var end in new[] { false, true })
        {
            var pointer = end ? text.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) : text.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var character = pointer.GetCharacterRect(end ? LogicalDirection.Backward : LogicalDirection.Forward);
            Assert.True(character.Height > 0, "Long text insertion point has no layout.");
            var samples = new List<object>();
            var previousOffset = double.NaN;
            var previousBounds = Rect.Empty;
            var stableSamples = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                character = pointer.GetCharacterRect(end ? LogicalDirection.Backward : LogicalDirection.Forward);
                var current = text.TransformToAncestor(viewport).TransformBounds(character);
                stableSamples = current.Top >= -1 && current.Bottom <= viewport.ActualHeight + 1
                    && Math.Abs(scroll.VerticalOffset - previousOffset) < 0.001 && Math.Abs(current.Top - previousBounds.Top) < 0.001 ? stableSamples + 1 : 0;
                if (attempt % 10 == 0 || stableSamples >= 3) samples.Add(new { attempt, offset = scroll.VerticalOffset, current = current.ToString(), viewport = viewport.RenderSize.ToString(), character = character.ToString(), scrollable = scroll.ScrollableHeight, stableSamples });
                if (stableSamples >= 3) break;
                if (attempt % 10 == 0 && (current.Top < -1 || current.Bottom > viewport.ActualHeight + 1))
                    scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + current.Top - (viewport.ActualHeight - character.Height) / 2, 0, scroll.ScrollableHeight));
                previousOffset = scroll.VerticalOffset;
                previousBounds = current;
                await Task.Delay(10);
            }
            var evidenceRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidenceRoot)) File.WriteAllText(Path.Combine(evidenceRoot, $"long-scroll-{window.Width}-{window.Height}-{OpenVisionLanguageService.CurrentLanguage}-{phase}-{(end ? "end" : "top")}.json"), System.Text.Json.JsonSerializer.Serialize(samples));
            Assert.True(stableSamples >= 3, $"Long text scrolling/layout did not settle inside viewport. phase={phase}, end={end}, current={previousBounds}, offset={scroll.VerticalOffset}, viewport={viewport.RenderSize}, character={character}, scrollable={scroll.ScrollableHeight}, textHeight={text.ActualHeight}, valueRetained={text.Text == value}.");
            Assert.Equal(value, text.Text);
            var viewportBounds = new Rect(viewport.RenderSize);
            var textBounds = text.TransformToAncestor(viewport).TransformBounds(new Rect(text.RenderSize));
            var characterBounds = text.TransformToAncestor(viewport).TransformBounds(character);
            Assert.True(viewportBounds.IntersectsWith(textBounds), $"Long text does not intersect viewport: {textBounds} / {viewportBounds}.");
            Assert.True(characterBounds.Top >= -1 && characterBounds.Bottom <= viewportBounds.Bottom + 1, $"Required {(end ? "end" : "top")} character is outside viewport: {characterBounds} / {viewportBounds}.");
            var marker = end ? "END" : "TOP";
            var glyph = Glyphs(System.Windows.Media.VisualTreeHelper.GetDrawing(text)).Single(run => run.GlyphRun.Characters is { } characters && new string(characters.ToArray()).Contains(marker, StringComparison.Ordinal));
            Assert.NotEmpty(glyph.GlyphRun.GlyphIndices);
            var glyphBounds = text.TransformToAncestor(viewport).TransformBounds(glyph.Bounds);
            Assert.True(viewportBounds.IntersectsWith(glyphBounds), $"Rendered {marker} glyph is outside viewport: {glyphBounds} / {viewportBounds}.");
            capture(phase + (end ? "-end" : "-top"));
        }
        static IEnumerable<System.Windows.Media.GlyphRunDrawing> Glyphs(System.Windows.Media.Drawing? drawing)
        {
            if (drawing is System.Windows.Media.GlyphRunDrawing glyph) yield return glyph;
            if (drawing is System.Windows.Media.DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var nested in Glyphs(child)) yield return nested;
        }
    }

    private static CameraViewProjection CaptureProjection(
        ComboBox selector,
        TextBlock resultText,
        TextBlock evidenceDetails) => new(
        selector.SelectedValue?.ToString() ?? string.Empty,
        resultText.Text,
        evidenceDetails.Text);

    private static CameraCommissioningProjection CreateProjection(VirtualCameraSnapshot snapshot) => new(
        snapshot,
        HasCameraDefinition: true,
        FallbackCameraName: snapshot.Name,
        ImageSource: new VirtualSingleImageSourceDefinition
        {
            SourceRelativePath = "images/part.pgm",
            Width = 2,
            Height = 2,
            PixelFormat = "Mono8"
        },
        ProjectPath: @"D:\OpenVisionLab-TestData\Machine\r19-priority-25-inspector-view\machine.ovmachine",
        SelectedCameraRecipe: "recipe-1",
        SimulationFixedStep: TimeSpan.FromMilliseconds(5),
        RuntimeRunMode: SimulationRunMode.Paused,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        ControlOwner: SimulationControlOwner.EmbeddedSequence,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: SequenceExecutionStatus.Completed);

    private static VirtualCameraSnapshot CreateCameraSnapshot(
        string cameraId,
        string cameraName,
        string acquisitionId,
        string inspectionId,
        PlaceholderInspectionDecision decision,
        string workpieceComponentId)
    {
        var frame = new VirtualCameraFrameEvidence(
            $"frame-{cameraId}",
            "images/part.pgm",
            new string('A', 64),
            4,
            2,
            2,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            inspectionId,
            acquisitionId,
            cameraId,
            "recipe-1",
            frame.FrameId,
            decision,
            decision == PlaceholderInspectionDecision.Pass ? "Pass result" : "Fail result",
            new Dictionary<string, double> { ["score"] = decision == PlaceholderInspectionDecision.Pass ? 0.9 : 0.2 });
        var result = new VirtualCameraAcquisitionResult(
            acquisitionId,
            cameraId,
            "recipe-1",
            1,
            decision,
            frame,
            inspection,
            WorkpieceComponentId: workpieceComponentId,
            WorkpieceInstanceId: "workpiece-instance-1");
        return new VirtualCameraSnapshot(
            cameraId,
            cameraName,
            VirtualCameraState.FrameReady,
            1,
            acquisitionId,
            "recipe-1",
            0,
            0,
            result,
            frame);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed record CameraInspectorContext(CameraCommissioningViewModel Camera, ShellNavigationViewModel? Navigation = null);

    private sealed record CameraViewProjection(string SelectedCameraId, string ResultText, string EvidenceDetails);

    private sealed record CameraSwitchResult(
        CameraViewProjection First,
        CameraViewProjection Second,
        string PassText,
        string FailText,
        string MockSourceText);
}
