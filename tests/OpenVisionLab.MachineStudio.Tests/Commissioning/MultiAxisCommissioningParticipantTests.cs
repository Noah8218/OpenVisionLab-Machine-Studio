using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MultiAxisCommissioningParticipantTests
{
    [Fact]
    public async Task TracksCompletionAndReturnsTheTypedValidationResult()
    {
        using var participant = new MultiAxisCommissioningParticipant();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new MultiAxisCommissioningParticipantResult(
            MultiAxisCommissioningParticipantOutcome.Completed);

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

        Assert.Same(expected, await operation);
        Assert.Same(expected, await observationTask);
    }

    [Fact]
    public async Task RepeatedTrackCallsShareTheInFlightValidation()
    {
        using var participant = new MultiAxisCommissioningParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new MultiAxisCommissioningParticipantResult(
            MultiAxisCommissioningParticipantOutcome.Completed);

        var first = participant.TrackAsync(async _ =>
        {
            await release.Task;
            return expected;
        });
        var second = participant.TrackAsync(_ => Task.FromResult(
            new MultiAxisCommissioningParticipantResult(
                MultiAxisCommissioningParticipantOutcome.Failed)));

        Assert.Same(first, second);
        release.SetResult(true);
        Assert.Same(expected, await second);
    }

    [Fact]
    public async Task CancelRequestsTheTrackedValidationAndIsTypedForClose()
    {
        using var participant = new MultiAxisCommissioningParticipant();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(async cancellationToken =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new MultiAxisCommissioningParticipantResult(
                MultiAxisCommissioningParticipantOutcome.Completed);
        });
        await started.Task;

        participant.Cancel();

        var observation = await participant.ObserveAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(
            MultiAxisCommissioningParticipantOutcome.Cancelled,
            observation.Outcome);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
    }

    [Fact]
    public async Task ObservationTimeoutDoesNotCancelTheValidation()
    {
        using var participant = new MultiAxisCommissioningParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(async _ =>
        {
            await release.Task;
            return new MultiAxisCommissioningParticipantResult(
                MultiAxisCommissioningParticipantOutcome.Completed);
        });

        var observation = await participant.ObserveAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            MultiAxisCommissioningParticipantOutcome.TimedOut,
            observation.Outcome);
        Assert.IsType<TimeoutException>(observation.Exception);
        Assert.False(operation.IsCompleted);

        release.SetResult(true);
        await operation;
    }

    [Fact]
    public async Task OperationFailureIsObservedWithoutChangingTheCallerException()
    {
        using var participant = new MultiAxisCommissioningParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("commissioning validation failed");
        var operation = participant.TrackAsync(async _ =>
        {
            await release.Task;
            throw expected;
        });
        var observationTask = participant.ObserveAsync(TimeSpan.FromSeconds(1));

        release.SetResult(true);

        var observation = await observationTask;
        Assert.Equal(
            MultiAxisCommissioningParticipantOutcome.Failed,
            observation.Outcome);
        Assert.Same(expected, observation.Exception);
        var callerException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation);
        Assert.Same(expected, callerException);
    }
}
