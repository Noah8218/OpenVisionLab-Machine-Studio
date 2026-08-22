using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicFaultInjectionTests
{
    [Fact]
    public async Task StuckDigitalInput_RetainsNominalWrite_ClearRecoversAndResetRemovesFault()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateInputRuntime()))).IsAccepted);

        var inject = new InjectSimulationFaultCommand(
            SimulationFaultKind.StuckDigitalInput,
            "di.sensor",
            false);
        SimulationCommandResult injected = await engine.EnqueueCommandAsync(inject);
        SimulationCommandResult nominalWrite = await engine.EnqueueCommandAsync(
            new SetVirtualInputCommand("di.sensor", true));

        Assert.True(injected.IsAccepted, injected.Detail);
        Assert.True(nominalWrite.IsAccepted, nominalWrite.Detail);
        Assert.False(Signal(engine, "di.sensor").Value);
        SimulationFaultSnapshot active = Assert.Single(engine.CurrentSnapshot.Faults);
        Assert.Equal(SimulationFaultKind.StuckDigitalInput, active.Kind);
        Assert.Equal(false, active.ForcedValue);

        var clear = new ClearSimulationFaultCommand(
            SimulationFaultKind.StuckDigitalInput,
            "di.sensor");
        SimulationCommandResult cleared = await engine.EnqueueCommandAsync(clear);
        Assert.True(cleared.IsAccepted, cleared.Detail);
        Assert.True(Signal(engine, "di.sensor").Value);
        Assert.Empty(engine.CurrentSnapshot.Faults);

        Assert.True((await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                true))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.False(Signal(engine, "di.sensor").Value);
        Assert.Empty(engine.CurrentSnapshot.Faults);

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item => item.Code == "FaultInjected" && item.CommandId == inject.CommandId);
        Assert.Contains(events, item => item.Code == "FaultCleared" && item.CommandId == clear.CommandId);
    }

    [Fact]
    public async Task CylinderTravelBlocked_IsSnapshotVisibleAndClearResumesRuntimeState()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateCylinderRuntime()))).IsAccepted);

        var inject = new InjectSimulationFaultCommand(
            SimulationFaultKind.CylinderTravelBlocked,
            "cylinder-1");
        Assert.True((await engine.EnqueueCommandAsync(inject)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);

        LayoutComponentSnapshot faulted = Assert.Single(engine.CurrentSnapshot.LayoutComponents);
        Assert.Equal(PneumaticCylinderState.Fault, faulted.CylinderState);
        Assert.Equal(0, faulted.MotionProgress);
        Assert.Single(engine.CurrentSnapshot.Faults);

        var clear = new ClearSimulationFaultCommand(
            SimulationFaultKind.CylinderTravelBlocked,
            "cylinder-1");
        Assert.True((await engine.EnqueueCommandAsync(clear)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);

        LayoutComponentSnapshot recovered = Assert.Single(engine.CurrentSnapshot.LayoutComponents);
        Assert.Equal(PneumaticCylinderState.Retracted, recovered.CylinderState);
        Assert.Equal(0, recovered.MotionProgress);
        Assert.Empty(engine.CurrentSnapshot.Faults);

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        Assert.Contains(events, item => item.Code == "CylinderStateChanged" && item.Message.Contains("Fault"));
        Assert.Contains(events, item => item.Code == "FaultCleared" && item.CommandId == clear.CommandId);
    }

    [Fact]
    public async Task ManualCylinder_ExtendRetractPauseStepFaultRecoveryAndResetAreDeterministic()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateCylinderRuntime()))).IsAccepted);

        var rejectedBeforeManual = await engine.EnqueueCommandAsync(
            new SetCylinderCommand("cylinder-1", true));
        Assert.False(rejectedBeforeManual.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.ControlOwnerNotAllowed, rejectedBeforeManual.ErrorCode);

        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        var extend = new SetCylinderCommand("cylinder-1", true);
        Assert.True((await engine.EnqueueCommandAsync(extend)).IsAccepted);

        for (var tick = 0; tick < 4; tick++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }

        LayoutComponentSnapshot extended = Assert.Single(engine.CurrentSnapshot.LayoutComponents);
        Assert.Equal(PneumaticCylinderState.Extended, extended.CylinderState);
        Assert.Equal(1, extended.MotionProgress);
        Assert.True(Signal(engine, "do.extend").Value);
        Assert.True(Signal(engine, "di.extended").Value);
        Assert.False(Signal(engine, "di.retracted").Value);

        var retract = new SetCylinderCommand("cylinder-1", false);
        Assert.True((await engine.EnqueueCommandAsync(retract)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.Equal(PneumaticCylinderState.Retracting,
            Assert.Single(engine.CurrentSnapshot.LayoutComponents).CylinderState);

        var inject = new InjectSimulationFaultCommand(
            SimulationFaultKind.CylinderTravelBlocked,
            "cylinder-1");
        Assert.True((await engine.EnqueueCommandAsync(inject)).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        LayoutComponentSnapshot faulted = Assert.Single(engine.CurrentSnapshot.LayoutComponents);
        Assert.Equal(PneumaticCylinderState.Fault, faulted.CylinderState);
        Assert.Equal(0.75, faulted.MotionProgress);

        var blockedExtend = new SetCylinderCommand("cylinder-1", true);
        SimulationCommandResult blocked = await engine.EnqueueCommandAsync(blockedExtend);
        Assert.False(blocked.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.CylinderInterlocked, blocked.ErrorCode);

        var clear = new ClearSimulationFaultCommand(
            SimulationFaultKind.CylinderTravelBlocked,
            "cylinder-1");
        Assert.True((await engine.EnqueueCommandAsync(clear)).IsAccepted);
        var explicitRetract = new SetCylinderCommand("cylinder-1", false);
        Assert.True((await engine.EnqueueCommandAsync(explicitRetract)).IsAccepted);
        for (var tick = 0; tick < 3; tick++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }

        LayoutComponentSnapshot recovered = Assert.Single(engine.CurrentSnapshot.LayoutComponents);
        Assert.Equal(PneumaticCylinderState.Retracted, recovered.CylinderState);
        Assert.Equal(0, recovered.MotionProgress);
        Assert.False(Signal(engine, "do.extend").Value);
        Assert.False(Signal(engine, "di.extended").Value);
        Assert.True(Signal(engine, "di.retracted").Value);

        Assert.True((await engine.EnqueueCommandAsync(new SetCylinderCommand("cylinder-1", true))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        LayoutComponentSnapshot reset = Assert.Single(engine.CurrentSnapshot.LayoutComponents);
        Assert.Equal(PneumaticCylinderState.Retracted, reset.CylinderState);
        Assert.Equal(0, reset.MotionProgress);
        Assert.False(Signal(engine, "do.extend").Value);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
        Assert.Equal(SimulationControlOwner.Definition, engine.CurrentSnapshot.ControlOwner);

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        SimulationEvent extendEvent = Assert.Single(events, item =>
            item.Code == "CylinderExtendAccepted" && item.CommandId == extend.CommandId);
        SimulationEvent retractEvent = Assert.Single(events, item =>
            item.Code == "CylinderRetractAccepted" && item.CommandId == retract.CommandId);
        SimulationEvent faultEvent = Assert.Single(events, item =>
            item.Code == "FaultInjected" && item.CommandId == inject.CommandId);
        SimulationEvent rejectedEvent = Assert.Single(events, item =>
            item.Code == "CommandRejected" && item.CommandId == blockedExtend.CommandId);
        SimulationEvent clearEvent = Assert.Single(events, item =>
            item.Code == "FaultCleared" && item.CommandId == clear.CommandId);
        SimulationEvent recoveryEvent = Assert.Single(events, item =>
            item.Code == "CylinderRetractAccepted" && item.CommandId == explicitRetract.CommandId);
        Assert.True(extendEvent.EventIndex < retractEvent.EventIndex);
        Assert.True(retractEvent.EventIndex < faultEvent.EventIndex);
        Assert.True(faultEvent.EventIndex < rejectedEvent.EventIndex);
        Assert.True(rejectedEvent.EventIndex < clearEvent.EventIndex);
        Assert.True(clearEvent.EventIndex < recoveryEvent.EventIndex);
    }

    [Fact]
    public async Task AxisMotionBlocked_StopsMotionInterlocksCommandsAndClearRecovers()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateAxisRuntime()))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(
            new JogAxisCommand("x", AxisJogDirection.Positive))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        Assert.True(engine.CurrentSnapshot.Axes[0].Position > 0);

        var inject = new InjectSimulationFaultCommand(
            SimulationFaultKind.AxisMotionBlocked,
            "x");
        var injected = await engine.EnqueueCommandAsync(inject);

        Assert.True(injected.IsAccepted, injected.Detail);
        Assert.Equal(AxisState.Error, engine.CurrentSnapshot.Axes[0].State);
        Assert.Equal(0, engine.CurrentSnapshot.Axes[0].Velocity);
        var active = Assert.Single(engine.CurrentSnapshot.Faults);
        Assert.Equal(SimulationFaultKind.AxisMotionBlocked, active.Kind);
        Assert.Equal(injected.AppliedTick, active.ActivatedTick);

        var blockedHome = new HomeAxisCommand("x");
        var blockedHomeResult = await engine.EnqueueCommandAsync(blockedHome);
        var blockedMoveResult = await engine.EnqueueCommandAsync(
            new MoveAbsoluteCommand("x", 50));
        var blockedRelativeMoveResult = await engine.EnqueueCommandAsync(
            new MoveRelativeCommand("x", 10));
        var blockedVelocityMoveResult = await engine.EnqueueCommandAsync(
            new MoveVelocityCommand("x", 10));
        var blockedJogResult = await engine.EnqueueCommandAsync(
            new JogAxisCommand("x", AxisJogDirection.Negative));
        Assert.False(blockedHomeResult.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisInterlocked, blockedHomeResult.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.AxisInterlocked, blockedMoveResult.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.AxisInterlocked, blockedRelativeMoveResult.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.AxisInterlocked, blockedVelocityMoveResult.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.AxisInterlocked, blockedJogResult.ErrorCode);
        Assert.Equal(AxisState.Error, engine.CurrentSnapshot.Axes[0].State);

        var clear = new ClearSimulationFaultCommand(
            SimulationFaultKind.AxisMotionBlocked,
            "x");
        var cleared = await engine.EnqueueCommandAsync(clear);
        Assert.True(cleared.IsAccepted, cleared.Detail);
        Assert.Empty(engine.CurrentSnapshot.Faults);
        Assert.Equal(AxisState.Stopped, engine.CurrentSnapshot.Axes[0].State);

        var home = await engine.EnqueueCommandAsync(new HomeAxisCommand("x"));
        Assert.True(home.IsAccepted, home.Detail);
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes[0].State);
        Assert.Equal(0, engine.CurrentSnapshot.Axes[0].Position);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var injectedEvent = Assert.Single(events, item =>
            item.Code == "FaultInjected" && item.CommandId == inject.CommandId);
        var rejectedEvent = Assert.Single(events, item =>
            item.Code == "CommandRejected" && item.CommandId == blockedHome.CommandId);
        var clearedEvent = Assert.Single(events, item =>
            item.Code == "FaultCleared" && item.CommandId == clear.CommandId);
        Assert.True(injectedEvent.EventIndex < rejectedEvent.EventIndex);
        Assert.True(rejectedEvent.EventIndex < clearedEvent.EventIndex);
    }

    [Fact]
    public async Task AxisFollowingError_AlarmsOnSameTickAcrossRunsAndExplicitClearRecovers()
    {
        var runEvidence = new List<string>();
        for (var run = 0; run < 2; run++)
        {
            using var engine = CreateEngine();
            await engine.StartAsync();
            Assert.True((await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(CreateAxisRuntime()))).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new MoveVelocityCommand("x", 5))).IsAccepted);

            var inject = new InjectSimulationFaultCommand(
                SimulationFaultKind.AxisFollowingError,
                "x");
            var injected = await engine.EnqueueCommandAsync(inject);
            Assert.True(injected.IsAccepted, injected.Detail);
            var paused = engine.CurrentSnapshot;
            await Task.Delay(50);
            Assert.Equal(paused.TickIndex, engine.CurrentSnapshot.TickIndex);
            Assert.Equal(paused.Axes[0].Position, engine.CurrentSnapshot.Axes[0].Position, 10);

            for (var step = 0; step < 4; step++)
            {
                var previousTick = engine.CurrentSnapshot.TickIndex;
                Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
                Assert.Equal(previousTick + 1, engine.CurrentSnapshot.TickIndex);
            }

            var alarmed = engine.CurrentSnapshot.Axes[0];
            Assert.True(alarmed.DriveAlarmActive);
            Assert.Equal(AxisState.Error, alarmed.State);
            Assert.Equal(0, alarmed.Position, 10);
            Assert.Equal(0.075, alarmed.CommandPosition, 10);
            Assert.Equal(0.075, alarmed.FollowingError, 10);
            Assert.Equal(0.05, alarmed.FollowingErrorLimit, 10);
            Assert.Equal(0, alarmed.Velocity, 10);
            var rejectedMove = await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 10));
            Assert.Equal(SimulationCommandErrorCode.AxisInterlocked, rejectedMove.ErrorCode);

            var clear = new ClearSimulationFaultCommand(
                SimulationFaultKind.AxisFollowingError,
                "x");
            var cleared = await engine.EnqueueCommandAsync(clear);
            Assert.True(cleared.IsAccepted, cleared.Detail);
            var recovered = engine.CurrentSnapshot.Axes[0];
            Assert.False(recovered.DriveAlarmActive);
            Assert.Equal(0, recovered.FollowingError, 10);
            Assert.Equal(AxisState.Stopped, recovered.State);
            Assert.Empty(engine.CurrentSnapshot.Faults);
            Assert.True((await engine.EnqueueCommandAsync(new MoveAbsoluteCommand("x", 10))).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
            Assert.Equal(AxisState.Idle, engine.CurrentSnapshot.Axes[0].State);
            Assert.Equal(0, engine.CurrentSnapshot.Axes[0].Position, 10);

            await engine.StopAsync();
            var events = await ReadAllEventsAsync(engine);
            var injectedEvent = Assert.Single(events, item =>
                item.Code == "FaultInjected" && item.CommandId == inject.CommandId);
            var alarmEvent = Assert.Single(events, item => item.Code == "AxisDriveAlarmActivated");
            var alarmClearedEvent = Assert.Single(events, item =>
                item.Code == "AxisDriveAlarmCleared" && item.CommandId == clear.CommandId);
            var faultClearedEvent = Assert.Single(events, item =>
                item.Code == "FaultCleared" && item.CommandId == clear.CommandId);
            Assert.True(injectedEvent.EventIndex < alarmEvent.EventIndex);
            Assert.True(alarmEvent.EventIndex < alarmClearedEvent.EventIndex);
            Assert.True(alarmClearedEvent.EventIndex < faultClearedEvent.EventIndex);
            Assert.Equal(4, alarmEvent.TickIndex);
            Assert.Equal(TimeSpan.FromMilliseconds(20), alarmEvent.SimulationTime);
            runEvidence.Add(string.Join('|',
                alarmed.State,
                alarmed.Position,
                alarmed.CommandPosition,
                alarmed.FollowingError,
                alarmed.DriveAlarmActive,
                alarmEvent.TickIndex,
                alarmEvent.SimulationTime));
        }

        Assert.Equal(runEvidence[0], runEvidence[1]);
    }

    [Fact]
    public async Task InvalidFaultRequests_AreRejectedWithoutChangingSnapshot()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateCylinderRuntime()))).IsAccepted);

        SimulationCommandResult missingValue = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "di.extended"));
        SimulationCommandResult outputTarget = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "do.extend",
                true));
        SimulationCommandResult missingCylinder = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.CylinderTravelBlocked,
                "missing-cylinder"));
        SimulationCommandResult missingAxis = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.AxisMotionBlocked,
                "missing-axis"));
        SimulationCommandResult axisForcedValue = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.AxisMotionBlocked,
                "x",
                true));
        SimulationCommandResult followingErrorForcedValue = await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.AxisFollowingError,
                "x",
                true));
        SimulationCommandResult inactiveClear = await engine.EnqueueCommandAsync(
            new ClearSimulationFaultCommand(
                SimulationFaultKind.CylinderTravelBlocked,
                "cylinder-1"));

        Assert.Equal(SimulationCommandErrorCode.FaultParameterInvalid, missingValue.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.FaultTargetNotFound, outputTarget.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.FaultTargetNotFound, missingCylinder.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.FaultTargetNotFound, missingAxis.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.FaultParameterInvalid, axisForcedValue.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.FaultParameterInvalid, followingErrorForcedValue.ErrorCode);
        Assert.Equal(SimulationCommandErrorCode.FaultNotActive, inactiveClear.ErrorCode);
        Assert.Empty(engine.CurrentSnapshot.Faults);

        await engine.StopAsync();
    }

    [Fact]
    public async Task StuckInput_DrivesAuthoredTimeoutRecoveryPathDeterministically()
    {
        using var engine = CreateEngine();
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRecoverySequenceRuntime()))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(
            new InjectSimulationFaultCommand(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                false))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(
            new SetVirtualInputCommand("di.sensor", true))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(
            new StartSequenceCommand("recovery"))).IsAccepted);

        for (var tick = 0; tick < 4; tick++)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
        }

        SequenceExecutionSnapshot sequence = Assert.Single(engine.CurrentSnapshot.Sequences);
        Assert.Equal(SequenceExecutionStatus.Completed, sequence.Status);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, sequence.LastError?.Code);
        Assert.True(Signal(engine, "do.recovery").Value);
        Assert.False(Signal(engine, "di.sensor").Value);

        await engine.StopAsync();
    }

    private static FixedStepSimulationEngine CreateEngine() =>
        new(new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });

    private static SimulationRuntimeConfiguration CreateInputRuntime() =>
        new(
            Array.Empty<AxisConfiguration>(),
            new[] { Channel("di.sensor", ChannelKind.DigitalInput, 0) },
            Array.Empty<CompiledSequence>());

    private static SimulationRuntimeConfiguration CreateAxisRuntime() =>
        new(
            new[]
            {
                new AxisConfiguration
                {
                    Id = "x",
                    Name = "X Axis",
                    MinimumPosition = 0,
                    MaximumPosition = 300,
                    HomePosition = 0,
                    MaximumVelocity = 200,
                    Acceleration = 500,
                    Deceleration = 500,
                    FollowingErrorLimit = 0.05
                }
            },
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>());

    private static SimulationRuntimeConfiguration CreateCylinderRuntime()
    {
        ChannelDefinition[] channels =
        [
            Channel("do.extend", ChannelKind.DigitalOutput, 0),
            Channel("di.extended", ChannelKind.DigitalInput, 0),
            Channel("di.retracted", ChannelKind.DigitalInput, 1)
        ];
        var cylinder = new PneumaticCylinderRuntimeConfiguration(
            "cylinder-1",
            "Cylinder 1",
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
            new MachineLayoutRuntimeConfiguration("main", "Main", new[] { cylinder }));
    }

    private static SimulationRuntimeConfiguration CreateRecoverySequenceRuntime()
    {
        ChannelDefinition[] channels =
        [
            Channel("di.sensor", ChannelKind.DigitalInput, 0),
            Channel("do.recovery", ChannelKind.DigitalOutput, 0)
        ];
        var definition = new SequenceDefinition
        {
            Id = "recovery",
            Name = "Recovery",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait-sensor",
                    Name = "Wait Sensor",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.sensor",
                    Parameter = "true",
                    TimeoutMs = 10,
                    NextStepId = "success",
                    ErrorStepId = "recover"
                },
                new SequenceStepDefinition
                {
                    Id = "success",
                    Name = "Success",
                    Action = SequenceStepAction.Complete
                },
                new SequenceStepDefinition
                {
                    Id = "recover",
                    Name = "Recover",
                    Action = SequenceStepAction.SetSignal,
                    TargetId = "do.recovery",
                    Parameter = "true",
                    NextStepId = "recovered"
                },
                new SequenceStepDefinition
                {
                    Id = "recovered",
                    Name = "Recovered",
                    Action = SequenceStepAction.Complete
                }
            }
        };
        var targets = new SequenceCompilationTargets(
            channels.ToDictionary(channel => channel.Id, channel => channel.Kind, StringComparer.Ordinal),
            Array.Empty<string>());
        SequenceCompilationResult compilation = new SequenceCompiler().Compile(definition, targets);
        Assert.True(compilation.IsSuccess, string.Join("; ", compilation.Errors.Select(error => error.Message)));
        return new SimulationRuntimeConfiguration(
            Array.Empty<AxisConfiguration>(),
            channels,
            new[] { compilation.Sequence! });
    }

    private static ChannelDefinition Channel(string id, ChannelKind kind, double initialValue) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = initialValue
        };

    private static DigitalSignalSnapshot Signal(FixedStepSimulationEngine engine, string id) =>
        Assert.Single(engine.CurrentSnapshot.Signals, signal => signal.Id == id);

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
