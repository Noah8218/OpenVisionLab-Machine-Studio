using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40FaultWorkspaceTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task RenderCurrentSourceWithTwoExplicitSessionFaults(bool fullArea, OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); await Prepare(main, true); var window = Show(main, fullArea);
        try
        {
            string definition = JsonSerializer.Serialize(project), runtime = Runtime(main); Capture(window, $"source-{language}-{fullArea}", fullArea);
            foreach (string name in new[] { "GlobalFaultKindComboBox", "GlobalFaultTargetComboBox", "GlobalFaultForcedValueComboBox", "GlobalInjectFaultButton" }) Assert.True(IsUnclipped(window, Assert.IsAssignableFrom<FrameworkElement>(View(window).FindName(name))), name);
            Assert.Equal(2, main.FaultManager.ActiveFaults.Count); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main));
            Assert.False(main.HasUnsavedChanges || main.IsRunning || main.Camera.HasCurrentCameraInputs);
        }
        finally { Close(window, previous); }
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task EmbeddedLayoutAndTypedSelectionsPreserveRuntimeAndRecipe(bool fullArea, OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); await Prepare(main, true); var window = Show(main, fullArea);
        try
        {
            var view = View(window); var vm = main.FaultManager; string definition = JsonSerializer.Serialize(project), runtime = Runtime(main);
            Assert.Same(vm, view.DataContext); Assert.True(view.IsEmbedded); Assert.False(Named<Button>(view, "CloseFaultDrawerButton").IsVisible);
            var choice = Bounds(window, Named<StackPanel>(view, "FaultChoicePanel")); var active = Bounds(window, Named<StackPanel>(view, "FaultActivePanel"));
            if (main.Navigation.IsCompactLayout) Assert.True(active.Top >= choice.Bottom); else Assert.True(active.Left >= choice.Right);
            var list = Named<ListBox>(view, "GlobalActiveFaultList"); Assert.Equal(2, list.Items.Count);
            var ids = Descendants(list).OfType<TextBlock>().Where(item => item.Name == "FaultActiveTargetId" && item.IsVisible).Select(item => item.Text).Order().ToArray();
            Assert.Equal(vm.ActiveFaults.Select(item => item.TargetId).Order(), ids); Assert.Single(vm.ActiveFaults.Select(item => item.TargetName).Distinct());
            var target = Named<ComboBox>(view, "GlobalFaultTargetComboBox"); Select(target, vm.Targets.Last(), window); Assert.Same(target.SelectedItem, vm.SelectedTarget);
            vm.SelectedTarget = vm.Targets.First(); Pump(window); Assert.Same(vm.SelectedTarget, target.SelectedItem);
            Select(list, vm.ActiveFaults.Last(), window); Assert.Same(list.SelectedItem, vm.SelectedActiveFault);
            vm.SelectedActiveFault = vm.ActiveFaults.First(); Pump(window); Assert.Same(vm.SelectedActiveFault, list.SelectedItem);
            Select(Named<ComboBox>(view, "GlobalFaultForcedValueComboBox"), vm.ForcedValueOptions.Last(), window); Assert.True(vm.SelectedForcedValue.Value);
            Select(Named<ComboBox>(view, "GlobalFaultKindComboBox"), vm.AvailableKinds.Single(item => item.Kind == SimulationFaultKind.AxisFollowingError), window);
            Assert.Equal(SimulationFaultKind.AxisFollowingError, vm.SelectedKind.Kind); Assert.False(Named<ComboBox>(view, "GlobalFaultForcedValueComboBox").IsVisible);
            vm.SelectedKind = vm.AvailableKinds[0]; Pump(window); Assert.Same(vm.SelectedKind, Named<ComboBox>(view, "GlobalFaultKindComboBox").SelectedItem);
            Named<ScrollViewer>(view, "FaultDrawerScroll").ScrollToBottom(); Pump(window); AssertVisible(window, Named<Button>(view, "GlobalClearAllFaultsButton")); Capture(window, $"layout-{language}-{fullArea}", fullArea);
            main.Navigation.SelectedExperimentTabIndex = 2; main.Navigation.SelectedExperimentTabIndex = 1; Pump(window);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning);
        }
        finally { Close(window, previous); }
    });

    [Theory]
    [InlineData(SimulationFaultKind.StuckDigitalInput, OpenVisionLanguage.Korean)]
    [InlineData(SimulationFaultKind.CylinderTravelBlocked, OpenVisionLanguage.Korean)]
    [InlineData(SimulationFaultKind.AxisMotionBlocked, OpenVisionLanguage.Korean)]
    [InlineData(SimulationFaultKind.AxisFollowingError, OpenVisionLanguage.Korean)]
    [InlineData(SimulationFaultKind.StuckDigitalInput, OpenVisionLanguage.English)]
    [InlineData(SimulationFaultKind.CylinderTravelBlocked, OpenVisionLanguage.English)]
    [InlineData(SimulationFaultKind.AxisMotionBlocked, OpenVisionLanguage.English)]
    [InlineData(SimulationFaultKind.AxisFollowingError, OpenVisionLanguage.English)]
    public Task ExplicitCommandsUseRealKindAndTargetThenRecoverWithoutSaving(SimulationFaultKind kind, OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); await Prepare(main, false); var window = Show(main, false);
        try
        {
            var view = View(window); var vm = main.FaultManager; string definition = JsonSerializer.Serialize(project), beforeSelection = Runtime(main);
            Select(Named<ComboBox>(view, "GlobalFaultKindComboBox"), vm.AvailableKinds.Single(item => item.Kind == kind), window);
            Select(Named<ComboBox>(view, "GlobalFaultTargetComboBox"), vm.Targets.Last(), window); string id = vm.SelectedTarget!.Id;
            if (kind == SimulationFaultKind.StuckDigitalInput) Select(Named<ComboBox>(view, "GlobalFaultForcedValueComboBox"), vm.ForcedValueOptions.Single(item => item.Value), window);
            Assert.Equal(beforeSelection, Runtime(main)); Assert.Equal(kind == SimulationFaultKind.StuckDigitalInput, Named<ComboBox>(view, "GlobalFaultForcedValueComboBox").IsVisible);
            Invoke(Named<Button>(view, "GlobalInjectFaultButton")); await Wait(() => !vm.IsOperationPending && vm.ActiveFaults.Any(item => item.Kind == kind && item.TargetId == id)); Pump(window);
            var original = vm.ActiveFaults.Single(item => item.Kind == kind && item.TargetId == id); Assert.Equal(kind == SimulationFaultKind.StuckDigitalInput ? true : (bool?)null, original.ForcedValue);
            Assert.False(vm.InjectCommand.CanExecute(null)); Assert.False(Named<Button>(view, "GlobalInjectFaultButton").IsEnabled);
            Capture(window, $"injected-{kind}-{language}", false);
            Select(Named<ComboBox>(view, "GlobalFaultKindComboBox"), vm.AvailableKinds[0], window);
            Select(Named<ComboBox>(view, "GlobalFaultTargetComboBox"), vm.Targets.First(item => item.Id != id), window); string otherId = vm.SelectedTarget!.Id;
            Invoke(Named<Button>(view, "GlobalInjectFaultButton")); await Wait(() => !vm.IsOperationPending && vm.ActiveFaults.Count == 2); Pump(window);
            Select(Named<ListBox>(view, "GlobalActiveFaultList"), vm.ActiveFaults.Single(item => item.Kind == kind && item.TargetId == id), window);
            Invoke(Named<Button>(view, "GlobalClearFaultButton")); await Wait(() => !vm.IsOperationPending && vm.ActiveFaults.Count == 1); Pump(window);
            Assert.Equal(otherId, Assert.Single(vm.ActiveFaults).TargetId); Assert.DoesNotContain(main.SceneSnapshots.Latest!.Faults, item => item.Kind == kind && item.TargetId == id);
            Invoke(Named<Button>(view, "GlobalClearAllFaultsButton")); await Wait(() => !vm.IsOperationPending && !vm.HasActiveFaults); Pump(window);
            Assert.True(vm.InjectCommand.CanExecute(null)); Assert.False(Named<Button>(view, "GlobalClearAllFaultsButton").IsEnabled);
            Invoke(Named<Button>(view, "GlobalInjectFaultButton")); await Wait(() => !vm.IsOperationPending && vm.HasActiveFaults);
            Assert.True(main.ResetCommand.CanExecute(null)); main.ResetCommand.Execute(null); await Wait(() => !vm.HasActiveFaults); Pump(window);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.False(main.HasUnsavedChanges || main.IsRunning || main.Camera.HasCurrentCameraInputs);
        }
        finally { Close(window, previous); }
    });

    [Theory]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.English)]
    [InlineData(true, OpenVisionLanguage.English)]
    public Task DesignAndEmptyContextsStayReadableWithoutDispatch(bool empty, OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = Language(language); var project = empty ? new MachineProjectDocument { Id = "empty-fault-project" } : CreateProject(); using var main = CreateMain(project); var window = Show(main, false);
        try
        {
            var view = View(window); string definition = JsonSerializer.Serialize(project), runtime = Runtime(main);
            Assert.False(main.FaultManager.IsEnabled); Assert.False(Named<ComboBox>(view, "GlobalFaultKindComboBox").IsEnabled);
            Assert.False(Named<Button>(view, "GlobalInjectFaultButton").IsEnabled || Named<Button>(view, "GlobalClearAllFaultsButton").IsEnabled);
            Assert.False(Named<TextBlock>(view, "FaultCommandPending").IsVisible); Assert.Empty(Named<ListBox>(view, "GlobalActiveFaultList").Items);
            if (empty) Assert.True(Named<TextBlock>(view, "GlobalFaultTargetHelpText").IsVisible);
            Capture(window, $"readonly-empty-{empty}-{language}", false); main.Navigation.SelectedExperimentTabIndex = 0; main.Navigation.SelectedExperimentTabIndex = 1; Pump(window);
            Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning);
        }
        finally { Close(window, previous); }
        return true;
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task PendingCommandsDisableReentryAndDiscardLateStatusThenRejectAndRecover(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var gate = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously); var commands = new List<SimulationCommand>();
        using var vm = new FaultManagerViewModel(command =>
        {
            commands.Add(command);
            return commands.Count == 1 ? gate.Task : Task.FromResult(new SimulationCommandResult(command.CommandId, commands.Count != 2, 5, TimeSpan.FromMilliseconds(25), commands.Count == 2 ? SimulationCommandErrorCode.FaultApplicationRejected : SimulationCommandErrorCode.None, commands.Count == 2 ? string.Join(" ", Enumerable.Repeat("요청한 런타임 대상 상태를 확인하세요.", 8)) : null));
        });
        vm.SelectedKind = vm.AvailableKinds.Single(item => item.Kind == SimulationFaultKind.AxisMotionBlocked); vm.ApplySnapshot(Snapshot()); vm.IsEnabled = true;
        var panel = new FaultSimulationPanelView { DataContext = vm, IsEmbedded = true }; var window = new Window { Content = panel, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 900, 700); ShellWindow.SetIsCompactLayout(window, false); window.Show(); Pump(window);
        try
        {
            Select(Named<ComboBox>(panel, "GlobalFaultTargetComboBox"), vm.Targets.Last(), window); string original = vm.OperationStatusText;
            Invoke(Named<Button>(panel, "GlobalInjectFaultButton")); await Wait(() => vm.IsOperationPending); Pump(window);
            Assert.True(Named<TextBlock>(panel, "FaultCommandPending").IsVisible); Assert.False(Named<Button>(panel, "GlobalInjectFaultButton").IsEnabled);
            vm.InjectCommand.Execute(null); vm.ClearAllCommand.Execute(null); Assert.Single(commands); Assert.Equal("axis-b", Assert.IsType<InjectSimulationFaultCommand>(commands[0]).TargetId);
            Capture(window, $"adapter-pending-{language}", false);
            vm.IsEnabled = false; gate.SetResult(new(commands[0].CommandId, true, 5, TimeSpan.FromMilliseconds(25), SimulationCommandErrorCode.None, null)); await Wait(() => !vm.IsOperationPending); Pump(window);
            Assert.Equal(original, vm.OperationStatusText); Assert.False(Named<TextBlock>(panel, "FaultCommandPending").IsVisible); Assert.False(vm.InjectCommand.CanExecute(null));
            vm.IsEnabled = true; await Wait(() => vm.InjectCommand.CanExecute(null) && Named<Button>(panel, "GlobalInjectFaultButton").IsEnabled); Invoke(Named<Button>(panel, "GlobalInjectFaultButton")); await Wait(() => !vm.IsOperationPending && commands.Count == 2); Pump(window);
            Assert.Contains("FaultApplicationRejected", vm.OperationStatusText); Assert.Empty(vm.ActiveFaults); Assert.Equal(vm.OperationStatusText, Named<TextBlock>(panel, "GlobalFaultOperationStatus").Text);
            Named<ScrollViewer>(panel, "FaultDrawerScroll").ScrollToBottom(); Pump(window); Capture(window, $"adapter-rejected-{language}", false);
            await Wait(() => vm.InjectCommand.CanExecute(null) && Named<Button>(panel, "GlobalInjectFaultButton").IsEnabled); Invoke(Named<Button>(panel, "GlobalInjectFaultButton")); await Wait(() => !vm.IsOperationPending && commands.Count == 3);
            Assert.Empty(vm.ActiveFaults); vm.ApplySnapshot(Snapshot([new(SimulationFaultKind.AxisMotionBlocked, "axis-b", null, 5, TimeSpan.FromMilliseconds(25))])); Pump(window);
            Assert.Equal("axis-b", Assert.Single(vm.ActiveFaults).TargetId); Assert.Equal("axis-b", Descendants(panel).OfType<TextBlock>().Single(item => item.Name == "FaultActiveTargetId").Text);
            await Wait(() => Named<Button>(panel, "GlobalClearFaultButton").IsEnabled); Capture(window, $"adapter-recovered-{language}", false);
        }
        finally { if (!gate.Task.IsCompleted) gate.SetResult(new("cleanup", false, 0, TimeSpan.Zero, SimulationCommandErrorCode.EngineStopped, null)); Close(window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task ReentryAndViewDataContextReplacementKeepEachSessionsActualFaults(OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject(); using var main = CreateMain(project); await Prepare(main, true);
        int replacementDispatches = 0;
        using var replacement = new FaultManagerViewModel(command => { replacementDispatches++; return Task.FromResult(new SimulationCommandResult(command.CommandId, true, 0, TimeSpan.Zero, SimulationCommandErrorCode.None, null)); });
        var window = Show(main, false);
        try
        {
            string definition = JsonSerializer.Serialize(project), runtime = Runtime(main); var ids = main.FaultManager.ActiveFaults.Select(item => item.TargetId).Order().ToArray();
            main.Navigation.SelectedExperimentTabIndex = 2; main.Navigation.SelectedExperimentTabIndex = 1; Pump(window); Assert.Equal(2, main.FaultManager.ActiveFaults.Count);
            var view = View(window); var binding = System.Windows.Data.BindingOperations.GetBinding(view, FrameworkElement.DataContextProperty);
            view.SetCurrentValue(FrameworkElement.DataContextProperty, replacement); Pump(window); Assert.Same(replacement, view.DataContext); Assert.Empty(Named<ListBox>(view, "GlobalActiveFaultList").Items);
            view.SetCurrentValue(FrameworkElement.DataContextProperty, main.FaultManager); Pump(window); Assert.Same(main.FaultManager, view.DataContext);
            Assert.Same(binding, System.Windows.Data.BindingOperations.GetBinding(view, FrameworkElement.DataContextProperty)); Assert.Equal(0, replacementDispatches);
            Assert.Equal(ids, main.FaultManager.ActiveFaults.Select(item => item.TargetId).Order()); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning);
        }
        finally { Close(window, previous); }
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task LongActiveNamesAndIdsRemainReachableThroughBothScrollAreas(bool fullArea, OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = Language(language); var project = CreateProject();
        string name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "장시간 공정 확인 센서" : "Long process sensor target", 4));
        var ids = Enumerable.Range(1, 6).Select(i => $"di.fault-long-{i}-runtime-target-runtime-target-runtime-target").ToArray();
        foreach (string id in ids) project.Channels.Add(new() { Id = id, Name = name, Kind = ChannelKind.DigitalInput });
        using var main = CreateMain(project); await Prepare(main, false); var window = Show(main, fullArea);
        try
        {
            var view = View(window); var vm = main.FaultManager; string definition = JsonSerializer.Serialize(project);
            foreach (string id in ids)
            {
                Select(Named<ComboBox>(view, "GlobalFaultTargetComboBox"), vm.Targets.Single(item => item.Id == id), window);
                await Wait(() => vm.InjectCommand.CanExecute(null)); Invoke(Named<Button>(view, "GlobalInjectFaultButton"));
                await Wait(() => !vm.IsOperationPending && vm.ActiveFaults.Any(item => item.TargetId == id));
            }
            Pump(window); var list = Named<ListBox>(view, "GlobalActiveFaultList"); Assert.Equal(6, list.Items.Count); Assert.InRange(list.ActualHeight, 1, 180);
            string runtime = Runtime(main); list.BringIntoView(); Pump(window); Capture(window, $"long-first-{language}-{fullArea}", fullArea);
            var last = vm.ActiveFaults.Single(item => item.TargetId == ids[^1]); Select(list, last, window); list.ScrollIntoView(last); Pump(window);
            var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(last)); Assert.Same(last, row.DataContext);
            Assert.Equal(ids[^1], Descendants(row).OfType<TextBlock>().Single(item => item.Name == "FaultActiveTargetId").Text);
            var innerScroll = Descendants(list).OfType<ScrollViewer>().Single(); Assert.True(innerScroll.ScrollableHeight > 0); Assert.True(innerScroll.VerticalOffset > 0);
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(list)); Assert.InRange(innerScroll.ScrollableWidth, 0, 0.01);
            Named<ScrollViewer>(view, "FaultDrawerScroll").ScrollToBottom(); Pump(window); AssertVisible(window, Named<Button>(view, "GlobalClearAllFaultsButton"));
            Capture(window, $"long-last-{language}-{fullArea}", fullArea); Assert.Equal(definition, JsonSerializer.Serialize(project)); Assert.Equal(runtime, Runtime(main)); Assert.False(main.HasUnsavedChanges || main.IsRunning);
        }
        finally { Close(window, previous); }
    });

    private static MachineProjectDocument CreateProject()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
        project.Id = "r40-session-fault-support"; project.Name = "장비 고장 확인"; project.Sequences.Clear(); project.Simulation.AutomaticRun = null;
        project.Simulation.TestScenarioFault = null; project.Simulation.TestScenarioAxisFault = null; project.Simulation.TestScenarioAssertions.Clear();
        project.Axes[0].Name = "같은 이름의 축"; project.Axes.Add(new() { Id = "axis-secondary", Name = "같은 이름의 축", HomePosition = 0, SoftLimitMin = -50, SoftLimitMax = 50 });
        foreach (var channel in project.Channels.Where(item => item.Kind == ChannelKind.DigitalInput)) channel.Name = "같은 이름의 센서";
        return project;
    }
    private static MainViewModel CreateMain(MachineProjectDocument project)
    {
        var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsExperimentWorkspace = true; main.Navigation.SelectedExperimentTabIndex = 1; return main;
    }
    private static async Task Prepare(MainViewModel main, bool active)
    {
        main.IsRunMode = true; await Wait(() => !main.IsModeTransitioning && main.FaultManager.IsEnabled && main.FaultManager.Targets.Count >= 2);
        if (!active) return;
        var vm = main.FaultManager; var ids = vm.Targets.Take(2).Select(item => item.Id).ToArray();
        for (int i = 0; i < ids.Length; i++)
        {
            vm.SelectedTarget = vm.Targets.Single(item => item.Id == ids[i]); vm.SelectedForcedValue = vm.ForcedValueOptions.Single(item => item.Value == (i == 0));
            await Wait(() => vm.InjectCommand.CanExecute(null)); vm.InjectCommand.Execute(null); int expected = i + 1; await Wait(() => !vm.IsOperationPending && vm.ActiveFaults.Count == expected);
        }
    }
    private static SimulationSnapshot Snapshot(IEnumerable<SimulationFaultSnapshot>? faults = null) => new(TimeSpan.FromMilliseconds(25), 5, SimulationRunMode.Paused, SimulationControlOwner.EmbeddedSequence, 1,
        [new OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot("axis-a", "같은 축 이름", OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 0, 0), new OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot("axis-b", "같은 축 이름", OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 0, 0)], 1, [], [], [], AutomaticRunSnapshot.NotConfigured, [], faults: faults, sequenceDebug: null);
    private static async Task Wait(Func<bool> ready) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static OpenVisionLanguage Language(OpenVisionLanguage language) { var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false); return previous; }
    private static FaultSimulationPanelView View(Window window) => Descendants(window).OfType<ExperimentWorkspaceView>().Single(item => item.IsVisible).ExperimentFaultEditor;
    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement => Assert.IsType<T>(root.FindName(name));
    private static Rect Bounds(Window window, FrameworkElement item) => item.TransformToAncestor(window).TransformBounds(new Rect(item.RenderSize));
    private static void AssertVisible(Window window, FrameworkElement item) { Assert.True(item.IsVisible); Assert.True(new Rect(window.RenderSize).Contains(Bounds(window, item)), item.Name); }
    private static string Runtime(MainViewModel main) => JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.TickIndex, main.SceneSnapshots.Latest.SimulationTime, main.SceneSnapshots.Latest.RuntimeGeneration, main.SceneSnapshots.Latest.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Faults, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.LayoutComponents, main.SceneSnapshots.Latest.Workpieces });
    private static void Select(Selector control, object item, Window window) { control.SetCurrentValue(Selector.SelectedItemProperty, item); Pump(window); }
    private static void Invoke(Button button) { Assert.True(button.IsEnabled); Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); }
    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false }; SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760); window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray(); var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX; window.Top = area[1] / dpi.DpiScaleY; window.Width = area[2] / dpi.DpiScaleX; window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window); return window;
    }
    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window); WaitForRender(window); var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window); var dpi = VisualTreeHelper.GetDpi(window); Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor); Assert.Equal(96, dpi.PixelsPerInchX);
        var folder = Path.Combine(TestStorage.RootPath, "fault-workspace"); Directory.CreateDirectory(folder); new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        var controls = Descendants(window).OfType<FrameworkElement>().Where(item => item.IsVisible && new[] { "FaultChoicePanel", "FaultActivePanel", "GlobalFaultKindComboBox", "GlobalFaultTargetComboBox", "GlobalInjectFaultButton" }.Contains(item.Name)).Select(item => new
        {
            item.Name, type = item.GetType().Name, width = item.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), item.ActualWidth, item.ActualHeight, margin = item.Margin.ToString(), alignment = item.HorizontalAlignment.ToString(), slot = LayoutInformation.GetLayoutSlot(item).ToString(), clip = VisualTreeHelper.GetClip(item)?.Bounds.ToString(), drawing = VisualTreeHelper.GetDrawing(item)?.Bounds.ToString(), item.IsMeasureValid, item.IsArrangeValid
        }).ToArray();
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none", controls, method = name.StartsWith("adapter-") ? "In-process existing View/VM adapter with controlled dispatch;900px supplemental window" : "In-process full Shell WPF RenderTargetBitmap; original full window, no crop/composite/resize/native input qualification" }));
    }
    private static bool IsUnclipped(Window window, FrameworkElement element)
    {
        for (DependencyObject? parent = element; parent is Visual visual; parent = VisualTreeHelper.GetParent(parent))
        {
            var clip = VisualTreeHelper.GetClip(visual);
            if (clip is not null)
            {
                var bounds = element.TransformToAncestor(visual).TransformBounds(new Rect(element.RenderSize)); var allowed = clip.Bounds; allowed.Inflate(0.01, 0.01);
                if (!allowed.Contains(bounds)) return false;
            }
            if (ReferenceEquals(parent, window)) break;
        }
        return true;
    }
    private static void WaitForRender(Window window)
    {
        string Signature() => string.Join("|", Descendants(window).OfType<UIElement>().Where(item => item.IsVisible).Select(item =>
            FormattableString.Invariant($"{item.RenderSize.Width:R}/{item.RenderSize.Height:R}/{item.Opacity:R}/{item.RenderTransform.Value}/{VisualTreeHelper.GetClip(item)?.Bounds}/{VisualTreeHelper.GetDrawing(item)?.Bounds}")));
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        string previous = ""; int stable = 0, ticks = 0;
        EventHandler handler = (_, _) => { window.UpdateLayout(); string current = Signature(); stable = current == previous ? stable + 1 : 0; previous = current; if (stable >= 2 || ++ticks >= 125) frame.Continue = false; };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(stable >= 2, "Actual rendered size/transform/clip/opacity/drawing must settle; no product animation or presentation pacing change.");
    }
    private static void Close(Window window, OpenVisionLanguage previous)
    {
        window.DataContext = null; window.Close(); Pump(window); var remaining = Application.Current.Windows.Cast<Window>().ToArray(); var folder = Path.Combine(TestStorage.RootPath, "fault-workspace"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window), window.IsVisible, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible); OpenVisionLanguageService.SetLanguage(previous, save: false);
    }
}
