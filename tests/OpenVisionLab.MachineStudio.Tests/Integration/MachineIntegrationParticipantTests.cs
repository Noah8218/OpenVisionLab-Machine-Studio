using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationParticipantTests
{
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
