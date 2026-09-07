using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class FaultManagerViewModelTests
{
    [Fact]
    public async Task Dispose_SuppressesLateOperationPublication_AndDisablesCommands()
    {
        OpenVisionLanguageService.Load();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new FaultManagerViewModel(async command =>
        {
            await gate.Task;
            return Accepted(command);
        });
        viewModel.SelectedKind = viewModel.AvailableKinds.Single(option =>
            option.Kind == SimulationFaultKind.AxisMotionBlocked);
        viewModel.ApplySnapshot(CreateSnapshot());
        viewModel.SetEnabled(true, invalidateCommands: true);
        Assert.NotNull(viewModel.SelectedTarget);
        var initialStatus = viewModel.OperationStatusText;
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.InjectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsOperationPending);

        viewModel.Dispose();
        viewModel.Dispose();
        gate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsOperationPending);

        Assert.Equal(initialStatus, viewModel.OperationStatusText);
        Assert.DoesNotContain(nameof(viewModel.OperationStatusText), changedProperties);
        Assert.False(viewModel.InjectCommand.CanExecute(null));
        Assert.False(viewModel.ClearSelectedCommand.CanExecute(null));
        Assert.False(viewModel.ClearAllCommand.CanExecute(null));
    }

    private static SimulationSnapshot CreateSnapshot() => new(
        TimeSpan.FromMilliseconds(25),
        5,
        SimulationRunMode.Paused,
        SimulationControlOwner.EmbeddedSequence,
        1,
        [new OpenVisionLab.Machine.Simulation.Axis.AxisSnapshot(
            "axis-x",
            "Axis X",
            OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle,
            12.5,
            0)],
        1,
        [new DigitalSignalSnapshot("di.sensor-1", "Sensor", ChannelKind.DigitalInput, false)],
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
        faults: [],
        sequenceDebug: null);

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
