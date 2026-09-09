using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioBatchParticipantTests
{
    [Fact]
    public async Task TracksCompletionAndReturnsTheTypedBatchResult()
    {
        using var participant = new SimulationScenarioBatchParticipant();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new SimulationScenarioBatchParticipantResult(
            SimulationScenarioBatchParticipantOutcome.Completed);

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
    public async Task RepeatedTrackCallsShareTheInFlightBatch()
    {
        using var participant = new SimulationScenarioBatchParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new SimulationScenarioBatchParticipantResult(
            SimulationScenarioBatchParticipantOutcome.Completed);

        var first = participant.TrackAsync(async _ =>
        {
            await release.Task;
            return expected;
        });
        var second = participant.TrackAsync(_ => Task.FromResult(
            new SimulationScenarioBatchParticipantResult(
                SimulationScenarioBatchParticipantOutcome.Failed)));

        Assert.Same(first, second);
        release.SetResult(true);
        Assert.Same(expected, await second);
    }

    [Fact]
    public async Task CancelRequestsTheTrackedBatchAndIsTypedForClose()
    {
        using var participant = new SimulationScenarioBatchParticipant();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(async cancellationToken =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SimulationScenarioBatchParticipantResult(
                SimulationScenarioBatchParticipantOutcome.Completed);
        });
        await started.Task;

        participant.Cancel();

        var observation = await participant.ObserveAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(
            SimulationScenarioBatchParticipantOutcome.Cancelled,
            observation.Outcome);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
    }

    [Fact]
    public async Task ObservationTimeoutDoesNotCancelTheBatch()
    {
        using var participant = new SimulationScenarioBatchParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(async _ =>
        {
            await release.Task;
            return new SimulationScenarioBatchParticipantResult(
                SimulationScenarioBatchParticipantOutcome.Completed);
        });

        var observation = await participant.ObserveAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            SimulationScenarioBatchParticipantOutcome.TimedOut,
            observation.Outcome);
        Assert.IsType<TimeoutException>(observation.Exception);
        Assert.False(operation.IsCompleted);

        release.SetResult(true);
        await operation;
    }

    [Fact]
    public async Task OperationFailureIsObservedWithoutChangingTheCallerException()
    {
        using var participant = new SimulationScenarioBatchParticipant();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("scenario batch failed");
        var operation = participant.TrackAsync(async _ =>
        {
            await release.Task;
            throw expected;
        });
        var observationTask = participant.ObserveAsync(TimeSpan.FromSeconds(1));

        release.SetResult(true);

        var observation = await observationTask;
        Assert.Equal(
            SimulationScenarioBatchParticipantOutcome.Failed,
            observation.Outcome);
        Assert.Same(expected, observation.Exception);
        var callerException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation);
        Assert.Same(expected, callerException);
    }
}
