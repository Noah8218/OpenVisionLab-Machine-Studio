using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Analysis;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class AutomaticTransferCellEndToEndTests
{
    private const int MaximumStepCount = 2_000;
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task PersistedSample_CompilesAndCompletesTwoAutomaticCyclesAtHome()
    {
        string samplePath = Path.Combine(
            AppContext.BaseDirectory,
            "AutomaticTransferCell.ovmachine");
        MachineProjectDocument project = new ProjectDocumentStore().Load(
            File.ReadAllText(samplePath));
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);

        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);
        Assert.Empty(runtime.Cameras);

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001,
            Seed = project.Simulation.Seed
        });
        await engine.StartAsync();

        SimulationCommandResult configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(runtime));
        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.Empty(engine.CurrentSnapshot.Cameras);
        AssertAtReset(engine.CurrentSnapshot);

        SimulationCommandResult started = await engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand());
        SimulationCommandResult paused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(started.IsAccepted, started.Detail);
        Assert.True(paused.IsAccepted, paused.Detail);
        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
        List<SimulationSnapshot> snapshots = new()
        {
            engine.CurrentSnapshot
        };

        bool sawSensorOn = false;
        bool sawSensorOffAfterOn = false;
        bool sawCylinderExtended = false;
        bool sawCylinderRetractedAfterExtended = false;
        bool sawConveyorForward = false;
        bool sawConveyorReverse = false;
        bool sawWorkpieceAtStation = false;
        int stepsExecuted = 0;

        while (stepsExecuted < MaximumStepCount &&
               engine.CurrentSnapshot.AutomaticRun.CompletedCycleCount < 2)
        {
            SimulationCommandResult step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, step.Detail);
            stepsExecuted++;

            SimulationSnapshot snapshot = engine.CurrentSnapshot;
            snapshots.Add(snapshot);
            LayoutComponentSnapshot sensor = LayoutComponent(snapshot, "sensor-1");
            bool sensorInput = Assert.Single(
                snapshot.Signals,
                signal => signal.Id == "di.station-present").Value;
            Assert.Equal(sensor.IsDetected, sensorInput);

            if (sensorInput)
            {
                sawSensorOn = true;
            }
            else if (sawSensorOn)
            {
                sawSensorOffAfterOn = true;
            }

            LayoutComponentSnapshot cylinder = LayoutComponent(snapshot, "cylinder-1");
            if (cylinder.CylinderState == PneumaticCylinderState.Extended)
            {
                sawCylinderExtended = true;
            }
            else if (sawCylinderExtended
                     && cylinder.CylinderState == PneumaticCylinderState.Retracted)
            {
                sawCylinderRetractedAfterExtended = true;
            }

            LayoutComponentSnapshot conveyor = LayoutComponent(snapshot, "conveyor-1");
            if (conveyor.ConveyorRunning == true)
            {
                sawConveyorForward |= conveyor.ConveyorDirection == ConveyorDirection.Forward;
                sawConveyorReverse |= conveyor.ConveyorDirection == ConveyorDirection.Reverse;
            }

            LayoutComponentSnapshot workpiece = LayoutComponent(snapshot, "workpiece-1");
            sawWorkpieceAtStation |= workpiece.X >= 275;
        }

        SimulationSnapshot completed = engine.CurrentSnapshot;
        Assert.True(
            stepsExecuted < MaximumStepCount,
            "The persisted automatic cycle exceeded its bounded deterministic step budget.");
        Assert.Equal(2, completed.AutomaticRun.CompletedCycleCount);
        Assert.True(sawSensorOn);
        Assert.True(sawSensorOffAfterOn);
        Assert.True(sawCylinderExtended);
        Assert.True(sawCylinderRetractedAfterExtended);
        Assert.True(sawConveyorForward);
        Assert.True(sawConveyorReverse);
        Assert.True(sawWorkpieceAtStation);
        Assert.Empty(completed.Cameras);
        AssertAtCompletedHome(completed);

        var stationTimeline = SimulationSignalTimelineAnalyzer.GetSignalTimeline(snapshots, "di.station-present");
        Assert.Equal(
            new[] { false, true, false, true, false },
            stationTimeline.Select(item => item.Value).ToArray());
        var cylinderExtendedTimeline = SimulationSignalTimelineAnalyzer.GetSignalTimeline(snapshots, "di.cylinder-1.extended");
        Assert.Equal(
            new[] { false, true, false, true, false },
            cylinderExtendedTimeline.Select(item => item.Value).ToArray());
        var conveyorHomeTimeline = SimulationSignalTimelineAnalyzer.GetSignalTimeline(snapshots, "di.conveyor-home");
        Assert.False(conveyorHomeTimeline[0].Value);
        Assert.True(conveyorHomeTimeline.Last().Value);

        await engine.StopAsync();
        IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
        Assert.DoesNotContain(events, item => item.Category == "Camera");
        Assert.Equal(2, events.Count(item =>
            item.Category == "Sequence" && item.Code == "SequenceCompleted"));
        Assert.Equal(2, events.Count(item =>
            item.Category == "AutomaticRun" && item.Code == "AutomaticRunCycleCompleted"));
        Assert.Equal(
            new[]
            {
                "SensorActivated",
                "SensorDeactivated",
                "SensorActivated",
                "SensorDeactivated"
            },
            events
                .Where(item => item.Category == "Sensor"
                    && item.Message.StartsWith("sensor-1 ", StringComparison.Ordinal))
                .Select(item => item.Code)
                .ToArray());
        Assert.Equal(5, events.Count(item =>
            item.Category == "Sensor"
            && item.Message.StartsWith("sensor-home ", StringComparison.Ordinal)));
        Assert.Equal(8, events.Count(item =>
            item.Category == "Cylinder" && item.Code == "CylinderStateChanged"));
        Assert.Equal(8, events.Count(item =>
            item.Category == "Cylinder" && item.Code == "CylinderFeedbackChanged"));
        Assert.Equal(12, events.Count(item =>
            item.Category == "Conveyor" && item.Code == "ConveyorStateChanged"));
    }

    private static void AssertAtReset(SimulationSnapshot snapshot)
    {
        Assert.Equal(0, Assert.Single(snapshot.Axes, axis => axis.Id == "x").Position);
        LayoutComponentSnapshot stage = LayoutComponent(snapshot, "stage-1");
        LayoutComponentSnapshot sensor = LayoutComponent(snapshot, "sensor-1");
        LayoutComponentSnapshot cylinder = LayoutComponent(snapshot, "cylinder-1");
        Assert.Equal(LayoutComponentKind.LinearStage, stage.Kind);
        Assert.Equal(40, stage.X);
        Assert.Equal(LayoutComponentKind.DigitalSensor, sensor.Kind);
        Assert.False(sensor.IsDetected);
        Assert.Equal(0, sensor.PendingTransitionTicks);
        Assert.False(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "di.station-present").Value);
        Assert.Equal(LayoutComponentKind.PneumaticCylinder, cylinder.Kind);
        Assert.Equal(PneumaticCylinderState.Retracted, cylinder.CylinderState);
        Assert.Equal(0, cylinder.MotionProgress);
        Assert.False(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "do.cylinder-1.extend").Value);
        Assert.False(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "di.cylinder-1.extended").Value);
        Assert.True(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "di.cylinder-1.retracted").Value);
        LayoutComponentSnapshot conveyor = LayoutComponent(snapshot, "conveyor-1");
        LayoutComponentSnapshot workpiece = LayoutComponent(snapshot, "workpiece-1");
        Assert.False(conveyor.ConveyorRunning);
        Assert.Equal(ConveyorDirection.Forward, conveyor.ConveyorDirection);
        Assert.Equal(40, workpiece.X);
        Assert.Equal(-150, workpiece.CarrierPosition);
        Assert.False(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "di.conveyor-home").Value);
    }

    private static void AssertAtCompletedHome(SimulationSnapshot snapshot)
    {
        AssertAtResetCore(snapshot);
        LayoutComponentSnapshot homeSensor = LayoutComponent(snapshot, "sensor-home");
        LayoutComponentSnapshot workpiece = LayoutComponent(snapshot, "workpiece-1");
        Assert.True(homeSensor.IsDetected);
        Assert.True(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "di.conveyor-home").Value);
        Assert.InRange(workpiece.X, 30, 40);
        Assert.False(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "do.conveyor-1.run").Value);
        Assert.False(Assert.Single(
            snapshot.Signals,
            signal => signal.Id == "do.conveyor-1.reverse").Value);
    }

    private static void AssertAtResetCore(SimulationSnapshot snapshot)
    {
        Assert.Equal(0, Assert.Single(snapshot.Axes, axis => axis.Id == "x").Position);
        Assert.False(LayoutComponent(snapshot, "sensor-1").IsDetected);
        Assert.Equal(PneumaticCylinderState.Retracted, LayoutComponent(snapshot, "cylinder-1").CylinderState);
        Assert.False(LayoutComponent(snapshot, "conveyor-1").ConveyorRunning);
        Assert.Equal(ConveyorDirection.Forward, LayoutComponent(snapshot, "conveyor-1").ConveyorDirection);
    }

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

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(
            Environment.NewLine,
            compilation.Errors.Select(error =>
                $"{error.Code} [{error.TargetId}]: {error.Message}"));
}
