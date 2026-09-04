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
            diagnostics.Add);

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
