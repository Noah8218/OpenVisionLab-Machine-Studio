using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeShutdownTests
{
    [Fact]
    public void OperationalDiagnosticsRetainStructuredRuntimeMessagesWithinBound()
    {
        using var viewModel = new MainViewModel();

        for (var index = 0; index < 1_200; index++)
        {
            viewModel.AppendLog(
                TimeSpan.FromMilliseconds(index),
                "Runtime",
                $"message-{index}");
        }

        var diagnostics = viewModel.OperationalDiagnostics;
        var lastMessage = diagnostics.Single(diagnostic => diagnostic.Message == "message-1199");

        Assert.True(diagnostics.Count <= MainViewModel.OperationalDiagnosticRetentionLimit);
        Assert.Equal(SimulationOperationalDiagnosticKind.RuntimeMessage, lastMessage.Kind);
        Assert.Equal(SimulationLogSeverity.Info, lastMessage.Severity);
        Assert.Equal("MachineStudio", lastMessage.Component);
        Assert.Equal("Runtime", lastMessage.Category);
        Assert.Equal("message-1199", lastMessage.Message);
        Assert.True(viewModel.LogMessages.Count <= MainViewModel.LogMessageRetentionLimit);
    }

    [Fact]
    public async Task CoordinatorTimesOutBlockedOperationAndSharesOneShutdownTask()
    {
        var completion = new TaskCompletionSource<RuntimeShutdownResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new BoundedShutdownCoordinator();

        var firstTask = coordinator.ShutdownAsync(
            TimeSpan.FromMilliseconds(50),
            _ => completion.Task);
        var secondTask = coordinator.ShutdownAsync(
            TimeSpan.FromSeconds(5),
            _ => Task.FromResult(new RuntimeShutdownResult(
                RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)));

        var result = await firstTask;

        Assert.Same(firstTask, secondTask);
        Assert.Equal(RuntimeShutdownOutcome.TimedOut, result.Outcome);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2));

        completion.TrySetResult(new RuntimeShutdownResult(
            RuntimeShutdownOutcome.Completed,
            TimeSpan.Zero));
    }

    [Fact]
    public async Task CoordinatorStageWaitNamesTheStageWhenDeadlineExpires()
    {
        var blocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsAsync<RuntimeShutdownTimeoutException>(
            () => BoundedShutdownCoordinator.AwaitStageAsync(
                blocked.Task,
                "EngineStop",
                deadline.Token));

        Assert.Equal("EngineStop", exception.Stage);
    }

    [Fact]
    public async Task CoordinatorStageWaitPreservesChildCancellationContract()
    {
        using var childCancellation = new CancellationTokenSource();
        childCancellation.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await BoundedShutdownCoordinator.AwaitStageAsync(
            Task.FromCanceled(childCancellation.Token),
            "RuntimeTask",
            deadline.Token);

        var resultCancellation = new CancellationTokenSource();
        resultCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedShutdownCoordinator.AwaitStageAsync(
                Task.FromCanceled<RuntimeShutdownResult>(resultCancellation.Token),
                "EngineTermination",
                deadline.Token));
    }

    [Fact]
    public async Task MainViewModelShutdownObservesTerminationAndIsRepeatSafe()
    {
        using var viewModel = new MainViewModel();

        var firstTask = viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));
        var result = await firstTask;
        var secondResult = await viewModel.ShutdownAsync(TimeSpan.FromMilliseconds(1));

        Assert.Same(result, secondResult);
        Assert.True(
            result.Outcome == RuntimeShutdownOutcome.Completed,
            $"journal={result.EventJournal}; consumption={result.EventConsumption}");
        Assert.Equal(
            SimulationEngineTerminationOutcome.Stopped,
            result.EngineTermination?.Outcome);
        Assert.Contains(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.ShutdownRequested);
        Assert.Contains(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.EngineTermination
                && diagnostic.TerminationOutcome == SimulationEngineTerminationOutcome.Stopped);
        Assert.Contains(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.ShutdownCompleted);
        Assert.Single(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.EngineTermination);
        Assert.Single(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.ShutdownRequested);
        Assert.Single(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.ShutdownCompleted);

        viewModel.Dispose();
        viewModel.Dispose();
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedLanguageCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var designModeBefore = viewModel.IsDesignMode;
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var languageChanged = typeof(MainViewModel).GetMethod(
            "OnLanguageChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(languageChanged);
        var exception = Record.Exception(() =>
            languageChanged!.Invoke(viewModel, new object?[] { null, EventArgs.Empty }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedShellNavigationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var shellNavigationChanged = typeof(MainViewModel).GetMethod(
            "OnShellNavigationPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(shellNavigationChanged);
        var exception = Record.Exception(() =>
            shellNavigationChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs(nameof(ShellNavigationViewModel.IsCompactLayout))
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedDryRunPlaybackCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var dryRunPlaybackChanged = typeof(MainViewModel).GetMethod(
            "OnDryRunPlaybackPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(dryRunPlaybackChanged);
        var exception = Record.Exception(() =>
            dryRunPlaybackChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs("IsActive")
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProcessPlanReviewCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var processPlanReviewChanged = typeof(MainViewModel).GetMethod(
            "OnProcessPlanReviewPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(processPlanReviewChanged);
        var exception = Record.Exception(() =>
            processPlanReviewChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs("HasReturnContext")
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSequenceDefinitionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var sequenceDefinitionChanged = typeof(MainViewModel).GetMethod(
            "OnSequenceDefinitionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(sequenceDefinitionChanged);
        var exception = Record.Exception(() =>
            sequenceDefinitionChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new SequenceEditorChangedEventArgs(false)
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSelectionSynchronizationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var selectionSynchronizationChanged = typeof(MainViewModel).GetMethod(
            "OnSelectionSynchronizationPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(selectionSynchronizationChanged);
        var exception = Record.Exception(() =>
            selectionSynchronizationChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs(
                        nameof(ProjectSelectionSynchronizationWorkflow.AxisDriveTuningEditor))
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSelectionStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var selectionSynchronization = typeof(MainViewModel).GetField(
            "_selectionSynchronization",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);

        Assert.NotNull(selectionSynchronization);
        var setStatus = selectionSynchronization!.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(selectionSynchronization)
            as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late selection status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationSessionStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var setStatus = runControl?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late simulation session status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationSessionLogCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var log = runControl?.GetType().GetField(
            "_log",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Action<string, string>;

        Assert.NotNull(log);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var exception = Record.Exception(() => log!("Simulation", "late simulation session log"));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedEnsureRuntimeDefinitionAppliedCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var runtimeDefinitionDirty = typeof(MainViewModel).GetField(
            "_runtimeDefinitionDirty",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(runtimeDefinitionDirty);
        runtimeDefinitionDirty!.SetValue(viewModel, true);

        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var ensureRuntimeDefinitionApplied = runControl?.GetType().GetField(
            "_ensureRuntimeDefinitionApplied",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Func<Task<bool>>;

        Assert.NotNull(ensureRuntimeDefinitionApplied);

        viewModel.Dispose();
        var exception = await Record.ExceptionAsync(() => ensureRuntimeDefinitionApplied!());

        Assert.Null(exception);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationRunControlStateCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var getRunControlState = runControl?.GetType().GetField(
            "_getState",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Func<SimulationRunControlState>;

        Assert.NotNull(getRunControlState);

        viewModel.Dispose();
        var exception = Record.Exception(() => getRunControlState!());

        Assert.Null(exception);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedInitialRuntimeAppliedCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onInitialRuntimeApplied = runtimeLoop?.GetType().GetField(
            "_onInitialRuntimeApplied",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop) as Action;

        Assert.NotNull(onInitialRuntimeApplied);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var exception = Record.Exception(() => onInitialRuntimeApplied!());

        Assert.Null(exception);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedInitialConfigurationRejectedCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onInitialConfigurationRejected = runtimeLoop?.GetType().GetField(
            "_onInitialConfigurationRejected",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<string>;

        Assert.NotNull(onInitialConfigurationRejected);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var exception = Record.Exception(() => onInitialConfigurationRejected!("late configuration rejection"));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRuntimeEventCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onRuntimeEvent = runtimeLoop?.GetType().GetField(
            "_onEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<OpenVisionLab.Machine.Simulation.Events.SimulationEvent>;

        Assert.NotNull(onRuntimeEvent);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var runtimeEvent = new OpenVisionLab.Machine.Simulation.Events.SimulationEvent(
            EventIndex: 777,
            TickIndex: 9,
            SimulationTime: TimeSpan.FromMilliseconds(45),
            Category: "Warning",
            Code: "LateRuntimeEvent",
            Message: "late runtime event",
            CommandId: "late-command");
        var exception = Record.Exception(() => onRuntimeEvent!(runtimeEvent));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedTerminationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onTerminated = runtimeLoop?.GetType().GetField(
            "_onTerminated",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<SimulationEngineTerminationResult>;

        Assert.NotNull(onTerminated);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var termination = new SimulationEngineTerminationResult(
            SimulationEngineTerminationOutcome.Stopped,
            TickIndex: 777,
            SimulationTime: TimeSpan.FromMilliseconds(3_885),
            CurrentCommandId: "late-termination-command",
            Operation: "late-callback");
        var exception = Record.Exception(() => onTerminated!(termination));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCanonicalJournalCompletedCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onCanonicalJournalCompleted = runtimeLoop?.GetType().GetField(
            "_onCanonicalJournalCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<SimulationEventJournalSnapshot>;

        Assert.NotNull(onCanonicalJournalCompleted);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var journal = new SimulationEventJournalSnapshot(
            Capacity: 1,
            StoredEventCount: 1,
            TotalEventCount: 2,
            FirstEventIndex: 1,
            LastEventIndex: 2,
            IsCompleted: true,
            IsComplete: false,
            FirstMissingEventIndex: 2);
        var exception = Record.Exception(() => onCanonicalJournalCompleted!(journal));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCanonicalEventCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onCanonicalEvent = runtimeLoop?.GetType().GetField(
            "_onCanonicalEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<SimulationEvent>;

        Assert.NotNull(onCanonicalEvent);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var runtimeEvent = new SimulationEvent(
            EventIndex: 777,
            TickIndex: 9,
            SimulationTime: TimeSpan.FromMilliseconds(45),
            Category: "Warning",
            Code: "LateCanonicalEvent",
            Message: "late canonical event",
            CommandId: "late-canonical-command");
        var exception = Record.Exception(() => onCanonicalEvent!(runtimeEvent));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public void MainViewModelRejectsRetainedCanonicalEventBeforeVisionEvidenceDisposeAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var recorder = new OpenVisionLab.Machine.Simulation.Scenarios.DeterministicVisionExecutionRecorder(
            "late-project",
            "Late Project",
            @"D:\OpenVisionLab-TestData\Machine\late-project.ovmachine",
            "{\"id\":\"late-project\"}",
            "0.2.0-test+late",
            TimeSpan.FromMilliseconds(5),
            0,
            "late-canonical-command",
            "late-camera",
            "late-recipe",
            "late-acquisition",
            "late-frame",
            "late-inspection");
        viewModel.Camera.VisionEvidence.BeginCapture(recorder);
        var recorderEvents = recorder.GetType().GetField(
            "_events",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(recorder)
            as System.Collections.ICollection;
        Assert.NotNull(recorderEvents);
        var eventCountBeforeDispose = recorderEvents.Count;

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onCanonicalEvent = runtimeLoop?.GetType().GetField(
            "_onCanonicalEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<SimulationEvent>;

        Assert.NotNull(onCanonicalEvent);

        viewModel.Dispose();
        var runtimeEvent = new SimulationEvent(
            EventIndex: 778,
            TickIndex: 10,
            SimulationTime: TimeSpan.FromMilliseconds(50),
            Category: "Vision",
            Code: "CameraTriggered",
            Message: "late canonical trigger",
            CommandId: "late-canonical-command");
        var exception = Record.Exception(() => onCanonicalEvent!(runtimeEvent));

        Assert.Null(exception);
        Assert.Equal(eventCountBeforeDispose, recorderEvents.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedUnhandledExceptionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var onUnhandledException = runtimeLoop?.GetType().GetField(
            "_onUnhandledException",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<Exception>;

        Assert.NotNull(onUnhandledException);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var statusBefore = viewModel.StatusMessage;
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var exception = Record.Exception(() =>
            onUnhandledException!(new InvalidOperationException("late unhandled exception")));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
        Assert.Equal(statusBefore, viewModel.StatusMessage);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedShutdownDiagnosticCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeShutdown = simulationSession?.GetType().GetField(
            "_runtimeShutdown",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var recordDiagnostic = runtimeShutdown?.GetType().GetField(
            "_recordDiagnostic",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeShutdown)
            as Action<SimulationRuntimeShutdownDiagnostic>;

        Assert.NotNull(recordDiagnostic);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var diagnostic = new SimulationRuntimeShutdownDiagnostic(
            SimulationOperationalDiagnosticKind.ShutdownCompleted,
            SimulationLogSeverity.Info,
            "late shutdown diagnostic",
            "LateCallback");
        var exception = Record.Exception(() => recordDiagnostic!(diagnostic));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCloseAdmissionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setCloseAdmission = simulationSession?.GetType().GetField(
            "_setCloseAdmission",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession)
            as Action<bool>;
        var manualEquipment = typeof(MainViewModel).GetField(
            "_manualEquipment",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var camera = typeof(MainViewModel).GetField(
            "_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);

        Assert.NotNull(setCloseAdmission);
        Assert.NotNull(manualEquipment);
        Assert.NotNull(camera);

        viewModel.Dispose();
        Assert.True(viewModel.IsSessionCloseRequested);
        var manualEquipmentCloseRequested = (bool)manualEquipment!.GetType().GetField(
            "_sessionCloseRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manualEquipment)!;
        var cameraCloseRequested = (bool)camera!.GetType().GetField(
            "_sessionCloseRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(camera)!;
        Assert.True(manualEquipmentCloseRequested);
        Assert.True(cameraCloseRequested);
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var exception = Record.Exception(() => setCloseAdmission!(false));

        Assert.Null(exception);
        Assert.True(viewModel.IsSessionCloseRequested);
        Assert.True((bool)manualEquipment.GetType().GetField(
            "_sessionCloseRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manualEquipment)!);
        Assert.True((bool)camera.GetType().GetField(
            "_sessionCloseRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(camera)!);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRetainedProjectSaveObservationRemainsInertAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var closeWorkflow = simulationSession?.GetType().GetField(
            "_closeWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var observeProjectSave = closeWorkflow?.GetType().GetField(
            "_observeProjectSave",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(closeWorkflow)
            as Func<TimeSpan, Task<ProjectSaveParticipantResult>>;

        Assert.NotNull(observeProjectSave);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        ProjectSaveParticipantResult? observed = null;
        var exception = await Record.ExceptionAsync(async () =>
            observed = await observeProjectSave!(TimeSpan.FromSeconds(1)));

        Assert.Null(exception);
        Assert.NotNull(observed);
        Assert.Equal(ProjectSaveParticipantOutcome.Idle, observed!.Outcome);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRetainedCameraAcquisitionObservationRemainsInertAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var closeWorkflow = simulationSession?.GetType().GetField(
            "_closeWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var observeCameraAcquisition = closeWorkflow?.GetType().GetField(
            "_observeCameraAcquisition",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(closeWorkflow)
            as Func<TimeSpan, Task<CameraAcquisitionParticipantResult>>;

        Assert.NotNull(observeCameraAcquisition);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        CameraAcquisitionParticipantResult? observed = null;
        var exception = await Record.ExceptionAsync(async () =>
            observed = await observeCameraAcquisition!(TimeSpan.FromSeconds(1)));

        Assert.Null(exception);
        Assert.NotNull(observed);
        Assert.Equal(CameraAcquisitionParticipantOutcome.Idle, observed!.Outcome);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRetainedScenarioBatchObservationRemainsInertAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var closeWorkflow = simulationSession?.GetType().GetField(
            "_closeWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var observeScenarioBatch = closeWorkflow?.GetType().GetField(
            "_observeScenarioBatch",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(closeWorkflow)
            as Func<TimeSpan, Task<SimulationScenarioBatchParticipantResult>>;

        Assert.NotNull(observeScenarioBatch);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        SimulationScenarioBatchParticipantResult? observed = null;
        var exception = await Record.ExceptionAsync(async () =>
            observed = await observeScenarioBatch!(TimeSpan.FromSeconds(1)));

        Assert.Null(exception);
        Assert.NotNull(observed);
        Assert.Equal(SimulationScenarioBatchParticipantOutcome.Idle, observed!.Outcome);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedScenarioBatchPauseAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var scenarioBatch = typeof(MainViewModel).GetField(
            "_scenarioBatch",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var pauseMainRuntime = scenarioBatch?.GetType().GetField(
            "_pauseMainRuntime",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(scenarioBatch)
            as Func<Task<bool>>;

        Assert.NotNull(pauseMainRuntime);

        viewModel.Dispose();
        var statusBefore = viewModel.StatusMessage;
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var paused = true;
        var exception = await Record.ExceptionAsync(async () => paused = await pauseMainRuntime!());

        Assert.Null(exception);
        Assert.False(paused);
        Assert.Equal(statusBefore, viewModel.StatusMessage);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRetainedCommissioningValidationObservationRemainsInertAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var closeWorkflow = simulationSession?.GetType().GetField(
            "_closeWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var observeCommissioningValidation = closeWorkflow?.GetType().GetField(
            "_observeCommissioningValidation",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(closeWorkflow)
            as Func<TimeSpan, Task<MultiAxisCommissioningParticipantResult>>;

        Assert.NotNull(observeCommissioningValidation);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        MultiAxisCommissioningParticipantResult? observed = null;
        var exception = await Record.ExceptionAsync(async () =>
            observed = await observeCommissioningValidation!(TimeSpan.FromSeconds(1)));

        Assert.Null(exception);
        Assert.NotNull(observed);
        Assert.Equal(MultiAxisCommissioningParticipantOutcome.Idle, observed!.Outcome);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRetainedIntegrationObservationRemainsInertAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var closeWorkflow = simulationSession?.GetType().GetField(
            "_closeWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var observeIntegration = closeWorkflow?.GetType().GetField(
            "_observeIntegration",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(closeWorkflow)
            as Func<TimeSpan, Task<MachineIntegrationParticipantResult>>;

        Assert.NotNull(observeIntegration);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        MachineIntegrationParticipantResult? observed = null;
        var exception = await Record.ExceptionAsync(async () =>
            observed = await observeIntegration!(TimeSpan.FromSeconds(1)));

        Assert.Null(exception);
        Assert.NotNull(observed);
        Assert.Equal(MachineIntegrationParticipantOutcome.Idle, observed!.Outcome);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRetainedShellDisposeCompletionReleasesShellAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeResources = simulationSession?.GetType().GetField(
            "_runtimeResources",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var integrationDisposedField = viewModel.Integration.GetType().GetField(
            "_disposed",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(runtimeResources);
        Assert.NotNull(integrationDisposedField);

        viewModel.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!(bool)runtimeResources!.GetType().GetProperty(
                       "IsDisposed",
                       BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtimeResources)!
                   && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(
            (bool)runtimeResources.GetType().GetProperty(
                "IsDisposed",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtimeResources)!,
            "Runtime resources did not reach their safe disposal state.");
        Assert.True(
            (bool)integrationDisposedField!.GetValue(viewModel.Integration)!,
            "The retained shell-dispose completion callback did not release the shell integration owner.");
    }

    [Fact]
    public async Task MainViewModelRetainedUnsavedChangesResolverRemainsInertAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var projectLifecycle = typeof(MainViewModel).GetField(
            "_projectLifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var markChanged = projectLifecycle?.GetType().GetMethod(
            "MarkChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var refreshDirtyState = projectLifecycle?.GetType().GetMethod(
            "RefreshDirtyState",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(projectLifecycle);
        Assert.NotNull(markChanged);
        Assert.NotNull(refreshDirtyState);
        var currentProject = projectLifecycle.GetType().GetProperty(
            "CurrentProject",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(projectLifecycle);
        var projectName = currentProject?.GetType().GetProperty("Name");

        Assert.NotNull(currentProject);
        Assert.NotNull(projectName);
        projectName!.SetValue(currentProject, "Changed after shutdown");
        markChanged!.Invoke(projectLifecycle, null);
        refreshDirtyState!.Invoke(projectLifecycle, null);
        Assert.True(viewModel.HasUnsavedChanges);
        viewModel.UnsavedProjectPrompt = static () => UnsavedProjectDecision.Discard;

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var closeWorkflow = simulationSession?.GetType().GetField(
            "_closeWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var resolveUnsavedChanges = closeWorkflow?.GetType().GetField(
            "_resolveUnsavedChanges",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(closeWorkflow)
            as Func<Task<bool>>;

        Assert.NotNull(resolveUnsavedChanges);

        viewModel.Dispose();
        var propertyChangedCount = 0;
        viewModel.PropertyChanged += (_, _) => propertyChangedCount++;
        var invocation = new TaskCompletionSource<(bool Resolved, Exception? Exception)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                invocation.SetResult((resolveUnsavedChanges!().GetAwaiter().GetResult(), null));
            }
            catch (Exception exception)
            {
                invocation.SetResult((false, exception));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var outcome = await invocation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        thread.Join();

        Assert.Null(outcome.Exception);
        Assert.False(outcome.Resolved);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCommandPresentationStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var dispatcher = typeof(MainViewModel).GetField(
            "_simulationCommandPresentationDispatcher",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = dispatcher?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dispatcher)
            as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late command presentation status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCommandTraceStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var commandTrace = typeof(MainViewModel).GetField(
            "_simulationCommandTrace",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = commandTrace?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(commandTrace)
            as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late command trace status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedScenarioExecutionStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var coordinator = typeof(MainViewModel).GetField(
            "_simulationScenarioExecutionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = coordinator?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(coordinator)
            as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late scenario status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedScenarioExecutionDesignModeCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var coordinator = typeof(MainViewModel).GetField(
            "_simulationScenarioExecutionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setDesignMode = coordinator?.GetType().GetField(
            "_setDesignMode",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(coordinator)
            as Action<bool>;

        Assert.NotNull(setDesignMode);

        viewModel.Dispose();
        var designModeBefore = viewModel.IsDesignMode;
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var exception = Record.Exception(() => setDesignMode!(!designModeBefore));

        Assert.Null(exception);
        Assert.Equal(designModeBefore, viewModel.IsDesignMode);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedScenarioExecutionRunningCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var coordinator = typeof(MainViewModel).GetField(
            "_simulationScenarioExecutionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setRunning = coordinator?.GetType().GetField(
            "_setRunning",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(coordinator)
            as Action<bool>;

        Assert.NotNull(setRunning);

        viewModel.Dispose();
        var runningBefore = viewModel.IsRunning;
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var exception = Record.Exception(() => setRunning!(true));

        Assert.Null(exception);
        Assert.Equal(runningBefore, viewModel.IsRunning);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRuntimeProjectionRunningCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var projection = typeof(MainViewModel).GetField(
            "_runtimeProjectionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setRunning = projection?.GetType().GetField(
            "_setRunning",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(projection)
            as Action<bool>;

        Assert.NotNull(setRunning);

        viewModel.Dispose();
        var runningBefore = viewModel.IsRunning;
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var exception = Record.Exception(() => setRunning!(true));

        Assert.Null(exception);
        Assert.Equal(runningBefore, viewModel.IsRunning);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRuntimeProjectionCameraCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var projection = typeof(MainViewModel).GetField(
            "_runtimeProjectionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var refreshCameraProjection = projection?.GetType().GetField(
            "_refreshCameraProjection",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(projection)
            as Action;
        var camera = typeof(MainViewModel).GetField(
            "_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel)
            as INotifyPropertyChanged;

        Assert.NotNull(refreshCameraProjection);
        Assert.NotNull(camera);

        viewModel.Dispose();
        var rootNotificationCount = 0;
        var cameraNotificationCount = 0;
        viewModel.PropertyChanged += (_, _) => rootNotificationCount++;
        camera!.PropertyChanged += (_, _) => cameraNotificationCount++;
        var exception = Record.Exception(() => refreshCameraProjection!());

        Assert.Null(exception);
        Assert.Equal(0, rootNotificationCount);
        Assert.Equal(0, cameraNotificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRuntimeProjectionManualEquipmentCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var projection = typeof(MainViewModel).GetField(
            "_runtimeProjectionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var refreshManualProjection = projection?.GetType().GetField(
            "_refreshManualProjection",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(projection)
            as Action<SimulationSnapshot>;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var engine = simulationSession?.GetType().GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession)
            as ISimulationEngine;
        var manualEquipment = typeof(MainViewModel).GetField(
            "_manualEquipment",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel)
            as INotifyPropertyChanged;

        Assert.NotNull(refreshManualProjection);
        Assert.NotNull(engine);
        Assert.NotNull(manualEquipment);

        viewModel.Dispose();
        var rootNotificationCount = 0;
        var manualEquipmentNotificationCount = 0;
        viewModel.PropertyChanged += (_, _) => rootNotificationCount++;
        manualEquipment!.PropertyChanged += (_, _) => manualEquipmentNotificationCount++;
        var exception = Record.Exception(() => refreshManualProjection!(engine!.CurrentSnapshot));

        Assert.Null(exception);
        Assert.Equal(0, rootNotificationCount);
        Assert.Equal(0, manualEquipmentNotificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRuntimeProjectionDebuggerSnapshotCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var projection = typeof(MainViewModel).GetField(
            "_runtimeProjectionCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeDebugger = projection?.GetType().GetField(
            "_runtimeDebugger",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(projection)
            as INotifyPropertyChanged;
        var applySnapshot = runtimeDebugger?.GetType().GetMethod(
            "ApplySnapshot",
            BindingFlags.Instance | BindingFlags.Public)?.CreateDelegate(
                typeof(Action<SimulationSnapshot>),
                runtimeDebugger) as Action<SimulationSnapshot>;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var engine = simulationSession?.GetType().GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession)
            as ISimulationEngine;
        var debuggerDisposed = runtimeDebugger?.GetType().GetProperty(
            "IsDisposed",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(runtimeDebugger);
        Assert.NotNull(applySnapshot);
        Assert.NotNull(engine);
        Assert.NotNull(debuggerDisposed);

        viewModel.Dispose();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!(bool)debuggerDisposed!.GetValue(runtimeDebugger)! && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(
            (bool)debuggerDisposed.GetValue(runtimeDebugger)!,
            "Runtime debugger did not reach its disposed state after MainViewModel.Dispose.");

        var rootNotificationCount = 0;
        var debuggerNotificationCount = 0;
        viewModel.PropertyChanged += (_, _) => rootNotificationCount++;
        runtimeDebugger.PropertyChanged += (_, _) => debuggerNotificationCount++;
        var exception = Record.Exception(() => applySnapshot!(engine!.CurrentSnapshot));

        Assert.Null(exception);
        Assert.Equal(0, rootNotificationCount);
        Assert.Equal(0, debuggerNotificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRuntimeProjectionVisionEvidenceCompletionAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var evidence = viewModel.Camera.VisionEvidence;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var engine = simulationSession?.GetType().GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession)
            as ISimulationEngine;
        Assert.NotNull(engine);
        var recorder = new OpenVisionLab.Machine.Simulation.Scenarios.DeterministicVisionExecutionRecorder(
            "late-project",
            "Late Project",
            @"D:\OpenVisionLab-TestData\Machine\late-project.ovmachine",
            "{\"id\":\"late-project\"}",
            "0.2.0-test+late",
            TimeSpan.FromMilliseconds(5),
            0,
            "late-vision-command",
            "late-camera",
            "late-recipe",
            "late-acquisition",
            "late-frame",
            "late-inspection");
        evidence.BeginCapture(recorder);
        var tryComplete = new Func<SimulationSnapshot, bool>(evidence.TryComplete);
        var recorderEvents = recorder.GetType().GetField(
            "_events",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(recorder)
            as System.Collections.ICollection;
        Assert.NotNull(recorderEvents);
        var eventCountBeforeDispose = recorderEvents.Count;
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var rootNotificationCount = 0;
        var evidenceNotificationCount = 0;
        viewModel.PropertyChanged += (_, _) => rootNotificationCount++;
        evidence.PropertyChanged += (_, _) => evidenceNotificationCount++;
        var exception = Record.Exception(() => Assert.False(tryComplete(engine!.CurrentSnapshot)));

        Assert.Null(exception);
        Assert.Equal(eventCountBeforeDispose, recorderEvents.Count);
        Assert.False(evidence.IsCapturing);
        Assert.Null(evidence.LatestEvidence);
        Assert.Equal(0, rootNotificationCount);
        Assert.Equal(0, evidenceNotificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationRunningCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var setRunning = runControl?.GetType().GetField(
            "_setRunning",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Action<bool>;

        Assert.NotNull(setRunning);
        var exception = Record.Exception(() => setRunning!(true));

        Assert.Null(exception);
        Assert.False(viewModel.IsRunning);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationDesignModeCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var designModeBefore = viewModel.IsDesignMode;
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var setDesignMode = runControl?.GetType().GetField(
            "_setDesignMode",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Action<bool>;

        Assert.NotNull(setDesignMode);
        var exception = Record.Exception(() => setDesignMode!(!designModeBefore));

        Assert.Null(exception);
        Assert.Equal(designModeBefore, viewModel.IsDesignMode);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationSnapshotCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var engine = simulationSession?.GetType().GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession)
            as ISimulationEngine;
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var applySnapshot = runControl?.GetType().GetField(
            "_applySnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Action<SimulationSnapshot>;

        Assert.NotNull(engine);
        Assert.NotNull(applySnapshot);
        var exception = Record.Exception(() => applySnapshot!(engine!.CurrentSnapshot));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationSnapshotPublicationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var publishedCount = 0;
        viewModel.SceneSnapshots.SnapshotPublished += (_, _) => publishedCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var engine = simulationSession?.GetType().GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession)
            as ISimulationEngine;
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var publishSnapshot = runtimeLoop?.GetType().GetField(
            "_publishSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop)
            as Action<SimulationSnapshot>;

        Assert.NotNull(engine);
        Assert.NotNull(publishSnapshot);
        var latestBefore = viewModel.SceneSnapshots.Latest;
        var exception = Record.Exception(() => publishSnapshot!(engine!.CurrentSnapshot));

        Assert.Null(exception);
        Assert.Same(latestBefore, viewModel.SceneSnapshots.Latest);
        Assert.Equal(0, publishedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationCommandInvalidationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var commandChangedCount = 0;
        viewModel.SaveProjectCommand.CanExecuteChanged += (_, _) => commandChangedCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var notifyCommandsChanged = runControl?.GetType().GetField(
            "_notifyCommandsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl)
            as Action;

        Assert.NotNull(notifyCommandsChanged);
        var exception = Record.Exception(() => notifyCommandsChanged!());

        Assert.Null(exception);
        Assert.Equal(0, commandChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationWorkspaceCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var simulationWorkspaceChanged = typeof(MainViewModel).GetMethod(
            "OnSimulationWorkspacePropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(simulationWorkspaceChanged);
        var exception = Record.Exception(() =>
            simulationWorkspaceChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs(nameof(SimulationWorkspaceViewModel.ScenarioSeed))
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationCommandTraceCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var commandTraceChanged = typeof(MainViewModel).GetMethod(
            "OnSimulationCommandTracePropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(commandTraceChanged);
        var exception = Record.Exception(() =>
            commandTraceChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs(nameof(SimulationCommandTraceViewModel.CanStartCapture))
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCameraCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var cameraChanged = typeof(MainViewModel).GetMethod(
            "OnCameraPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(cameraChanged);
        var exception = Record.Exception(() =>
            cameraChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs("CurrentCameraEvidenceDetailsText")
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedManualEquipmentCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var manualEquipmentChanged = typeof(MainViewModel).GetMethod(
            "OnManualEquipmentPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(manualEquipmentChanged);
        var exception = Record.Exception(() =>
            manualEquipmentChanged!.Invoke(
                viewModel,
                new object?[]
                {
                    null,
                    new PropertyChangedEventArgs("CurrentSensorId")
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectRuntimeApplicationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var stateChanged = typeof(MainViewModel).GetMethod(
            "OnProjectRuntimeApplicationStateChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(stateChanged);
        var exception = Record.Exception(() =>
            stateChanged!.Invoke(viewModel, new object?[] { false }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectRuntimeApplicationCompletionAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var currentProject = typeof(MainViewModel)
            .GetProperty("CurrentProject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel);
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var projectApplied = typeof(MainViewModel).GetMethod(
            "CompleteProjectRuntimeApplication",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(projectApplied);
        var exception = Record.Exception(() =>
            projectApplied!.Invoke(viewModel, new[] { currentProject }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectTransitionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var transitionCompleted = typeof(MainViewModel).GetMethod(
            "OnProjectTransitionCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(transitionCompleted);
        var exception = Record.Exception(() =>
            transitionCompleted!.Invoke(
                viewModel,
                new object?[]
                {
                    new ProjectLifecycleTransition(
                        ProjectLifecycleTransitionKind.BundledSampleOpened,
                        new MachineProjectDocument { Name = "Late transition" },
                        null)
                }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectSaveCompletionAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var saveCompleted = typeof(MainViewModel).GetMethod(
            "OnProjectSaveCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(saveCompleted);
        var result = new ProjectSaveLifecycleResult(
            new ProjectDocumentSaveReceipt("retained-session", 1, "late-save.ovmachine", "hash"),
            true);
        var exception = Record.Exception(() =>
            saveCompleted!.Invoke(viewModel, new object?[] { result }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedBlankLayoutCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var blankLayoutStarted = typeof(MainViewModel).GetMethod(
            "OnBlankLayoutStarted",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(blankLayoutStarted);
        var exception = Record.Exception(() => blankLayoutStarted!.Invoke(viewModel, null));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectRuntimeRejectionAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var diagnosticCount = viewModel.OperationalDiagnostics.Count;
        var logMessageCount = viewModel.LogMessages.Count;
        var runtimeRejected = typeof(MainViewModel).GetMethod(
            "OnProjectRuntimeApplicationRejected",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(runtimeRejected);
        var result = new RuntimeDefinitionApplicationResult(
            RuntimeDefinitionApplicationOutcome.CompilationRejected,
            "late rejection",
            null);
        var exception = Record.Exception(() =>
            runtimeRejected!.Invoke(viewModel, new object?[] { result }));

        Assert.Null(exception);
        Assert.Equal(diagnosticCount, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(logMessageCount, viewModel.LogMessages.Count);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedScenarioBatchPresentationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var scenarioBatchChanged = typeof(MainViewModel).GetMethod(
            "OnScenarioBatchPresentationChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(scenarioBatchChanged);
        var exception = Record.Exception(() =>
            scenarioBatchChanged!.Invoke(viewModel, new object?[] { true }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectTreeSelectionPresentationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var treeSelectionChanged = typeof(MainViewModel).GetMethod(
            "OnProjectTreeSelectionPresentationChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(treeSelectionChanged);
        var exception = Record.Exception(() =>
            treeSelectionChanged!.Invoke(viewModel, new object?[] { false }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedLayoutSelectionPresentationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var layoutSelectionChanged = typeof(MainViewModel).GetMethod(
            "OnLayoutSelectionPresentationChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(layoutSelectionChanged);
        var exception = Record.Exception(() =>
            layoutSelectionChanged!.Invoke(viewModel, null));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedAxisDefinitionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var axisDefinitionChanged = typeof(MainViewModel).GetMethod(
            "OnAxisDefinitionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(axisDefinitionChanged);
        var exception = Record.Exception(() =>
            axisDefinitionChanged!.Invoke(viewModel, null));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedAnalogChannelDefinitionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var analogDefinitionChanged = typeof(MainViewModel).GetMethod(
            "OnAnalogChannelDefinitionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(analogDefinitionChanged);
        var exception = Record.Exception(() =>
            analogDefinitionChanged!.Invoke(viewModel, null));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedLayoutDefinitionCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var layoutDefinitionChanged = typeof(MainViewModel).GetMethod(
            "OnLayoutDefinitionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(layoutDefinitionChanged);
        var exception = Record.Exception(() =>
            layoutDefinitionChanged!.Invoke(viewModel, null));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedMultiAxisCommissioningRecipeCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var recipeChanged = typeof(MainViewModel).GetMethod(
            "OnMultiAxisCommissioningRecipeChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(recipeChanged);
        var exception = Record.Exception(() =>
            recipeChanged!.Invoke(viewModel, null));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedMultiAxisCommissioningPresentationCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var presentationChanged = typeof(MainViewModel).GetMethod(
            "OnMultiAxisCommissioningPresentationChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(presentationChanged);
        var exception = Record.Exception(() =>
            presentationChanged!.Invoke(viewModel, new object?[] { true }));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedMultiAxisStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var commissioning = typeof(MainViewModel).GetField(
            "_multiAxisCommissioning",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = commissioning?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(commissioning) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late multi-axis status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedScenarioBatchStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var scenarioBatch = typeof(MainViewModel).GetField(
            "_scenarioBatch",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = scenarioBatch?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(scenarioBatch) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late scenario batch status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedEquipmentCommandStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var dispatcher = typeof(MainViewModel).GetField(
            "_equipmentCommandDispatcher",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = dispatcher?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dispatcher) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late equipment command status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedUnifiedEvidenceStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var evidence = typeof(MainViewModel).GetField(
            "_unifiedCommissioningEvidence",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = evidence?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(evidence) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late unified evidence status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedRecipeAuthoringStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var authoring = typeof(MainViewModel).GetField(
            "_recipeAuthoring",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = authoring?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(authoring) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late recipe authoring status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCameraStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var camera = typeof(MainViewModel).GetField(
            "_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var setStatus = camera?.GetType().GetField(
            "_setStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(camera) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late camera status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCancelVisionCaptureCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var camera = typeof(MainViewModel).GetField(
            "_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        Assert.NotNull(camera);

        var propertyChangedCount = 0;
        ((INotifyPropertyChanged)camera!).PropertyChanged += (_, _) => propertyChangedCount++;
        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runControl = simulationSession?.GetType().GetField(
            "_runControl",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var cancelVisionCapture = runControl?.GetType().GetField(
            "_cancelVisionCapture",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runControl) as Action;

        Assert.NotNull(cancelVisionCapture);
        var exception = Record.Exception(() => cancelVisionCapture!());

        Assert.Null(exception);
        Assert.Equal(0, propertyChangedCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedSimulationDispatchCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var simulationSession = typeof(MainViewModel).GetField(
            "_simulationSession",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var runtimeLoop = simulationSession?.GetType().GetField(
            "_runtimeLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(simulationSession);
        var dispatch = runtimeLoop?.GetType().GetField(
            "_dispatch",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtimeLoop) as Func<Action, Task>;

        Assert.NotNull(dispatch);

        viewModel.Dispose();

        var invocationCount = 0;
        var exception = await Record.ExceptionAsync(() => dispatch!(() => invocationCount++));

        Assert.Null(exception);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedLayoutStatusCallbackAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var layoutAuthoring = typeof(MainViewModel).GetField(
            "_layoutAuthoring",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel);
        var history = layoutAuthoring?.GetType().GetField(
            "_history",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(layoutAuthoring);
        var setStatus = history?.GetType().GetField(
            "_setStatusMessage",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(history) as Action<string>;

        Assert.NotNull(setStatus);
        var exception = Record.Exception(() => setStatus!("late layout status"));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedClosePresentationAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        var exception = Record.Exception(() =>
        {
            viewModel.PresentCloseResult(new SimulationSessionCloseResult(
                SimulationSessionCloseOutcome.ShutdownIncomplete));
            viewModel.PresentCloseFailure(new InvalidOperationException("late close failure"));
        });

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectOpenFailureAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var presentedCount = 0;
        viewModel.ProjectOpenFailurePresenter = _ => presentedCount++;
        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        var openFailure = typeof(MainViewModel).GetMethod(
            "HandleProjectOpenFailure",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(openFailure);

        var exception = Record.Exception(() => openFailure!.Invoke(
            viewModel,
            [new FileNotFoundException("late project open failure", "late.ovmachine")]));

        Assert.Null(exception);
        Assert.Equal(0, presentedCount);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedProjectSaveFailureAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        var dialogHost = typeof(MainViewModel).GetField(
            "_mainMessageDialogHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dialogHost);
        dialogHost!.SetValue(viewModel, null);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        var saveFailure = typeof(MainViewModel).GetMethod(
            "HandleProjectSaveFailure",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(saveFailure);

        var exception = Record.Exception(() => saveFailure!.Invoke(
            viewModel,
            [new IOException("late project save failure")]));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedBatchMismatchNavigationAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        var navigateToMismatch = typeof(MainViewModel).GetMethod(
            "NavigateToBatchMismatch",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(navigateToMismatch);

        var mismatch = new OpenVisionLab.Machine.Simulation.Scenarios.DeterministicSimulationBatchMismatch(
            1,
            "ScenarioMismatch",
            "late mismatch",
            "Condition",
            "late-target",
            17,
            "ABCDEF1234567890");

        var exception = Record.Exception(() => navigateToMismatch!.Invoke(
            viewModel,
            [mismatch]));

        Assert.Null(exception);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task MainViewModelRejectsRetainedCommissioningMismatchNavigationAfterDispose()
    {
        using var viewModel = new MainViewModel();
        var shutdown = await viewModel.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdown.Outcome);

        viewModel.Dispose();
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;
        var statusBefore = viewModel.StatusMessage;
        var diagnosticsBefore = viewModel.OperationalDiagnostics.Count;
        var navigateToMismatch = typeof(MainViewModel).GetMethod(
            "NavigateToCommissioningMismatch",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(navigateToMismatch);

        var mismatch = new OpenVisionLab.Machine.Simulation.Commissioning.DeterministicCommissioningMismatch(
            1,
            17,
            "Snapshot",
            "late-axis",
            "EXPECTED",
            "ACTUAL");

        var exception = Record.Exception(() => navigateToMismatch!.Invoke(
            viewModel,
            [mismatch]));

        Assert.Null(exception);
        Assert.Equal(statusBefore, viewModel.StatusMessage);
        Assert.Equal(diagnosticsBefore, viewModel.OperationalDiagnostics.Count);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public void MainViewModelNavigatesLiveCommissioningMismatchThroughExistingLayoutAndJournalOwners()
    {
        var samplePath = Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine");
        var project = new ProjectDocumentStore().Load(File.ReadAllText(samplePath));
        using var viewModel = new MainViewModel(project);
        var target = viewModel.Layout.Items.First(item => item.Component is not null);
        var navigateToMismatch = typeof(MainViewModel).GetMethod(
            "NavigateToCommissioningMismatch",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(navigateToMismatch);

        var mismatch = new OpenVisionLab.Machine.Simulation.Commissioning.DeterministicCommissioningMismatch(
            1,
            17,
            "Snapshot",
            target.Id,
            "EXPECTED",
            "ACTUAL");

        var exception = Record.Exception(() => navigateToMismatch!.Invoke(
            viewModel,
            [mismatch]));

        Assert.Null(exception);
        Assert.Equal(target.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Contains(target.Id, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("17", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Category == "Motion"
                && diagnostic.Message.Contains("Commissioning mismatch selected", StringComparison.Ordinal)
                && diagnostic.Message.Contains(target.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void MainViewModelNavigatesLiveBatchMismatchThroughExistingLayoutAndJournalOwners()
    {
        var samplePath = Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine");
        var project = new ProjectDocumentStore().Load(File.ReadAllText(samplePath));
        using var viewModel = new MainViewModel(project);
        var target = viewModel.Layout.Items.First(item => item.Component is not null);
        var navigateToMismatch = typeof(MainViewModel).GetMethod(
            "NavigateToBatchMismatch",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(navigateToMismatch);

        var mismatch = new OpenVisionLab.Machine.Simulation.Scenarios.DeterministicSimulationBatchMismatch(
            1,
            "ScenarioMismatch",
            "live mismatch",
            "Condition",
            target.Id,
            17,
            "ABCDEF1234567890");

        var exception = Record.Exception(() => navigateToMismatch!.Invoke(
            viewModel,
            [mismatch]));

        Assert.Null(exception);
        Assert.Equal(target.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Contains(target.Id, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("17", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(
            viewModel.OperationalDiagnostics,
            diagnostic => diagnostic.Category == "Batch"
                && diagnostic.Message.Contains("First mismatch selected", StringComparison.Ordinal)
                && diagnostic.Message.Contains(target.Id, StringComparison.Ordinal));
    }

}
