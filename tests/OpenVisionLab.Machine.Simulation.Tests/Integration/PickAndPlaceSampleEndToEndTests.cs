using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class PickAndPlaceSampleEndToEndTests
{
    private const int MaximumStepCount = 2_000;
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task PersistedSample_CompletesDeterministicPickPlaceAndResetAcrossRuns()
    {
        PickPlaceRun first = await RunAsync();
        PickPlaceRun second = await RunAsync();

        Assert.True(first.SawPickHeld);
        Assert.True(first.SawPlaceHeld);
        Assert.True(first.SawPlaceReleased);
        Assert.InRange(first.StepsExecuted, 1, MaximumStepCount - 1);
        Assert.Equal(first.FinalState, second.FinalState);
        Assert.Equal(first.OrderedEvents, second.OrderedEvents);
    }

    private static async Task<PickPlaceRun> RunAsync()
    {
        string samplePath = Path.Combine(AppContext.BaseDirectory, "sample-pick-and-place.ovmachine");
        MachineProjectDocument project = new ProjectDocumentStore().Load(File.ReadAllText(samplePath));
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);
        Assert.Null(runtime.AutomaticRun);

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001,
            Seed = project.Simulation.Seed
        });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(runtime))).IsAccepted);

        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("main"))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PlayCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        SimulationSnapshot paused = engine.CurrentSnapshot;
        PickPlaceWorkpieceSnapshot pausedWorkpiece = Assert.Single(paused.Workpieces);
        await Task.Delay(25);
        Assert.Equal(paused.TickIndex, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(pausedWorkpiece, Assert.Single(engine.CurrentSnapshot.Workpieces));
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Equal(paused.TickIndex + 1, engine.CurrentSnapshot.TickIndex);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        AssertReset(engine.CurrentSnapshot);

        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("main"))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PlayCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        var sawPickHeld = false;
        var sawPlaceHeld = false;
        var sawPlaceReleased = false;
        var stepsExecuted = 0;
        while (stepsExecuted < MaximumStepCount
               && Assert.Single(engine.CurrentSnapshot.Sequences).Status != SequenceExecutionStatus.Completed)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            stepsExecuted++;
            SimulationSnapshot snapshot = engine.CurrentSnapshot;
            var x = AxisPosition(snapshot, "x");
            var y = AxisPosition(snapshot, "y");
            var gripper = Signal(snapshot, "do.gripper");
            PickPlaceWorkpieceSnapshot workpiece = Assert.Single(snapshot.Workpieces);
            sawPickHeld |= gripper && Near(x, 240) && Near(y, 120)
                && workpiece.State == PickPlaceWorkpieceState.Attached
                && Near(workpiece.X, 240) && Near(workpiece.Y, 120);
            sawPlaceHeld |= gripper && Near(x, 400) && Near(y, 240)
                && workpiece.State == PickPlaceWorkpieceState.Attached
                && Near(workpiece.X, 400) && Near(workpiece.Y, 240);
            sawPlaceReleased |= !gripper && Near(x, 400) && Near(y, 240)
                && workpiece.State == PickPlaceWorkpieceState.Placed
                && Near(workpiece.X, 400) && Near(workpiece.Y, 240);
        }

        SimulationSnapshot completed = engine.CurrentSnapshot;
        Assert.True(stepsExecuted < MaximumStepCount, "Pick-and-Place exceeded its deterministic step budget.");
        Assert.Equal(SequenceExecutionStatus.Completed, Assert.Single(completed.Sequences).Status);
        Assert.True(Near(AxisPosition(completed, "x"), 0));
        Assert.True(Near(AxisPosition(completed, "y"), 0));
        Assert.False(Signal(completed, "do.gripper"));
        PickPlaceWorkpieceSnapshot completedWorkpiece = Assert.Single(completed.Workpieces);
        Assert.Equal(PickPlaceWorkpieceState.Placed, completedWorkpiece.State);
        Assert.True(Near(completedWorkpiece.X, 400));
        Assert.True(Near(completedWorkpiece.Y, 240));

        string finalState = string.Join(
            "|",
            completed.TickIndex,
            string.Join(",", completed.Axes.Select(axis => $"{axis.Id}:{axis.Position:F3}:{axis.State}")),
            $"gripper:{Signal(completed, "do.gripper")}",
            $"workpiece:{completedWorkpiece.State}:{completedWorkpiece.X:F3}:{completedWorkpiece.Y:F3}",
            $"sequence:{Assert.Single(completed.Sequences).Status}");

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        Assert.Equal(2, events.Count(item => item.Code == "SequenceStarted"));
        Assert.Single(events, item => item.Code == "SequenceCompleted");
        Assert.Single(events, item => item.Code == "WorkpieceAttached");
        Assert.Single(events, item => item.Code == "WorkpiecePlaced");
        string[] orderedEvents = events
            .Where(item => item.Category is "Sequence" or "Workpiece")
            .Select(item => $"{item.TickIndex}|{item.Category}|{item.Code}|{item.Message}")
            .ToArray();

        return new PickPlaceRun(
            sawPickHeld,
            sawPlaceHeld,
            sawPlaceReleased,
            stepsExecuted,
            finalState,
            orderedEvents);
    }

    private static void AssertReset(SimulationSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.TickIndex);
        Assert.Equal(SequenceExecutionStatus.Ready, Assert.Single(snapshot.Sequences).Status);
        Assert.True(snapshot.Axes.All(axis => Near(axis.Position, 0)));
        Assert.False(Signal(snapshot, "di.start"));
        Assert.False(Signal(snapshot, "do.gripper"));
        PickPlaceWorkpieceSnapshot workpiece = Assert.Single(snapshot.Workpieces);
        Assert.Equal(PickPlaceWorkpieceState.Available, workpiece.State);
        Assert.True(Near(workpiece.X, 240));
        Assert.True(Near(workpiece.Y, 120));
    }

    private static double AxisPosition(SimulationSnapshot snapshot, string axisId) =>
        Assert.Single(snapshot.Axes, axis => axis.Id == axisId).Position;

    private static bool Signal(SimulationSnapshot snapshot, string signalId) =>
        Assert.Single(snapshot.Signals, signal => signal.Id == signalId).Value;

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static async Task<IReadOnlyList<SimulationEvent>> ReadAllEventsAsync(
        FixedStepSimulationEngine engine)
    {
        var events = new List<SimulationEvent>();
        await foreach (SimulationEvent item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }

        return events;
    }

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(Environment.NewLine, compilation.Errors.Select(error =>
            $"{error.Code} [{error.TargetId}]: {error.Message}"));

    private sealed record PickPlaceRun(
        bool SawPickHeld,
        bool SawPlaceHeld,
        bool SawPlaceReleased,
        int StepsExecuted,
        string FinalState,
        IReadOnlyList<string> OrderedEvents);
}
