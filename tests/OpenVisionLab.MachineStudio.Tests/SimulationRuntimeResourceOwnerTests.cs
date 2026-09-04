using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationRuntimeResourceOwnerTests
{
    [Fact]
    public async Task DefersDisposalUntilRuntimeCompletesAndRoutesCancellation()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(1),
                TimeScale = 1
            });
        var initialRuntimeApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new SimulationRuntimeLoop(
            engine,
            static action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            () => initialRuntimeApplied.TrySetResult(true),
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        var workspace = new SimulationWorkspaceViewModel();
        var owner = new SimulationRuntimeResourceOwner(engine, loop, workspace);

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        await initialRuntimeApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(owner.TryDisposeIfSafe());
        Assert.True(workspace.ResetScenarioCommand.CanExecute(null));

        owner.RequestCancellation();
        Assert.True(loop.CancellationToken.IsCancellationRequested);
        await engine.StopAsync();
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(owner.TryDisposeIfSafe());
        Assert.False(owner.TryDisposeIfSafe());
        Assert.True(owner.IsDisposed);
        Assert.False(workspace.ResetScenarioCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConcurrentDisposalAttemptsProduceOneOwnerAndNoDuplicateResourceDisposal()
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
            _ => { });
        var workspace = new SimulationWorkspaceViewModel();
        var owner = new SimulationRuntimeResourceOwner(engine, loop, workspace);

        loop.Start(new SimulationRuntimeConfiguration([], [], []));
        owner.RequestCancellation();
        await engine.StopAsync();
        await loop.RuntimeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.TerminationObservationTask.WaitAsync(TimeSpan.FromSeconds(2));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(owner.TryDisposeIfSafe)));

        Assert.Equal(1, results.Count(result => result));
        Assert.All(results, result => Assert.True(result || owner.IsDisposed));
    }
}
