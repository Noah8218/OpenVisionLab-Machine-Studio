using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationSessionCoordinatorTests
{
    [Fact]
    public async Task StartsRuntimeAndSharesOneCloseResult()
    {
        using var session = new SimulationSessionCoordinator(TimeSpan.FromMilliseconds(1));
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var admissionStates = new List<bool>();

        session.Start(CreateStartup(
            session,
            initialRuntimeApplied,
            admissionStates.Add));

        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var firstClose = session.RequestCloseAsync(TimeSpan.FromSeconds(2));
        var secondClose = session.RequestCloseAsync(TimeSpan.FromSeconds(2));
        var results = await Task.WhenAll(firstClose, secondClose);

        Assert.All(results, result =>
            Assert.Equal(SimulationSessionCloseOutcome.Approved, result.Outcome));
        Assert.Contains(true, admissionStates);
        Assert.True(session.IsShutdownRequested);
    }

    [Fact]
    public async Task BeginDisposeSharesCompletionAndRejectsSecondStart()
    {
        using var session = new SimulationSessionCoordinator(TimeSpan.FromMilliseconds(1));
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        session.Start(CreateStartup(session, initialRuntimeApplied, _ => { }));
        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<InvalidOperationException>(() =>
            session.Start(CreateStartup(session, initialRuntimeApplied, _ => { })));

        var disposeCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeginDispose(
            TimeSpan.FromSeconds(2),
            () => disposeCompleted.TrySetResult(true));
        session.BeginDispose(
            TimeSpan.FromSeconds(2),
            () => disposeCompleted.TrySetResult(false));

        Assert.True(await disposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static SimulationSessionStartup CreateStartup(
        SimulationSessionCoordinator session,
        TaskCompletionSource<bool> initialRuntimeApplied,
        Action<bool> setCloseAdmission) =>
        new()
        {
            ProjectId = "project-a",
            InitialRuntime = new SimulationRuntimeConfiguration([], [], []),
            GetRunControlState = static () => new SimulationRunControlState(
                false,
                false,
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                SimulationControlOwner.Definition,
                null,
                null),
            EnsureRuntimeDefinitionApplied = static () => Task.FromResult(true),
            SetDesignMode = _ => { },
            SetRunning = _ => { },
            ApplySnapshot = _ => { },
            CancelVisionCapture = () => { },
            SetStatus = _ => { },
            Log = (_, _) => { },
            NotifyCommandsChanged = () => { },
            Dispatch = static action =>
            {
                action();
                return Task.CompletedTask;
            },
            PublishSnapshot = _ => { },
            OnInitialRuntimeApplied = () => initialRuntimeApplied.TrySetResult(true),
            OnInitialConfigurationRejected = _ => { },
            OnRuntimeEvent = _ => { },
            OnTerminated = _ => { },
            OnUnhandledException = exception => initialRuntimeApplied.TrySetException(exception),
            OnCanonicalEvent = _ => { },
            OnCanonicalJournalCompleted = _ => { },
            Workspace = new SimulationWorkspaceViewModel(),
            ScenarioBatch = null,
            MultiAxisCommissioning = null,
            ObserveProjectSave = static _ => Task.FromResult(
                new ProjectSaveParticipantResult(ProjectSaveParticipantOutcome.Idle)),
            ObserveCameraAcquisition = static _ => Task.FromResult(
                new CameraAcquisitionParticipantResult(CameraAcquisitionParticipantOutcome.Idle)),
            ObserveScenarioBatch = static _ => Task.FromResult(
                new SimulationScenarioBatchParticipantResult(SimulationScenarioBatchParticipantOutcome.Idle)),
            ObserveCommissioningValidation = static _ => Task.FromResult(
                new MultiAxisCommissioningParticipantResult(MultiAxisCommissioningParticipantOutcome.Idle)),
            ObserveIntegration = static _ => Task.FromResult(
                new MachineIntegrationParticipantResult(MachineIntegrationParticipantOutcome.Idle)),
            ResolveUnsavedChanges = static () => Task.FromResult(true),
            SetCloseAdmission = setCloseAdmission,
            RecordShutdownDiagnostic = _ => { }
        };
}
