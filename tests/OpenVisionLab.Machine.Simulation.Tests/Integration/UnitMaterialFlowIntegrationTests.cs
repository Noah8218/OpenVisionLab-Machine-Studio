using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Workpieces;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class UnitMaterialFlowIntegrationTests
{
    [Fact]
    public async Task TotalSupplyStopsAtThreeAndDrainsAcrossPauseResumeAndReset()
    {
        var supply = Compile("a", 4, Step("feed", SequenceStepAction.FeedUnitWorkpiece, "a"),
            Step("capture", SequenceStepAction.TriggerCamera, "camera", "inspection"), Step("inspect", SequenceStepAction.WaitVisionResult, "camera"),
            Step("transfer", SequenceStepAction.TransferUnitWorkpiece, "cv.a", ".05", "b"));
        var exit = Compile("b", 4, Step("receive", SequenceStepAction.ReceiveUnitWorkpiece, "b"),
            Step("delay", SequenceStepAction.Delay, "", ".04"), Step("eject", SequenceStepAction.EjectUnitWorkpiece, "b", "auto"));
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(10) });
        await engine.StartAsync();
        var configuration = new SimulationRuntimeConfiguration([], [], [supply, exit],
            [new VirtualCameraConfiguration("camera", "camera", 1, 1, PlaceholderInspectionDecision.Pass)], null, null, null,
            unitMaterialFlow: UnitMaterialFlowRuntimeTests.Configuration() with { TotalFeedCount = 3 });
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal(1, engine.CurrentSnapshot.MaterialSupply!.FedCount);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        Assert.Equal(1, engine.CurrentSnapshot.MaterialSupply.FedCount);
        for (int tick = 0; tick < 200 && engine.CurrentSnapshot.Sequences.Any(sequence => sequence.Status == SequenceExecutionStatus.Running); tick++)
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.All(engine.CurrentSnapshot.Sequences, sequence => Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status));
        Assert.Equal(new UnitMaterialSupplySnapshot(3, 3, 3, false), engine.CurrentSnapshot.MaterialSupply);
        Assert.Equal(3, engine.CurrentSnapshot.UnitMaterialExits.Count);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(0, engine.CurrentSnapshot.MaterialSupply.FedCount);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal(1, engine.CurrentSnapshot.MaterialSupply.FedCount);
        await engine.StopAsync();
    }

    [Fact]
    public async Task CompletedStepsWithAnUnejectedProductFaultInsteadOfReportingNormalCompletion()
    {
        var supply = Compile("a", 1, Step("feed", SequenceStepAction.FeedUnitWorkpiece, "a"));
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(10) });
        await engine.StartAsync();
        var configuration = new SimulationRuntimeConfiguration([], [], [supply], [], null, null, null,
            unitMaterialFlow: UnitMaterialFlowRuntimeTests.Configuration() with { TotalFeedCount = 1 });
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        for (int tick = 0; tick < 3; tick++) await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal(SequenceExecutionStatus.Faulted, Assert.Single(engine.CurrentSnapshot.Sequences).Status);
        Assert.True(engine.CurrentSnapshot.MaterialSupply!.HasInFlight);
        Assert.Empty(engine.CurrentSnapshot.UnitMaterialExits);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.False(engine.CurrentSnapshot.MaterialSupply.HasInFlight);
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(PlaceholderInspectionDecision.Pass, "OK", "straight")]
    [InlineData(PlaceholderInspectionDecision.Fail, "NG", "side")]
    public async Task TwoUnitsRepeatThreeTimesWithoutLosingIdentityOrResults(PlaceholderInspectionDecision decision, string verdict, string route)
    {
        var supply = Compile("a", 3,
            Step("feed", SequenceStepAction.FeedUnitWorkpiece, "a"),
            Step("capture", SequenceStepAction.TriggerCamera, "camera", "inspection"),
            Step("inspect", SequenceStepAction.WaitVisionResult, "camera"),
            Step("transfer", SequenceStepAction.TransferUnitWorkpiece, "cv.a", ".05", "b"));
        var exit = Compile("b", 3,
            Step("receive", SequenceStepAction.ReceiveUnitWorkpiece, "b"),
            Step("delay", SequenceStepAction.Delay, "", ".04"),
            Step("eject", SequenceStepAction.EjectUnitWorkpiece, "b", "auto"));
        var settings = new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(10) };
        using var engine = new FixedStepSimulationEngine(settings);
        await engine.StartAsync();
        var configuration = new SimulationRuntimeConfiguration([], [], [supply, exit],
            [new VirtualCameraConfiguration("camera", "camera", 1, 1, decision)], null, null, null,
            unitMaterialFlow: UnitMaterialFlowRuntimeTests.Configuration());
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        bool sawReservation = false;
        for (int tick = 0; tick < 200; tick++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            var snapshot = engine.CurrentSnapshot;
            sawReservation |= snapshot.UnitMaterialSlots.Any(slot => slot.State == "Reserved");
            Assert.DoesNotContain(snapshot.Sequences, sequence => sequence.Status == SequenceExecutionStatus.Faulted);
            if (snapshot.Sequences.All(sequence => sequence.Status == SequenceExecutionStatus.Completed)) break;
        }
        var final = engine.CurrentSnapshot;
        Assert.True(sawReservation);
        Assert.All(final.Sequences, sequence => { Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status); Assert.Equal(3, sequence.CompletedCycles); });
        Assert.Equal(3, final.UnitMaterialExits.Count);
        Assert.Equal(3, final.UnitMaterialExits.Select(item => item.InstanceId).Distinct().Count());
        Assert.All(final.UnitMaterialExits, item => { Assert.Equal(verdict, item.Verdict); Assert.Equal(route, item.Route); });
        Assert.All(final.UnitMaterialSlots, slot => Assert.Equal("Empty", slot.State));
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Empty(engine.CurrentSnapshot.UnitMaterialExits);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        await engine.StopAsync();
    }

    [Fact]
    public async Task CompletedSupplyCannotRestartWhileTheOtherUnitWaitsAndAbortResetRecover()
    {
        var supply = Compile("a", 1,
            Step("feed", SequenceStepAction.FeedUnitWorkpiece, "a"),
            Step("capture", SequenceStepAction.TriggerCamera, "camera", "inspection"),
            Step("inspect", SequenceStepAction.WaitVisionResult, "camera"),
            Step("transfer", SequenceStepAction.TransferUnitWorkpiece, "cv.a", ".05", "b"));
        var exit = Compile("b", 1,
            Step("receive", SequenceStepAction.ReceiveUnitWorkpiece, "b"),
            Step("delay", SequenceStepAction.Delay, "", "1.5"),
            Step("eject", SequenceStepAction.EjectUnitWorkpiece, "b", "auto"));
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(10) });
        await engine.StartAsync();
        var configuration = new SimulationRuntimeConfiguration([], [], [supply, exit],
            [new VirtualCameraConfiguration("camera", "camera", 1, 1, PlaceholderInspectionDecision.Pass)], null, null, null,
            unitMaterialFlow: UnitMaterialFlowRuntimeTests.Configuration());
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        for (int tick = 0; tick < 50 && engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == supply.Id).Status != SequenceExecutionStatus.Completed; tick++)
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Equal(SequenceExecutionStatus.Completed, engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == supply.Id).Status);
        Assert.Equal(SequenceExecutionStatus.Running, engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == exit.Id).Status);
        Assert.False((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        Assert.False((await engine.EnqueueCommandAsync(new StartAutomaticRunCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var pausedTime = engine.CurrentSnapshot.SimulationTime;
        await Task.Delay(40);
        Assert.Equal(pausedTime, engine.CurrentSnapshot.SimulationTime);
        Assert.Empty(engine.CurrentSnapshot.UnitMaterialExits);
        Assert.True((await engine.EnqueueCommandAsync(new AbortSequenceCommand(supply.Id))).IsAccepted);
        Assert.DoesNotContain(engine.CurrentSnapshot.Sequences, sequence => sequence.Status == SequenceExecutionStatus.Running);
        Assert.DoesNotContain(engine.CurrentSnapshot.UnitMaterialSlots, slot => slot.State == "Reserved");
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.All(engine.CurrentSnapshot.UnitMaterialSlots, slot => Assert.Equal("Empty", slot.State));
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        for (int tick = 0; tick < 200 && !engine.CurrentSnapshot.Sequences.All(sequence => sequence.Status == SequenceExecutionStatus.Completed); tick++)
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.All(engine.CurrentSnapshot.Sequences, sequence => Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status));
        Assert.Single(engine.CurrentSnapshot.UnitMaterialExits);
        await engine.StopAsync();
    }

    [Fact]
    public async Task ReceiveTimeoutStopsAllUnitsAndResetAllowsAValidNewRun()
    {
        var wait = Step("receive", SequenceStepAction.ReceiveUnitWorkpiece, "b");
        wait.TimeoutMs = 20;
        var supply = Compile("a", 1, Step("delay", SequenceStepAction.Delay, "", "1.5"));
        var exit = Compile("b", 1, wait);
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(10) });
        await engine.StartAsync();
        var configuration = new SimulationRuntimeConfiguration([], [], [supply, exit], [], null, null, null,
            unitMaterialFlow: UnitMaterialFlowRuntimeTests.Configuration());
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        for (int tick = 0; tick < 5; tick++) await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Contains(engine.CurrentSnapshot.Sequences, sequence => sequence.SequenceId == exit.Id && sequence.Status == SequenceExecutionStatus.Faulted);
        Assert.DoesNotContain(engine.CurrentSnapshot.Sequences, sequence => sequence.Status == SequenceExecutionStatus.Running);
        Assert.Empty(engine.CurrentSnapshot.UnitMaterialExits);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.All(engine.CurrentSnapshot.Sequences, sequence => Assert.Equal(SequenceExecutionStatus.Ready, sequence.Status));
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(supply.Id))).IsAccepted);
        await engine.StopAsync();
    }

    private static SequenceStepDefinition Step(string id, SequenceStepAction action, string target, string parameter = "", string? destination = null) =>
        new() { Id = id, Name = id, Action = action, TargetId = target, Parameter = parameter, DestinationUnitId = destination,
            TimeoutMs = action is SequenceStepAction.FeedUnitWorkpiece or SequenceStepAction.ReceiveUnitWorkpiece or SequenceStepAction.TransferUnitWorkpiece or SequenceStepAction.WaitVisionResult ? 1000 : 0 };

    private static CompiledSequence Compile(string unit, int repeat, params SequenceStepDefinition[] steps)
    {
        var definition = new SequenceDefinition { Id = "sequence." + unit, UnitId = unit, WatchdogTimeoutMs = 0 };
        definition.Steps.AddRange(steps);
        definition.Steps.Add(Step("done", SequenceStepAction.Complete, ""));
        for (int index = 0; index < steps.Length; index++)
        {
            steps[index].NextStepId = definition.Steps[index + 1].Id;
            if (steps[index].Action == SequenceStepAction.WaitVisionResult) steps[index].FailureStepId = steps[index].NextStepId;
        }
        var compiled = new SequenceCompiler().Compile(definition, repeatCount: repeat);
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Errors.Select(error => error.Message)));
        return compiled.Sequence!;
    }
}
