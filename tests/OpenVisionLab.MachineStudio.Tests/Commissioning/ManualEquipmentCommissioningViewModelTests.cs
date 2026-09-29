using System.Threading.Channels;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualEquipmentCommissioningViewModelTests
{
    [Fact]
    public void ProjectionAndCommandsAreOwnedByTheWorkspace()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1");
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.Conveyor,
            () => { },
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);

        Assert.True(workspace.HasSelectedManualEquipment);
        Assert.True(workspace.HasSelectedConveyor);
        Assert.True(workspace.CanStartManualEquipmentControl);
        Assert.True(workspace.StartManualEquipmentControlCommand.CanExecute(null));
        Assert.True(workspace.RunConveyorForwardCommand.CanExecute(null));
    }

    [Fact]
    public void ModeOnlyProjectionRefreshRaisesOnlyManualCommandGates()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1");
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.Conveyor,
            () => { },
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);
        var changedProperties = new List<string?>();
        workspace.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        workspace.ApplyProjection(projection with { IsRunMode = false }, invalidateCommands: false);

        Assert.Contains(nameof(ManualEquipmentCommissioningViewModel.CanStartManualEquipmentControl), changedProperties);
        Assert.Contains(nameof(ManualEquipmentCommissioningViewModel.CanRunConveyorForward), changedProperties);
        Assert.DoesNotContain(nameof(ManualEquipmentCommissioningViewModel.HasSelectedManualEquipment), changedProperties);
        Assert.DoesNotContain(nameof(ManualEquipmentCommissioningViewModel.ConveyorCommissioningHintText), changedProperties);
    }

    [Fact]
    public void SessionCloseAndDisposeGateEveryManualCommand()
    {
        var projection = CreateProjection(LayoutComponentKind.DigitalSensor, "sensor-1");
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.DigitalSensor,
            () => { },
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);
        Assert.True(workspace.ForceSensorOnCommand.CanExecute(null));

        workspace.SetSessionCloseAdmission(true);
        Assert.False(workspace.ForceSensorOnCommand.CanExecute(null));

        workspace.SetSessionCloseAdmission(false);
        Assert.True(workspace.ForceSensorOnCommand.CanExecute(null));

        workspace.Dispose();
        Assert.False(workspace.ForceSensorOnCommand.CanExecute(null));
        Assert.False(workspace.StartManualEquipmentControlCommand.CanExecute(null));
    }

    [Fact]
    public void DisposeRejectsDirectLocalizationRefreshNotifications()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1");
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.Conveyor,
            () => { },
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);
        var changedProperties = new List<string?>();
        workspace.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        workspace.Dispose();
        var notificationCountAfterDispose = changedProperties.Count;

        workspace.RefreshLocalization();

        Assert.Equal(notificationCountAfterDispose, changedProperties.Count);
    }

    [Fact]
    public void AxisProjectionUsesTheCurrentRuntimeInterlockForSharedStart()
    {
        var projection = CreateProjection(LayoutComponentKind.LinearStage, "axis-1") with
        {
            HasCurrentAxis = true,
            IsCurrentAxisInterlocked = false
        };
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.LinearStage,
            () => { },
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);
        Assert.True(workspace.CanStartManualEquipmentControl);

        workspace.ApplyProjection(projection with { IsCurrentAxisInterlocked = true }, invalidateCommands: false);
        Assert.False(workspace.CanStartManualEquipmentControl);
    }

    [Fact]
    public async Task DisposedWorkspaceSuppressesLateManualControlStartCallback()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1");
        var completion = new TaskCompletionSource<SimulationCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new RecordingSimulationEngine(completion);
        var callbackCount = 0;
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.Conveyor,
            () => callbackCount++,
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);
        workspace.StartManualEquipmentControlCommand.Execute(null);
        await engine.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        workspace.Dispose();
        completion.SetResult(new SimulationCommandResult(
            "late-start",
            true,
            0,
            TimeSpan.Zero,
            SimulationCommandErrorCode.None,
            null));
        await engine.CommandFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(20);

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task ClosingWorkspaceSuppressesLateManualControlStartCallback()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1");
        var completion = new TaskCompletionSource<SimulationCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new RecordingSimulationEngine(completion);
        var callbackCount = 0;
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection,
            () => LayoutComponentKind.Conveyor,
            () => callbackCount++,
            _ => { });

        workspace.ApplyProjection(projection, invalidateCommands: false);
        workspace.StartManualEquipmentControlCommand.Execute(null);
        await engine.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        workspace.SetSessionCloseAdmission(true);
        completion.SetResult(new SimulationCommandResult(
            "late-start",
            true,
            0,
            TimeSpan.Zero,
            SimulationCommandErrorCode.None,
            null));
        await engine.CommandFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(20);

        Assert.Equal(0, callbackCount);
    }

    private static ManualEquipmentProjection CreateProjection(
        LayoutComponentKind selectedKind,
        string selectedId) => new(
        CreateSnapshot(selectedKind, selectedId),
        selectedId,
        selectedKind,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        SimulationControlOwner.Manual,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: null);

    private static SimulationSnapshot CreateSnapshot(
        LayoutComponentKind selectedKind,
        string selectedId) => new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [new DigitalSignalSnapshot(
            "di.sensor-1",
            "Sensor",
            OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput,
            false)],
        [],
        [],
        AutomaticRunSnapshot.NotConfigured,
        [new LayoutComponentSnapshot(
            selectedId,
            selectedKind.ToString(),
            selectedKind,
            0,
            0,
            0,
            10,
            10,
            false,
            null,
            SensorOutputChannelId: selectedKind == LayoutComponentKind.DigitalSensor
                ? "di.sensor-1"
                : null,
            ConveyorRunning: selectedKind == LayoutComponentKind.Conveyor ? false : null,
            ConveyorDirection: selectedKind == LayoutComponentKind.Conveyor ? ConveyorDirection.Forward : null)]);

    private sealed class RecordingSimulationEngine : ISimulationEngine
    {
        private readonly Channel<SimulationSnapshot> _snapshotChannel = Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel = Channel.CreateUnbounded<SimulationEvent>();
        private readonly TaskCompletionSource<SimulationCommandResult>? _commandCompletion;

        public RecordingSimulationEngine(
            TaskCompletionSource<SimulationCommandResult>? commandCompletion = null)
        {
            _commandCompletion = commandCompletion;
        }

        public TaskCompletionSource<bool> CommandStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CommandFinished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public SimulationSnapshot CurrentSnapshot => new(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            [],
            0,
            [],
            []);

        public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotChannel.Reader;
        public ChannelReader<SimulationEvent> EventReader => _eventChannel.Reader;
        public Task<SimulationEngineTerminationResult> Termination => Task.FromResult(
            new SimulationEngineTerminationResult(
                SimulationEngineTerminationOutcome.Normal,
                0,
                TimeSpan.Zero));

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SimulationCommandResult> EnqueueCommandAsync(
            SimulationCommand command,
            CancellationToken cancellationToken = default) => EnqueueCommandCoreAsync(command);

        private async Task<SimulationCommandResult> EnqueueCommandCoreAsync(SimulationCommand command)
        {
            CommandStarted.TrySetResult(true);
            var result = _commandCompletion is null
                ? Accepted(command.CommandId)
                : await _commandCompletion.Task;
            CommandFinished.TrySetResult(true);
            return result;
        }

        public void AddAxis(OpenVisionLab.Machine.Simulation.Axis.ServoAxisComponent axis)
        {
        }

        public void Dispose()
        {
            _snapshotChannel.Writer.TryComplete();
            _eventChannel.Writer.TryComplete();
        }

        private static SimulationCommandResult Accepted(string commandId) => new(
            commandId,
            true,
            0,
            TimeSpan.Zero,
            SimulationCommandErrorCode.None,
            null);
    }
}
