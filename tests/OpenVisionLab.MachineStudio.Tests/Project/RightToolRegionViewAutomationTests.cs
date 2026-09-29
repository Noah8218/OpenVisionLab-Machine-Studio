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
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(EquipmentOutlineViewAutomationTestCollection.Name)]
public sealed class RightToolRegionViewAutomationTests
{
    [Fact]
    public async Task UnitAssignmentComboBoxUpdatesSelectedComponentThroughBinding()
    {
        var assignment = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            var project = CreateProject();
            using var layout = new MachineLayoutViewModel();
            layout.Load(project);
            layout.Select("camera-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;

            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            Arrange(view);

            var comboBox = Descendants(view).OfType<ComboBox>().Single(candidate =>
                ReferenceEquals(candidate.ItemsSource, editor.UnitOptions));
            var initialValue = comboBox.SelectedValue as string;
            comboBox.SelectedValue = "unit-camera";
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

            var roundTripRoot = Path.Combine(TestStorage.RootPath, "r19-priority-12-unit-assignment", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(roundTripRoot);
            var projectPath = Path.Combine(roundTripRoot, "unit-assignment.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            fileStore.SaveAsync(project, projectPath).GetAwaiter().GetResult();
            Assert.True(File.Exists(projectPath));
            var reopenedProject = fileStore.Load(projectPath);
            using var reopenedLayout = new MachineLayoutViewModel();
            reopenedLayout.Load(reopenedProject);
            reopenedLayout.Select("camera-01");
            var reopenedEditor = Assert.IsType<LayoutComponentEditorViewModel>(reopenedLayout.SelectedComponentEditor);
            var reopenedView = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(reopenedLayout, navigation, IsRunMode: false)
            };
            Arrange(reopenedView);
            var reopenedComboBox = Descendants(reopenedView).OfType<ComboBox>().Single(candidate =>
                ReferenceEquals(candidate.ItemsSource, reopenedEditor.UnitOptions));

            return (
                InitialValue: initialValue,
                EditorUnitId: editor.UnitId,
                ComponentUnitId: layout.SelectedItem?.Component?.UnitId,
                SavedProjectPath: projectPath,
                ReopenedEditorUnitId: reopenedEditor.UnitId,
                ReopenedComboBoxValue: reopenedComboBox.SelectedValue as string);
        });

        Assert.Equal("unit-press", assignment.InitialValue);
        Assert.Equal("unit-camera", assignment.EditorUnitId);
        Assert.Equal("unit-camera", assignment.ComponentUnitId);
        Assert.True(File.Exists(assignment.SavedProjectPath));
        Assert.Equal("unit-camera", assignment.ReopenedEditorUnitId);
        Assert.Equal("unit-camera", assignment.ReopenedComboBoxValue);
    }

    [Fact]
    public async Task MultiSelectionPropertiesPaneUsesR19FirstSelectedComponent()
    {
        var state = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            var project = CreateProject();
            var layoutDefinition = Assert.Single(project.Layouts);
            layoutDefinition.Components.Add(new LayoutComponentDefinition
            {
                Id = "frame-02", Name = "Frame 02", Kind = LayoutComponentKind.MachineFrame,
                UnitId = "unit-press", Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
            });

            using var layout = new MachineLayoutViewModel();
            layout.Load(project);
            layout.SelectMany(["camera-01", "frame-02"], "frame-02");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            Arrange(view);

            var title = Descendants(view).OfType<TextBlock>().Single(candidate =>
                candidate.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "PaneTitle");
            var headerStack = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(title));
            var editorHeader = Assert.IsType<Grid>(VisualTreeHelper.GetParent(headerStack));
            var id = Descendants(editorHeader).OfType<TextBlock>().Single(candidate =>
                candidate.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "Id");

