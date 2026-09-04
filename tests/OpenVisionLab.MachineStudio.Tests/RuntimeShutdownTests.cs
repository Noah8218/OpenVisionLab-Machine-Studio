using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
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
        Assert.Equal(RuntimeShutdownOutcome.Completed, result.Outcome);
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
}
