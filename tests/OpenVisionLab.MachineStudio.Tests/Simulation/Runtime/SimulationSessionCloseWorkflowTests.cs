using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationSessionCloseWorkflowTests
{
    [Fact]
    public async Task RejectedUnsavedChangesReleaseAdmissionAndAllowRetry()
    {
        var decisions = new Queue<bool>([false, true]);
        var admissions = new List<bool>();
        var shutdownCalls = 0;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () => Task.FromResult(decisions.Dequeue()),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) =>
            {
                shutdownCalls++;
                return Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                    OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                    TimeSpan.Zero));
            },
            admissions.Add);

        var rejected = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        var approved = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.UnsavedChangesRejected,
            rejected.Outcome);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Approved,
            approved.Outcome);
        Assert.Equal([true, false, true], admissions);
        Assert.Equal(1, shutdownCalls);
    }

    [Fact]
    public async Task ShutdownResultIsMappedAndConcurrentCloseSharesOneTransaction()
    {
        var shutdownCompletion = new TaskCompletionSource<
            OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownCalls = 0;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () => Task.FromResult(true),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) =>
            {
                shutdownCalls++;
                return shutdownCompletion.Task;
            },
            _ => { });

        var firstTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        var secondTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        var shutdown = new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
            OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.TimedOut,
            TimeSpan.FromSeconds(1),
            "EngineStop");
        shutdownCompletion.SetResult(shutdown);

        var first = await firstTask;
        var second = await secondTask;

        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.ShutdownTimedOut,
            first.Outcome);
        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Same(shutdown, first.Shutdown);
        Assert.Equal(1, shutdownCalls);
        Assert.True(workflow.IsCloseRequested);
    }

    [Fact]
    public async Task ResolverFailureIsReturnedAsTypedFailure()
    {
        var expected = new InvalidOperationException("prompt failed");
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () => Task.FromException<bool>(expected),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var result = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Failed,
            result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.True(result.IsRetryable);
        Assert.False(workflow.IsCloseRequested);
    }

    [Fact]
    public async Task CloseWaitsForAnInFlightProjectSaveBeforeResolvingUnsavedChanges()
    {
        var saveObservationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSaveObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverCalled = false;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            async _ =>
            {
                saveObservationStarted.SetResult(true);
                await releaseSaveObservation.Task;
                return new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                    OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Completed);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var closeTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        await saveObservationStarted.Task;

        Assert.False(resolverCalled);
        Assert.False(closeTask.IsCompleted);

        releaseSaveObservation.SetResult(true);
        var result = await closeTask;

        Assert.True(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Completed,
            result.Save?.Outcome);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Approved,
            result.Outcome);
    }

    [Fact]
    public async Task SaveObservationTimeoutReturnsRetryableCloseFailure()
    {
        var resolverCalled = false;
        var expected = new TimeoutException("save observation timed out");
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.TimedOut,
                Exception: expected)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var result = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.False(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Failed,
            result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.Same(expected, result.Save?.Exception);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task CloseWaitsForAnInFlightCameraAcquisitionBeforeResolvingUnsavedChanges()
    {
        var cameraObservationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCameraObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverCalled = false;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            async _ =>
            {
                cameraObservationStarted.SetResult(true);
                await releaseCameraObservation.Task;
                return new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                    OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Cancelled);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var closeTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        await cameraObservationStarted.Task;

        Assert.False(resolverCalled);
        Assert.False(closeTask.IsCompleted);

        releaseCameraObservation.SetResult(true);
        var result = await closeTask;

        Assert.True(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Cancelled,
            result.Camera?.Outcome);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Approved,
            result.Outcome);
    }

    [Fact]
    public async Task CameraObservationTimeoutReturnsRetryableCloseFailure()
    {
        var resolverCalled = false;
        var expected = new TimeoutException("camera observation timed out");
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.TimedOut,
                Exception: expected)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var result = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.False(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Failed,
            result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.Same(expected, result.Camera?.Exception);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task CloseWaitsForAnInFlightScenarioBatchBeforeResolvingUnsavedChanges()
    {
        var batchObservationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBatchObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverCalled = false;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            async _ =>
            {
                batchObservationStarted.SetResult(true);
                await releaseBatchObservation.Task;
                return new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                    OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Completed);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var closeTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        await batchObservationStarted.Task;

        Assert.False(resolverCalled);
        Assert.False(closeTask.IsCompleted);

        releaseBatchObservation.SetResult(true);
        var result = await closeTask;

        Assert.True(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Completed,
            result.ScenarioBatch?.Outcome);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Approved,
            result.Outcome);
    }

    [Fact]
    public async Task ScenarioBatchObservationTimeoutReturnsRetryableCloseFailure()
    {
        var resolverCalled = false;
        var expected = new TimeoutException("scenario batch observation timed out");
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.TimedOut,
                Exception: expected)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var result = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.False(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Failed,
            result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.Same(expected, result.ScenarioBatch?.Exception);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task CloseWaitsForAnInFlightCommissioningValidationBeforeResolvingUnsavedChanges()
    {
        var validationObservationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValidationObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverCalled = false;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            async _ =>
            {
                validationObservationStarted.SetResult(true);
                await releaseValidationObservation.Task;
                return new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                    OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Completed);
            },
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var closeTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        await validationObservationStarted.Task;

        Assert.False(resolverCalled);
        Assert.False(closeTask.IsCompleted);

        releaseValidationObservation.SetResult(true);
        var result = await closeTask;

        Assert.True(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Completed,
            result.CommissioningValidation?.Outcome);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Approved,
            result.Outcome);
    }

    [Fact]
    public async Task CommissioningValidationObservationTimeoutReturnsRetryableCloseFailure()
    {
        var resolverCalled = false;
        var expected = new TimeoutException("commissioning validation observation timed out");
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.TimedOut,
                Exception: expected)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { });

        var result = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.False(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Failed,
            result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.Same(expected, result.CommissioningValidation?.Exception);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task CloseWaitsForAnInFlightIntegrationOperationBeforeResolvingUnsavedChanges()
    {
        var integrationObservationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIntegrationObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverCalled = false;
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { },
            observeIntegration: async _ =>
            {
                integrationObservationStarted.SetResult(true);
                await releaseIntegrationObservation.Task;
                return new OpenVisionLab.MachineStudio.ViewModel.MachineIntegrationParticipantResult(
                    OpenVisionLab.MachineStudio.ViewModel.MachineIntegrationParticipantOutcome.Completed);
            });

        var closeTask = workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));
        await integrationObservationStarted.Task;

        Assert.False(resolverCalled);
        Assert.False(closeTask.IsCompleted);

        releaseIntegrationObservation.SetResult(true);
        var result = await closeTask;

        Assert.True(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.MachineIntegrationParticipantOutcome.Completed,
            result.Integration?.Outcome);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Approved,
            result.Outcome);
    }

    [Fact]
    public async Task IntegrationObservationTimeoutReturnsRetryableCloseFailure()
    {
        var resolverCalled = false;
        var expected = new TimeoutException("integration observation timed out");
        var workflow = new OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseWorkflow(
            () =>
            {
                resolverCalled = true;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.ProjectSaveParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.SimulationScenarioBatchParticipantOutcome.Idle)),
            _ => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantResult(
                OpenVisionLab.MachineStudio.ViewModel.MultiAxisCommissioningParticipantOutcome.Idle)),
            (_, _) => Task.FromResult(new OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownResult(
                OpenVisionLab.MachineStudio.ViewModel.RuntimeShutdownOutcome.Completed,
                TimeSpan.Zero)),
            _ => { },
            observeIntegration: _ => Task.FromResult(
                new OpenVisionLab.MachineStudio.ViewModel.MachineIntegrationParticipantResult(
                    OpenVisionLab.MachineStudio.ViewModel.MachineIntegrationParticipantOutcome.TimedOut,
                    Exception: expected)));

        var result = await workflow.RequestCloseAsync(TimeSpan.FromSeconds(1));

        Assert.False(resolverCalled);
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.SimulationSessionCloseOutcome.Failed,
            result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.Same(expected, result.Integration?.Exception);
        Assert.True(result.IsRetryable);
    }
}
