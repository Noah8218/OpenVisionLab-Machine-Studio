using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40SimulationWorkspaceTests(StudioUiTestHost ui)
{
    [Fact]
    public async Task HistoryNavigationHonorsDraftCancellationModalAdmissionAndDisposal()
    {
        await ui.InvokeAsync(() =>
        {
            bool allowNavigation = true;
            using var navigation = new ShellNavigationViewModel(false, () => true, () => true, () => Task.CompletedTask,
                () => { }, exception => throw exception, resolvePendingPlacementDraft: () => allowNavigation);
            navigation.IsSimulationOverviewWorkspace = true;
            allowNavigation = false;
            navigation.OpenResultsWorkspaceCommand.Execute(null);
            Assert.True(navigation.IsSimulationOverviewWorkspace);
            allowNavigation = true;
            navigation.OpenResultsWorkspaceCommand.Execute(null);
            Assert.True(navigation.IsResultsWorkspace);
            navigation.IsSimulationOverviewWorkspace = true;
            navigation.OpenTestScenarioCommand.Execute(null);
            Assert.True(navigation.IsModalOverlayOpen);
            Assert.False(navigation.OpenResultsWorkspaceCommand.CanExecute(null));
            navigation.OpenResultsWorkspaceCommand.Execute(null);
            Assert.True(navigation.IsSimulationOverviewWorkspace);
            navigation.CloseTestScenarioCommand.Execute(null);
            Assert.True(navigation.OpenResultsWorkspaceCommand.CanExecute(null));
            navigation.Dispose();
            navigation.OpenResultsWorkspaceCommand.Execute(null);
            Assert.True(navigation.IsSimulationOverviewWorkspace);
            Assert.False(navigation.OpenResultsWorkspaceCommand.CanExecute(null));
            return true;
        });
    }

    [Theory]
    [InlineData(1280, 760, true, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, false, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, false, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, false, OpenVisionLanguage.English)]
    public async Task EmptyAndLongFaultDiagnosticsStayBoundedAndReachable(int width, int height, bool empty, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = empty ? new MachineProjectDocument() : CreateProject();
            if (!empty)
            {
                project.Stations[0].Units[0].Name = string.Join(" ", Enumerable.Repeat("검사 위치 확인 유닛", 8));
                project.Sequences[0].Name = string.Join(" ", Enumerable.Repeat("Long process name", 7));
                project.Sequences[0].Steps[0].Name = string.Join(" ", Enumerable.Repeat("다음 장치의 준비 신호를 기다리는 공정", 8));
            }
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsSimulationOverviewWorkspace = true;
            main.Navigation.IsSimulationDiagnosticsExpanded = true;
            string before = JsonSerializer.Serialize(project);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            try
            {
                window.Show();
                await WaitForAsync(() => main.SceneSnapshots.Latest is not null);
                Pump(window);
                if (!empty)
                {
                    var error = new SequenceExecutionError(SequenceExecutionErrorCode.StepTimedOut, "sequence-a", "delay", string.Join(" ", Enumerable.Repeat("격리된 검사용 오류 메시지", 10)));
                    main.RuntimeDebugger.ApplySnapshot(SimulationSequenceObservationTests.Snapshot(project,
                        [SimulationSequenceObservationTests.Execution("sequence-a", "delay") with { Status = SequenceExecutionStatus.Faulted, LastError = error }]));
                }
                Pump(window);
                var view = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
                var scroll = Assert.IsType<ScrollViewer>(view.FindName("SimulationDiagnosticsScroll"));
                Assert.InRange(scroll.ActualHeight, 30, 200);
                if (empty)
                {
                    Assert.Empty(main.RuntimeDebugger.SequenceObservations);
                    Assert.Contains(Descendants(view).OfType<TextBlock>(), item => item.IsVisible && item.Text == OpenVisionLanguageService.T("SimObservation.NoProcesses"));
                    Assert.Single(Descendants(view).OfType<Button>().Where(item => item.IsVisible && item.Command == main.OpenStationUnitEditorCommand));
                }
                else
                {
                    var row = Assert.Single(main.RuntimeDebugger.SequenceObservations);
                    Assert.True(row.IsFaulted);
                    Assert.Contains("StepTimedOut", row.DetailText);
                    var command = Descendants(view).OfType<Button>().Single(item => item.Command == main.OpenSimulationObservationStepCommand);
                    command.BringIntoView();
                    Pump(window);
                    Assert.True(scroll.ScrollableHeight > 0);
                    Assert.True(new Rect(window.RenderSize).Contains(Bounds(command, window)));
                    Assert.False(command.IsEnabled); // Synthetic display data cannot navigate through the real engine identity.
                }
                Capture(window, $"synthetic-{(empty ? "empty" : "long-fault")}-{language}-{width}x{height}");
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
            }
            finally
            {
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    public async Task DiagnosticsObserveThePausedRunAndNavigateThroughTheActualBoundButton(int width, int height, OpenVisionLanguage language)
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsSimulationOverviewWorkspace = true;
            string before = JsonSerializer.Serialize(project);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            try
            {
                window.Show();
                Pump(window);
                var observationView = Assert.Single(Descendants(window).OfType<SimulationObservationView>());
                var toggle = Assert.IsType<CheckBox>(observationView.FindName("SimulationDiagnosticsToggle"));
                Assert.False(main.Navigation.IsSimulationDiagnosticsExpanded);
                Assert.IsAssignableFrom<IToggleProvider>(new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                Assert.True(main.Navigation.IsSimulationDiagnosticsExpanded);
                await WaitForAsync(() => main.RunCommand.CanExecute(null));
                main.RunCommand.Execute(null);
                await WaitForAsync(() => main.IsRunning && main.RuntimeDebugger.SequenceObservations.Count(row => row.StepId == "delay") == 2);
                await WaitForAsync(() => main.PauseCommand.CanExecute(null));
                main.PauseCommand.Execute(null);
                await WaitForAsync(() => !main.IsRunning && main.RuntimeDebugger.SequenceObservations.All(row => row.StateText == OpenVisionLanguageService.T("Shell.Paused")));
                Pump(window);
                var rows = Assert.IsType<ItemsControl>(observationView.FindName("SimulationSequenceRows"));
                Assert.Equal(2, rows.Items.Count);
                var row = main.RuntimeDebugger.SequenceObservations.Single(item => item.RootSequenceId == "sequence-b");
                Assert.True(main.OpenSimulationObservationStepCommand.CanExecute(row));
                Assert.False(main.OpenSimulationObservationStepCommand.CanExecute(row with { ProjectId = "stale" }));
                Assert.False(main.OpenSimulationObservationStepCommand.CanExecute(row with { RuntimeGeneration = row.RuntimeGeneration + 1 }));
                Assert.False(main.OpenSimulationObservationStepCommand.CanExecute(row with { StepId = "done" }));
                string originalParameter = main.SequenceEditor.SelectedStepDraft!.Parameter;
                main.SequenceEditor.SelectedStepDraft.Parameter = "17";
                Assert.True(main.SequenceEditor.HasPendingStepDraft);
                main.OpenSimulationObservationStepCommand.Execute(row);
                Assert.True(main.Navigation.IsSimulationOverviewWorkspace);
                Assert.Equal("17", main.SequenceEditor.SelectedStepDraft.Parameter);
                main.SequenceEditor.SelectedStepDraft.Parameter = originalParameter;
                var scroll = Assert.IsType<ScrollViewer>(observationView.FindName("SimulationDiagnosticsScroll"));
                Assert.InRange(scroll.ActualHeight, 100, 200);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var viewport = Assert.IsType<MachineSceneViewport>(scene.FindName("SceneViewport"));
                Assert.True(viewport.ActualHeight >= 150, $"Scene height was {viewport.ActualHeight}.");
                Assert.True(Bounds(viewport, window).Bottom <= Bounds(observationView, window).Top + 1);
                Capture(window, $"paused-{language}-{width}x{height}");
                var unitButton = Descendants(rows).OfType<Button>().Single(item => item.Command == main.ShowEquipmentUnitCommand && Equals(item.CommandParameter, "b"));
                unitButton.BringIntoView();
                Pump(window);
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(unitButton).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
                Assert.Equal("b", main.Layout.ActiveUnitId);
                Assert.True(main.Navigation.IsSimulationOverviewWorkspace);
                Assert.Equal(2, main.RuntimeDebugger.SequenceObservations.Count(item => item.StepId == "delay"));
                var stepButton = Descendants(rows).OfType<Button>().Single(item => item.Command == main.OpenSimulationObservationStepCommand && item.CommandParameter is SimulationSequenceObservation reference && reference.RootSequenceId == "sequence-b");
                stepButton.BringIntoView();
                Pump(window);
                Assert.True(stepButton.IsEnabled);
                Assert.True(new Rect(window.RenderSize).Contains(Bounds(stepButton, window)));
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(stepButton).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
                Assert.True(main.Navigation.IsProcessWorkspace);
                Assert.Equal("sequence-b", main.SequenceEditor.SelectedSequence?.Id);
                Assert.Equal("delay", main.SequenceEditor.SelectedStep?.Id);
                Assert.False(main.SequenceEditor.IsEditable);
                Assert.False(main.OpenSimulationObservationStepCommand.CanExecute(row));
                main.Navigation.IsSimulationOverviewWorkspace = true;
                Assert.True(main.OpenSimulationObservationStepCommand.CanExecute(row));
                Pump(window);
                var history = Assert.IsType<Button>(observationView.FindName("SimulationRunHistoryButton"));
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(history).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
                Assert.True(main.Navigation.IsResultsWorkspace);
                main.Navigation.IsSimulationOverviewWorkspace = true;
                await WaitForAsync(() => main.ResetCommand.CanExecute(null));
                main.ResetCommand.Execute(null);
                await WaitForAsync(() => main.RuntimeDebugger.SequenceObservations.All(item => !item.CanOpenStep && item.StepId is null));
                Assert.False(main.OpenSimulationObservationStepCommand.CanExecute(row));
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
            }
            finally
            {
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    [Fact]
    public async Task ChangedDefinitionsHideOldValuesUntilTheExistingRunWorkflowRebuildsThem()
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var project = CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsProcessWorkspace = true;
            main.SequenceEditor.SelectedStepDraft!.Parameter = "12";
            Assert.True(main.SequenceEditor.ApplyStepDraftCommand.CanExecute(null));
            main.SequenceEditor.ApplyStepDraftCommand.Execute(null);
            Assert.False(main.AreSimulationObservationsCurrent);
            Assert.True(main.HasUnsavedChanges);
            main.Navigation.IsSimulationOverviewWorkspace = true;
            var view = new SimulationObservationView { DataContext = main };
            main.Navigation.IsSimulationDiagnosticsExpanded = true;
            view.Measure(new Size(800, 400));
            view.Arrange(new Rect(0, 0, 800, 400));
            view.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, view.SimulationSequenceRows.Visibility);
            Assert.Equal(Visibility.Visible, view.SimulationPendingDefinitionHint.Visibility);
            await WaitForAsync(() => main.RunCommand.CanExecute(null));
            main.RunCommand.Execute(null);
            await WaitForAsync(() => main.IsRunning && main.AreSimulationObservationsCurrent && main.RuntimeDebugger.SequenceObservations.Any(row => row.ValuesText.Contains("12 s", StringComparison.Ordinal)));
            await WaitForAsync(() => main.PauseCommand.CanExecute(null));
            main.PauseCommand.Execute(null);
            await WaitForAsync(() => !main.IsRunning);
            Assert.Equal(Visibility.Visible, view.SimulationSequenceRows.Visibility);
            Assert.Equal(Visibility.Collapsed, view.SimulationPendingDefinitionHint.Visibility);
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 760)]
    public async Task RenderSimulationWithoutChangingDefinition(int width, int height)
    {
        await ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = CreateProject();
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsSimulationOverviewWorkspace = true;
            string before = JsonSerializer.Serialize(project);
            var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                var folder = Path.Combine(TestStorage.RootPath, "simulation-workspace");
                Directory.CreateDirectory(folder);
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = File.Create(Path.Combine(folder, $"simulation-{width}x{height}.png"))) encoder.Save(output);
                File.WriteAllText(Path.Combine(folder, $"monitor-{width}.json"), JsonSerializer.Serialize(monitor));
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                return true;
            }
            finally
            {
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    internal static MachineProjectDocument CreateProject()
    {
        var units = new[] { "a", "b" };
        return new MachineProjectDocument
        {
            Name = "병렬 공정 관찰",
            Stations = [new MachineStationDefinition { Id = "station", Name = "검사 장비", Units = units.Select(id => new MachineUnitDefinition { Id = id, Name = "검사 유닛 " + id.ToUpperInvariant() }).ToList() }],
            Axes = units.Select(id => new VirtualAxisDefinition { Id = "axis-" + id, Name = "이송 축 " + id.ToUpperInvariant() }).ToList(),
            Layouts = [new MachineLayoutDefinition
            {
                Id = "layout", Name = "병렬 장비",
                Components = units.Select((id, index) => new LayoutComponentDefinition
                {
                    Id = "stage-" + id, Name = "이송 축 " + id.ToUpperInvariant(), Kind = LayoutComponentKind.LinearStage,
                    UnitId = id, BehaviorBindingId = "axis-" + id,
                    Transform = new Transform2D { X = index * 280 }, Size = new Size2D { Width = 200, Height = 40 }
                }).ToList()
            }],
            Sequences = units.Select(id => new SequenceDefinition
            {
                Id = "sequence-" + id, Name = "검사 공정 " + id.ToUpperInvariant(), UnitId = id, WatchdogTimeoutMs = 120000,
                Steps = [new SequenceStepDefinition { Id = "delay", Name = "검사 조건 대기", Action = SequenceStepAction.Delay, Parameter = "30" }, new SequenceStepDefinition { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }]
            }).ToList()
        };
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static Rect Bounds(FrameworkElement element, Window window) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "Expected simulation state was not observed within the bounded wait.");
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

    private static void Capture(Window window, string name)
    {
        var folder = Path.Combine(TestStorage.RootPath, "simulation-workspace");
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(folder, name + ".png"))) encoder.Save(output);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(monitor.WindowIntersectsMonitor);
        File.WriteAllText(Path.Combine(folder, name + "-monitor.json"), JsonSerializer.Serialize(monitor));
    }
}
