using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class AutomaticRunIntegrationTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task StartAutomaticRun_AppliesInputStartsSequenceAndEntersRealTimeAtomically()
    {
        using var engine = await CreateEngineAsync();
        var configured = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(
                CreateWaitForInputSequence(),
                new AutomaticRunConfiguration(
                    "automatic-cycle",
                    "di.start",
                    true,
                    Repeat: false,
                    RepeatDelayMilliseconds: 0))));

        var started = await engine.EnqueueCommandAsync(new StartAutomaticRunCommand());
        var snapshot = engine.CurrentSnapshot;

        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.True(started.IsAccepted, started.Detail);
        Assert.Equal(0, started.AppliedTick);
        Assert.Equal(TimeSpan.Zero, started.SimulationTime);
        Assert.Equal(SimulationRunMode.RealTime, snapshot.RunMode);
        Assert.Equal(SimulationControlOwner.EmbeddedSequence, snapshot.ControlOwner);
        Assert.True(Assert.Single(snapshot.Signals, signal => signal.Id == "di.start").Value);
        Assert.Equal(SequenceExecutionStatus.Running, Assert.Single(snapshot.Sequences).Status);
        Assert.True(snapshot.AutomaticRun.IsConfigured);
        Assert.True(snapshot.AutomaticRun.IsActive);
        Assert.False(snapshot.AutomaticRun.IsWaitingForRepeat);
        Assert.Equal(0, snapshot.AutomaticRun.CompletedCycleCount);
        Assert.Equal(0, snapshot.AutomaticRun.RemainingDelayTicks);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var input = Assert.Single(events, item => item.Code == "DigitalInputChanged");
        var sequence = Assert.Single(events, item => item.Code == "SequenceStarted");
        var automatic = Assert.Single(events, item => item.Code == "AutomaticRunStarted");
        Assert.Equal(0, input.TickIndex);
        Assert.Equal(input.TickIndex, sequence.TickIndex);
        Assert.Equal(input.TickIndex, automatic.TickIndex);
        Assert.True(input.EventIndex < sequence.EventIndex);
        Assert.True(sequence.EventIndex < automatic.EventIndex);
    }

    [Fact]
    public async Task StartAutomaticRun_StepDrivenModeRemainsPausedUntilOneTickIsRequested()
    {
        using var engine = await CreateEngineAsync();
        await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(
                CreateWaitForInputSequence(),
                new AutomaticRunConfiguration(
                    "automatic-cycle",
                    "di.start",
                    true,
                    Repeat: false,
                    RepeatDelayMilliseconds: 0))));

        var started = await engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand(beginRealTime: false));
        var startedSnapshot = engine.CurrentSnapshot;

        Assert.True(started.IsAccepted, started.Detail);
        Assert.Equal(SimulationRunMode.Paused, startedSnapshot.RunMode);
        Assert.Equal(0, startedSnapshot.TickIndex);
        Assert.True(startedSnapshot.AutomaticRun.IsActive);
        Assert.Equal(SequenceExecutionStatus.Running, Assert.Single(startedSnapshot.Sequences).Status);

        var step = await engine.EnqueueCommandAsync(new StepCommand());
        var steppedSnapshot = engine.CurrentSnapshot;

        Assert.True(step.IsAccepted, step.Detail);
        Assert.Equal(SimulationRunMode.Paused, steppedSnapshot.RunMode);
        Assert.Equal(1, steppedSnapshot.TickIndex);
        Assert.Equal("complete", Assert.Single(steppedSnapshot.Sequences).CurrentStepId);
    }

    [Fact]
    public async Task ConfigureRuntime_RejectsInvalidAutomaticRunWithoutReplacingCurrentRuntime()
    {
        using var engine = await CreateEngineAsync();
        var baseline = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(
                CreateCompleteSequence(),
                automaticRun: null,
                new ChannelDefinition
                {
                    Id = "di.original",
                    Name = "Original Input",
                    Kind = ChannelKind.DigitalInput
                })));
        Assert.True(baseline.IsAccepted, baseline.Detail);

        var invalidConfigurations = new[]
        {
            new AutomaticRunConfiguration("missing", null, true, false, 0),
            new AutomaticRunConfiguration("automatic-cycle", "missing", true, false, 0),
            new AutomaticRunConfiguration("automatic-cycle", "do.start", true, false, 0),
            new AutomaticRunConfiguration("automatic-cycle", " ", true, false, 0),
            new AutomaticRunConfiguration("automatic-cycle", null, true, true, -1),
            new AutomaticRunConfiguration("automatic-cycle", null, true, true, 7),
            new AutomaticRunConfiguration("automatic-cycle", null, true, false, 5)
        };

        foreach (var invalid in invalidConfigurations)
        {
            var rejected = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
                CreateRuntime(
                    CreateCompleteSequence(),
                    invalid,
                    new ChannelDefinition
                    {
                        Id = "do.start",
                        Name = "Output",
                        Kind = ChannelKind.DigitalOutput
                    })));

            Assert.False(rejected.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.RuntimeConfigurationInvalid, rejected.ErrorCode);
            Assert.Single(engine.CurrentSnapshot.Signals, signal => signal.Id == "di.original");
            Assert.False(engine.CurrentSnapshot.AutomaticRun.IsConfigured);
        }
    }

    [Fact]
    public async Task Repeat_TwoCyclesUseTheSameDeterministicTickSchedule()
    {
        var first = await RunTwoCyclesAsync();
        var second = await RunTwoCyclesAsync();

        Assert.Equal(first.TickIndex, second.TickIndex);
        Assert.Equal(first.CompletedCycleCount, second.CompletedCycleCount);
        Assert.Equal(first.IsActive, second.IsActive);
        Assert.Equal(first.IsWaitingForRepeat, second.IsWaitingForRepeat);
        Assert.Equal(first.RemainingDelayTicks, second.RemainingDelayTicks);
        Assert.Equal(first.AutomaticEvents, second.AutomaticEvents);
        Assert.Equal(3, first.TickIndex);
        Assert.Equal(2, first.CompletedCycleCount);
        Assert.True(first.IsActive);
        Assert.True(first.IsWaitingForRepeat);
        Assert.Equal(2, first.RemainingDelayTicks);
        Assert.Equal(
            new[]
            {
                (1L, "AutomaticRunCycleCompleted"),
                (3L, "AutomaticRunCycleRestarted"),
                (3L, "AutomaticRunCycleCompleted")
            },
            first.AutomaticEvents);
    }

    [Fact]
    public async Task PauseStepAndReset_ControlAutomaticRunAndClearCycleState()
    {
        using var engine = await CreateEngineAsync();
        await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(
                CreateWaitForInputSequence(),
                new AutomaticRunConfiguration(
                    "automatic-cycle",
                    "di.start",
                    true,
                    Repeat: true,
                    RepeatDelayMilliseconds: 10))));
        await engine.EnqueueCommandAsync(new StartAutomaticRunCommand());
        var pause = await engine.EnqueueCommandAsync(new PauseCommand());
        var pausedTick = engine.CurrentSnapshot.TickIndex;

        await Task.Delay(20);
        Assert.True(pause.IsAccepted, pause.Detail);
        Assert.Equal(pausedTick, engine.CurrentSnapshot.TickIndex);
        Assert.True(engine.CurrentSnapshot.AutomaticRun.IsActive);

        var step = await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True(step.IsAccepted, step.Detail);
        Assert.Equal(pausedTick + 1, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
        Assert.Equal("complete", Assert.Single(engine.CurrentSnapshot.Sequences).CurrentStepId);

        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        var snapshot = engine.CurrentSnapshot;
        Assert.True(reset.IsAccepted, reset.Detail);
        Assert.Equal(0, snapshot.TickIndex);
        Assert.Equal(TimeSpan.Zero, snapshot.SimulationTime);
        Assert.Equal(SimulationRunMode.Paused, snapshot.RunMode);
        Assert.Equal(SimulationControlOwner.Definition, snapshot.ControlOwner);
        Assert.True(snapshot.AutomaticRun.IsConfigured);
        Assert.False(snapshot.AutomaticRun.IsActive);
        Assert.False(snapshot.AutomaticRun.IsWaitingForRepeat);
        Assert.Equal(0, snapshot.AutomaticRun.CompletedCycleCount);
        Assert.Equal(0, snapshot.AutomaticRun.RemainingDelayTicks);
        Assert.False(Assert.Single(snapshot.Signals, signal => signal.Id == "di.start").Value);
        Assert.Equal(SequenceExecutionStatus.Ready, Assert.Single(snapshot.Sequences).Status);
    }

    private static async Task<RepeatEvidence> RunTwoCyclesAsync()
    {
        using var engine = await CreateEngineAsync();
        await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(
                CreateCompleteSequence(),
                new AutomaticRunConfiguration(
                    "automatic-cycle",
                    null,
                    true,
                    Repeat: true,
                    RepeatDelayMilliseconds: 10))));
        await engine.EnqueueCommandAsync(new StartAutomaticRunCommand());
        await engine.EnqueueCommandAsync(new PauseCommand());

        for (var index = 0; index < 3; index++)
        {
            var step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, step.Detail);
        }

        var snapshot = engine.CurrentSnapshot;
        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        return new RepeatEvidence(
            snapshot.TickIndex,
            snapshot.AutomaticRun.CompletedCycleCount,
            snapshot.AutomaticRun.IsActive,
            snapshot.AutomaticRun.IsWaitingForRepeat,
            snapshot.AutomaticRun.RemainingDelayTicks,
            events
                .Where(item => item.Category == "AutomaticRun"
                    && item.Code is "AutomaticRunCycleCompleted" or "AutomaticRunCycleRestarted")
                .Select(item => (item.TickIndex, item.Code))
                .ToArray());
    }

    private static async Task<FixedStepSimulationEngine> CreateEngineAsync()
    {
        var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001
        });
        await engine.StartAsync();
        return engine;
    }

    private static SimulationRuntimeConfiguration CreateRuntime(
        CompiledSequence sequence,
        AutomaticRunConfiguration? automaticRun,
        params ChannelDefinition[] additionalChannels)
    {
        var channels = additionalChannels.ToList();
        if (!channels.Any(channel => channel.Id == "di.start"))
        {
            channels.Add(new ChannelDefinition
            {
                Id = "di.start",
                Name = "Start Input",
                Kind = ChannelKind.DigitalInput
            });
        }

        return new SimulationRuntimeConfiguration(
            Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
            channels,
            new[] { sequence },
            Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
            automaticRun);
    }

    private static CompiledSequence CreateCompleteSequence() =>
        Compile(
            new SequenceDefinition
            {
                Id = "automatic-cycle",
                Name = "Automatic Cycle",
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "complete",
                        Name = "Complete",
                        Action = SequenceStepAction.Complete
                    }
                }
            },
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal));

    private static CompiledSequence CreateWaitForInputSequence() =>
        Compile(
            new SequenceDefinition
            {
                Id = "automatic-cycle",
                Name = "Automatic Cycle",
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "wait-start",
                        Name = "Wait Start",
                        Action = SequenceStepAction.WaitSignal,
                        TargetId = "di.start",
                        Parameter = "true",
                        NextStepId = "complete"
                    },
                    new SequenceStepDefinition
                    {
                        Id = "complete",
                        Name = "Complete",
                        Action = SequenceStepAction.Complete
                    }
                }
            },
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
            {
                ["di.start"] = ChannelKind.DigitalInput
            });

    private static CompiledSequence Compile(
        SequenceDefinition definition,
        IReadOnlyDictionary<string, ChannelKind> channels)
    {
        var result = new SequenceCompiler().Compile(
            definition,
            new SequenceCompilationTargets(channels, Array.Empty<string>()));
        Assert.True(
            result.IsSuccess,
            string.Join(Environment.NewLine, result.Errors.Select(error => error.Message)));
        return result.Sequence!;
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

    private sealed record RepeatEvidence(
        long TickIndex,
        long CompletedCycleCount,
        bool IsActive,
        bool IsWaitingForRepeat,
        int RemainingDelayTicks,
        IReadOnlyList<(long TickIndex, string Code)> AutomaticEvents);
}
