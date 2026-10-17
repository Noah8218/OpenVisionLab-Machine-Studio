using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;
using Path = System.IO.Path;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40SequenceFlowWorkspaceTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task CaptureRankBranchWorkspace(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateBranchLayoutProject();
        if (!fullArea) project.Sequences[0].Steps[1].Name = "후공정 정렬 검사 장비의 판정 결과와 목적지 연결을 확인하는 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessSceneVisible = false;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            Capture(window, $"rank-branch-summary-{language}-{(fullArea ? "work-area" : "compact-long")}");
            main.SequenceEditor.IsProcessSummary = false;
            Pump(window);
            Capture(window, $"rank-branch-steps-{language}-{(fullArea ? "work-area" : "compact-long")}");
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || main.SequenceEditor.HasPendingStepDraft);
            return true;
        }
        finally { main.SequenceEditor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task CaptureGraphRouteWorkspace(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateMultiUnitProject();
        if (!fullArea) project.Sequences[0].Steps[7].Name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            var route = Descendants(window).OfType<Button>().Single(button => button.DataContext is SequenceTaskRoute { Label: "NG", Source.Id: "inspection" });
            Invoke(route);
            Pump(window);
            Assert.Equal("inspection", main.SequenceEditor.SelectedStep!.Id);
            Capture(window, $"graph-route-{language}-{(fullArea ? "work-area" : "compact-long")}");
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || main.SequenceEditor.HasPendingStepDraft);
            return true;
        }
        finally { CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Fact]
    public Task AppliedRoutesUseActualSuccessFailureAlarmAndTerminalContracts() => ui.InvokeAsync(() =>
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Sequences[0].Steps[7].NextStepId = "return";
        project.Sequences[0].Steps[7].ErrorStepId = "done";
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            var lane = Assert.Single(editor.ProcessLanes);
            var routes = lane.Routes.Where(route => route.Source.Id == "inspection").ToArray();
            Assert.Equal(new[] { "OK", "NG", OpenVisionLanguageService.T("R24.Inspector.AlarmRoute") }, routes.Select(route => route.Label));
            Assert.Equal(new[] { "return", "unload", "done" }, routes.Select(route => route.TargetId));
            Assert.All(routes, route => Assert.True(route.IsValid));
            Assert.Equal("move-1", lane.Routes.Single(route => route.Source.Id == "feed").TargetId);
            Assert.DoesNotContain(lane.Routes, route => route.Source.Id == "move-1");
            Assert.True(lane.Routes.Single(route => route.Source.Id == "done").IsTerminal);
            editor.IsProcessSummary = false;
            Assert.Equal(11, Assert.Single(editor.ProcessLanes).Groups.Count);
            Assert.Equal("wait-1", editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "move-1").TargetId);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-ng")]
    [InlineData("duplicate")]
    [InlineData("nonterminal-last")]
    [InlineData("terminal-transition")]
    [InlineData("backward")]
    [InlineData("whitespace-default")]
    public Task InvalidRoutesStayInvalidAndSupportedBackwardRoutesAreNotInventedAsForwardOnly(string boundary) => ui.InvokeAsync(() =>
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        var steps = project.Sequences[0].Steps;
        switch (boundary)
        {
            case "missing": steps[0].NextStepId = "not-a-step"; break;
            case "missing-ng": steps[7].FailureStepId = string.Empty; break;
            case "duplicate": steps[2].Id = "move-1"; break;
            case "nonterminal-last": steps.RemoveAt(10); break;
            case "terminal-transition": steps[10].NextStepId = "feed"; break;
            case "backward": steps[8].NextStepId = "feed"; break;
            case "whitespace-default": steps[0].NextStepId = "  "; break;
        }
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            var lane = Assert.Single(editor.ProcessLanes);
            if (boundary is "backward" or "whitespace-default")
            {
                Assert.Empty(editor.ValidationIssues);
                var route = lane.Routes.Single(item => item.Source.Id == (boundary == "backward" ? "unload" : "feed"));
                Assert.True(route.IsValid);
                Assert.Equal(boundary == "backward" ? "feed" : "move-1", route.TargetId);
            }
            else
            {
                Assert.NotEmpty(editor.ValidationIssues);
                Assert.Contains(lane.Routes, route => !route.IsValid && route.Caption.Contains(OpenVisionLanguageService.T("Process.InvalidRoute")));
                Assert.Contains(lane.Groups, group => group.HasIssue);
            }
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task AllLanesIncludeEmptyUnitsAndCommonSequenceAndSearchCyclesAcrossTheirExactOwners() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.IsProcessAllUnits = true;
            Assert.Equal(new[] { "첫 유닛", "둘째 유닛", "빈 유닛", "공통 시퀀스" }, editor.ProcessLanes.Select(lane => lane.Name));
            Assert.True(editor.ProcessLanes[2].IsEmpty);
            editor.StepSearchText = "needle";
            Assert.Equal(3, editor.StepSearchMatchCount);
            foreach (string expected in new[] { "sequence", "second-sequence", "common", "sequence" })
            {
                editor.FindNextStepCommand.Execute(null);
                Assert.Equal(expected, editor.SelectedSequence!.Id);
                Assert.Equal("feed", editor.SelectedStep!.Id);
                Assert.Equal(3, editor.StepSearchMatchCount);
            }
            editor.IsProcessTable = true;
            Assert.Equal(3, editor.StepSearchMatchCount);
            editor.IsProcessOverview = true;
            Assert.Equal(3, editor.StepSearchMatchCount);
            editor.IsProcessSummary = false;
            editor.FlowZoom = 0.6;
            Assert.False(editor.ChangeFlowZoomCommand.CanExecute("-"));
            editor.FlowZoom = 1.4;
            Assert.False(editor.ChangeFlowZoomCommand.CanExecute("+"));
            editor.FlowZoom = double.NaN;
            Assert.Equal(1.4, editor.FlowZoom);
            editor.ChangeFlowZoomCommand.Execute("reset");
            Assert.Equal(1, editor.FlowZoom);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task LaneSelectionKeepsDraftOnCancelRechecksRemovedTargetAndRejectsStaleRows() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.IsProcessAllUnits = true;
            var target = editor.ProcessLanes[1].Steps[0];
            editor.SelectedStepDraft!.Parameter = "0.4";
            editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
            editor.SelectOverviewStepCommand.Execute(target);
            Assert.Equal("sequence", editor.SelectedSequence!.Id);
            Assert.Equal("0.4", editor.SelectedStepDraft.Parameter);
            editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
            editor.SelectOverviewStepCommand.Execute(target);
            Assert.Equal("second-sequence", editor.SelectedSequence.Id);
            Assert.Equal("feed", editor.SelectedStep!.Id);
            Assert.False(editor.HasPendingStepDraft);
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(target));
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(editor.SelectedStep.CreateDraft()));
            editor.SelectedStepDraft!.Parameter = "0.5";
            var first = editor.ProcessLanes[0].Steps[0];
            editor.PendingStepDraftPrompt = () => { project.Sequences[0].Steps.RemoveAt(0); return PlacementDraftDecision.Discard; };
            editor.SelectOverviewStepCommand.Execute(first);
            Assert.Equal("second-sequence", editor.SelectedSequence.Id);
            editor.Load(CreateMultiUnitProject());
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(first));
            Assert.False(editor.IsProcessAllUnits);
            Assert.True(editor.IsProcessSummary);
            Assert.Equal(1, editor.FlowZoom);
            editor.Dispose();
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(editor.Steps.FirstOrDefault()));
            Assert.False(editor.ChangeFlowZoomCommand.CanExecute("+"));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task RouteDraftRemainsSeparateUntilExistingValidationAndExplicitApplyThenSerializes() => ui.InvokeAsync(() =>
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.StepNextStepId = "missing";
            Assert.False(editor.CanApplyStepDraft);
            Assert.Equal("move-1", editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "feed").TargetId);
            Assert.False(editor.TryApplyStepDraft());
            editor.DiscardStepDraft();
            editor.StepNextStepId = "return";
            Assert.True(editor.TryApplyStepDraft());
            Assert.Equal("return", editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "feed").TargetId);
            var store = new ProjectDocumentStore();
            editor.Load(store.Load(store.Serialize(project)));
            Assert.Equal("return", editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "feed").TargetId);
            Assert.False(editor.HasPendingStepDraft);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true, false, false)]
    [InlineData(OpenVisionLanguage.English, true, false, false)]
    [InlineData(OpenVisionLanguage.Korean, false, false, true)]
    [InlineData(OpenVisionLanguage.English, false, false, true)]
    [InlineData(OpenVisionLanguage.Korean, true, true, false)]
    [InlineData(OpenVisionLanguage.English, false, true, false)]
    public Task RenderedFlowControlsAndRoutesUseExistingBindingsWithoutApplyingOrRunning(OpenVisionLanguage language, bool fullArea, bool allUnits, bool longNames) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateMultiUnitProject();
        if (longNames)
        {
            const string name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 매우 긴 공정 설정 이름";
            project.Sequences[0].Steps[3].Name = name;
            project.Devices[0].Name = name;
            project.Stations[0].Units[0].Name = name;
        }
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessAllUnits = allUnits;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            var editor = main.SequenceEditor;
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            var overview = Descendants(view).OfType<SequenceTaskOverviewView>().Single();
            Assert.Same(editor, overview.DataContext);
            Assert.Contains(Descendants(overview).OfType<Canvas>(), canvas => canvas.Name == "FlowWires" && canvas.Children.OfType<Polyline>().Any());
            Assert.Equal(editor.IsProcessAllUnits, view.AllProcessUnitsToggle.IsChecked);
            Invoke(view.FlowZoomInButton);
            Pump(window);
            Assert.Equal(1.1, overview.FlowScale.ScaleX);
            Invoke(view.FlowZoomResetButton);
            view.ProcessSummaryToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Pump(window);
            Assert.False(editor.IsProcessSummary);
            Assert.Equal(11, editor.ProcessLanes[0].Groups.Count);
            view.ProcessSummaryToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Pump(window);
            Assert.True(editor.IsProcessAllUnits && editor.IsProcessSummary);
            Assert.Equal(4, editor.ProcessLanes.Count);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "needle beta");
            Pump(window);
            Assert.Equal("needle beta", editor.StepSearchText);
            Invoke(view.FindNextStepButton);
            Pump(window);
            Assert.Equal("second-sequence", editor.SelectedSequence!.Id);
            Assert.False(editor.IsProcessSummary);
            Assert.Equal(1, editor.FlowZoom);
            var card = Descendants(overview).OfType<Border>().Single(item => item.Name == "FlowNodeCard" && item.Tag is true);
            Rect visible = card.TransformToAncestor(overview.FlowViewport).TransformBounds(new Rect(card.RenderSize));
            Assert.True(visible.IntersectsWith(new Rect(overview.FlowViewport.RenderSize)));
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "needle common");
            Invoke(view.FindNextStepButton);
            Pump(window);
            Assert.Equal("common", editor.SelectedSequence.Id);
            Capture(window, $"search-common-{language}-{fullArea}-{allUnits}-{longNames}");
            Assert.True(editor.IsProcessAllUnits);
            var commonCard = Descendants(overview).OfType<Border>().Single(item => item.Name == "FlowNodeCard" && item.Tag is true);
            Rect commonBounds = commonCard.TransformToAncestor(overview.FlowViewport).TransformBounds(new Rect(commonCard.RenderSize));
            Assert.True(new Rect(overview.FlowViewport.RenderSize).Contains(commonBounds));
            var commonLane = Descendants(overview).OfType<Border>().Single(item => item.Name == "FlowLaneFrame" && item.DataContext is SequenceTaskLaneViewModel { Definition.Id: "common" });
            Rect laneBounds = commonLane.TransformToAncestor(overview.FlowViewport).TransformBounds(new Rect(commonLane.RenderSize));
            Assert.True(laneBounds.Left >= 0 && laneBounds.Right <= overview.FlowViewport.ActualWidth);
            Assert.True(overview.FlowViewport.HorizontalOffset > 0);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "no-such-step");
            Pump(window);
            Assert.False(view.FindNextStepButton.IsEnabled);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
            editor.SelectSequence("sequence");
            view.ProcessSummaryToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, allUnits);
            Pump(window);
            foreach (var canvas in Descendants(overview).OfType<Canvas>().Where(item => item.Name == "FlowWires"))
            {
                var labels = canvas.Children.OfType<TextBlock>().ToArray();
                for (int i = 0; i < labels.Length; i++)
                for (int j = i + 1; j < labels.Length; j++)
                    Assert.False(labels[i].TransformToAncestor(canvas).TransformBounds(new Rect(labels[i].RenderSize))
                        .IntersectsWith(labels[j].TransformToAncestor(canvas).TransformBounds(new Rect(labels[j].RenderSize))), $"Flow labels overlap: {labels[i].Text} / {labels[j].Text}");
            }
            Capture(window, $"flow-{language}-{(fullArea ? "work-area" : "compact")}-{(allUnits ? "all" : "selected")}-{(longNames ? "long" : "normal")}");
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || editor.IsStepTestBusy);
            return true;
        }
        finally { CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Fact]
    public Task RenderedRouteOpensExistingInspectorAndInvalidNgDraftRecoversBeforeExplicitApply() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        using var main = new MainViewModel(project);
        main.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, fullArea: true);
        try
        {
            var editor = main.SequenceEditor;
            var routeButton = Descendants(window).OfType<Button>().Single(button => button.DataContext is SequenceTaskRoute { Label: "NG", Source.Id: "inspection" });
            Assert.Same(editor.BeginRouteDestinationSelectionCommand, routeButton.Command);
            Invoke(routeButton);
            Pump(window);
            Assert.Equal("inspection", editor.SelectedStep!.Id);
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
            Assert.Same(editor, inspector.DataContext);
            inspector.StepDraftFailure.SetCurrentValue(Selector.SelectedItemProperty, editor.StepFailureOptions.Single(option => option.Key == string.Empty));
            Pump(window);
            Assert.True(editor.HasPendingStepDraft);
            Assert.False(inspector.ApplyStepDraftButton.IsEnabled);
            Assert.True(editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "inspection" && route.Label == "NG").IsValid);
            Assert.Equal("unload", project.Sequences[0].Steps[7].FailureStepId);
            Capture(window, "route-invalid-draft-work-area");
            Invoke(inspector.DiscardStepDraftButton);
            Pump(window);
            Assert.False(editor.HasPendingStepDraft);
            inspector.StepDraftNext.SetCurrentValue(Selector.SelectedItemProperty, editor.StepNextStepOptions.Single(option => option.Key == "done"));
            Pump(window);
            Assert.True(inspector.ApplyStepDraftButton.IsEnabled);
            Assert.Equal("return", project.Sequences[0].Steps[7].NextStepId);
            Invoke(inspector.ApplyStepDraftButton);
            Pump(window);
            Assert.Equal("done", project.Sequences[0].Steps[7].NextStepId);
            Assert.Equal("done", editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "inspection" && route.Label == "OK").TargetId);
            Assert.False(editor.HasPendingStepDraft || main.IsRunning || editor.IsStepTestBusy);
            Assert.True(main.HasUnsavedChanges);
            Capture(window, "route-applied-recovery-work-area");
            return true;
        }
        finally { CloseFixtureWindow(window); }
    });

    [Fact]
    public Task CrossLaneSelectionUsesMainPlacementDraftAdmissionAndExistingUnitNavigation() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor;
        editor.IsProcessAllUnits = true;
        main.Layout.Select("stage");
        var placement = Assert.IsType<LayoutComponentEditorViewModel>(main.Layout.SelectedComponentEditor);
        placement.DraftAxisSpeedText = "123";
        Assert.True(placement.HasPendingInspectorDraft);
        var target = editor.ProcessLanes[1].Steps[0];
        string before = JsonSerializer.Serialize(project);
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
        editor.SelectOverviewStepCommand.Execute(target);
        Assert.Equal("sequence", editor.SelectedSequence!.Id);
        Assert.True(placement.HasPendingInspectorDraft);
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
        editor.SelectOverviewStepCommand.Execute(target);
        Assert.Equal("second-sequence", editor.SelectedSequence.Id);
        Assert.Equal("feed", editor.SelectedStep!.Id);
        Assert.False(placement.HasPendingInspectorDraft);
        Assert.Equal(before, JsonSerializer.Serialize(project));
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
        var first = editor.ProcessLanes[0].Steps[0];
        main.Navigation.IsInspectionWorkspace = true;
        Assert.False(editor.SelectOverviewStepCommand.CanExecute(first));
        editor.SelectOverviewStepCommand.Execute(first);
        Assert.Equal("second-sequence", editor.SelectedSequence.Id);
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task CaptureAllUnitsTableWorkspace(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateMultiUnitProject();
        if (!fullArea) project.Sequences[0].Steps[3].Name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessAllUnits = true;
        main.SequenceEditor.IsProcessTable = true;
        main.Navigation.IsInspectorOpen = false;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            Capture(window, $"all-units-table-{language}-{(fullArea ? "work-area" : "compact-long")}");
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning);
            return true;
        }
        finally { CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedAllUnitsTableUsesExactOwnerCommandsAndSharedSearchScope(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateMultiUnitProject();
        if (!fullArea) project.Sequences[0].Steps[3].Name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessTable = true;
        main.Navigation.IsInspectorOpen = false;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            var editor = main.SequenceEditor;
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            Assert.Same(editor, view.DataContext);
            Assert.True(view.AllProcessUnitsToggle.IsVisible);
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Pump(window);
            Assert.True(editor.IsProcessAllUnits && view.AllUnitsTableViewport.IsVisible);
            Assert.False(view.StepListDataGrid.IsVisible);
            Assert.Equal(4, view.AllUnitsTableLanes.Items.Count);
            Assert.Equal(15, TableRows(view).Count());
            Assert.Single(Descendants(view.AllUnitsTableLanes).OfType<TextBlock>().Where(text => text.Name == "AllUnitsEmptySteps" && text.IsVisible));
            Assert.Empty(Descendants(view.AllUnitsTableLanes).OfType<TextBox>());
            var tableGrids = Descendants(view.AllUnitsTableLanes).OfType<Grid>().Where(grid => grid.ColumnDefinitions.Count == 6).ToArray();
            foreach (var grid in tableGrids)
            {
                Assert.True(grid.ActualWidth <= view.AllUnitsTableLanes.ActualWidth);
                for (int column = 0; column < 6; column++)
                    Assert.True(Math.Abs(grid.ColumnDefinitions[column].ActualWidth - tableGrids[0].ColumnDefinitions[column].ActualWidth) < 0.5, $"Table column {column} is misaligned.");
            }
            foreach (var row in TableRows(view))
            {
                Assert.Same(editor.SelectOverviewStepCommand, row.Command);
                Assert.Same(row.DataContext, row.CommandParameter);
                foreach (var text in Descendants(row).OfType<TextBlock>().Where(text => text.IsVisible && text.Text.Length > 0))
                    Assert.True(text.ActualHeight + 0.1 >= text.DesiredSize.Height, text.Text);
            }
            var second = TableRows(view).Single(row => row.DataContext is SequenceStepEditorItem { SequenceId: "second-sequence", Id: "feed" });
            editor.SelectedStepDraft!.Parameter = "0.4";
            main.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            Invoke(second);
            Pump(window);
            Assert.Equal("sequence", editor.SelectedSequence!.Id);
            Assert.Equal("0.4", editor.SelectedStepDraft.Parameter);
            Assert.Single(TableRows(view).Where(row => row.Tag is true));
            main.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
            Invoke(second);
            Pump(window);
            Assert.Equal("second-sequence", editor.SelectedSequence.Id);
            Assert.Equal("second", main.Layout.ActiveUnitId);
            Assert.Same(editor.Steps[0], editor.SelectedStep);
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(second.CommandParameter));
            Assert.Same(editor, Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible).DataContext);
            Assert.Same(editor.SelectedStep, TableRows(view).Single(row => row.Tag is true).DataContext);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "needle");
            Pump(window);
            Assert.Equal(3, editor.StepSearchMatchCount);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "needle common");
            Invoke(view.FindNextStepButton);
            Pump(window);
            Assert.Equal("common", editor.SelectedSequence.Id);
            var selected = TableRows(view).Single(row => row.Tag is true);
            Rect bounds = selected.TransformToAncestor(view.AllUnitsTableViewport).TransformBounds(new Rect(selected.RenderSize));
            Assert.True(bounds.Top >= -0.1 && bounds.Bottom <= view.AllUnitsTableViewport.ViewportHeight + 0.1, bounds.ToString());
            Capture(window, $"table-common-selected-{language}-{fullArea}");
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "needle");
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Pump(window);
            Assert.True(view.StepListDataGrid.IsVisible);
            Assert.Equal(2, view.StepListDataGrid.Items.Count);
            Assert.Equal(1, editor.StepSearchMatchCount);
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            view.ProcessViewToolbar.Children.OfType<RadioButton>().First().SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Pump(window);
            Assert.True(editor.IsProcessOverview);
            Assert.Equal(3, editor.StepSearchMatchCount);
            view.ProcessViewToolbar.Children.OfType<RadioButton>().Skip(1).First().SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Pump(window);
            Assert.True(editor.IsProcessTable && view.AllUnitsTableViewport.IsVisible);
            Assert.Equal(3, editor.StepSearchMatchCount);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "no-table-match");
            Pump(window);
            Assert.False(view.FindNextStepButton.IsEnabled);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(editor.HasPendingStepDraft || main.HasUnsavedChanges || main.IsRunning || editor.IsStepTestBusy);
            return true;
        }
        finally { CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Fact]
    public Task OtherUnitTableShowsCompilerErrorAndRecoversThroughExistingInspectorApply() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        using var main = new MainViewModel(project);
        main.SequenceEditor.SelectSequence("second-sequence");
        main.SequenceEditor.Steps[0].NextStepId = "missing";
        main.SequenceEditor.SelectSequence("sequence");
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessAllUnits = true;
        main.SequenceEditor.IsProcessTable = true;
        var window = Show(main, fullArea: true);
        try
        {
            var editor = main.SequenceEditor;
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            var other = TableRows(view).Single(row => row.DataContext is SequenceStepEditorItem { SequenceId: "second-sequence", Id: "feed" });
            var item = Assert.IsType<SequenceStepEditorItem>(other.DataContext);
            Assert.NotEmpty(item.StepListStatusText);
            Assert.Contains(Descendants(other).OfType<TextBlock>(), text => text.Text == item.StepListStatusText);
            Invoke(other);
            Pump(window);
            Assert.NotEmpty(editor.ValidationIssues);
            Capture(window, "table-other-unit-error-work-area");
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
            inspector.StepDraftNext.SetCurrentValue(Selector.SelectedItemProperty, editor.StepNextStepOptions.Single(option => option.Key == "done"));
            Pump(window);
            Assert.Equal("missing", project.Sequences[1].Steps[0].NextStepId);
            Assert.True(inspector.ApplyStepDraftButton.IsEnabled && editor.HasPendingStepDraft);
            Invoke(inspector.ApplyStepDraftButton);
            Pump(window);
            Assert.Equal("done", project.Sequences[1].Steps[0].NextStepId);
            Assert.Empty(editor.ValidationIssues);
            Assert.Empty(editor.ProcessLanes[1].Steps[0].StepListStatusText);
            Assert.True(main.HasUnsavedChanges);
            Assert.False(editor.HasPendingStepDraft || main.IsRunning || editor.IsStepTestBusy);
            var store = new ProjectDocumentStore();
            Assert.Equal("done", store.Load(store.Serialize(project)).Sequences[1].Steps[0].NextStepId);
            Capture(window, "table-other-unit-applied-work-area");
            return true;
        }
        finally { CloseFixtureWindow(window); }
    });

    [Fact]
    public Task TableScopeKeepsEmptyAndUnassignedSequencesAndReleasesOldRowsOnLoadAndDispose() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        project.Sequences.Add(new SequenceDefinition { Id = "empty-sequence", Name = "빈 공정", UnitId = "empty" });
        project.Sequences.Add(new SequenceDefinition { Id = "orphan", Name = "미지정 유닛 공정", UnitId = "not-current", Steps = [new() { Id = "feed", Name = "needle orphan", Action = SequenceStepAction.Delay, Parameter = "0.2" }, new() { Id = "done", Action = SequenceStepAction.Complete }] });
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.IsProcessAllUnits = true;
            editor.IsProcessTable = true;
            Assert.Equal(new[] { "sequence", "second-sequence", "empty-sequence", "common", "orphan" }, editor.ProcessLanes.Select(lane => lane.Definition!.Id));
            Assert.True(editor.ProcessLanes[2].IsEmpty);
            Assert.Equal(OpenVisionLanguageService.T("R24.EmptyUnitSteps"), editor.ProcessLanes[2].EmptyText);
            editor.StepSearchText = "needle";
            Assert.Equal(4, editor.StepSearchMatchCount);
            var target = editor.ProcessLanes[^1].Steps[0];
            editor.CanSelectOverviewSequence = () => false;
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(target));
            editor.CanSelectOverviewSequence = () => true;
            editor.SelectOverviewStepCommand.Execute(target);
            Assert.Equal("orphan", editor.SelectedSequence!.Id);
            Assert.Same(editor.Steps[0], editor.SelectedStep);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            editor.Load(CreateMultiUnitProject());
            Assert.False(editor.IsProcessAllUnits || editor.IsProcessTable);
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(target));
            editor.IsProcessAllUnits = true;
            var current = editor.ProcessLanes[1].Steps[0];
            editor.Dispose();
            Assert.Empty(editor.ProcessLanes);
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(current));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("feed", "NextStepId", "done")]
    [InlineData("inspection", "NextStepId", "feed")]
    [InlineData("inspection", "FailureStepId", "done")]
    [InlineData("inspection", "ErrorStepId", "feed")]
    public Task GraphDestinationWritesOnlyTheSelectedRouteDraftThenUsesExistingApplyAndJsonRoundTrip(string sourceId, string property, string targetId) => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            var route = editor.ProcessLanes[0].Routes.Single(item => item.Source.Id == sourceId && item.PropertyName == property);
            string applied = route.TargetId;
            editor.BeginRouteDestinationSelectionCommand.Execute(route);
            Assert.True(editor.IsSelectingRouteDestination);
            Assert.False(editor.IsProcessSummary || editor.HasPendingStepDraft);
            Assert.Equal(sourceId, editor.SelectedStep!.Id);
            Assert.False(editor.BeginRouteDestinationSelectionCommand.CanExecute(route));
            var target = editor.Steps.Single(step => step.Id == targetId);
            Assert.True(editor.SelectOverviewStepCommand.CanExecute(target));
            editor.SelectOverviewStepCommand.Execute(target);
            Assert.False(editor.IsSelectingRouteDestination);
            Assert.Equal(sourceId, editor.SelectedStep.Id);
            var draft = editor.SelectedStepDraft!.CopyDefinition();
            Assert.Equal(targetId, property switch { "NextStepId" => draft.NextStepId, "FailureStepId" => draft.FailureStepId, _ => draft.ErrorStepId });
            Assert.Equal(applied, editor.ProcessLanes[0].Routes.Single(item => item.Source.Id == sourceId && item.PropertyName == property).TargetId);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.True(editor.CanApplyStepDraft);
            Assert.True(editor.TryApplyStepDraft());
            Assert.Equal(targetId, editor.ProcessLanes[0].Routes.Single(item => item.Source.Id == sourceId && item.PropertyName == property).TargetId);
            var store = new ProjectDocumentStore();
            editor.Load(store.Load(store.Serialize(project)));
            Assert.Empty(editor.ValidationIssues);
            Assert.False(editor.HasPendingStepDraft || editor.IsSelectingRouteDestination);
            Assert.Equal(targetId, editor.ProcessLanes[0].Routes.Single(item => item.Source.Id == sourceId && item.PropertyName == property).TargetId);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task GraphDestinationCancelScopeFindAndLifetimeKeepDraftAndRejectForeignOrStaleSteps() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.IsProcessAllUnits = true;
            editor.SelectedStepDraft!.Parameter = "0.4";
            string before = JsonSerializer.Serialize(project);
            var route = editor.ProcessLanes[0].Routes.First(item => item.Source.Id == "feed");
            editor.BeginRouteDestinationSelectionCommand.Execute(route);
            Assert.True(editor.IsSelectingRouteDestination);
            var foreign = editor.ProcessLanes[1].Steps[0];
            Assert.Equal(editor.SelectedStep!.Id, foreign.Id);
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(foreign));
            editor.SelectOverviewStepCommand.Execute(foreign);
            Assert.True(editor.IsSelectingRouteDestination);
            Assert.Equal("0.4", editor.SelectedStepDraft.Parameter);
            editor.CancelRouteDestinationSelectionCommand.Execute(null);
            Assert.False(editor.IsSelectingRouteDestination);
            Assert.Equal("0.4", editor.SelectedStepDraft.Parameter);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(editor.BeginRouteDestinationSelectionCommand.CanExecute(route));
            editor.DiscardStepDraft();
            void Begin() => editor.BeginRouteDestinationSelectionCommand.Execute(editor.ProcessLanes[0].Routes.First(item => item.Source.Id == "feed"));
            Begin(); editor.IsProcessTable = true;
            Assert.False(editor.IsSelectingRouteDestination);
            editor.IsProcessOverview = true;
            Begin(); editor.IsProcessAllUnits = false;
            Assert.False(editor.IsSelectingRouteDestination);
            Begin(); editor.IsProcessSummary = true;
            Assert.False(editor.IsSelectingRouteDestination);
            Begin(); editor.StepSearchText = "capture-2"; editor.FindNextStepCommand.Execute(null);
            Assert.False(editor.IsSelectingRouteDestination || editor.HasPendingStepDraft);
            Assert.Equal("capture-2", editor.SelectedStep!.Id);
            Begin(); editor.IsEditable = false;
            Assert.False(editor.IsSelectingRouteDestination);
            editor.IsEditable = true;
            Begin(); var stale = editor.Steps[1]; editor.Load(CreateMultiUnitProject());
            Assert.False(editor.IsSelectingRouteDestination || editor.SelectOverviewStepCommand.CanExecute(stale));
            Begin(); editor.Dispose();
            Assert.False(editor.IsSelectingRouteDestination || editor.CancelRouteDestinationSelectionCommand.CanExecute(null));
            Assert.False(editor.BeginRouteDestinationSelectionCommand.CanExecute(editor.ProcessLanes.FirstOrDefault()));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task GraphRouteSelectionRechecksDraftCancellationRemovedSourceAndMainUnitAdmission() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor;
        editor.IsProcessAllUnits = true;
        main.Layout.Select("stage");
        var placement = Assert.IsType<LayoutComponentEditorViewModel>(main.Layout.SelectedComponentEditor);
        placement.DraftAxisSpeedText = "123";
        var route = editor.ProcessLanes[1].Routes.First(item => !item.IsTerminal);
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
        editor.BeginRouteDestinationSelectionCommand.Execute(route);
        Assert.Equal("sequence", editor.SelectedSequence!.Id);
        Assert.False(editor.IsSelectingRouteDestination);
        Assert.True(placement.HasPendingInspectorDraft);
        main.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
        editor.BeginRouteDestinationSelectionCommand.Execute(route);
        Assert.Equal("second-sequence", editor.SelectedSequence.Id);
        Assert.True(editor.IsSelectingRouteDestination);
        Assert.False(placement.HasPendingInspectorDraft);
        editor.SelectOverviewStepCommand.Execute(editor.Steps.Single(step => step.Id == "done"));
        Assert.True(editor.HasPendingStepDraft);
        var first = editor.ProcessLanes[0].Routes.First(item => item.Source.Id == "feed");
        editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
        editor.BeginRouteDestinationSelectionCommand.Execute(first);
        Assert.Equal("second-sequence", editor.SelectedSequence.Id);
        Assert.False(editor.IsSelectingRouteDestination);
        editor.PendingStepDraftPrompt = () => { project.Sequences[0].Steps.RemoveAt(0); return PlacementDraftDecision.Discard; };
        editor.BeginRouteDestinationSelectionCommand.Execute(first);
        Assert.False(editor.IsSelectingRouteDestination);
        editor.Load(CreateMultiUnitProject());
        main.Navigation.IsInspectionWorkspace = true;
        Assert.False(editor.BeginRouteDestinationSelectionCommand.CanExecute(editor.ProcessLanes[0].Routes.First()));
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedGraphDestinationControlsKeepSourceInspectorAndExplicitApply(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateMultiUnitProject();
        if (!fullArea) project.Sequences[0].Steps[7].Name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            var editor = main.SequenceEditor;
            var view = Descendants(window).OfType<SequenceTaskOverviewView>().Single();
            var route = Descendants(view).OfType<Button>().Single(button => button.DataContext is SequenceTaskRoute { Label: "NG", Source.Id: "inspection" });
            Invoke(route); Pump(window);
            Assert.True(view.RouteDestinationSelectionBar.IsVisible && editor.IsSelectingRouteDestination);
            Assert.Same(editor.CancelRouteDestinationSelectionCommand, view.CancelRouteDestinationSelectionButton.Command);
            Capture(window, $"graph-route-layout-{language}-{fullArea}");
            var textMeasurements = new List<object>();
            foreach (var text in Descendants(view.RouteDestinationSelectionBar).OfType<TextBlock>().Where(text => text.IsVisible))
            {
                Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
                text.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
                textMeasurements.Add(new { text.Text, text.ActualWidth, text.ActualHeight, desiredHeight = text.DesiredSize.Height, margin = text.Margin.ToString() });
                // DesiredSize includes the layout margin; ActualHeight is the rendered content height.
                Assert.True(text.DesiredSize.Height <= text.ActualHeight + text.Margin.Top + text.Margin.Bottom + 1, text.Text);
            }
            File.WriteAllText(Path.Combine(TestStorage.RootPath, "sequence-flow", $"graph-route-text-measure-{language}-{fullArea}.json"), JsonSerializer.Serialize(textMeasurements));
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Invoke(view.CancelRouteDestinationSelectionButton); Pump(window);
            Assert.False(view.RouteDestinationSelectionBar.IsVisible || editor.HasPendingStepDraft);
            route = Descendants(view).OfType<Button>().Single(button => button.DataContext is SequenceTaskRoute { Label: "NG", Source.Id: "inspection" });
            Invoke(route); Pump(window);
            var target = Descendants(view).OfType<Button>().Single(button => button.Command == editor.SelectOverviewStepCommand
                && button.CommandParameter is SequenceStepEditorItem { Id: "done" });
            target.BringIntoView(); Pump(window);
            Assert.True(target.IsEnabled);
            Invoke(target); Pump(window);
            Assert.Equal("inspection", editor.SelectedStep!.Id);
            Assert.Equal("done", editor.SelectedStepDraft!.FailureStepId);
            Assert.Equal("unload", project.Sequences[0].Steps[7].FailureStepId);
            Assert.False(view.RouteDestinationSelectionBar.IsVisible);
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
            Assert.Same(editor, inspector.DataContext);
            Assert.Equal(editor.StepFailureOption, inspector.StepDraftFailure.SelectedItem);
            Capture(window, $"graph-destination-draft-{language}-{fullArea}");
            Assert.True(inspector.ApplyStepDraftButton.IsEnabled);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Invoke(inspector.ApplyStepDraftButton); Pump(window);
            Assert.Equal("done", project.Sequences[0].Steps[7].FailureStepId);
            Assert.False(editor.HasPendingStepDraft || main.IsRunning || editor.IsStepTestBusy);
            Assert.True(main.HasUnsavedChanges);
            Capture(window, $"graph-destination-applied-{language}-{fullArea}");
            return true;
        }
        finally { CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData("source-removed")]
    [InlineData("destination-removed")]
    [InlineData("source-changed")]
    public Task GraphDestinationRejectsChangedSourceOrRemovedDestinationThenReloadRecovers(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.BeginRouteDestinationSelectionCommand.Execute(editor.ProcessLanes[0].Routes.First(item => item.Source.Id == "feed"));
            var target = editor.Steps.Last();
            string draft = editor.SelectedStepDraft!.DefinitionSnapshot;
            if (boundary == "source-removed") project.Sequences[0].Steps.RemoveAt(0);
            else if (boundary == "destination-removed") project.Sequences[0].Steps.RemoveAt(project.Sequences[0].Steps.Count - 1);
            else project.Sequences[0].Steps[0].Parameter = "9";
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(target));
            editor.SelectOverviewStepCommand.Execute(target);
            Assert.Equal(draft, editor.SelectedStepDraft.DefinitionSnapshot);
            editor.Load(CreateMultiUnitProject());
            editor.BeginRouteDestinationSelectionCommand.Execute(editor.ProcessLanes[0].Routes.First(item => item.Source.Id == "feed"));
            editor.SelectOverviewStepCommand.Execute(editor.Steps.Last());
            Assert.True(editor.TryApplyStepDraft());
            Assert.Empty(editor.ValidationIssues);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task GraphDestinationIsCancelledWhileExistingIsolatedStepTestOwnsTheEditor() => ui.InvokeTaskAsync(async () =>
    {
        var project = CreateMultiUnitProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.IsProcessSummary = false;
            var route = editor.ProcessLanes[0].Routes.Single(item => item.Source.Id == "move-1");
            editor.BeginRouteDestinationSelectionCommand.Execute(route);
            Assert.True(editor.IsSelectingRouteDestination);
            string before = JsonSerializer.Serialize(project);
            var pending = editor.TestSelectedStepAsync();
            Assert.True(editor.IsStepTestBusy);
            Assert.False(editor.IsSelectingRouteDestination || editor.BeginRouteDestinationSelectionCommand.CanExecute(route));
            await pending;
            Assert.False(editor.IsStepTestBusy || editor.HasPendingStepDraft);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            editor.BeginRouteDestinationSelectionCommand.Execute(editor.ProcessLanes[0].Routes.Single(item => item.Source.Id == "move-1"));
            Assert.True(editor.IsSelectingRouteDestination);
            editor.CancelRouteDestinationSelectionCommand.Execute(null);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task GraphRouteOnAlreadySelectedSourceReopensInspectorWithoutChangingSelectionOrDefinition() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor;
        Assert.Equal("feed", editor.SelectedStep!.Id);
        main.Navigation.IsInspectorOpen = false;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea: true);
        try
        {
            var view = Descendants(window).OfType<SequenceTaskOverviewView>().Single();
            var route = Descendants(view).OfType<Button>().Single(button => button.DataContext is SequenceTaskRoute { Source.Id: "feed" });
            Invoke(route); Pump(window);
            Capture(window, "graph-same-source-pick-work-area");
            Assert.True(main.Navigation.IsInspectorOpen);
            Assert.True(editor.IsSelectingRouteDestination);
            Assert.Equal("feed", editor.SelectedStep.Id);
            main.Navigation.IsInspectorOpen = false;
            var target = Descendants(view).OfType<Button>().Single(button => button.Command == editor.SelectOverviewStepCommand
                && button.CommandParameter is SequenceStepEditorItem { Id: "done" });
            Invoke(target); Pump(window);
            Assert.True(main.Navigation.IsInspectorOpen);
            Assert.Equal("feed", editor.SelectedStep.Id);
            Assert.Equal("done", editor.SelectedStepDraft!.NextStepId);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning);
            Capture(window, "graph-same-source-draft-work-area");
            return true;
        }
        finally { editor.DiscardStepDraft(); CloseFixtureWindow(window); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RankBranchSiblingsShareARankAndJoinAfterBothPathsWithoutChangingTheProject(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateBranchLayoutProject();
        if (!fullArea) project.Sequences[0].Steps[1].Name = "후공정 정렬 검사 장비의 판정 결과와 목적지 연결을 확인하는 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessSceneVisible = false;
        string before = JsonSerializer.Serialize(project);
        var window = Show(main, fullArea);
        try
        {
            var editor = main.SequenceEditor;
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            var overview = Descendants(view).OfType<SequenceTaskOverviewView>().Single();
            foreach (bool summary in new[] { true, false })
            {
                view.ProcessSummaryToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, summary);
                Pump(window);
                Assert.Equal(summary, editor.IsProcessSummary);
                AssertFlowGeometry(overview);
                var boxes = Descendants(overview).OfType<Border>().Where(card => card.Name == "FlowNodeCard")
                    .ToDictionary(card => ((SequenceTaskGroupViewModel)card.DataContext).FirstStep.Id, card => card.TransformToAncestor(overview).TransformBounds(new Rect(card.RenderSize)));
                if (summary)
                {
                    Assert.True(boxes["prepare"].Right < boxes["inspection"].Left);
                    Assert.Equal(boxes["accept"].Left, boxes["reject"].Left, 1);
                    Assert.True(boxes["accept"].Bottom < boxes["reject"].Top);
                    Assert.True(boxes["inspection"].Right < boxes["accept"].Left && boxes["accept"].Right < boxes["done"].Left);
                }
                else
                {
                    Assert.True(boxes["prepare"].Bottom < boxes["inspection"].Top);
                    Assert.Equal(boxes["accept"].Top, boxes["reject"].Top, 1);
                    Assert.True(boxes["accept"].Right < boxes["reject"].Left);
                    Assert.True(boxes["inspection"].Bottom < boxes["accept"].Top && Math.Max(boxes["accept"].Bottom, boxes["reject"].Bottom) < boxes["done"].Top);
                }
                foreach (var block in Descendants(overview).OfType<TextBlock>().Where(block => block.IsVisible && block.ActualWidth > 0 && block.ActualHeight > 0))
                {
                    block.Measure(new Size(block.ActualWidth, double.PositiveInfinity));
                    Assert.True(block.DesiredSize.Height <= block.ActualHeight + block.Margin.Top + block.Margin.Bottom + 1, block.Text);
                }
                var folder = Path.Combine(TestStorage.RootPath, "sequence-flow");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"rank-metrics-{language}-{fullArea}-{summary}.json"), JsonSerializer.Serialize(boxes));
            }
            Invoke(view.FlowZoomInButton); Pump(window);
            Assert.Equal(1.1, overview.FlowScale.ScaleX);
            AssertFlowGeometry(overview);
            Invoke(view.FlowZoomResetButton); Pump(window);
            view.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, "reject");
            Invoke(view.FindNextStepButton); Pump(window);
            Assert.Equal("reject", editor.SelectedStep!.Id);
            var selected = Descendants(overview).OfType<Border>().Single(card => card.Name == "FlowNodeCard" && card.Tag is true);
            Rect visible = selected.TransformToAncestor(overview.FlowViewport).TransformBounds(new Rect(selected.RenderSize));
            Assert.True(new Rect(overview.FlowViewport.RenderSize).Contains(visible));
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || editor.HasPendingStepDraft);
            return true;
        }
        catch { Capture(window, $"rank-failure-{language}-{fullArea}"); throw; }
        finally { main.SequenceEditor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData("backward", true)]
    [InlineData("backward", false)]
    [InlineData("self", true)]
    [InlineData("self", false)]
    [InlineData("invalid", true)]
    [InlineData("invalid", false)]
    [InlineData("unreachable", true)]
    [InlineData("unreachable", false)]
    [InlineData("duplicate", false)]
    [InlineData("nonterminal-last", false)]
    [InlineData("empty", true)]
    public Task RankLayoutKeepsBackwardSelfInvalidAndEmptyInputsFiniteAndReloadRecovers(string boundary, bool summary) => ui.InvokeAsync(() =>
    {
        var project = CreateBranchLayoutProject();
        var steps = project.Sequences[0].Steps;
        switch (boundary)
        {
            case "backward": steps[3].NextStepId = "prepare"; break;
            case "self": steps[3].NextStepId = "reject"; break;
            case "invalid": steps[1].NextStepId = "missing"; break;
            case "unreachable": steps[0].NextStepId = "done"; break;
            case "duplicate": steps[2].Id = "reject"; break;
            case "nonterminal-last": steps.RemoveAt(4); break;
            case "empty": steps.Clear(); break;
        }
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        editor.IsProcessSummary = summary;
        var overview = new SequenceTaskOverviewView { DataContext = editor };
        var window = new Window { Content = overview, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        try
        {
            window.Show(); Pump(window);
            AssertFlowGeometry(overview);
            Assert.Equal(steps.Count, Descendants(overview).OfType<Border>().Count(card => card.Name == "FlowNodeCard"));
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(editor.HasPendingStepDraft);
            if (boundary is "backward" or "self")
            {
                Assert.Empty(editor.ValidationIssues);
                var route = editor.ProcessLanes[0].Routes.Single(route => route.Source.Id == "reject");
                Assert.Equal(boundary == "backward" ? "prepare" : "reject", route.TargetId);
                Assert.Contains(Descendants(overview).OfType<Polyline>(), line => ReferenceEquals(line.Tag, route));
            }
            if (boundary is "invalid" or "duplicate" or "nonterminal-last") Assert.NotEmpty(editor.ValidationIssues);
            Capture(window, $"rank-boundary-{boundary}-{summary}");
            editor.Load(CreateBranchLayoutProject()); Pump(window);
            AssertFlowGeometry(overview);
            Assert.Empty(editor.ValidationIssues);
            Assert.Equal(5, Descendants(overview).OfType<Border>().Count(card => card.Name == "FlowNodeCard"));
            return true;
        }
        finally { editor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); editor.Dispose(); }
    });

    [Fact]
    public Task RankSummaryRemeasuresExpandedGroupsAndLinearRowsAdaptOnResize() => ui.InvokeAsync(() =>
    {
        var project = CreateMultiUnitProject();
        project.Sequences[0].Steps[3].Name = "후공정 정렬 검사 장비의 두 번째 촬영 위치와 장치 상태 확인을 위한 매우 긴 공정 설정 이름";
        project.Sequences[1].Steps = [
            new() { Id = "a", Name = "첫 준비", Action = SequenceStepAction.Delay, Parameter = "0.1" },
            new() { Id = "b", Name = "둘째 준비", Action = SequenceStepAction.Delay, Parameter = "0.1" },
            new() { Id = "c", Name = "셋째 준비", Action = SequenceStepAction.Delay, Parameter = "0.1" },
            new() { Id = "d", Name = "넷째 준비", Action = SequenceStepAction.Delay, Parameter = "0.1" },
            new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }
        ];
        string before = JsonSerializer.Serialize(project);
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessSceneVisible = false;
        var window = Show(main, fullArea: true);
        try
        {
            var editor = main.SequenceEditor;
            var overview = Descendants(window).OfType<SequenceTaskOverviewView>().Single();
            double folded = overview.FlowViewport.ExtentHeight;
            foreach (var group in editor.ProcessLanes[0].Groups.Where(group => group.IsGroup)) group.IsExpanded = true;
            Pump(window);
            AssertFlowGeometry(overview);
            Assert.True(overview.FlowViewport.ExtentHeight > folded);
            Capture(window, "rank-expanded-groups-work-area");
            editor.SelectSequence("second-sequence"); Pump(window);
            AssertFlowGeometry(overview);
            int wideRows = Rows();
            window.Width = 1280; window.Height = 760; Pump(window);
            AssertFlowGeometry(overview);
            Assert.True(Rows() > wideRows);
            editor.IsProcessAllUnits = true; Pump(window);
            AssertFlowGeometry(overview);
            Assert.Equal(4, editor.ProcessLanes.Count);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || editor.HasPendingStepDraft);
            Capture(window, "rank-all-lanes-compact");
            return true;

            int Rows() => Descendants(overview).OfType<Border>().Where(card => card.Name == "FlowNodeCard")
                .Select(card => Math.Round(card.TransformToAncestor(overview).Transform(new Point()).Y, 1)).Distinct().Count();
        }
        finally { main.SequenceEditor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); }
    });

    [Fact]
    public Task RankSelectedCardUsesActualScrollContentViewportAfterLayout() => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
        var project = CreateBranchLayoutProject();
        project.Sequences[0].Steps[1].Name = "후공정 정렬 검사 장비의 판정 결과와 목적지 연결을 확인하는 매우 긴 공정 설정 이름";
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessSceneVisible = false;
        var window = Show(main, fullArea: false);
        try
        {
            main.SequenceEditor.IsProcessSummary = false;
            Pump(window);
            var overview = Descendants(window).OfType<SequenceTaskOverviewView>().Single();
            var presenter = Descendants(overview.FlowViewport).OfType<ScrollContentPresenter>().Single(part => ReferenceEquals(part.TemplatedParent, overview.FlowViewport));
            var card = Descendants(overview).OfType<Border>().Single(card => card.Name == "FlowNodeCard" && card.Tag is true);
            Rect actual = card.TransformToAncestor(presenter).TransformBounds(new Rect(card.RenderSize));
            Capture(window, "rank-selected-viewport-compact");
            File.WriteAllText(Path.Combine(TestStorage.RootPath, "sequence-flow", "selected-viewport.json"), JsonSerializer.Serialize(new
            {
                actual = actual.ToString(), viewport = new Rect(presenter.RenderSize).ToString(),
                scrollViewer = card.TransformToAncestor(overview.FlowViewport).TransformBounds(new Rect(card.RenderSize)).ToString(),
                padding = overview.FlowViewport.Padding.ToString(), presenter.Name,
                namedPartFound = overview.FlowViewport.Template.FindName("PART_ScrollContentPresenter", overview.FlowViewport) is not null
            }));
            // Layout arithmetic may differ at the edge by floating-point rounding; 1e-6 DIP cannot hide a rendered pixel.
            var viewportBounds = new Rect(presenter.RenderSize);
            viewportBounds.Inflate(1e-6, 1e-6);
            Assert.True(viewportBounds.Contains(actual), $"Selected card outside actual content viewport: {actual} / {presenter.RenderSize}");
            var editorView = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            var search = new List<object>();
            foreach (string target in new[] { "reject", "done", "prepare" })
            {
                editorView.StepSearchTextBox.SetCurrentValue(TextBox.TextProperty, target);
                Invoke(editorView.FindNextStepButton); Pump(window);
                Assert.Equal(target, main.SequenceEditor.SelectedStep!.Id);
                card = Descendants(overview).OfType<Border>().Single(item => item.Name == "FlowNodeCard" && item.Tag is true);
                actual = card.TransformToAncestor(presenter).TransformBounds(new Rect(card.RenderSize));
                search.Add(new { target, actual = actual.ToString(), viewport = presenter.RenderSize.ToString(), overview.FlowViewport.HorizontalOffset, overview.FlowViewport.VerticalOffset });
                File.WriteAllText(Path.Combine(TestStorage.RootPath, "sequence-flow", "selected-viewport-search.json"), JsonSerializer.Serialize(search));
                Assert.True(viewportBounds.Contains(actual), $"Selected {target} outside actual content viewport: {actual} / {presenter.RenderSize}");
            }
            Capture(window, "rank-selected-viewport-search-return-compact");
            Assert.False(main.SequenceEditor.HasPendingStepDraft || main.HasUnsavedChanges || main.IsRunning);
            return true;
        }
        finally { main.SequenceEditor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task CaptureOtherLaneFoldWorkspace(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateFoldStateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.IsProcessSceneVisible = false;
        main.SequenceEditor.IsProcessAllUnits = true;
        string before = JsonSerializer.Serialize(project);
        var selected = main.SequenceEditor.SelectedStep;
        var window = Show(main, fullArea);
        try
        {
            var expander = FoldExpander(window, "second-sequence", "capture-1");
            RevealFoldCard(window, expander);
            Assert.IsAssignableFrom<IExpandCollapseProvider>(new ExpanderAutomationPeer(expander).GetPattern(PatternInterface.ExpandCollapse)).Expand();
            Pump(window);
            Assert.True(((SequenceTaskGroupViewModel)expander.DataContext).IsExpanded);
            CaptureLaneFolds(window, $"lane-fold-open-{language}-{(fullArea ? "work-area" : "compact")}");
            main.SequenceEditor.IsProcessSummary = false; Pump(window);
            main.SequenceEditor.IsProcessSummary = true; Pump(window);
            expander = FoldExpander(window, "second-sequence", "capture-1");
            RevealFoldCard(window, expander);
            CaptureLaneFolds(window, $"lane-fold-returned-{language}-{(fullArea ? "work-area" : "compact")}");
            Assert.Same(selected, main.SequenceEditor.SelectedStep);
            Assert.Equal("sequence", main.SequenceEditor.SelectedSequence!.Id);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || main.SequenceEditor.HasPendingStepDraft);
            return true;
        }
        finally { main.SequenceEditor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Fact]
    public Task OtherLaneExpansionSurvivesSummaryRebuildWithoutChangingSelectionOrDefinition() => ui.InvokeAsync(() =>
    {
        var project = CreateFoldStateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project);
            editor.IsProcessAllUnits = true;
            var selected = editor.SelectedStep;
            string before = JsonSerializer.Serialize(project);
            var other = editor.ProcessLanes.Single(lane => lane.Definition?.Id == "second-sequence").Groups.Single(group => group.Steps.Any(step => step.Id == "capture-1"));
            other.IsExpanded = true;
            editor.IsProcessSummary = false;
            editor.IsProcessSummary = true;
            var restored = editor.ProcessLanes.Single(lane => lane.Definition?.Id == "second-sequence").Groups.Single(group => group.Steps.Any(step => step.Id == "capture-1"));
            Assert.True(restored.IsExpanded);
            Assert.NotSame(other, restored);
            Assert.Same(selected, editor.SelectedStep);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(editor.HasPendingStepDraft);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedOtherLaneFoldSurvivesViewRebuildAndCollapseWithoutChangingCommands(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateFoldStateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor;
        editor.IsProcessAllUnits = true; editor.IsProcessSceneVisible = false;
        string before = JsonSerializer.Serialize(project);
        var selected = editor.SelectedStep;
        var window = Show(main, fullArea);
        try
        {
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            var first = FoldExpander(window, "sequence", "capture-1");
            var other = FoldExpander(window, "second-sequence", "capture-1");
            Assert.False(first.IsExpanded || other.IsExpanded);
            Assert.IsAssignableFrom<IExpandCollapseProvider>(new ExpanderAutomationPeer(other).GetPattern(PatternInterface.ExpandCollapse)).Expand();
            Pump(window); WaitForFoldContent(window, other);
            Assert.True(((SequenceTaskGroupViewModel)other.DataContext).IsExpanded);
            view.ProcessSummaryToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Pump(window);
            view.ProcessSummaryToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(window);
            other = FoldExpander(window, "second-sequence", "capture-1");
            Assert.True(other.IsExpanded);
            Assert.False(FoldExpander(window, "sequence", "capture-1").IsExpanded);
            Assert.Same(editor.SelectOverviewStepCommand, Descendants(other).OfType<Button>().First(button => button.CommandParameter is SequenceStepEditorItem).Command);
            RevealFoldCard(window, other); WaitForFoldContent(window, other);
            CaptureLaneFolds(window, $"lane-fold-preserved-{language}-{(fullArea ? "work-area" : "compact")}");
            Assert.IsAssignableFrom<IExpandCollapseProvider>(new ExpanderAutomationPeer(other).GetPattern(PatternInterface.ExpandCollapse)).Collapse();
            Pump(window);
            Assert.False(((SequenceTaskGroupViewModel)other.DataContext).IsExpanded);
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Pump(window);
            view.AllProcessUnitsToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(window);
            Assert.False(FoldExpander(window, "second-sequence", "capture-1").IsExpanded);
            FoldGroup(editor, "second-sequence", "capture-1").IsExpanded = true; Pump(window);
            other = FoldExpander(window, "second-sequence", "capture-1");
            Assert.True(other.IsExpanded);
            WaitForFoldContent(window, other);
            Assert.Same(selected, editor.SelectedStep);
            Assert.Equal("sequence", editor.SelectedSequence!.Id);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || editor.HasPendingStepDraft);
            return true;
        }
        catch { CaptureLaneFolds(window, $"lane-fold-failure-{language}-{fullArea}"); throw; }
        finally { editor.DiscardStepDraftCommand.Execute(null); CloseFixtureWindow(window); OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LaneFoldCacheSeparatesSequencesAndClosedGroupsAcrossRefresh(bool readOnly) => ui.InvokeAsync(() =>
    {
        var project = CreateFoldStateProject();
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        int changed = 0; editor.DefinitionChanged += (_, _) => changed++;
        try
        {
            editor.Load(project); editor.IsProcessAllUnits = true;
            editor.IsEditable = !readOnly;
            FoldGroup(editor, "second-sequence", "capture-1").IsExpanded = true;
            FoldGroup(editor, "second-sequence", "capture-2").IsExpanded = false;
            foreach (string transition in new[] { "summary", "scope", "targets", "language" })
            {
                switch (transition)
                {
                    case "summary": editor.IsProcessSummary = false; editor.IsProcessSummary = true; break;
                    case "scope": editor.IsProcessAllUnits = false; editor.IsProcessAllUnits = true; break;
                    case "targets": editor.RefreshAuthoringTargets(); break;
                    case "language": editor.RefreshLocalization(); break;
                }
                Assert.True(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded, transition);
                Assert.False(FoldGroup(editor, "second-sequence", "capture-2").IsExpanded);
                Assert.False(FoldGroup(editor, "sequence", "capture-1").IsExpanded);
            }
            FoldGroup(editor, "second-sequence", "capture-1").IsExpanded = false;
            editor.RefreshLocalization();
            Assert.False(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded);
            Assert.Equal(0, changed);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            Assert.False(editor.HasPendingStepDraft);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LaneFoldLoadClearsCacheAndStaleGroupsCannotRepopulateIt(bool sameInstance) => ui.InvokeAsync(() =>
    {
        var project = CreateFoldStateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.IsProcessAllUnits = true;
            FoldGroup(editor, "second-sequence", "capture-1").IsExpanded = true;
            var stale = editor.ProcessLanes.SelectMany(lane => lane.Groups).ToArray();
            var target = sameInstance ? project : JsonSerializer.Deserialize<MachineProjectDocument>(JsonSerializer.Serialize(project))!;
            string before = JsonSerializer.Serialize(target);
            editor.Load(target);
            foreach (var group in stale) group.IsExpanded = true;
            editor.IsProcessAllUnits = true;
            Assert.False(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded);
            Assert.False(FoldGroup(editor, "sequence", "capture-1").IsExpanded);
            editor.IsProcessSummary = false; editor.IsProcessSummary = true;
            Assert.False(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded);
            Assert.Equal(before, JsonSerializer.Serialize(target));
            editor.Dispose();
            foreach (var group in stale) group.IsExpanded = false;
            editor.IsProcessAllUnits = true;
            Assert.Empty(editor.ProcessLanes);
            Assert.False(editor.SelectOverviewStepCommand.CanExecute(stale[0].FirstStep));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task LaneFoldChangedMembershipDoesNotRestoreIncompatibleStateOrListenToStaleGroups() => ui.InvokeAsync(() =>
    {
        var project = CreateFoldStateProject();
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.IsProcessAllUnits = true;
            var stale = FoldGroup(editor, "second-sequence", "capture-1");
            string oldKey = stale.Key; stale.IsExpanded = true;
            project.Axes.Add(new() { Id = "other-axis", Name = "다른 촬영 축" });
            var camera = project.Devices[0].Camera!;
            camera.Geometry = camera.Geometry! with { AxisId = "other-axis" };
            string before = JsonSerializer.Serialize(project);
            editor.RefreshAuthoringTargets();
            var current = FoldGroup(editor, "second-sequence", "move-1");
            Assert.NotEqual(oldKey, current.Key);
            Assert.Equal(2, current.Steps.Count);
            Assert.False(current.IsExpanded);
            stale.IsExpanded = true;
            editor.IsProcessSummary = false; editor.IsProcessSummary = true;
            Assert.False(FoldGroup(editor, "second-sequence", "move-1").IsExpanded);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task LaneFoldKeepsMainDraftAdmissionAndSelectedStepReveal() => ui.InvokeAsync(() =>
    {
        var project = CreateFoldStateProject();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; editor.IsProcessAllUnits = true;
        string before = JsonSerializer.Serialize(project);
        FoldGroup(editor, "second-sequence", "capture-1").IsExpanded = true;
        editor.SelectedStepDraft!.Parameter = "0.65";
        editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
        editor.SelectOverviewStepCommand.Execute(FoldGroup(editor, "second-sequence", "capture-1").FirstStep);
        Assert.Equal("sequence", editor.SelectedSequence!.Id);
        Assert.True(editor.HasPendingStepDraft);
        Assert.True(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded);
        editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
        editor.SelectOverviewStepCommand.Execute(FoldGroup(editor, "second-sequence", "capture-1").FirstStep);
        Assert.Equal("second-sequence", editor.SelectedSequence!.Id);
        Assert.Equal("move-1", editor.SelectedStep!.Id);
        Assert.True(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded);
        Assert.False(FoldGroup(editor, "sequence", "capture-1").IsExpanded);
        Assert.False(editor.HasPendingStepDraft || main.HasUnsavedChanges || main.IsRunning);
        Assert.Equal(before, JsonSerializer.Serialize(project));
        return true;
    });

    [Fact]
    public Task LaneFoldSameFirstStepIdsDoNotReopenThePreviousSequenceDuringSelectionRepopulation() => ui.InvokeAsync(() =>
    {
        var project = CreateFoldStateProject();
        foreach (var sequence in project.Sequences.Take(2)) sequence.Steps.RemoveAt(0);
        string before = JsonSerializer.Serialize(project);
        var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.IsProcessAllUnits = true;
            FoldGroup(editor, "sequence", "capture-1").IsExpanded = false;
            FoldGroup(editor, "second-sequence", "capture-1").IsExpanded = false;
            editor.SelectSequence("second-sequence");
            Assert.True(FoldGroup(editor, "second-sequence", "capture-1").IsExpanded);
            Assert.False(FoldGroup(editor, "sequence", "capture-1").IsExpanded);
            editor.IsProcessSummary = false; editor.IsProcessSummary = true;
            Assert.False(FoldGroup(editor, "sequence", "capture-1").IsExpanded);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    private static SequenceTaskGroupViewModel FoldGroup(SequenceEditorViewModel editor, string sequenceId, string memberId) => editor.ProcessLanes
        .Single(lane => lane.Definition?.Id == sequenceId).Groups.Single(group => group.Steps.Any(step => step.Id == memberId));

    private static void RevealFoldCard(Window window, Expander expander)
    {
        var overview = Descendants(window).OfType<SequenceTaskOverviewView>().Single();
        for (DependencyObject? item = expander; item is not null; item = VisualTreeHelper.GetParent(item))
        {
            if (item is not Border { Name: "FlowNodeCard" } card) continue;
            card.BringIntoView(); Pump(window);
            Rect bounds = card.TransformToAncestor(overview.FlowViewport).TransformBounds(new Rect(card.RenderSize));
            overview.FlowViewport.ScrollToHorizontalOffset(overview.FlowViewport.HorizontalOffset + bounds.Left + bounds.Width / 2 - overview.FlowViewport.ViewportWidth / 2);
            Pump(window);
            return;
        }
    }

    private static void CloseFixtureWindow(Window window, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var main = window.DataContext as MainViewModel;
        bool? dirtyBeforeCleanup = main?.HasUnsavedChanges;
        // fixture는 기존 using/명시 Dispose가 해제한다. 정리에서 비동기 Shell 종료 승인을 시작하지 않는다.
        window.DataContext = null;
        window.Close();
        bool retained = Application.Current.Windows.Cast<Window>().Contains(window);
        Directory.CreateDirectory(Path.Combine(TestStorage.RootPath, "sequence-flow"));
        File.AppendAllText(Path.Combine(TestStorage.RootPath, "sequence-flow", "fixture-window-cleanup.jsonl"), JsonSerializer.Serialize(new
        {
            testName, dirtyBeforeCleanup, ownWindowRetained = retained, window.IsVisible,
            remainingWindows = Application.Current.Windows.Cast<Window>().Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray()
        }) + Environment.NewLine);
        Assert.False(retained || window.IsVisible, "The owned fixture window must be removed before ViewModel disposal.");
    }

    private static void WaitForFoldContent(Window window, Expander expander)
    {
        bool Ready()
        {
            var buttons = Descendants(expander).OfType<Button>().Where(button => button.CommandParameter is SequenceStepEditorItem).ToArray();
            if (buttons.Length != ((SequenceTaskGroupViewModel)expander.DataContext).Steps.Count) return false;
            foreach (var button in buttons)
            {
                Rect bounds = button.TransformToAncestor(expander).TransformBounds(new Rect(button.RenderSize));
                var outer = new Rect(expander.RenderSize); outer.Inflate(1e-6, 1e-6);
                if (!button.IsVisible || button.ActualHeight <= 0 || !outer.Contains(bounds) || bounds.Height < button.ActualHeight - 0.01) return false;
                for (DependencyObject? item = button; item is not null && item != expander; item = VisualTreeHelper.GetParent(item))
                {
                    if (item is UIElement element && element.Opacity < 0.99) return false;
                    if (item is not Visual visual || VisualTreeHelper.GetClip(visual) is not { } clip) continue;
                    if (clip.Bounds.IsEmpty) return false;
                    var clipBounds = clip.Bounds; clipBounds.Inflate(1e-6, 1e-6);
                    if (!clipBounds.Contains(button.TransformToAncestor(visual).TransformBounds(new Rect(button.RenderSize)))) return false;
                }
            }
            return true;
        }
        if (Ready()) return;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        int ticks = 0;
        EventHandler handler = (_, _) => { window.UpdateLayout(); if (Ready() || ++ticks >= 125) frame.Continue = false; };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(Ready(), "Expanded step controls did not finish rendering; native input acceptance is not implied.");
    }

    private static void AssertFlowGeometry(SequenceTaskOverviewView overview)
    {
        foreach (var surface in Descendants(overview).OfType<Grid>().Where(grid => grid.Name == "FlowLaneSurface" && grid.IsVisible))
        {
            var lane = (SequenceTaskLaneViewModel)surface.DataContext;
            var cards = Descendants(surface).OfType<Border>().Where(card => card.Name == "FlowNodeCard")
                .Select(card => card.TransformToAncestor(surface).TransformBounds(new Rect(card.RenderSize))).ToArray();
            for (int i = 0; i < cards.Length; i++)
            {
                Assert.True(new Rect(surface.RenderSize).Contains(cards[i]));
                for (int j = i + 1; j < cards.Length; j++) Assert.False(cards[i].IntersectsWith(cards[j]), $"Cards overlap: {cards[i]} / {cards[j]}");
            }
            var wires = Descendants(surface).OfType<Polyline>().Where(line => line.Tag is SequenceTaskRoute).ToArray();
            foreach (var route in lane.Routes.Where(route => route.IsValid)) Assert.Single(wires.Where(line => ReferenceEquals(line.Tag, route)));
            foreach (var wire in wires)
            for (int i = 1; i < wire.Points.Count; i++)
            {
                var segment = new Rect(wire.Points[i - 1], wire.Points[i]);
                segment.Inflate(0.1, 0.1);
                foreach (var card in cards)
                {
                    var interior = card; interior.Inflate(-0.5, -0.5);
                    Assert.False(interior.IntersectsWith(segment), $"Route crosses a card: {((SequenceTaskRoute)wire.Tag).Source.Id} {((SequenceTaskRoute)wire.Tag).PropertyName}: {segment} / {interior}");
                }
            }
        }
    }

    private static IEnumerable<Button> TableRows(SequenceEditorView view) => Descendants(view.AllUnitsTableLanes).OfType<Button>().Where(button => button.Name == "AllUnitsTableStep");

    private static MachineProjectDocument CreateFoldStateProject()
    {
        var project = CreateMultiUnitProject();
        project.Sequences[1].Steps = JsonSerializer.Deserialize<List<SequenceStepDefinition>>(JsonSerializer.Serialize(project.Sequences[0].Steps))!;
        foreach (var step in project.Sequences[1].Steps) step.Name = "둘째 유닛 " + step.Name;
        return project;
    }

    private static Expander FoldExpander(Window window, string sequenceId, string memberId) => Descendants(window).OfType<Expander>()
        .Single(expander => expander.DataContext is SequenceTaskGroupViewModel group && group.FirstStep.SequenceId == sequenceId && group.Steps.Any(step => step.Id == memberId));

    private static void CaptureLaneFolds(Window window, string name)
    {
        foreach (var expander in Descendants(window).OfType<Expander>().Where(expander => expander.DataContext is SequenceTaskGroupViewModel { IsGroup: true } && expander.IsExpanded).ToArray()) WaitForFoldContent(window, expander);
        Capture(window, name);
        var states = Descendants(window).OfType<Expander>().Where(expander => expander.DataContext is SequenceTaskGroupViewModel { IsGroup: true })
            .Select(expander => new { sequence = ((SequenceTaskGroupViewModel)expander.DataContext).FirstStep.SequenceId,
                key = ((SequenceTaskGroupViewModel)expander.DataContext).Key, modelExpanded = ((SequenceTaskGroupViewModel)expander.DataContext).IsExpanded,
                expander.IsExpanded, expander.IsVisible, expander.ActualWidth, expander.ActualHeight }).ToArray();
        File.WriteAllText(Path.Combine(TestStorage.RootPath, "sequence-flow", name + "-state.json"), JsonSerializer.Serialize(states));
    }

    private static MachineProjectDocument CreateBranchLayoutProject()
    {
        var project = CreateMultiUnitProject();
        project.Sequences[0].Steps = [
            new() { Id = "prepare", Name = "판정 준비", Action = SequenceStepAction.Delay, Parameter = "0.1", NextStepId = "inspection" },
            new() { Id = "inspection", Name = "검사 결과 분기", Action = SequenceStepAction.WaitVisionResult, TargetId = "camera", TimeoutMs = 5000, NextStepId = "accept", FailureStepId = "reject", ErrorStepId = "reject" },
            new() { Id = "accept", Name = "OK 제품 배출", Action = SequenceStepAction.Delay, Parameter = "0.2", NextStepId = "done" },
            new() { Id = "reject", Name = "NG 제품 분리", Action = SequenceStepAction.Delay, Parameter = "0.2", NextStepId = "done" },
            new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }
        ];
        return project;
    }

    internal static MachineProjectDocument CreateMultiUnitProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Stations = [new MachineStationDefinition { Id = "station", Name = "공정 장비", Units = [new() { Id = "first", Name = "첫 유닛" }, new() { Id = "second", Name = "둘째 유닛" }, new() { Id = "empty", Name = "빈 유닛" }] }];
        project.Sequences[0].UnitId = "first";
        project.Sequences[0].Steps[0].Name = "needle alpha";
        project.Sequences[0].Steps[7].NextStepId = "return";
        project.Sequences[0].Steps[7].ErrorStepId = "done";
        project.Sequences.Add(new SequenceDefinition { Id = "second-sequence", Name = "두 번째 공정", UnitId = "second", Steps = [new() { Id = "feed", Name = "needle beta", Action = SequenceStepAction.Delay, Parameter = "0.2" }, new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }] });
        project.Sequences.Add(new SequenceDefinition { Id = "common", Name = "공통 시퀀스", Steps = [new() { Id = "feed", Name = "needle common", Action = SequenceStepAction.Delay, Parameter = "0.3" }, new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }] });
        return project;
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray();
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX;
            window.Top = area[1] / dpi.DpiScaleY;
            window.Width = area[2] / dpi.DpiScaleX;
            window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window);
        return window;
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string name)
    {
        Pump(window);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(monitor.WindowIntersectsMonitor && monitor.WindowContainedByMonitor);
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        var folder = Path.Combine(TestStorage.RootPath, "sequence-flow");
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(folder, name + ".png"))) encoder.Save(output);
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new
        {
            monitor, width, height, nativeDpi = GetDpiForWindow(new WindowInteropHelper(window).Handle), wpfDpi = dpi.PixelsPerInchX,
            method = "In-process WPF RenderTargetBitmap; bound controls, no native input qualification", crop = "none",
            tables = Descendants(window).OfType<SequenceEditorView>().Where(view => view.IsVisible && view.AllUnitsTableViewport.IsVisible).Select(view => new
            {
                view.AllUnitsTableViewport.VerticalOffset, view.AllUnitsTableViewport.ViewportHeight,
                view.AllUnitsTableViewport.HorizontalOffset, view.AllUnitsTableViewport.ViewportWidth,
                lanes = view.AllUnitsTableLanes.Items.OfType<SequenceTaskLaneViewModel>().Select(lane => new { lane.Name, lane.SequenceText, count = lane.Steps.Count, lane.IsEmpty }).ToArray(),
                rows = TableRows(view).Select(row => new
                {
                    step = ((SequenceStepEditorItem)row.DataContext).Id, sequence = ((SequenceStepEditorItem)row.DataContext).SequenceId,
                    row.IsEnabled, selected = row.Tag, status = ((SequenceStepEditorItem)row.DataContext).StepListStatusText,
                    bounds = row.TransformToAncestor(view.AllUnitsTableViewport).TransformBounds(new Rect(row.RenderSize)).ToString()
                }).ToArray()
            }).ToArray(),
            geometry = Descendants(window).OfType<Canvas>().Where(canvas => canvas.Name == "FlowWires").Select(canvas => canvas.Children.OfType<Polyline>().Select(line => line.Points.ToString()).ToArray()).ToArray(),
            flow = Descendants(window).OfType<SequenceTaskOverviewView>().Select(view => new
            {
                view.IsVisible, view.IsLoaded, view.Tag, view.LaneWidth,
                view.FlowViewport.HorizontalOffset, view.FlowViewport.ExtentWidth, view.FlowViewport.ViewportWidth,
                view.FlowViewport.VerticalOffset, view.FlowViewport.ExtentHeight, view.FlowViewport.ViewportHeight,
                cards = Descendants(view).OfType<Border>().Where(card => card.Name == "FlowNodeCard" && card.Tag is true).Select(card => new
                {
                    card.IsLoaded, card.IsVisible,
                    selected = ((SequenceTaskGroupViewModel)card.DataContext).FirstStep.SequenceId,
                    rect = card.TransformToAncestor(view.FlowViewport).TransformBounds(new Rect(card.RenderSize)).ToString()
                }).ToArray()
            }).ToArray()
        }));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
