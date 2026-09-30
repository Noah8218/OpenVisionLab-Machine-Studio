using System.Windows;
using System.Windows.Controls;
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
                        Assert.Equal(expanded ? Visibility.Collapsed : Visibility.Visible, preview.Visibility);
                        Assert.Equal(expanded || narrow ? 0 : 2, Grid.GetColumn(setup));
                        Assert.Equal(expanded ? 3 : 1, Grid.GetColumnSpan(setup));
                        var bounds = new Rect(view.RenderSize);
                        foreach (var control in new FrameworkElement[] { toggle, tabs })
                        {
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
                        var source = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraSourceSettings"));
                        var scroll = Assert.IsType<ScrollViewer>(source.FindName("CameraSourceFieldsScrollViewer"));
                        source.BrowseCameraSourceButton.BringIntoView();
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var browseBounds = source.BrowseCameraSourceButton.TransformToAncestor(scroll).TransformBounds(new Rect(source.BrowseCameraSourceButton.RenderSize));
                        Assert.True(browseBounds.Top >= 0 && browseBounds.Bottom <= scroll.ViewportHeight);
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
    [InlineData(1280, 760, true, false, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, false, false, OpenVisionLanguage.Korean)]
    [InlineData(794, 1032, true, true, OpenVisionLanguage.English)]
    [InlineData(794, 760, true, true, OpenVisionLanguage.English)]
    public async Task CapturedImageBindingClearsStalePixelsAndRecoversAfterMalformedInput(double width, double height, bool compact, bool narrow, OpenVisionLanguage language)
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
                var frameReady = CreateCameraSnapshot("camera-1", "Camera 1", "acquisition-1", "inspection-1", PlaceholderInspectionDecision.Pass, "workpiece-1");
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
                    Assert.True(new Rect(view.RenderSize).Contains(error.TransformToAncestor(view).TransformBounds(new Rect(error.RenderSize))));

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
