using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineLayoutAutomaticRunIntegrationTests
{
    private const string AxisId = "axis.x";
    private const string StageId = "stage.fixture";
    private const string SensorId = "sensor.inspect";
    private const string SensorInputId = "di.sensor.inspect";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task AutomaticRun_GeometrySensorAdvancesWaitSignalsAndReturnsStageHomeAcrossRepeats()
    {
        using var engine = await CreateEngineAsync();
        var configured = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateRuntime()));

        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.Empty(engine.CurrentSnapshot.Cameras);
        AssertLayoutAtHomeWithSensorLow(engine.CurrentSnapshot);

        var started = await engine.EnqueueCommandAsync(new StartAutomaticRunCommand());
        var paused = await engine.EnqueueCommandAsync(new PauseCommand());

        Assert.True(started.IsAccepted, started.Detail);
        Assert.True(paused.IsAccepted, paused.Detail);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);

        var visitedSteps = new HashSet<string>(StringComparer.Ordinal);
        var sawSensorOn = false;
        var sawSensorOffAfterOn = false;
        var sawFirstCycleAtHome = false;

        for (var iteration = 0;
             iteration < 100 && engine.CurrentSnapshot.AutomaticRun.CompletedCycleCount < 2;
             iteration++)
        {
            var step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, step.Detail);

            SimulationSnapshot snapshot = engine.CurrentSnapshot;
            var sequence = Assert.Single(snapshot.Sequences);
            if (sequence.CurrentStepId is not null)
            {
                visitedSteps.Add(sequence.CurrentStepId);
            }

            LayoutComponentSnapshot sensor = LayoutComponent(snapshot, SensorId);
            var sensorInput = Assert.Single(snapshot.Signals, signal => signal.Id == SensorInputId);
            Assert.NotNull(sensor.IsDetected);
            Assert.Equal(sensorInput.Value, sensor.IsDetected!.Value);

            if (sensor.IsDetected.Value)
            {
                sawSensorOn = true;
            }
            else if (sawSensorOn)
            {
                sawSensorOffAfterOn = true;
            }

            if (snapshot.AutomaticRun.CompletedCycleCount >= 1 &&
                Axis(snapshot).Position == 0 &&
                LayoutComponent(snapshot, StageId).X == 0)
            {
                sawFirstCycleAtHome = true;
            }
        }

        SimulationSnapshot completed = engine.CurrentSnapshot;
        Assert.Equal(2, completed.AutomaticRun.CompletedCycleCount);
        Assert.True(completed.AutomaticRun.IsActive);
        Assert.True(completed.AutomaticRun.IsWaitingForRepeat);
        Assert.Equal(2, completed.AutomaticRun.RemainingDelayTicks);
        Assert.True(sawSensorOn);
        Assert.True(sawSensorOffAfterOn);
        Assert.True(sawFirstCycleAtHome);
        Assert.Contains("wait-sensor-on", visitedSteps);
        Assert.Contains("move-home", visitedSteps);
        Assert.Contains("wait-sensor-off", visitedSteps);
        Assert.Contains("complete", visitedSteps);
        AssertLayoutAtHomeWithSensorLow(completed);

        var reset = await engine.EnqueueCommandAsync(new ResetCommand());
        SimulationSnapshot resetSnapshot = engine.CurrentSnapshot;
        Assert.True(reset.IsAccepted, reset.Detail);
        Assert.Equal(0, resetSnapshot.TickIndex);
        Assert.Equal(TimeSpan.Zero, resetSnapshot.SimulationTime);
        Assert.False(resetSnapshot.AutomaticRun.IsActive);
        Assert.False(resetSnapshot.AutomaticRun.IsWaitingForRepeat);
        Assert.Equal(0, resetSnapshot.AutomaticRun.CompletedCycleCount);
        Assert.Equal(SequenceExecutionStatus.Ready, Assert.Single(resetSnapshot.Sequences).Status);
        AssertLayoutAtHomeWithSensorLow(resetSnapshot);

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        Assert.Equal(
            new[]
            {
                "SensorActivated",
                "SensorDeactivated",
                "SensorActivated",
                "SensorDeactivated"
            },
            events
                .Where(item => item.Category == "Sensor")
                .Select(item => item.Code)
                .ToArray());
    }

    [Fact]
    public async Task ConfigureRuntime_RejectsInvalidLayoutBindingsWithoutReplacingCurrentRuntime()
    {
        using var engine = await CreateEngineAsync();
        var baseline = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateRuntime()));
        Assert.True(baseline.IsAccepted, baseline.Detail);

        var missingAxis = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(layoutAxisId: "axis.missing")));

        Assert.False(missingAxis.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.RuntimeConfigurationInvalid, missingAxis.ErrorCode);
        Assert.Contains("axis.missing", missingAxis.Detail, StringComparison.Ordinal);
        AssertCurrentRuntimeStillConfigured(engine.CurrentSnapshot);

        var wrongSensorChannelKind = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            CreateRuntime(sensorChannelKind: ChannelKind.DigitalOutput)));

        Assert.False(wrongSensorChannelKind.IsAccepted);
        Assert.Equal(
            SimulationCommandErrorCode.RuntimeConfigurationInvalid,
            wrongSensorChannelKind.ErrorCode);
        Assert.Contains("DigitalInput", wrongSensorChannelKind.Detail, StringComparison.Ordinal);
        AssertCurrentRuntimeStillConfigured(engine.CurrentSnapshot);
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
        string layoutAxisId = AxisId,
        ChannelKind sensorChannelKind = ChannelKind.DigitalInput) =>
        new(
            new[] { AxisConfiguration() },
            new[]
            {
                new ChannelDefinition
                {
                    Id = SensorInputId,
                    Name = "Inspection Sensor",
                    Kind = sensorChannelKind,
                    InitialValue = 0
                }
            },
            new[] { CreateAutomaticSequence() },
            Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
            new AutomaticRunConfiguration(
                "automatic-layout-cycle",
                StartInputId: null,
                StartInputValue: true,
                Repeat: true,
                RepeatDelayMilliseconds: 10),
            CreateLayout(layoutAxisId));

    private static AxisConfiguration AxisConfiguration() =>
        new()
        {
            Id = AxisId,
            Name = "X Axis",
            MinimumPosition = 0,
            MaximumPosition = 10,
            HomePosition = 0,
            MaximumVelocity = 1_000,
            Acceleration = 100_000,
            Deceleration = 100_000
        };

    private static MachineLayoutRuntimeConfiguration CreateLayout(string axisId) =>
        new(
            "layout.main",
            "Main Layout",
            new LayoutComponentRuntimeConfiguration[]
            {
                new LinearStageRuntimeConfiguration(
                    StageId,
                    "Fixture Stage",
                    axisId,
                    homePosition: 0,
                    new LayoutRuntimeTransform(0, 0),
                    new LayoutRuntimeSize(2, 2)),
                new DigitalSensorRuntimeConfiguration(
                    SensorId,
                    "Inspection Sensor",
                    SensorInputId,
                    StageId,
                    onDelayTicks: 1,
                    offDelayTicks: 1,
                    new LayoutRuntimeTransform(10, 0),
                    new LayoutRuntimeSize(2, 2))
            });

    private static CompiledSequence CreateAutomaticSequence()
    {
        var definition = new SequenceDefinition
        {
            Id = "automatic-layout-cycle",
            Name = "Automatic Layout Cycle",
            Steps =
            {
                Step("move-to-sensor", SequenceStepAction.MoveAxis, AxisId, "10", "wait-axis-sensor"),
                Step("wait-axis-sensor", SequenceStepAction.WaitAxisDone, AxisId, "", "wait-sensor-on", 1_000),
                Step("wait-sensor-on", SequenceStepAction.WaitSignal, SensorInputId, "true", "move-home", 1_000),
                Step("move-home", SequenceStepAction.MoveAxis, AxisId, "0", "wait-axis-home"),
                Step("wait-axis-home", SequenceStepAction.WaitAxisDone, AxisId, "", "wait-sensor-off", 1_000),
                Step("wait-sensor-off", SequenceStepAction.WaitSignal, SensorInputId, "false", "complete", 1_000),
                Step("complete", SequenceStepAction.Complete, "", "", null)
            }
        };
        var compilation = new SequenceCompiler().Compile(
            definition,
            new SequenceCompilationTargets(
                new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
                {
                    [SensorInputId] = ChannelKind.DigitalInput
                },
                new[] { AxisId }));
        Assert.True(
            compilation.IsSuccess,
            string.Join(Environment.NewLine, compilation.Errors.Select(error => error.Message)));
        return compilation.Sequence!;
    }

    private static SequenceStepDefinition Step(
        string id,
        SequenceStepAction action,
        string targetId,
        string parameter,
        string? nextStepId,
        int timeoutMilliseconds = 0) =>
        new()
        {
            Id = id,
            Name = id,
            Action = action,
            TargetId = targetId,
            Parameter = parameter,
            NextStepId = nextStepId,
            TimeoutMs = timeoutMilliseconds
        };

    private static void AssertLayoutAtHomeWithSensorLow(SimulationSnapshot snapshot)
    {
        Assert.Equal(0, Axis(snapshot).Position);
        LayoutComponentSnapshot stage = LayoutComponent(snapshot, StageId);
        LayoutComponentSnapshot sensor = LayoutComponent(snapshot, SensorId);
        Assert.Equal(LayoutComponentKind.LinearStage, stage.Kind);
        Assert.Equal(0, stage.X);
        Assert.Equal(LayoutComponentKind.DigitalSensor, sensor.Kind);
        Assert.False(sensor.IsDetected);
        Assert.Equal(0, sensor.PendingTransitionTicks);
        Assert.False(Assert.Single(snapshot.Signals, signal => signal.Id == SensorInputId).Value);
    }

    private static void AssertCurrentRuntimeStillConfigured(SimulationSnapshot snapshot)
    {
        Assert.Equal(AxisId, Assert.Single(snapshot.Axes).Id);
        Assert.Equal(
            new[] { SensorId, StageId },
            snapshot.LayoutComponents.Select(component => component.Id).ToArray());
        Assert.Equal(ChannelKind.DigitalInput, Assert.Single(snapshot.Signals).Kind);
        Assert.True(snapshot.AutomaticRun.IsConfigured);
    }

    private static AxisSnapshot Axis(SimulationSnapshot snapshot) =>
        Assert.Single(snapshot.Axes, axis => axis.Id == AxisId);

    private static LayoutComponentSnapshot LayoutComponent(
        SimulationSnapshot snapshot,
        string componentId) =>
        Assert.Single(snapshot.LayoutComponents, component => component.Id == componentId);

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
