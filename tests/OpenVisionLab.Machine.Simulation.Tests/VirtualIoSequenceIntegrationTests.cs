using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class VirtualIoSequenceIntegrationTests
{
    [Fact]
    public async Task CycleStart_CompletesAxisAndDigitalOutputFlow()
    {
        using var engine = await CreateConfiguredEngineAsync();
        var start = await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        var input = await engine.EnqueueCommandAsync(new SetVirtualInputCommand("di.cycle-start", true));

        Assert.True(start.IsAccepted);
        Assert.True(input.IsAccepted);
        Assert.Equal(TimeSpan.Zero, engine.CurrentSnapshot.SimulationTime);
        Assert.Equal("wait-cycle-start", Assert.Single(engine.CurrentSnapshot.Sequences).CurrentStepId);

        var completed = await StepUntilCompletedAsync(engine);

        Assert.Equal(SimulationControlOwner.EmbeddedSequence, completed.ControlOwner);
        Assert.Equal(100, Assert.Single(completed.Axes).Position, 6);
        Assert.Equal(AxisState.Idle, completed.Axes[0].State);
        Assert.True(Signal(completed, "di.cycle-start"));
        Assert.False(Signal(completed, "do.cycle-active"));
        Assert.True(Signal(completed, "do.cycle-done"));
        Assert.Equal(SequenceExecutionStatus.Completed, Assert.Single(completed.Sequences).Status);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item => item.Code == "DigitalInputChanged");
        Assert.Equal(3, events.Count(item => item.Code == "DigitalOutputChanged"));
        Assert.Contains(events, item => item.Code == "SequenceAxisMoveAccepted");
        Assert.Contains(events, item => item.Code == "AxisTargetReached");
        Assert.Single(events, item => item.Code == "SequenceCompleted");
        Assert.True(events.Zip(events.Skip(1), (left, right) => left.EventIndex < right.EventIndex).All(value => value));
    }

    [Fact]
    public async Task PauseStepAndReset_ApplyToAxisIoSequenceAndClockAtomically()
    {
        using var engine = await CreateConfiguredEngineAsync();
        await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        await engine.EnqueueCommandAsync(new SetVirtualInputCommand("di.cycle-start", true));

        var paused = engine.CurrentSnapshot;
        await Task.Delay(30);
        Assert.Equal(paused.TickIndex, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(paused.SimulationTime, engine.CurrentSnapshot.SimulationTime);

        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal(1, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(5), engine.CurrentSnapshot.SimulationTime);
        Assert.Equal("active-on", Assert.Single(engine.CurrentSnapshot.Sequences).CurrentStepId);

        await engine.EnqueueCommandAsync(new PauseCommand());
        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal(2, engine.CurrentSnapshot.TickIndex);
        Assert.True(Signal(engine.CurrentSnapshot, "do.cycle-active"));

        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        var snapshot = engine.CurrentSnapshot;

        Assert.True(reset.IsAccepted);
        Assert.Equal(2, reset.AppliedTick);
        Assert.Equal(0, snapshot.TickIndex);
        Assert.Equal(TimeSpan.Zero, snapshot.SimulationTime);
        Assert.Equal(0, Assert.Single(snapshot.Axes).Position);
        Assert.All(snapshot.Signals, signal => Assert.False(signal.Value));
        Assert.Equal(SequenceExecutionStatus.Ready, Assert.Single(snapshot.Sequences).Status);
        Assert.Equal(SimulationRunMode.Paused, snapshot.RunMode);
    }

    [Fact]
    public async Task SameStepStream_ProducesIdenticalSnapshotAndNonDroppingEventTrace()
    {
        var first = await RunDeterministicCycleAsync();
        var second = await RunDeterministicCycleAsync();

        Assert.Equal(first.Snapshot.TickIndex, second.Snapshot.TickIndex);
        Assert.Equal(first.Snapshot.SimulationTime, second.Snapshot.SimulationTime);
        Assert.Equal(first.Snapshot.Axes[0].Position, second.Snapshot.Axes[0].Position, 9);
        Assert.Equal(
            first.Snapshot.Signals.Select(signal => (signal.Id, signal.Value)),
            second.Snapshot.Signals.Select(signal => (signal.Id, signal.Value)));
        Assert.Equal(first.NormalizedEvents, second.NormalizedEvents);
        Assert.True(first.NormalizedEvents.Count > 100);
    }

    [Fact]
    public async Task ManualOutputAndSequenceOwnedManualMove_AreRejected()
    {
        using var engine = await CreateConfiguredEngineAsync();
        var outputWrite = await engine.EnqueueCommandAsync(
            new SetVirtualInputCommand("do.cycle-active", true));
        await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        var move = await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 100));

        Assert.False(outputWrite.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.SignalWriteRejected, outputWrite.ErrorCode);
        Assert.False(move.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ControlOwnerNotAllowed, move.ErrorCode);
        Assert.Equal(0, engine.CurrentSnapshot.Axes[0].Position);
    }

    [Fact]
    public async Task ManualInputForce_PersistsAcrossPauseAndStep_AndClearsOnCommandAndReset()
    {
        using var engine = await CreateConfiguredEngineAsync();
        var manual = await engine.EnqueueCommandAsync(new StartManualControlCommand());
        var forceOn = await engine.EnqueueCommandAsync(
            new SetVirtualInputForceCommand("di.cycle-start", true));

        Assert.True(manual.IsAccepted, manual.Detail);
        Assert.True(forceOn.IsAccepted, forceOn.Detail);
        var forced = SignalSnapshot(engine.CurrentSnapshot, "di.cycle-start");
        Assert.True(forced.Value);
        Assert.False(forced.NominalValue);
        Assert.True(forced.OverrideValue);
        var forcedRevision = engine.CurrentSnapshot.SignalRevision;

        await engine.EnqueueCommandAsync(new PauseCommand());
        await engine.EnqueueCommandAsync(new StepCommand());
        var stepped = SignalSnapshot(engine.CurrentSnapshot, "di.cycle-start");
        Assert.True(stepped.Value);
        Assert.True(stepped.OverrideValue);
        Assert.Equal(forcedRevision, engine.CurrentSnapshot.SignalRevision);

        var clear = await engine.EnqueueCommandAsync(
            new SetVirtualInputForceCommand("di.cycle-start", null));
        Assert.True(clear.IsAccepted, clear.Detail);
        var cleared = SignalSnapshot(engine.CurrentSnapshot, "di.cycle-start");
        Assert.False(cleared.Value);
        Assert.False(cleared.NominalValue);
        Assert.Null(cleared.OverrideValue);

        await engine.EnqueueCommandAsync(
            new SetVirtualInputForceCommand("di.cycle-start", false));
        await engine.EnqueueCommandAsync(new ResetCommand());
        var reset = SignalSnapshot(engine.CurrentSnapshot, "di.cycle-start");
        Assert.False(reset.Value);
        Assert.False(reset.NominalValue);
        Assert.Null(reset.OverrideValue);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item => item.Code == "DigitalInputForceOnAccepted");
        Assert.Contains(events, item => item.Code == "DigitalInputForceOffAccepted");
        Assert.Contains(events, item => item.Code == "DigitalInputForceCleared");
        Assert.True(events.Zip(events.Skip(1), (left, right) => left.EventIndex < right.EventIndex).All(value => value));
    }

    private static async Task<FixedStepSimulationEngine> CreateConfiguredEngineAsync()
    {
        var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        var result = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntimeConfiguration()));
        Assert.True(result.IsAccepted, result.Detail);
        return engine;
    }

    private static SimulationRuntimeConfiguration CreateRuntimeConfiguration()
    {
        var channels = new[]
        {
            Channel("di.cycle-start", ChannelKind.DigitalInput),
            Channel("do.cycle-active", ChannelKind.DigitalOutput),
            Channel("do.cycle-done", ChannelKind.DigitalOutput)
        };
        var definition = new SequenceDefinition
        {
            Id = "inspection-cycle",
            Name = "Inspection Cycle",
            Steps =
            {
                Step("wait-cycle-start", SequenceStepAction.WaitSignal, "di.cycle-start", "true", "active-on"),
                Step("active-on", SequenceStepAction.SetSignal, "do.cycle-active", "true", "move-inspection"),
                Step("move-inspection", SequenceStepAction.MoveAxis, "x", "100", "wait-axis-done"),
                new SequenceStepDefinition
                {
                    Id = "wait-axis-done",
                    Name = "Wait Axis Done",
                    Action = SequenceStepAction.WaitAxisDone,
                    TargetId = "x",
                    TimeoutMs = 2000,
                    NextStepId = "active-off"
                },
                Step("active-off", SequenceStepAction.SetSignal, "do.cycle-active", "false", "done-on"),
                Step("done-on", SequenceStepAction.SetSignal, "do.cycle-done", "true", "complete"),
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var targets = new SequenceCompilationTargets(
            channels.ToDictionary(channel => channel.Id, channel => channel.Kind, StringComparer.Ordinal),
            new[] { "x" });
        var compilation = new SequenceCompiler().Compile(definition, targets);
        Assert.True(compilation.IsSuccess);

        return new SimulationRuntimeConfiguration(
            new[] { CreateAxisConfig() },
            channels,
            new[] { compilation.Sequence! });
    }

    private static async Task<SimulationSnapshot> StepUntilCompletedAsync(
        FixedStepSimulationEngine engine)
    {
        for (var index = 0; index < 450; index++)
        {
            var step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, step.Detail);
            var snapshot = engine.CurrentSnapshot;
            if (snapshot.Sequences.Single().Status == SequenceExecutionStatus.Completed)
            {
                return snapshot;
            }
        }

        throw new TimeoutException("The inspection sequence did not complete within 450 fixed ticks.");
    }

    private static async Task<(SimulationSnapshot Snapshot, IReadOnlyList<string> NormalizedEvents)>
        RunDeterministicCycleAsync()
    {
        using var engine = await CreateConfiguredEngineAsync();
        await engine.EnqueueCommandAsync(new StartSequenceCommand("inspection-cycle"));
        await engine.EnqueueCommandAsync(new SetVirtualInputCommand("di.cycle-start", true));
        var snapshot = await StepUntilCompletedAsync(engine);
        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var normalized = events
            .Select(item =>
                $"{item.EventIndex}|{item.TickIndex}|{item.SimulationTime.Ticks}|" +
                $"{item.Category}|{item.Code}|{item.Message}")
            .ToArray();
        return (snapshot, normalized);
    }

    private static async Task<IReadOnlyList<SimulationEvent>> ReadAllEventsAsync(
        FixedStepSimulationEngine engine)
    {
        var events = new List<SimulationEvent>();
        await foreach (var item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }
        return events;
    }

    private static bool Signal(SimulationSnapshot snapshot, string id) =>
        snapshot.Signals.Single(signal => signal.Id == id).Value;

    private static OpenVisionLab.Machine.IO.Channels.DigitalSignalSnapshot SignalSnapshot(
        SimulationSnapshot snapshot,
        string id) =>
        snapshot.Signals.Single(signal => signal.Id == id);

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new() { Id = id, Name = id, Kind = kind, InitialValue = 0 };

    private static AxisConfiguration CreateAxisConfig() =>
        new()
        {
            Id = "x",
            Name = "Inspection X Axis",
            MinimumPosition = 0,
            MaximumPosition = 300,
            HomePosition = 0,
            MaximumVelocity = 200,
            Acceleration = 500,
            Deceleration = 500
        };

    private static SequenceStepDefinition Step(
        string id,
        SequenceStepAction action,
        string target,
        string parameter,
        string? next = null) =>
        new()
        {
            Id = id,
            Name = id,
            Action = action,
            TargetId = target,
            Parameter = parameter,
            NextStepId = next
        };
}
