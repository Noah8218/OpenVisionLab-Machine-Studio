using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Converter;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40SimulationDiagnosticsTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task CurrentSourceIsCapturedWithoutChangingTheModel(bool fullArea, OpenVisionLanguage language) => WithWindow(fullArea, language, async (main, window, project) =>
    {
        await StartPaused(main);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        var before = State(main, project);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var rows = Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows"));
        Assert.Equal(2, rows.Items.Count);
        Assert.All(rows.Items.Cast<SimulationSequenceObservation>(), row => Assert.True(row.CanOpenStep));
        Capture(window, "source-" + language + "-" + fullArea, main, project);
        Assert.Equal(before, State(main, project));
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task DiagnosticsMoveWithoutReplacingTheViewOrReducingTheWideSceneHeight(bool fullArea, OpenVisionLanguage language) => WithWindow(fullArea, language, async (main, window, project) =>
    {
        await StartPaused(main);
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var viewport = Assert.Single(Descendants(window).OfType<MachineSceneViewport>());
        var original = Bounds(viewport, window);
        var before = State(main, project);
        var toggle = Assert.IsType<CheckBox>(view.FindName("SimulationDiagnosticsToggle"));
        Toggle(toggle);
        await Stable(window);
        Assert.True(main.Navigation.IsSimulationDiagnosticsExpanded);
        Assert.Same(main, view.DataContext);
        AssertPlacement(main, window, view, viewport);
        if (view.IsSidebar) Assert.True(viewport.ActualHeight >= original.Height - 2, $"Expanded scene {viewport.ActualHeight} was lower than collapsed scene {original.Height}.");
        AssertInside(window, toggle);
        AssertInside(window, Assert.IsType<Button>(view.FindName("SimulationRunHistoryButton")));
        Capture(window, "layout-" + language + "-" + fullArea, main, project);
        Toggle(toggle);
        await Stable(window);
        Assert.False(view.IsSidebar || main.Navigation.IsSimulationDiagnosticsExpanded);
        Assert.InRange(Math.Abs(viewport.ActualHeight - original.Height), 0, 2);
        Assert.InRange(Math.Abs(viewport.ActualWidth - original.Width), 0, 2);
        Toggle(toggle);
        Place(window, !fullArea);
        await Stable(window);
        Assert.Same(view, Assert.Single(Descendants(window).OfType<SimulationObservationView>()));
        AssertPlacement(main, window, view, viewport);
        Place(window, fullArea);
        await Stable(window);
        AssertPlacement(main, window, view, viewport);
        Assert.Equal(before, State(main, project));
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task ActualUnitIdsSelectReadOnlyScopeAndWholeViewRecoversForEqualNames(bool fullArea, OpenVisionLanguage language) => WithWindow(fullArea, language, async (main, window, project) =>
    {
        await StartPaused(main);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var rows = Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows"));
        var unitCards = Assert.IsType<ItemsControl>(view.FindName("SimulationUnitCards"));
        var before = State(main, project);
        var source = main.RuntimeDebugger.SequenceObservations.ToArray();
        Assert.Equal(new[] { "a", "b" }, source.Select(row => row.UnitId));
        var observe = Descendants(rows).OfType<Button>().Single(button => button.Command == main.ObserveSimulationUnitCommand && Equals(button.CommandParameter, "b"));
        observe.BringIntoView();
        await Stable(window);
        Invoke(observe);
        await Stable(window);
        Assert.Equal("b", main.Layout.ActiveUnitId);
        Assert.False(main.Navigation.IsInspectorOpen);
        Assert.Equal("b", Assert.IsType<SimulationSequenceObservation>(Assert.Single(rows.Items.Cast<object>())).UnitId);
        Assert.Equal("b", Assert.IsType<TextBlock>(view.FindName("SimulationObservationScopeId")).Text);
        Assert.Equal("같은 유닛", Assert.IsType<TextBlock>(view.FindName("SimulationObservationScopeName")).Text.Replace("\u2060", string.Empty));
        Assert.Equal("b", Assert.IsType<EquipmentUnitActivityItem>(Assert.Single(unitCards.Items.Cast<object>())).UnitId);
        var card = Assert.Single(Descendants(unitCards).OfType<Button>());
        foreach (var text in Descendants(card).OfType<TextBlock>()) AssertInside(card, text);
        Assert.Equal(source, main.RuntimeDebugger.SequenceObservations);
        Assert.Equal(before, State(main, project));
        Capture(window, "scope-b-" + language + "-" + fullArea, main, project);
        var whole = Assert.IsType<Button>(view.FindName("SimulationObserveWholeButton"));
        whole.BringIntoView();
        await Stable(window);
        Invoke(whole);
        await Stable(window);
        Assert.Null(main.Layout.ActiveUnitId);
        Assert.Equal(2, rows.Items.Count);
        Assert.Equal(2, unitCards.Items.Count);
        Assert.False(main.Navigation.IsInspectorOpen);
        Assert.Equal(before, State(main, project));
        main.ObserveSimulationUnitCommand.Execute("a");
        await Stable(window);
        Assert.Equal("a", Assert.IsType<SimulationSequenceObservation>(Assert.Single(rows.Items.Cast<object>())).UnitId);
        main.Navigation.ToggleInspectorCommand.Execute(null);
        await Stable(window);
        Assert.True(main.Navigation.IsInspectorOpen);
        Assert.False(view.IsSidebar);
        main.Navigation.ToggleInspectorCommand.Execute(null);
        await Stable(window);
        AssertPlacement(main, window, view, Assert.Single(Descendants(window).OfType<MachineSceneViewport>()));
    }, project =>
    {
        foreach (var unit in project.Stations.SelectMany(station => station.Units)) unit.Name = "같은 유닛";
        foreach (var sequence in project.Sequences) sequence.Name = "같은 공정";
    });

    [Theory]
    [InlineData("replace", OpenVisionLanguage.Korean)]
    [InlineData("modal", OpenVisionLanguage.Korean)]
    [InlineData("replace", OpenVisionLanguage.English)]
    [InlineData("modal", OpenVisionLanguage.English)]
    public Task ScopeAdmissionCancelsRechecksCurrentContextAndRecovers(string boundary, OpenVisionLanguage language) => WithWindow(false, language, async (main, window, project) =>
    {
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        main.SequenceEditor.SelectedStepDraft!.Parameter = "17";
        int prompts = 0;
        main.PlacementDraftPrompt = () => { prompts++; return PlacementDraftDecision.Cancel; };
        var before = State(main, project);
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        Assert.Equal(1, prompts);
        Assert.Null(main.Layout.ActiveUnitId);
        Assert.Equal(before, State(main, project));
        main.PlacementDraftPrompt = () =>
        {
            prompts++;
            if (boundary == "replace")
                project.Stations[0].Units[1] = new MachineUnitDefinition { Id = "b", Name = "교체된 실제 유닛" };
            else
                main.Navigation.OpenTestScenarioCommand.Execute(null);
            return PlacementDraftDecision.Discard;
        };
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        Assert.Equal(2, prompts);
        Assert.Null(main.Layout.ActiveUnitId);
        Assert.False(main.SequenceEditor.HasPendingStepDraft);
        if (boundary == "modal")
        {
            Assert.True(main.Navigation.IsModalOverlayOpen);
            Assert.False(main.ObserveSimulationUnitCommand.CanExecute("b"));
            main.Navigation.CloseTestScenarioCommand.Execute(null);
        }
        var afterExplicitDiscardAndFixtureChange = State(main, project);
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        Assert.Equal("b", main.Layout.ActiveUnitId);
        Assert.False(main.Navigation.IsInspectorOpen);
        Assert.Equal(afterExplicitDiscardAndFixtureChange, State(main, project));
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ReplacingOnlyTheChildDataContextClearsOldRowsAndRestoresItsInheritedOwner(OpenVisionLanguage language) => WithWindow(false, language, async (main, window, project) =>
    {
        await StartPaused(main);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var rows = Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows"));
        var before = State(main, project);
        using var empty = new MainViewModel(new MachineProjectDocument { Id = "other-diagnostics" });
        empty.Navigation.HideStartupChoice();
        empty.Navigation.IsSimulationOverviewWorkspace = true;
        empty.Navigation.IsSimulationDiagnosticsExpanded = true;
        view.DataContext = empty;
        await Stable(window);
        Assert.Empty(rows.Items.Cast<object>());
        Assert.Empty(Assert.IsType<TextBlock>(view.FindName("SimulationObservationScopeId")).Text);
        view.ClearValue(FrameworkElement.DataContextProperty);
        await Stable(window);
        Assert.Same(main, view.DataContext);
        Assert.Equal("b", Assert.IsType<SimulationSequenceObservation>(Assert.Single(rows.Items.Cast<object>())).UnitId);
        Assert.Equal(before, State(main, project));
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LoadingAnotherDefinitionWithTheSameProjectIdClearsTheOldScopeAndRows(OpenVisionLanguage language) => WithWindow(false, language, async (main, window, project) =>
    {
        await StartPaused(main);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var old = main.RuntimeDebugger.SequenceObservations.Single(row => row.UnitId == "b");
        var path = Path.Combine(TestStorage.RootPath, "replacement-" + language + ".ovmachine");
        var replacement = new MachineProjectDocument { Id = project.Id, Name = "새 관찰 프로젝트" };
        File.WriteAllText(path, new ProjectDocumentStore().Serialize(replacement));
        main.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        Assert.True(await main.OpenProjectReplacingCurrentAsync(path));
        main.Navigation.IsSimulationOverviewWorkspace = true;
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        Assert.Null(main.Layout.ActiveUnitId);
        Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows")).Items.Cast<object>());
        Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("SimulationUnitCards")).Items.Cast<object>());
        Assert.False(main.ObserveSimulationUnitCommand.CanExecute("b"));
        Assert.False(main.OpenSimulationObservationStepCommand.CanExecute(old));
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task EmptyUnitGuidanceAndItsExplicitCommandStayReadable(bool fullArea, OpenVisionLanguage language) => WithWindow(fullArea, language, async (main, window, project) =>
    {
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        var before = State(main, project);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows")).Items.Cast<object>());
        Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("SimulationUnitCards")).Items.Cast<object>());
        var button = Descendants(view).OfType<Button>().Single(item => item.Command == main.OpenStationUnitEditorCommand);
        button.BringIntoView();
        await Stable(window);
        AssertInside(window, button);
        AssertInside(Assert.IsType<ScrollViewer>(view.FindName("SimulationDiagnosticsScroll")), button);
        Capture(window, "empty-" + language + "-" + fullArea, main, project);
        Assert.Equal(before, State(main, project));
    }, project => { project.Stations.Clear(); project.Axes.Clear(); project.Layouts.Clear(); project.Sequences.Clear(); });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ADeclaredUnitWithoutAProcessHasItsOwnEmptyGuidance(OpenVisionLanguage language) => WithWindow(false, language, async (main, window, project) =>
    {
        await Wait(() => main.RuntimeDebugger.SequenceObservations.Count == 1);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        var before = State(main, project);
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows")).Items.Cast<object>());
        var hint = Assert.IsType<TextBlock>(view.FindName("SimulationNoProcessesHint"));
        Assert.True(hint.IsVisible);
        Assert.Equal(OpenVisionLanguageService.T("SimObservation.NoScopedProcesses"), hint.Text);
        Assert.Equal("b", Assert.IsType<EquipmentUnitActivityItem>(Assert.Single(Assert.IsType<ItemsControl>(view.FindName("SimulationUnitCards")).Items.Cast<object>())).UnitId);
        Assert.False(main.Navigation.IsInspectorOpen);
        Assert.Equal(before, State(main, project));
    }, project => project.Sequences.RemoveAt(1));

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task PlaybackPresentationHidesCurrentRunValuesAndRestoresThemOnExit(OpenVisionLanguage language) => WithWindow(true, language, async (main, window, project) =>
    {
        await Wait(() => main.SceneSnapshots.Latest is not null);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
        var clock = Assert.IsType<TextBlock>(scene.FindName("SimulationRunSummaryTime"));
        Assert.True(clock.IsVisible);
        var before = State(main, project);
        var snapshot = Assert.IsType<SimulationSnapshot>(main.SceneSnapshots.Latest);
        var step = new RecipeDryRunStepPresentation("sequence-a", "delay", null, "#1", "검사 조건 대기", "tick 0", false, false, false, string.Empty, snapshot);
        main.DryRunPlayback.Show(step, [step]);
        main.Navigation.IsSimulationOverviewWorkspace = true;
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        Assert.True(main.IsDryRunPlaybackActive);
        Assert.False(main.AreSimulationObservationsCurrent);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows")).Visibility);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<ItemsControl>(view.FindName("SimulationUnitCards")).Visibility);
        Assert.False(clock.IsVisible);
        Assert.False(Assert.IsType<TextBlock>(scene.FindName("SimulationRunSummaryRecipe")).IsVisible);
        Assert.True(Assert.IsType<TextBlock>(view.FindName("SimulationPendingDefinitionHint")).IsVisible);
        Assert.Equal(before, State(main, project));
        main.DryRunPlayback.ExitCommand.Execute(null);
        await Stable(window);
        Assert.True(main.AreSimulationObservationsCurrent && clock.IsVisible);
        Assert.Equal(Visibility.Visible, Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows")).Visibility);
        Assert.Equal(before, State(main, project));
    });

    [Fact]
    public Task ScopeCommandsRejectWrongKindsDuplicateIdsOtherCallersAndDispose() => ui.InvokeAsync(() =>
    {
        var project = Project();
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsSimulationOverviewWorkspace = true;
        foreach (var target in new object?[] { null, "", " ", "axis-a", "stage-a", "sequence-a", "station", "A", 1 })
        {
            Assert.False(main.ObserveSimulationUnitCommand.CanExecute(target));
            main.ObserveSimulationUnitCommand.Execute(target);
            Assert.Null(main.Layout.ActiveUnitId);
        }
        project.Stations[0].Units.Add(new MachineUnitDefinition { Id = "a", Name = "중복 ID" });
        Assert.False(main.ObserveSimulationUnitCommand.CanExecute("a"));
        main.ObserveSimulationUnitCommand.Execute("a");
        Assert.Null(main.Layout.ActiveUnitId);
        project.Stations[0].Units.RemoveAt(2);
        main.ObserveSimulationUnitCommand.Execute("a");
        Assert.Equal("a", main.Layout.ActiveUnitId);
        main.Navigation.IsResultsWorkspace = true;
        Assert.False(main.ObserveSimulationUnitCommand.CanExecute("b"));
        Assert.False(main.ObserveWholeSimulationCommand.CanExecute(null));
        main.ObserveSimulationUnitCommand.Execute("b");
        main.ObserveWholeSimulationCommand.Execute(null);
        Assert.Equal("a", main.Layout.ActiveUnitId);
        main.Navigation.IsSimulationOverviewWorkspace = true;
        main.ObserveWholeSimulationCommand.Execute(null);
        Assert.Null(main.Layout.ActiveUnitId);
        main.Dispose();
        Assert.False(main.ObserveSimulationUnitCommand.CanExecute("b"));
        Assert.False(main.ObserveWholeSimulationCommand.CanExecute(null));
        main.ObserveSimulationUnitCommand.Execute("b");
        Assert.Null(main.Layout.ActiveUnitId);
        return true;
    });

    [Fact]
    public Task ScopeProjectionPreservesRowIdentityOrderAndExplicitIds() => ui.InvokeAsync(() =>
    {
        var project = Project();
        var source = SimulationSequenceObservation.Create(project, SimulationSequenceObservationTests.Snapshot(project,
            [SimulationSequenceObservationTests.Execution("sequence-a", "delay"), SimulationSequenceObservationTests.Execution("sequence-b", "delay")]));
        var converter = new SimulationObservationScopeConverter();
        Assert.Same(source, converter.Convert([source, null!], typeof(object), null, CultureInfo.InvariantCulture));
        var scoped = Assert.IsType<SimulationSequenceObservation[]>(converter.Convert([source, "b"], typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Same(source[1], Assert.Single(scoped));
        Assert.Equal(new[] { "a", "b" }, source.Select(row => row.UnitId));
        IReadOnlyList<EquipmentUnitActivityItem> units = new[] { new EquipmentUnitActivityItem("a", "same", "first"), new EquipmentUnitActivityItem("b", "same", "second") };
        Assert.Same(units, converter.Convert([units, null!], typeof(object), null, CultureInfo.InvariantCulture));
        var scopedUnits = Assert.IsType<EquipmentUnitActivityItem[]>(converter.Convert([units, "b"], typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Same(units[1], Assert.Single(scopedUnits));
        Assert.Empty(Assert.IsType<SimulationSequenceObservation[]>(converter.Convert([source, source[0].ProcessText], typeof(object), null, CultureInfo.InvariantCulture)));
        Assert.All(converter.ConvertBack(scoped, [typeof(object), typeof(string)], null, CultureInfo.InvariantCulture), value => Assert.Same(Binding.DoNothing, value));
        return true;
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ScopeProjectionClearsMissingInvalidAndUnresolvedBindingValues(int boundary)
    {
        IReadOnlyList<SimulationSequenceObservation> rows = Array.Empty<SimulationSequenceObservation>();
        object[] values = boundary switch
        {
            0 => [], 1 => ["wrong source", null!], 2 => [rows, DependencyProperty.UnsetValue],
            3 => [rows, " "], _ => [rows, new object()]
        };
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<SimulationSequenceObservation>>(new SimulationObservationScopeConverter().Convert(values, typeof(object), null, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task LongCurrentFieldsAndLastCommandsRemainReachable(bool fullArea, OpenVisionLanguage language) => WithWindow(fullArea, language, async (main, window, project) =>
    {
        await StartPaused(main);
        main.ObserveSimulationUnitCommand.Execute("b");
        await Stable(window);
        var viewport = Assert.Single(Descendants(window).OfType<MachineSceneViewport>());
        var collapsedHeight = viewport.ActualHeight;
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var scroll = Assert.IsType<ScrollViewer>(view.FindName("SimulationDiagnosticsScroll"));
        if (view.IsSidebar) Assert.True(viewport.ActualHeight >= collapsedHeight - 2, $"Long scoped scene height changed from {collapsedHeight} to {viewport.ActualHeight}.");
        var scopeName = Assert.IsType<TextBlock>(view.FindName("SimulationObservationScopeName"));
        Assert.Equal(project.Stations[0].Units[1].Name, scopeName.Text.Replace("\u2060", string.Empty));
        Assert.Equal("b", Assert.IsType<TextBlock>(view.FindName("SimulationObservationScopeId")).Text);
        var title = Assert.IsType<TextBlock>(Assert.Single(Descendants(window).OfType<SceneDocumentView>()).FindName("SimulationSceneTitle"));
        Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
        scopeName.BringIntoView();
        await Stable(window);
        AssertInside(scroll, scopeName);
        Capture(window, "long-scope-" + language + "-" + fullArea, main, project);
        var rows = Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows"));
        var last = Descendants(rows).OfType<Button>().Last(button => button.Command == main.OpenSimulationObservationStepCommand);
        last.BringIntoView();
        await Stable(window);
        AssertInside(window, last);
        AssertInside(scroll, last);
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Capture(window, "long-" + language + "-" + fullArea, main, project);
    }, project =>
    {
        foreach (var unit in project.Stations[0].Units) unit.Name = string.Join(" ", Enumerable.Repeat("검사 위치와 장치 준비 상태 확인 유닛", 7));
        foreach (var sequence in project.Sequences)
        {
            sequence.Name = string.Join(" ", Enumerable.Repeat("Long process and actual target identity", 7));
            sequence.Steps[0].Name = string.Join(" ", Enumerable.Repeat("다음 장치의 준비 상태를 확인하는 공정", 10));
        }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task QueryingUnitAndWholeScopeDuringRunKeepsTheRunTargetAndRuntimeGeneration(OpenVisionLanguage language) => WithWindow(true, language, async (main, window, project) =>
    {
        await Wait(() => main.RunCommand.CanExecute(null));
        main.RunCommand.Execute(null);
        await Wait(() => main.IsRunning && main.RuntimeDebugger.SequenceObservations.Count(row => row.StepId == "delay") == 2);
        main.Navigation.IsSimulationDiagnosticsExpanded = true;
        await Stable(window);
        var definition = JsonSerializer.Serialize(project);
        var initial = Assert.IsType<SimulationSnapshot>(main.SceneSnapshots.Latest);
        var target = main.RunTargetText;
        var dirty = main.HasUnsavedChanges;
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var rows = Assert.IsType<ItemsControl>(view.FindName("SimulationSequenceRows"));
        var observe = Descendants(rows).OfType<Button>().Single(button => button.Command == main.ObserveSimulationUnitCommand && Equals(button.CommandParameter, "b"));
        observe.BringIntoView();
        await Stable(window);
        Invoke(observe);
        await Stable(window);
        Assert.Equal("b", main.Layout.ActiveUnitId);
        Assert.Equal("b", Assert.IsType<SimulationSequenceObservation>(Assert.Single(rows.Items.Cast<object>())).UnitId);
        Invoke(Assert.IsType<Button>(view.FindName("SimulationObserveWholeButton")));
        await Stable(window);
        var current = Assert.IsType<SimulationSnapshot>(main.SceneSnapshots.Latest);
        Assert.Null(main.Layout.ActiveUnitId);
        Assert.Equal(2, rows.Items.Count);
        Assert.True(main.IsRunning);
        Assert.Equal(initial.RunMode, current.RunMode);
        Assert.Equal(initial.RuntimeGeneration, current.RuntimeGeneration);
        Assert.True(current.TickIndex >= initial.TickIndex);
        Assert.Equal(target, main.RunTargetText);
        Assert.Equal(dirty, main.HasUnsavedChanges);
        Assert.Equal(definition, JsonSerializer.Serialize(project));
        Assert.False(main.Navigation.IsInspectorOpen);
        main.PauseCommand.Execute(null);
        await Wait(() => !main.IsRunning);
    });

    private Task WithWindow(bool fullArea, OpenVisionLanguage language, Func<MainViewModel, ShellWindow, MachineProjectDocument, Task> action, Action<MachineProjectDocument>? setup = null) => ui.InvokeTaskAsync(async () =>
    {
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = Project();
        setup?.Invoke(project);
        using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice();
        main.Navigation.IsSimulationOverviewWorkspace = true;
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        Place(window, fullArea);
        try
        {
            window.Show();
            await Stable(window);
            await action(main, window, project);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            var evidence = Path.Combine(TestStorage.RootPath, "simulation-diagnostics");
            Directory.CreateDirectory(evidence);
            File.AppendAllText(Path.Combine(evidence, "windows.jsonl"), JsonSerializer.Serialize(new
            {
                fullArea, language, ownWindowRetained = Application.Current.Windows.Cast<Window>().Contains(window),
                window.IsVisible, remainingWindows = Application.Current.Windows.Cast<Window>().Select(item => item.GetType().Name).ToArray()
            }) + Environment.NewLine);
            OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
        }
    });

    private static MachineProjectDocument Project()
    {
        var source = R40SimulationWorkspaceTests.CreateProject();
        return new MachineProjectDocument
        {
            Id = "r40-simulation-diagnostics", Name = source.Name, Stations = source.Stations,
            Axes = source.Axes, Layouts = source.Layouts, Sequences = source.Sequences
        };
    }

    private static void Place(Window window, bool fullArea)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screens.Length == 2
            ? screens.OrderBy(item => (long)item.WorkingArea.Width * item.WorkingArea.Height).ThenBy(item => item.Bounds.Left).First()
            : screens.FirstOrDefault(item => item.Primary) ?? screens[0];
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = screen.WorkingArea.Left;
        window.Top = screen.WorkingArea.Top;
        window.Width = fullArea ? screen.WorkingArea.Width : 1280;
        window.Height = fullArea ? screen.WorkingArea.Height : 760;
    }

    private static async Task StartPaused(MainViewModel main)
    {
        await Wait(() => main.RunCommand.CanExecute(null));
        main.RunCommand.Execute(null);
        await Wait(() => main.IsRunning && main.RuntimeDebugger.SequenceObservations.Count(row => row.StepId == "delay") == 2);
        await Wait(() => main.PauseCommand.CanExecute(null));
        main.PauseCommand.Execute(null);
        await Wait(() => !main.IsRunning && main.RuntimeDebugger.SequenceObservations.All(row => row.StateText == OpenVisionLanguageService.T("Shell.Paused")));
    }

    private static string State(MainViewModel main, MachineProjectDocument project) => JsonSerializer.Serialize(new
    {
        definition = JsonSerializer.Serialize(project), snapshot = main.SceneSnapshots.Latest,
        main.HasUnsavedChanges, main.IsRunning, main.RunTargetText,
        selectedSequence = main.SequenceEditor.SelectedSequence?.Id, selectedStep = main.SequenceEditor.SelectedStep?.Id,
        draft = main.SequenceEditor.SelectedStepDraft?.Parameter
    });

    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(16);
        Assert.True(condition(), "The expected fixture state was not observed.");
    }

    private static async Task Stable(Window window)
    {
        var done = new TaskCompletionSource();
        string previous = string.Empty;
        int stable = 0, count = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += OnTick;
        timer.Start();
        try { await done.Task; }
        finally { timer.Stop(); timer.Tick -= OnTick; }
        void OnTick(object? sender, EventArgs args)
        {
            window.UpdateLayout();
            var signature = string.Join("|", Descendants(window).OfType<FrameworkElement>().Where(item => item.IsVisible)
                .Select(item => $"{item.GetType().Name}:{item.ActualWidth:R}:{item.ActualHeight:R}:{item.RenderTransform.Value}:{item.Opacity:R}:{item.Clip?.Bounds}"));
            stable = signature == previous ? stable + 1 : 0;
            previous = signature;
            if (stable >= 3) done.TrySetResult();
            else if (++count >= 125) done.TrySetException(new TimeoutException("Fixture rendering did not settle."));
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static Rect Bounds(FrameworkElement element, Window window) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    private static void AssertInside(FrameworkElement ancestor, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
        var available = new Rect(ancestor.RenderSize);
        available.Inflate(0.01, 0.01);
        Assert.True(element.IsVisible && available.Contains(bounds), $"{element.Name}: {bounds} outside {available}.");
    }

    private static void AssertPlacement(MainViewModel main, Window window, SimulationObservationView view, MachineSceneViewport viewport)
    {
        var scroll = Assert.IsType<ScrollViewer>(view.FindName("SimulationDiagnosticsScroll"));
        bool sidebar = !main.Navigation.IsCompactLayout && !main.Navigation.IsInspectorOpen;
        Assert.Equal(sidebar, view.IsSidebar);
        if (sidebar)
        {
            Assert.InRange(view.ActualWidth, 350, 370);
            Assert.True(Bounds(viewport, window).Right <= Bounds(view, window).Left + 1);
            Assert.True(scroll.ActualHeight > 200);
        }
        else
        {
            Assert.True(Bounds(viewport, window).Bottom <= Bounds(view, window).Top + 1);
            Assert.InRange(scroll.ActualHeight, 30, 200);
        }
        AssertInside(window, view);
        AssertInside(window, viewport);
    }

    private static void Toggle(CheckBox checkbox) => Assert.IsAssignableFrom<IToggleProvider>(new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle)).Toggle();
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static void Capture(Window window, string name, MainViewModel main, MachineProjectDocument project)
    {
        var folder = Path.Combine(TestStorage.RootPath, "simulation-diagnostics");
        Directory.CreateDirectory(folder);
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal(1, dpi.DpiScaleX);
        Assert.Equal(1, dpi.DpiScaleY);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(monitor.WindowIntersectsMonitor);
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(folder, name + ".png"))) encoder.Save(file);
        var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
        var viewport = Assert.Single(Descendants(window).OfType<MachineSceneViewport>());
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new
        {
            monitor, width = image.PixelWidth, height = image.PixelHeight, dpi = 96,
            productVersion = FileVersionInfo.GetVersionInfo(typeof(MainViewModel).Assembly.Location).ProductVersion,
            project.Id, main.HasUnsavedChanges, main.IsRunning, unitId = main.Layout.ActiveUnitId,
            viewport = Bounds(viewport, window).ToString(), observations = Bounds(view, window).ToString()
        }));
    }
}
