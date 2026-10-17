using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SimulationSequenceObservationTests(StudioUiTestHost ui)
{
    [Fact]
    public async Task ParallelUnitsKeepTheirOwnActualValuesAndConfiguredTargets()
    {
        await ui.InvokeAsync(() =>
        {
            var project = R40SimulationWorkspaceTests.CreateProject();
            project.Sequences[0].Steps[0] = new() { Id = "wait", Name = "위치 확인", Action = SequenceStepAction.WaitAxisDone, TargetId = "axis-a", TimeoutMs = 8000 };
            project.Sequences[1].Steps[0] = new() { Id = "wait", Name = "센서 확인", Action = SequenceStepAction.WaitSignal, TargetId = "sensor", Parameter = "0", TimeoutMs = 5000 };
            var snapshot = Snapshot(project, [Execution("sequence-a", "wait"), Execution("sequence-b", "wait")],
                axes: [new("axis-a", "이송 축 A", AxisState.Moving, 12.5, 1, 40, 0, 0, false, 0, 0, 0)],
                signals: [new("sensor", "제품 센서", ChannelKind.DigitalInput, true)]);
            var rows = SimulationSequenceObservation.Create(project, snapshot);
            Assert.Equal(2, rows.Count);
            Assert.Equal("a", rows[0].UnitId);
            Assert.Equal("b", rows[1].UnitId);
            Assert.Contains("12.5", rows[0].ValuesText);
            Assert.Contains("40", rows[0].ValuesText);
            Assert.Contains("8 s", rows[0].TimingText);
            Assert.Contains("ON", rows[1].ValuesText);
            Assert.Contains("OFF", rows[1].ValuesText);
            Assert.Contains("5 s", rows[1].TimingText);
            Assert.All(rows, row => Assert.True(row.CanOpenStep && row.Matches(snapshot)));
            return true;
        });
    }

    [Theory]
    [InlineData(SequenceExecutionStatus.Ready)]
    [InlineData(SequenceExecutionStatus.Completed)]
    [InlineData(SequenceExecutionStatus.Aborted)]
    public async Task TerminalAndReadyStatesDoNotReportAnActiveWait(SequenceExecutionStatus status)
    {
        await ui.InvokeAsync(() =>
        {
            var project = R40SimulationWorkspaceTests.CreateProject();
            var row = Assert.Single(SimulationSequenceObservation.Create(project, Snapshot(project, [Execution("sequence-a", "delay") with { Status = status }])));
            Assert.Equal(OpenVisionLanguageService.T($"Equipment.State.{status}"), row.StateText);
            Assert.Empty(row.DetailText);
            if (status == SequenceExecutionStatus.Ready)
            {
                Assert.Null(row.StepId);
                Assert.False(row.CanOpenStep);
                Assert.Equal(OpenVisionLanguageService.T("SimObservation.NoStep"), row.StepText);
            }
            return true;
        });
    }

    [Fact]
    public async Task PausedSubsequenceAndFaultUseTheActualLeafAndErrorLocation()
    {
        await ui.InvokeAsync(() =>
        {
            var project = R40SimulationWorkspaceTests.CreateProject();
            project.Sequences.Add(new() { Id = "sub", Name = "하위 공정", Steps = [new() { Id = "leaf", Name = "하위 대기", Action = SequenceStepAction.Delay, Parameter = "2" }] });
            var execution = Execution("sequence-a", "leaf") with { ActiveSequenceId = "sub" };
            var paused = Assert.Single(SimulationSequenceObservation.Create(project, Snapshot(project, [execution], mode: SimulationRunMode.Paused)));
            Assert.Equal("sub", paused.SequenceId);
            Assert.Equal("sequence-a", paused.RootSequenceId);
            Assert.Equal(OpenVisionLanguageService.T("Shell.Paused"), paused.StateText);
            Assert.Contains("하위 공정", paused.StepText);
            Assert.True(paused.CanOpenStep);
            var error = new SequenceExecutionError(SequenceExecutionErrorCode.StepTimedOut, "sub", "leaf", "owned failure detail");
            var faulted = Assert.Single(SimulationSequenceObservation.Create(project, Snapshot(project,
                [execution with { Status = SequenceExecutionStatus.Faulted, ActiveSequenceId = "sequence-a", CurrentStepId = "delay", LastError = error }])));
            Assert.True(faulted.IsFaulted && faulted.CanOpenStep);
            Assert.Equal("sub", faulted.SequenceId);
            Assert.Equal("leaf", faulted.StepId);
            Assert.Equal("StepTimedOut · owned failure detail", faulted.DetailText);
            return true;
        });
    }

    [Fact]
    public async Task MissingDefinitionsForeignProjectsAndOldGenerationsCannotBecomeCurrentSteps()
    {
        await ui.InvokeAsync(() =>
        {
            var project = R40SimulationWorkspaceTests.CreateProject();
            project.Sequences[0].Name = string.Empty;
            project.Sequences[0].Steps[0].Name = string.Empty;
            project.Stations[0].Units[0].Name = string.Empty;
            var valid = Snapshot(project, [Execution("sequence-a", "delay")]);
            var row = Assert.Single(SimulationSequenceObservation.Create(project, valid));
            Assert.Equal("a · sequence-a", row.ProcessText);
            Assert.Equal("delay", row.StepText);
            Assert.False(row.Matches(Snapshot(project, [Execution("sequence-a", "done")])));
            Assert.False(row.Matches(Snapshot(project, valid.Sequences, generation: 8)));
            Assert.False(row.Matches(Snapshot(new MachineProjectDocument(), valid.Sequences)));
            Assert.Empty(SimulationSequenceObservation.Create(new MachineProjectDocument(), valid));
            Assert.Empty(SimulationSequenceObservation.Create(project, null));
            var missing = Assert.Single(SimulationSequenceObservation.Create(project, Snapshot(project, [Execution("sequence-a", "missing") with { ActiveSequenceId = "removed-subsequence" }])));
            Assert.False(missing.CanOpenStep);
            Assert.Contains("missing", missing.StepText);
            return true;
        });
    }

    [Fact]
    public async Task CameraWaitingUsesAcquisitionStateAndResetClearsTheObservationList()
    {
        await ui.InvokeAsync(() =>
        {
            var project = R40SimulationWorkspaceTests.CreateProject();
            project.Sequences[0].Steps[0] = new() { Id = "vision", Action = SequenceStepAction.WaitVisionResult, TargetId = "camera", TimeoutMs = 3000 };
            var snapshot = Snapshot(project, [Execution("sequence-a", "vision")], cameras:
                [new("camera", "검사 카메라", VirtualCameraState.AwaitingExternalResult, 1, "acquisition", "recipe", 0, 0, null)]);
            using var debugger = new RuntimeDebuggerViewModel(_ => throw new InvalidOperationException("Observation must not dispatch a runtime command."));
            debugger.LoadProject(project, resetSession: true);
            debugger.ApplySnapshot(snapshot);
            var row = Assert.Single(debugger.SequenceObservations);
            Assert.Contains(OpenVisionLanguageService.T("Equipment.State.AwaitingExternalResult"), row.ValuesText);
            Assert.Contains(OpenVisionLanguageService.T("Equipment.State.FrameReady"), row.ValuesText);
            var cached = debugger.SequenceObservations;
            debugger.ApplySnapshot(snapshot);
            Assert.Same(cached, debugger.SequenceObservations);
            debugger.LoadProject(project, resetSession: true);
            Assert.Empty(debugger.SequenceObservations);
            return true;
        });
    }

    internal static SequenceExecutionSnapshot Execution(string sequenceId, string stepId) =>
        new(sequenceId, SequenceExecutionStatus.Running, stepId, 0, TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(3), 250, null, TimeSpan.FromSeconds(120));

    internal static SimulationSnapshot Snapshot(MachineProjectDocument project, IEnumerable<SequenceExecutionSnapshot> executions,
        IEnumerable<AxisSnapshot>? axes = null, IEnumerable<DigitalSignalSnapshot>? signals = null, IEnumerable<VirtualCameraSnapshot>? cameras = null,
        SimulationRunMode mode = SimulationRunMode.RealTime, long generation = 7) =>
        new(TimeSpan.FromSeconds(3), 600, mode, SimulationControlOwner.EmbeddedSequence, 1,
            axes ?? [], 0, signals ?? [], executions, cameras ?? [], AutomaticRunSnapshot.NotConfigured, [], projectId: project.Id, runtimeGeneration: generation);
}
