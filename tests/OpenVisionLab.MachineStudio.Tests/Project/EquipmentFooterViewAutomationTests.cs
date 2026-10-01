using System.Windows;
using System.IO;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class EquipmentFooterViewAutomationTests
{
    private readonly StudioUiTestHost _ui;
    public EquipmentFooterViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1920, 1040)]
    [InlineData(1280, 760)]
    [InlineData(1000, 760)]
    [InlineData(1000, 720)]
    [InlineData(1000, 1032)]
    public async Task InspectionActionsStayVisibleWhileInputAndResultFieldsScroll(double width, double height)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            using var model = new MainViewModel(project);
            model.Navigation.IsInspectionWorkspace = true;
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                var mmi = Assert.Single(Descendants(window).OfType<MmiOperatorLayoutView>());
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.False(Assert.Single(Descendants(window).OfType<RightToolRegionView>()).IsVisible);
                Assert.DoesNotContain(Descendants(window).OfType<Button>(), button => button.Name == "WorkspaceInspectorToggleButton");
                model.Navigation.IsInspectorOpen = false;
                var preview = Assert.IsType<ScrollViewer>(mmi.FindName("MmiInspectionPreviewScrollViewer"));
                Assert.True(preview.IsVisible);
                if (height == 760) Assert.True(preview.ScrollableHeight > 0);
                var pill = Assert.IsType<Border>(mmi.FindName("MmiInspectionStatusPill"));
                Assert.InRange(pill.ActualHeight, 16, 34);
                foreach (var name in new[] { "MmiPublishButton", "MmiRefreshButton", "MmiApplyResultButton" })
                {
                    var button = Assert.IsType<Button>(mmi.FindName(name));
                    Assert.True(new Rect(window.RenderSize).Contains(Bounds(button, window)));
                }
                var cameraSource = Assert.IsType<CameraImageSourceView>(mmi.FindName("MmiCameraSourceSettings"));
                if (model.Navigation.IsNarrowLayout)
                {
                    var setup = Assert.IsType<ScrollViewer>(mmi.FindName("MmiCaptureSetupScrollViewer"));
                    cameraSource.ApplyCameraSourceButton.BringIntoView();
                    Pump(window);
                    Assert.True(new Rect(0, 0, setup.ViewportWidth, setup.ViewportHeight).Contains(Bounds(cameraSource.ApplyCameraSourceButton, setup)),
                        $"Apply must fit setup viewport: button={Bounds(cameraSource.ApplyCameraSourceButton, setup)}, viewport={setup.ViewportWidth}x{setup.ViewportHeight}, offset={setup.VerticalOffset}, scrollable={setup.ScrollableHeight}.");
                    Assert.True(new Rect(window.RenderSize).Contains(Bounds(cameraSource.ApplyCameraSourceButton, window)));
                    cameraSource.BrowseCameraSourceButton.BringIntoView();
                    Pump(window);
                    Assert.True(new Rect(0, 0, setup.ViewportWidth, setup.ViewportHeight).Contains(Bounds(cameraSource.BrowseCameraSourceButton, setup)),
                        $"Browse must fit setup viewport: button={Bounds(cameraSource.BrowseCameraSourceButton, setup)}, viewport={setup.ViewportWidth}x{setup.ViewportHeight}, offset={setup.VerticalOffset}.");
                }
                else Assert.True(new Rect(window.RenderSize).Contains(Bounds(cameraSource.ApplyCameraSourceButton, window)));
                var before = Bounds(Assert.IsType<Button>(mmi.FindName("MmiApplyResultButton")), window);
                preview.ScrollToBottom();
                Pump(window);
                Assert.Equal(preview.ScrollableHeight, preview.VerticalOffset, precision: 1);
                Assert.Equal(before, Bounds(Assert.IsType<Button>(mmi.FindName("MmiApplyResultButton")), window));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SessionRecordsReuseTheJournalAndNavigateAnExactErrorWithDraftCancelProtection()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var sequence = project.Sequences[0];
            var step = sequence.Steps.First(item => item.Action == SequenceStepAction.TriggerCamera);
            var part = project.Layouts.SelectMany(layout => layout.Components).First(item => item.BehaviorBindingId == step.TargetId);
            using var model = new MainViewModel(project);
            model.Navigation.IsResultsWorkspace = true;
            model.Layout.Select("frame-1");
            var window = new ShellWindow { DataContext = model, Width = 1280, Height = 760, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                var journal = Assert.Single(Descendants(window).OfType<EventJournalView>().Where(view => view.IsInline));
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.False(Assert.Single(Descendants(window).OfType<RightToolRegionView>()).IsVisible);
                model.Navigation.IsInspectorOpen = false;
                Assert.True(journal.IsVisible && journal.EvidenceTabs.IsVisible);
                Assert.False(journal.EvidenceDrawerToggle.IsVisible);
                var error = new SequenceExecutionError(SequenceExecutionErrorCode.CameraTriggerFailed, sequence.Id, step.Id, "Camera trigger failed");
                model.RuntimeDebugger.ApplyEvent(new SimulationEvent(1, 5, TimeSpan.Zero, "Sequence", "SequenceFaulted", "Timed out"));
                model.RuntimeDebugger.ApplySnapshot(new SimulationSnapshot(TimeSpan.Zero, 5, SimulationRunMode.Paused, SimulationControlOwner.EmbeddedSequence, 1,
                    [], 0, [], [new SequenceExecutionSnapshot(sequence.Id, SequenceExecutionStatus.Faulted, step.Id, 0, TimeSpan.Zero, TimeSpan.Zero, 5, error, TimeSpan.FromSeconds(10))]));
                Pump(window);
                var button = Assert.IsType<Button>(journal.FindName("NavigateRuntimeErrorButton"));
                Assert.Same(model.NavigateRuntimeErrorCommand, button.Command);
                Assert.True(button.IsEnabled);
                var draft = model.Layout.SelectedComponentEditor!;
                draft.DraftXText = "77";
                model.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                model.NavigateRuntimeErrorCommand.Execute(button.CommandParameter);
                Assert.True(model.Navigation.IsResultsWorkspace);
                Assert.Equal("77", draft.DraftXText);
                model.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
                model.NavigateRuntimeErrorCommand.Execute(button.CommandParameter);
                Pump(window);
                Assert.True(model.Navigation.IsSimulationWorkspace);
                Assert.Equal(1, model.Navigation.SelectedExecutionTabIndex);
                Assert.Equal(sequence.Id, model.SequenceEditor.SelectedSequence?.Id);
                Assert.Equal(step.Id, model.SequenceEditor.SelectedStep?.Id);
                Assert.Equal(part.Id, model.Layout.SelectedItem?.Id);
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                var obsolete = model.RuntimeDebugger.LastSequenceError! with { ProjectId = "other-project" };
                Assert.False(model.NavigateRuntimeErrorCommand.CanExecute(obsolete));
                var sequenceOnly = model.RuntimeDebugger.LastSequenceError! with
                {
                    Error = new SequenceExecutionError(SequenceExecutionErrorCode.CameraTriggerFailed, sequence.Id, null, "Sequence-level error"),
                    TargetId = null
                };
                model.NavigateRuntimeErrorCommand.Execute(sequenceOnly);
                Assert.Equal(sequence.Id, model.SequenceEditor.SelectedSequence?.Id);
                Assert.Null(model.SequenceEditor.SelectedStep);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1000, false, true)]
    [InlineData(1280, 760, false, true)]
    [InlineData(1280, 760, true, true)]
    [InlineData(1280, 760, false, false)]
    public async Task UnitSlotsAndActionsRemainOutsideInspectorAndPreserveSelectionDraftGuards(double width, double height, bool longName, bool hasWorkpieces)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            if (!hasWorkpieces) project.Layouts[0].Components.RemoveAll(component => component.Kind == OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.Workpiece);
            if (longName) project.Stations[0].Units[0].Name = "Long feed unit for inspection / 긴 투입 유닛과 전체 이름";
            using var model = new MainViewModel(project);
            model.Navigation.IsEquipmentWorkspace = true;
            model.Navigation.IsInspectorOpen = true;
            model.Layout.Select("inspection");
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var footer = Assert.IsType<Grid>(scene.FindName("EquipmentFooterRegion"));
                var slots = Assert.IsType<Border>(scene.FindName("EquipmentMaterialSlots"));
                var actions = Assert.IsType<Border>(scene.FindName("EquipmentSelectionActions"));
                var inspector = Assert.Single(Descendants(window).OfType<RightToolRegionView>());
                Assert.Equal(hasWorkpieces, slots.IsVisible);
                Assert.True(actions.IsVisible && inspector.IsVisible);
                var footerBounds = Bounds(footer, window);
                var inspectorBounds = Bounds(inspector, window);
                Assert.True(inspectorBounds.Bottom <= footerBounds.Top, $"Inspector={inspectorBounds}; footer={footerBounds}");
                Assert.Equal(5, Descendants(actions).OfType<Button>().Count());
                var primary = Descendants(actions).OfType<Button>().Single(button => ReferenceEquals(button.Command, model.OpenEquipmentPropertiesCommand));
                var primaryCaption = Assert.Single(Descendants(primary).OfType<TextBlock>());
                Assert.Equal(Assert.IsType<SolidColorBrush>(primary.Foreground).Color, Assert.IsType<SolidColorBrush>(primaryCaption.Foreground).Color);
                if (!hasWorkpieces)
                {
                    Assert.False(model.EquipmentMaterialFlow.HasSlots);
                    Assert.Empty(model.EquipmentMaterialFlow.Slots);
                    return true;
                }
                var cards = Descendants(slots).OfType<Button>().ToArray();
                Assert.Equal(3, cards.Length);
                if (longName)
                {
                    var scroll = Assert.Single(Descendants(slots).OfType<ScrollViewer>());
                    Assert.True(scroll.ScrollableWidth > 0);
                    scroll.ScrollToRightEnd();
                    Pump(window);
                    Assert.True(Bounds(cards[^1], window).Right <= Bounds(slots, window).Right);
                    scroll.ScrollToLeftEnd();
                    Pump(window);
                    Assert.True(Bounds(cards[0], window).Left >= Bounds(slots, window).Left);
                }
                else Assert.All(cards, card => Assert.True(Bounds(slots, window).Contains(Bounds(card, window))));
                var peer = new ButtonAutomationPeer(cards[0]);
                var editor = model.Layout.SelectedComponentEditor!;
                editor.DraftXText = "77";
                model.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                Pump(window);
                Assert.Equal("inspection", model.Layout.SelectedItem?.Id);
                Assert.Equal("77", editor.DraftXText);
                Assert.True(model.Navigation.IsInspectorOpen);
                model.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                Pump(window);
                Assert.Equal("unit-feed", model.Layout.ActiveUnitId);
                Assert.Null(model.Layout.SelectedItem);
                Assert.False(model.Navigation.IsInspectorOpen);
                Assert.False(editor.HasPendingInspectorDraft);
                Assert.False(model.HasUnsavedChanges);
                Assert.Equal(TimeSpan.Zero, model.SceneSnapshots.Latest!.SimulationTime);
                var slotName = model.EquipmentMaterialFlow.Slots[0].Name;
                model.Layout.Select("feed");
                Pump(window);
                Assert.Equal(project.Layouts[0].Components[0].Name, model.Layout.EquipmentSelectionNameText);
                Assert.Equal("feed", model.Layout.EquipmentSelectionIdText);
                Assert.False(model.HasUnsavedChanges);
                Assert.Equal(TimeSpan.Zero, model.SceneSnapshots.Latest!.SimulationTime);
                model.Layout.SelectedItem!.CurrentName = "Renamed workpiece position";
                Pump(window);
                Assert.Equal("Renamed workpiece position", model.Layout.EquipmentSelectionNameText);
                Assert.Contains(Descendants(actions).OfType<TextBlock>(), text => text.Text == "Renamed workpiece position");
                Assert.Equal(slotName, model.EquipmentMaterialFlow.Slots[0].Name);
                var revisionText = Descendants(window).OfType<TextBlock>().Single(text =>
                    text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.EquipmentFooterStatusText));
                var firstRevision = revisionText.Text;
                model.Layout.SelectedItem.CurrentName = "Renamed again while already dirty";
                Pump(window);
                Assert.NotEqual(firstRevision, revisionText.Text);
                Assert.Equal(model.EquipmentFooterStatusText, revisionText.Text);
                Assert.True(model.DuplicateLayoutSelectionCommand.CanExecute(null));
                model.DuplicateLayoutSelectionCommand.Execute(null);
                Pump(window);
                Assert.Equal(3, model.EquipmentMaterialFlow.Slots.Count);
                Assert.Equal(3, Descendants(slots).OfType<Button>().Count());
                model.UndoLayoutEditCommand.Execute(null);
                Pump(window);
                Assert.Equal(3, model.EquipmentMaterialFlow.Slots.Count);
                Assert.Equal(3, Descendants(slots).OfType<Button>().Count());
                Assert.Equal(TimeSpan.Zero, model.SceneSnapshots.Latest!.SimulationTime);
                model.Layout.SelectMany(["feed", "inspection"], "feed");
                Pump(window);
                Assert.Equal(string.Empty, model.Layout.EquipmentSelectionIdText);
                Assert.Equal(model.Layout.SelectionSummaryText, model.Layout.EquipmentSelectionNameText);
                model.Layout.SelectMany([]);
                Pump(window);
                Assert.Equal(string.Empty, model.Layout.EquipmentSelectionIdText);
                Assert.All(Descendants(actions).OfType<Button>(), button => Assert.False(button.IsEnabled));
                Assert.Equal(Assert.IsType<SolidColorBrush>(primary.Foreground).Color, Assert.IsType<SolidColorBrush>(primaryCaption.Foreground).Color);
                return true;
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1920, 1040, true)]
    [InlineData(1280, 760, true)]
    [InlineData(1920, 1040, false)]
    [InlineData(1280, 760, false)]
    public async Task SimulationKeepsMaterialOrUnitActivityCardsAndCommonControls(double width, double height, bool hasWorkpieces)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.Sequences.Clear();
            project.Layouts[0].Components.Single(component => component.Id == "belt").UnitId = "unit-feed";
            if (!hasWorkpieces) project.Layouts[0].Components.RemoveAll(component => component.Kind == OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.Workpiece);
            using var model = new MainViewModel(project);
            model.Navigation.IsSimulationWorkspace = true;
            model.Navigation.SelectedExecutionTabIndex = 0;
            var window = new ShellWindow { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var slots = Assert.IsType<Border>(scene.FindName("EquipmentMaterialSlots"));
                var activity = Assert.IsType<Border>(scene.FindName("SimulationUnitActivities"));
                Assert.Equal(hasWorkpieces, slots.IsVisible);
                Assert.Equal(!hasWorkpieces, activity.IsVisible);
                var cards = Descendants(hasWorkpieces ? slots : activity).OfType<Button>().Where(button => button.IsVisible).ToArray();
                Assert.Equal(3, cards.Length);
                Assert.All(cards, card => Assert.Same(model.ShowEquipmentUnitCommand, card.Command));
                var viewing = Assert.IsType<WrapPanel>(scene.FindName("EquipmentViewToolbar"));
                Assert.True(viewing.IsVisible);
                Assert.True(Assert.IsType<Button>(scene.FindName("FitLayoutButton")).IsVisible);
                var bar = Assert.Single(Descendants(window).OfType<GlobalCommandBarView>());
                var state = Assert.IsType<TextBlock>(bar.FindName("CommonRunStateText"));
                var target = Assert.IsType<TextBlock>(bar.FindName("CommonRunTargetText"));
                Assert.Equal(OpenVisionLanguageService.T("Shell.Editing"), state.Text);
                Assert.Equal(model.RunTargetText, target.Text);
                var stateBounds = Bounds(state, window);
                var targetBounds = Bounds(target, window);
                var barBounds = Bounds(bar, window);
                Assert.True(stateBounds.Bottom <= barBounds.Bottom && targetBounds.Bottom <= barBounds.Bottom);
                Assert.True(Bounds(viewing, window).Top >= barBounds.Bottom);
                var before = target.Text;
                ((IInvokeProvider)new ButtonAutomationPeer(cards[0]).GetPattern(PatternInterface.Invoke)!).Invoke();
                Pump(window);
                Assert.Equal("unit-feed", model.Layout.ActiveUnitId);
                Assert.Equal(before, target.Text);
                Assert.False(model.IsRunning);
                Assert.False(model.HasUnsavedChanges);
                model.IsRunMode = true;
                Pump(window);
                Assert.Equal(OpenVisionLanguageService.T("Equipment.State.Ready"), state.Text);
                Assert.False(model.IsRunning);
                var commands = Assert.IsType<WrapPanel>(bar.FindName("CommonRunCommands"));
                Assert.True(commands.IsVisible);
                var viewport = Assert.IsType<MachineSceneViewport>(scene.FindName("SceneViewport"));
                Assert.True(Bounds(viewport, window).Bottom <= Bounds(hasWorkpieces ? slots : activity, window).Top);
                return true;
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
