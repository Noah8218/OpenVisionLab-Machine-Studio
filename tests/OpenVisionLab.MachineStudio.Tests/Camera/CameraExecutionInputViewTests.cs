using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class CameraExecutionInputViewTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task InspectionCameraListSynchronizesLongSelectionWithImageAndSettings(double width, double height, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            foreach (var camera in project.Devices.Where(device => device.Kind == DeviceKind.Camera))
            {
                camera.Name = language == OpenVisionLanguage.Korean ? new string('검', 80) : new string('C', 80);
                camera.Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = "recipe-" + camera.Id };
            }
            using var main = new MainViewModel(project);
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.IsCompactLayout = width == 1280;
            var left = new OpenVisionLab.MachineStudio.View.Project.LeftToolRegionView { DataContext = main };
            var center = new MmiOperatorLayoutView { DataContext = main };
            var root = new Grid { Background = (Brush)Application.Current.FindResource("Surface.Root") };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width == 1280 ? 240 : 300) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(left);
            root.Children.Add(center);
            Grid.SetColumn(center, 1);
            var window = new Window { Content = root, Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Directory.CreateDirectory(TestStorage.RootPath);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "camera-list-monitor.jsonl"), JsonSerializer.Serialize(monitor) + Environment.NewLine);
                var panel = Assert.IsType<Border>(left.FindName("NavigationPanelSurface"));
                var header = Assert.IsType<Border>(left.FindName("NavigationPanelHeader"));
                var listTitle = Assert.IsType<TextBlock>(left.FindName("InspectionCameraListTitle"));
                Assert.Equal(Color.FromRgb(248, 250, 252), Assert.IsType<SolidColorBrush>(panel.Background).Color);
                Assert.Equal(Color.FromRgb(220, 227, 235), Assert.IsType<SolidColorBrush>(panel.BorderBrush).Color);
                Assert.Equal(new Thickness(0, 0, 1, 0), panel.BorderThickness);
                Assert.Equal(new Thickness(10, 8, 10, 8), header.Padding);
                Assert.Equal(new Thickness(0, 0, 0, 1), header.BorderThickness);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(header.Background).Color);
                double headerHeight = width < 1400 ? 35.125 : 35.85;
                Assert.InRange(header.ActualHeight, headerHeight - 0.5, headerHeight + 1);
                IEnumerable<GlyphRunDrawing> ListGlyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph] : drawing is DrawingGroup group ? group.Children.SelectMany(ListGlyphs) : [];
                void AssertGlyphs(TextBlock text, double size, int weight)
                {
                    var glyphs = ListGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.Equal(weight, text.FontWeight.ToOpenTypeWeight());
                    Assert.All(glyphs, glyph =>
                    {
                        Assert.Equal(size, glyph.GlyphRun.FontRenderingEmSize, 3);
                        bool malgunFallback = glyph.GlyphRun.GlyphTypeface.FontUri.LocalPath.Contains("malgun", StringComparison.OrdinalIgnoreCase);
                        Assert.Equal(weight == 600 && malgunFallback ? 700 : weight, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
                    });
                }
                AssertGlyphs(listTitle, width < 1400 ? 12.5 : 13, 700);
                var before = store.Serialize(project);
                Capture(window, width, height, language, "inspection-list-initial");
                var list = Assert.IsType<ListBox>(left.FindName("InspectionCameraList"));
                Assert.True(list.IsVisible);
                Assert.DoesNotContain(Descendants(left).OfType<TabControl>(), tab => tab.IsVisible);
                Assert.DoesNotContain(Descendants(left).OfType<OpenVisionLab.MachineStudio.View.Project.EquipmentOutlineView>(), outline => outline.IsVisible);
                Assert.False(main.Navigation.IsEquipmentOutlineVisible);
                Assert.Equal(main.Camera.VirtualCameras.Count, list.Items.Count);
                Assert.Equal(main.Camera.SelectedCameraId, list.SelectedValue);
                var trigger = Assert.Single(Interaction.GetTriggers(list).OfType<Microsoft.Xaml.Behaviors.EventTrigger>());
                var action = Assert.Single(trigger.Actions.OfType<InvokeCommandAction>());
                Assert.Same(main.Camera.SelectCameraCommand, action.Command);
                var cameras = main.Camera.VirtualCameras;
                foreach (var camera in cameras)
                {
                    list.SetCurrentValue(Selector.SelectedValueProperty, camera.Id);
                    list.ScrollIntoView(camera);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(camera.Id, main.Camera.SelectedCameraId);
                    Assert.Equal(camera.Camera!.NumericalInput!.RecipeName, main.Camera.CaptureEditor.RecipeName);
                    Assert.Equal(camera.Id, Assert.IsType<ComboBox>(center.FindName("MmiCameraSelectionComboBox")).SelectedValue);
                    var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(camera));
                    Assert.True(item.IsSelected);
                    var chrome = Assert.IsType<Border>(item.Template.FindName("InspectionCameraItemChrome", item));
                    Assert.Equal(Color.FromRgb(228, 244, 239), Assert.IsType<SolidColorBrush>(chrome.Background).Color);
                    Assert.Equal(Color.FromRgb(8, 127, 115), Assert.IsType<SolidColorBrush>(chrome.BorderBrush).Color);
                    foreach (var renderedRow in list.Items.Cast<object>().Select(value => Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(value))))
                    {
                        var rowChrome = Assert.IsType<Border>(renderedRow.Template.FindName("InspectionCameraItemChrome", renderedRow));
                        Assert.Equal(Bounds(list, window).X, Bounds(rowChrome, window).X);
                        Assert.Equal(list.ActualWidth, rowChrome.ActualWidth);
                    }
                    Assert.Equal(new Thickness(6, 4, 6, 4), item.Padding);
                    Assert.Equal(new Thickness(0), item.Margin);
                    Assert.Equal(new CornerRadius(4), chrome.CornerRadius);
                    var rowName = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowName");
                    var rowId = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowId");
                    var rowIcon = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowIcon");
                    var rowKind = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowKind");
                    Assert.Equal("▣", rowIcon.Text);
                    Assert.Equal(OpenVisionLanguageService.T("Camera.TwoDImage"), rowKind.Text);
                    AssertGlyphs(rowName, width < 1400 ? 12.5 : 13, 600);
                    AssertGlyphs(rowIcon, width < 1400 ? 12.5 : 13, 600);
                    AssertGlyphs(rowId, 10.5, 600);
                    AssertGlyphs(rowKind, 10.5, 400);
                    Assert.Equal(Color.FromRgb(8, 127, 115), Assert.IsType<SolidColorBrush>(rowName.Foreground).Color);
                    Assert.Equal(Color.FromRgb(82, 104, 124), Assert.IsType<SolidColorBrush>(rowId.Foreground).Color);
                    Assert.Equal(Color.FromRgb(82, 104, 124), Assert.IsType<SolidColorBrush>(rowKind.Foreground).Color);
                    foreach (var text in Descendants(item).OfType<TextBlock>().Where(text => text.IsVisible && (text.Text == camera.Name || text.Text == camera.Id)))
                    {
                        var drawing = VisualTreeHelper.GetDrawing(text);
                        var renderedText = string.Concat(ListGlyphs(drawing).Select(glyph => new string(glyph.GlyphRun.Characters.ToArray())));
                        Assert.Equal(text.Text, renderedText);
                        Assert.False(text.ClipToBounds);
                        Assert.True(new Rect(item.RenderSize).Contains(text.TransformToAncestor(item).TransformBounds(drawing.Bounds)), $"{text.Text}: ink={drawing.Bounds}, row={item.RenderSize}");
                        Assert.True(new Rect(item.RenderSize).Contains(Bounds(text, item)));
                    }
                    Assert.Contains(Descendants(item).OfType<TextBlock>(), text => text.Text == camera.Name);
                    Assert.Contains(Descendants(item).OfType<TextBlock>(), text => text.Text == camera.Id);
                    Capture(window, width, height, language, "inspection-list-selected-" + camera.Id);
                }
                main.Camera.SelectedCameraId = cameras[0].Id;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(cameras[0].Id, list.SelectedValue);
                if (width == 1280 && language == OpenVisionLanguage.English)
                {
                    double originalMaxHeight = list.MaxHeight;
                    list.SetCurrentValue(FrameworkElement.MaxHeightProperty, 60d);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var scroll = Assert.Single(Descendants(list).OfType<ScrollViewer>());
                    var viewport = Assert.Single(Descendants(scroll).OfType<ScrollContentPresenter>());
                    Assert.True(scroll.CanContentScroll);
                    Assert.Single(Descendants(list).OfType<VirtualizingStackPanel>());
                    Assert.True(scroll.ScrollableHeight > 0);
                    scroll.ScrollToEnd();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
                    var lastRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(cameras[^1]));
                    Assert.True(new Rect(viewport.RenderSize).IntersectsWith(Bounds(lastRow, viewport)));
                    Assert.Equal(cameras[0].Id, list.SelectedValue);
                    Assert.Equal(cameras[0].Id, main.Camera.SelectedCameraId);
                    Capture(window, width, height, language, "inspection-list-scroll-end");
                    scroll.ScrollToTop();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(0, scroll.VerticalOffset);
                    var firstRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(cameras[0]));
                    Assert.True(new Rect(viewport.RenderSize).IntersectsWith(Bounds(firstRow, viewport)));
                    Assert.Equal(cameras[0].Id, list.SelectedValue);
                    Assert.Equal(cameras[0].Id, main.Camera.SelectedCameraId);
                    Capture(window, width, height, language, "inspection-list-scroll-return");
                    list.SetCurrentValue(FrameworkElement.MaxHeightProperty, originalMaxHeight);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                }
                main.Navigation.IsEquipmentWorkspace = true;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(list.IsVisible);
                Assert.True(main.Navigation.IsEquipmentOutlineVisible);
                Assert.Equal(Color.FromRgb(248, 250, 252), Assert.IsType<SolidColorBrush>(panel.Background).Color);
                Assert.Equal(new Thickness(10, 8, 10, 0), header.Padding);
                main.Navigation.IsSimulationWorkspace = true;
                main.Navigation.SelectedExecutionTabIndex = 1;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(list.IsVisible);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("Surface.Panel")).Color, Assert.IsType<SolidColorBrush>(panel.Background).Color);
                Assert.Equal(new Thickness(1), panel.BorderThickness);
                Assert.True(main.Navigation.IsEquipmentOutlineVisible);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(header.Background).Color);
                Assert.Equal(new Thickness(10, 8, 10, 0), header.Padding);
                main.Navigation.IsResultsWorkspace = true;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(list.IsVisible);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("Surface.Panel")).Color, Assert.IsType<SolidColorBrush>(panel.Background).Color);
                Assert.Equal(new Thickness(1), panel.BorderThickness);
                main.Navigation.IsInspectionWorkspace = true;
                main.Navigation.SelectedLeftToolTabIndex = 1;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(list.IsVisible);
                Assert.Equal(cameras[0].Id, list.SelectedValue);
                var title = Assert.IsType<TextBlock>(left.FindName("InspectionCameraListTitle"));
                Assert.Equal(language == OpenVisionLanguage.Korean ? "카메라" : "Cameras", title.Text);
                Assert.True(new Rect(window.RenderSize).Contains(Bounds(list, window)));
                Assert.Equal(before, store.Serialize(project));
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
                Assert.Equal(0, main.SceneSnapshots.Latest!.TickIndex);
                Capture(window, width, height, language, "inspection-list-workspace-return");
            }
            finally { window.Close(); left.DataContext = null; center.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task InspectionCameraListRetainsRowsDuringRefreshAndClearsOldProjectSelection(double width, double height, OpenVisionLanguage language)
    {
        await ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var fixture = new CameraExecutionInputFixture();
            fixture.Project.Devices[0].Name = "Height in a name remains 2D";
            fixture.Project.Devices[0].Id = "camera-" + new string('I', 70);
            fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false, () => Task.CompletedTask, () => { }, exception => throw exception);
            navigation.IsInspectionWorkspace = true;
            navigation.IsCompactLayout = width == 1280;
            var context = new InputViewContext(fixture.Camera, navigation);
            var left = new OpenVisionLab.MachineStudio.View.Project.LeftToolRegionView { DataContext = context };
            var center = new MmiOperatorLayoutView { DataContext = context };
            var root = new Grid { Background = (Brush)Application.Current.FindResource("Surface.Root") };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width == 1280 ? 240 : 300) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(left);
            root.Children.Add(center);
            Grid.SetColumn(center, 1);
            var window = new Window { Content = root, Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                Arrange(root, width, height);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Console.WriteLine(JsonSerializer.Serialize(monitor));
                Capture(window, width, height, language, "inspection-list-runtime-initial");
                var list = Assert.IsType<ListBox>(left.FindName("InspectionCameraList"));
                var firstRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(fixture.Camera.VirtualCameras[0]));
                Assert.Equal("▣", Descendants(firstRow).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowIcon").Text);
                Assert.Equal(OpenVisionLanguageService.T("Camera.TwoDImage"), Descendants(firstRow).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowKind").Text);
                var longId = Descendants(firstRow).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowId");
                Assert.Equal(fixture.Camera.VirtualCameras[0].Id, longId.Text);
                var longIdDrawing = VisualTreeHelper.GetDrawing(longId);
                IEnumerable<GlyphRunDrawing> IdGlyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph] : drawing is DrawingGroup group ? group.Children.SelectMany(IdGlyphs) : [];
                Assert.Equal(longId.Text, string.Concat(IdGlyphs(longIdDrawing).Select(glyph => new string(glyph.GlyphRun.Characters.ToArray()))));
                Assert.False(longId.ClipToBounds);
                Assert.True(new Rect(firstRow.RenderSize).Contains(longId.TransformToAncestor(firstRow).TransformBounds(longIdDrawing.Bounds)));
                if (width == 1280) Assert.True(longId.ActualHeight > 15.225);
                Assert.True(new Rect(firstRow.RenderSize).Contains(Bounds(longId, firstRow)));
                var initialChrome = Assert.IsType<Border>(firstRow.Template.FindName("InspectionCameraItemChrome", firstRow));
                Assert.Equal(Color.FromRgb(228, 244, 239), Assert.IsType<SolidColorBrush>(initialChrome.Background).Color);
                var before = new ProjectDocumentStore().Serialize(fixture.Project);
                list.SetCurrentValue(Selector.SelectedValueProperty, "camera-2");
                Arrange(root, width, height);
                Assert.Equal("camera-2", fixture.Camera.SelectedCameraId);
                var preview = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<Image>(center.FindName("MmiVirtualCameraImageViewer")).Source);
                var pixels = new byte[4];
                preview.CopyPixels(pixels, 2, 0);
                Assert.Equal(new byte[] { 0, 64, 128, 255 }, pixels);
                var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(fixture.Camera.SelectedVirtualCamera));
                Assert.False(firstRow.IsSelected);
                Assert.Equal(firstRow.IsMouseOver ? Color.FromRgb(237, 243, 247) : Colors.Transparent, Assert.IsType<SolidColorBrush>(initialChrome.Background).Color);
                Assert.False(firstRow.IsSelected);
                Assert.Equal(Bounds(list, window).X, Bounds(initialChrome, window).X);
                Assert.Equal(list.ActualWidth, initialChrome.ActualWidth);
                Assert.Equal(Color.FromRgb(36, 52, 72), Assert.IsType<SolidColorBrush>(firstRow.Foreground).Color);
                var oldName = Descendants(row).OfType<TextBlock>().Single(text => text.Text == fixture.Camera.SelectedVirtualCamera!.Name);
                for (int tick = 0; tick < 3; tick++)
                {
                    fixture.Camera.NotifyPresentationChanged(invalidateCommands: false);
                    Arrange(root, width, height);
                    Assert.Same(row, list.ItemContainerGenerator.ContainerFromItem(fixture.Camera.SelectedVirtualCamera));
                    Assert.Same(oldName, Descendants(row).OfType<TextBlock>().Single(text => text.Text == fixture.Camera.SelectedVirtualCamera!.Name));
                    Assert.True(row.IsSelected);
                }
                Assert.Equal(before, new ProjectDocumentStore().Serialize(fixture.Project));
                fixture.Project.Devices[1].Name = new string('R', 80);
                fixture.Project.Devices[1].Camera!.NumericalInput = new NumericalCameraSettings { InputKind = NumericalCameraInputKind.HeightMap3D };
                var renamed = new ProjectDocumentStore().Serialize(fixture.Project);
                fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
                Arrange(root, width, height);
                row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(fixture.Camera.SelectedVirtualCamera));
                Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == new string('R', 80));
                Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("Camera.HeightMap3D"));
                Assert.Equal("▤", Descendants(row).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowIcon").Text);
                var appliedType = Descendants(row).OfType<TextBlock>().Single(text => text.Name == "InspectionCameraRowKind");
                fixture.Camera.CaptureEditor.InputKind = NumericalCameraInputKind.Image2D;
                Arrange(root, width, height);
                Assert.Equal(OpenVisionLanguageService.T("Camera.HeightMap3D"), appliedType.Text);
                fixture.Camera.SelectCameraCommand.Execute(fixture.Project.Devices[0].Id);
                Arrange(root, width, height);
                Assert.Equal(fixture.Project.Devices[0].Id, list.SelectedValue);
                fixture.Camera.SelectCameraCommand.Execute("camera-2");
                Arrange(root, width, height);
                Assert.Equal("camera-2", list.SelectedValue);
                Assert.Equal(renamed, new ProjectDocumentStore().Serialize(fixture.Project));
                fixture.Project = new MachineProjectDocument { Id = "no-camera-project", Name = "No cameras" };
                fixture.Snapshot = fixture.CreateSnapshot(Array.Empty<VirtualCameraSnapshot>(), projectId: fixture.Project.Id);
                fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
                Arrange(root, width, height);
                Assert.Empty(list.Items);
                Assert.Null(fixture.Camera.SelectedCameraId);
                Assert.Null(list.SelectedValue);
                var empty = Assert.IsType<TextBlock>(left.FindName("InspectionCameraEmptyHint"));
                Assert.True(empty.IsVisible);
                Assert.False(list.IsVisible);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "카메라가 없습니다. 장비 구성에서 추가하세요." : "No cameras. Add a camera in Equipment.", empty.Text);
                Assert.Null(Assert.IsType<Image>(center.FindName("MmiVirtualCameraImageViewer")).Source);
                var emptyWorkspace = Assert.IsType<Grid>(center.FindName("MmiEmptyCameraWorkspace"));
                var configuredWorkspace = Assert.IsType<Border>(center.FindName("MmiCameraConfiguredWorkspace"));
                Assert.True(emptyWorkspace.IsVisible);
                Assert.False(configuredWorkspace.IsVisible);
                var title = Assert.IsType<TextBlock>(center.FindName("MmiEmptyCameraTitle"));
                Assert.Equal(language == OpenVisionLanguage.Korean ? "카메라가 없습니다" : "No cameras", title.Text);
                AssertReadable(center, "MmiEmptyCameraHint", "MmiEmptyCameraInspectorHint");
                Assert.Equal(420, Assert.IsType<TextBlock>(center.FindName("MmiEmptyCameraHint")).ActualWidth);
                Assert.Equal(360, Assert.IsType<Border>(center.FindName("MmiEmptyCameraInspector")).ActualWidth);
                Assert.Equal(Color.FromRgb(233, 237, 243), Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(center.FindName("MmiEmptyCameraStage")).Background).Color);
                var emptyProject = new ProjectDocumentStore().Serialize(fixture.Project);
                Capture(window, width, height, language, "inspection-list-empty");
                navigation.OpenInspectionStatusCommand.Execute(null);
                Arrange(root, width, height);
                Assert.Equal(3, Assert.IsType<TabControl>(center.FindName("MmiInspectionSettingsTabs")).SelectedIndex);
                Assert.False(emptyWorkspace.IsVisible);
                Assert.True(configuredWorkspace.IsVisible);
                Capture(window, width, height, language, "inspection-list-empty-setup");
                var settings = Assert.IsType<TabControl>(center.FindName("MmiInspectionSettingsTabs"));
                settings.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                settings.GetBindingExpression(Selector.SelectedIndexProperty)!.UpdateSource();
                Arrange(root, width, height);
                Assert.Equal(0, navigation.SelectedInspectionSettingsTabIndex);
                Assert.True(emptyWorkspace.IsVisible);
                Assert.False(configuredWorkspace.IsVisible);
                Assert.Equal(emptyProject, new ProjectDocumentStore().Serialize(fixture.Project));
                fixture.Project.Devices.Add(new DeviceDefinition { Id = "replacement-camera", Name = "Replacement camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition() });
                fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
                Arrange(root, width, height);
                Assert.Single(list.Items);
                Assert.Equal("replacement-camera", list.SelectedValue);
                Assert.False(empty.IsVisible);
                Assert.False(emptyWorkspace.IsVisible);
                Assert.True(configuredWorkspace.IsVisible);
                Assert.Equal(0, fixture.SideEffects);
                Capture(window, width, height, language, "inspection-list-replaced-project");
                return true;
            }
            finally { window.Close(); left.DataContext = null; center.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }


    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task InputKindChoicesUseReadableNamesWithoutApplyingDraft(double width, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var camera = project.Devices.First(device => device.Kind == DeviceKind.Camera);
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = camera.Id;
            var view = new CameraImageSourceView { DataContext = main.Camera, CaptureSection = 2 };
            var surface = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.FindResource("Surface.Panel"), Child = view };
            var window = new Window { Content = surface, Width = width, Height = 620, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                var editor = main.Camera.CaptureEditor;
                editor.Parameters[0].Value = "NaN";
                var before = store.Serialize(project);
                foreach (int section in new[] { 2, -1 })
                {
                    view.CaptureSection = section;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var combo = Descendants(view).OfType<ComboBox>().Single(control => control.IsVisible && ReferenceEquals(control.ItemsSource, editor.InputKinds));
                    foreach (var kind in new[] { NumericalCameraInputKind.Image2D, NumericalCameraInputKind.HeightMap3D })
                    {
                        combo.SelectedItem = kind;
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        Assert.Equal(kind, editor.InputKind);
                        string expected = kind == NumericalCameraInputKind.Image2D ? (language == OpenVisionLanguage.Korean ? "2D 이미지" : "2D image") : (language == OpenVisionLanguage.Korean ? "3D 높이맵" : "3D height map");
                        var text = Descendants(combo).OfType<TextBlock>().Single(block => block.IsVisible && block.Text == expected);
                        AssertChoiceFits(text, combo);
                        Capture(surface, width, surface.ActualHeight, language, $"input-kind-section-{section}-{kind}");
                    }
                    combo.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var popup = Descendants(combo).OfType<Popup>().Single();
                    Assert.Contains(Descendants(popup.Child).OfType<TextBlock>(), text => text.Text == (language == OpenVisionLanguage.Korean ? "2D 이미지" : "2D image"));
                    Assert.Contains(Descendants(popup.Child).OfType<TextBlock>(), text => text.Text == (language == OpenVisionLanguage.Korean ? "3D 높이맵" : "3D height map"));
                    combo.IsDropDownOpen = false;
                }
                Assert.Equal("NaN", editor.Parameters[0].Value);
                Assert.Equal(before, store.Serialize(project));
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
            }
            finally { window.Close(); view.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task AppliedSourceSummaryStaysWithImageAndFollowsLanguage(double width, double height, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var camera = project.Devices.First(device => device.Kind == DeviceKind.Camera);
            string recipe = language == OpenVisionLanguage.Korean ? new string('검', 80) : new string('R', 80);
            camera.Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = recipe, InputKind = NumericalCameraInputKind.HeightMap3D, Version = 7, ConfirmedVersion = 7 };
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = camera.Id;
            main.Navigation.IsCompactLayout = width == 1280;
            var view = new MmiOperatorLayoutView { DataContext = main };
            var window = new Window { Content = view, Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var before = store.Serialize(project);
                var summary = Assert.IsType<TextBlock>(view.FindName("MmiAppliedSourceSummary"));
                var hint = Assert.IsType<TextBlock>(view.FindName("MmiSourceActionHint"));
                var scroll = Assert.IsType<ScrollViewer>(view.FindName("MmiSourceSummaryScrollViewer"));
                var image = Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame"));
                Assert.Contains(recipe, summary.Text);
                Assert.Contains(language == OpenVisionLanguage.Korean ? "3D 높이맵" : "3D height map", summary.Text);
                Assert.DoesNotContain("HeightMap3D", summary.Text);
                Assert.Equal(main.Camera.CameraCommissioningHintText, hint.Text);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "프로젝트를 저장하고 수치 촬영 설정의 레시피·버전을 확인하세요." : "Save the project and confirm the recipe and version of the numerical capture settings.", hint.Text);
                Assert.True(image.ActualHeight >= 220, $"Image height {image.ActualHeight}.");
                Assert.True(new Rect(window.RenderSize).Contains(Bounds(image, window)));
                var sourceMenu = Assert.IsType<MenuItem>(view.FindName("MmiSourceSummaryMenu"));
                var popup = Assert.IsType<Popup>(sourceMenu.Template.FindName("PART_Popup", sourceMenu));
                var imageBounds = Bounds(image, window);
                sourceMenu.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, true);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(popup.IsOpen);
                Assert.True(new Rect(popup.Child.RenderSize).Contains(Bounds(scroll, (Visual)popup.Child)));
                Assert.True(scroll.ActualHeight <= 280.5);
                Assert.Equal(imageBounds, Bounds(image, window));
                AssertReadable(view, "MmiAppliedSourceSummary", "MmiSourceActionHint");
                Capture(window, width, height, language, "applied-source-long");
                main.Camera.CaptureEditor.InputKind = NumericalCameraInputKind.Image2D;
                Assert.Contains(recipe, summary.Text);
                Assert.Contains(language == OpenVisionLanguage.Korean ? "3D 높이맵" : "3D height map", summary.Text);
                var otherLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(otherLanguage, save: false);
                main.Camera.RefreshLocalization();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Contains(otherLanguage == OpenVisionLanguage.Korean ? "3D 높이맵" : "3D height map", summary.Text);
                Assert.Contains(recipe, summary.Text);
                Capture(window, width, height, otherLanguage, "applied-source-language-change");
                var otherCamera = project.Devices.First(device => device.Kind == DeviceKind.Camera && device.Id != camera.Id);
                main.Camera.SelectedCameraId = otherCamera.Id;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.DoesNotContain(recipe, summary.Text);
                Assert.Equal(main.Camera.CurrentCameraInputKindText, summary.Text);
                Assert.False(main.Camera.HasSelectedCameraInput);
                Capture(window, width, height, otherLanguage, "applied-source-camera-change");
                Assert.True(image.ActualHeight >= 220, $"Image height after camera change {image.ActualHeight}.");
                var changedCameraImageBounds = Bounds(image, window);
                sourceMenu.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(popup.IsOpen);
                Assert.Equal(changedCameraImageBounds, Bounds(image, window));
                Assert.Equal(before, store.Serialize(project));
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
                Assert.Equal(Stretch.Uniform, Assert.IsType<Image>(view.FindName("MmiVirtualCameraImageViewer")).Stretch);
            }
            finally { window.Close(); view.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task CaptureDraftActionsStayVisibleWhileSettingsScrollAndInvalidInputRecovers(double width, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var definition = project.Devices.First(device => device.Kind == DeviceKind.Camera);
            definition.Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = "camera-footer-recipe", Version = 1, ConfirmedVersion = 1, GenerationDelayMilliseconds = 0, ResponseDelayMilliseconds = 0 };
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = definition.Id;
            var view = new CameraImageSourceView { DataContext = main.Camera, CaptureSection = 0, Margin = new Thickness(12) };
            var surface = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.FindResource("Surface.Panel"), Child = view };
            var window = new Window { Content = surface, Width = width, Height = 360, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -3000, Top = -3000,
                Background = (Brush)Application.Current.FindResource("Surface.Panel"), ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                var editor = main.Camera.CaptureEditor;
                var before = store.Serialize(project);
                var apply = Assert.IsType<Button>(view.FindName("CaptureApplySettingsButton"));
                var scroll = Assert.IsType<ScrollViewer>(view.FindName("CameraSourceFieldsScrollViewer"));
                Assert.Same(editor.ApplyCommand, apply.Command);
                Assert.Equal(width - 24, view.ActualWidth, precision: 1);
                foreach (int section in new[] { 0, 1, 2 })
                {
                    view.CaptureSection = section;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    scroll.ScrollToTop();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var top = Bounds(apply, window);
                    Capture(surface, width, surface.ActualHeight, language, $"footer-section-{section}-top");
                    scroll.ScrollToBottom();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var bottom = Bounds(apply, window);
                    Capture(surface, width, surface.ActualHeight, language, $"footer-section-{section}-bottom");
                    Assert.True(new Rect(window.RenderSize).Contains(top) && new Rect(window.RenderSize).Contains(bottom), $"Apply must be fully visible in section {section}: {top} / {bottom}.");
                    Assert.Equal(top, bottom);
                    Assert.False(Descendants(scroll).Contains(apply), "Apply must remain outside the form scroll.");
                }
                view.CaptureSection = 0;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var field = Assert.IsType<TextBox>(view.FindName("CaptureFieldOfViewTextBox"));
                field.SetCurrentValue(TextBox.TextProperty, "NaN");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Assert.Equal("NaN", editor.Parameters[0].Value);
                apply.Command!.Execute(apply.CommandParameter);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(before, store.Serialize(project));
                Assert.Contains(language == OpenVisionLanguage.English ? "finite" : "유한", editor.Status);
                var status = Descendants(view).OfType<TextBlock>().Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(CameraCaptureEditorViewModel.Status));
                Assert.Equal(editor.Status, status.Text);
                Assert.False(Descendants(scroll).Contains(status), "Capture validation must remain outside the form scroll.");
                Assert.True(new Rect(window.RenderSize).Contains(Bounds(status, window)));
                Capture(surface, width, surface.ActualHeight, language, "footer-invalid");
                field.SetCurrentValue(TextBox.TextProperty, "140");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                apply.Command.Execute(apply.CommandParameter);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal(140, definition.Camera.NumericalInput!.FieldOfView);
                Assert.True(main.HasUnsavedChanges);
                Assert.False(main.IsRunning);
                Assert.Equal(0, main.SceneSnapshots.Latest!.TickIndex);
                Capture(surface, width, surface.ActualHeight, language, "footer-applied");
                editor.Parameters[0].Value = "175";
                var revert = Descendants(view).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.RevertCommand));
                revert.Command!.Execute(revert.CommandParameter);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal("140", field.Text);
                Assert.Equal(140, definition.Camera.NumericalInput!.FieldOfView);
                var reset = Descendants(view).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.ResetCommand));
                reset.Command!.Execute(reset.CommandParameter);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Equal("120", field.Text);
                Assert.Equal(140, definition.Camera.NumericalInput!.FieldOfView);
                var enabled = Descendants(view).OfType<CheckBox>().Single(control => control.IsVisible
                    && control.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == nameof(CameraCaptureEditorViewModel.Enabled));
                var fileApply = Assert.IsType<Button>(view.FindName("ApplyCameraSourceButton"));
                Assert.True(apply.IsVisible);
                Assert.False(fileApply.IsVisible);
                enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(editor.Enabled);
                Assert.True(editor.HasCaptureSettingsToApply);
                Assert.True(apply.IsVisible);
                Assert.False(fileApply.IsVisible);
                Assert.NotNull(definition.Camera.NumericalInput);
                apply.Command.Execute(apply.CommandParameter);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.Null(definition.Camera.NumericalInput);
                Assert.False(apply.IsVisible);
                Assert.True(fileApply.IsVisible);
                enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.True(editor.Enabled);
                Assert.True(apply.IsVisible);
                Assert.False(fileApply.IsVisible);
                editor.RevertCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert.False(enabled.IsChecked);
                Assert.False(apply.IsVisible);
                Assert.True(fileApply.IsVisible);
                Capture(surface, width, surface.ActualHeight, language, "footer-mode-restored");
                Assert.False(main.IsRunning);
                Capture(surface, width, surface.ActualHeight, language, "footer-reset-draft");
            }
            finally { window.Close(); view.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 300, OpenVisionLanguage.Korean)]
    [InlineData(1920, 420, OpenVisionLanguage.Korean)]
    [InlineData(1280, 300, OpenVisionLanguage.English)]
    [InlineData(1920, 420, OpenVisionLanguage.English)]
    public async Task CameraAndReferenceAxisShowEntireLongSelectedValues(double width, double pane, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var definition = project.Devices.First(device => device.Kind == DeviceKind.Camera);
            var axis = project.Axes.First();
            axis.Name = language == OpenVisionLanguage.Korean ? new string('축', 80) : new string('X', 80);
            definition.Name = language == OpenVisionLanguage.Korean ? new string('검', 80) : new string('C', 80);
            definition.Camera!.NumericalInput = new NumericalCameraSettings { AxisId = axis.Id, RecipeName = "long-camera-choice", ConfirmedVersion = 1 };
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = definition.Id;
            var source = new CameraImageSourceView { DataContext = main.Camera, CaptureSection = 0, Margin = new Thickness(12) };
            var surface = new Border { Width = pane, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.FindResource("Surface.Panel"), Child = source };
            var window = new Window { Content = surface, Width = pane, Height = 520, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -3000, Top = -3000,
                Background = (Brush)Application.Current.FindResource("Surface.Panel"), ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var combo = Assert.IsType<ComboBox>(source.FindName("CaptureReferenceAxisComboBox"));
                Assert.Equal(axis.Id, combo.SelectedValue);
                Assert.Equal(pane - 24, source.ActualWidth, precision: 1);
                var text = Descendants(combo).OfType<TextBlock>().Single(block => block.Text.Contains(axis.Name, StringComparison.Ordinal));
                Capture(surface, pane, surface.ActualHeight, language, "long-axis-selected");
                AssertChoiceFits(text, combo);
                source.DataContext = null;
                main.Navigation.IsCompactLayout = width == 1280;
                var view = new MmiOperatorLayoutView { DataContext = main };
                window.Content = view;
                window.Width = width;
                window.Height = 760;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var camera = Assert.IsType<ComboBox>(view.FindName("MmiCameraSelectionComboBox"));
                Assert.Equal(definition.Id, camera.SelectedValue);
                var cameraText = Descendants(camera).OfType<TextBlock>().Single(block => block.Text.Contains(definition.Name, StringComparison.Ordinal));
                Capture(window, width, 760, language, "long-camera-selected");
                AssertChoiceFits(cameraText, camera);
                Assert.True(new Rect(window.RenderSize).Contains(Bounds(camera, window)));
                Assert.True(Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame")).ActualHeight > 0);
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
            }
            finally { window.Close(); source.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    private static void AssertChoiceFits(TextBlock text, ComboBox combo)
    {
        var glyph = new FormattedText(text.Text, System.Globalization.CultureInfo.InvariantCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, text.ActualWidth) };
        Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
        Assert.True(glyph.Height <= text.ActualHeight + 0.5, $"Selected value needs {glyph.Height}px but has {text.ActualHeight}px.");
        Assert.True(new Rect(combo.RenderSize).Contains(Bounds(text, combo)), $"Selected value {Bounds(text, combo)} exceeds selector {combo.RenderSize}.");
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, 1280, 760)]
    [InlineData(OpenVisionLanguage.English, 1280, 760)]
    [InlineData(OpenVisionLanguage.Korean, 1920, 1032)]
    [InlineData(OpenVisionLanguage.English, 1920, 1032)]
    public async Task CurrentInputsSelectExistingCameraAndClearPreviewOnRestart(OpenVisionLanguage language, double width, double height)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var fixture = new CameraExecutionInputFixture(canEditCapture: true);
            var capturedSnapshot = fixture.Snapshot;
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false, () => Task.CompletedTask, () => { }, exception => throw exception);
            navigation.IsInspectionWorkspace = true;
            navigation.IsCompactLayout = width < 1400;
            var view = new MmiOperatorLayoutView { DataContext = new InputViewContext(fixture.Camera, navigation) };
            bool baseline = Environment.GetEnvironmentVariable("OPENVISIONLAB_CAMERA_INPUT_BASELINE") == "1";
            var window = new Window { Content = view, Width = width, Height = height, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            try
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Directory.CreateDirectory(TestStorage.RootPath);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "input-typography-monitor.jsonl"), JsonSerializer.Serialize(monitor) + Environment.NewLine);
                var before = new ProjectDocumentStore().Serialize(fixture.Project);
                Arrange(view, width, height);
                var allCameras = Assert.IsType<ComboBox>(view.FindName("MmiCameraSelectionComboBox"));
                Assert.Equal("camera-1", allCameras.SelectedValue);
                Assert.Same(fixture.Camera.SelectedVirtualCamera, allCameras.SelectedItem);
                var cameraName = Assert.IsType<TextBlock>(view.FindName("MmiSelectedCameraName"));
                var cameraIdentity = Assert.IsType<TextBlock>(view.FindName("MmiSelectedCameraId"));
                var identityPanel = Assert.IsType<WrapPanel>(view.FindName("MmiSelectedCameraIdentity"));
                void AssertCameraIdentity(string cameraId)
                {
                    var definition = fixture.Project.Devices.Single(device => device.Id == cameraId);
                    Assert.Equal($"{(cameraId == "camera-4" && fixture.Camera.CanShowHeightData ? "▤" : "▣")} {definition.Name}", cameraName.Text);
                    Assert.Equal(definition.Id, cameraIdentity.Text);
                    Assert.Equal(width < 1400 ? 18.125 : 18.85, cameraName.LineHeight, 3);
                    Assert.Equal(16.675, cameraIdentity.LineHeight, 3);
                    Assert.Equal(new Thickness(8, 0, 0, 0), Assert.IsType<Border>(cameraIdentity.Parent).Padding);
                    new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32).Render(view);
                    IEnumerable<GlyphRunDrawing> IdentityGlyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                        : drawing is DrawingGroup group ? group.Children.SelectMany(IdentityGlyphs) : [];
                    foreach (var text in new[] { cameraName, cameraIdentity })
                    {
                        Assert.True(new Rect(identityPanel.RenderSize).Contains(Bounds(text, identityPanel)), $"Camera identity {text.Name} must remain within its actual header.");
                        var glyphs = IdentityGlyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                        Assert.NotEmpty(glyphs);
                        Assert.All(glyphs, glyph =>
                        {
                            Assert.Equal(text == cameraName ? width < 1400 ? 12.5 : 13 : 11.5, glyph.GlyphRun.FontRenderingEmSize, 3);
                            Assert.Equal(text == cameraName ? 700 : 400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
                            Assert.Equal(text == cameraName ? "#FF243448" : "#FF52687C", glyph.ForegroundBrush.ToString());
                        });
                    }
                }
                AssertCameraIdentity("camera-1");
                var settingsColumn = Assert.IsType<ColumnDefinition>(view.FindName("MmiInspectionSetupColumn"));
                var settingsGap = Assert.IsType<ColumnDefinition>(view.FindName("MmiInspectionColumnGap"));
                Assert.Equal(360, settingsColumn.ActualWidth, 3);
                Assert.Equal(5, settingsGap.ActualWidth, 3);
                var configured = Assert.IsType<Border>(view.FindName("MmiCameraConfiguredWorkspace"));
                var previewSurface = Assert.IsType<Border>(view.FindName("MmiInspectionPreviewPanel"));
                var imageSurface = Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame"));
                Assert.Equal(new Thickness(0), configured.Padding);
                Assert.Equal(new Thickness(0), configured.BorderThickness);
                Assert.Equal("#FFFFFFFF", configured.Background.ToString());
                Assert.Equal(new Thickness(0), previewSurface.Padding);
                Assert.Equal(new Thickness(0), previewSurface.BorderThickness);
                Assert.Equal("#FFE9EDF3", previewSurface.Background.ToString());
                Assert.Equal(new Thickness(0), imageSurface.BorderThickness);
                Assert.Equal("#FFE9EDF3", imageSurface.Background.ToString());
                var settingsTabs = Assert.IsType<TabControl>(view.FindName("MmiInspectionSettingsTabs"));
                var emptyTitle = Assert.IsType<TextBlock>(view.FindName("MmiCurrentInputEmptyTitle"));
                var imageError = Assert.IsType<TextBlock>(view.FindName("MmiVirtualCameraImageError"));
                Assert.True(fixture.Camera.HasCurrentCameraInputs);
                Assert.False(fixture.Camera.HasSelectedCameraInput);
                Assert.True(emptyTitle.IsVisible);
                Assert.False(imageError.IsVisible);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "아직 이미지가 없습니다" : "No image yet", emptyTitle.Text);
                Assert.Equal(20.3, emptyTitle.LineHeight, 3);
                Assert.Equal(420, emptyTitle.ActualWidth, 3);
                Assert.Equal("#FF52687C", emptyTitle.Foreground.ToString());
                Assert.True(new Rect(imageSurface.RenderSize).Contains(Bounds(emptyTitle, imageSurface)));
                new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32).Render(view);
                IEnumerable<GlyphRunDrawing> ReadGlyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(ReadGlyphs) : [];
                var emptyGlyphs = ReadGlyphs(Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(emptyTitle))).ToArray();
                Assert.NotEmpty(emptyGlyphs);
                Assert.All(emptyGlyphs, glyph =>
                {
                    Assert.Equal(14, glyph.GlyphRun.FontRenderingEmSize, 3);
                    Assert.Equal(700, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
                    Assert.Equal("#FF52687C", glyph.ForegroundBrush.ToString());
                });
                Capture(view, width, height, language, "selected-empty-title");
                foreach (TabItem tab in settingsTabs.Items)
                {
                    Assert.Equal(28, tab.ActualHeight, 3);
                    var capsule = Assert.Single(Descendants(tab).OfType<Border>());
                    // R24 button:hover:not(:disabled) takes precedence over the selected/background rule.
                    Assert.Equal(tab.IsMouseOver ? "#FFEDF3F7" : tab.IsSelected ? "#FFE4F4EF" : "#FFEAF0F6", capsule.Background.ToString());
                    File.AppendAllText(Path.Combine(TestStorage.RootPath, "settings-tab-observed-state.jsonl"), JsonSerializer.Serialize(new
                    {
                        language, width, height, tab.IsSelected, tab.IsMouseOver, Background = capsule.Background.ToString(),
                        Scope = "Observed native pointer state only; no physical input or intentional hover path exercised"
                    }) + Environment.NewLine);
                    Assert.Equal(tab.IsSelected ? "#FF087F73" : "#FFCBD6E2", capsule.BorderBrush.ToString());
                    Assert.Equal(new Thickness(1), capsule.BorderThickness);
                    Assert.Equal(new CornerRadius(5), capsule.CornerRadius);
                    var caption = Assert.Single(Descendants(tab).OfType<TextBlock>().Where(text => text.Name == "MmiInspectionSettingsTabCaption"));
                    Assert.Equal(17.4, caption.LineHeight, 3);
                    var drawing = Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(caption));
                    var glyphs = ReadGlyphs(drawing).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.Equal(12, glyph.GlyphRun.FontRenderingEmSize, 3));
                    Assert.True(new Rect(tab.RenderSize).Contains(Bounds(caption, tab)), "Rendered settings tab caption must fit its actual tab.");
                }
                var sourceSettings = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraSourceSettings"));
                var sourcePath = Assert.IsType<TextBox>(sourceSettings.FindName("CameraSourcePathTextBox"));
                Assert.True(sourcePath.IsReadOnly);
                Assert.Equal(28, sourcePath.ActualHeight, 3);
                Assert.Equal(new Thickness(7, 4, 7, 4), sourcePath.Padding);
                // Source assertions above use the file fixture; variation uses an explicitly configured numerical camera.
                fixture.Project.Devices[0].Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = "variation-typography", ConfirmedVersion = 1 };
                fixture.Camera.CaptureEditor.Load(fixture.Project, fixture.Camera.SelectedCameraId);
                before = new ProjectDocumentStore().Serialize(fixture.Project);
                settingsTabs.SetCurrentValue(Selector.SelectedIndexProperty, 1);
                settingsTabs.GetBindingExpression(Selector.SelectedIndexProperty)?.UpdateSource();
                Arrange(view, width, height);
                var variation = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraVariationSettings"));
                void TraceVariationContent(string stage)
                {
                    Directory.CreateDirectory(TestStorage.RootPath);
                    File.AppendAllText(Path.Combine(TestStorage.RootPath, "variation-content-generation.jsonl"), JsonSerializer.Serialize(new
                    {
                        language, width, height, stage, settingsTabs.SelectedIndex, navigation.SelectedInspectionSettingsTabIndex,
                        variation.IsVisible, variation.IsLoaded, HasPresentationSource = PresentationSource.FromVisual(variation) is not null,
                        Fields = Descendants(variation).OfType<TextBox>().Count(field => field.IsVisible && field.DataContext is CaptureParameterEditor)
                    }) + Environment.NewLine);
                }
                TraceVariationContent("before-dispatcher-content");
                await view.Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                TraceVariationContent("after-dispatcher-content");
                new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32).Render(view);
                var variationFields = Descendants(variation).OfType<TextBox>().Where(field => field.IsVisible && field.DataContext is CaptureParameterEditor).ToArray();
                Assert.Equal(5, variationFields.Length);
                var variationHeading = Assert.IsType<TextBlock>(variation.FindName("CameraVariationHeading"));
                Assert.Equal(Visibility.Collapsed, variationHeading.Visibility);
                Assert.False(variationHeading.IsVisible);
                Assert.Equal(0, variationHeading.DesiredSize.Height);
                foreach (var field in variationFields)
                {
                    Assert.Equal(28, field.ActualHeight, 3);
                    Assert.Equal(new Thickness(7, 4, 7, 4), field.Padding);
                    Assert.Equal("#FF52687C", field.Foreground.ToString());
                    Assert.Equal("#FFFFFFFF", field.Background.ToString());
                    Assert.Equal("#FFCBD6E2", field.BorderBrush.ToString());
                    var glyphs = Descendants(field).OfType<Visual>().SelectMany(visual => ReadGlyphs(VisualTreeHelper.GetDrawing(visual))).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.Equal(12, glyph.GlyphRun.FontRenderingEmSize, 3));
                    Assert.True(new Rect(view.RenderSize).Contains(Bounds(field, view)));
                }
                foreach (var caption in Descendants(variation).OfType<TextBlock>().Where(caption => caption.IsVisible && caption.DataContext is CaptureParameterEditor))
                {
                    Assert.Equal(17.4, caption.LineHeight, 3);
                    var glyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(caption)).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.Equal(12, glyph.GlyphRun.FontRenderingEmSize, 3));
                }
                var editor = fixture.Camera.CaptureEditor;
                var parameter = editor.Parameters[1];
                var valueBefore = parameter.Value;
                var invalidField = variationFields.Single(field => ReferenceEquals(field.DataContext, parameter));
                editor.Enabled = true;
                invalidField.SetCurrentValue(TextBox.TextProperty, "NaN");
                invalidField.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal("NaN", parameter.Value);
                Assert.IsType<Button>(variation.FindName("CaptureApplySettingsButton")).Command!.Execute(null);
                await view.Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                Assert.True(editor.HasError);
                Assert.Equal(before, new ProjectDocumentStore().Serialize(fixture.Project));
                var error = Descendants(variation).OfType<TextBlock>().Single(caption => caption.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(CameraCaptureEditorViewModel.Status));
                Assert.Equal(editor.Status, error.Text);
                Assert.Equal(Application.Current.FindResource("State.Fault").ToString(), error.Foreground.ToString());
                Assert.True(new Rect(view.RenderSize).Contains(Bounds(error, view)));
                Capture(view, width, height, language, "variation-invalid");
                editor.RevertCommand.Execute(null);
                await view.Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                Assert.False(editor.HasError);
                Assert.Equal(valueBefore, editor.Parameters[1].Value);
                Capture(view, width, height, language, "variation-recovered");

                // Isolated existing equipment-ancestor style guard; this does not exercise the full V09 caller.
                navigation.IsEquipmentWorkspace = true;
                var equipmentSource = new CameraImageSourceView { DataContext = fixture.Camera, CaptureSection = 1 };
                var equipment = new RightToolRegionView { DataContext = new InputViewContext(fixture.Camera, navigation), Content = equipmentSource };
                window.Content = equipment;
                window.Width = 360;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Arrange(equipment, 360, height);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "input-typography-monitor.jsonl"), JsonSerializer.Serialize(SmokeDpiTestHook.CaptureMonitorEvidence(window)) + Environment.NewLine);
                new RenderTargetBitmap(360, (int)height, 96, 96, PixelFormats.Pbgra32).Render(equipment);
                var equipmentHeading = Assert.IsType<TextBlock>(equipmentSource.FindName("CameraVariationHeading"));
                Assert.True(equipmentHeading.IsVisible);
                Assert.Equal(16, equipmentHeading.FontSize);
                Assert.Equal(FontWeights.SemiBold, equipmentHeading.FontWeight);
                Assert.Equal(new Thickness(0, 0, 0, 10), equipmentHeading.Margin);
                var headingGlyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(equipmentHeading)).ToArray();
                Assert.NotEmpty(headingGlyphs);
                Assert.All(headingGlyphs, glyph => Assert.Equal(16, glyph.GlyphRun.FontRenderingEmSize, 3));
                var equipmentWidth = Assert.IsType<TextBox>(equipmentSource.FindName("CameraSourcePathTextBox"));
                Assert.Equal(28, equipmentWidth.Height, 3);
                Assert.Equal(12, equipmentWidth.FontSize, 3);
                foreach (var field in Descendants(equipmentSource).OfType<TextBox>().Where(field => field.IsVisible && field.DataContext is CaptureParameterEditor))
                {
                    Assert.Equal(28, field.ActualHeight, 3);
                    Assert.Equal(equipment.Resources["EquipmentInspector.Input"].ToString(), field.Background.ToString());
                    Assert.Equal(equipment.Resources["EquipmentInspector.Muted"].ToString(), field.Foreground.ToString());
                    var glyphs = Descendants(field).OfType<Visual>().SelectMany(visual => ReadGlyphs(VisualTreeHelper.GetDrawing(visual))).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.Equal(12, glyph.GlyphRun.FontRenderingEmSize, 3));
                }
                foreach (var caption in Descendants(equipmentSource).OfType<TextBlock>().Where(caption => caption.IsVisible && caption.DataContext is CaptureParameterEditor))
                {
                    Assert.Equal(18.85, caption.LineHeight, 3);
                    var glyphs = ReadGlyphs(VisualTreeHelper.GetDrawing(caption)).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.Equal(equipment.FontSize, glyph.GlyphRun.FontRenderingEmSize, 3));
                }
                equipmentSource.DataContext = null;
                equipment.Content = null;
                equipment.DataContext = null;
                navigation.IsInspectionWorkspace = true;
                window.Width = width;
                window.Content = view;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "input-typography-monitor.jsonl"), JsonSerializer.Serialize(SmokeDpiTestHook.CaptureMonitorEvidence(window)) + Environment.NewLine);
                settingsTabs.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                settingsTabs.GetBindingExpression(Selector.SelectedIndexProperty)?.UpdateSource();
                await view.Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                Capture(view, width, height, language, "empty-camera");
                ComboBox? inputs = view.FindName("MmiCurrentInputComboBox") as ComboBox;
                if (!baseline)
                {
                    Assert.NotNull(inputs);
                    var frame = Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame"));
                    var frameBeforeDetails = Bounds(frame, view);
                    Assert.True(double.IsPositiveInfinity(frame.MaxHeight));
                    Assert.True(frame.ActualHeight > (width < 1400 ? 180 : 260));
                    Assert.True(new Rect(view.RenderSize).Contains(frameBeforeDetails));
                    Assert.IsType<Expander>(view.FindName("MmiCurrentInputDetails")).IsExpanded = true;
                    Arrange(view, width, height);
                    Assert.Equal(frameBeforeDetails, Bounds(frame, view));
                    Assert.Equal(2, inputs.Items.Count);
                    Assert.Null(inputs.SelectedItem);
                    AssertReadable(view, "MmiCurrentInputSummary", "MmiCurrentInputState", "MmiCurrentInputContext", "MmiCurrentInputStepContext");
                }

                foreach (var cameraId in new[] { "camera-2", "camera-4" })
                {
                    if (baseline) fixture.Camera.SelectCameraCommand.Execute(cameraId);
                    else
                    {
                        inputs!.SetCurrentValue(Selector.SelectedValueProperty, cameraId);
                        var behavior = Assert.Single(Interaction.GetBehaviors(inputs).OfType<ComboBoxSelectionCommandBehavior>());
                        Assert.Same(fixture.Camera.SelectCameraCommand, behavior.Command);
                        behavior.Command!.Execute(inputs.SelectedValue);
                    }
                    Arrange(view, width, height);
                    Assert.Equal(cameraId, fixture.Camera.SelectedCameraId);
                    Assert.Equal(cameraId, allCameras.SelectedValue);
                    AssertCameraIdentity(cameraId);
                    if (!baseline) Assert.Equal(cameraId, inputs!.SelectedValue);
                    Assert.Equal(cameraId == "camera-2" ? OpenVisionLanguageService.T("Shell.ResultPass") : OpenVisionLanguageService.T("Shell.ResultFail"), fixture.Camera.CurrentCameraResultText);
                    var preview = Assert.IsType<Image>(view.FindName(cameraId == "camera-2" ? "MmiVirtualCameraImageViewer" : "MmiNumericalCameraImageViewer"));
                    var bitmap = Assert.IsAssignableFrom<BitmapSource>(preview.Source);
                    var expected = cameraId == "camera-2" ? new byte[] { 0, 64, 128, 255 } : fixture.Snapshot.Cameras.Single(camera => camera.Id == cameraId).Result!.NumericalInput!.CopyPreview();
                    var actual = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
                    bitmap.CopyPixels(actual, bitmap.PixelWidth, 0);
                    Assert.Equal(expected, actual);
                    Assert.False(emptyTitle.IsVisible);
                    Assert.False(imageError.IsVisible);
                    if (!baseline)
                    {
                        var context = Assert.IsType<TextBlock>(view.FindName("MmiCurrentInputContext"));
                        var step = Assert.IsType<TextBlock>(view.FindName("MmiCurrentInputStepContext"));
                        Assert.Contains(cameraId == "camera-2" ? "run-1/WP-2" : "run-1/WP-4", context.Text);
                        Assert.Contains(cameraId == "camera-2" ? "capture-2" : "capture-4", step.Text);
                        AssertReadable(view, "MmiCurrentInputSummary", "MmiCurrentInputState", "MmiCurrentInputContext", "MmiCurrentInputStepContext");
                    }
                    Capture(view, width, height, language, cameraId);
                }

                fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => CameraExecutionInputFixture.Idle(camera.Id, camera.Name)), generation: 2);
                fixture.Camera.RefreshProjection();
                Arrange(view, width, height);
                Assert.Equal("camera-4", allCameras.SelectedValue);
                AssertCameraIdentity("camera-4");
                Assert.Null(Assert.IsType<Image>(view.FindName("MmiVirtualCameraImageViewer")).Source);
                Assert.Null(Assert.IsType<Image>(view.FindName("MmiNumericalCameraImageViewer")).Source);
                Assert.Equal("—", fixture.Camera.CurrentCameraResultText);
                if (!baseline)
                {
                    Assert.Empty(inputs!.Items);
                    Assert.Null(inputs.SelectedItem);
                    Assert.Equal("camera-4", fixture.Camera.SelectedCameraId);
                    Assert.False(inputs.IsEnabled);
                    Assert.True(emptyTitle.IsVisible);
                    Assert.False(imageError.IsVisible);
                    Capture(view, width, height, language, "restart");
                }
                var capturedTwoD = capturedSnapshot.Cameras.Single(camera => camera.Id == "camera-2");
                var originalFrame = capturedTwoD.FrameEvidence!;
                var missingFrame = new VirtualCameraFrameEvidence(originalFrame.FrameId, "images/missing-owned-test.pgm", originalFrame.ContentSha256,
                    originalFrame.ContentLength, originalFrame.Width, originalFrame.Height, originalFrame.PixelFormat);
                fixture.Snapshot = fixture.CreateSnapshot(capturedSnapshot.Cameras.Select(camera => camera.Id == "camera-2" ? camera with { FrameEvidence = missingFrame } : camera), generation: 3);
                fixture.Camera.SelectCameraCommand.Execute("camera-2");
                fixture.Camera.RefreshProjection();
                Arrange(view, width, height);
                Assert.True(fixture.Camera.HasSelectedCameraInput);
                Assert.Null(Assert.IsType<Image>(view.FindName("MmiVirtualCameraImageViewer")).Source);
                Assert.False(emptyTitle.IsVisible);
                Assert.True(imageError.IsVisible);
                Capture(view, width, height, language, "missing-image-error");
                fixture.Snapshot = fixture.CreateSnapshot(capturedSnapshot.Cameras, generation: 3);
                fixture.Camera.RefreshProjection();
                Arrange(view, width, height);
                var recovered = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<Image>(view.FindName("MmiVirtualCameraImageViewer")).Source);
                var recoveredPixels = new byte[recovered.PixelWidth * recovered.PixelHeight];
                recovered.CopyPixels(recoveredPixels, recovered.PixelWidth, 0);
                Assert.Equal(new byte[] { 0, 64, 128, 255 }, recoveredPixels);
                Assert.False(emptyTitle.IsVisible);
                Assert.False(imageError.IsVisible);
                Capture(view, width, height, language, "missing-image-recovery");
                Assert.Equal(before, new ProjectDocumentStore().Serialize(fixture.Project));
                Assert.Equal(0, fixture.SideEffects);
            }
            finally
            {
                view.DataContext = null;
                window.Content = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task ParentShellKeepsSettingsAndActionsReachableWithLongInputContext(OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var fixture = new CameraExecutionInputFixture();
            const double width = 1000, height = 720;
            string longName = language == OpenVisionLanguage.Korean
                ? "웨이퍼 가장자리와 표면의 위치 편차를 확인하는 두 번째 검사 공정의 촬영 대상"
                : "Wafer edge and surface positioning inspection camera for the second process";
            string recipe = "wafer-edge-and-surface-positioning-inspection-recipe-confirmed-for-second-process";
            fixture.Project.Layouts[0].Components[0].Name = longName;
            fixture.Project.Sequences[0].Name = longName;
            foreach (var step in fixture.Project.Sequences[0].Steps) { step.Name = longName; step.Parameter = recipe; }
            foreach (var device in fixture.Project.Devices) device.Name = longName;
            fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Result is null ? camera with { Name = longName } : camera with
            {
                Name = longName, CurrentRecipeId = recipe,
                Result = camera.Result with { RecipeId = recipe, WorkpieceInstanceId = "run-0000000001/second-inspection-station/wafer-workpiece-instance-000000042" }
            }));
            fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
            var parentProject = EquipmentMaterialFlowViewModelTests.CreateProject();
            parentProject.Sequences.Clear();
            using var main = new MainViewModel(parentProject);
            main.Navigation.IsInspectionWorkspace = true;
            main.Navigation.IsCompactLayout = true;
            main.Navigation.IsNarrowLayout = true;
            var shell = new ShellWindow { DataContext = main };
            var root = Assert.IsType<Grid>(shell.Content);
            Assert.IsType<ColumnDefinition>(shell.FindName("LeftWorkspaceColumn")).Width = new GridLength(185);
            shell.Content = null;
            root.DataContext = main;
            Arrange(root, width, height);
            var view = Assert.Single(Descendants(root).OfType<MmiOperatorLayoutView>());
            view.DataContext = new ParentInputViewContext(fixture.Camera, main);
            try
            {
                string before = new ProjectDocumentStore().Serialize(fixture.Project);
                string beforeParent = new ProjectDocumentStore().Serialize(parentProject);
                Arrange(root, width, height);
                var inputs = Assert.IsType<ComboBox>(view.FindName("MmiCurrentInputComboBox"));
                var inputPanel = Assert.IsType<Border>(Assert.IsType<Popup>(view.FindName("MmiRunInputsPopup")).Child);
                Arrange(inputPanel, 440, 420);
                var previewScroll = Assert.IsType<ScrollViewer>(view.FindName("MmiInspectionPreviewScrollViewer"));
                var footer = new[] { Assert.IsType<Button>(view.FindName("MmiPublishButton")) };
                var initialFooter = footer.Select(button => Bounds(button, root)).ToArray();
                foreach (string cameraId in new[] { "camera-2", "camera-4" })
                {
                    inputs.SetCurrentValue(Selector.SelectedValueProperty, cameraId);
                    Assert.Single(Interaction.GetBehaviors(inputs).OfType<ComboBoxSelectionCommandBehavior>()).Command!.Execute(inputs.SelectedValue);
                    Arrange(root, width, height);
                    var selectedName = Assert.IsType<TextBlock>(view.FindName("MmiSelectedCameraName"));
                    var selectedId = Assert.IsType<TextBlock>(view.FindName("MmiSelectedCameraId"));
                    var identity = Assert.IsType<WrapPanel>(view.FindName("MmiSelectedCameraIdentity"));
                    Assert.Contains(longName, selectedName.Text);
                    Assert.Equal(cameraId, selectedId.Text);
                    Assert.Equal(TextWrapping.Wrap, selectedName.TextWrapping);
                    File.AppendAllText(Path.Combine(TestStorage.RootPath, "camera-header-long-observed.jsonl"), JsonSerializer.Serialize(new
                    {
                        language, cameraId, selectedName.Text, selectedName.IsVisible, selectedName.ActualWidth, selectedName.ActualHeight,
                        selectedName.MaxWidth, selectedName.LineHeight, selectedName.FontSize, IdentityWidth = identity.ActualWidth,
                        IdentityHeight = identity.ActualHeight, NameBounds = Bounds(selectedName, root), IdBounds = Bounds(selectedId, root),
                        RootSize = root.RenderSize, MmiSize = view.RenderSize, GlyphBounds = VisualTreeHelper.GetDrawing(selectedName)?.Bounds
                    }) + Environment.NewLine);
                    Assert.True(selectedName.ActualWidth <= identity.ActualWidth, "Long camera name must fit the available header width.");
                    foreach (var text in new[] { selectedName, selectedId })
                    {
                        Assert.True(new Rect(identity.RenderSize).Contains(Bounds(text, identity)), text.Name);
                        Assert.True(new Rect(root.RenderSize).Contains(Bounds(text, root)), text.Name);
                    }
                    var renderedName = VisualTreeHelper.GetDrawing(selectedName);
                    Assert.NotNull(renderedName);
                    var imageHeader = Assert.IsType<DockPanel>(identity.Parent);
                    Assert.True(new Rect(imageHeader.RenderSize).Contains(selectedName.TransformToAncestor(imageHeader).TransformBounds(renderedName.Bounds)), "Long camera name ink must remain inside its actual image header.");
                    Capture(root, width, height, language, $"parent-shell-{cameraId}-picker");
                    Assert.All(footer, button => Assert.True(new Rect(root.RenderSize).Contains(Bounds(button, root)), button.Name));
                    Assert.True(previewScroll.ViewportHeight > 0, "Current input must retain visible preview space.");
                    var header = Assert.IsType<ScrollViewer>(view.FindName("MmiInputHeaderScrollViewer"));
                    await AssertReachableAsync(inputs, header, inputPanel, 440, 420);
                    Assert.True(new Rect(root.RenderSize).Contains(Bounds(Assert.IsType<TextBlock>(view.FindName("MmiCurrentInputState")), root)));
                    var frame = Assert.IsType<Border>(view.FindName("MmiVirtualCameraImageFrame"));
                    var toggleDetails = Assert.IsType<ToggleButton>(view.FindName("MmiInspectionSettingsToggle"));
                    toggleDetails.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    Arrange(root, width, height);
                    Assert.True(selectedName.ActualHeight > selectedName.LineHeight, "When the settings panel narrows the image, the entire long camera name must wrap.");
                    Assert.Contains(longName, selectedName.Text);
                    Assert.Equal(cameraId, selectedId.Text);
                    Assert.True(new Rect(identity.RenderSize).Contains(Bounds(selectedName, identity)));
                    Assert.True(new Rect(identity.RenderSize).Contains(Bounds(selectedId, identity)));
                    Assert.True(new Rect(root.RenderSize).Contains(Bounds(imageHeader, root)));
                    var beforeRenderName = VisualTreeHelper.GetDrawing(selectedName);
                    new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32).Render(root);
                    var wrappedName = VisualTreeHelper.GetDrawing(selectedName);
                    File.AppendAllText(Path.Combine(TestStorage.RootPath, "camera-header-wrapped-observed.jsonl"), JsonSerializer.Serialize(new
                    {
                        language, cameraId, selectedName.Text, selectedName.ActualWidth, selectedName.ActualHeight, selectedName.MaxWidth,
                        HeaderSize = imageHeader.RenderSize, BeforeInk = beforeRenderName?.Bounds, AfterInk = wrappedName?.Bounds,
                        NameInHeader = Bounds(selectedName, imageHeader), InkInHeader = wrappedName is null ? Rect.Empty : selectedName.TransformToAncestor(imageHeader).TransformBounds(wrappedName.Bounds)
                    }) + Environment.NewLine);
                    Capture(root, width, height, language, $"parent-shell-{cameraId}-wrapped-header");
                    Assert.NotNull(wrappedName);
                    Assert.True(new Rect(imageHeader.RenderSize).Contains(selectedName.TransformToAncestor(imageHeader).TransformBounds(wrappedName.Bounds)));
                    Assert.Equal(Visibility.Visible, frame.Visibility);
                    Assert.True(frame.ActualWidth > 0 && frame.ActualHeight > 0);
                    Assert.True(new Rect(root.RenderSize).Contains(Bounds(frame, root)));
                    var frameBeforeDetails = Bounds(frame, root);
                    var details = Assert.IsType<Expander>(view.FindName("MmiCurrentInputDetails"));
                    details.IsExpanded = true;
                    Arrange(root, width, height);
                    Arrange(inputPanel, 440, 420);
                    Assert.Equal(frameBeforeDetails, Bounds(frame, root));
                    var detailScroll = Assert.IsType<ScrollViewer>(view.FindName("MmiCurrentInputDetailsScrollViewer"));
                    foreach (string name in new[] { "MmiCurrentInputContext", "MmiCurrentInputStepContext", "MmiCurrentInputAcquisition" })
                        await AssertReachableAsync(Assert.IsType<TextBlock>(view.FindName(name)), detailScroll, inputPanel, 440, 420);
                    Assert.Equal(frameBeforeDetails, Bounds(frame, root));
                    AssertReadable(view, "MmiCurrentInputSummary", "MmiCurrentInputState", "MmiCurrentInputContext", "MmiCurrentInputStepContext", "MmiCurrentInputAcquisition");
                    var image = Assert.IsType<Image>(view.FindName(cameraId == "camera-2" ? "MmiVirtualCameraImageViewer" : "MmiNumericalCameraImageViewer"));
                    Assert.True(image.ActualWidth > 0 && image.ActualHeight > 0, "Actual input image must be visible.");
                    var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                    byte[] expected = cameraId == "camera-2" ? [0, 64, 128, 255] : fixture.Snapshot.Cameras.Single(camera => camera.Id == cameraId).Result!.NumericalInput!.CopyPreview();
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth, 0);
                    Assert.Equal(expected, pixels);
                    Assert.Equal(cameraId, inputs.SelectedValue);
                    Assert.Contains("wafer-workpiece-instance-000000042", fixture.Camera.SelectedInputContextText);
                    Assert.Contains(cameraId == "camera-2" ? "capture-2" : "capture-4", fixture.Camera.SelectedInputStepContextText);
                    Capture(root, width, height, language, $"parent-shell-{cameraId}-details");
                    Assert.IsType<Expander>(view.FindName("MmiCurrentInputDetails")).IsExpanded = false;
                    toggleDetails.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                    Arrange(root, width, height);
                }
                var toggle = Assert.IsType<ToggleButton>(view.FindName("MmiInspectionSettingsToggle"));
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Arrange(root, width, height);
                Assert.True(main.Navigation.IsInspectionSettingsExpanded);
                var setup = Assert.IsType<ScrollViewer>(view.FindName("MmiCaptureSetupScrollViewer"));
                var cameraSource = Assert.IsType<CameraImageSourceView>(view.FindName("MmiCameraSourceSettings"));
                await AssertReachableAsync(cameraSource.ApplyCameraSourceButton, setup, root, width, height);
                await AssertReachableAsync(cameraSource.BrowseCameraSourceButton, setup, root, width, height);
                main.Navigation.SelectedInspectionSettingsTabIndex = 3;
                Arrange(root, width, height);
                var recipeField = Assert.IsType<TextBox>(view.FindName("MmiCaptureRecipeName"));
                recipeField.SetCurrentValue(TextBox.TextProperty, recipe);
                Assert.Equal(recipe, fixture.Camera.CaptureEditor.RecipeName);
                var fields = Assert.IsType<ScrollViewer>(view.FindName("MmiExternalImagesScrollViewer"));
                await AssertReachableAsync(recipeField, fields, root, width, height);
                Assert.IsType<Expander>(view.FindName("IntegrationExchangeDetailsExpander")).IsExpanded = true;
                Arrange(root, width, height);
                foreach (string name in new[] { "MmiRefreshButton", "MmiApplyResultButton" })
                    await AssertReachableAsync(Assert.IsType<Button>(view.FindName(name)), fields, root, width, height);
                Capture(root, width, height, language, "parent-shell-settings-long-recipe");
                main.Navigation.SelectedInspectionSettingsTabIndex = 0;
                Arrange(root, width, height);
                await AssertReachableAsync(cameraSource.ApplyCameraSourceButton, setup, root, width, height);
                Capture(root, width, height, language, "parent-shell-settings-apply");
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Arrange(root, width, height);
                Assert.False(main.Navigation.IsInspectionSettingsExpanded);
                await AssertReachableAsync(inputs, Assert.IsType<ScrollViewer>(view.FindName("MmiInputHeaderScrollViewer")), inputPanel, 440, 420);
                previewScroll.ScrollToBottom();
                Arrange(root, width, height);
                Assert.Equal(initialFooter, footer.Select(button => Bounds(button, root)).ToArray());
                Capture(root, width, height, language, "parent-shell-return-preview");
                Assert.Equal(before, new ProjectDocumentStore().Serialize(fixture.Project));
                Assert.Equal(beforeParent, new ProjectDocumentStore().Serialize(parentProject));
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
                Assert.Equal(0, fixture.SideEffects);
                return true;
            }
            finally
            {
                view.DataContext = null;
                root.DataContext = null;
                shell.DataContext = null;
                shell.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task CaptureParametersAndFeedbackFollowLanguageWithoutResettingTheDraft(double width, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var definition = project.Devices.First(device => device.Kind == DeviceKind.Camera);
            definition.Camera!.NumericalInput = new NumericalCameraSettings { RecipeName = new string('R', 80), ConfirmedVersion = 1, GenerationDelayMilliseconds = 0, ResponseDelayMilliseconds = 0 };
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = definition.Id;
            var editor = main.Camera.CaptureEditor;
            var view = new CameraImageSourceView { DataContext = main.Camera, CaptureSection = 1, Margin = new Thickness(12) };
            var surface = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.FindResource("Surface.Panel"), Child = view };
            var window = new Window { Content = surface, Width = width, Height = 520, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -3000, Top = -3000,
                Background = (Brush)Application.Current.FindResource("Surface.Panel"), ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                foreach (var activeLanguage in new[] { language, language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean })
                {
                    OpenVisionLanguageService.SetLanguage(activeLanguage, save: false);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Capture(surface, width, surface.ActualHeight, activeLanguage, $"locale-variation-start-{language}");
                    Assert.Equal(activeLanguage == OpenVisionLanguage.English ? "X deviation" : "X 편차", editor.Parameters[1].Name);
                    var field = Descendants(view).OfType<TextBox>().Single(box => ReferenceEquals(box.DataContext, editor.Parameters[1]));
                    field.SetCurrentValue(TextBox.TextProperty, "NaN");
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                    var before = new ProjectDocumentStore().Serialize(project);
                    Assert.IsType<Button>(view.FindName("CaptureApplySettingsButton")).Command!.Execute(null);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
                    Assert.Equal(activeLanguage == OpenVisionLanguage.English ? "X deviation: enter a finite number." : "X 편차: 유한한 숫자를 입력하세요.", editor.Status);
                    var status = Descendants(view).OfType<TextBlock>().Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(CameraCaptureEditorViewModel.Status));
                    Assert.Equal(editor.Status, status.Text);
                    Assert.True(new Rect(window.RenderSize).Contains(Bounds(status, window)));
                    Capture(surface, width, surface.ActualHeight, activeLanguage, $"locale-variation-invalid-{language}");
                    var parameter = editor.Parameters[1];
                    var opposite = activeLanguage == OpenVisionLanguage.English ? OpenVisionLanguage.Korean : OpenVisionLanguage.English;
                    OpenVisionLanguageService.SetLanguage(opposite, save: false);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Assert.Same(parameter, editor.Parameters[1]);
                    Assert.Equal("NaN", field.Text);
                    Assert.Equal(opposite == OpenVisionLanguage.English ? "X deviation: enter a finite number." : "X 편차: 유한한 숫자를 입력하세요.", status.Text);
                    Assert.Equal(new string('R', 80), editor.RecipeName);
                    editor.RevertCommand.Execute(null);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var recoveredField = Descendants(view).OfType<TextBox>().Single(box => ReferenceEquals(box.DataContext, editor.Parameters[1]));
                    Assert.Equal("0", recoveredField.Text);
                    Assert.False(main.IsRunning || main.HasUnsavedChanges);
                    Assert.Equal(0, main.SceneSnapshots.Latest!.TickIndex);
                }
            }
            finally { window.Close(); view.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, 1920, 1032)]
    [InlineData(OpenVisionLanguage.English, 1920, 1032)]
    [InlineData(OpenVisionLanguage.Korean, 1280, 900)]
    [InlineData(OpenVisionLanguage.English, 1280, 900)]
    public async Task RightSettingsIdentityUsesAppliedTypeAndKeepsLongIdWithoutExecution(OpenVisionLanguage language, double width, double height)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var fixture = new CameraExecutionInputFixture(canEditCapture: true);
            fixture.Project.Devices[3].Camera!.NumericalInput = new NumericalCameraSettings { InputKind = NumericalCameraInputKind.HeightMap3D };
            fixture.Project.Devices[0].Name = "Height in a camera name is not an applied height map";
            fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false, () => Task.CompletedTask, () => { }, exception => throw exception);
            navigation.IsInspectionWorkspace = true;
            navigation.IsCompactLayout = width < 1400;
            var view = new MmiOperatorLayoutView { DataContext = new InputViewContext(fixture.Camera, navigation) };
            var window = new Window { Content = view, Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            try
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Directory.CreateDirectory(TestStorage.RootPath);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "right-identity-monitor.jsonl"), JsonSerializer.Serialize(monitor) + Environment.NewLine);
                var tabs = Assert.IsType<TabControl>(view.FindName("MmiInspectionSettingsTabs"));
                T Named<T>(string name) where T : FrameworkElement => Assert.IsType<T>(tabs.Template.FindName(name, tabs));
                var header = Named<Border>("MmiSettingsIdentityHeader");
                var nameText = Named<TextBlock>("MmiSettingsCameraName");
                var idText = Named<TextBlock>("MmiSettingsCameraId");
                var icon = Named<TextBlock>("MmiSettingsCameraIcon");
                var type = Named<TextBlock>("MmiSettingsCameraType");
                var chip = Named<Border>("MmiSettingsCameraTypeChip");
                IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph] : drawing is DrawingGroup group ? group.Children.SelectMany(Glyphs) : [];
                void AssertIdentity(bool heightMap)
                {
                    var definition = fixture.Camera.SelectedVirtualCamera!;
                    Assert.Equal(definition.Name, nameText.Text);
                    Assert.Equal(definition.Id, idText.Text);
                    Assert.Equal(heightMap ? "▤" : "▣", icon.Text);
                    Assert.Equal(OpenVisionLanguageService.T(heightMap ? "Camera.HeightMap3D" : "Camera.TwoDImage"), type.Text);
                    Assert.Equal("Camera.SelectedVirtualCamera.Name", nameText.GetBindingExpression(TextBlock.TextProperty)!.ParentBinding.Path.Path);
                    Assert.Equal("Camera.SelectedVirtualCamera.Id", idText.GetBindingExpression(TextBlock.TextProperty)!.ParentBinding.Path.Path);
                    Assert.Equal(TextTrimming.CharacterEllipsis, nameText.TextTrimming);
                    Assert.Equal(TextWrapping.Wrap, idText.TextWrapping);
                    Assert.True(nameText.ActualWidth > 0);
                    foreach (var text in new[] { nameText, idText, icon, type })
                    {
                        var glyphs = Glyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
                        Assert.NotEmpty(glyphs);
                        Assert.All(glyphs, glyph =>
                        {
                            Assert.Equal(text == nameText || text == icon ? width < 1400 ? 12.5 : 13 : 11.5, glyph.GlyphRun.FontRenderingEmSize, 3);
                            Assert.Equal(text == nameText ? 700 : 400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight());
                        });
                        Assert.True(new Rect(header.RenderSize).Contains(Bounds(text, header)), text.Name);
                    }
                    Assert.Equal(new Thickness(12, 9, 12, 9), header.Padding);
                    Assert.Equal(new Thickness(0, 0, 0, 1), header.BorderThickness);
                    Assert.Equal(new Thickness(9, 1, 9, 1), chip.Padding);
                    Assert.Equal(new CornerRadius(12), chip.CornerRadius);
                    Assert.True(chip.ActualHeight >= 22);
                }
                var before = new ProjectDocumentStore().Serialize(fixture.Project);
                foreach (string id in new[] { "camera-1", "camera-4", "camera-1" })
                {
                    fixture.Camera.SelectCameraCommand.Execute(id);
                    Arrange(view, width, height);
                    AssertIdentity(id == "camera-4");
                    fixture.Camera.CaptureEditor.InputKind = id == "camera-4" ? NumericalCameraInputKind.Image2D : NumericalCameraInputKind.HeightMap3D;
                    Arrange(view, width, height);
                    AssertIdentity(id == "camera-4");
                    foreach (int tab in new[] { 0, 1, 2, 3, 1 })
                    {
                        tabs.SetCurrentValue(TabControl.SelectedIndexProperty, tab);
                        Arrange(view, width, height);
                        Assert.Equal(tab, navigation.SelectedInspectionSettingsTabIndex);
                        AssertIdentity(id == "camera-4");
                    }
                    Capture(view, width, height, language, "right-identity-" + id);
                    Assert.Equal(before, new ProjectDocumentStore().Serialize(fixture.Project));
                    Assert.Equal(0, fixture.SideEffects);
                }
                var originalProject = fixture.Project;
                fixture.Project = new MachineProjectDocument { Id = "empty-right-identity", Name = "No cameras" };
                fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
                Arrange(view, width, height);
                Assert.False(header.IsVisible);
                Assert.Equal(string.Empty, nameText.Text);
                Assert.Equal(string.Empty, idText.Text);
                var emptyBefore = new ProjectDocumentStore().Serialize(fixture.Project);
                tabs.SetCurrentValue(TabControl.SelectedIndexProperty, 3);
                Arrange(view, width, height);
                Assert.Equal(3, navigation.SelectedInspectionSettingsTabIndex);
                Assert.True(header.IsVisible);
                Assert.Equal(Visibility.Collapsed, Named<Grid>("MmiSettingsCameraIdentity").Visibility);
                Assert.False(icon.IsVisible || nameText.IsVisible || idText.IsVisible || type.IsVisible || chip.IsVisible);
                Assert.True(Assert.IsType<ScrollViewer>(view.FindName("MmiExternalImagesScrollViewer")).IsVisible);
                Assert.Equal(emptyBefore, new ProjectDocumentStore().Serialize(fixture.Project));
                Assert.Equal(0, fixture.SideEffects);
                Capture(view, width, height, language, "right-identity-empty-integration");
                fixture.Project = originalProject;
                var longCamera = fixture.Project.Devices[0];
                longCamera.Id = "camera-" + new string('I', 100);
                longCamera.Name = new string('N', 120);
                fixture.Camera.LoadProject(fixture.Project, fixture.ProjectPath);
                fixture.Camera.SelectCameraCommand.Execute(longCamera.Id);
                Arrange(view, width, height);
                Assert.Equal(3, navigation.SelectedInspectionSettingsTabIndex);
                Assert.True(Named<Grid>("MmiSettingsCameraIdentity").IsVisible);
                tabs.SetCurrentValue(TabControl.SelectedIndexProperty, 1);
                Arrange(view, width, height);
                AssertIdentity(false);
                Assert.True(idText.ActualHeight > 16.675);
                Assert.Equal(360, tabs.ActualWidth, 3);
                Capture(view, width, height, language, "right-identity-long-return");
                Assert.Equal(0, fixture.SideEffects);
            }
            finally { view.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }
    private sealed record InputViewContext(CameraCommissioningViewModel Camera, ShellNavigationViewModel Navigation);

    private sealed record ParentInputViewContext(CameraCommissioningViewModel Camera, MainViewModel Main)
    {
        public ShellNavigationViewModel Navigation => Main.Navigation;
        public object Integration => Main.Integration;
        public string ModeText => Main.ModeText;
        public string StateText => Main.StateText;
        public string ControlOwnerText => Main.ControlOwnerText;
        public string SimulationStatusText => Main.SimulationStatusText;
        public bool IsRunning => Main.IsRunning;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static async Task AssertReachableAsync(FrameworkElement element, ScrollViewer scroll, FrameworkElement root, double width, double height)
    {
        var viewport = Assert.Single(Descendants(scroll).OfType<ScrollContentPresenter>().Where(presenter => ReferenceEquals(presenter.TemplatedParent, scroll)));
        var samples = new List<object>();
        var timer = Stopwatch.StartNew();
        Rect previousBounds = Rect.Empty;
        Size previousViewport = Size.Empty;
        int stableSamples = 0;
        bool reached = false;
        do
        {
            element.BringIntoView();
            await Task.Delay(20);
            Arrange(root, width, height);
            var bounds = Bounds(element, viewport);
            bool fits = new Rect(viewport.RenderSize).Contains(bounds) && new Rect(root.RenderSize).Contains(Bounds(element, root));
            stableSamples = fits && bounds == previousBounds && viewport.RenderSize == previousViewport ? stableSamples + 1 : 0;
            samples.Add(new { milliseconds = timer.ElapsedMilliseconds, bounds = bounds.ToString(), viewport = viewport.RenderSize.ToString(), offset = scroll.VerticalOffset, fits,
                transforms = AncestorTransforms(element, scroll) });
            previousBounds = bounds;
            previousViewport = viewport.RenderSize;
            reached = stableSamples >= 3;
        } while (!reached && timer.Elapsed < TimeSpan.FromSeconds(2));
        string? evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_CAMERA_INPUT_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence))
            File.AppendAllText(Path.Combine(evidence, $"scroll-{OpenVisionLanguageService.CurrentLanguage}.jsonl"), JsonSerializer.Serialize(new { control = element.Name, scroll = scroll.Name,
                text = (element as TextBox)?.Text, enabled = element.IsEnabled, reached, samples }) + Environment.NewLine);
        Assert.True(reached, $"{element.Name} must reach stable full bounds in {scroll.Name} within 2 seconds.");
        Assert.True(new Rect(viewport.RenderSize).Contains(Bounds(element, viewport)),
            $"{element.Name} must fit {scroll.Name}: control={Bounds(element, viewport)}, viewport={viewport.RenderSize}, offset={scroll.VerticalOffset}.");
        Assert.True(new Rect(root.RenderSize).Contains(Bounds(element, root)), element.Name);
    }

    private static string AncestorTransforms(DependencyObject element, DependencyObject boundary)
    {
        var transforms = new List<string>();
        for (DependencyObject? current = element; current is not null && current != boundary; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement framework)
                transforms.Add($"{framework.GetType().Name}/{framework.Name}: render={framework.RenderTransform.Value}, layout={framework.LayoutTransform.Value}");
        return string.Join(" | ", transforms);
    }

    private static void Arrange(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        view.UpdateLayout();
        if (view is MmiOperatorLayoutView mmi && mmi.FindName("MmiRunInputsPopup") is Popup { Child: FrameworkElement popupChild })
            Arrange(popupChild, 440, 420);
    }

    private static void AssertReadable(FrameworkElement view, params string[] names)
    {
        foreach (var name in names)
        {
            var text = Assert.IsType<TextBlock>(view.FindName(name));
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.DoesNotContain("Camera.Input", text.Text);
            Assert.DoesNotContain("Camera.CurrentInputs", text.Text);
            Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0, name);
            Assert.True(text.DesiredSize.Height <= text.ActualHeight + text.Margin.Top + text.Margin.Bottom + 0.5, name);
        }
    }


    [Theory]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    [InlineData(300, OpenVisionLanguage.English)]
    public async Task CameraVariationControlsRetainDraftsRejectInvalidValuesAndSaveOnlyExplicitApply(double width, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new MachineProjectDocument { Devices = [
                new() { Id = "a", Name = "Camera A", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { ConfirmedVersion = 1 } } },
                new() { Id = "b", Name = "Camera B", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { ConfirmedVersion = 1 } } },
                new() { Id = "file-camera", Name = "File camera", Kind = DeviceKind.Camera, Camera = new() }
            ] };
            var store = new ProjectDocumentStore();
            using var main = new MainViewModel(project);
            main.Camera.SelectedCameraId = "a";
            var editor = main.Camera.CaptureEditor;
            var view = new CameraImageSourceView { DataContext = main.Camera, CaptureSection = 1, Margin = new Thickness(12) };
            var window = new Window { Content = view, Width = width, Height = 580, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.FindResource("Surface.Panel"), ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, 580);
            window.Show();
            try
            {
                async Task Settle() => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                TextBox[] Fields() => Descendants(view).OfType<TextBox>().Where(field => field.IsVisible && field.DataContext is CaptureParameterEditor).ToArray();
                async Task SetValue(int index, string value)
                {
                    var field = Fields()[index];
                    Assert.True(field.IsEnabled);
                    field.SetCurrentValue(TextBox.TextProperty, value);
                    field.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    await Settle();
                    Assert.Equal(value, editor.VariationParameters[index].Value);
                }
                async Task Invoke(Button button)
                {
                    Assert.True(button.IsEnabled);
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                    var provider = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
                    provider.Invoke();
                    await Settle();
                }
                await Settle();
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Directory.CreateDirectory(TestStorage.RootPath);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "v15-variation-monitor.jsonl"), JsonSerializer.Serialize(monitor) + Environment.NewLine);
                var initial = store.Serialize(project);
                var apply = Assert.IsType<Button>(view.FindName("CaptureApplySettingsButton"));
                var revert = Descendants(view).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.RevertCommand));
                var defaults = Descendants(view).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, editor.ResetCommand));
                var fields = Fields();
                Assert.Equal(5, fields.Length);
                Assert.Equal(new[] { "LU", "LU", "°", "°", "LU" }, fields.Select(field => ((CaptureParameterEditor)field.DataContext).Unit));
                Assert.All(fields, field => { Assert.True(field.IsEnabled); Assert.True(field.ActualHeight > 0); Assert.True(new Rect(window.RenderSize).Contains(Bounds(field, window))); });
                Assert.True(Bounds(fields[1], view).X > Bounds(fields[0], view).Right);
                Assert.Equal(Bounds(fields[0], view).X, Bounds(fields[2], view).X, 2);
                Assert.Equal(Bounds(fields[0], view).Y, Bounds(fields[1], view).Y, 2);
                Assert.Equal(0, Assert.IsType<ScrollViewer>(view.FindName("CameraSourceFieldsScrollViewer")).Padding.Right);
                await SetValue(0, "12.5");
                main.Camera.SelectedCameraId = "b";
                await Settle();
                await SetValue(1, "7.5");
                main.Camera.SelectedCameraId = "a";
                await Settle();
                Assert.Equal("12.5", Fields()[0].Text);
                editor.VariationParameters[1].Value = "-4";
                await Settle();
                Assert.Equal("-4", Fields()[1].Text);
                Assert.Equal(initial, store.Serialize(project));
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
                foreach (var invalid in new[] { (0, "10001"), (1, "NaN"), (2, "abc"), (3, "81"), (4, "-1"), (4, "51") })
                {
                    await SetValue(invalid.Item1, invalid.Item2);
                    await Invoke(apply);
                    Assert.True(editor.HasError);
                    Assert.Equal(initial, store.Serialize(project));
                    var status = Assert.IsType<TextBlock>(view.FindName("CaptureSettingsStatus"));
                    Assert.Equal(editor.Status, status.Text);
                    Assert.True(new Rect(window.RenderSize).Contains(Bounds(status, window)));
                    await SetValue(invalid.Item1, "0");
                }
                string[] boundary = ["-10000", "10000", "-10000", "-80", "50"];
                for (int index = 0; index < boundary.Length; index++) await SetValue(index, boundary[index]);
                await Invoke(apply);
                Assert.False(editor.HasError);
                var applied = project.Devices[0].Camera!.NumericalInput!;
                Assert.Equal((-10000d, 10000d, -10000d, -80d, 50d), (applied.OffsetX, applied.OffsetY, applied.Yaw, applied.Tilt, applied.Shake));
                Assert.Equal(0, project.Devices[1].Camera!.NumericalInput!.OffsetY);
                Assert.True(main.HasUnsavedChanges);
                Assert.False(main.IsRunning);
                await SetValue(0, "9999.123456789001");
                Fields()[0].Focus();
                Fields()[0].Select(Fields()[0].Text.Length, 0);
                Assert.Equal("9999.123456789001", Fields()[0].Text);
                await Invoke(revert);
                Assert.Equal("-10000", Fields()[0].Text);
                await Invoke(defaults);
                Assert.True(editor.Enabled);
                Assert.All(editor.VariationParameters, parameter => Assert.Equal("0", parameter.Value));
                Assert.Equal("120", editor.Parameters[0].Value);
                Assert.Equal(-10000, project.Devices[0].Camera!.NumericalInput!.OffsetX);
                Assert.False(main.IsRunning);
                await Invoke(revert);
                Assert.Equal("-10000", Fields()[0].Text);
                main.Camera.SelectedCameraId = "b";
                await Settle();
                Assert.Equal("7.5", Fields()[1].Text);
                main.Camera.SelectedCameraId = "file-camera";
                await Settle();
                Assert.False(apply.IsEnabled);
                Assert.All(Fields(), field => Assert.False(field.IsEnabled));
                Assert.True(Assert.IsType<TextBlock>(view.FindName("CameraVariationSourceRequired")).IsVisible);
                Assert.Null(project.Devices[2].Camera!.NumericalInput);
                main.Camera.LoadProject(new MachineProjectDocument(), "");
                await Settle();
                Assert.False(apply.IsEnabled);
                Assert.All(Fields(), field => Assert.False(field.IsEnabled));
                main.Camera.LoadProject(project, "");
                main.Camera.SelectedCameraId = "a";
                await Settle();
                Assert.True(apply.IsEnabled);
                Assert.Equal("-10000", Fields()[0].Text);
                string directory = Path.Combine(TestStorage.RootPath, "v15-variation-save", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string recipePath = Path.Combine(directory, "variation.ovmachine");
                await main.SaveProjectAsync(recipePath);
                Assert.False(main.HasUnsavedChanges);
                Assert.True(await main.OpenProjectAsync(recipePath));
                main.Camera.SelectedCameraId = "a";
                await Settle();
                Assert.Equal("-10000", Fields()[0].Text);
                Assert.Equal(50, main.Camera.SelectedVirtualCamera!.Camera!.NumericalInput!.Shake);
                Assert.False(main.IsRunning || main.HasUnsavedChanges);
                Assert.Equal(0, main.SceneSnapshots.Latest!.TickIndex);
                File.AppendAllText(Path.Combine(TestStorage.RootPath, "v15-variation-cases.jsonl"), JsonSerializer.Serialize(new { language, width, cases = "five-fields/two-columns/two-way/drafts/six-invalid/bounds/retry/revert/full-defaults/source-disabled/camera0/recovery/save-reopen", recipePath, monitor, Scope = "Rendered controls and AutomationPeer; no physical input or numerical generation evidence" }) + Environment.NewLine);
            }
            finally { window.Close(); view.DataContext = null; OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    private static void Capture(FrameworkElement view, double width, double height, OpenVisionLanguage language, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_CAMERA_INPUT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"camera-input-{language}-{width:0}x{height:0}-{state}.png"));
        encoder.Save(stream);
        File.WriteAllText(Path.Combine(directory, $"camera-input-{language}-{width:0}x{height:0}-{state}.json"), JsonSerializer.Serialize(new { requestedWidth = width, requestedHeight = height, actual = view.RenderSize.ToString(),
            fields = Descendants(view).OfType<ScrollViewer>().Where(control => control.Name == "CameraSourceFieldsScrollViewer").Select(control => new { control.ViewportWidth, control.ViewportHeight, control.ExtentWidth, control.ExtentHeight, control.VerticalOffset, bounds = Bounds(control, view).ToString() }),
            captions = Descendants(view).OfType<TextBlock>().Where(control => control.IsVisible && !string.IsNullOrWhiteSpace(control.Text)).Select(control => new { control.Text, wrap = control.TextWrapping.ToString(), bounds = Bounds(control, view).ToString() }) }));
    }
}