            return (
                SelectionCount: layout.SelectionCount,
                PrimaryId: layout.SelectedItem?.Id,
                PropertiesComponentId: editor.Id,
                PropertiesTitle: title.Text,
                PropertiesId: id.Text);
        });

        Assert.Equal(2, state.SelectionCount);
        Assert.Equal("frame-02", state.PrimaryId);
        Assert.Equal("camera-01", state.PropertiesComponentId);
        Assert.Equal("Inspection Camera", state.PropertiesTitle);
        Assert.Equal("camera-01", state.PropertiesId);
    }

    [Fact]
    public async Task VerticalEnvelopeTextBoxKeepsInvalidDraftThenAppliesValidBoundary()
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            using var layout = CreateEnvelopeLayout();
            layout.Select("frame-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            var component = layout.SelectedItem!.Component!;
            var changes = 0;
            layout.DefinitionChanged += (_, _) => changes++;
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
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
                var height = controls.OfType<TextBox>().Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "EquipmentDraftVerticalHeight");
                var elevation = controls.OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "DraftBaseElevationText");
                var error = controls.OfType<TextBlock>().Single(textBlock =>
                    textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "PlacementDraftError");
                var apply = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var discard = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.DiscardPlacementDraftCommand));
                var heightValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(height)?.GetPattern(PatternInterface.Value));
                var elevationValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(elevation)?.GetPattern(PatternInterface.Value));
                var applyInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(apply)?.GetPattern(PatternInterface.Invoke));
                var discardInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(discard)?.GetPattern(PatternInterface.Invoke));
                var initiallyDisabled = !apply.IsEnabled && !apply.Command!.CanExecute(apply.CommandParameter)
                    && !discard.IsEnabled && !discard.Command!.CanExecute(discard.CommandParameter);

                heightValue.SetValue("NaN");
                PumpBindings();
                var invalidDraftCommandsEnabled = apply.IsEnabled && apply.Command!.CanExecute(apply.CommandParameter)
                    && discard.IsEnabled && discard.Command!.CanExecute(discard.CommandParameter);
                applyInvoke.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var invalid = (editor.DraftVerticalHeightText, error.Text, component.VerticalEnvelope, changes,
                    editor.HasPendingPlacementDraft);

                heightValue.SetValue("60");
                elevationValue.SetValue("-10000");
                PumpBindings();
                var validDraftCommandsEnabled = apply.IsEnabled && apply.Command!.CanExecute(apply.CommandParameter)
                    && discard.IsEnabled && discard.Command!.CanExecute(discard.CommandParameter);
                applyInvoke.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var appliedCommandsDisabled = !apply.IsEnabled && !apply.Command!.CanExecute(apply.CommandParameter)
                    && !discard.IsEnabled && !discard.Command!.CanExecute(discard.CommandParameter);
                heightValue.SetValue("61");
                PumpBindings();
                var reeditCommandsEnabled = apply.IsEnabled && apply.Command!.CanExecute(apply.CommandParameter)
                    && discard.IsEnabled && discard.Command!.CanExecute(discard.CommandParameter);
                discardInvoke.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var discardRestoredBaseline = height.Text == "60" && !apply.IsEnabled && !discard.IsEnabled
                    && !apply.Command!.CanExecute(apply.CommandParameter)
                    && !discard.Command!.CanExecute(discard.CommandParameter);
                return (invalid, editor.DraftVerticalHeightText, component.VerticalEnvelope, changes,
                    editor.HasPendingPlacementDraft, initiallyDisabled, invalidDraftCommandsEnabled,
                    validDraftCommandsEnabled, appliedCommandsDisabled, reeditCommandsEnabled,
                    discardRestoredBaseline);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("NaN", result.invalid.DraftVerticalHeightText);
        Assert.NotEmpty(result.invalid.Text);
        Assert.Null(result.invalid.VerticalEnvelope);
        Assert.Equal(0, result.invalid.changes);
        Assert.True(result.invalid.HasPendingPlacementDraft);
        Assert.Equal("60", result.DraftVerticalHeightText);
        Assert.Equal(-10000, result.VerticalEnvelope?.BaseElevation);
        Assert.Equal(60, result.VerticalEnvelope?.Height);
        Assert.Equal(1, result.changes);
        Assert.False(result.HasPendingPlacementDraft);
        Assert.True(result.initiallyDisabled);
        Assert.True(result.invalidDraftCommandsEnabled);
        Assert.True(result.validDraftCommandsEnabled);
        Assert.True(result.appliedCommandsDisabled);
        Assert.True(result.reeditCommandsEnabled);
        Assert.True(result.discardRestoredBaseline);
    }

    [Fact]
    public async Task VerticalEnvelopeDraftActionsAreReachableAndUsableByKeyboard()
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            using var layout = CreateEnvelopeLayout();
            layout.Select("frame-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            var component = layout.SelectedItem!.Component!;
            var changes = 0;
            layout.DefinitionChanged += (_, _) => changes++;
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
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
                var height = controls.OfType<TextBox>().Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "EquipmentDraftVerticalHeight");
                var apply = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var discard = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.DiscardPlacementDraftCommand));
                var scrollViewer = controls.OfType<ScrollViewer>().Single(candidate =>
                    candidate.Name == "DesignInspectorScrollViewer");
                var heightValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(height)?.GetPattern(PatternInterface.Value));

                scrollViewer.ScrollToEnd();
                heightValue.SetValue("61");
                PumpBindings();

                bool ReachButtonByTab(Button target)
                {
                    if (!ReferenceEquals(Keyboard.Focus(height), height)) return false;
                    var visited = new HashSet<UIElement>();
                    for (var step = 0; step < 24; step++)
                    {
                        if (Keyboard.FocusedElement is not UIElement focused || !visited.Add(focused)) return false;
                        if (ReferenceEquals(focused, target)) return true;
                        if (!focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) return false;
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    }

                    return false;
                }

                void PressSpace(Button button)
                {
                    var source = PresentationSource.FromVisual(button)
                        ?? throw new InvalidOperationException("The envelope action had no presentation source.");
                    button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    });
                    button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                    {
                        RoutedEvent = Keyboard.KeyUpEvent
                    });
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    PumpBindings();
                }

                var applyReached = ReachButtonByTab(apply);
                if (applyReached) PressSpace(apply);
                var appliedHeight = component.VerticalEnvelope?.Height;
                var appliedChanges = changes;
                heightValue.SetValue("62");
                PumpBindings();
                var discardReached = ReachButtonByTab(discard);
                if (discardReached) PressSpace(discard);

                return (applyReached, appliedHeight, appliedChanges, discardReached,
                    restoredDraft: editor.DraftVerticalHeightText, finalHeight: component.VerticalEnvelope?.Height,
                    finalChanges: changes, noPendingDraft: !editor.HasPendingPlacementDraft);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(result.applyReached);
        Assert.Equal(61, result.appliedHeight);
        Assert.Equal(1, result.appliedChanges);
        Assert.True(result.discardReached);
        Assert.Equal("61", result.restoredDraft);
        Assert.Equal(61, result.finalHeight);
        Assert.Equal(1, result.finalChanges);
        Assert.True(result.noPendingDraft);
    }

    [Theory]
    [InlineData(500d)]
    [InlineData(900d)]
    public async Task VerticalEnvelopeFieldAndDraftActionsRemainReachableAtScrollEnd(double windowHeight)
    {
        var layoutResult = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            using var layout = CreateEnvelopeLayout();
            layout.Select("frame-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            var window = new Window
            {
                Width = 520,
                Height = windowHeight,
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
                var scrollViewer = controls.OfType<ScrollViewer>().Single(candidate =>
                    candidate.Name == "DesignInspectorScrollViewer");
                var content = Assert.IsType<StackPanel>(scrollViewer.Content);
                var height = controls.OfType<TextBox>().Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "EquipmentDraftVerticalHeight");
                var apply = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var discard = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.DiscardPlacementDraftCommand));
                var footerGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(apply));
                var footerStack = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(footerGrid));
                var footer = Assert.IsType<Border>(VisualTreeHelper.GetParent(footerStack));

                scrollViewer.ScrollToEnd();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var fieldBounds = height.TransformToAncestor(scrollViewer).TransformBounds(
                    new Rect(0, 0, height.ActualWidth, height.ActualHeight));
                var contentBounds = content.TransformToAncestor(scrollViewer).TransformBounds(
                    new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                var scrollBounds = scrollViewer.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, scrollViewer.ActualWidth, scrollViewer.ActualHeight));
                var footerBounds = footer.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, footer.ActualWidth, footer.ActualHeight));
                var applyBounds = apply.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, apply.ActualWidth, apply.ActualHeight));
                var discardBounds = discard.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, discard.ActualWidth, discard.ActualHeight));

                return (
                    ViewportHeight: scrollViewer.ViewportHeight,
                    ScrollableHeight: scrollViewer.ScrollableHeight,
                    VerticalOffset: scrollViewer.VerticalOffset,
                    FieldBounds: fieldBounds,
                    ContentBounds: contentBounds,
                    ScrollBounds: scrollBounds,
                    FooterBounds: footerBounds,
                    ApplyBounds: applyBounds,
                    DiscardBounds: discardBounds,
                    ViewHeight: view.ActualHeight,
                    FooterVisible: footer.IsVisible,
                    ApplyVisible: apply.IsVisible,
                    DiscardVisible: discard.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(layoutResult.ViewportHeight > 0);
        Assert.True(layoutResult.ScrollableHeight > 0);
        Assert.Equal(layoutResult.ScrollableHeight, layoutResult.VerticalOffset, precision: 2);
        Assert.True(layoutResult.FieldBounds.Top >= -1 && layoutResult.FieldBounds.Bottom <= layoutResult.ViewportHeight + 1);
        Assert.InRange(Math.Abs(layoutResult.ContentBounds.Bottom - layoutResult.ViewportHeight), 0, 1);
        Assert.True(layoutResult.ScrollBounds.Bottom <= layoutResult.FooterBounds.Top + 1,
            $"ScrollViewer/footer overlap at {windowHeight}px: scroll={layoutResult.ScrollBounds}, footer={layoutResult.FooterBounds}, viewHeight={layoutResult.ViewHeight}.");
        Assert.True(layoutResult.FooterBounds.Bottom <= layoutResult.ViewHeight + 1);
        Assert.True(layoutResult.FooterBounds.Contains(layoutResult.ApplyBounds));
        Assert.True(layoutResult.FooterBounds.Contains(layoutResult.DiscardBounds));
        Assert.True(layoutResult.FooterVisible && layoutResult.ApplyVisible && layoutResult.DiscardVisible);
    }

    private static MachineProjectDocument CreateProject()
    {
        var station = new MachineStationDefinition { Id = "station-main", Name = "Main" };
        station.Units.Add(new MachineUnitDefinition { Id = "unit-press", Name = "Press" });
        station.Units.Add(new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" });
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-01", Name = "Inspection Camera", Kind = LayoutComponentKind.Camera,
            UnitId = "unit-press", Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [station],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        return project;
    }

    private static MachineLayoutViewModel CreateEnvelopeLayout()
    {
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "frame-01", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 80 }
        });
        var project = new MachineProjectDocument { Layouts = [definition] };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static void Arrange(FrameworkElement view)
    {
        view.Measure(new Size(520, 900));
        view.Arrange(new Rect(0, 0, 520, 900));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void PumpBindings()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private sealed record InspectorDataContext(
        MachineLayoutViewModel Layout,
        ShellNavigationViewModel Navigation,
        bool IsRunMode,
        bool IsEquipmentSinglePartCheckOpen = false,
        bool IsEquipmentDriveTabOpen = false);
}
