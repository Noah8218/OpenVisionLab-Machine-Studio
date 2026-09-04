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
        SequenceExecutionSnapshot? sequence = null) => new(
        TimeSpan.FromMilliseconds(25),
        5,
        SimulationRunMode.Paused,
        SimulationControlOwner.EmbeddedSequence,
        1,
        [new OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot("axis-x", "Axis X", OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 12.5, 0)],
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
