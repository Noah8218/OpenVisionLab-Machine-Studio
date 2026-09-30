using System.Windows;
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
                    var selection = controls.OfType<CheckBox>().Single(checkBox =>
                        checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");

                    Assert.Same(search, Keyboard.Focus(search));
                    var reachedPart = false;
                    var reachedSelection = false;
                    var visited = new HashSet<UIElement>();
                    for (var step = 0; step < 12 && !(reachedPart && reachedSelection); step++)
                    {
                        if (Keyboard.FocusedElement is not UIElement focusedElement || !visited.Add(focusedElement)) break;
                        reachedPart |= ReferenceEquals(focusedElement, part);
                        reachedSelection |= ReferenceEquals(focusedElement, selection);
                        if (!focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) break;
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    }

                    var toggleCommandBound = ReferenceEquals(selection.Command, layout.ToggleEquipmentOutlinePartCommand);
                    var togglePattern = Assert.IsAssignableFrom<IToggleProvider>(
                        UIElementAutomationPeer.CreatePeerForElement(selection)?.GetPattern(PatternInterface.Toggle));
                    togglePattern.Toggle();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.UpdateLayout();
                    var selectedControl = Descendants(view).OfType<CheckBox>().Single(checkBox =>
                        checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
                    var selectedStateBound = selectedControl.IsChecked == true
                        && layout.SelectionCount == 1 && layout.SelectedItem?.Id == "camera-01";

                    var selectedTogglePattern = Assert.IsAssignableFrom<IToggleProvider>(
                        UIElementAutomationPeer.CreatePeerForElement(selectedControl)?.GetPattern(PatternInterface.Toggle));
                    selectedTogglePattern.Toggle();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.UpdateLayout();
                    var deselectedControl = Descendants(view).OfType<CheckBox>().Single(checkBox =>
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

        Assert.Equal(("검사 카메라 / camera-01 / 카메라", "camera-01 선택", "검사 유닛 / 부품 수: 1", "빈 유닛 / 부품 수: 0"), result.koreanNames);
        Assert.Equal(("검사 카메라 / camera-01 / Camera", "Select camera-01", "검사 유닛 / component count: 1", "빈 유닛 / component count: 0"), result.englishNames);
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
                var selection = Descendants(view).OfType<CheckBox>().Single(checkBox =>
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
    public async Task UnitRemovalButtonInvokesTheMainViewModelCommandAndUsesTheUnitName()
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
                    var deleteButton = Descendants(view).OfType<Button>().Single(button =>
                        ReferenceEquals(button.Command, viewModel.RemoveEquipmentUnitCommand));
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
        var selection = descendants.OfType<CheckBox>().Single(checkBox =>
            checkBox.DataContext is EquipmentOutlinePartItem item && item.Item.Id == "camera-01");
        var unit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-inspection"
            && button.Content is DockPanel);
        var emptyUnit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-empty"
            && button.Content is DockPanel);
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
            && button.Content is DockPanel);
        var emptyUnit = descendants.OfType<Button>().Single(button =>
            button.DataContext is EquipmentOutlineUnitItem item && item.UnitId == "unit-empty"
            && button.Content is DockPanel);
        return (
            UIElementAutomationPeer.CreatePeerForElement(unit)?.GetItemStatus() ?? string.Empty,
            UIElementAutomationPeer.CreatePeerForElement(emptyUnit)?.GetItemStatus() ?? string.Empty);
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
