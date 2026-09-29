using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EventJournalAlarmViewAutomationTestCollection
{
    public const string Name = "Event journal alarm view WPF automation";
}

[Collection(EventJournalAlarmViewAutomationTestCollection.Name)]
public sealed class EventJournalAlarmViewAutomationTests
{
    [Fact]
    public async Task TimelineViewFiltersClearsAndAcceptsNewSessionEvents()
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                using var runtimeDebugger = new RuntimeDebuggerViewModel(_ => Task.FromResult(Accepted()));
                runtimeDebugger.LoadProject(CreateProject(), resetSession: true);
                runtimeDebugger.ApplyEvent(new SimulationEvent(1, 10, TimeSpan.FromMilliseconds(250), "Error", "CAMERA_WAIT_FAILURE", "external result wait failed", "command-10"));
                runtimeDebugger.ApplyEvent(new SimulationEvent(2, 11, TimeSpan.FromMilliseconds(275), "Recovery", "CAMERA_WAIT_RECOVERED", "external result wait resumed", "command-11"));

                var view = new EventJournalView { DataContext = new EventJournalContext(runtimeDebugger) };
                var window = new Window
                {
                    Width = 900,
                    Height = 520,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };

                window.Show();
                try
                {
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var tabs = Assert.IsType<TabControl>(view.FindName("EvidenceTabs"));
                    tabs.SelectedIndex = 0;
                    var eventList = Assert.IsType<ListBox>(view.FindName("DebuggerTimelineList"));
                    var initialCount = eventList.Items.Count;
                    var categoryFilter = Assert.IsType<ComboBox>(view.FindName("DebuggerTimelineFilterComboBox"));
                    var severityFilter = Assert.IsType<ComboBox>(view.FindName("DebuggerTimelineSeverityFilterComboBox"));
                    categoryFilter.SelectedItem = categoryFilter.Items.OfType<RuntimeTimelineFilterItem>()
                        .Single(item => item.Key == "Error");
                    severityFilter.SelectedItem = severityFilter.Items.OfType<RuntimeTimelineFilterItem>()
                        .Single(item => item.Key == "Alarm");
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var filteredRow = Assert.IsType<ListBoxItem>(eventList.ItemContainerGenerator.ContainerFromIndex(0));
                    var filteredText = string.Join("\n", Descendants<TextBlock>(filteredRow).Select(item => item.Text));
                    var clearButton = Descendants<Button>(view).Single(button =>
                        button.GetBindingExpression(Button.CommandProperty)?.ParentBinding.Path?.Path
                            == "RuntimeDebugger.ClearTimelineCommand");
                    var clearEnabledBefore = clearButton.IsEnabled;
                    var clearInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                        UIElementAutomationPeer.CreatePeerForElement(clearButton)?.GetPattern(PatternInterface.Invoke));
                    clearInvoke.Invoke();
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var emptyText = Assert.IsType<TextBlock>(view.FindName("DebuggerTimelineEmptyTextBlock"));
                    var emptyVisibleAfterClear = emptyText.Visibility == Visibility.Visible;
                    var clearDisabledAfterClear = !clearButton.IsEnabled;

                    runtimeDebugger.ApplyEvent(new SimulationEvent(3, 12, TimeSpan.FromMilliseconds(300), "Information", "NEXT_RUN_STARTED", "next run started", "command-12"));
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

                    return (initialCount,
                        filteredCount: eventList.Items.Count,
                        filteredText,
                        clearEnabledBefore,
                        emptyVisibleAfterClear,
                        clearDisabledAfterClear,
                        newEventCount: eventList.Items.Count,
                        newEvent: runtimeDebugger.Timeline.Single().Code);
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

