using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class EquipmentOutlineViewAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public EquipmentOutlineViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task FocusedSearchUsesProductAccentAndRestoresTheNormalBorder()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout();
            var view = CreateView(layout);
            var window = new Window
            {
                Width = 520,
                Height = 900,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var search = Descendants(view).OfType<TextBox>().Single(textBox => textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Layout.EquipmentOutlineSearchText");
                var border = Assert.IsType<Border>(search.Template.FindName("AccentBorder", search));
                var normalColor = Assert.IsType<SolidColorBrush>(border.BorderBrush).Color;
                search.Text = "camera-01";
                Assert.Same(search, Keyboard.Focus(search));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var focusedColor = Assert.IsType<SolidColorBrush>(border.BorderBrush).Color;
                var expectedColor = Assert.IsType<SolidColorBrush>(search.FindResource("Accent.Primary")).Color;
                var width = (int)Math.Ceiling(search.ActualWidth);
                var height = (int)Math.Ceiling(search.ActualHeight);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(search), null, new Rect(0, 0, width, height));
                bitmap.Render(drawing);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                var offset = ((height - 1) * width + width / 2) * 4;
                var renderedColor = Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                var part = Descendants(view).OfType<Button>().Single(button => button.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
                Assert.Same(part, Keyboard.Focus(part));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var restoredColor = Assert.IsType<SolidColorBrush>(border.BorderBrush).Color;
                search.IsEnabled = false;
                window.UpdateLayout();
                var disabledColor = Assert.IsType<SolidColorBrush>(border.BorderBrush).Color;
                var expectedDisabledColor = Assert.IsType<SolidColorBrush>(search.FindResource("TextControlBorderBrushDisabled")).Color;
                return (focusedColor, expectedColor, renderedColor, normalColor, restoredColor,
                    disabledColor, expectedDisabledColor, query: layout.EquipmentOutlineSearchText);
            }
            finally { window.Close(); }
        });

        Assert.Equal(Color.FromRgb(0x08, 0x7F, 0x73), result.expectedColor);
        Assert.Equal(result.expectedColor, result.focusedColor);
        Assert.Equal(result.expectedColor, result.renderedColor);
        Assert.Equal(result.normalColor, result.restoredColor);
        Assert.Equal(result.expectedDisabledColor, result.disabledColor);
        Assert.Equal("camera-01", result.query);
    }

    [Fact]
    public async Task OutlineControlsExposeLocalizedNamesAndAreKeyboardReachable()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                using var layout = CreateLayout();
                var view = CreateView(layout);
                var koreanNames = ReadPartAutomationNames(view);
                var koreanInactiveUnitStatus = ReadUnitAutomationStatuses(view);
                layout.ShowUnit("unit-inspection");
                Arrange(view);
                var koreanActiveUnitStatus = ReadUnitAutomationStatuses(view);
                layout.ShowOverview();
                Arrange(view);

                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
                layout.RefreshLocalization();
                Arrange(view);
                var englishNames = ReadPartAutomationNames(view);
                var englishInactiveUnitStatus = ReadUnitAutomationStatuses(view);
                layout.ShowUnit("unit-inspection");
                Arrange(view);
                var englishActiveUnitStatus = ReadUnitAutomationStatuses(view);
                layout.ShowOverview();
                Arrange(view);

                var window = new Window
                {
                    Width = 520,
                    Height = 900,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };

                window.Show();
                try
                {
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var controls = Descendants(view).ToArray();
                    var search = controls.OfType<TextBox>().Single(textBox =>
                        textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path
                        == "Layout.EquipmentOutlineSearchText");
                    var part = controls.OfType<Button>().Single(button =>
                        button.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
                    var selection = ReadSelectionControls(view, keepOpen: true).Single(checkBox =>
                        checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");

                    part.ContextMenu!.IsOpen = false;
                    Assert.Same(search, Keyboard.Focus(search));
                    var reachedPart = false;
                    var reachedSelection = false;
                    var visited = new HashSet<UIElement>();
                    for (var step = 0; step < 12 && !(reachedPart && reachedSelection); step++)
                    {
                        if (Keyboard.FocusedElement is not UIElement focusedElement || !visited.Add(focusedElement)) break;
                        reachedPart |= ReferenceEquals(focusedElement, part);
                        if (!focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) break;
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    }

                    part.ContextMenu!.PlacementTarget = part;
                    part.ContextMenu.IsOpen = true;
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    reachedSelection = ReferenceEquals(selection, Keyboard.Focus(selection));
                    var toggleCommandBound = ReferenceEquals(selection.Command, layout.ToggleEquipmentOutlinePartCommand);
                    var togglePattern = Assert.IsAssignableFrom<IToggleProvider>(
                        UIElementAutomationPeer.CreatePeerForElement(selection)?.GetPattern(PatternInterface.Toggle));
                    togglePattern.Toggle();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.UpdateLayout();
                    var selectedControl = ReadSelectionControls(view, keepOpen: true).Single(checkBox =>
                        checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
                    var selectedStateBound = selectedControl.IsChecked == true
                        && layout.SelectionCount == 1 && layout.SelectedItem?.Id == "camera-01";

                    var selectedTogglePattern = Assert.IsAssignableFrom<IToggleProvider>(
                        UIElementAutomationPeer.CreatePeerForElement(selectedControl)?.GetPattern(PatternInterface.Toggle));
                    selectedTogglePattern.Toggle();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.UpdateLayout();
                    var deselectedControl = ReadSelectionControls(view, keepOpen: true).Single(checkBox =>
                        checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
                    var deselectedStateBound = deselectedControl.IsChecked == false && layout.SelectionCount == 0;

                    Assert.Same(deselectedControl, Keyboard.Focus(deselectedControl));
                    var inputSource = PresentationSource.FromVisual(deselectedControl)
                        ?? throw new InvalidOperationException("The equipment outline selection control had no presentation source.");
                    void RaiseSpace()
                    {
                        deselectedControl.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space)
                        {
                            RoutedEvent = Keyboard.KeyDownEvent
                        });
                        deselectedControl.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space)
                        {
                            RoutedEvent = Keyboard.KeyUpEvent
                        });
                    }

                    RaiseSpace();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.UpdateLayout();
                    var keyboardSelectedStateBound = deselectedControl.IsChecked == true
                        && layout.SelectionCount == 1 && layout.SelectedItem?.Id == "camera-01";

                    RaiseSpace();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.UpdateLayout();
                    var keyboardDeselectedStateBound = deselectedControl.IsChecked == false && layout.SelectionCount == 0;

                    return (koreanNames, englishNames, koreanInactiveUnitStatus, koreanActiveUnitStatus,
                        englishInactiveUnitStatus, englishActiveUnitStatus, reachedPart, reachedSelection,
                        toggleCommandBound, selectedStateBound, deselectedStateBound,
                        keyboardSelectedStateBound, keyboardDeselectedStateBound);
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

        Assert.Equal(("검사 카메라 / camera-01 / 2D 카메라", "camera-01 선택", "검사 유닛 / 장치 수: 1", "빈 유닛 / 장치 수: 0"), result.koreanNames);
        Assert.Equal(("검사 카메라 / camera-01 / 2D camera", "Select camera-01", "검사 유닛 / component count: 1", "빈 유닛 / component count: 0"), result.englishNames);
        Assert.Equal((string.Empty, string.Empty), result.koreanInactiveUnitStatus);
        Assert.Equal(("현재 유닛", string.Empty), result.koreanActiveUnitStatus);
        Assert.Equal((string.Empty, string.Empty), result.englishInactiveUnitStatus);
        Assert.Equal(("Current unit", string.Empty), result.englishActiveUnitStatus);
        Assert.True(result.reachedPart);
        Assert.True(result.reachedSelection);
        Assert.True(result.toggleCommandBound);
        Assert.True(result.selectedStateBound);
        Assert.True(result.deselectedStateBound);
        Assert.True(result.keyboardSelectedStateBound);
        Assert.True(result.keyboardDeselectedStateBound);
    }

    [Fact]
    public async Task DisabledOutlineAutomationToggleDoesNotChangeSelection()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout();
            var view = CreateView(layout);
            var window = new Window
            {
                Width = 520,
                Height = 900,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };

            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var selection = ReadSelectionControls(view, keepOpen: true).Single(checkBox =>
                    checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
                selection.IsEnabled = false;
                var togglePattern = Assert.IsAssignableFrom<IToggleProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(selection)?.GetPattern(PatternInterface.Toggle));

                var toggleException = Record.Exception(togglePattern.Toggle);
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                window.UpdateLayout();
                return (toggleException?.GetType(), selection.IsChecked, layout.SelectionCount);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(typeof(System.Windows.Automation.ElementNotEnabledException), result.Item1);
        Assert.False(result.IsChecked);
        Assert.Equal(0, result.SelectionCount);
    }

    [Fact]
    public async Task UnitRemovalMenuInvokesTheMainViewModelCommandAndUsesTheUnitName()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
                var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection unit" };
                var station = new MachineStationDefinition
                {
                    Id = "station-main",
                    Name = "Main station",
                    Units = [unit]
                };
                var layout = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
                var project = new MachineProjectDocument
                {
                    Name = "Outline unit deletion",
                    Stations = [station],
                    Layouts = [layout]
                };
                project.Simulation.ActiveLayoutId = layout.Id;
                using var viewModel = new MainViewModel(project);
                viewModel.EquipmentUnitRemovalPrompt = (_, _) => true;
                viewModel.ShowEquipmentUnitCommand.Execute(unit.Id);
                var view = new EquipmentOutlineView { DataContext = viewModel };
                var window = new Window
                {
                    Width = 520,
                    Height = 900,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };

                window.Show();
                try
                {
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var unitButton = Descendants(view).OfType<Button>().Single(button =>
                        button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == unit.Id);
                    unitButton.ContextMenu!.PlacementTarget = unitButton;
                    unitButton.ContextMenu.IsOpen = true;
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var deleteButton = Assert.IsType<MenuItem>(unitButton.ContextMenu.Items[0]);
                    Assert.Same(viewModel.RemoveEquipmentUnitCommand, deleteButton.Command);
                    var automationName = UIElementAutomationPeer.CreatePeerForElement(deleteButton)?.GetName() ?? string.Empty;
                    var commandEnabled = deleteButton.IsEnabled;
                    var invoke = Assert.IsAssignableFrom<IInvokeProvider>(
                        UIElementAutomationPeer.CreatePeerForElement(deleteButton)?.GetPattern(PatternInterface.Invoke));
                    invoke.Invoke();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

                    return (automationName, commandEnabled, station.Units.Count,
                        viewModel.Layout.ActiveUnitId, viewModel.HasUnsavedChanges);
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

        Assert.Equal("Delete unit Inspection unit", result.automationName);
        Assert.True(result.commandEnabled);
        Assert.Equal(0, result.Count);
        Assert.Null(result.ActiveUnitId);
        Assert.True(result.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(215)]
    [InlineData(240)]
    [InlineData(300)]
    public async Task EquipmentHeaderKeepsAddActionsTogetherAboveSearch(int width)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            try
            {
                using var main = new MainViewModel(new MachineProjectDocument());
                var view = new LeftToolRegionView { DataContext = main };
                view.Measure(new Size(width, 800));
                view.Arrange(new Rect(0, 0, width, 800));
                view.UpdateLayout();
                var addUnit = Descendants(view).OfType<Button>().Single(button => button.Name == "OutlineAddUnitButton");
                var addDevice = Descendants(view).OfType<Button>().Single(button => button.Name == "OutlineAddDeviceButton");
                var search = Descendants(view).OfType<TextBox>().Single(textBox => textBox.Name == "OutlineSearchTextBox");
                var title = Descendants(view).OfType<TextBlock>().Single(block => block.Name == "EquipmentOutlineTitle");
                Assert.Equal("장비", title.Text);
                Assert.Equal("Segoe UI, Malgun Gothic", title.FontFamily.Source);
                Assert.Equal(13, title.FontSize);
                Assert.Equal(FontWeights.Bold, title.FontWeight);
                Assert.Equal(18.85, title.LineHeight);
                foreach (var button in new[] { addUnit, addDevice })
                {
                    Assert.Equal("Segoe UI, Malgun Gothic", button.FontFamily.Source);
                    Assert.Equal(13, button.FontSize);
                    Assert.Equal(FontWeights.Normal, button.FontWeight);
                    var caption = Assert.IsType<TextBlock>(button.Content);
                    Assert.Equal(button.Name == "OutlineAddUnitButton" ? "+ 유닛" : "+ 장치", caption.Text);
                    Assert.Equal("Segoe UI, Malgun Gothic", caption.FontFamily.Source);
                    Assert.Equal(13, caption.FontSize);
                }
                Assert.Equal("Segoe UI, Malgun Gothic", search.FontFamily.Source);
                Assert.Equal("장치 검색", AutomationProperties.GetName(search));
                var unitTop = addUnit.TranslatePoint(new Point(), view).Y;
                var deviceTop = addDevice.TranslatePoint(new Point(), view).Y;
                Assert.Equal(unitTop, deviceTop, precision: 2);
                Assert.True(search.TranslatePoint(new Point(), view).Y >= unitTop + addUnit.ActualHeight);
                // R24 Wide input is 28.84375 CSS px, rounded to 29 native WPF px.
                Assert.Equal(29, search.ActualHeight);
                Assert.True(addUnit.TranslatePoint(new Point(addUnit.ActualWidth, 0), view).X <= width);
                Assert.True(addDevice.TranslatePoint(new Point(addDevice.ActualWidth, 0), view).X <= width);
                Assert.Same(main.OpenStationUnitEditorCommand, addUnit.Command);
                Assert.Same(main.OpenEquipmentComponentLibraryDialogCommand, addDevice.Command);
                return true;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }


    [Fact]
    public async Task OutlineRendersProposalTypographyAndQueryInBothLanguages()
    {
        await _ui.InvokeAsync(() =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                using var layout = CreateLayout();
                var view = CreateView(layout);
                foreach (var language in new[] { OpenVisionLanguage.Korean, OpenVisionLanguage.English })
                {
                    OpenVisionLanguageService.SetLanguage(language, save: false);
                    layout.RefreshLocalization();
                    Arrange(view);
                    var blocks = Descendants(view).OfType<TextBlock>().ToArray();
                    foreach (var caption in new[] { "검사 카메라", "검사 유닛", "Main", OpenVisionLanguageService.T("Equipment.OverviewButton") })
                    {
                        var block = Assert.Single(blocks, block => block.Text == caption);
                        Assert.Equal("Segoe UI, Malgun Gothic", block.FontFamily.Source);
                        Assert.Equal(13, block.FontSize);
                        Assert.Equal(FontWeights.Normal, block.FontWeight);
                        Assert.Equal(18.85, block.LineHeight);
                        Assert.True(block.ActualHeight >= block.LineHeight);
                    }
                    var detail = Assert.Single(blocks, block => block.Text.StartsWith("camera-01 · ", StringComparison.Ordinal));
                    Assert.Equal("Segoe UI, Malgun Gothic", detail.FontFamily.Source);
                    Assert.Equal(10.5, detail.FontSize);
                    Assert.Equal(FontWeights.Normal, detail.FontWeight);
                    Assert.Equal(15.225, detail.LineHeight);
                    layout.EquipmentOutlineSearchText = "absent";
                    Arrange(view);
                    var empty = Assert.Single(Descendants(view).OfType<TextBlock>(), block => block.Text == "\"absent\" " + OpenVisionLanguageService.T("Equipment.OutlineNoMatches"));
                    Assert.Equal(11.5, empty.FontSize);
                    Assert.Equal(16.675, empty.LineHeight);
                    layout.ClearEquipmentOutlineSearchCommand.Execute(null);
                    Arrange(view);
                    Assert.Contains(Descendants(view).OfType<TextBlock>(), block => block.Text == "검사 카메라" && block.FontSize == 13);
                }
                return true;
            }
            finally { OpenVisionLanguageService.SetLanguage(originalLanguage, save: false); }
        });
    }

    private static MachineLayoutViewModel CreateLayout()
    {
        var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "검사 유닛" };
        var emptyUnit = new MachineUnitDefinition { Id = "unit-empty", Name = "빈 유닛" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-01", Name = "검사 카메라", Kind = LayoutComponentKind.Camera,
            UnitId = unit.Id, Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [new MachineStationDefinition { Id = "station-main", Name = "Main", Units = [unit, emptyUnit] }],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static EquipmentOutlineView CreateView(MachineLayoutViewModel layout)
    {
        var view = new EquipmentOutlineView { DataContext = new { Layout = layout } };
        Arrange(view);
        return view;
    }

    private static (string PartName, string SelectionName, string UnitName, string EmptyUnitName) ReadPartAutomationNames(EquipmentOutlineView view)
    {
        var descendants = Descendants(view).ToArray();
        var part = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
        var selection = ReadSelectionControls(view).Single(checkBox =>
            checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
        var unit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-inspection"
            && button.Content is Grid);
        var emptyUnit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-empty"
            && button.Content is Grid);
        return (
            UIElementAutomationPeer.CreatePeerForElement(part)?.GetName() ?? string.Empty,
            UIElementAutomationPeer.CreatePeerForElement(selection)?.GetName() ?? string.Empty,
            UIElementAutomationPeer.CreatePeerForElement(unit)?.GetName() ?? string.Empty,
            UIElementAutomationPeer.CreatePeerForElement(emptyUnit)?.GetName() ?? string.Empty);
    }

    private static (string UnitStatus, string EmptyUnitStatus) ReadUnitAutomationStatuses(EquipmentOutlineView view)
    {
        var descendants = Descendants(view).ToArray();
        var unit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-inspection"
            && button.Content is Grid);
        var emptyUnit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-empty"
            && button.Content is Grid);
        return (
            UIElementAutomationPeer.CreatePeerForElement(unit)?.GetItemStatus() ?? string.Empty,
            UIElementAutomationPeer.CreatePeerForElement(emptyUnit)?.GetItemStatus() ?? string.Empty);
    }

    private static IEnumerable<CheckBox> ReadSelectionControls(EquipmentOutlineView view, bool keepOpen = false)
    {
        foreach (var part in Descendants(view).OfType<Button>().Where(button => button.DataContext is EquipmentOutlinePartItem))
        {
            var menu = part.ContextMenu!;
            menu.PlacementTarget = part;
            menu.IsOpen = true;
            menu.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(menu.UpdateLayout));
            try
            {
                yield return Assert.IsType<EquipmentOutlineSelectionCheckBox>(Assert.IsType<MenuItem>(menu.Items[0]).Header);
            }
            finally
            {
                if (!keepOpen) menu.IsOpen = false;
            }
        }
    }

    private static void Arrange(FrameworkElement view)
    {
        view.Measure(new Size(360, 720));
        view.Arrange(new Rect(0, 0, 360, 720));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

}
