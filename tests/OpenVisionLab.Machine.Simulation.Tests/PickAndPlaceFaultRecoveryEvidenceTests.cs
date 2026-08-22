using System.Collections.Immutable;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class PickAndPlaceFaultRecoveryEvidenceTests
{
    private const int ScenarioTicks = 2_000;
    private const int BlockedHoldTicks = 3;
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task BlockedPlaceAxis_ClearAndRestart_ProducesDeterministicSchema4Evidence()
    {
        PickPlaceFaultRun first = await RunAsync();
        PickPlaceFaultRun second = await RunAsync();

        Assert.True(first.Package.IsEquivalentTo(second.Package));
        Assert.Equal(first.FaultInjectedTick, second.FaultInjectedTick);
        Assert.Equal(first.FaultClearedTick, second.FaultClearedTick);
        Assert.Equal(first.RecoveryStartedTick, second.RecoveryStartedTick);
        Assert.Equal(first.SequenceCompletedTick, second.SequenceCompletedTick);
        Assert.Equal(first.BlockedPosition, second.BlockedPosition);

        Assert.Equal(DeterministicSimulationRunResultPackage.CurrentSchemaVersion, first.Package.SchemaVersion);
        Assert.Equal(ScenarioTicks, first.Package.PlannedTicks);
        Assert.Equal(ScenarioTicks, first.Package.ExecutedTicks);
        Assert.True(first.Package.IsSuccess);
        Assert.True(first.Package.HasValidEvidenceHash());
        Assert.NotEqual(string.Empty, first.Package.FaultHash);
        Assert.NotEqual(string.Empty, first.Package.WorkpieceHash);
        Assert.NotEqual(string.Empty, first.Package.SnapshotHash);
        Assert.NotEqual(string.Empty, first.Package.EventHash);

        var artifactPath = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260812-pick-place-axis-fault",
            "pick-place-axis-fault-recovery-package.json");
        DeterministicSimulationRunResultPackage.SaveToJson(first.Package, artifactPath);
        var loaded = Assert.IsType<DeterministicSimulationRunResultPackage>(
            DeterministicSimulationRunResultPackage.LoadFromJson(artifactPath));
        Assert.True(first.Package.IsEquivalentTo(loaded));
    }

    [Fact]
    public async Task RepeatedRecoveryBatch_MatchesBaselineAndLocatesChangedClearTick()
    {
        PickPlaceFaultRun accepted = await RunAsync();
        var definition = new DeterministicSimulationBatchDefinition(
            "pick-place-axis-fault-recovery",
            RepetitionCount: 3,
            BuildIdentity: "pick-place-axis-fault-recovery-v1");
        var runner = new DeterministicSimulationBatchRunner();

        DeterministicSimulationBatchResultPackage repeated = await runner.RunAsync(
            definition,
            async (_, _) => (await RunAsync()).Package,
            accepted.Package);

        Assert.True(repeated.IsComplete);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(3, repeated.CompletedRuns);
        Assert.Null(repeated.FirstMismatch);
        Assert.All(repeated.Runs, run => Assert.True(run.ReferenceComparison.IsMatch));
        Assert.True(repeated.HasValidEvidenceHash());

        DeterministicSimulationBatchResultPackage changedSchedule = await runner.RunAsync(
            definition with { BatchId = "pick-place-axis-fault-recovery-changed", RepetitionCount = 1 },
            async (_, _) => (await RunAsync(BlockedHoldTicks + 1)).Package,
            accepted.Package);

        Assert.True(changedSchedule.IsComplete);
        Assert.False(changedSchedule.IsSuccess);
        DeterministicSimulationBatchMismatch mismatch = Assert.IsType<DeterministicSimulationBatchMismatch>(
            changedSchedule.FirstMismatch);
        Assert.Equal(1, mismatch.RunIndex);
        Assert.Equal("FaultEvidenceMismatch", mismatch.Code);
        Assert.Equal("Fault", mismatch.EvidenceKind);
        Assert.Equal("x", mismatch.TargetId);
        Assert.Equal(accepted.FaultClearedTick, mismatch.ObservedTickIndex);
        Assert.True(changedSchedule.HasValidEvidenceHash());

        const string artifactRoot =
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts\\20260812-pick-place-axis-fault-batch";
        string baselinePath = Path.Combine(artifactRoot, "accepted-run-baseline.json");
        string batchPath = Path.Combine(artifactRoot, "repeated-recovery-batch.json");
        string mismatchPath = Path.Combine(artifactRoot, "changed-clear-schedule-mismatch.json");
        DeterministicSimulationRunResultPackage.SaveToJson(accepted.Package, baselinePath);
        DeterministicSimulationBatchResultPackage.SaveToJson(repeated, batchPath);
        DeterministicSimulationBatchResultPackage.SaveToJson(changedSchedule, mismatchPath);

        var loadedBaseline = Assert.IsType<DeterministicSimulationRunResultPackage>(
            DeterministicSimulationRunResultPackage.LoadFromJson(baselinePath));
        var loadedBatch = Assert.IsType<DeterministicSimulationBatchResultPackage>(
            DeterministicSimulationBatchResultPackage.LoadFromJson(batchPath));
        var loadedMismatch = Assert.IsType<DeterministicSimulationBatchResultPackage>(
            DeterministicSimulationBatchResultPackage.LoadFromJson(mismatchPath));
        Assert.True(accepted.Package.IsEquivalentTo(loadedBaseline));
        Assert.True(repeated.IsEquivalentTo(loadedBatch));
        Assert.True(changedSchedule.IsEquivalentTo(loadedMismatch));
    }

    internal static async Task<PickPlaceFaultRun> RunAsync(
        int blockedHoldTicks = BlockedHoldTicks,
        ImmutableArray<DeterministicScenarioAssertion> assertions = default)
    {
        string samplePath = Path.Combine(AppContext.BaseDirectory, "sample-pick-and-place.ovmachine");
        string projectJson = File.ReadAllText(samplePath);
        MachineProjectDocument project = new ProjectDocumentStore().Load(projectJson);
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);
        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "pick-place-axis-fault-recovery",
            "Pick-and-place blocked-axis recovery",
            "Block X during the place move, clear the fault, and restart the same sequence definition.",
            "x",
            project.Simulation.Seed,
            ScenarioTicks,
            MinimumStateTicks: 400,
            JitterTicks: 0,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.AxisMotionBlocked,
                "x",
                InjectTick: 403,
                HoldTicks: blockedHoldTicks,
                RestartSequenceId: "main"),
            Assertions: assertions);

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001,
            Seed = project.Simulation.Seed
        });
        await engine.StartAsync();

        var commandResults = new List<SimulationCommandResult>();
        var snapshots = new List<SimulationSnapshot> { engine.CurrentSnapshot };
        var events = new List<SimulationEvent>();
        var conditionHistory = new List<DeterministicConditionSample>();
        var transitions = new List<DeterministicConditionTransition>();

        Assert.True((await ExecuteAsync(
            engine, new ConfigureRuntimeCommand(runtime), commandResults, snapshots, events)).IsAccepted);
        Assert.True((await ExecuteAsync(
            engine, new PauseCommand(), commandResults, snapshots, events)).IsAccepted);
        Assert.True((await ExecuteAsync(
            engine, new StartConditionScenarioCommand(profile), commandResults, snapshots, events)).IsAccepted);
        Assert.True((await ExecuteAsync(
            engine, new StartSequenceCommand("main"), commandResults, snapshots, events)).IsAccepted);

        var faultInjected = false;
        var faultCleared = false;
        var recoveryStarted = false;
        var blockedTicks = 0;
        var blockedPosition = double.NaN;
        long faultInjectedTick = -1;
        long faultClearedTick = -1;
        long recoveryStartedTick = -1;

        for (var scenarioTick = 0; scenarioTick < ScenarioTicks; scenarioTick++)
        {
            var step = await ExecuteAsync(
                engine, new StepCommand(), commandResults, snapshots, events);
            Assert.True(step.IsAccepted, step.Detail);
            SimulationSnapshot snapshot = engine.CurrentSnapshot;
            DeterministicConditionScenarioSnapshot condition = snapshot.ConditionScenario;
            Assert.Equal(scenarioTick + 1, condition.ExecutedTicks);
            conditionHistory.Add(new DeterministicConditionSample(
                scenarioTick,
                Assert.IsType<string>(condition.TargetId),
                condition.State,
                condition.HealthScore));
            if (condition.LastTransition?.TickIndex == scenarioTick)
            {
                transitions.Add(condition.LastTransition);
            }

            PickPlaceWorkpieceSnapshot workpiece = Assert.Single(snapshot.Workpieces);
            double x = AxisPosition(snapshot, "x");
            bool axisFaultIsActive = snapshot.Faults.Any(fault =>
                fault.Kind == SimulationFaultKind.AxisMotionBlocked && fault.TargetId == "x");
            if (axisFaultIsActive)
            {
                if (!faultInjected)
                {
                    faultInjected = true;
                    faultInjectedTick = Assert.Single(snapshot.Faults).ActivatedTick;
                    blockedPosition = x;
                }

                blockedTicks++;
                Assert.True(Near(x, blockedPosition));
                Assert.Equal(PickPlaceWorkpieceState.Attached, workpiece.State);
                Assert.True(Near(workpiece.X, blockedPosition));
                Assert.Contains(
                    Assert.Single(snapshot.Sequences).Status,
                    blockedTicks == 1
                        ? new[] { SequenceExecutionStatus.Running, SequenceExecutionStatus.Faulted }
                        : new[] { SequenceExecutionStatus.Faulted });
            }
            else if (faultInjected && !faultCleared)
            {
                faultCleared = true;
                faultClearedTick = snapshot.TickIndex;
                Assert.Equal(AxisState.Stopped, Axis(snapshot, "x").State);
                Assert.Equal(SequenceExecutionStatus.Running, Assert.Single(snapshot.Sequences).Status);
                recoveryStarted = true;
                recoveryStartedTick = snapshot.TickIndex;
            }
        }

        Assert.True(faultInjected);
        Assert.True(faultCleared);
        Assert.True(recoveryStarted);
        Assert.Equal(blockedHoldTicks, blockedTicks);
        Assert.Equal(
            new[]
            {
                DeterministicConditionState.Degraded,
                DeterministicConditionState.Fault,
                DeterministicConditionState.Recovering,
                DeterministicConditionState.Normal
            },
            transitions.Select(transition => transition.To).ToArray());

        SimulationSnapshot completed = engine.CurrentSnapshot;
        Assert.Equal(ScenarioTicks, completed.TickIndex);
        Assert.False(completed.ConditionScenario.IsActive);
        Assert.Equal(ScenarioTicks, completed.ConditionScenario.ExecutedTicks);
        Assert.Equal(SequenceExecutionStatus.Completed, Assert.Single(completed.Sequences).Status);
        Assert.True(Near(AxisPosition(completed, "x"), 0));
        Assert.True(Near(AxisPosition(completed, "y"), 0));
        Assert.False(Signal(completed, "do.gripper"));
        PickPlaceWorkpieceSnapshot completedWorkpiece = Assert.Single(completed.Workpieces);
        Assert.Equal(PickPlaceWorkpieceState.Placed, completedWorkpiece.State);
        Assert.True(Near(completedWorkpiece.X, 400));
        Assert.True(Near(completedWorkpiece.Y, 240));

        SimulationEvent attachedEvent = Assert.Single(events, item => item.Code == "WorkpieceAttached");
        SimulationEvent injectedEvent = Assert.Single(events, item => item.Code == "FaultInjected");
        SimulationEvent sequenceFaultedEvent = Assert.Single(events, item => item.Code == "SequenceFaulted");
        SimulationEvent clearedEvent = Assert.Single(events, item => item.Code == "FaultCleared");
        SimulationEvent placedEvent = Assert.Single(events, item => item.Code == "WorkpiecePlaced");
        SimulationEvent completedEvent = Assert.Single(events, item => item.Code == "SequenceCompleted");
        SimulationEvent[] startedEvents = events.Where(item => item.Code == "SequenceStarted").ToArray();
        Assert.Equal(2, startedEvents.Length);
        Assert.True(attachedEvent.EventIndex < injectedEvent.EventIndex);
        Assert.True(injectedEvent.EventIndex < sequenceFaultedEvent.EventIndex);
        Assert.True(sequenceFaultedEvent.EventIndex < clearedEvent.EventIndex);
        Assert.True(clearedEvent.EventIndex < startedEvents[1].EventIndex);
        Assert.True(startedEvents[1].EventIndex < placedEvent.EventIndex);
        Assert.True(placedEvent.EventIndex < completedEvent.EventIndex);

        var package = DeterministicSimulationRunResultPackage.Create(
            project.Id,
            project.Name,
            samplePath,
            projectJson,
            FixedStep,
            profile,
            isSuccess: true,
            executedTicks: ScenarioTicks,
            commandResults,
            conditionHistory,
            transitions,
            snapshots,
            events);

        await engine.StopAsync();
        return new PickPlaceFaultRun(
            package,
            faultInjectedTick,
            faultClearedTick,
            recoveryStartedTick,
            completedEvent.TickIndex,
            blockedPosition);
    }

    private static async Task<SimulationCommandResult> ExecuteAsync(
        FixedStepSimulationEngine engine,
        SimulationCommand command,
        List<SimulationCommandResult> commandResults,
        List<SimulationSnapshot> snapshots,
        List<SimulationEvent> events)
    {
        SimulationCommandResult result = await engine.EnqueueCommandAsync(command);
        commandResults.Add(result);
        DrainEvents(engine.EventReader, events);
        snapshots.Add(engine.CurrentSnapshot);
        return result;
    }

    private static void DrainEvents(
        ChannelReader<SimulationEvent> reader,
        List<SimulationEvent> events)
    {
        while (reader.TryRead(out SimulationEvent? item))
        {
            events.Add(item);
        }
    }

    private static AxisSnapshot Axis(SimulationSnapshot snapshot, string axisId) =>
        Assert.Single(snapshot.Axes, axis => axis.Id == axisId);

    private static double AxisPosition(SimulationSnapshot snapshot, string axisId) =>
        Axis(snapshot, axisId).Position;

    private static bool Signal(SimulationSnapshot snapshot, string signalId) =>
        Assert.Single(snapshot.Signals, signal => signal.Id == signalId).Value;

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(Environment.NewLine, compilation.Errors.Select(error =>
            $"{error.Code} [{error.TargetId}]: {error.Message}"));

    internal sealed record PickPlaceFaultRun(
        DeterministicSimulationRunResultPackage Package,
        long FaultInjectedTick,
        long FaultClearedTick,
        long RecoveryStartedTick,
        long SequenceCompletedTick,
        double BlockedPosition);
}
