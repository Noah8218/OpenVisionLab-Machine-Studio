using System.Threading.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
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

[Collection(StudioUiTestCollection.Name)]
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

    [Theory]
    [InlineData(LayoutComponentKind.LinearStage)]
    [InlineData(LayoutComponentKind.RotaryStage)]
    [InlineData(LayoutComponentKind.Camera)]
    [InlineData(LayoutComponentKind.DigitalSensor)]
    [InlineData(LayoutComponentKind.PneumaticCylinder)]
    [InlineData(LayoutComponentKind.Conveyor)]
    public async Task ExplicitEntryPreparesOnlyTheSelectedManualControl(LayoutComponentKind kind)
    {
        var projection = CreateProjection(kind, "selected-1") with
        {
            IsRunMode = false, ControlOwner = SimulationControlOwner.Definition,
            HasCurrentAxis = true, HasCurrentCamera = true
        };
        using var engine = new RecordingSimulationEngine();
        var started = 0;
        var paused = 0;
        ManualEquipmentCommissioningViewModel? workspace = null;
        workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => kind,
            () => { started++; projection = projection with { IsRunMode = true, IsRunning = true, ControlOwner = SimulationControlOwner.Manual }; },
            exception => throw exception,
            () => { workspace!.BeginRuntimePreparation(); return Task.FromResult(true); },
            () => { paused++; projection = projection with { IsRunning = false }; });
        using (workspace)
        {
            workspace.ApplyProjection(projection);
            Assert.True(workspace.StartManualEquipmentControlCommand.CanExecute(null));
            await ExecuteEntryAsync(workspace);

            Assert.Equal(1, started);
            Assert.Equal(kind == LayoutComponentKind.Camera ? 1 : 0, paused);
            Assert.True(projection.IsRunMode);
            Assert.Equal(SimulationControlOwner.Manual, projection.ControlOwner);
            Assert.False(workspace.IsStartingManualControl);
            Assert.False(workspace.IsManualControlAdmissionLocked);
            Assert.IsType<StartManualControlCommand>(engine.Commands[0]);
            if (kind == LayoutComponentKind.Camera)
            {
                Assert.Equal(2, engine.Commands.Count);
                Assert.IsType<PauseCommand>(engine.Commands[1]);
                Assert.False(projection.IsRunning);
            }
            else
            {
                Assert.Single(engine.Commands);
                Assert.True(projection.IsRunning);
            }
            Assert.DoesNotContain(engine.Commands, command => command is StartAutomaticRunCommand or StartSequenceCommand or ResetCommand);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclinedOrCancelledPreparationPreservesStateAndAllowsAnExplicitRetry(bool cancel)
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with { IsRunMode = false };
        var original = projection;
        var attempt = 0;
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => LayoutComponentKind.Conveyor,
            () => projection = projection with { IsRunMode = true, IsRunning = true },
            exception => throw exception,
            () => ++attempt == 1
                ? cancel ? Task.FromException<bool>(new OperationCanceledException()) : Task.FromResult(false)
                : Task.FromResult(true));
        workspace.ApplyProjection(projection);

        await ExecuteEntryAsync(workspace);
        Assert.Same(original, projection);
        Assert.Empty(engine.Commands);
        Assert.False(workspace.IsStartingManualControl);
        Assert.False(workspace.IsManualControlAdmissionLocked);
        Assert.True(workspace.StartManualEquipmentControlCommand.CanExecute(null));
        if (!cancel) Assert.DoesNotContain("Equipment.ManualHint.", workspace.ManualControlHintText);

        await ExecuteEntryAsync(workspace);
        Assert.Equal(2, attempt);
        Assert.True(projection.IsRunMode);
        Assert.IsType<StartManualControlCommand>(Assert.Single(engine.Commands));
    }

    [Fact]
    public async Task DuplicateEntryCannotPrepareOrStartTwiceAndReleasesTheAdmissionLock()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with { IsRunMode = false };
        var preparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var engine = new RecordingSimulationEngine();
        ManualEquipmentCommissioningViewModel? workspace = null;
        workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => LayoutComponentKind.Conveyor,
            () => projection = projection with { IsRunMode = true, IsRunning = true },
            exception => throw exception,
            () => { attempts++; workspace!.BeginRuntimePreparation(); return preparation.Task; });
        using (workspace)
        {
            workspace.ApplyProjection(projection);
            var execution = ExecuteEntryAsync(workspace);
            Assert.True(workspace.IsStartingManualControl);
            Assert.True(workspace.IsManualControlAdmissionLocked);
            Assert.False(workspace.StartManualEquipmentControlCommand.CanExecute(null));
            workspace.StartManualEquipmentControlCommand.Execute(null);
            preparation.SetResult(true);
            await execution;

            Assert.Equal(1, attempts);
            Assert.IsType<StartManualControlCommand>(Assert.Single(engine.Commands));
            Assert.False(workspace.IsManualControlAdmissionLocked);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void AutomaticRunningAndPausedOwnershipCannotBeTakenByEntry(bool running, bool sequence)
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with
        {
            IsRunMode = true, IsRunning = running, IsAutomaticRunActive = !sequence,
            ActiveSequenceStatus = sequence ? SequenceExecutionStatus.Running : null,
            ControlOwner = SimulationControlOwner.EmbeddedSequence
        };
        var original = projection;
        var preparations = 0;
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => LayoutComponentKind.Conveyor, () => throw new InvalidOperationException("Unexpected control transfer"),
            exception => throw exception, () => { preparations++; return Task.FromResult(true); });
        workspace.ApplyProjection(projection);

        Assert.False(workspace.StartManualEquipmentControlCommand.CanExecute(null));
        workspace.StartManualEquipmentControlCommand.Execute(null);
        Assert.Equal(0, preparations);
        Assert.Empty(engine.Commands);
        Assert.Same(original, projection);
        var hint = workspace.ManualControlHintText;
        Assert.True(hint.Contains("초기화", StringComparison.Ordinal) || hint.Contains("reset", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChangedSelectionClosingOrDisposalBeforePreparationEndsDoesNotStart(int transition)
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with { IsRunMode = false };
        var preparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new RecordingSimulationEngine();
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => projection.SelectedComponentKind,
            () => throw new InvalidOperationException("Unexpected mode change"), exception => throw exception,
            () => preparation.Task);
        workspace.ApplyProjection(projection);
        var execution = ExecuteEntryAsync(workspace);
        if (transition == 0) projection = projection with { SelectedComponentId = "other-component" };
        else if (transition == 1) workspace.SetSessionCloseAdmission(true);
        else workspace.Dispose();
        preparation.SetResult(true);
        await execution;

        Assert.Empty(engine.Commands);
        Assert.False(projection.IsRunMode);
        Assert.False(workspace.IsStartingManualControl);
        Assert.False(workspace.IsManualControlAdmissionLocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewProjectOrRuntimeGenerationSuppressesALateAcceptedModeCallback(bool replaceProject)
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with { IsRunMode = false };
        var completion = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new RecordingSimulationEngine(completion);
        var modeChanges = 0;
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => LayoutComponentKind.Conveyor, () => modeChanges++, exception => throw exception,
            () => Task.FromResult(true));
        workspace.ApplyProjection(projection);
        var execution = ExecuteEntryAsync(workspace);
        await engine.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        projection = projection with
        {
            Snapshot = new SimulationSnapshot(TimeSpan.Zero, 0, SimulationRunMode.Paused, SimulationControlOwner.Definition,
                1, [], 0, [], [], [], AutomaticRunSnapshot.NotConfigured, [],
                projectId: replaceProject ? "replacement-project" : null, runtimeGeneration: replaceProject ? 0 : 1)
        };
        completion.SetResult(new SimulationCommandResult("accepted-old-request", true, 0, TimeSpan.Zero, SimulationCommandErrorCode.None, null));
        await execution;

        Assert.IsType<StartManualControlCommand>(Assert.Single(engine.Commands));
        Assert.Equal(0, modeChanges);
        Assert.False(projection.IsRunMode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptedEntryWithChangedSelectionKeepsRunModeAndRecoversOnlyTheSameManualRuntime(bool acceptPause)
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with { IsRunMode = false };
        var completion = new TaskCompletionSource<SimulationCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new RecordingSimulationEngine(completion, acceptPause: acceptPause);
        var starts = 0;
        var pauses = 0;
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => projection.SelectedComponentKind,
            () => { starts++; projection = projection with { IsRunMode = true, IsRunning = true, ControlOwner = SimulationControlOwner.Manual }; },
            exception => throw exception, () => Task.FromResult(true),
            () => { pauses++; projection = projection with { IsRunning = false }; });
        workspace.ApplyProjection(projection);
        var execution = ExecuteEntryAsync(workspace);
        await engine.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        projection = projection with { SelectedComponentId = "other-component" };
        completion.SetResult(new SimulationCommandResult("accepted-current-runtime", true, 0, TimeSpan.Zero, SimulationCommandErrorCode.None, null));
        await execution;

        Assert.Equal(1, starts);
        Assert.Equal(acceptPause ? 1 : 0, pauses);
        Assert.True(projection.IsRunMode);
        Assert.Equal(!acceptPause, projection.IsRunning);
        Assert.Equal(SimulationControlOwner.Manual, projection.ControlOwner);
        Assert.Collection(engine.Commands, command => Assert.IsType<StartManualControlCommand>(command), command => Assert.IsType<PauseCommand>(command));
        Assert.False(workspace.IsStartingManualControl || workspace.IsManualControlAdmissionLocked);
        var hint = workspace.ManualControlHintText;
        Assert.DoesNotContain("Equipment.ManualHint.", hint);
        Assert.True(hint.Contains("일시정지", StringComparison.Ordinal) || hint.Contains("pause", StringComparison.OrdinalIgnoreCase));
        if (acceptPause) Assert.True(hint.Contains("선택", StringComparison.Ordinal) || hint.Contains("selection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectedStartRetainsEditModeAndReleasesAllEntryGates()
    {
        var projection = CreateProjection(LayoutComponentKind.Conveyor, "conveyor-1") with { IsRunMode = false };
        using var engine = new RecordingSimulationEngine(acceptCommands: false);
        using var workspace = new ManualEquipmentCommissioningViewModel(
            new EquipmentCommandDispatcher(engine, _ => { }, (_, _) => { }),
            _ => projection, () => LayoutComponentKind.Conveyor,
            () => throw new InvalidOperationException("Rejected start changed mode"), exception => throw exception,
            () => Task.FromResult(true));
        workspace.ApplyProjection(projection);
        await ExecuteEntryAsync(workspace);

        Assert.False(projection.IsRunMode);
        Assert.False(workspace.IsStartingManualControl);
        Assert.True(workspace.StartManualEquipmentControlCommand.CanExecute(null));
        Assert.DoesNotContain("Equipment.ManualHint.", workspace.ManualControlHintText);
        Assert.IsType<StartManualControlCommand>(Assert.Single(engine.Commands));
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var koreanHint = workspace.ManualControlHintText;
            Assert.Equal(OpenVisionLanguageService.T("Equipment.ManualHint.Rejected"), koreanHint);
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            Assert.Equal(OpenVisionLanguageService.T("Equipment.ManualHint.Rejected"), workspace.ManualControlHintText);
            Assert.NotEqual(koreanHint, workspace.ManualControlHintText);
        }
        finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
    }

    private static async Task ExecuteEntryAsync(ManualEquipmentCommissioningViewModel workspace)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasBusy = false;
        void Observe()
        {
            wasBusy |= workspace.IsStartingManualControl;
            if (wasBusy && !workspace.IsStartingManualControl
                && (!workspace.CanStartManualEquipmentControl || workspace.StartManualEquipmentControlCommand.CanExecute(null))) completion.TrySetResult(true);
        }
        void PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Observe();
        void CommandChanged(object? sender, EventArgs args) => Observe();
        workspace.PropertyChanged += PropertyChanged;
        workspace.StartManualEquipmentControlCommand.CanExecuteChanged += CommandChanged;
        try
        {
            workspace.StartManualEquipmentControlCommand.Execute(null);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            workspace.PropertyChanged -= PropertyChanged;
            workspace.StartManualEquipmentControlCommand.CanExecuteChanged -= CommandChanged;
        }
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
        private readonly bool _acceptCommands;
        private readonly bool _acceptPause;
        public List<SimulationCommand> Commands { get; } = [];

        public RecordingSimulationEngine(
            TaskCompletionSource<SimulationCommandResult>? commandCompletion = null, bool acceptCommands = true, bool acceptPause = true)
        {
            _commandCompletion = commandCompletion;
            _acceptCommands = acceptCommands;
            _acceptPause = acceptPause;
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
            Commands.Add(command);
            CommandStarted.TrySetResult(true);
            if (command is PauseCommand && !_acceptPause)
            {
                return new SimulationCommandResult(command.CommandId, false, 0, TimeSpan.Zero, SimulationCommandErrorCode.InvalidRunMode, "pause rejection");
            }
            var result = _commandCompletion is null
                ? _acceptCommands ? Accepted(command.CommandId)
                    : new SimulationCommandResult(command.CommandId, false, 0, TimeSpan.Zero, SimulationCommandErrorCode.InvalidRunMode, "test rejection")
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
