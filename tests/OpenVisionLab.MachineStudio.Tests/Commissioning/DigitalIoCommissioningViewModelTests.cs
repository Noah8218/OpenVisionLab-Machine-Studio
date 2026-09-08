using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class DigitalIoCommissioningViewModelTests
{
    [Fact]
    public async Task Dispose_SuppressesLateDispatchCompletion_AndDisablesCommands()
    {
        OpenVisionLanguageService.Load();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new DigitalIoCommissioningViewModel(async command =>
        {
            await gate.Task;
            return Accepted(command);
        });
        viewModel.ApplySnapshot(CreateSnapshot());
        viewModel.SetEnabled(true, invalidateCommands: true);
        Assert.NotNull(viewModel.SelectedSignal);
        Assert.True(viewModel.ForceOnCommand.CanExecute(null));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ForceOnCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsOperationPending);
        var changedBeforeDispose = changedProperties.Count;

        viewModel.Dispose();
        viewModel.Dispose();
        gate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsOperationPending);

        Assert.DoesNotContain(
            nameof(viewModel.IsOperationPending),
            changedProperties.Skip(changedBeforeDispose));
        Assert.False(viewModel.ForceOnCommand.CanExecute(null));
        Assert.False(viewModel.ForceOffCommand.CanExecute(null));
        Assert.False(viewModel.ClearForceCommand.CanExecute(null));
    }

    private static SimulationSnapshot CreateSnapshot() => new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [new DigitalSignalSnapshot("di.sensor-1", "Sensor", ChannelKind.DigitalInput, false)],
        [],
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
