using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectSaveParticipantTests
{
    [Fact]
    public async Task ObserveWaitsForTrackedSaveAndReturnsItsTypedReceipt()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new ProjectSaveParticipant();
        var save = new ProjectSaveLifecycleResult(
            new ProjectDocumentSaveReceipt("session", 4, "save.ovmachine", "hash"),
            true);

        var operation = participant.TrackSaveAsync(async () =>
        {
            started.SetResult(true);
            await release.Task;
            return save;
        });
        await started.Task;

        var observation = participant.ObserveAsync();
        Assert.False(observation.IsCompleted);

        release.SetResult(true);
        Assert.Same(save, await operation);
        var result = await observation;

        Assert.Equal(ProjectSaveParticipantOutcome.Completed, result.Outcome);
        Assert.Same(save, result.Save);
        Assert.Equal(ProjectSaveParticipantOutcome.Idle, (await participant.ObserveAsync()).Outcome);
    }

    [Fact]
    public async Task FailedSaveIsObservedWithoutChangingTheCallerException()
    {
        var expected = new IOException("save failed");
        var participant = new ProjectSaveParticipant();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = participant.TrackSaveAsync(async () =>
        {
            started.SetResult(true);
            await release.Task;
            throw expected;
        });
        await started.Task;
        var observation = participant.ObserveAsync();

        release.SetResult(true);
        var thrown = await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.Same(expected, thrown);

        var result = await observation;
        Assert.Equal(ProjectSaveParticipantOutcome.Failed, result.Outcome);
        Assert.Same(expected, result.Exception);
    }

    [Fact]
    public async Task HandledSaveFailureRemainsTypedForCloseObservation()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("handled save failure");
        var participant = new ProjectSaveParticipant();

        var operation = participant.TrackAttemptAsync(async () =>
        {
            started.SetResult(true);
            await release.Task;
            return new ProjectSaveAttemptResult(
                ProjectSaveAttemptOutcome.Failed,
                Exception: expected);
        });
        await started.Task;
        var observation = participant.ObserveAsync();

        release.SetResult(true);
        var attempt = await operation;
        var result = await observation;

        Assert.Equal(ProjectSaveAttemptOutcome.Failed, attempt.Outcome);
        Assert.Equal(ProjectSaveParticipantOutcome.Failed, result.Outcome);
        Assert.Same(expected, result.Exception);
    }

    [Fact]
    public async Task ObservationTimeoutDoesNotCancelTheSaveOperation()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new ProjectSaveParticipant();
        var operation = participant.TrackSaveAsync(async () =>
        {
            await release.Task;
            return new ProjectSaveLifecycleResult(
                new ProjectDocumentSaveReceipt("session", 1, "save.ovmachine", "hash"),
                true);
        });

        var result = await participant.ObserveAsync(TimeSpan.FromMilliseconds(10));

        Assert.Equal(ProjectSaveParticipantOutcome.TimedOut, result.Outcome);
        Assert.IsType<TimeoutException>(result.Exception);
        Assert.False(operation.IsCompleted);

        release.SetResult(true);
        await operation;
    }
}
