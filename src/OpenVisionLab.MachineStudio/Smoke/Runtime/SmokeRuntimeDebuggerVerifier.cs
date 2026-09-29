using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeRuntimeDebuggerReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeRuntimeDebuggerVerifier
{
    public static async Task<SmokeRuntimeDebuggerReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Action<Window> activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action pressSmokePointer,
        Action releaseSmokePointer,
        string? finalState,
        SmokeNativeInput input,
        SmokeWindowCapture capture,
        string reportPath)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-runtime-debugger-report requires --smoke-run-layout.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        var debugger = viewModel.RuntimeDebugger;
        await WaitForAsync(
            () => debugger.IsEnabled && debugger.Breakpoints.Count >= 2 && viewModel.RunCommand.CanExecute(null),
            "Runtime debugger did not become available from the authored runtime.");

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var journal = SmokeVisualTreeQuery.FindVisualDescendant<EventJournalView>(window)
            ?? throw new InvalidOperationException("Run records panel was unavailable.");
        var commandBar = SmokeVisualTreeQuery.FindVisualDescendant<GlobalCommandBarView>(window)
            ?? throw new InvalidOperationException("Global command bar was unavailable.");
        var stepButton = commandBar.SemanticSequenceStepButton;
        var semanticOnly = string.Equals(finalState, "semantic-step", StringComparison.OrdinalIgnoreCase);
        var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Check("semantic-step-global-binding", ReferenceEquals(stepButton.Command, debugger.SemanticStepCommand));
        Check("tick-not-in-command-bar", !SmokeVisualTreeQuery.FindVisualDescendants<Button>(commandBar)
            .Any(button => ReferenceEquals(button.Command, viewModel.StepCommand)));
        if (semanticOnly)
        {
            Check("ready-disables-semantic-step", !stepButton.IsEnabled && !debugger.SemanticStepCommand.CanExecute(null));
            capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-ready-disabled.png"));
        }
        inspector.RunInspectorScrollViewer.ScrollToTop();
        inspector.DebuggerBreakpointsExpander.IsExpanded = true;
        inspector.DebuggerWatchesExpander.IsExpanded = true;
        journal.DebuggerAlarmsExpander.IsExpanded = true;
        inspector.RuntimeDebuggerSectionAnchor.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        Check("debugger-card-visible", inspector.RuntimeDebuggerPanel.IsVisible);
        Check("breakpoint-selector-items-visible",
            inspector.DebuggerBreakpointComboBox.IsVisible
            && inspector.DebuggerBreakpointComboBox.Items.Count == debugger.Breakpoints.Count);
        Check("default-sequence-watch", debugger.Watches.Count == 1
            && debugger.Watches[0].Target.Kind == RuntimeWatchKind.Sequence);
        Check("empty-alarm-state", !debugger.HasAlarms
            && !debugger.HasAlarmHistory
            && debugger.AlarmSummaryText == OpenVisionLanguageService.T("Debugger.NoAlarms"));

        var breakpoint = debugger.Breakpoints[1];
        inspector.DebuggerBreakpointComboBox.SelectedItem = breakpoint;
        inspector.DebuggerBreakpointComboBox.Focus();
        inspector.DebuggerBreakpointComboBox.IsDropDownOpen = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("breakpoint-two-way-selection", ReferenceEquals(debugger.SelectedBreakpoint, breakpoint));
        Check("breakpoint-popup-open", inspector.DebuggerBreakpointComboBox.IsDropDownOpen);
        Check("breakpoint-selector-keyboard-focus", inspector.DebuggerBreakpointComboBox.IsKeyboardFocusWithin);
        inspector.DebuggerBreakpointComboBox.IsDropDownOpen = false;
        Check("breakpoint-command-available", inspector.ToggleSequenceBreakpointButton.Command.CanExecute(null));
        inspector.ToggleSequenceBreakpointButton.Command.Execute(null);
        await WaitForAsync(
            () => breakpoint.IsEnabled && !debugger.IsOperationPending,
            "Breakpoint was not confirmed by an immutable runtime snapshot.");
        Check("breakpoint-snapshot-roundtrip", viewModel.SceneSnapshots.Latest?.SequenceDebug.Breakpoints.Any(item =>
            item.SequenceId == breakpoint.SequenceId && item.StepId == breakpoint.StepId) == true);

        viewModel.RunCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                || viewModel.SceneSnapshots.Latest?.SequenceDebug.PauseReason == SequenceDebugPauseReason.Breakpoint,
            "Runtime did not enter the running state or reach the breakpoint before verification.");
        if (viewModel.IsRunning && viewModel.CycleStartCommand.CanExecute(null))
        {
            viewModel.CycleStartCommand.Execute(null);
        }
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.SequenceDebug.PauseReason == SequenceDebugPauseReason.Breakpoint,
            "Runtime did not pause before the selected breakpoint step.");
        var breakpointSnapshot = viewModel.SceneSnapshots.Latest
            ?? throw new InvalidOperationException("Breakpoint snapshot was unavailable.");
        var activeSequence = breakpointSnapshot.Sequences.FirstOrDefault(item =>
            item.SequenceId == breakpoint.SequenceId);
        Check("paused-before-breakpoint-step", activeSequence?.CurrentStepId == breakpoint.StepId);
        Check("breakpoint-reason-visible", debugger.PauseReasonText ==
            OpenVisionLanguageService.T("Debugger.PauseBreakpoint"));
        Check("structured-timeline-populated", debugger.Timeline.Count > 0
            && debugger.Timeline.All(item => !string.IsNullOrWhiteSpace(item.Code)
                && !string.IsNullOrWhiteSpace(item.Category)
                && !string.IsNullOrWhiteSpace(item.HeaderText)));

        var tickBeforeSemanticStep = breakpointSnapshot.TickIndex;
        Check("semantic-step-command-available", stepButton.Command.CanExecute(null));
        activateWindow(window);
        stepButton.BringIntoView();
        stepButton.Focus();
        movePointerToCenter(stepButton);
        Mouse.Synchronize();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        var activationDeadline = Environment.TickCount64 + 15000;
        while (!input.CheckPointerOwnership(window).IsOwned && Environment.TickCount64 < activationDeadline)
        {
            await Task.Delay(100);
        }
        var ownership = input.CheckPointerOwnership(window);
        if (!ownership.IsOwned) throw new InvalidOperationException(ownership.Diagnostic);
        Check("semantic-step-keyboard-focus", stepButton.IsKeyboardFocused);
        Check("semantic-step-hover", stepButton.IsMouseOver);
        if (semanticOnly) capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-hover-focus.png"));
        try
        {
            pressSmokePointer();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("semantic-step-pointer-down", stepButton.IsPressed);
            if (semanticOnly) capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-pressed.png"));
            input.SendMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            for (var attempt = 0; attempt < 100 && stepButton.IsPressed; attempt++) await Task.Delay(10);
        }
        finally
        {
            releaseSmokePointer();
        }
        await WaitForAsync(
            () => !debugger.IsOperationPending
                && viewModel.SceneSnapshots.Latest is { } snapshot
                && snapshot.TickIndex > tickBeforeSemanticStep
                && snapshot.SequenceDebug.PauseReason is SequenceDebugPauseReason.SemanticStep
                    or SequenceDebugPauseReason.SequenceCompleted,
            "Semantic next-step command did not stop at the next sequence boundary.");
        Check("semantic-step-advanced", viewModel.SceneSnapshots.Latest!.TickIndex > tickBeforeSemanticStep);

        if (semanticOnly)
        {
            // Expected transitions come from the authored AutomaticTransferCell fixture,
            // not from the command's resulting snapshot.
            Check("mouse-stops-at-authored-next-step", viewModel.SceneSnapshots.Latest!.Sequences
                .Single(item => item.SequenceId == "auto-transfer-cycle").CurrentStepId == "wait-stopper-extended");
            var tickBeforeKeyboard = viewModel.SceneSnapshots.Latest.TickIndex;
            input.MovePointerToCenter(window.SimulationWorkspaceButton);
            await Task.Delay(100);
            Check("semantic-step-mouse-leave", !stepButton.IsMouseOver && !stepButton.IsPressed);
            capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-after-click.png"));
            Keyboard.Focus(stepButton);
            if (!input.CheckPointerOwnership(window).IsOwned) throw new InvalidOperationException("Semantic keyboard input lost window ownership.");
            input.SendKey(0x20);
            await WaitForAsync(() => !debugger.IsOperationPending && viewModel.SceneSnapshots.Latest!.TickIndex > tickBeforeKeyboard
                && viewModel.SceneSnapshots.Latest.SequenceDebug.PauseReason == SequenceDebugPauseReason.SemanticStep,
                "Space did not advance to a semantic boundary.");
            Check("space-advances-multiple-ticks-to-next-step", viewModel.SceneSnapshots.Latest!.TickIndex > tickBeforeKeyboard + 1
                && viewModel.SceneSnapshots.Latest.Sequences.Single(item => item.SequenceId == "auto-transfer-cycle").CurrentStepId == "conveyor-forward");
            capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-keyboard.png"));

            var simulationMenu = SmokeVisualTreeQuery.FindVisualDescendant<MenuItem>(window,
                item => Equals(item.Header, OpenVisionLanguageService.T("Shell.Simulation")))!;
            var advancedMenu = SmokeVisualTreeQuery.FindVisualDescendant<MenuItem>(window,
                item => Equals(item.Header, OpenVisionLanguageService.T("Shell.AdvancedDebugging")))!;
            Check("tick-only-in-advanced-menu", !simulationMenu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, viewModel.StepCommand))
                && advancedMenu.Items.OfType<MenuItem>().Single().Command == viewModel.StepCommand);
            var tickBeforeF10 = viewModel.SceneSnapshots.Latest.TickIndex;
            if (!input.CheckPointerOwnership(window).IsOwned) throw new InvalidOperationException("Tick keyboard input lost window ownership.");
            input.SendKey(0x79);
            await WaitForAsync(() => viewModel.SceneSnapshots.Latest!.TickIndex > tickBeforeF10, "F10 did not advance a tick.");
            await Task.Delay(100);
            Check("f10-remains-exactly-one-tick", viewModel.SceneSnapshots.Latest!.TickIndex == tickBeforeF10 + 1 && !viewModel.IsRunning);

            viewModel.RunCommand.Execute(null);
            await WaitForAsync(() => viewModel.IsRunning && !stepButton.IsEnabled, "Running did not disable semantic step.");
            Check("running-disables-semantic-step", !debugger.SemanticStepCommand.CanExecute(null));
            capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-running-disabled.png"));
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning && stepButton.IsEnabled, "Pause did not restore semantic step.");
            Check("pause-restores-semantic-step", debugger.SemanticStepCommand.CanExecute(null));
            capture.Capture(window, Path.Combine(evidenceDirectory, "semantic-paused.png"));
            return new SmokeRuntimeDebuggerReport { Checks = checks, Failures = failures,
                Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window) };
        }

        var axisTarget = debugger.WatchTargets.FirstOrDefault(item => item.Kind == RuntimeWatchKind.Axis);
        if (axisTarget is not null)
        {
            inspector.DebuggerWatchTargetComboBox.SelectedItem = axisTarget;
            debugger.AddWatchCommand.Execute(null);
            Check("watch-selection-two-way", ReferenceEquals(debugger.SelectedWatchTarget, axisTarget));
            Check("axis-watch-added", debugger.Watches.Any(item => item.Target == axisTarget));
            Check("duplicate-watch-blocked", !debugger.AddWatchCommand.CanExecute(null));
        }
        else
        {
            Check("axis-watch-added", false);
            Check("duplicate-watch-blocked", false);
        }

        SimulationFaultTarget? faultTarget = null;
        if (viewModel.FaultManager.AvailableKinds.Any(option =>
                option.Kind == SimulationFaultKind.AxisMotionBlocked))
        {
            viewModel.FaultManager.SelectedKind = viewModel.FaultManager.AvailableKinds.Single(option =>
                option.Kind == SimulationFaultKind.AxisMotionBlocked);
            faultTarget = viewModel.FaultManager.Targets.FirstOrDefault();
            viewModel.FaultManager.SelectedTarget = faultTarget;
            if (viewModel.FaultManager.InjectCommand.CanExecute(null))
            {
                viewModel.FaultManager.InjectCommand.Execute(null);
                await WaitForAsync(
                    () => debugger.Alarms.Any(item => item.Source == faultTarget!.Id),
                    "Injected fault was not projected into the debugger alarm view.");
            }
        }
        Check("alarm-projected-with-recovery", debugger.Alarms.Any(item =>
            !string.IsNullOrWhiteSpace(item.Source)
            && !string.IsNullOrWhiteSpace(item.State)
            && !string.IsNullOrWhiteSpace(item.RecoveryText)));

        viewModel.Navigation.IsEvidenceExpanded = true;
        viewModel.Navigation.SelectedEvidenceTabIndex = 1;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var projectedAlarm = debugger.Alarms.FirstOrDefault(item =>
            string.Equals(item.Source, faultTarget?.Id, StringComparison.Ordinal));
        Check("alarm-history-occurrence-created", projectedAlarm is not null
            && debugger.AlarmHistory.Contains(projectedAlarm));
        if (projectedAlarm is not null)
        {
            Check("alarm-acknowledge-command-available",
                journal.AcknowledgeAllAlarmsButton.Command.CanExecute(null));
            Check("alarm-acknowledge-button-enabled", journal.AcknowledgeAllAlarmsButton.IsEnabled);
            activateWindow(window);
            journal.AcknowledgeAllAlarmsButton.BringIntoView();
            journal.AcknowledgeAllAlarmsButton.Focus();
            movePointerToCenter(journal.AcknowledgeAllAlarmsButton);
            Mouse.Capture(journal.AcknowledgeAllAlarmsButton, CaptureMode.SubTree);
            Mouse.Synchronize();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            Check("alarm-acknowledge-keyboard-focus",
                journal.AcknowledgeAllAlarmsButton.IsKeyboardFocused);
            Check("alarm-acknowledge-hover", journal.AcknowledgeAllAlarmsButton.IsMouseOver);
            var alarmPressedObserved = false;
            var alarmPressedDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ButtonBase.IsPressedProperty,
                typeof(ButtonBase))
                ?? throw new InvalidOperationException("The alarm acknowledgement pressed property descriptor was unavailable.");
            EventHandler alarmPressedChanged = (_, _) =>
            {
                alarmPressedObserved |= journal.AcknowledgeAllAlarmsButton.IsPressed;
            };
            alarmPressedDescriptor.AddValueChanged(journal.AcknowledgeAllAlarmsButton, alarmPressedChanged);
            try
            {
                pressSmokePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("alarm-acknowledge-pointer-capture",
                    journal.AcknowledgeAllAlarmsButton.IsMouseCaptureWithin
                    || alarmPressedObserved);
                Check("alarm-acknowledge-pointer-down-observed", alarmPressedObserved);
            }
            finally
            {
                releaseSmokePointer();
                Mouse.Capture(null);
                alarmPressedDescriptor.RemoveValueChanged(journal.AcknowledgeAllAlarmsButton, alarmPressedChanged);
            }
            if (debugger.AcknowledgeAllAlarmsCommand.CanExecute(null))
            {
                debugger.AcknowledgeAllAlarmsCommand.Execute(null);
            }
            await WaitForAsync(
                () => projectedAlarm.IsAcknowledged && debugger.UnacknowledgedAlarmCount == 0,
                "Alarm acknowledgement was not reflected by the session debugger state.");
            Check("alarm-remains-active-after-acknowledge",
                projectedAlarm.IsActive && debugger.Alarms.Contains(projectedAlarm));
            Check("alarm-acknowledge-command-disabled", !projectedAlarm.CanAcknowledge);
        }

        var selectedActiveFault = viewModel.FaultManager.ActiveFaults.FirstOrDefault(item =>
            string.Equals(item.TargetId, faultTarget?.Id, StringComparison.Ordinal));
        viewModel.FaultManager.SelectedActiveFault = selectedActiveFault;
        if (viewModel.FaultManager.ClearSelectedCommand.CanExecute(null))
        {
            viewModel.FaultManager.ClearSelectedCommand.Execute(null);
            await WaitForAsync(
                () => projectedAlarm is not null
                    && !projectedAlarm.IsActive
                    && !debugger.Alarms.Any(item => item.Source == faultTarget!.Id),
                "Cleared fault did not close the debugger alarm occurrence.");
        }
        Check("alarm-history-cleared-occurrence", projectedAlarm is not null
            && !projectedAlarm.IsActive
            && projectedAlarm.ClearedTick.HasValue
            && debugger.AlarmHistory.Contains(projectedAlarm));

        journal.DebuggerAlarmHistoryExpander.IsExpanded = true;
        journal.DebuggerAlarmHistoryExpander.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var alarmHistoryFilter = debugger.AlarmHistoryFilters
            .SingleOrDefault(item => item.State == RuntimeAlarmHistoryFilterState.Cleared);
        Check("alarm-history-filter-control-visible",
            journal.DebuggerAlarmHistoryFilterComboBox.IsVisible
            && journal.DebuggerAlarmHistoryFilterComboBox.Items.Count == 5);
        if (alarmHistoryFilter is not null)
        {
            journal.DebuggerAlarmHistoryFilterComboBox.BringIntoView();
            journal.DebuggerAlarmHistoryFilterComboBox.Focus();
            Keyboard.Focus(journal.DebuggerAlarmHistoryFilterComboBox);
            journal.DebuggerAlarmHistoryFilterComboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("alarm-history-filter-keyboard-focus",
                journal.DebuggerAlarmHistoryFilterComboBox.IsKeyboardFocusWithin);
            Check("alarm-history-filter-popup-open",
                journal.DebuggerAlarmHistoryFilterComboBox.IsDropDownOpen);
            journal.DebuggerAlarmHistoryFilterComboBox.IsDropDownOpen = false;
            journal.DebuggerAlarmHistoryFilterComboBox.SelectedItem = alarmHistoryFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("alarm-history-filter-two-way-selection",
                debugger.SelectedAlarmHistoryFilter?.State == RuntimeAlarmHistoryFilterState.Cleared);
            Check("alarm-history-filter-applied",
                debugger.VisibleAlarmHistory.Count > 0
                && debugger.VisibleAlarmHistory.All(item => !item.IsActive));

            var unacknowledgedFilter = debugger.AlarmHistoryFilters
                .Single(item => item.State == RuntimeAlarmHistoryFilterState.Unacknowledged);
            journal.DebuggerAlarmHistoryFilterComboBox.SelectedItem = unacknowledgedFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("alarm-history-filter-empty-state",
                debugger.HasAlarmHistory
                && !debugger.HasVisibleAlarmHistory
                && journal.DebuggerAlarmHistoryEmptyTextBlock.IsVisible);
            Check("alarm-history-filter-empty-text",
                journal.DebuggerAlarmHistoryEmptyTextBlock.Text == debugger.AlarmHistoryEmptyText);

            var allAlarmHistoryFilter = debugger.AlarmHistoryFilters.Single(item => item.State is null);
            journal.DebuggerAlarmHistoryFilterComboBox.SelectedItem = allAlarmHistoryFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("alarm-history-filter-clears-to-all",
                debugger.SelectedAlarmHistoryFilter?.State is null
                && debugger.VisibleAlarmHistory.Count == debugger.AlarmHistory.Count
                && !debugger.HasAlarmHistoryEmptyState);
        }

        viewModel.Navigation.SelectedEvidenceTabIndex = 0;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var faultFilter = debugger.TimelineFilters.FirstOrDefault(item => item.Key == "Fault");
        Check("timeline-filter-control-visible",
            journal.DebuggerTimelineFilterComboBox.IsVisible
            && journal.DebuggerTimelineFilterComboBox.Items.Count >= 2);
        if (faultFilter is not null)
        {
            journal.DebuggerTimelineFilterComboBox.BringIntoView();
            journal.DebuggerTimelineFilterComboBox.Focus();
            Keyboard.Focus(journal.DebuggerTimelineFilterComboBox);
            journal.DebuggerTimelineFilterComboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-filter-keyboard-focus",
                journal.DebuggerTimelineFilterComboBox.IsKeyboardFocusWithin);
            Check("timeline-filter-popup-open",
                journal.DebuggerTimelineFilterComboBox.IsDropDownOpen);
            journal.DebuggerTimelineFilterComboBox.IsDropDownOpen = false;
            journal.DebuggerTimelineFilterComboBox.SelectedItem = faultFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-filter-two-way-selection",
                debugger.SelectedTimelineFilter?.Key == "Fault");
            Check("timeline-filter-applied",
                debugger.HasTimeline
                && debugger.Timeline.All(item => item.Category == OpenVisionLanguageService.T("Runtime.Category.Fault")));

            for (var index = 0; index < 200; index++)
            {
                debugger.ApplyEvent(new SimulationEvent(
                    10_000 + index,
                    10_000 + index,
                    TimeSpan.FromMilliseconds(index * 5),
                    "Sequence",
                    $"Smoke.Sequence.{index}",
                    $"Smoke sequence {index}"));
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-filter-empty-state",
                !debugger.HasTimeline && journal.DebuggerTimelineEmptyTextBlock.IsVisible);
            Check("timeline-filter-clear-remains-available",
                debugger.ClearTimelineCommand.CanExecute(null));
            debugger.ClearTimelineCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-filter-clears-to-all",
                debugger.SelectedTimelineFilter?.Key == string.Empty
                && debugger.TimelineFilters.Count == 1
                && !debugger.HasTimeline);
        }
        else
        {
            Check("timeline-filter-keyboard-focus", false);
            Check("timeline-filter-popup-open", false);
            Check("timeline-filter-two-way-selection", false);
            Check("timeline-filter-applied", false);
            Check("timeline-filter-empty-state", false);
            Check("timeline-filter-clear-remains-available", false);
            Check("timeline-filter-clears-to-all", false);
        }

        debugger.ApplyEvent(new SimulationEvent(
            20_000,
            20_000,
            TimeSpan.FromMilliseconds(5),
            "Sequence",
            "Smoke.Sequence.Severity",
            "Smoke sequence severity"));
        debugger.ApplyEvent(new SimulationEvent(
            20_001,
            20_001,
            TimeSpan.FromMilliseconds(10),
            "Fault",
            "Smoke.Fault.Severity",
            "Smoke fault severity"));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var alarmSeverityFilter = debugger.TimelineSeverityFilters.FirstOrDefault(item => item.Key == "Alarm");
        Check("timeline-severity-filter-control-visible",
            journal.DebuggerTimelineSeverityFilterComboBox.IsVisible
            && journal.DebuggerTimelineSeverityFilterComboBox.Items.Count == 5);
        if (alarmSeverityFilter is not null)
        {
            journal.DebuggerTimelineSeverityFilterComboBox.BringIntoView();
            journal.DebuggerTimelineSeverityFilterComboBox.Focus();
            Keyboard.Focus(journal.DebuggerTimelineSeverityFilterComboBox);
            journal.DebuggerTimelineSeverityFilterComboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-severity-filter-keyboard-focus",
                journal.DebuggerTimelineSeverityFilterComboBox.IsKeyboardFocusWithin);
            Check("timeline-severity-filter-popup-open",
                journal.DebuggerTimelineSeverityFilterComboBox.IsDropDownOpen);
            journal.DebuggerTimelineSeverityFilterComboBox.IsDropDownOpen = false;
            journal.DebuggerTimelineSeverityFilterComboBox.SelectedItem = alarmSeverityFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-severity-filter-two-way-selection",
                debugger.SelectedTimelineSeverityFilter?.Key == "Alarm");
            Check("timeline-severity-filter-applied",
                debugger.HasTimeline
                && debugger.Timeline.All(item => item.Code == "Smoke.Fault.Severity"));

            var sequenceFilter = debugger.TimelineFilters.FirstOrDefault(item => item.Key == "Sequence");
            Check("timeline-severity-filter-category-option-available", sequenceFilter is not null);
            if (sequenceFilter is not null)
            {
                debugger.SelectedTimelineFilter = sequenceFilter;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                journal.DebuggerTimelineEmptyTextBlock.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("timeline-severity-filter-combined-selection",
                    debugger.SelectedTimelineFilter?.Key == "Sequence"
                    && debugger.SelectedTimelineSeverityFilter?.Key == "Alarm");
                Check("timeline-severity-filter-combined-empty-state",
                    !debugger.HasTimeline && journal.DebuggerTimelineEmptyTextBlock.IsVisible);
                Check("timeline-severity-filter-clear-remains-available",
                    debugger.ClearTimelineCommand.CanExecute(null));
            }
            else
            {
                Check("timeline-severity-filter-combined-selection", false);
                Check("timeline-severity-filter-combined-empty-state", false);
                Check("timeline-severity-filter-clear-remains-available", false);
            }

            debugger.ClearTimelineCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("timeline-severity-filter-clears-to-all",
                debugger.SelectedTimelineFilter?.Key == string.Empty
                && debugger.SelectedTimelineSeverityFilter?.Key == string.Empty
                && !debugger.HasTimeline);
        }
        else
        {
            Check("timeline-severity-filter-keyboard-focus", false);
            Check("timeline-severity-filter-popup-open", false);
            Check("timeline-severity-filter-two-way-selection", false);
            Check("timeline-severity-filter-applied", false);
            Check("timeline-severity-filter-combined-selection", false);
            Check("timeline-severity-filter-combined-empty-state", false);
            Check("timeline-severity-filter-clear-remains-available", false);
            Check("timeline-severity-filter-clears-to-all", false);
        }

        debugger.ApplyEvent(new SimulationEvent(
            30_000,
            30_000,
            TimeSpan.FromMilliseconds(15),
            "Command",
            "Smoke.Command.Context",
            "Smoke command context",
            "smoke-command-123"));
        debugger.ApplyEvent(new SimulationEvent(
            30_001,
            30_001,
            TimeSpan.FromMilliseconds(20),
            "Sequence",
            "Smoke.Sequence.NoCommand",
            "Smoke sequence without command"));
        debugger.SelectedTimelineFilter = debugger.TimelineFilters.Single(item => item.Key == string.Empty);
        debugger.SelectedTimelineSeverityFilter = debugger.TimelineSeverityFilters
            .Single(item => item.Key == string.Empty);
        journal.DebuggerTimelineList.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var renderedTimelineText = SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(
                journal.DebuggerTimelineList)
            .Select(item => item.Text)
            .ToArray();
        Check("timeline-command-context-model",
            debugger.Timeline.Any(item => item.CommandId == "smoke-command-123"
                && item.CommandIdText.Contains("smoke-command-123", StringComparison.Ordinal))
            && debugger.Timeline.Any(item => item.Code == "Smoke.Sequence.NoCommand"
                && item.CommandId is null));
        Check("timeline-command-context-rendered",
            renderedTimelineText.Any(item =>
                item.Contains("smoke-command-123", StringComparison.Ordinal)));
        Check("timeline-no-command-rendered",
            renderedTimelineText.Any(item => string.Equals(
                item,
                OpenVisionLanguageService.T("Debugger.TimelineNoCommand"),
                StringComparison.Ordinal)));

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(
            originalLanguage == OpenVisionLanguage.Korean
                ? OpenVisionLanguage.English
                : OpenVisionLanguage.Korean,
            save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var expectedAlarmSummary = debugger.Alarms.Count switch
        {
            0 => OpenVisionLanguageService.T("Debugger.NoAlarms"),
            1 => OpenVisionLanguageService.T("Debugger.OneAlarm"),
            _ => string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Debugger.AlarmCount"),
                debugger.Alarms.Count)
        };
        Check("language-refreshes-debugger", debugger.AlarmSummaryText == expectedAlarmSummary);
        Check("language-refreshes-alarm-history", debugger.HasAlarmHistory
            && debugger.AlarmHistorySummaryText.Contains("200", StringComparison.Ordinal));
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewModel.IsRunMode = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("design-mode-disables-debug-commands", !debugger.IsEnabled
            && !debugger.SemanticStepCommand.CanExecute(null)
            && !debugger.ToggleBreakpointCommand.CanExecute(null));
        viewModel.IsRunMode = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("run-mode-restores-debugger", debugger.IsEnabled);

        if (string.Equals(finalState, "alarms", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.Navigation.SelectedEvidenceTabIndex = 1;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            journal.DebuggerAlarmHistoryExpander.IsExpanded = true;
            journal.DebuggerAlarmsExpander.BringIntoView();
            journal.DebuggerAlarmHistoryExpander.BringIntoView();
        }
        else if (string.Equals(finalState, "timeline", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.Navigation.SelectedEvidenceTabIndex = 0;
        }
        else if (string.IsNullOrWhiteSpace(finalState)
            || string.Equals(finalState, "top", StringComparison.OrdinalIgnoreCase))
        {
            inspector.RunInspectorScrollViewer.ScrollToTop();
            inspector.RuntimeDebuggerSectionAnchor.BringIntoView();
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported --smoke-runtime-debugger-state '{finalState}'. Expected top, timeline, or alarms.");
        }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        return new SmokeRuntimeDebuggerReport
        {
            Checks = checks,
            Failures = failures
        };
    }
}
