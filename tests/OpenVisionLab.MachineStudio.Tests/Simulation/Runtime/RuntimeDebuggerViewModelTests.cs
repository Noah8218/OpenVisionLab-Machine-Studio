using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeDebuggerViewModelTests
{
    [Fact]
    public async Task SemanticStep_UsesActiveSequence_RejectsDuplicatesAndRunning_ThenRecovers()
    {
        OpenVisionLanguageService.Load();
        var commands = new List<SimulationCommand>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new RuntimeDebuggerViewModel(async command =>
        {
            commands.Add(command);
            await gate.Task;
            return Accepted(command);
        });
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        Assert.False(viewModel.SemanticStepCommand.CanExecute(null));
        viewModel.ApplySnapshot(CreateSnapshot());
        Assert.True(viewModel.SemanticStepCommand.CanExecute(null));
        viewModel.SemanticStepCommand.Execute(null);
        await WaitUntilAsync(() => commands.Count == 1);
        Assert.False(viewModel.SemanticStepCommand.CanExecute(null));
        viewModel.SemanticStepCommand.Execute(null);
        Assert.Equal("cycle", Assert.IsType<StepSequenceCommand>(Assert.Single(commands)).SequenceId);
        gate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsOperationPending);
        viewModel.ApplySnapshot(CreateSnapshot(runMode: SimulationRunMode.RealTime));
        Assert.False(viewModel.SemanticStepCommand.CanExecute(null));
        viewModel.ApplySnapshot(CreateSnapshot());
        Assert.True(viewModel.SemanticStepCommand.CanExecute(null));
        viewModel.SetEnabled(false, invalidateCommands: true);
        Assert.False(viewModel.SemanticStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_UseSelectedRuntimeTargets_AndPreventRepeatedExecution()
    {
        OpenVisionLanguageService.Load();
        var dispatched = new List<SimulationCommand>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new RuntimeDebuggerViewModel(async command =>
        {
            dispatched.Add(command);
            await gate.Task;
            return Accepted(command);
        });
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(new SequenceDebugSnapshot(
            false,
            null,
            SequenceDebugPauseReason.None,
            null,
            [new SequenceBreakpointSnapshot("cycle", "on")])));

        viewModel.SelectedBreakpoint = viewModel.Breakpoints.Single(item => item.StepId == "off");
        viewModel.ToggleBreakpointCommand.Execute(null);
        await WaitUntilAsync(() => dispatched.Count == 1);

        Assert.False(viewModel.ToggleBreakpointCommand.CanExecute(null));
        viewModel.ToggleBreakpointCommand.Execute(null);
        Assert.Single(dispatched);
        var command = Assert.IsType<SetSequenceBreakpointCommand>(dispatched[0]);
        Assert.Equal("cycle", command.SequenceId);
        Assert.Equal("off", command.StepId);
        Assert.True(command.IsEnabled);

        gate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsOperationPending);
    }

    [Fact]
    public async Task Dispose_SuppressesLateOperationPublication_AndIsIdempotent()
    {
        OpenVisionLanguageService.Load();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new RuntimeDebuggerViewModel(async command =>
        {
            await gate.Task;
            return Accepted(command);
        });
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(new SequenceDebugSnapshot(
            false,
            null,
            SequenceDebugPauseReason.None,
            null,
            [new SequenceBreakpointSnapshot("cycle", "on")])));
        viewModel.SelectedBreakpoint = viewModel.Breakpoints.Single(item => item.StepId == "off");
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ToggleBreakpointCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsOperationPending);

        viewModel.Dispose();
        viewModel.Dispose();
        gate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsOperationPending);

        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.ReadyHint"),
            viewModel.OperationStatusText);
        Assert.DoesNotContain(nameof(viewModel.OperationStatusText), changedProperties);
        Assert.False(viewModel.ToggleBreakpointCommand.CanExecute(null));
        Assert.False(viewModel.AddWatchCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_NotifiesCommandsOfFinalAdmission()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(
            new SequenceDebugSnapshot(
                false,
                null,
                SequenceDebugPauseReason.User,
                null,
                [new SequenceBreakpointSnapshot("cycle", "on")])));
        viewModel.SelectedBreakpoint = viewModel.Breakpoints.Single(item => item.StepId == "off");

        var semanticStepNotifications = 0;
        var toggleBreakpointNotifications = 0;
        viewModel.SemanticStepCommand.CanExecuteChanged += (_, _) => semanticStepNotifications++;
        viewModel.ToggleBreakpointCommand.CanExecuteChanged += (_, _) => toggleBreakpointNotifications++;

        viewModel.Dispose();

        Assert.False(viewModel.SemanticStepCommand.CanExecute(null));
        Assert.False(viewModel.ToggleBreakpointCommand.CanExecute(null));
        Assert.Equal(1, semanticStepNotifications);
        Assert.Equal(1, toggleBreakpointNotifications);
    }

    [Fact]
    public async Task ProjectReload_SuppressesLateOperationPublication()
    {
        OpenVisionLanguageService.Load();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new RuntimeDebuggerViewModel(async command =>
        {
            await gate.Task;
            return Accepted(command);
        });
        var project = CreateProject();
        viewModel.LoadProject(project, resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(new SequenceDebugSnapshot(
            false,
            null,
            SequenceDebugPauseReason.None,
            null,
            [new SequenceBreakpointSnapshot("cycle", "on")])));
        viewModel.SelectedBreakpoint = viewModel.Breakpoints.Single(item => item.StepId == "off");

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ToggleBreakpointCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsOperationPending);

        viewModel.LoadProject(project, resetSession: true);
        gate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsOperationPending);

        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.ReadyHint"),
            viewModel.OperationStatusText);
        Assert.DoesNotContain(nameof(viewModel.OperationStatusText), changedProperties);
    }

    [Fact]
    public void ProjectReset_ClearsSnapshotBackedAlarmProjection()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        var project = CreateProject();
        viewModel.LoadProject(project, resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(faults:
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-x",
                null,
                5,
                TimeSpan.FromMilliseconds(25))
        ]));

        Assert.Single(viewModel.Alarms);
        Assert.NotEqual(
            OpenVisionLanguageService.T("Debugger.NoActiveSequence"),
            viewModel.SequenceStateText);

        viewModel.LoadProject(project, resetSession: true);

        Assert.Empty(viewModel.Alarms);
        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.NoActiveSequence"),
            viewModel.SequenceStateText);

        viewModel.RefreshLocalization();

        Assert.Empty(viewModel.Alarms);
        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.NoActiveSequence"),
            viewModel.SequenceStateText);
    }

    [Fact]
    public void Snapshot_ProjectsBreakpointsWatchesAndRecoveryAlarms()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        var snapshot = CreateSnapshot(
            new SequenceDebugSnapshot(
                false,
                null,
                SequenceDebugPauseReason.Breakpoint,
                "off",
                [new SequenceBreakpointSnapshot("cycle", "off")]),
            [new SimulationFaultSnapshot(SimulationFaultKind.AxisMotionBlocked, "axis-x", null, 5, TimeSpan.FromMilliseconds(25))]);

        viewModel.ApplySnapshot(snapshot);

        Assert.True(viewModel.Breakpoints.Single(item => item.StepId == "off").IsEnabled);
        Assert.Equal(OpenVisionLanguageService.T("Debugger.PauseBreakpoint"), viewModel.PauseReasonText);
        Assert.Single(viewModel.Watches);
        Assert.Contains("Running", viewModel.Watches[0].ValueText, StringComparison.Ordinal);
        var alarm = Assert.Single(viewModel.Alarms);
        Assert.Equal("axis-x", alarm.Source);
        Assert.Equal(OpenVisionLanguageService.T("Debugger.RecoveryClearFault"), alarm.RecoveryText);
    }

    [Fact]
    public void Snapshot_ProjectsRetryRecoveryForFaultedSequence()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(
            sequence: new SequenceExecutionSnapshot(
                "cycle",
                SequenceExecutionStatus.Faulted,
                "on",
                0,
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(25),
                5,
                new SequenceExecutionError(
                    SequenceExecutionErrorCode.SequenceWatchdogTimedOut,
                    "cycle",
                    "on",
                    "Sequence watchdog timed out."),
                TimeSpan.FromMilliseconds(25))));

        var alarm = Assert.Single(viewModel.Alarms);

        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.RecoveryRetry"),
            alarm.RecoveryText);
    }

    [Fact]
    public void Snapshot_ProjectsRecoveredWorkpieceFailureAndClearsItAfterReset()
    {
        OpenVisionLanguageService.Load();
        using var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        var recoveredSequence = new SequenceExecutionSnapshot(
            "cycle",
            SequenceExecutionStatus.Completed,
            "complete",
            2,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(25),
            5,
            new SequenceExecutionError(
                SequenceExecutionErrorCode.WorkpieceOperationFailed,
                "cycle",
                "feed-a-again",
                "The target workpiece position is occupied."),
            TimeSpan.FromSeconds(10));

        viewModel.ApplySnapshot(CreateSnapshot(sequence: recoveredSequence));

        var alarm = Assert.Single(viewModel.Alarms);
        Assert.Equal("Main cycle", alarm.Source);
        Assert.Contains(nameof(SequenceExecutionErrorCode.WorkpieceOperationFailed), alarm.State, StringComparison.Ordinal);
        Assert.Equal(OpenVisionLanguageService.T("Debugger.RecoveryReset"), alarm.RecoveryText);

        viewModel.ApplySnapshot(CreateSnapshot(sequence: recoveredSequence with { LastError = null }));

        Assert.Empty(viewModel.Alarms);
        Assert.False(alarm.IsActive);
        Assert.Same(alarm, Assert.Single(viewModel.AlarmHistory));
    }

    [Fact]
    public void AlarmLifecycle_PreservesAcknowledgementAndCreatesNewOccurrenceAfterClear()
    {
        OpenVisionLanguageService.Load();
        var dispatched = new List<SimulationCommand>();
        var viewModel = new RuntimeDebuggerViewModel(command =>
        {
            dispatched.Add(command);
            return Task.FromResult(Accepted(command));
        });
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        var activeSnapshot = CreateSnapshot(faults:
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-x",
                null,
                5,
                TimeSpan.FromMilliseconds(25))
        ]);

        viewModel.ApplySnapshot(activeSnapshot);
        viewModel.ApplySnapshot(activeSnapshot);

        var firstOccurrence = Assert.Single(viewModel.Alarms);
        Assert.Single(viewModel.AlarmHistory);
        Assert.Equal(1, viewModel.UnacknowledgedAlarmCount);
        viewModel.AcknowledgeAlarmCommand.Execute(firstOccurrence);

        Assert.Empty(dispatched);
        Assert.True(firstOccurrence.IsAcknowledged);
        Assert.True(firstOccurrence.IsActive);
        Assert.False(firstOccurrence.CanAcknowledge);
        Assert.Equal(0, viewModel.UnacknowledgedAlarmCount);

        viewModel.ApplySnapshot(CreateSnapshot(faults: []));

        Assert.Empty(viewModel.Alarms);
        Assert.False(firstOccurrence.IsActive);
        Assert.True(firstOccurrence.ClearedTick.HasValue);
        Assert.Single(viewModel.AlarmHistory);

        viewModel.ApplySnapshot(activeSnapshot);

        var secondOccurrence = Assert.Single(viewModel.Alarms);
        Assert.Equal(2, viewModel.AlarmHistory.Count);
        Assert.NotSame(firstOccurrence, secondOccurrence);
        Assert.False(secondOccurrence.IsAcknowledged);
        Assert.True(secondOccurrence.IsActive);

        viewModel.RefreshLocalization();

        Assert.Equal(2, viewModel.AlarmHistory.Count);
        Assert.True(viewModel.AlarmHistory[0].OccurrenceText.Length > 0);
        Assert.True(viewModel.AlarmHistory[1].ClearedAtText.Length > 0);
    }

    [Fact]
    public void AlarmHistoryFilter_UsesLifecycleStateAndPreservesRawHistory()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        var project = CreateProject();
        viewModel.LoadProject(project, resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        var history = viewModel.AlarmHistory;
        var visibleHistory = viewModel.VisibleAlarmHistory;

        viewModel.ApplySnapshot(CreateSnapshot(faults:
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-a",
                null,
                1,
                TimeSpan.FromMilliseconds(5)),
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisFollowingError,
                "axis-b",
                null,
                1,
                TimeSpan.FromMilliseconds(5))
        ]));
        viewModel.AcknowledgeAllAlarmsCommand.Execute(null);
        viewModel.ApplySnapshot(CreateSnapshot(faults:
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisFollowingError,
                "axis-b",
                null,
                2,
                TimeSpan.FromMilliseconds(10))
        ]));
        viewModel.ApplySnapshot(CreateSnapshot(faults:
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisFollowingError,
                "axis-b",
                null,
                3,
                TimeSpan.FromMilliseconds(15)),
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-c",
                null,
                3,
                TimeSpan.FromMilliseconds(15))
        ]));

        Assert.Equal(3, history.Count);
        Assert.Equal(3, visibleHistory.Count);
        Assert.Equal(5, viewModel.AlarmHistoryFilters.Count);
        Assert.Same(history, viewModel.AlarmHistory);
        Assert.Same(visibleHistory, viewModel.VisibleAlarmHistory);

        viewModel.SelectedAlarmHistoryFilter = viewModel.AlarmHistoryFilters
            .Single(item => item.State == RuntimeAlarmHistoryFilterState.Active);
        Assert.Equal(2, visibleHistory.Count);
        Assert.All(visibleHistory, item => Assert.True(item.IsActive));

        viewModel.SelectedAlarmHistoryFilter = viewModel.AlarmHistoryFilters
            .Single(item => item.State == RuntimeAlarmHistoryFilterState.Cleared);
        Assert.Single(visibleHistory);
        Assert.False(visibleHistory[0].IsActive);

        viewModel.SelectedAlarmHistoryFilter = viewModel.AlarmHistoryFilters
            .Single(item => item.State == RuntimeAlarmHistoryFilterState.Acknowledged);
        Assert.Equal(2, visibleHistory.Count);
        Assert.All(visibleHistory, item => Assert.True(item.IsAcknowledged));

        viewModel.SelectedAlarmHistoryFilter = viewModel.AlarmHistoryFilters
            .Single(item => item.State == RuntimeAlarmHistoryFilterState.Unacknowledged);
        Assert.Single(visibleHistory);
        Assert.False(visibleHistory[0].IsAcknowledged);

        viewModel.AcknowledgeAllAlarmsCommand.Execute(null);

        Assert.Empty(visibleHistory);
        Assert.True(viewModel.HasAlarmHistory);
        Assert.True(viewModel.HasAlarmHistoryEmptyState);
        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.NoMatchingAlarmHistory"),
            viewModel.AlarmHistoryEmptyText);

        viewModel.SelectedAlarmHistoryFilter = viewModel.AlarmHistoryFilters
            .Single(item => item.State is null);
        Assert.Equal(3, visibleHistory.Count);
        Assert.False(viewModel.HasAlarmHistoryEmptyState);

        viewModel.LoadProject(project, resetSession: true);

        Assert.Null(viewModel.SelectedAlarmHistoryFilter?.State);
        Assert.Empty(history);
        Assert.Empty(visibleHistory);
        Assert.False(viewModel.HasAlarmHistory);
    }

    [Fact]
    public void AlarmAcknowledgement_AllActiveRowsArePresentationOnly()
    {
        OpenVisionLanguageService.Load();
        var dispatched = new List<SimulationCommand>();
        var viewModel = new RuntimeDebuggerViewModel(command =>
        {
            dispatched.Add(command);
            return Task.FromResult(Accepted(command));
        });
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);
        viewModel.ApplySnapshot(CreateSnapshot(faults:
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-x",
                null,
                5,
                TimeSpan.FromMilliseconds(25)),
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisFollowingError,
                "axis-x",
                null,
                5,
                TimeSpan.FromMilliseconds(25))
        ]));

        Assert.True(viewModel.AcknowledgeAllAlarmsCommand.CanExecute(null));
        viewModel.AcknowledgeAllAlarmsCommand.Execute(null);

        Assert.Equal(2, viewModel.Alarms.Count);
        Assert.All(viewModel.Alarms, alarm =>
        {
            Assert.True(alarm.IsAcknowledged);
            Assert.True(alarm.IsActive);
        });
        Assert.False(viewModel.AcknowledgeAllAlarmsCommand.CanExecute(null));
        Assert.Empty(dispatched);
    }

    [Fact]
    public void AlarmHistory_IsBoundedAndClearedByProjectReset()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        var project = CreateProject();
        viewModel.LoadProject(project, resetSession: true);
        viewModel.SetEnabled(true, invalidateCommands: true);

        for (var index = 0; index < 205; index++)
        {
            viewModel.ApplySnapshot(CreateSnapshot(faults:
            [
                new SimulationFaultSnapshot(
                    SimulationFaultKind.AxisMotionBlocked,
                    $"axis-{index}",
                    null,
                    index,
                    TimeSpan.FromMilliseconds(index))
            ]));
        }

        Assert.Equal(200, viewModel.AlarmHistory.Count);
        Assert.Single(viewModel.Alarms);

        viewModel.LoadProject(project, resetSession: true);

        Assert.Empty(viewModel.Alarms);
        Assert.Empty(viewModel.AlarmHistory);
        Assert.False(viewModel.HasAlarmHistory);
    }

    [Fact]
    public void Timeline_RetainsLatestTwoHundredStructuredEvents()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));

        for (var index = 0; index < 205; index++)
        {
            viewModel.ApplyEvent(new SimulationEvent(
                index,
                index,
                TimeSpan.FromMilliseconds(index * 5),
                "Sequence",
                $"Code{index}",
                $"Message {index}"));
        }

        Assert.Equal(200, viewModel.Timeline.Count);
        Assert.Equal(204, viewModel.Timeline[0].EventIndex);
        Assert.Equal(5, viewModel.Timeline[^1].EventIndex);
        Assert.Equal("Code204", viewModel.Timeline[0].Code);
        Assert.Equal("Message 204", viewModel.Timeline[0].Message);
    }

    [Fact]
    public void TimelineCommandContext_PreservesCommandIdAndHandlesNonCommandEvents()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));

        viewModel.ApplyEvent(new SimulationEvent(
            1,
            1,
            TimeSpan.Zero,
            "Command",
            "Command.Accepted",
            "Command accepted",
            "command-7"));
        viewModel.ApplyEvent(new SimulationEvent(
            2,
            2,
            TimeSpan.FromMilliseconds(5),
            "Sequence",
            "Sequence.Started",
            "Sequence started"));

        var commandItem = Assert.Single(viewModel.Timeline, item => item.EventIndex == 1);
        Assert.Equal("command-7", commandItem.CommandId);
        Assert.Contains("command-7", commandItem.CommandIdText, StringComparison.Ordinal);
        Assert.Equal("command-7", commandItem.CommandIdTooltip);

        var nonCommandItem = Assert.Single(viewModel.Timeline, item => item.EventIndex == 2);
        Assert.Null(nonCommandItem.CommandId);
        Assert.Equal(
            OpenVisionLanguageService.T("Debugger.TimelineNoCommand"),
            nonCommandItem.CommandIdText);
        Assert.Equal(nonCommandItem.CommandIdText, nonCommandItem.CommandIdTooltip);
    }

    [Fact]
    public void TimelineCategoryFilter_ShowsSelectedCategoryAndKeepsClearAvailable()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));

        viewModel.ApplyEvent(new SimulationEvent(
            1,
            1,
            TimeSpan.Zero,
            "Sequence",
            "Sequence.Started",
            "Sequence started"));
        viewModel.ApplyEvent(new SimulationEvent(
            2,
            2,
            TimeSpan.FromMilliseconds(5),
            "Fault",
            "Fault.Raised",
            "Fault raised"));
        viewModel.ApplyEvent(new SimulationEvent(
            3,
            3,
            TimeSpan.FromMilliseconds(10),
            "Sequence",
            "Sequence.Completed",
            "Sequence completed"));

        viewModel.SelectedTimelineFilter = viewModel.TimelineFilters.Single(item => item.Key == "Fault");

        var item = Assert.Single(viewModel.Timeline);
        Assert.Equal("Fault.Raised", item.Code);
        Assert.Equal("Fault raised", item.Message);
        Assert.True(viewModel.ClearTimelineCommand.CanExecute(null));
    }

    [Fact]
    public void TimelineCategoryFilter_RetainsEmptySelectionWhenCategoryLeavesRetentionWindow()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.ApplyEvent(new SimulationEvent(
            0,
            0,
            TimeSpan.Zero,
            "Fault",
            "Fault.Initial",
            "Initial fault"));
        viewModel.SelectedTimelineFilter = viewModel.TimelineFilters.Single(item => item.Key == "Fault");

        for (var index = 1; index <= 200; index++)
        {
            viewModel.ApplyEvent(new SimulationEvent(
                index,
                index,
                TimeSpan.FromMilliseconds(index * 5),
                "Sequence",
                $"Sequence.{index}",
                $"Sequence {index}"));
        }

        Assert.Empty(viewModel.Timeline);
        Assert.False(viewModel.HasTimeline);
        Assert.Equal("Fault", viewModel.SelectedTimelineFilter?.Key);
        Assert.True(viewModel.ClearTimelineCommand.CanExecute(null));
    }

    [Fact]
    public void TimelineSeverityFilter_UsesExistingPolicyAndCombinesWithCategory()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ApplyEvent(new SimulationEvent(
            1,
            1,
            TimeSpan.Zero,
            "Sequence",
            "Sequence.Started",
            "Sequence started"));
        viewModel.ApplyEvent(new SimulationEvent(
            2,
            2,
            TimeSpan.FromMilliseconds(5),
            "Warning",
            "Warning.Raised",
            "Warning raised"));
        viewModel.ApplyEvent(new SimulationEvent(
            3,
            3,
            TimeSpan.FromMilliseconds(10),
            "Fault",
            "Fault.Raised",
            "Fault raised"));
        viewModel.ApplyEvent(new SimulationEvent(
            4,
            4,
            TimeSpan.FromMilliseconds(15),
            "Recovery",
            "Recovery.Completed",
            "Recovery completed"));

        Assert.Equal([string.Empty, "Info", "Warning", "Alarm", "Recovery"],
            viewModel.TimelineSeverityFilters.Select(item => item.Key));

        viewModel.SelectedTimelineSeverityFilter = viewModel.TimelineSeverityFilters
            .Single(item => item.Key == "Warning");

        var warningItem = Assert.Single(viewModel.Timeline);
        Assert.Equal("Warning.Raised", warningItem.Code);
        Assert.Contains(nameof(viewModel.SelectedTimelineSeverityFilter), changedProperties);
        Assert.True(viewModel.ClearTimelineCommand.CanExecute(null));

        viewModel.SelectedTimelineFilter = viewModel.TimelineFilters
            .Single(item => item.Key == "Fault");

        Assert.Empty(viewModel.Timeline);
        Assert.True(viewModel.ClearTimelineCommand.CanExecute(null));

        viewModel.SelectedTimelineFilter = viewModel.TimelineFilters
            .Single(item => item.Key == string.Empty);
        viewModel.SelectedTimelineSeverityFilter = viewModel.TimelineSeverityFilters
            .Single(item => item.Key == string.Empty);

        Assert.Equal(4, viewModel.Timeline.Count);
        Assert.Equal(4, viewModel.Timeline.Select(item => item.EventIndex).Distinct().Count());
    }

    [Fact]
    public void TimelineCategoryFilter_ResetsOnProjectReloadAndClear()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.ApplyEvent(new SimulationEvent(
            1,
            1,
            TimeSpan.Zero,
            "Fault",
            "Fault.Raised",
            "Fault raised"));
        viewModel.SelectedTimelineFilter = viewModel.TimelineFilters.Single(item => item.Key == "Fault");
        viewModel.SelectedTimelineSeverityFilter = viewModel.TimelineSeverityFilters
            .Single(item => item.Key == "Alarm");

        viewModel.LoadProject(CreateProject(), resetSession: true);

        Assert.Equal(string.Empty, viewModel.SelectedTimelineFilter?.Key);
        Assert.Equal(string.Empty, viewModel.SelectedTimelineSeverityFilter?.Key);
        Assert.Single(viewModel.TimelineFilters);
        Assert.Equal(5, viewModel.TimelineSeverityFilters.Count);
        Assert.False(viewModel.ClearTimelineCommand.CanExecute(null));

        viewModel.ApplyEvent(new SimulationEvent(
            2,
            2,
            TimeSpan.Zero,
            "Sequence",
            "Sequence.Started",
            "Sequence started"));
        viewModel.ClearTimelineCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.SelectedTimelineFilter?.Key);
        Assert.Equal(string.Empty, viewModel.SelectedTimelineSeverityFilter?.Key);
        Assert.Empty(viewModel.Timeline);
        Assert.False(viewModel.ClearTimelineCommand.CanExecute(null));
    }

    [Fact]
    public void ProjectReload_ClearsSessionDebuggerState_WithoutPersistingIt()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        var project = CreateProject();
        viewModel.LoadProject(project, resetSession: true);
        viewModel.ApplySnapshot(CreateSnapshot(new SequenceDebugSnapshot(
            false,
            null,
            SequenceDebugPauseReason.None,
            null,
            [new SequenceBreakpointSnapshot("cycle", "on")])));
        viewModel.SelectedWatchTarget = viewModel.WatchTargets.First(item => item.Kind == RuntimeWatchKind.Axis);
        viewModel.AddWatchCommand.Execute(null);
        viewModel.ApplyEvent(new SimulationEvent(1, 1, TimeSpan.Zero, "Sequence", "Started", "Started"));

        Assert.Equal(2, viewModel.Watches.Count);
        Assert.Single(viewModel.Timeline);

        viewModel.LoadProject(project, resetSession: true);

        Assert.Empty(viewModel.Watches);
        Assert.Empty(viewModel.Timeline);
        Assert.All(viewModel.Breakpoints, item => Assert.False(item.IsEnabled));
    }

    [Fact]
    public void ApplySnapshot_RefreshesWatchTargetNamesWhenIdsStayTheSame()
    {
        OpenVisionLanguageService.Load();
        var viewModel = new RuntimeDebuggerViewModel(command => Task.FromResult(Accepted(command)));
        viewModel.LoadProject(CreateProject(), resetSession: true);
        viewModel.ApplySnapshot(CreateSnapshot(axisName: "Axis X"));
        viewModel.SetEnabled(true, invalidateCommands: true);

        viewModel.SelectedWatchTarget = viewModel.WatchTargets
            .Single(item => item.Kind == RuntimeWatchKind.Axis);
        viewModel.AddWatchCommand.Execute(null);

        viewModel.ApplySnapshot(CreateSnapshot(axisName: "Renamed Axis X"));

        var target = Assert.Single(viewModel.WatchTargets, item => item.Kind == RuntimeWatchKind.Axis);
        var watch = Assert.Single(viewModel.Watches.Where(item => item.Target.Kind == RuntimeWatchKind.Axis));
        Assert.Equal("Renamed Axis X", target.Name);
        Assert.Equal("Renamed Axis X", watch.Name);
        Assert.Same(target, watch.Target);
    }

    private static MachineProjectDocument CreateProject() => new()
    {
        Id = "project",
        Name = "Debugger test",
        Sequences =
        [
            new SequenceDefinition
            {
                Id = "cycle",
                Name = "Main cycle",
                Steps =
                [
                    new SequenceStepDefinition { Id = "on", Name = "Turn on", NextStepId = "off" },
                    new SequenceStepDefinition { Id = "off", Name = "Turn off" }
                ]
            }
        ]
    };

    private static SimulationSnapshot CreateSnapshot(
        SequenceDebugSnapshot? debug = null,
        IEnumerable<SimulationFaultSnapshot>? faults = null,
        SequenceExecutionSnapshot? sequence = null,
        string axisName = "Axis X",
        SimulationRunMode runMode = SimulationRunMode.Paused) => new(
        TimeSpan.FromMilliseconds(25),
        5,
        runMode,
        SimulationControlOwner.EmbeddedSequence,
        1,
        [new OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot("axis-x", axisName, OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 12.5, 0)],
        1,
        [],
        [sequence ?? new SequenceExecutionSnapshot(
            "cycle",
            SequenceExecutionStatus.Running,
            "on",
            0,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(25),
            5,
            null,
            TimeSpan.FromSeconds(10))],
        [],
        AutomaticRunSnapshot.NotConfigured,
        [],
        faults,
        sequenceDebug: debug);

    private static SimulationCommandResult Accepted(SimulationCommand command) => new(
        command.CommandId,
        true,
        0,
        TimeSpan.Zero,
        SimulationCommandErrorCode.None,
        null);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
