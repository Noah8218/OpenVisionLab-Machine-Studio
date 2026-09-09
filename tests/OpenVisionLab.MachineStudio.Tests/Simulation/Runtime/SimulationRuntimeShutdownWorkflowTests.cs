using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationRuntimeShutdownWorkflowTests
{
    [Fact]
    public async Task StopsAndDisposesRuntimeWithoutConstructingMainViewModel()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1
            });
        using var loop = new SimulationRuntimeLoop(
            engine,
            static action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            static () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        var workspace = new SimulationWorkspaceViewModel();
        var resources = new SimulationRuntimeResourceOwner(engine, loop, workspace);
        var runControl = CreateRunControlWorkflow(engine);
        var diagnostics = new List<SimulationRuntimeShutdownDiagnostic>();
        var workflow = new SimulationRuntimeShutdownWorkflow(
            engine,
            loop,
            resources,
            runControl,
            diagnostics.Add,
            static action =>
            {
                action();
                return Task.CompletedTask;
            });

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        var result = await workflow.ShutdownAsync(TimeSpan.FromSeconds(2));
        var repeatedResult = await workflow.ShutdownAsync(TimeSpan.FromMilliseconds(1));

        Assert.Equal(RuntimeShutdownOutcome.Completed, result.Outcome);
        Assert.Same(result, repeatedResult);
        Assert.True(resources.IsDisposed);
        Assert.Equal(
            [
                SimulationOperationalDiagnosticKind.ShutdownRequested,
                SimulationOperationalDiagnosticKind.ShutdownCompleted
            ],
            diagnostics.Select(diagnostic => diagnostic.Kind));
        Assert.Equal("ResourceDispose", diagnostics[^1].Stage);
    }

    [Fact]
    public async Task CompletesApplicationDisposeAfterRuntimeResourcesAreDisposed()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1
            });
        using var loop = new SimulationRuntimeLoop(
            engine,
            static action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            static () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        var workspace = new SimulationWorkspaceViewModel();
        var resources = new SimulationRuntimeResourceOwner(engine, loop, workspace);
        var runControl = CreateRunControlWorkflow(engine);
        var dispatchCount = 0;
        var workflow = new SimulationRuntimeShutdownWorkflow(
            engine,
            loop,
            resources,
            runControl,
            _ => { },
            action =>
            {
                Interlocked.Increment(ref dispatchCount);
                action();
                return Task.CompletedTask;
            });
        var callback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposeDispatch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        var shutdownTask = workflow.ShutdownAsync(TimeSpan.FromSeconds(2));
        var shutdownResult = await shutdownTask;
        var delayedShutdownTask = Task.Run(async () =>
        {
            await shutdownTask;
            await releaseDisposeDispatch.Task;
            return shutdownResult;
        });
        workflow.CompleteDisposeAfterShutdown(
            delayedShutdownTask,
            () => callback.SetResult(resources.IsDisposed));

        releaseDisposeDispatch.TrySetResult(true);

        Assert.Equal(RuntimeShutdownOutcome.Completed, shutdownResult.Outcome);
        Assert.True(await callback.Task);
        Assert.True(resources.IsDisposed);
        Assert.Equal(1, Volatile.Read(ref dispatchCount));
    }

    [Fact]
    public async Task MarksShutdownIncompleteWhenCanonicalJournalBudgetIsExceeded()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1,
                EventBufferCapacity = 1
            });
        using var loop = new SimulationRuntimeLoop(
            engine,
            static action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            static () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        var workspace = new SimulationWorkspaceViewModel();
        var resources = new SimulationRuntimeResourceOwner(engine, loop, workspace);
        var runControl = CreateRunControlWorkflow(engine);
        var diagnostics = new List<SimulationRuntimeShutdownDiagnostic>();
        var workflow = new SimulationRuntimeShutdownWorkflow(
            engine,
            loop,
            resources,
            runControl,
            diagnostics.Add,
            static action =>
            {
                action();
                return Task.CompletedTask;
            });

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        await WaitForAsync(() => engine.EventJournal.TotalEventCount >= 2);

        var result = await workflow.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RuntimeShutdownOutcome.Incomplete, result.Outcome);
        Assert.False(result.IsCompleted);
        Assert.NotNull(result.EventJournal);
        Assert.False(result.EventJournal!.IsComplete);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Kind == SimulationOperationalDiagnosticKind.ShutdownIncomplete);
        Assert.Equal("EventJournal", result.Stage);
        Assert.Equal("EventJournal", diagnostics[^1].Stage);
        Assert.Contains("incomplete canonical event evidence", diagnostics[^1].Message);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The simulation did not publish the expected event budget.");
    }

    private static SimulationRunControlWorkflow CreateRunControlWorkflow(
        ISimulationEngine engine) =>
        new(
            engine,
            TimeSpan.FromMilliseconds(1),
            () => new SimulationRunControlState(
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
                SimulationControlOwner.Manual,
                null,
                null),
            () => Task.FromResult(true),
            _ => { },
            _ => { },
            _ => { },
            () => { },
            _ => { },
            (_, _) => { },
            () => { });
}