internal sealed class CameraExecutionInputFixture : IDisposable
{
    internal MachineProjectDocument Project { get; set; }
    internal SimulationSnapshot Snapshot { get; set; }
    internal CameraCommissioningProjection? ProjectionOverride { get; set; }
    internal CameraCommissioningViewModel Camera { get; }
    internal int SideEffects { get; private set; }
    internal string ProjectPath { get; }

    internal CameraExecutionInputFixture(bool canEditCapture = false)
    {
        string root = Path.Combine(TestStorage.RootPath, "camera-current-input", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        ProjectPath = Path.Combine(root, "machine.ovmachine");
        File.WriteAllText(ProjectPath, "{}");
        File.WriteAllBytes(Path.Combine(root, "images", "part.pgm"), "P5\n2 2\n255\n"u8.ToArray().Concat(new byte[] { 0, 64, 128, 255 }).ToArray());
        Project = new MachineProjectDocument
        {
            Id = "current-input-project", Name = "Current camera inputs",
            Devices = Enumerable.Range(1, 4).Select(index => new DeviceDefinition
            {
                Id = $"camera-{index}", Name = index is 2 or 4 ? "같은 이름의 웨이퍼 검사 카메라" : $"Camera {index}", Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition { SingleImageSource = new VirtualSingleImageSourceDefinition { SourceRelativePath = "images/part.pgm", Width = 2, Height = 2, PixelFormat = "Mono8" } }
            }).ToList(),
            Layouts = [new MachineLayoutDefinition { Id = "layout", Components = [new LayoutComponentDefinition { Id = "workpiece", Name = "웨이퍼 검사 작업물", Kind = LayoutComponentKind.Workpiece }] }],
            Sequences = [new SequenceDefinition { Id = "inspection", Name = "웨이퍼 검사",
                Steps = [new SequenceStepDefinition { Id = "capture-2", Name = "표면 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera-2", Parameter = "recipe-2", WorkpieceComponentId = "workpiece" },
                    new SequenceStepDefinition { Id = "capture-4", Name = "높이 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera-4", Parameter = "recipe-4", WorkpieceComponentId = "workpiece" }] }]
        };
        Snapshot = CreateSnapshot(Project.Devices.Select(definition => definition.Id is "camera-2" or "camera-4" ? Captured(definition.Id, definition.Name) : Idle(definition.Id, definition.Name)));
        CameraCommissioningViewModel? camera = null;
        camera = new CameraCommissioningViewModel(
            () => Project, () => ProjectionOverride ?? Projection(Snapshot.Cameras.FirstOrDefault(item => item.Id == camera?.SelectedCameraId)),
            () => new VisionEvidenceContext(Project.Id, "{}", "input-test-build", ProjectPath, camera?.SelectedCameraId, camera?.SelectedCameraRecipe),
            () => new ManualCameraSessionIdentity("input-session", 1),
            () => new ManualCameraTriggerRequestInput(false, "input-session", 1, Project.Id, Project.Name, ProjectPath, "{}", "input-test-build", TimeSpan.FromMilliseconds(5), Snapshot, null, null, null, null, 1),
            () => Snapshot, () => CancellationToken.None,
            (_, _) => { SideEffects++; return Task.FromResult(new SimulationCommandResult("test", true, 0, TimeSpan.Zero, SimulationCommandErrorCode.None, null)); },
            _ => { SideEffects++; }, () => camera!.RefreshProjection(),
            () => { SideEffects++; return Task.CompletedTask; }, () => { SideEffects++; }, _ => { }, (_, _) => { }, () => { }, OpenVisionLanguageService.T, exception => throw exception, canEditCapture: () => canEditCapture);
        Camera = camera;
        Camera.LoadProject(Project, ProjectPath);
    }

    internal SimulationSnapshot CreateSnapshot(IEnumerable<VirtualCameraSnapshot> cameras, long generation = 1, string? projectId = null) => new(
        TimeSpan.FromMilliseconds(30), 6, SimulationRunMode.Paused, SimulationControlOwner.EmbeddedSequence, 1,
        Array.Empty<AxisSnapshot>(), 0, Array.Empty<DigitalSignalSnapshot>(), Array.Empty<SequenceExecutionSnapshot>(), cameras,
        AutomaticRunSnapshot.NotConfigured, Array.Empty<LayoutComponentSnapshot>(), projectId: projectId ?? Project.Id, runtimeGeneration: generation);

    internal CameraCommissioningProjection Projection(VirtualCameraSnapshot? snapshot) => new(snapshot, true, snapshot?.Name,
        new VirtualSingleImageSourceDefinition { SourceRelativePath = "images/part.pgm", Width = 2, Height = 2, PixelFormat = "Mono8" },
        ProjectPath, snapshot?.CurrentRecipeId, TimeSpan.FromMilliseconds(5), SimulationRunMode.Paused, true, false, false, false, false,
        SimulationControlOwner.EmbeddedSequence, false, SequenceExecutionStatus.Completed);

    internal static VirtualCameraSnapshot Idle(string id, string name) => new(id, name, VirtualCameraState.Idle, 0, null, null, 0, 0, null);

    internal static VirtualCameraSnapshot Captured(string id, string name)
    {
        string acquisitionId = $"{id}/frame/00000001";
        var numerical = id == "camera-4" ? NumericalCameraInput.Generate(new NumericalCameraSettings { InputKind = NumericalCameraInputKind.HeightMap3D, Version = 1, ConfirmedVersion = 1 }, id, acquisitionId, 1) : null;
        var frame = numerical?.Frame ?? new VirtualCameraFrameEvidence($"frame-{id}", "images/part.pgm", new string('A', 64), 4, 2, 2, "Mono8");
        string recipe = id == "camera-2" ? "recipe-2" : "recipe-4";
        var result = new VirtualCameraAcquisitionResult(acquisitionId, id, recipe, 1, id == "camera-2" ? PlaceholderInspectionDecision.Pass : PlaceholderInspectionDecision.Fail,
            FrameEvidence: frame, WorkpieceComponentId: "workpiece", WorkpieceInstanceId: id == "camera-2" ? "run-1/WP-2" : "run-1/WP-4", NumericalInput: numerical);
        return new VirtualCameraSnapshot(id, name, VirtualCameraState.FrameReady, 1, acquisitionId, recipe, 0, 0, result, frame);
    }

    public void Dispose() => Camera.Dispose();
}
