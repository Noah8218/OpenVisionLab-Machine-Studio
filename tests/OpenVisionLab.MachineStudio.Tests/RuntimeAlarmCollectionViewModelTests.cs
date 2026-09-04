using OpenVisionLab;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeAlarmCollectionViewModelTests
{
    [Fact]
    public void ApplySnapshot_TracksAcknowledgementAndClearLifecycle()
    {
        OpenVisionLanguageService.Load();
        var status = string.Empty;
        var viewModel = new RuntimeAlarmCollectionViewModel(
            () => true,
            sequenceId => sequenceId,
            value => status = value);
        var activeSnapshot = CreateSnapshot(
        [
            new SimulationFaultSnapshot(
                SimulationFaultKind.AxisMotionBlocked,
                "axis-x",
                null,
                5,
                TimeSpan.FromMilliseconds(25))
        ]);

        viewModel.ApplySnapshot(activeSnapshot);
        var alarm = Assert.Single(viewModel.Alarms);

        viewModel.AcknowledgeAllAlarmsCommand.Execute(null);

        Assert.True(alarm.IsAcknowledged);
        Assert.Equal(OpenVisionLanguageService.T("Debugger.AlarmAcknowledgedStatus"), status);

        viewModel.ApplySnapshot(CreateSnapshot(faults: []));

        Assert.Empty(viewModel.Alarms);
        Assert.False(alarm.IsActive);
        Assert.Single(viewModel.AlarmHistory);
    }

    private static SimulationSnapshot CreateSnapshot(IEnumerable<SimulationFaultSnapshot>? faults = null) => new(
        TimeSpan.FromMilliseconds(25),
        5,
        SimulationRunMode.Paused,
        SimulationControlOwner.EmbeddedSequence,
        1,
        [new OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot("axis-x", "Axis X", OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle, 12.5, 0)],
        1,
        [],
        [new SequenceExecutionSnapshot(
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
        sequenceDebug: null);
}
