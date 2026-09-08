using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualCameraAcquisitionParticipantTests
{
    [Fact]
    public async Task TracksCompletionAndReturnsTheCameraResult()
    {
        using var participant = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraAcquisitionParticipant();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerResult(
            OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerOutcome.Accepted);

        var operation = participant.TrackAsync(async _ =>
        {
            started.SetResult(true);
            await release.Task;
            return expected;
        });
        await started.Task;
        var observationTask = participant.ObserveAsync(TimeSpan.FromSeconds(1));

        Assert.False(observationTask.IsCompleted);
        release.SetResult(true);

        var observation = await observationTask;
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Completed,
            observation.Outcome);
        Assert.Same(expected, observation.Trigger);
        Assert.Same(expected, await operation);
    }

    [Fact]
    public async Task RepeatedTrackCallsShareTheInFlightAcquisition()
    {
        using var participant = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraAcquisitionParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerResult(
            OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerOutcome.Accepted);

        var first = participant.TrackAsync(async _ =>
        {
            await release.Task;
            return expected;
        });
        var second = participant.TrackAsync(_ => Task.FromResult(
            new OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerResult(
                OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerOutcome.DispatchRejected)));

        Assert.Same(first, second);
        release.SetResult(true);
        Assert.Same(expected, await second);
    }

    [Fact]
    public async Task InvalidateCancelsTheTrackedAcquisition()
    {
        using var participant = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraAcquisitionParticipant();
        var operation = participant.TrackAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerResult(
                OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerOutcome.Accepted);
        });

        // Observe the in-flight operation before cancellation can finish it.
        // A later observation intentionally sees Idle once the task has ended.
        var observationTask = participant.ObserveAsync(TimeSpan.FromSeconds(1));
        Assert.False(observationTask.IsCompleted);
        participant.Invalidate();

        var observation = await observationTask;
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Cancelled,
            observation.Outcome);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        var idleObservation = await participant.ObserveAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Idle,
            idleObservation.Outcome);
    }

    [Fact]
    public async Task ObservationTimeoutDoesNotCancelTheAcquisition()
    {
        using var participant = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraAcquisitionParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(async _ =>
        {
            await release.Task;
            return new OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerResult(
                OpenVisionLab.MachineStudio.ViewModel.ManualCameraTriggerOutcome.Accepted);
        });

        var observation = await participant.ObserveAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.TimedOut,
            observation.Outcome);
        Assert.False(operation.IsCompleted);

        release.SetResult(true);
        await operation;
    }

    [Fact]
    public async Task OperationFailureIsObservedWithoutChangingTheCallerException()
    {
        using var participant = new OpenVisionLab.MachineStudio.ViewModel.ManualCameraAcquisitionParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("camera acquisition failed");
        var operation = participant.TrackAsync(async _ =>
        {
            await release.Task;
            throw expected;
        });
        var observationTask = participant.ObserveAsync(TimeSpan.FromSeconds(1));
        release.SetResult(true);

        var observation = await observationTask;
        Assert.Equal(
            OpenVisionLab.MachineStudio.ViewModel.CameraAcquisitionParticipantOutcome.Failed,
            observation.Outcome);
        Assert.Same(expected, observation.Exception);
        var callerException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation);
        Assert.Same(expected, callerException);
    }
}