        Assert.Equal(2, result.initialCount);
        Assert.Equal(1, result.filteredCount);
        Assert.Contains("CAMERA_WAIT_FAILURE", result.filteredText, StringComparison.Ordinal);
        Assert.Contains("external result wait failed", result.filteredText, StringComparison.Ordinal);
        Assert.True(result.clearEnabledBefore);
        Assert.True(result.emptyVisibleAfterClear);
        Assert.True(result.clearDisabledAfterClear);
        Assert.Equal(1, result.newEventCount);
        Assert.Equal("NEXT_RUN_STARTED", result.newEvent);
    }

    [Fact]
    public async Task InspectorShowsUserPauseAfterExternalResultWait()
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                using var runtimeDebugger = new RuntimeDebuggerViewModel(_ => Task.FromResult(Accepted()));
                var project = CreateProject();
                runtimeDebugger.LoadProject(project, resetSession: true);
                runtimeDebugger.SetEnabled(true, invalidateCommands: true);
                runtimeDebugger.ApplySnapshot(CreatePauseSnapshot(SimulationRunMode.RealTime, SequenceDebugPauseReason.None));

                var view = new RightToolRegionView
                {
                    DataContext = new RuntimeInspectorContext(runtimeDebugger, IsRunMode: true)
                };
                var window = new Window
                {
                    Width = 640,
                    Height = 760,
                    Left = -2000,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Top = -2000,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };

                window.Show();
                try
                {
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var pauseReason = LogicalDescendants<TextBlock>(view).Single(text =>
                        text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path
                            == "RuntimeDebugger.PauseReasonText");
                    var activeText = pauseReason.Text;
                    runtimeDebugger.ApplySnapshot(CreatePauseSnapshot(SimulationRunMode.Paused, SequenceDebugPauseReason.User));
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

                    return (activeText, pausedText: pauseReason.Text, expectedPausedText: OpenVisionLanguageService.T("Debugger.PauseUser"));
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

        Assert.NotEqual(result.activeText, result.pausedText);
        Assert.Equal(result.expectedPausedText, result.pausedText);
    }

    [Theory]
    [InlineData(SequenceExecutionErrorCode.WorkpieceOperationFailed)]
    [InlineData(SequenceExecutionErrorCode.CameraTriggerFailed)]
    public async Task AlarmView_BindsRecoveredFailureAndClearedHistory(SequenceExecutionErrorCode errorCode)
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                using var runtimeDebugger = new RuntimeDebuggerViewModel(_ => Task.FromResult(Accepted()));
                runtimeDebugger.LoadProject(CreateProject(), resetSession: true);
                runtimeDebugger.SetEnabled(true, invalidateCommands: true);
                var failedSequence = CreateFailedSequence(errorCode);
                runtimeDebugger.ApplySnapshot(CreateSnapshot(failedSequence));

                var view = new EventJournalView { DataContext = new EventJournalContext(runtimeDebugger) };
                var window = new Window
                {
                    Width = 760,
                    Height = 620,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };

                window.Show();
                try
                {
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Descendants<TabControl>(view).Single().SelectedItem = Descendants<TabControl>(view).Single()
                        .Items.OfType<TabItem>()
                        .Single(item => Equals(item.Header?.ToString(), OpenVisionLanguageService.T("Diagnostics.Alarms")));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

                    var alarmList = Assert.IsType<ListBox>(view.FindName("DebuggerAlarmList"));
                    var alarmContainer = Assert.IsType<ListBoxItem>(alarmList.ItemContainerGenerator.ContainerFromIndex(0));
                    var activeAlarmText = string.Join("\n", Descendants<TextBlock>(alarmContainer).Select(item => item.Text));
                    var activeAlarmVisible = alarmList.Items.Count == 1
                        && activeAlarmText.Contains("Main cycle", StringComparison.Ordinal)
                        && activeAlarmText.Contains(errorCode.ToString(), StringComparison.Ordinal)
                        && activeAlarmText.Contains(OpenVisionLanguageService.T("Debugger.RecoveryReset"), StringComparison.Ordinal);

                    runtimeDebugger.ApplySnapshot(CreateSnapshot(failedSequence with { LastError = null }));
                    window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var historyExpander = Assert.IsType<Expander>(view.FindName("DebuggerAlarmHistoryExpander"));
                    historyExpander.IsExpanded = true;
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var historyList = Assert.IsType<ListBox>(view.FindName("DebuggerAlarmHistoryList"));
                    var historyContainer = Assert.IsType<ListBoxItem>(historyList.ItemContainerGenerator.ContainerFromIndex(0));
                    var clearedAlarmText = string.Join("\n", Descendants<TextBlock>(historyContainer).Select(item => item.Text));

                    return (activeAlarmVisible,
                        activeAlarmCount: alarmList.Items.Count,
                        historyCount: historyList.Items.Count,
                        historyHasSource: clearedAlarmText.Contains("Main cycle", StringComparison.Ordinal),
                        historyHasFailureCode: clearedAlarmText.Contains(errorCode.ToString(), StringComparison.Ordinal),
                        historyHasFailureMessage: clearedAlarmText.Contains(failedSequence.LastError!.Message, StringComparison.Ordinal),
                        activeAlarmText);
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

        Assert.True(result.activeAlarmVisible, result.activeAlarmText);
        Assert.Equal(0, result.activeAlarmCount);
        Assert.Equal(1, result.historyCount);
        Assert.True(result.historyHasSource);
        Assert.True(result.historyHasFailureCode);
        Assert.True(result.historyHasFailureMessage);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in LogicalDescendants<T>(child)) yield return descendant;
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

    private static MachineProjectDocument CreateProject() => new()
    {
        Id = "project",
        Name = "Event journal alarm test",
        Sequences =
        [
            new SequenceDefinition
            {
                Id = "cycle",
                Name = "Main cycle",
                Steps =
                [
                    new SequenceStepDefinition { Id = "feed", Name = "Feed" },
                    new SequenceStepDefinition { Id = "complete", Name = "Complete" }
                ]
            }
        ]
    };

    private static SequenceExecutionSnapshot CreateFailedSequence(
        SequenceExecutionErrorCode errorCode = SequenceExecutionErrorCode.WorkpieceOperationFailed) => new(
        "cycle",
        SequenceExecutionStatus.Completed,
        "complete",
        2,
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(25),
        5,
        new SequenceExecutionError(
            errorCode,
            "cycle",
            "feed-a-again",
            errorCode == SequenceExecutionErrorCode.CameraTriggerFailed
                ? "Camera trigger failed."
                : "The target workpiece position is occupied."),
        TimeSpan.FromSeconds(10));

    private static SimulationSnapshot CreateSnapshot(SequenceExecutionSnapshot sequence) => new(
        TimeSpan.FromMilliseconds(25),
        5,
        SimulationRunMode.Paused,
        SimulationControlOwner.EmbeddedSequence,
        1,
        [new AxisSnapshot("axis-x", "Axis X", OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 0, 0)],
        1,
        [],
        [sequence],
        [],
        AutomaticRunSnapshot.NotConfigured,
        [],
        []);

    private static SimulationSnapshot CreatePauseSnapshot(SimulationRunMode runMode, SequenceDebugPauseReason pauseReason) => new(
        TimeSpan.FromMilliseconds(250),
        17,
        runMode,
        SimulationControlOwner.EmbeddedSequence,
        1,
        [new AxisSnapshot("axis-x", "Axis X", OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 0, 0)],
        0,
        [],
        [new SequenceExecutionSnapshot(
            "cycle",
            SequenceExecutionStatus.Running,
            "feed",
            1,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(25),
            10,
            null,
            TimeSpan.FromMilliseconds(250))],
        [],
        AutomaticRunSnapshot.NotConfigured,
        [],
        sequenceDebug: new SequenceDebugSnapshot(false, null, pauseReason, null, []));

    private static SimulationCommandResult Accepted() => new(
        "test-command",
        true,
        0,
        TimeSpan.Zero,
        SimulationCommandErrorCode.None,
        null);

    private sealed record EventJournalContext(RuntimeDebuggerViewModel RuntimeDebugger);

    private sealed record RuntimeInspectorContext(RuntimeDebuggerViewModel RuntimeDebugger, bool IsRunMode);
}
