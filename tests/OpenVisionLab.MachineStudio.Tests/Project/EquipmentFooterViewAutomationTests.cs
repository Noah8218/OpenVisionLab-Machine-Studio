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
using OpenVisionLab.Machine.Simulation.Workpieces;
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
            var window = new ShellWindow { DataContext = model, Width = width, MinWidth = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                Assert.Equal(width, window.ActualWidth, precision: 0);
                var mmi = Assert.Single(Descendants(window).OfType<MmiOperatorLayoutView>());
                model.Navigation.IsInspectorOpen = true;
                Pump(window);
                Assert.False(Assert.Single(Descendants(window).OfType<RightToolRegionView>()).IsVisible);
                Assert.DoesNotContain(Descendants(window).OfType<Button>(), button => button.Name == "WorkspaceInspectorToggleButton");
                model.Navigation.IsInspectorOpen = false;
                var preview = Assert.IsType<ScrollViewer>(mmi.FindName("MmiInspectionPreviewScrollViewer"));
                Assert.True(preview.IsVisible);
                var imageFrame = Assert.IsType<Border>(mmi.FindName("MmiVirtualCameraImageFrame"));
                Assert.True(double.IsPositiveInfinity(imageFrame.MaxHeight));
                Assert.True(new Rect(window.RenderSize).Contains(Bounds(imageFrame, window)));
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
                    var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(mmi.FindName("MmiInspectionSettingsToggle"));
                    Assert.False(cameraSource.IsVisible);
                    ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                    Pump(window);
                    Assert.True(model.Navigation.IsInspectionSettingsExpanded && cameraSource.IsVisible);
                    var setup = Assert.IsType<ScrollViewer>(mmi.FindName("MmiCaptureSetupScrollViewer"));
                    var viewport = Descendants(setup).OfType<ScrollContentPresenter>().First();
                    cameraSource.ApplyCameraSourceButton.BringIntoView();
                    Pump(window);
                    Assert.True(new Rect(viewport.RenderSize).Contains(Bounds(cameraSource.ApplyCameraSourceButton, viewport)),
                        $"Apply must fit setup viewport: button={Bounds(cameraSource.ApplyCameraSourceButton, setup)}, viewport={setup.ViewportWidth}x{setup.ViewportHeight}, offset={setup.VerticalOffset}, scrollable={setup.ScrollableHeight}.");
                    Assert.True(new Rect(window.RenderSize).Contains(Bounds(cameraSource.ApplyCameraSourceButton, window)));
                    cameraSource.BrowseCameraSourceButton.BringIntoView();
                    Pump(window);
                    Assert.True(new Rect(viewport.RenderSize).Contains(Bounds(cameraSource.BrowseCameraSourceButton, viewport)),
                        $"Browse must fit setup viewport: button={Bounds(cameraSource.BrowseCameraSourceButton, setup)}, viewport={setup.ViewportWidth}x{setup.ViewportHeight}, offset={setup.VerticalOffset}.");
                    ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                    Pump(window);
                    Assert.False(model.Navigation.IsInspectionSettingsExpanded);
                    Assert.True(preview.IsVisible);
                }
                else Assert.True(new Rect(window.RenderSize).Contains(Bounds(cameraSource.ApplyCameraSourceButton, window)));
                var before = Bounds(Assert.IsType<Button>(mmi.FindName("MmiApplyResultButton")), window);
                model.Navigation.IsInspectionSettingsExpanded = true;
                model.Navigation.SelectedInspectionSettingsTabIndex = 3;
                Pump(window);
                var imageBeforeResults = Bounds(imageFrame, window);
                var results = Assert.IsType<ScrollViewer>(mmi.FindName("MmiExternalImagesScrollViewer"));
                results.ScrollToBottom();
                Pump(window);
                Assert.Equal(results.ScrollableHeight, results.VerticalOffset, precision: 1);
                Assert.Equal(imageBeforeResults, Bounds(imageFrame, window));
                Assert.True(imageFrame.IsVisible);
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
                // The history workspace defaults to archived runs. Select the existing session-record tab explicitly.
                Assert.Single(Descendants(window).OfType<SceneDocumentView>()).ResultsWorkspaceTabs.SelectedIndex = 0;
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
            model.Layout.Select(hasWorkpieces ? "inspection" : "belt");
            var window = new ShellWindow { DataContext = model, Width = width, MinWidth = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                Assert.Equal(width, window.ActualWidth, precision: 0);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var footer = Assert.IsType<Grid>(scene.FindName("EquipmentFooterRegion"));
                var slots = Assert.IsType<Border>(scene.FindName("EquipmentMaterialSlots"));
                var actions = Assert.IsType<Border>(scene.FindName("EquipmentSelectionActions"));
                var inspector = Assert.Single(Descendants(window).OfType<RightToolRegionView>());
                Assert.False(slots.IsVisible);
                var disclosure = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(scene.FindName("EquipmentSlotsToggleButton"));
                var legend = Assert.IsType<WrapPanel>(scene.FindName("EquipmentSceneLegend"));
                var viewTools = Assert.IsType<Border>(scene.FindName("EquipmentViewToolbarGroup"));
                var additionalTools = Assert.IsType<StackPanel>(scene.FindName("EquipmentSupplementalActions"));
                var legendBounds = Bounds(legend, window);
                var actionBounds = Bounds(actions, window);
                Assert.InRange(Math.Abs(legendBounds.Bottom - actionBounds.Bottom), 0, 1);
                Assert.False(legendBounds.IntersectsWith(actionBounds), $"Legend={legendBounds}; selection={actionBounds}");
                Assert.True(Bounds(additionalTools, window).Bottom < Bounds(footer, window).Top);
                Assert.True(Bounds(additionalTools, window).Right <= Bounds(viewTools, window).Left);
                Assert.InRange(Math.Abs(Bounds(additionalTools, window).Top - Bounds(viewTools, window).Top), 0, 6);
                Assert.False(legend.IsHitTestVisible);
                if (width >= 1920) Assert.True(legend.ActualHeight < 24, "The normal Wide legend must fit one row.");
                ((IToggleProvider)new ToggleButtonAutomationPeer(disclosure).GetPattern(PatternInterface.Toggle)).Toggle();
                Pump(window);
                Assert.Equal(hasWorkpieces, slots.IsVisible);
                Assert.True(actions.IsVisible && inspector.IsVisible);
                var footerBounds = Bounds(footer, window);
                var inspectorBounds = Bounds(inspector, window);
                Assert.False(inspectorBounds.IntersectsWith(footerBounds), $"Inspector={inspectorBounds}; footer={footerBounds}");
                Assert.Equal(2, Descendants(actions).OfType<Button>().Count());
                var primary = Descendants(actions).OfType<Button>().Single(button => ReferenceEquals(button.Command, model.OpenEquipmentSinglePartCheckCommand));
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
                var scroll = Assert.Single(Descendants(slots).OfType<ScrollViewer>());
                if (scroll.ScrollableWidth > 0)
                {
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
                Assert.True(model.Navigation.IsInspectorOpen);
                Assert.Contains(model.Properties.Items, item => item.Value == "unit-feed");
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
                Assert.Contains(Descendants(actions).OfType<TextBlock>(), text => text.Text.Contains("Renamed workpiece position", StringComparison.Ordinal));
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
            var window = new ShellWindow { DataContext = model, Width = width, MinWidth = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                Assert.Equal(width, window.ActualWidth, precision: 0);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var slots = Assert.IsType<Border>(scene.FindName("EquipmentMaterialSlots"));
                var activity = Assert.IsType<OpenVisionLab.MachineStudio.View.Simulation.SimulationObservationView>(scene.FindName("SimulationUnitActivities"));
                Assert.Equal(hasWorkpieces, slots.IsVisible);
                Assert.True(activity.IsVisible);
                if (!hasWorkpieces) model.Navigation.IsSimulationDiagnosticsExpanded = true;
                Pump(window);
                var cards = Descendants(hasWorkpieces ? (FrameworkElement)slots : activity).OfType<Button>().Where(button => button.IsVisible && ReferenceEquals(button.Command, model.ShowEquipmentUnitCommand)).ToArray();
                Assert.Equal(3, cards.Length);
                Assert.All(cards, card => Assert.Same(model.ShowEquipmentUnitCommand, card.Command));
                var viewing = Assert.IsType<WrapPanel>(scene.FindName("EquipmentViewToolbar"));
                Assert.True(viewing.IsVisible);
                Assert.True(Assert.IsType<Button>(scene.FindName("FitLayoutButton")).IsVisible);
                var bar = Assert.Single(Descendants(window).OfType<GlobalCommandBarView>());
                var state = Assert.IsType<TextBlock>(bar.FindName("CommonRunStateText"));
                var target = Assert.Single(Descendants(Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(bar.FindName("RunTargetDetailsButton"))).OfType<TextBlock>());
                Assert.Equal(OpenVisionLanguageService.T("R24.Editing"), state.Text);
                Assert.Equal(model.RunTargetSummaryText, target.Text);
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
                var commands = Assert.IsType<StackPanel>(bar.FindName("CommonRunCommands"));
                Assert.True(commands.IsVisible);
                Assert.InRange(bar.ActualHeight, 34, 65);
                foreach (var control in commands.Children.OfType<FrameworkElement>().Where(control => control.IsVisible))
                    Assert.True(new Rect(bar.RenderSize).Contains(Bounds(control, bar)), "Run controls must fit the toolbar at both supported widths.");
                var viewport = Assert.IsType<MachineSceneViewport>(scene.FindName("SceneViewport"));
                Assert.True(Bounds(viewport, window).Bottom <= Bounds(hasWorkpieces ? (FrameworkElement)slots : activity, window).Top);
                return true;
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1040)]
    [InlineData(1280, 760)]
    [InlineData(1000, 720)]
    public async Task ThreeRunningSlotsKeepTheirRunIdentityAndFitWithoutHorizontalScrolling(double width, double height)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = EquipmentMaterialFlowViewModelTests.CreateProject();
            project.MaterialFlow.Enabled = true;
            project.Sequences = [new SequenceDefinition { Id = "inspect", UnitId = "unit-inspection", Steps =
                [new SequenceStepDefinition { Id = "capture", Name = "상면 검사 후 높이 촬영 준비를 위한 시간 대기", Action = SequenceStepAction.Delay, Parameter = "1", NextStepId = "complete" },
                 new SequenceStepDefinition { Id = "complete", Name = "완료", Action = SequenceStepAction.Complete }] }];
            using var model = new MainViewModel(project);
            model.Navigation.IsSimulationWorkspace = true;
            var snapshot = new SimulationSnapshot(TimeSpan.FromSeconds(3), 600, SimulationRunMode.RealTime, SimulationControlOwner.EmbeddedSequence, 1,
                [], 0, [], [new SequenceExecutionSnapshot("inspect", SequenceExecutionStatus.Running, "capture", 0, TimeSpan.Zero, TimeSpan.Zero, 600, null,
                    TimeSpan.Zero, UnitId: "unit-inspection", RepeatCount: 3)], [], AutomaticRunSnapshot.NotConfigured, [],
                unitMaterialSlots: project.Stations[0].Units.Select((unit, index) => new UnitMaterialSlotSnapshot(unit.Id,
                    $"744143a892f4494485418b56f1ba1d2a/unit-run0003/WP{index + 1:000}", "Occupied", null, 0, null, 0, 2)));
            var window = new ShellWindow { DataContext = model, Width = width, MinWidth = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Pump(window);
                Assert.Equal(width, window.ActualWidth, precision: 0);
                var materialFlow = new EquipmentMaterialFlowViewModel();
                materialFlow.Update(project, model.Layout.Items, snapshot);
                Pump(window);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var slots = Assert.IsType<Border>(scene.FindName("EquipmentMaterialSlots"));
                slots.DataContext = new { EquipmentMaterialFlow = materialFlow };
                model.EquipmentMaterialFlow.Update(project, model.Layout.Items, model.SceneSnapshots.Latest!);
                Assert.DoesNotContain(model.EquipmentMaterialFlow.Slots, slot => slot.InstanceId.StartsWith("744143a892f4494485418b56f1ba1d2a/", StringComparison.Ordinal));
                Pump(window);
                var scroll = Assert.Single(Descendants(slots).OfType<ScrollViewer>());
                Assert.InRange(scroll.ScrollableWidth, 0, 0.5);
                var cards = Descendants(slots).OfType<Button>().ToArray();
                Assert.Equal(3, cards.Length);
                Assert.All(cards, card => Assert.Same(model.ShowEquipmentUnitCommand, card.Command));
                for (var index = 0; index < cards.Length; index++)
                {
                    var bounds = Bounds(cards[index], slots);
                    Assert.True(bounds.Left >= 0 && bounds.Right <= slots.ActualWidth, $"All three cards must fit: {bounds}, footer={slots.ActualWidth}.");
                    var identity = Descendants(cards[index]).OfType<TextBlock>().Single(text => text.Text == $"unit-run0003/WP{index + 1:000}");
                    Assert.Equal(materialFlow.Slots[index].InstanceId, identity.ToolTip);
                    Assert.True(Bounds(identity, scroll).Bottom <= scroll.ViewportHeight);
                }
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
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
