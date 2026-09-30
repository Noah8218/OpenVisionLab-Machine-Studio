using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SceneDocumentViewAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public SceneDocumentViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1674)]
    [InlineData(1034)]
    [InlineData(794)]
    public async Task EquipmentToolbarSeparatesAuthoringAndViewControlsWithoutClipping(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout(out _);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new SceneDocumentView { DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true) };
            var window = new Window
            {
                Width = width, Height = 760, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var authoring = Assert.IsType<WrapPanel>(view.FindName("EquipmentAuthoringToolbar"));
                var viewing = Assert.IsType<WrapPanel>(view.FindName("EquipmentViewToolbar"));
                var authoringBounds = authoring.TransformToAncestor(view).TransformBounds(new Rect(authoring.RenderSize));
                var viewingBounds = viewing.TransformToAncestor(view).TransformBounds(new Rect(viewing.RenderSize));
                Assert.True(authoring.IsVisible && viewing.IsVisible);
                Assert.True(authoringBounds.Right <= viewingBounds.Left || authoringBounds.Bottom <= viewingBounds.Top);
                var header = Assert.IsType<Grid>(viewing.Parent);
                var headerBounds = header.TransformToAncestor(view).TransformBounds(new Rect(header.RenderSize));
                Assert.Equal(headerBounds.Right, viewingBounds.Right, precision: 3);
                var viewport = Assert.IsType<MachineSceneViewport>(view.FindName("SceneViewport"));
                var viewportBounds = viewport.TransformToAncestor(view).TransformBounds(new Rect(viewport.RenderSize));
                var identity = Assert.IsType<Border>(view.FindName("SceneIdentityCard"));
                var identityBounds = identity.TransformToAncestor(view).TransformBounds(new Rect(identity.RenderSize));
                var headerBorder = Assert.IsType<Border>(header.Parent);
                Assert.Equal(headerBounds.Bottom + headerBorder.BorderThickness.Bottom, viewportBounds.Top, precision: 3);
                Assert.True(viewportBounds.Contains(identityBounds));
                Assert.False(identity.IsHitTestVisible);
                var visibleBounds = new Rect(view.RenderSize);
                foreach (var control in authoring.Children.Cast<FrameworkElement>().Concat(viewing.Children.Cast<FrameworkElement>()))
                {
                    Assert.True(control.IsVisible);
                    Assert.True(visibleBounds.Contains(control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize))));
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
    public async Task SceneHitAndMultiSelectionKeepPrimaryComponentInProjectTree()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            var first = new LayoutComponentDefinition
            {
                Id = "frame-01", Name = "Machine Frame 01", Kind = LayoutComponentKind.MachineFrame,
                Transform = new Transform2D { X = 35, Y = 24 },
                Size = new Size2D { Width = 36, Height = 20 }
            };
            var second = new LayoutComponentDefinition
            {
                Id = "frame-02", Name = "Machine Frame 02", Kind = LayoutComponentKind.MachineFrame,
                Transform = new Transform2D { X = 130, Y = 72 },
                Size = new Size2D { Width = 64, Height = 16 }
            };
            var definition = new MachineLayoutDefinition
            {
                Id = "layout-main", Name = "Main", Components = [first, second]
            };
            var project = new MachineProjectDocument
            {
                Name = "P09 scene-tree selection",
                Layouts = [definition]
            };
            project.Simulation.ActiveLayoutId = definition.Id;

            using var viewModel = new MainViewModel(project);
            var serializedBeforeSelection = new ProjectDocumentStore().Serialize(project);
            var firstNode = viewModel.ProjectTree.Roots.Single().Children
                .Single(node => node.Kind == TreeNodeKind.Layouts).Children.Single()
                .Children.Single(node => node.Id == first.Id);
            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(viewModel.Layout, viewModel.Navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var viewport = Assert.Single(Descendants(view).OfType<MachineSceneViewport>());
                var bounds = viewport.GetItemScreenBounds(first.Id);
                if (bounds is null)
                {
                    throw new InvalidOperationException("The SceneDocumentView did not project the primary component.");
                }

                var center = new Point(bounds.Value.Left + (bounds.Value.Width / 2), bounds.Value.Top + (bounds.Value.Height / 2));
                var hit = viewport.SelectItemAt(center);
                PumpBindings();
                var hitSynchronizesTree = hit && ReferenceEquals(firstNode, viewModel.ProjectTree.SelectedNode)
                    && ReferenceEquals(first, viewModel.ProjectTree.SelectedNode?.Model)
                    && viewModel.Layout.SelectionCount == 1;

                viewModel.Layout.SelectMany([first.Id, second.Id], first.Id);
                PumpBindings();
                var groupPreservesPrimaryInTree = viewModel.Layout.SelectionCount == 2
                    && viewModel.Layout.SelectedItem?.Id == first.Id
                    && ReferenceEquals(firstNode, viewModel.ProjectTree.SelectedNode)
                    && viewModel.Layout.SelectedItems.Select(item => item.Id).SequenceEqual([first.Id, second.Id]);

                return (hitSynchronizesTree, groupPreservesPrimaryInTree,
                    ProjectUnchanged: serializedBeforeSelection == new ProjectDocumentStore().Serialize(project),
                    viewModel.HasUnsavedChanges);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.hitSynchronizesTree);
        Assert.True(state.groupPreservesPrimaryInTree);
        Assert.True(state.ProjectUnchanged);
        Assert.False(state.HasUnsavedChanges);
    }

    [Fact]
    public async Task EquipmentViewSelectorsPreserveSelectionAndSessionPreferenceAcrossProjectReload()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout(out var project);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;

            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
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
                var oblique = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowObliqueViewCommand));
                var plan = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowTopViewCommand));
                var viewport = Assert.Single(controls.OfType<MachineSceneViewport>());
                var item = layout.Items.Single(candidate => candidate.Id == "camera-01");
                var bounds = viewport.GetItemScreenBounds(item.Id);
                if (bounds is null)
                {
                    throw new InvalidOperationException("The oblique SceneDocumentView did not project the camera component.");
                }

                var center = new Point(bounds.Value.Left + (bounds.Value.Width / 2), bounds.Value.Top + (bounds.Value.Height / 2));
                var hit = viewport.SelectItemAt(center);
                PumpBindings();
                var selectionBound = hit && ReferenceEquals(item, viewport.SelectedItem)
                    && ReferenceEquals(item, layout.SelectedItem)
                    && layout.SelectedComponentEditor is LayoutComponentEditorViewModel;
                var selectedItem = layout.SelectedItem;
                var selectedEditor = layout.SelectedComponentEditor;
                var initial = (oblique.IsChecked == true, plan.IsChecked == true, viewport.IsObliqueView);
                var obliqueBinding = oblique.GetBindingExpression(ToggleButton.IsCheckedProperty);
                var diagnostics = $"model={layout.IsObliqueView}; oblique={oblique.IsChecked}; " +
                    $"binding={obliqueBinding?.Status}; path={obliqueBinding?.ParentBinding.Path.Path}; " +
                    $"source={obliqueBinding?.DataItem?.GetType().FullName}; dataContext={oblique.DataContext?.GetType().FullName}; " +
                    $"viewport={viewport.IsObliqueView}";

                PressSpace(plan, window);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var selectionPreservedOnPlan = ReferenceEquals(selectedItem, layout.SelectedItem)
                    && ReferenceEquals(selectedEditor, layout.SelectedComponentEditor);
                var planState = (oblique.IsChecked == true, plan.IsChecked == true, viewport.IsObliqueView);
                var planEmptyHit = viewport.SelectItemAt(new Point(0, 0));
                PumpBindings();
                var planEmptySpaceClearedSelection = !planEmptyHit && viewport.SelectedItem is null
                    && layout.SelectedItem is null && layout.SelectionCount == 0
                    && layout.SelectedComponentEditor is null;
                var planBounds = viewport.GetItemScreenBounds(item.Id);
                var planHit = false;
                if (planBounds is { } boundsInPlan)
                {
                    var planCenter = new Point(boundsInPlan.Left + (boundsInPlan.Width / 2),
                        boundsInPlan.Top + (boundsInPlan.Height / 2));
                    planHit = viewport.SelectItemAt(planCenter);
                }
                PumpBindings();
                var planHitUpdatesSelection = planHit && ReferenceEquals(item, viewport.SelectedItem)
                    && ReferenceEquals(item, layout.SelectedItem)
                    && layout.SelectedComponentEditor is LayoutComponentEditorViewModel;
                var selectedInPlan = layout.SelectedItem;
                var editorInPlan = layout.SelectedComponentEditor;

                PressSpace(oblique, window);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var obliqueState = (oblique.IsChecked == true, plan.IsChecked == true, viewport.IsObliqueView);
                var selectionPreservedOnOblique = ReferenceEquals(selectedInPlan, layout.SelectedItem)
                    && ReferenceEquals(editorInPlan, layout.SelectedComponentEditor);
                var emptyHit = viewport.SelectItemAt(new Point(0, 0));
                PumpBindings();
                var emptySpaceClearedSelection = !emptyHit && viewport.SelectedItem is null
                    && layout.SelectedItem is null && layout.SelectionCount == 0
                    && layout.SelectedComponentEditor is null;

                var store = new ProjectDocumentStore();
                var reopenedProject = store.Load(store.Serialize(project));
                layout.Load(reopenedProject);
                PumpBindings();
                var sameSessionRetainedPreference = layout.IsObliqueView && oblique.IsChecked == true
                    && plan.IsChecked == false && viewport.IsObliqueView;

                using var reopenedLayout = new MachineLayoutViewModel();
                reopenedLayout.Load(reopenedProject);
                view.DataContext = new SceneDataContext(reopenedLayout, navigation, IsSceneEditable: true);
                PumpBindings();
                var newSessionUsesDefault = reopenedLayout.IsObliqueView && oblique.IsChecked == true
                    && plan.IsChecked == false && viewport.IsObliqueView;

                return (initial, planState, obliqueState,
                    SelectionPreservedOnPlan: selectionPreservedOnPlan,
                    PlanEmptySpaceClearedSelection: planEmptySpaceClearedSelection,
                    PlanHitUpdatesSelection: planHitUpdatesSelection,
                    SelectionPreservedOnOblique: selectionPreservedOnOblique,
                    SelectionBound: selectionBound,
                    EmptySpaceClearedSelection: emptySpaceClearedSelection,
                    SameSessionRetainedPreference: sameSessionRetainedPreference,
                    NewSessionUsesDefault: newSessionUsesDefault,
                    Diagnostics: diagnostics);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.initial == (true, false, true), state.Diagnostics);
        Assert.Equal((false, true, false), state.planState);
        Assert.Equal((true, false, true), state.obliqueState);
        Assert.True(state.SelectionPreservedOnPlan);
        Assert.True(state.PlanEmptySpaceClearedSelection);
        Assert.True(state.PlanHitUpdatesSelection);
        Assert.True(state.SelectionPreservedOnOblique);
        Assert.True(state.SelectionBound);
        Assert.True(state.EmptySpaceClearedSelection);
        Assert.True(state.SameSessionRetainedPreference);
        Assert.True(state.NewSessionUsesDefault);
    }

    [Fact]
    public async Task ProjectionRadioButtonsRespondToKeyboardSpaceAndPreserveSelection()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout(out _);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            layout.Select("camera-01");
            var selected = layout.SelectedItem;
            var selectedEditor = layout.SelectedComponentEditor;
            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
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
                var oblique = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowObliqueViewCommand));
                var plan = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowTopViewCommand));
                var viewport = Assert.Single(controls.OfType<MachineSceneViewport>());

                PressSpace(plan, window);
                var planMode = !layout.IsObliqueView && plan.IsChecked == true
                    && oblique.IsChecked == false && !viewport.IsObliqueView;
                var selectionPreservedOnPlan = ReferenceEquals(selected, layout.SelectedItem)
                    && ReferenceEquals(selectedEditor, layout.SelectedComponentEditor);

                PressSpace(oblique, window);
                var obliqueMode = layout.IsObliqueView && oblique.IsChecked == true
                    && plan.IsChecked == false && viewport.IsObliqueView;
                var selectionPreservedOnOblique = ReferenceEquals(selected, layout.SelectedItem)
                    && ReferenceEquals(selectedEditor, layout.SelectedComponentEditor);
                return (planMode, obliqueMode, selectionPreservedOnPlan, selectionPreservedOnOblique);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.planMode);
        Assert.True(state.obliqueMode);
        Assert.True(state.selectionPreservedOnPlan);
        Assert.True(state.selectionPreservedOnOblique);
    }

    [Fact]
    public async Task PlanSelectionUpdatesInspectorAndAppliesBoundDraftToSelectedComponent()
    {
        var state = await _ui.InvokeTaskAsync(async () =>
        {
            using var seedLayout = CreateLayout(out var initialProject);
            initialProject.Name = "P14 Inspector edit";
            initialProject.Devices.Add(new DeviceDefinition
            {
                Id = "camera-01",
                Name = "Inspection Camera",
                Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition()
            });
            initialProject.Layouts.Single().Components.Single().BehaviorBindingId = "camera-01";
            using var viewModel = new MainViewModel(initialProject);
            var project = initialProject;
            var layout = viewModel.Layout;
            viewModel.Navigation.SelectedDocumentTabIndex = 0;

            var scene = new SceneDocumentView
            {
                DataContext = viewModel
            };
            var inspector = new RightToolRegionView
            {
                DataContext = viewModel
            };
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            root.Children.Add(scene);
            Grid.SetColumn(inspector, 1);
            root.Children.Add(inspector);

            var window = new Window
            {
                Width = 1280,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = root
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var viewport = Assert.Single(Descendants(scene).OfType<MachineSceneViewport>());
                layout.ShowTopViewCommand.Execute(null);
                PumpBindings();
                var item = layout.Items.Single(candidate => candidate.Id == "camera-01");
                var bounds = viewport.GetItemScreenBounds(item.Id)
                    ?? throw new InvalidOperationException("The plan view did not project camera-01.");
                var center = new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
                var selectedInPlan = viewport.SelectItemAt(center);
                PumpBindings();

                var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
                var x = Descendants(inspector).OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "DraftXText");
                var apply = Descendants(inspector).OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var inspectorSelectionBound = ReferenceEquals(layout.SelectedItem, item)
                    && x.DataContext is LayoutComponentEditorViewModel boundEditor
                    && ReferenceEquals(boundEditor, editor)
                    && x.Text == editor.DraftXText;
                var originalX = item.Component!.Transform.X;
                var boundsBeforeApply = viewport.GetItemScreenBounds(item.Id);

                x.Text = "99";
                PumpBindings();
                var editingDraftRemainsClean = !viewModel.HasUnsavedChanges;
                var validDraftEnabled = editor.HasPendingPlacementDraft && apply.IsEnabled
                    && apply.Command!.CanExecute(apply.CommandParameter);
                var applyPeer = new ButtonAutomationPeer(apply);
                var applyProvider = Assert.IsAssignableFrom<IInvokeProvider>(applyPeer.GetPattern(PatternInterface.Invoke));
                applyProvider.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var boundsAfterApply = viewport.GetItemScreenBounds(item.Id);
                var appliedDraftDisabled = !editor.HasPendingPlacementDraft && !apply.IsEnabled
                    && !apply.Command!.CanExecute(apply.CommandParameter);
                var appliedValueMarksProjectDirty = viewModel.HasUnsavedChanges
                    && viewModel.Title.EndsWith("*", StringComparison.Ordinal);

                var roundTripRoot = Path.Combine(TestStorage.RootPath, "r19-priority-14-plan-inspector", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(roundTripRoot);
                var projectPath = Path.Combine(roundTripRoot, "plan-inspector-edit.ovmachine");
                var fileStore = new ProjectDocumentFileStore();
                await viewModel.SaveProjectAsync(projectPath);
                var saveClearsDirtyMarker = !viewModel.HasUnsavedChanges
                    && !viewModel.Title.EndsWith("*", StringComparison.Ordinal);
                var reopenedProject = fileStore.Load(projectPath);
                using var reopenedViewModel = new MainViewModel(reopenedProject, projectPath);
                reopenedViewModel.Layout.Select("camera-01");
                var reopenedEditor = Assert.IsType<LayoutComponentEditorViewModel>(
                    reopenedViewModel.Layout.SelectedComponentEditor);

                return (
                    SelectedInPlan: selectedInPlan,
                    InspectorSelectionBound: inspectorSelectionBound,
                    OriginalX: originalX,
                    DraftX: editor.DraftXText,
                    UpdatedComponentX: item.Component.Transform.X,
                    SelectedItemRetained: ReferenceEquals(layout.SelectedItem, item),
                    ValidDraftEnabled: validDraftEnabled,
                    EditingDraftRemainsClean: editingDraftRemainsClean,
                    AppliedDraftDisabled: appliedDraftDisabled,
                    AppliedValueMarksProjectDirty: appliedValueMarksProjectDirty,
                    SaveClearsDirtyMarker: saveClearsDirtyMarker,
                    SelectionBoundsChanged: boundsBeforeApply != boundsAfterApply,
                    SavedProjectPath: projectPath,
                    PersistedX: reopenedViewModel.Layout.SelectedItem?.Component?.Transform.X,
                    ReopenedInspectorDraftX: reopenedEditor.DraftXText,
                    ReopenedProjectIsClean: !reopenedViewModel.HasUnsavedChanges);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.SelectedInPlan);
        Assert.True(state.InspectorSelectionBound);
        Assert.Equal(35d, state.OriginalX);
        Assert.Equal("99", state.DraftX);
        Assert.Equal(99d, state.UpdatedComponentX);
        Assert.True(state.SelectedItemRetained);
        Assert.True(state.ValidDraftEnabled);
        Assert.True(state.EditingDraftRemainsClean);
        Assert.True(state.AppliedDraftDisabled);
        Assert.True(state.AppliedValueMarksProjectDirty);
        Assert.True(state.SaveClearsDirtyMarker);
        Assert.True(state.SelectionBoundsChanged);
        Assert.True(File.Exists(state.SavedProjectPath));
        Assert.Equal(99d, state.PersistedX);
        Assert.Equal("99", state.ReopenedInspectorDraftX);
        Assert.True(state.ReopenedProjectIsClean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedSceneArrowMovesUseExactLayoutUnitsAndPreserveEditingGuards(bool oblique)
    {
        await _ui.InvokeAsync(() =>
        {
            using var seed = CreateLayout(out var project);
            project.Layouts.Single().GridSize = 25;
            project.Layouts.Single().SnapToGrid = true;
            project.Devices.Add(new DeviceDefinition
            {
                Id = "camera-01", Name = "Camera", Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition()
            });
            project.Layouts.Single().Components.Single().BehaviorBindingId = "camera-01";
            using var viewModel = new MainViewModel(project);
            viewModel.Navigation.SelectedDocumentTabIndex = 0;
            if (oblique) viewModel.Layout.ShowObliqueViewCommand.Execute(null);
            else viewModel.Layout.ShowTopViewCommand.Execute(null);
            var scene = new SceneDocumentView { DataContext = viewModel };
            var input = new TextBox { Text = "keep draft" };
            var root = new DockPanel();
            DockPanel.SetDock(input, Dock.Top);
            root.Children.Add(input);
            root.Children.Add(scene);
            var window = new Window { Content = root, Width = 1280, Height = 760, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                window.UpdateLayout();
                viewModel.Layout.Select("camera-01");
                PumpBindings();
                var viewport = Assert.IsType<MachineSceneViewport>(scene.FindName("SceneViewport"));
                var item = viewModel.Layout.SelectedItem!;
                Assert.Same(viewport, Keyboard.Focus(viewport));
                var source = PresentationSource.FromVisual(viewport)!;
                var right = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };
                viewport.RaiseEvent(right);
                PumpBindings();
                Assert.True(right.Handled);
                Assert.Equal(36, item.CurrentX); // Initial 35 + 1, despite the 25-unit grid.
                Assert.Equal(24, item.CurrentY);
                Assert.Equal(36, item.Component!.Transform.X);
                Assert.Equal("36", viewModel.Layout.SelectedComponentEditor!.DraftXText);
                Assert.False(viewModel.Layout.SelectedComponentEditor.HasPendingPlacementDraft);
                Assert.True(viewModel.HasUnsavedChanges);
                Assert.True(viewport.RequestKeyboardMove(Key.Up, ModifierKeys.Shift));
                Assert.Equal(14, item.CurrentY);
                Assert.True(viewport.RequestKeyboardMove(Key.Left, ModifierKeys.Shift));
                Assert.Equal(26, item.CurrentX);
                Assert.True(viewport.RequestKeyboardMove(Key.Down, ModifierKeys.None));
                Assert.Equal(15, item.CurrentY);
                Assert.False(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.Control));
                Assert.False(viewport.RequestKeyboardMove(Key.Down, ModifierKeys.Alt));
                Assert.False(viewport.RequestKeyboardMove(Key.Space, ModifierKeys.None));
                Assert.Equal((26d, 15d), (item.CurrentX, item.CurrentY));
                for (var index = 0; index < 4; index++) viewModel.UndoLayoutEditCommand.Execute(null);
                PumpBindings();
                item = viewModel.Layout.SelectedItem!; // Undo restores the layout items by identity.
                Assert.Equal((35d, 24d), (item.CurrentX, item.CurrentY));
                Assert.Equal("35", viewModel.Layout.SelectedComponentEditor!.DraftXText);

                Assert.Same(input, Keyboard.Focus(input));
                Assert.False(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None));
                Assert.Equal("keep draft", input.Text);
                Assert.Equal(35, item.CurrentX);
                Assert.Same(viewport, Keyboard.Focus(viewport));
                viewport.IsDesignMode = false;
                Assert.False(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None));
                Assert.Equal(35, item.CurrentX);
                viewport.IsDesignMode = true;
                viewModel.Layout.IsEditable = false;
                viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None);
                Assert.Equal(35, item.CurrentX);
                viewModel.Layout.IsEditable = true;
                Assert.True(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None));
                Assert.Equal(36, item.CurrentX);
                var editor = viewModel.Layout.SelectedComponentEditor!;
                editor.DraftXText = "77";
                viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None);
                Assert.Equal(36, item.CurrentX);
                Assert.Equal("77", editor.DraftXText);
                editor.DiscardPlacementDraftCommand.Execute(null);
                viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None);
                Assert.Equal(37, item.CurrentX);
                Assert.Equal("37", editor.DraftXText);
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task StationRemovalControlBindsGuardAndCommandForAnEmptyStation(OpenVisionLanguage language)
    {
        var state = await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection" };
                var station = new MachineStationDefinition
                {
                    Id = "station-main",
                    Name = "Main station",
                    Units = [unit]
                };
                var project = new MachineProjectDocument { Name = "Station deletion UI", Stations = [station] };
                using var viewModel = new MainViewModel(project);
                viewModel.OpenStationUnitEditorCommand.Execute(null);
                var scene = new SceneDocumentView { DataContext = viewModel };
                scene.Measure(new System.Windows.Size(900, 700));
                scene.Arrange(new System.Windows.Rect(0, 0, 900, 700));
                scene.UpdateLayout();
                scene.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(scene.UpdateLayout));
                var controls = Descendants(scene).OfType<Button>().ToArray();
                var removeButton = Assert.Single(controls.Where(button =>
                    ReferenceEquals(button.Command, viewModel.RemoveSelectedStationCommand)));
                var hint = Descendants(scene).OfType<TextBlock>().Single(textBlock =>
                    textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path
                    == "StationUnitRemovalHintText");
                var childState = (removeButton.IsEnabled, hint.Visibility,
                    System.Windows.Automation.AutomationProperties.GetName(removeButton));
                var serialized = new ProjectDocumentStore().Serialize(project);
                var stationPromptCount = 0;
                viewModel.EquipmentStationRemovalPrompt = _ => { stationPromptCount++; return true; };
                removeButton.Command.Execute(null);
                Assert.Equal(serialized, new ProjectDocumentStore().Serialize(project));
                Assert.Equal(0, stationPromptCount);
                Assert.False(viewModel.HasUnsavedChanges);
                Assert.Equal(string.Format(OpenVisionLanguageService.T("Equipment.StationRemovalRequiresEmpty"), 1), hint.Text);

                viewModel.CancelStationUnitEditorCommand.Execute(null);
                viewModel.EquipmentUnitRemovalPrompt = (_, _) => true;
                viewModel.RemoveEquipmentUnitCommand.Execute(unit.Id);
                viewModel.OpenStationUnitEditorCommand.Execute(null);
                scene.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(scene.UpdateLayout));
                var enabledButton = Descendants(scene).OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, viewModel.RemoveSelectedStationCommand));
                viewModel.EquipmentStationRemovalPrompt = _ => true;
                var commandCanExecute = enabledButton.Command?.CanExecute(null) == true;
                var emptyEnabled = enabledButton.IsEnabled;
                enabledButton.Command?.Execute(null);
                scene.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(scene.UpdateLayout));

                return (ChildEnabled: childState.IsEnabled,
                    HintVisibility: childState.Visibility,
                    AutomationName: childState.Item3,
                    ExpectedAutomationName: OpenVisionLanguageService.T("Equipment.StationRemove"),
                    EmptyEnabled: emptyEnabled,
                    EmptyCanExecute: commandCanExecute,
                    EditorOpen: viewModel.IsStationUnitEditorOpen,
                    IsCreatingNewStation: viewModel.IsCreatingNewStation,
                    SelectedStationId: viewModel.SelectedStationId,
                    HasSelectedStationChildren: viewModel.HasSelectedStationChildren,
                    IsSceneEditable: viewModel.IsSceneEditable,
                    IsEquipmentWorkspace: viewModel.Navigation.IsEquipmentWorkspace,
                    RemainingStations: project.Stations.Count,
                    HasUnsavedChanges: viewModel.HasUnsavedChanges);
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });

        Assert.False(state.ChildEnabled);
        Assert.Equal(Visibility.Visible, state.HintVisibility);
        Assert.Equal(state.ExpectedAutomationName, state.AutomationName);
        Assert.True(state.EmptyCanExecute,
            $"EditorOpen={state.EditorOpen}; NewStation={state.IsCreatingNewStation}; SelectedStation={state.SelectedStationId}; HasChildren={state.HasSelectedStationChildren}; Editable={state.IsSceneEditable}; Equipment={state.IsEquipmentWorkspace}");
        Assert.True(state.EmptyEnabled);
        Assert.Equal(0, state.RemainingStations);
        Assert.True(state.IsCreatingNewStation);
        Assert.True(state.HasUnsavedChanges);
    }

    [Fact]
    public async Task StationUnitEditorKeepsKeyboardFocusInsideAndReturnsToLauncherOnCancel()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new MachineProjectDocument { Name = "Station focus" };
            using var viewModel = new MainViewModel(project);
            var view = new SceneDocumentView { DataContext = viewModel };
            var window = new Window
            {
                Width = 900, Height = 700, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var launcher = Descendants(view).OfType<Button>().First(button =>
                    button.IsVisible && ReferenceEquals(button.Command, viewModel.OpenStationUnitEditorCommand));
                var before = new ProjectDocumentStore().Serialize(project);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    launcher.Focus();
                    ((IInvokeProvider)new ButtonAutomationPeer(launcher).GetPattern(PatternInterface.Invoke)).Invoke();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var name = Assert.IsType<TextBox>(view.FindName("StationUnitNameTextBox"));
                    var overlay = Assert.IsType<Grid>(view.FindName("StationUnitEditorOverlay"));
                    Assert.Same(name, Keyboard.FocusedElement);
                    for (var tab = 0; tab < 12; tab++)
                    {
                        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        Assert.Contains((DependencyObject)Keyboard.FocusedElement, Descendants(overlay));
                    }
                    var escape = Assert.Single(overlay.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.Escape);
                    Assert.Same(viewModel.CancelStationUnitEditorCommand, escape.Command);
                    escape.Command.Execute(null);
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.False(viewModel.IsStationUnitEditorOpen);
                    Assert.Same(launcher, Keyboard.FocusedElement);
                }
                Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
                Assert.False(viewModel.HasUnsavedChanges);
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task UnitSceneKeepsItsComponentsAcrossPlanAndObliqueViewsAndOverviewRestoresAllUnits()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateUnitLayout();
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var viewport = Assert.Single(Descendants(view).OfType<MachineSceneViewport>());
                layout.ShowUnit("unit-inspection");
                PumpBindings();
                var obliqueIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var obliqueBounds = viewport.GetItemScreenBounds("camera-inspection");
                var obliqueModeBound = layout.ActiveUnitId == "unit-inspection"
                    && viewport.IsObliqueView && layout.IsObliqueView;

                layout.ShowTopViewCommand.Execute(null);
                PumpBindings();
                var planIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var planBounds = viewport.GetItemScreenBounds("camera-inspection");
                var planModeBound = layout.ActiveUnitId == "unit-inspection"
                    && !viewport.IsObliqueView && !layout.IsObliqueView;

                layout.ShowObliqueViewCommand.Execute(null);
                PumpBindings();
                var returnedObliqueIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var returnedObliqueModeBound = layout.ActiveUnitId == "unit-inspection"
                    && viewport.IsObliqueView && layout.IsObliqueView;

                layout.ShowOverview();
                PumpBindings();
                var overviewIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var overviewRestored = layout.ActiveUnitId is null && !layout.IsUnitView
                    && overviewIds.Length == 3;

                return (obliqueIds, planIds, returnedObliqueIds, obliqueBounds, planBounds,
                    obliqueModeBound, planModeBound, returnedObliqueModeBound, overviewIds, overviewRestored);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(new[] { "camera-inspection", "conveyor-inspection" }, state.obliqueIds);
        Assert.Equal(state.obliqueIds, state.planIds);
        Assert.Equal(state.obliqueIds, state.returnedObliqueIds);
        Assert.NotNull(state.obliqueBounds);
        Assert.NotNull(state.planBounds);
        Assert.NotEqual(state.obliqueBounds, state.planBounds);
        Assert.True(state.obliqueModeBound);
        Assert.True(state.planModeBound);
        Assert.True(state.returnedObliqueModeBound);
        Assert.Equal(new[] { "camera-inspection", "camera-loading", "conveyor-inspection" }, state.overviewIds);
        Assert.True(state.overviewRestored);
    }

    private static MachineLayoutViewModel CreateLayout(out MachineProjectDocument project)
    {
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-01", Name = "Inspection Camera", Kind = LayoutComponentKind.Camera,
            Transform = new Transform2D { X = 35, Y = 24, RotationDegrees = 27 },
            Size = new Size2D { Width = 36, Height = 20 }
        });
        project = new MachineProjectDocument { Layouts = [definition] };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static MachineLayoutViewModel CreateUnitLayout()
    {
        var inspection = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection" };
        var loading = new MachineUnitDefinition { Id = "unit-loading", Name = "Loading" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-inspection", Name = "Inspection Camera", Kind = LayoutComponentKind.Camera,
            UnitId = inspection.Id, Transform = new Transform2D { X = 35, Y = 24 },
            Size = new Size2D { Width = 36, Height = 20 }
        });
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "conveyor-inspection", Name = "Inspection Conveyor", Kind = LayoutComponentKind.Conveyor,
            UnitId = inspection.Id, Transform = new Transform2D { X = 82, Y = 40 },
            Size = new Size2D { Width = 64, Height = 16 }
        });
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-loading", Name = "Loading Camera", Kind = LayoutComponentKind.Camera,
            UnitId = loading.Id, Transform = new Transform2D { X = 142, Y = 22 },
            Size = new Size2D { Width = 36, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [new MachineStationDefinition { Id = "station-main", Name = "Main", Units = [inspection, loading] }],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static void PressSpace(RadioButton button, Window window)
    {
        Assert.Same(button, Keyboard.Focus(button));
        var source = PresentationSource.FromVisual(button)
            ?? throw new InvalidOperationException("The projection radio button had no presentation source.");
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = Keyboard.KeyUpEvent
        });
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        PumpBindings();
    }

    private static void PumpBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed record SceneDataContext(
        MachineLayoutViewModel Layout,
        ShellNavigationViewModel Navigation,
        bool IsSceneEditable);

    private sealed record InspectorSceneDataContext(
        MachineLayoutViewModel Layout,
        ShellNavigationViewModel Navigation,
        bool IsRunMode);
}
