using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationParticipantTests
{
    [Fact]
    public async Task InFlightObservationConsumesItsCompletionBeforeTheNextObservation()
    {
        using var participant = new MachineIntegrationParticipant();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(MachineIntegrationOperationKind.PublishHandoff, async () =>
        {
            await release.Task;
            return new MachineIntegrationOperationObservation(MachineIntegrationOperationKind.PublishHandoff, MachineIntegrationParticipantOutcome.Completed);
        });
        var observation = participant.ObserveAsync(TimeSpan.FromSeconds(1));
        Assert.False(observation.IsCompleted);
        release.SetResult();
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, (await observation).Outcome);
        await operation;
        Assert.Equal(MachineIntegrationParticipantOutcome.Idle, (await participant.ObserveAsync(TimeSpan.FromSeconds(1))).Outcome);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task FinishedOperationIsObservedOnceBeforeReturningIdle(string outcome)
    {
        using var participant = new MachineIntegrationParticipant();
        var expected = Enum.Parse<MachineIntegrationParticipantOutcome>(outcome);
        var failure = expected == MachineIntegrationParticipantOutcome.Failed ? new InvalidOperationException("fast failure") : null;
        var operation = participant.TrackAsync(MachineIntegrationOperationKind.PublishHandoff,
            () => Task.FromResult(new MachineIntegrationOperationObservation(MachineIntegrationOperationKind.PublishHandoff, expected, failure)));
        await operation;
        Assert.False(participant.IsBusy);
        Assert.True(participant.CanStart);

        var observed = await participant.ObserveAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(expected, observed.Outcome);
        Assert.Equal(MachineIntegrationOperationKind.PublishHandoff, observed.Kind);
        Assert.Same(failure, observed.Exception);
        Assert.Equal(MachineIntegrationParticipantOutcome.Idle, (await participant.ObserveAsync(TimeSpan.FromSeconds(1))).Outcome);
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, (await participant.TrackAsync(MachineIntegrationOperationKind.RefreshResults,
            () => Task.FromResult(new MachineIntegrationOperationObservation(MachineIntegrationOperationKind.RefreshResults, MachineIntegrationParticipantOutcome.Completed)))).Outcome);
    }

    [Fact]
    public async Task TrackAsyncSharesOneInFlightOperationAndReportsCompletion()
    {
        using var participant = new MachineIntegrationParticipant();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOperationCalls = 0;

        var first = participant.TrackAsync(
            MachineIntegrationOperationKind.PublishHandoff,
            async () =>
            {
                started.SetResult(true);
                await release.Task;
                return new MachineIntegrationOperationObservation(
                    MachineIntegrationOperationKind.PublishHandoff,
                    MachineIntegrationParticipantOutcome.Completed);
            });
        await started.Task;

        var second = participant.TrackAsync(
            MachineIntegrationOperationKind.RefreshResults,
            () =>
            {
                Interlocked.Increment(ref secondOperationCalls);
                return Task.FromResult(new MachineIntegrationOperationObservation(
                    MachineIntegrationOperationKind.RefreshResults,
                    MachineIntegrationParticipantOutcome.Completed));
            });

        Assert.Same(first, second);
        Assert.True(participant.IsBusy);
        release.SetResult(true);

        var result = await first;

        Assert.Equal(MachineIntegrationOperationKind.PublishHandoff, result.Kind);
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, result.Outcome);
        Assert.Equal(0, Volatile.Read(ref secondOperationCalls));
        Assert.False(participant.IsBusy);
    }

    [Fact]
    public async Task ObservationTimeoutDoesNotCancelFileOperation()
    {
        using var participant = new MachineIntegrationParticipant();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = participant.TrackAsync(
            MachineIntegrationOperationKind.RefreshResults,
            async () =>
            {
                await release.Task;
                return new MachineIntegrationOperationObservation(
                    MachineIntegrationOperationKind.RefreshResults,
                    MachineIntegrationParticipantOutcome.Completed);
            });

        var observed = await participant.ObserveAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(MachineIntegrationParticipantOutcome.TimedOut, observed.Outcome);
        Assert.False(operation.IsCompleted);
        release.SetResult(true);
        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            (await operation).Outcome);
    }

    [Fact]
    public async Task CloseAdmissionBlocksNewWorkAndCanBeReleased()
    {
        using var participant = new MachineIntegrationParticipant();
        participant.SetCloseAdmission(true);

        var blocked = await participant.TrackAsync(
            MachineIntegrationOperationKind.PublishHandoff,
            () => Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Completed)));

        Assert.False(participant.CanStart);
        Assert.Equal(MachineIntegrationParticipantOutcome.Idle, blocked.Outcome);

        participant.SetCloseAdmission(false);
        var reopened = await participant.TrackAsync(
            MachineIntegrationOperationKind.PublishHandoff,
            () => Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Completed)));

        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, reopened.Outcome);
    }

    [Fact]
    public async Task UnexpectedOperationFailureIsReportedAsTypedFailure()
    {
        using var participant = new MachineIntegrationParticipant();
        var expected = new InvalidOperationException("integration failed");

        var operation = participant.TrackAsync(
            MachineIntegrationOperationKind.PublishHandoff,
            () => Task.FromException<MachineIntegrationOperationObservation>(expected));

        var result = await operation;

        Assert.Equal(MachineIntegrationParticipantOutcome.Failed, result.Outcome);
        Assert.Same(expected, result.Exception);
    }

    [Fact]
    public async Task DisposeRejectsNewWorkAndReturnsIdleObservation()
    {
        using var participant = new MachineIntegrationParticipant();
        participant.Dispose();

        var result = await participant.TrackAsync(
            MachineIntegrationOperationKind.RefreshResults,
            () => Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.RefreshResults,
                MachineIntegrationParticipantOutcome.Completed)));

        Assert.Equal(MachineIntegrationParticipantOutcome.Idle, result.Outcome);
        Assert.False(participant.CanStart);
    }
}
