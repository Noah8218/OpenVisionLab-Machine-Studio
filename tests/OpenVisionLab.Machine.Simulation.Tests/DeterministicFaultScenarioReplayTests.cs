using System.IO;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Analysis;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.FaultScenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicFaultScenarioReplayTests
{
    [Fact]
    public async Task ReplayScenario_FromPersistedJson_IsDeterministicBetweenRuns()
    {
        var scenario = new DeterministicFaultScenarioProfile(
            SchemaVersion: 1,
            ScenarioId: "deterministic-fixture",
            Name: "Deterministic input and cylinder fixture",
            Description: "Replayable fault timeline for deterministic CI.",
            DurationTicks: 12,
            Actions:
            [
                new DeterministicFaultScenarioAction(
                    Tick: 1,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.sensor",
                    ForcedValue: false),
                new DeterministicFaultScenarioAction(
                    Tick: 3,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.sensor"),
                new DeterministicFaultScenarioAction(
                    Tick: 5,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.CylinderTravelBlocked,
                    TargetId: "cylinder-1"),
                new DeterministicFaultScenarioAction(
                    Tick: 8,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.CylinderTravelBlocked,
                    TargetId: "cylinder-1")
            ]);

        var tempPath = Path.GetTempFileName();
        var persisted = DeterministicFaultScenarioProfile.SaveToJson(scenario);
        await File.WriteAllTextAsync(tempPath, persisted);
        try
        {
            var loaded = DeterministicFaultScenarioProfile.LoadFromJson(tempPath);
            Assert.NotNull(loaded);
            var profile = DeterministicFaultScenarioProfile.Normalize(loaded);

            DeterministicFaultScenarioReplayResult first = await RunReplayAsync(profile);
            DeterministicFaultScenarioReplayResult second = await RunReplayAsync(profile);

            Assert.True(
                first.IsSuccess,
                $"{first.FailureReason}{Environment.NewLine}{string.Join(Environment.NewLine, first.ValidationErrors)}");
            Assert.True(
                second.IsSuccess,
                $"{second.FailureReason}{Environment.NewLine}{string.Join(Environment.NewLine, second.ValidationErrors)}");
            Assert.Equal(first.PlannedTicks, second.PlannedTicks);
            Assert.Equal(first.ExecutedTicks, second.ExecutedTicks);
            Assert.Equal(first.PlannedActions, second.PlannedActions);
            Assert.Equal(first.FinalSnapshot.TickIndex, second.FinalSnapshot.TickIndex);
            Assert.Equal(first.FinalSnapshot.SimulationTime, second.FinalSnapshot.SimulationTime);
            Assert.Equal(first.FinalSnapshot.Faults.Count, second.FinalSnapshot.Faults.Count);
            Assert.Equal(first.CommandResults.Count, second.CommandResults.Count);
            Assert.Equal(first.EventHistory.Count, second.EventHistory.Count);
            Assert.Equal(first.FinalSnapshot.RunMode, second.FinalSnapshot.RunMode);
            Assert.Equal(first.FinalSnapshot.ControlOwner, second.FinalSnapshot.ControlOwner);
            Assert.True(first.FinalSnapshot.Faults.Count == 0);
            Assert.True(second.FinalSnapshot.Faults.Count == 0);
            Assert.Equal(first.FinalSnapshot.Signals.Count, second.FinalSnapshot.Signals.Count);

            IReadOnlyList<SignalTimelineSample> sensorTimeline =
                SimulationSignalTimelineAnalyzer.GetSignalTimeline(first.SnapshotHistory, "di.sensor");
            Assert.Equal(new[] { true, false, true }, sensorTimeline.Select(item => item.Value).ToArray());
            Assert.Equal(0, sensorTimeline[0].TickIndex);
            Assert.Equal(2, sensorTimeline[1].TickIndex);
            Assert.Equal(4, sensorTimeline[2].TickIndex);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplayScenario_WithInvalidAction_FailsBeforeExecution()
    {
        using var engine = await CreateEngineWithRuntimeAsync();
        var invalid = new DeterministicFaultScenarioProfile(
            SchemaVersion: 1,
            ScenarioId: "invalid-scenario",
            Name: "Invalid scenario",
            Description: "Missing forced value.",
            DurationTicks: 1,
            Actions:
            [
                new DeterministicFaultScenarioAction(
                    Tick: 0,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.sensor")
            ]);

        var runner = new DeterministicFaultScenarioRunner();
        var result = await runner.ReplayAsync(engine, invalid);

        Assert.False(result.IsSuccess);
        Assert.Equal("Scenario validation failed.", result.FailureReason);
        Assert.Contains(result.ValidationErrors, error => error.Contains("InjectFault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReplayScenario_WithConflictingActionsAtSameTick_FailsBeforeExecution()
    {
        using var engine = await CreateEngineWithRuntimeAsync();
        var conflicting = new DeterministicFaultScenarioProfile(
            SchemaVersion: 1,
            ScenarioId: "conflicting-actions-scenario",
            Name: "Conflicting actions fixture",
            Description: "Inject and clear at the same tick for the same target.",
            DurationTicks: 1,
            Actions:
            [
                new DeterministicFaultScenarioAction(
                    Tick: 0,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.sensor",
                    ForcedValue: false),
                new DeterministicFaultScenarioAction(
                    Tick: 0,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.sensor")
            ]);

        var runner = new DeterministicFaultScenarioRunner();
        var result = await runner.ReplayAsync(engine, conflicting);

        Assert.False(result.IsSuccess);
        Assert.Equal("Scenario validation failed.", result.FailureReason);
        Assert.Contains(result.ValidationErrors, error => error.Contains("Conflicting actions", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<DeterministicFaultScenarioReplayResult> RunReplayAsync(
        DeterministicFaultScenarioProfile profile)
    {
        using var engine = await CreateEngineWithRuntimeAsync();
        var runner = new DeterministicFaultScenarioRunner();
        var result = await runner.ReplayAsync(engine, profile);
        await engine.StopAsync();
        return result;
    }

    private static async Task<FixedStepSimulationEngine> CreateEngineWithRuntimeAsync()
    {
        var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        var configuration = CreateInputCylinderRuntime();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration))).IsAccepted);
        return engine;
    }

    private static SimulationRuntimeConfiguration CreateInputCylinderRuntime()
    {
        ChannelDefinition[] channels =
        [
            new()
            {
                Id = "do.extend",
                Name = "Cylinder Extend",
                Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput,
                InitialValue = 0
            },
            new()
            {
                Id = "di.extended",
                Name = "Cylinder Extended",
                Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput,
                InitialValue = 0
            },
            new()
            {
                Id = "di.retracted",
                Name = "Cylinder Retracted",
                Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput,
                InitialValue = 1
            },
            new()
            {
                Id = "di.sensor",
                Name = "Vision Sensor",
                Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput,
                InitialValue = 1
            }
        ];

        var cylinder = new PneumaticCylinderRuntimeConfiguration(
            "cylinder-1",
            "Pneumatic Cylinder 1",
            "do.extend",
            "di.extended",
            "di.retracted",
            4,
            4,
            0,
            0,
            80,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(100, 40));

        return new SimulationRuntimeConfiguration(
            Array.Empty<AxisConfiguration>(),
            channels,
            Array.Empty<CompiledSequence>(),
            Array.Empty<VirtualCameraConfiguration>(),
            null,
            new MachineLayoutRuntimeConfiguration("fault-layout", "Fault Layout", new[] { cylinder }));
    }
}
