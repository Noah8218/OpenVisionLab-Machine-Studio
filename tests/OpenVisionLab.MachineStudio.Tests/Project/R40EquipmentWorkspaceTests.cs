using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40EquipmentWorkspaceTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, false)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, false)]
    [InlineData(1920, 1032, OpenVisionLanguage.English, false)]
    [InlineData(1280, 760, OpenVisionLanguage.English, false)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, true)]
    [InlineData(1280, 760, OpenVisionLanguage.English, true)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, true)]
    public async Task EquipmentWorkflowKeepsContextAndRoutesExistingCommandsWithoutRunningOrSaving(int width, int height, OpenVisionLanguage language, bool longName)
    {
        await ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var path = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_R40_EQUIPMENT_PROJECT")
                ?? Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine");
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(path));
            if (longName)
            {
                project.Layouts.SelectMany(layout => layout.Components).First(component => component.Kind == OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.LinearStage).Name = new string('가', 60);
                project.Stations[0].Units[0].Name = new string('나', 60);
            }
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var original = store.SerializeForEvidence(project);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump(window);
                Assert.True(model.Navigation.IsEquipmentInspectorOpen);
                Assert.True(Find<StackPanel>(window, "EquipmentOverviewInspector").IsVisible);
                Assert.Same(model.MaterialFlowSettings.OpenCommand, Find<Button>(window, "EquipmentProductSetupButton").Command);
                Assert.Same(model.OpenEquipmentComponentLibraryDialogCommand, Find<Button>(window, "OutlineAddDeviceButton").Command);
                Assert.Same(model.SemiconductorRecipes.OpenCommand, Find<Button>(window, "EquipmentRecipeExamplesButton").Command);
                foreach (var name in new[] { "EquipmentAuthoringNavigation", "EquipmentTaskHeader", "EquipmentProductSetupButton", "EquipmentOpenProcessButton" })
                    AssertInside(window, Find<FrameworkElement>(window, name));

                var item = model.Layout.Items.First(candidate => candidate.Kind == LayoutItemKind.LinearStage);
                model.Layout.Select(item.Id);
                Pump(window);
                Assert.True(Find<Border>(window, "EquipmentSelectionContextCard").IsVisible);
                Assert.Equal(model.EquipmentSelectionStatusText, Find<TextBlock>(window, "EquipmentTaskContext").Text);
                Assert.Same(model.OpenEquipmentSequenceReferencesCommand, Find<Button>(window, "EquipmentContextReferencesButton").Command);
                var viewport = Find<MachineSceneViewport>(window, "SceneViewport");
                Assert.False(viewport.IsPlacementMoveEnabled);
                var move = Find<RadioButton>(window, "EquipmentMoveToolButton");
                var select = Find<RadioButton>(window, "EquipmentSelectionToolButton");
                move.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Pump(window);
                Assert.True(model.Layout.IsPlacementMoveTool);
                Assert.True(viewport.IsPlacementMoveEnabled);
                Assert.False(select.IsChecked);
                select.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Pump(window);
                Assert.False(model.Layout.IsPlacementMoveTool);
                Assert.False(viewport.IsPlacementMoveEnabled);
                Assert.True(select.IsChecked);
                Assert.InRange(viewport.ActualWidth, 620, width);
                Assert.InRange(viewport.ActualHeight, 400, height);
                AssertInside(window, Find<FrameworkElement>(window, "EquipmentViewToolbarGroup"));
                Assert.InRange(Find<Border>(window, "EquipmentTaskHeader").ActualHeight, 40, 64);
                Assert.InRange(Find<Border>(window, "EquipmentSelectionActions").ActualHeight, 20, 38);
                Assert.Equal(TextWrapping.NoWrap, Find<TextBlock>(window, "EquipmentSelectionDetail").TextWrapping);
                var selectionBounds = Find<Border>(window, "EquipmentSelectionActions").TransformToAncestor(window).TransformBounds(new Rect(Find<Border>(window, "EquipmentSelectionActions").RenderSize));
                var legendBounds = Find<WrapPanel>(window, "EquipmentSceneLegend").TransformToAncestor(window).TransformBounds(new Rect(Find<WrapPanel>(window, "EquipmentSceneLegend").RenderSize));
                Assert.False(selectionBounds.IntersectsWith(legendBounds), $"Selection {selectionBounds} overlaps legend {legendBounds}.");

                Find<Button>(window, "EquipmentOpenProcessButton").Command.Execute(null);
                Pump(window);
                Assert.True(model.Navigation.IsSequenceWorkspace);
                model.Navigation.IsEquipmentWorkspace = true;
                Pump(window);
                Assert.True(model.Navigation.IsEquipmentInspectorOpen);
                Assert.Same(item, model.Layout.SelectedItem);
                Assert.False(model.Layout.IsPlacementMoveTool);
                Assert.False(model.IsRunning);
                Assert.False(model.HasUnsavedChanges);
                Assert.Equal(original, store.SerializeForEvidence(project));
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return true;
        });
    }

    [Fact]
    public async Task RenderedPlacementFieldsRejectNonFiniteInputThenApplySaveAndReopenDistinctCoordinates()
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var path = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_R40_EQUIPMENT_PROJECT")
                ?? Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine");
            var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var item = model.Layout.Items.First(candidate => candidate.Kind == LayoutItemKind.LinearStage);
            model.Layout.Select(item.Id);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
            window.Show();
            try
            {
                Pump(window);
                TextBox Coordinate(string axis) => SmokeVisualTreeQuery.FindVisualDescendant<TextBox>(window,
                    box => box.IsVisible && AutomationProperties.GetAutomationId(box) == "EquipmentCoordinate" + axis)!;
                var editor = model.Layout.SelectedComponentEditor!;
                var originalX = item.CurrentX;
                var originalY = item.CurrentY;
                var x = Coordinate("X");
                var y = Coordinate("Y");
                var apply = Find<Button>(window, "EquipmentPlacementApplyButton");
                AssertInside(window, x);
                AssertInside(window, y);
                x.SetCurrentValue(TextBox.TextProperty, "17.5");
                y.SetCurrentValue(TextBox.TextProperty, "NaN");
                Pump(window);
                apply.Command.Execute(null);
                Pump(window);
                Assert.True(editor.HasPlacementValidationErrors);
                Assert.Equal(originalX, item.CurrentX);
                Assert.Equal(originalY, item.CurrentY);
                Assert.Equal("17.5", x.Text);
                Assert.Equal("NaN", y.Text);
                Assert.False(model.HasUnsavedChanges);
                Find<Button>(window, "EquipmentPlacementDiscardButton").Command.Execute(null);
                Pump(window);
                Assert.False(editor.HasPendingPlacementDraft);
                Assert.False(editor.HasPlacementValidationErrors);
                x.SetCurrentValue(TextBox.TextProperty, "17.5");
                y.SetCurrentValue(TextBox.TextProperty, "24.25");
                Pump(window);
                Assert.Equal(originalX, item.CurrentX);
                Assert.Equal(originalY, item.CurrentY);
                apply.Command.Execute(null);
                Pump(window);
                Assert.Equal(17.5, item.CurrentX);
                Assert.Equal(24.25, item.CurrentY);
                Assert.True(model.HasUnsavedChanges);
                Assert.False(model.IsRunning);
                var savePath = Path.Combine(TestStorage.RootPath, "r40-equipment", Guid.NewGuid().ToString("N"), "placement.ovmachine");
                Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                var store = new ProjectDocumentFileStore();
                await store.SaveAsync(project, savePath);
                var reopened = store.Load(savePath).Layouts.SelectMany(layout => layout.Components).Single(component => component.Id == item.Id);
                Assert.Equal(17.5, reopened.Transform.X);
                Assert.Equal(24.25, reopened.Transform.Y);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, "camera-top-2d", "mount")]
    [InlineData(1280, 760, OpenVisionLanguage.English, "camera-top-2d", "mount")]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, "sensor-load", "target")]
    [InlineData(1280, 760, OpenVisionLanguage.Korean, "wafer-stage", null)]
    public async Task SettingsPopupSmokeUsesCurrentChoiceAndPreservesDefinition(int width, int height, OpenVisionLanguage language, string componentId, string? choiceId)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var path = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_R40_POPUP_PROJECT")
                ?? Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine");
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(path));
            project.Axes[0].Name = new string('가', 60);
            var original = store.SerializeForEvidence(project);
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            model.Layout.Select(componentId);
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            FrameworkElement? capturedPopup = null;
            var interaction = new SmokeUiInteraction
            {
                FindTextBlock = (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendant(view, predicate),
                FindButton = (view, predicate) => SmokeVisualTreeQuery.FindVisualDescendant(view, predicate),
                ActivateWindow = () => throw new InvalidOperationException("No native input in the settings popup smoke."),
                MovePointerToCenter = _ => throw new InvalidOperationException("No native input in the settings popup smoke."),
                MouseEvent = (_, _, _, _, _) => throw new InvalidOperationException("No native input in the settings popup smoke."),
                SetCursorPosition = (_, _) => throw new InvalidOperationException("No native input in the settings popup smoke."),
                GetCursorPosition = () => throw new InvalidOperationException("No native input in the settings popup smoke."),
                SetPopupContent = content => capturedPopup = content,
                MarkSmokePointerHeld = () => throw new InvalidOperationException("No native input in the settings popup smoke."),
                ReleaseSmokePointer = () => throw new InvalidOperationException("No native input in the settings popup smoke."),
                CheckPointerOwnership = _ => throw new InvalidOperationException("No native input in the settings popup smoke.")
            };
            try
            {
                Pump(window);
                if (choiceId is not null)
                {
                    await SmokeLayoutPropertyStateVerifier.ApplyAsync(window, model, "drive-popup", interaction);
                    var field = model.Layout.SelectedComponentEditor!.SettingsFields.Single(candidate => candidate.Id == choiceId);
                    var combo = SmokeVisualTreeQuery.FindVisualDescendant<ComboBox>(window,
                        candidate => candidate.IsVisible && ReferenceEquals(candidate.DataContext, field))!;
                    Assert.True(combo.IsDropDownOpen);
                    Assert.Equal(field.Baseline, combo.SelectedValue);
                    Assert.Same(field.Options, combo.ItemsSource);
                    var popup = SmokeVisualTreeQuery.FindVisualDescendant<Popup>(combo)!;
                    Assert.True(popup.IsOpen);
                    Assert.Same(popup.Child, capturedPopup);
                    Assert.True(capturedPopup!.IsVisible);
                    Assert.True(capturedPopup.ActualWidth > 0 && capturedPopup.ActualHeight > 0);
                    var captureRoot = Path.Combine(TestStorage.RootPath, "settings-popup", componentId, language.ToString());
                    Directory.CreateDirectory(captureRoot);
                    var dpi = VisualTreeHelper.GetDpi(capturedPopup);
                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(capturedPopup.ActualWidth * dpi.DpiScaleX),
                        (int)Math.Ceiling(capturedPopup.ActualHeight * dpi.DpiScaleY),
                        dpi.PixelsPerInchX,
                        dpi.PixelsPerInchY,
                        PixelFormats.Pbgra32);
                    bitmap.Render(capturedPopup);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var image = File.Create(Path.Combine(captureRoot, "popup-direct.png"))) encoder.Save(image);
                    var items = combo.Items.Cast<object>().Select(value => (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromItem(value)).ToArray();
                    Assert.Same(window.TryFindResource(typeof(ComboBoxItem)), combo.ItemContainerStyle.BasedOn);
                    Assert.All(items, item => Assert.Equal(((LayoutPropertyOption)item.DataContext).IsEnabled, item.IsEnabled));
                    File.WriteAllText(Path.Combine(captureRoot, "popup.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Build = typeof(MainViewModel).Assembly.Location,
                        Size = capturedPopup.RenderSize.ToString(),
                        DescendantBounds = VisualTreeHelper.GetDescendantBounds(capturedPopup).ToString(),
                        Items = items.Select(item => new { item.ActualWidth, item.ActualHeight, item.FontSize, Background = item.Background?.ToString(), HasBasedOn = item.Style?.BasedOn is not null }).ToArray()
                    }));
                    combo.IsDropDownOpen = false;
                    Pump(window);
                    Assert.False(popup.IsOpen);
                }
                else
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        SmokeLayoutPropertyStateVerifier.ApplyAsync(window, model, "drive-popup", interaction));
                    Assert.Contains("no visible choice field", error.Message);
                    Assert.Null(capturedPopup);
                }
                Assert.True(model.IsEquipmentDriveTabOpen);
                Assert.False(model.Layout.SelectedComponentEditor!.HasPendingInspectorDraft);
                Assert.False(model.HasUnsavedChanges);
                Assert.False(model.IsRunning);
                Assert.Equal(original, store.SerializeForEvidence(project));
            }
            finally
            {
                foreach (var combo in SmokeVisualTreeQuery.FindVisualDescendants<ComboBox>(window)) combo.IsDropDownOpen = false;
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        SmokeVisualTreeQuery.FindVisualDescendant<T>(root, element => element.Name == name)
        ?? throw new InvalidOperationException($"Missing {name}.");

    private static void AssertInside(ShellWindow window, FrameworkElement element)
    {
        Assert.True(element.IsVisible);
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Assert.True(new Rect(window.RenderSize).Contains(bounds), $"{element.Name}: {bounds} outside {window.RenderSize}.");
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
