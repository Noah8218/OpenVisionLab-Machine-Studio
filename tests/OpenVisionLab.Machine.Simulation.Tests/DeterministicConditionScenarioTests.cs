using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicConditionScenarioTests
{
    private const string TargetId = "equipment-1";

    [Fact]
    public async Task ReplayScenario_IsDeterministicAndCapturesAllConditionPhases()
    {
        var profile = new DeterministicConditionScenarioProfile(
            SchemaVersion: DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            ScenarioId: "condition-cycle",
            Name: "Condition cycle",
            Description: "Deterministic condition transition fixture.",
            TargetId: TargetId,
            Seed: 42,
            DurationTicks: 10,
            MinimumStateTicks: 2,
            JitterTicks: 0);

        var first = await RunAsync(profile);
        var second = await RunAsync(profile);

        Assert.True(first.IsSuccess, first.FailureReason);
        Assert.True(second.IsSuccess, second.FailureReason);
        Assert.Equal(profile.DurationTicks, first.ExecutedTicks);
        Assert.Equal(first.EvidenceHash, second.EvidenceHash);
        Assert.Equal(first.ConditionHistory, second.ConditionHistory);
        Assert.Equal(first.Transitions, second.Transitions);
        Assert.Equal(
            first.SnapshotHistory.Select(ConditionProjection),
            second.SnapshotHistory.Select(ConditionProjection));
        Assert.Equal(
            first.EventHistory.Select(EventProjection),
            second.EventHistory.Select(EventProjection));
        Assert.Equal(
            new[]
            {
                DeterministicConditionState.Degraded,
                DeterministicConditionState.Fault,
                DeterministicConditionState.Recovering,
                DeterministicConditionState.Normal
            },
            first.Transitions.Select(item => item.To).ToArray());
        Assert.Equal(first.FinalSnapshot.TickIndex, second.FinalSnapshot.TickIndex);
        Assert.Equal(profile.DurationTicks, first.FinalSnapshot.TickIndex);
        Assert.True(first.FinalSnapshot.ConditionScenario.IsConfigured);
        Assert.False(first.FinalSnapshot.ConditionScenario.IsActive);
        Assert.Equal(profile.DurationTicks, first.FinalSnapshot.ConditionScenario.ExecutedTicks);
        Assert.Equal(
            new[]
            {
                "ConditionScenarioStarted",
                "ConditionStateChanged",
                "ConditionStateChanged",
                "ConditionStateChanged",
                "ConditionStateChanged",
                "ConditionScenarioCompleted"
            },
            first.EventHistory
                .Where(item => item.Category == "Condition")
                .Select(item => item.Code)
                .ToArray());
    }

    [Fact]
    public async Task EngineCommands_OwnPauseStepStopAndResetConditionState()
    {
        using var engine = await CreateEngineAsync();
        var profile = new DeterministicConditionScenarioProfile(
            SchemaVersion: DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            ScenarioId: "command-cycle",
            Name: "Command cycle",
            Description: "Engine-owned command fixture.",
            TargetId: TargetId,
            Seed: 21,
            DurationTicks: 1_000,
            MinimumStateTicks: 2,
            JitterTicks: 0,
            InitialState: DeterministicConditionState.Degraded);

        var started = await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile));
        Assert.True(started.IsAccepted, started.Detail);
        Assert.True(engine.CurrentSnapshot.ConditionScenario.IsActive);
        Assert.Equal(DeterministicConditionState.Degraded, engine.CurrentSnapshot.ConditionScenario.State);
        Assert.Equal(68, engine.CurrentSnapshot.ConditionScenario.HealthScore);
        Assert.Equal(0, engine.CurrentSnapshot.ConditionScenario.ExecutedTicks);

        var duplicate = await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile));
        Assert.False(duplicate.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ConditionScenarioAlreadyActive, duplicate.ErrorCode);

        var play = await engine.EnqueueCommandAsync(new PlayCommand());
        Assert.True(play.IsAccepted, play.Detail);
        for (var attempt = 0;
             attempt < 100 && engine.CurrentSnapshot.ConditionScenario.ExecutedTicks == 0;
             attempt++)
        {
            await Task.Delay(5);
        }

        var paused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(paused.IsAccepted, paused.Detail);
        long pausedEngineTick = engine.CurrentSnapshot.TickIndex;
        long pausedConditionTick = engine.CurrentSnapshot.ConditionScenario.ExecutedTicks;
        Assert.True(pausedConditionTick > 0);
        await Task.Delay(20);
        Assert.Equal(pausedEngineTick, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(pausedConditionTick, engine.CurrentSnapshot.ConditionScenario.ExecutedTicks);

        var firstStep = await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(firstStep.IsAccepted, firstStep.Detail);
        Assert.Equal(pausedEngineTick + 1, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(pausedConditionTick + 1, engine.CurrentSnapshot.ConditionScenario.ExecutedTicks);

        var stopped = await engine.EnqueueCommandAsync(new StopConditionScenarioCommand());
        Assert.True(stopped.IsAccepted, stopped.Detail);
        Assert.False(engine.CurrentSnapshot.ConditionScenario.IsActive);

        var engineOnlyStep = await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(engineOnlyStep.IsAccepted, engineOnlyStep.Detail);
        Assert.Equal(pausedEngineTick + 2, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(pausedConditionTick + 1, engine.CurrentSnapshot.ConditionScenario.ExecutedTicks);

        var restarted = await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile));
        Assert.True(restarted.IsAccepted, restarted.Detail);
        Assert.Equal(0, engine.CurrentSnapshot.ConditionScenario.ExecutedTicks);
        Assert.Equal(DeterministicConditionState.Degraded, engine.CurrentSnapshot.ConditionScenario.State);

        var restartedStep = await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(restartedStep.IsAccepted, restartedStep.Detail);
        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        Assert.True(reset.IsAccepted, reset.Detail);

        var resetSnapshot = engine.CurrentSnapshot;
        Assert.Equal(0, resetSnapshot.TickIndex);
        Assert.True(resetSnapshot.ConditionScenario.IsConfigured);
        Assert.False(resetSnapshot.ConditionScenario.IsActive);
        Assert.Equal(0, resetSnapshot.ConditionScenario.ExecutedTicks);
        Assert.Equal(DeterministicConditionState.Degraded, resetSnapshot.ConditionScenario.InitialState);
        Assert.Equal(DeterministicConditionState.Degraded, resetSnapshot.ConditionScenario.State);
        Assert.Equal(68, resetSnapshot.ConditionScenario.HealthScore);

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        Assert.Equal(2, events.Count(item => item.Code == "ConditionScenarioStarted"));
        Assert.Single(events, item => item.Code == "ConditionScenarioStopped");
        Assert.Single(events, item => item.Code == "ConditionScenarioReset");
    }

    [Fact]
    public async Task StartScenario_WithMissingRuntimeTargetFailsClosed()
    {
        using var engine = await CreateEngineAsync();
        var profile = new DeterministicConditionScenarioProfile(
            SchemaVersion: DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            ScenarioId: "missing-target",
            Name: "Missing target",
            Description: "Must be rejected.",
            TargetId: "missing-equipment",
            Seed: 1,
            DurationTicks: 4);

        var result = await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile));

        Assert.False(result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ConditionScenarioTargetNotFound, result.ErrorCode);
        Assert.False(engine.CurrentSnapshot.ConditionScenario.IsConfigured);
        Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
    }

    [Fact]
    public async Task StartScenario_WithAxisRuntimeTargetIsAcceptedWithoutLayout()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    new[]
                    {
                        new OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration
                        {
                            Id = "x",
                            Name = "X",
                            MaximumPosition = 500
                        }
                    },
                    Array.Empty<OpenVisionLab.Machine.Core.Channels.ChannelDefinition>(),
                    Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>())));
        Assert.True(configured.IsAccepted, configured.Detail);
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "axis-condition",
            "Axis condition",
            "Axis-backed condition target.",
            "x",
            42,
            4);

        var started = await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile));

        Assert.True(started.IsAccepted, started.Detail);
        Assert.Equal("x", engine.CurrentSnapshot.ConditionScenario.TargetId);
    }

    [Fact]
    public async Task AxisFaultSchedule_PauseStepStopAndResetRemainEngineOwned()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    new[]
                    {
                        new OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration
                        {
                            Id = "x",
                            Name = "X",
                            MaximumPosition = 500
                        }
                    },
                    Array.Empty<OpenVisionLab.Machine.Core.Channels.ChannelDefinition>(),
                    Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>())));
        Assert.True(configured.IsAccepted, configured.Detail);
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "axis-fault-command-cycle",
            "Axis fault command cycle",
            "Engine-owned scheduled axis fault.",
            "x",
            42,
            10,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.AxisMotionBlocked,
                "x",
                InjectTick: 2,
                HoldTicks: 2));

        Assert.True((await engine.EnqueueCommandAsync(
            new StartConditionScenarioCommand(profile))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        long pausedTick = engine.CurrentSnapshot.TickIndex;
        await Task.Delay(20);
        Assert.Equal(pausedTick, engine.CurrentSnapshot.TickIndex);
        Assert.Empty(engine.CurrentSnapshot.Faults);

        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Single(engine.CurrentSnapshot.Faults, fault =>
            fault.Kind == SimulationFaultKind.AxisMotionBlocked && fault.TargetId == "x");
        Assert.Equal(3, engine.CurrentSnapshot.TickIndex);

        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.False(engine.CurrentSnapshot.ConditionScenario.IsActive);

        Assert.True((await engine.EnqueueCommandAsync(
            new StartConditionScenarioCommand(profile))).IsAccepted);
        for (var tick = 0; tick < 3; tick++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }
        Assert.Single(engine.CurrentSnapshot.Faults);
        Assert.True((await engine.EnqueueCommandAsync(new StopConditionScenarioCommand())).IsAccepted);
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.False(engine.CurrentSnapshot.ConditionScenario.IsActive);

        Assert.True((await engine.EnqueueCommandAsync(
            new StartConditionScenarioCommand(profile))).IsAccepted);
        for (var tick = 0; tick < 5; tick++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.Equal(5, engine.CurrentSnapshot.ConditionScenario.ExecutedTicks);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        Assert.Equal(3, events.Count(item => item.Code == "FaultInjected"));
        Assert.Equal(2, events.Count(item => item.Code == "FaultCleared"));
        Assert.True(
            events.First(item => item.Code == "FaultInjected").EventIndex
            < events.First(item => item.Code == "FaultCleared").EventIndex);
    }

    [Fact]
    public async Task StuckInputFaultSchedule_StepInjectsAndClearsAtExactTicks()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            new SimulationRuntimeConfiguration(
                new[] { new OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration { Id = "x", Name = "X" } },
                new[]
                {
                    new OpenVisionLab.Machine.Core.Channels.ChannelDefinition
                    {
                        Id = "di.sensor",
                        Name = "Sensor",
                        Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput
                    }
                },
                Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>())))).IsAccepted);
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "stuck-input-schedule",
            "Stuck input schedule",
            "Engine-owned stuck input schedule.",
            "x",
            42,
            8,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                InjectTick: 1,
                HoldTicks: 2,
                ForcedValue: true));

        Assert.True((await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True(Assert.Single(engine.CurrentSnapshot.Signals, signal => signal.Id == "di.sensor").Value);
        Assert.Single(engine.CurrentSnapshot.Faults, fault =>
            fault.Kind == SimulationFaultKind.StuckDigitalInput && fault.ForcedValue == true);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Single(engine.CurrentSnapshot.Faults);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.False(Assert.Single(engine.CurrentSnapshot.Signals, signal => signal.Id == "di.sensor").Value);
    }

    [Fact]
    public async Task CylinderFaultSchedule_PauseStepResetRemainEngineOwned()
    {
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        OpenVisionLab.Machine.Core.Channels.ChannelDefinition[] channels =
        [
            new() { Id = "do.extend", Name = "Extend", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput },
            new() { Id = "di.extended", Name = "Extended", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput },
            new() { Id = "di.retracted", Name = "Retracted", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput }
        ];
        var cylinder = new PneumaticCylinderRuntimeConfiguration(
            "cylinder-1", "Cylinder 1", "do.extend", "di.extended", "di.retracted",
            4, 4, 0, 0, 80,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(100, 40));
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            new SimulationRuntimeConfiguration(
                Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                channels,
                Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>(),
                Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
                null,
                new MachineLayoutRuntimeConfiguration("main", "Main", new[] { cylinder }))))).IsAccepted);
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "cylinder-fault-schedule",
            "Cylinder fault schedule",
            "Engine-owned blocked cylinder schedule.",
            "cylinder-1",
            42,
            8,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.CylinderTravelBlocked,
                "cylinder-1",
                InjectTick: 1,
                HoldTicks: 2));

        Assert.True((await engine.EnqueueCommandAsync(new StartConditionScenarioCommand(profile))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Equal(PneumaticCylinderState.Fault,
            Assert.Single(engine.CurrentSnapshot.LayoutComponents).CylinderState);
        Assert.Single(engine.CurrentSnapshot.Faults);
        long pausedTick = engine.CurrentSnapshot.TickIndex;
        await Task.Delay(20);
        Assert.Equal(pausedTick, engine.CurrentSnapshot.TickIndex);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.Equal(PneumaticCylinderState.Retracted,
            Assert.Single(engine.CurrentSnapshot.LayoutComponents).CylinderState);
        Assert.False(engine.CurrentSnapshot.ConditionScenario.IsActive);
    }

    [Fact]
    public void LegacyAxisFault_NormalizesIntoCommonFaultRecovery()
    {
        var normalized = DeterministicConditionScenarioProfile.Normalize(
            new DeterministicConditionScenarioProfile(
                1, "legacy", "Legacy", "Legacy axis schedule.", "x", 42, 10,
                AxisFaultRecovery: new DeterministicAxisFaultRecoverySchedule("x", 2, 3)));

        Assert.Null(normalized.AxisFaultRecovery);
        Assert.Equal(SimulationFaultKind.AxisMotionBlocked, normalized.FaultRecovery?.FaultKind);
        Assert.Equal("x", normalized.FaultRecovery?.TargetId);
        Assert.Equal(2, normalized.FaultRecovery?.InjectTick);
        Assert.Equal(3, normalized.FaultRecovery?.HoldTicks);
    }

    [Fact]
    public void FaultRecoveryValidation_EnforcesKindSpecificForcedValue()
    {
        var stuckInputWithoutValue = CreateFaultProfile(
            new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                1,
                2));
        var cylinderWithValue = CreateFaultProfile(
            new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.CylinderTravelBlocked,
                "cylinder-1",
                1,
                2,
                ForcedValue: true));

        Assert.Contains(
            DeterministicConditionScenarioProfile.Validate(stuckInputWithoutValue),
            error => error.Contains("ForcedValue is required", StringComparison.Ordinal));
        Assert.Contains(
            DeterministicConditionScenarioProfile.Validate(cylinderWithValue),
            error => error.Contains("ForcedValue is not valid", StringComparison.Ordinal));

        static DeterministicConditionScenarioProfile CreateFaultProfile(
            DeterministicFaultRecoverySchedule faultRecovery) =>
            new(
                DeterministicConditionScenarioProfile.CurrentSchemaVersion,
                "fault-validation",
                "Fault validation",
                "Fault validation fixture.",
                "x",
                42,
                8,
                FaultRecovery: faultRecovery);
    }

    [Fact]
    public void ProfileJson_RoundTripsAndNormalizesDefaults()
    {
        var profile = new DeterministicConditionScenarioProfile(
            SchemaVersion: 0,
            ScenarioId: "  json-cycle ",
            Name: "  JSON cycle ",
            Description: "  persisted profile ",
            TargetId: "  sensor-1 ",
            Seed: 7,
            DurationTicks: 8,
            MinimumStateTicks: 0,
            JitterTicks: -4);

        var normalized = DeterministicConditionScenarioProfile.Normalize(profile);
        var path = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260810-condition-scenario-tests",
            "condition-profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DeterministicConditionScenarioProfile.SaveToJson(profile, path);
        try
        {
            var loaded = Assert.IsType<DeterministicConditionScenarioProfile>(
                DeterministicConditionScenarioProfile.LoadFromJson(path));
            Assert.Equal(normalized, DeterministicConditionScenarioProfile.Normalize(loaded));
            Assert.Empty(DeterministicConditionScenarioProfile.Validate(loaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplayScenario_WithUnsupportedSchemaFailsBeforeStepping()
    {
        using var engine = await CreateEngineAsync();
        var profile = new DeterministicConditionScenarioProfile(
            SchemaVersion: DeterministicConditionScenarioProfile.CurrentSchemaVersion + 1,
            ScenarioId: "invalid-schema",
            Name: "Invalid schema",
            Description: "Must not execute.",
            TargetId: "sensor-1",
            Seed: 1,
            DurationTicks: 4);

        var result = await new DeterministicConditionScenarioRunner().ReplayAsync(engine, profile);

        Assert.False(result.IsSuccess);
        Assert.Equal("Scenario validation failed.", result.FailureReason);
        Assert.Contains(result.ValidationErrors, error => error.Contains("Unsupported schema", StringComparison.Ordinal));
        Assert.Equal(0, result.ExecutedTicks);
        Assert.Equal(0, result.FinalSnapshot.TickIndex);
    }

    private static async Task<DeterministicConditionScenarioReplayResult> RunAsync(
        DeterministicConditionScenarioProfile profile)
    {
        using var engine = await CreateEngineAsync();
        var result = await new DeterministicConditionScenarioRunner().ReplayAsync(engine, profile);
        await engine.StopAsync();
        return result;
    }

    private static async Task<FixedStepSimulationEngine> CreateEngineAsync()
    {
        var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                    Array.Empty<OpenVisionLab.Machine.Core.Channels.ChannelDefinition>(),
                    Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>(),
                    Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
                    automaticRun: null,
                    new MachineLayoutRuntimeConfiguration(
                        "main",
                        "Main",
                        new LayoutComponentRuntimeConfiguration[]
                        {
                            new MachineFrameRuntimeConfiguration(
                                TargetId,
                                "Equipment",
                                new LayoutRuntimeTransform(0, 0),
                                new LayoutRuntimeSize(10, 10))
                        }))));
        Assert.True(configured.IsAccepted, configured.Detail);
        return engine;
    }

    private static object ConditionProjection(OpenVisionLab.Machine.Simulation.Snapshots.SimulationSnapshot snapshot) =>
        new
        {
            snapshot.TickIndex,
            snapshot.SimulationTime,
            snapshot.RunMode,
            snapshot.ConditionScenario
        };

    private static object EventProjection(SimulationEvent item) =>
        new
        {
            item.EventIndex,
            item.TickIndex,
            item.SimulationTime,
            item.Category,
            item.Code,
            item.Message
        };

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
}
