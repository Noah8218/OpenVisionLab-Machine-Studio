using System.Net;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationTcpOperationOwnerTests
{
    [Fact]
    public async Task TrackAsyncPublishesBusyStatusAndCompletion()
    {
        var busyStates = new List<bool>();
        var statuses = new List<string>();
        using var owner = CreateOwner(busyStates, statuses);

        var observation = await owner.TrackAsync("Starting", _ => Task.CompletedTask);

        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, observation.Outcome);
        Assert.Equal(new[] { true, false }, busyStates);
        Assert.Equal(new[] { "Starting" }, statuses);
    }

    [Fact]
    public async Task ConcurrentOperationsReturnIdleWithoutStartingAnotherOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationCalls = 0;
        using var owner = CreateOwner();

        var first = owner.TrackAsync(
            "First",
            async _ =>
            {
                Interlocked.Increment(ref operationCalls);
                started.TrySetResult();
                await release.Task;
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await owner.TrackAsync(
            "Second",
            _ =>
            {
                Interlocked.Increment(ref operationCalls);
                return Task.CompletedTask;
            });

        release.TrySetResult();
        var firstObservation = await first;

        Assert.Equal(MachineIntegrationParticipantOutcome.Idle, second.Outcome);
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, firstObservation.Outcome);
        Assert.Equal(1, operationCalls);
    }

    [Fact]
    public async Task CloseAdmissionCancelsTheCurrentOperationAndObservationReportsIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = CreateOwner();

        var operation = owner.TrackAsync(
            "Waiting",
            async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        owner.SetCloseAdmission(true);
        var observation = await owner.ObserveAsync(TimeSpan.FromSeconds(2));
        var completion = await operation;

        Assert.Equal(MachineIntegrationParticipantOutcome.Cancelled, observation.Outcome);
        Assert.Equal(MachineIntegrationParticipantOutcome.Cancelled, completion.Outcome);
        Assert.False(owner.IsBusy);
        Assert.True(owner.IsCloseAdmissionRequested);
    }

    [Fact]
    public async Task ObservationTimeoutDoesNotCancelTheCurrentOperation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = CreateOwner();

        var operation = owner.TrackAsync("Waiting", _ => release.Task);
        var timedOut = await owner.ObserveAsync(TimeSpan.FromMilliseconds(20));

        release.TrySetResult();
        var completed = await operation;

        Assert.Equal(MachineIntegrationParticipantOutcome.TimedOut, timedOut.Outcome);
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, completed.Outcome);
    }

    [Fact]
    public async Task DisposeSuppressesLateStatusCallbacks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<string>();
        using var owner = CreateOwner(statuses: statuses);

        var operation = owner.TrackAsync(
            "Waiting",
            async _ =>
            {
                await release.Task;
                throw new InvalidOperationException("late failure");
            });
        var statusCountBeforeDispose = statuses.Count;

        owner.Dispose();
        release.TrySetResult();
        var observation = await operation;

        Assert.Equal(MachineIntegrationParticipantOutcome.Failed, observation.Outcome);
        Assert.Equal(statusCountBeforeDispose, statuses.Count);
        Assert.True(owner.IsDisposed);
        Assert.False(owner.TryDispose());
    }

    [Fact]
    public void TcpControlDisposeIsIdempotent()
    {
        using var viewModel = new MachineIntegrationTcpControlViewModel(
            () => new MachineIntegrationTcpSettings(
                string.Empty,
                IPAddress.Loopback,
                0,
                IPAddress.Loopback.ToString(),
                45101),
            () => null,
            () => Task.CompletedTask,
            _ => { });

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.False(viewModel.IsTcpListening);
        Assert.False(viewModel.IsTcpBusy);
    }

    private static MachineIntegrationTcpOperationOwner CreateOwner(
        List<bool>? busyStates = null,
        List<string>? statuses = null) =>
        new(
            isBusy => busyStates?.Add(isBusy),
            status => statuses?.Add(status),
            () => "cancelled");
}
