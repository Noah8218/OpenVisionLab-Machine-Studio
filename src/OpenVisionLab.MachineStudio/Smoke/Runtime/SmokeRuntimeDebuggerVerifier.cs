using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Inspector;
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
        string? finalState)
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
        inspector.RunInspectorScrollViewer.ScrollToTop();
        inspector.DebuggerBreakpointsExpander.IsExpanded = true;
        inspector.DebuggerWatchesExpander.IsExpanded = true;
        inspector.DebuggerTimelineExpander.IsExpanded = true;
        inspector.DebuggerAlarmsExpander.IsExpanded = true;
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
        Check("semantic-step-command-available", inspector.SemanticSequenceStepButton.Command.CanExecute(null));
        activateWindow(window);
        inspector.SemanticSequenceStepButton.BringIntoView();
        inspector.SemanticSequenceStepButton.Focus();
        movePointerToCenter(inspector.SemanticSequenceStepButton);
        Mouse.Capture(inspector.SemanticSequenceStepButton, CaptureMode.SubTree);
        Mouse.Synchronize();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        Check("semantic-step-keyboard-focus", inspector.SemanticSequenceStepButton.IsKeyboardFocused);
        Check("semantic-step-hover", inspector.SemanticSequenceStepButton.IsMouseOver);
        pressSmokePointer();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("semantic-step-pointer-down", inspector.SemanticSequenceStepButton.IsPressed);
        releaseSmokePointer();
        Mouse.Capture(null);
        if (inspector.SemanticSequenceStepButton.Command.CanExecute(null))
        {
            inspector.SemanticSequenceStepButton.Command.Execute(null);
        }
        await WaitForAsync(
            () => !debugger.IsOperationPending
                && viewModel.SceneSnapshots.Latest is { } snapshot
                && snapshot.TickIndex > tickBeforeSemanticStep
                && snapshot.SequenceDebug.PauseReason is SequenceDebugPauseReason.SemanticStep
                    or SequenceDebugPauseReason.SequenceCompleted,
            "Semantic next-step command did not stop at the next sequence boundary.");
        Check("semantic-step-advanced", viewModel.SceneSnapshots.Latest!.TickIndex > tickBeforeSemanticStep);

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

        var projectedAlarm = debugger.Alarms.FirstOrDefault(item =>
            string.Equals(item.Source, faultTarget?.Id, StringComparison.Ordinal));
        Check("alarm-history-occurrence-created", projectedAlarm is not null
            && debugger.AlarmHistory.Contains(projectedAlarm));
        if (projectedAlarm is not null)
        {
            Check("alarm-acknowledge-command-available",
                inspector.AcknowledgeAllAlarmsButton.Command.CanExecute(null));
            Check("alarm-acknowledge-button-enabled", inspector.AcknowledgeAllAlarmsButton.IsEnabled);
            activateWindow(window);
            inspector.AcknowledgeAllAlarmsButton.BringIntoView();
            inspector.AcknowledgeAllAlarmsButton.Focus();
            movePointerToCenter(inspector.AcknowledgeAllAlarmsButton);
            Mouse.Capture(inspector.AcknowledgeAllAlarmsButton, CaptureMode.SubTree);
            Mouse.Synchronize();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            Check("alarm-acknowledge-keyboard-focus",
                inspector.AcknowledgeAllAlarmsButton.IsKeyboardFocused);
            Check("alarm-acknowledge-hover", inspector.AcknowledgeAllAlarmsButton.IsMouseOver);
            var alarmPressedObserved = false;
            var alarmPressedDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ButtonBase.IsPressedProperty,
                typeof(ButtonBase))
                ?? throw new InvalidOperationException("The alarm acknowledgement pressed property descriptor was unavailable.");
            EventHandler alarmPressedChanged = (_, _) =>
            {
                alarmPressedObserved |= inspector.AcknowledgeAllAlarmsButton.IsPressed;
            };
            alarmPressedDescriptor.AddValueChanged(inspector.AcknowledgeAllAlarmsButton, alarmPressedChanged);
            try
            {
                pressSmokePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("alarm-acknowledge-pointer-capture",
                    inspector.AcknowledgeAllAlarmsButton.IsMouseCaptureWithin
                    || alarmPressedObserved);
                Check("alarm-acknowledge-pointer-down-observed", alarmPressedObserved);
            }
            finally
            {
                releaseSmokePointer();
                Mouse.Capture(null);
                alarmPressedDescriptor.RemoveValueChanged(inspector.AcknowledgeAllAlarmsButton, alarmPressedChanged);
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
            inspector.DebuggerAlarmHistoryExpander.IsExpanded = true;
            inspector.DebuggerAlarmsExpander.BringIntoView();
            inspector.DebuggerAlarmHistoryExpander.BringIntoView();
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
                $"Unsupported --smoke-runtime-debugger-state '{finalState}'. Expected top or alarms.");
        }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        return new SmokeRuntimeDebuggerReport
        {
            Checks = checks,
            Failures = failures
        };
    }
}
