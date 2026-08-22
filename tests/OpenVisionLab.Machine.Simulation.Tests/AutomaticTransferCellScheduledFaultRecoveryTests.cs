using System.Collections.Immutable;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class AutomaticTransferCellScheduledFaultRecoveryTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    internal static readonly AutomaticTransferFaultCase PersistedStuckInput = new(
        "automatic-transfer-stuck-di-recovery",
        "Automatic transfer stuck-DI recovery",
        "Force the extended feedback OFF, clear it after timeout, and recover the automatic cycle.",
        SimulationFaultKind.StuckDigitalInput,
        "di.cylinder-1.extended",
        ForcedValue: false,
        InjectTick: 1,
        HoldTicks: 110,
        "20260813-automatic-transfer-stuck-di",
        "automatic-transfer-stuck-di-recovery-package.json",
        IsPersistedConfiguration: true);

    internal static readonly AutomaticTransferFaultCase BlockedCylinderTravel = new(
        "automatic-transfer-cylinder-block-recovery",
        "Automatic transfer blocked-cylinder recovery",
        "Freeze the extending stopper cylinder, clear it after timeout, and recover the automatic cycle.",
        SimulationFaultKind.CylinderTravelBlocked,
        "cylinder-1",
        ForcedValue: null,
        InjectTick: 5,
        HoldTicks: 110,
        "20260813-automatic-transfer-cylinder-fault",
        "automatic-transfer-cylinder-block-recovery-package.json",
        IsPersistedConfiguration: false);

    [Fact]
    public async Task PersistedStuckInputSchedule_RecoversAutomaticCycleDeterministically()
    {
        await AssertDeterministicAsync(PersistedStuckInput);
    }

    [Fact]
    public async Task CylinderTravelBlockedSchedule_ResumesTravelAndAutomaticCycleDeterministically()
    {
        await AssertDeterministicAsync(BlockedCylinderTravel);
    }

    [Fact]
    public async Task PersistedAssertions_FlowThroughBatchResultAndBaselineLifecycle()
    {
        AutomaticTransferFaultRun accepted = await RunAsync(PersistedStuckInput);
        Assert.Equal(3, accepted.Profile.Assertions.Length);
        Assert.True(accepted.Package.IsSuccess, accepted.Package.FailureReason);
        Assert.All(accepted.Package.AssertionOutcomes, outcome => Assert.True(outcome.IsPassed));

        const string batchId = "automatic-transfer-persisted-assertions";
        const string buildIdentity = "test-build";
        const int repetitions = 2;
        var batch = await new DeterministicSimulationBatchRunner().RunAsync(
            new DeterministicSimulationBatchDefinition(batchId, repetitions, buildIdentity),
            async (_, _) => (await RunAsync(PersistedStuckInput)).Package,
            accepted.Package);
        Assert.True(batch.IsSuccess);
        Assert.True(batch.HasValidEvidenceHash());

        string samplePath = Path.Combine(AppContext.BaseDirectory, "AutomaticTransferCell.ovmachine");
        var store = new ProjectDocumentStore();
        MachineProjectDocument reopened = store.Load(File.ReadAllText(samplePath));
        string projectJson = store.SerializeForEvidence(reopened);
        Assert.Equal(3, reopened.Simulation.TestScenarioAssertions.Count);

        string artifactDirectory = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts",
            "20260813-project-scenario-assertions");
        string baselinePath = Path.Combine(artifactDirectory, "accepted-baseline.json");
        string resultPath = Path.Combine(artifactDirectory, "batch-result.json");
        DeterministicSimulationRunResultPackage.SaveToJson(accepted.Package, baselinePath);
        DeterministicSimulationBatchResultPackage.SaveToJson(batch, resultPath);

        var restoredBaseline = Assert.IsType<DeterministicSimulationRunResultPackage>(
            DeterministicSimulationRunResultPackage.LoadFromJson(baselinePath));
        var restoredResult = Assert.IsType<DeterministicSimulationBatchResultPackage>(
            DeterministicSimulationBatchResultPackage.LoadFromJson(resultPath));
        Assert.True(restoredBaseline.IsForContext(
            reopened.Id,
            projectJson,
            FixedStep,
            accepted.Profile));
        Assert.True(restoredResult.IsForContext(
            batchId,
            buildIdentity,
            repetitions,
            reopened.Id,
            projectJson,
            FixedStep,
            accepted.Profile));

        reopened.Simulation.TestScenarioAssertions[2].ExpectedState = "Retracted";
        var changedProfile = accepted.Profile with
        {
            Assertions = DeterministicScenarioAssertion.FromProjectDefinitions(
                reopened.Simulation.TestScenarioAssertions)
        };
        Assert.False(restoredBaseline.IsForContext(
            reopened.Id,
            projectJson,
            FixedStep,
            changedProfile));
    }

    private static async Task AssertDeterministicAsync(AutomaticTransferFaultCase faultCase)
    {
        AutomaticTransferFaultRun first = await RunAsync(faultCase);
        AutomaticTransferFaultRun second = await RunAsync(faultCase);

        Assert.True(first.Package.IsEquivalentTo(second.Package));
        Assert.Equal(first.FaultInjectedTick, second.FaultInjectedTick);
        Assert.Equal(first.SequenceFaultedTick, second.SequenceFaultedTick);
        Assert.Equal(first.FaultClearedTick, second.FaultClearedTick);
        Assert.Equal(first.AutomaticRunRecoveredTick, second.AutomaticRunRecoveredTick);
        Assert.Equal(first.FirstCompletedCycleTick, second.FirstCompletedCycleTick);
        Assert.Equal(first.BlockedMotionProgress, second.BlockedMotionProgress);
        Assert.True(first.Package.HasValidEvidenceHash());

        string artifactPath = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\artifacts",
            faultCase.ArtifactDirectory,
            faultCase.ArtifactFileName);
        DeterministicSimulationRunResultPackage.SaveToJson(first.Package, artifactPath);
        var loaded = Assert.IsType<DeterministicSimulationRunResultPackage>(
            DeterministicSimulationRunResultPackage.LoadFromJson(artifactPath));
        Assert.True(first.Package.IsEquivalentTo(loaded));
    }

    internal static async Task<AutomaticTransferFaultRun> RunAsync(
        AutomaticTransferFaultCase faultCase,
        ImmutableArray<DeterministicScenarioAssertion> assertions = default)
    {
        string samplePath = Path.Combine(AppContext.BaseDirectory, "AutomaticTransferCell.ovmachine");
        string projectJson = File.ReadAllText(samplePath);
        var store = new ProjectDocumentStore();
        MachineProjectDocument project = store.Load(projectJson);
        projectJson = store.SerializeForEvidence(project);
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);
        if (faultCase.IsPersistedConfiguration)
        {
            TestScenarioFaultDefinition persistedFault = Assert.IsType<TestScenarioFaultDefinition>(
                project.Simulation.TestScenarioFault);
            Assert.True(persistedFault.Enabled);
            Assert.Equal(TestScenarioFaultKind.StuckDigitalInput, persistedFault.Kind);
            Assert.Equal(faultCase.TargetId, persistedFault.TargetId);
            Assert.Equal(faultCase.ForcedValue, persistedFault.ForcedValue);
            Assert.Equal(faultCase.InjectTick, persistedFault.InjectTick);
            Assert.Equal(faultCase.HoldTicks, persistedFault.HoldTicks);
            Assert.Equal("auto-transfer-cycle", persistedFault.RestartSequenceId);
        }

        var profile = new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            faultCase.ProfileId,
            faultCase.Name,
            faultCase.Description,
            Assert.IsType<string>(project.Simulation.TestScenarioTargetId),
            project.Simulation.TestScenarioSeed ?? project.Simulation.Seed,
            project.Simulation.TestScenarioDurationCycles,
            MinimumStateTicks: 200,
            JitterTicks: 0,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                faultCase.Kind,
                faultCase.TargetId,
                faultCase.InjectTick,
                faultCase.HoldTicks,
                faultCase.ForcedValue,
                "auto-transfer-cycle"),
            Assertions: assertions.IsDefault
                ? DeterministicScenarioAssertion.FromProjectDefinitions(
                    project.Simulation.TestScenarioAssertions)
                : assertions);

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
            engine, new StartConditionScenarioCommand(profile), commandResults, snapshots, events)).IsAccepted);
        Assert.True((await ExecuteAsync(
            engine, new StartAutomaticRunCommand(), commandResults, snapshots, events)).IsAccepted);
        Assert.True((await ExecuteAsync(
            engine, new PauseCommand(), commandResults, snapshots, events)).IsAccepted);

        bool sawExpectedFaultEffect = false;
        bool sawCylinderResumeToExtended = false;
        double? blockedMotionProgress = null;
        for (var scenarioTick = 0; scenarioTick < profile.DurationTicks; scenarioTick++)
        {
            SimulationCommandResult step = await ExecuteAsync(
                engine, new StepCommand(), commandResults, snapshots, events);
            Assert.True(step.IsAccepted, step.Detail);

            SimulationSnapshot snapshot = engine.CurrentSnapshot;
            DeterministicConditionScenarioSnapshot condition = snapshot.ConditionScenario;
            conditionHistory.Add(new DeterministicConditionSample(
                scenarioTick,
                Assert.IsType<string>(condition.TargetId),
                condition.State,
                condition.HealthScore));
            if (condition.LastTransition?.TickIndex == scenarioTick)
            {
                transitions.Add(condition.LastTransition);
            }

            bool faultActive = snapshot.Faults.Any(active =>
                active.Kind == faultCase.Kind
                && active.TargetId == faultCase.TargetId);
            LayoutComponentSnapshot cylinder = snapshot.LayoutComponents.Single(
                component => component.Id == "cylinder-1");
            if (faultCase.Kind == SimulationFaultKind.StuckDigitalInput)
            {
                sawExpectedFaultEffect |= faultActive
                    && cylinder.CylinderState == PneumaticCylinderState.Extended
                    && !Signal(snapshot, "di.cylinder-1.extended");
            }
            else if (faultActive)
            {
                Assert.Equal(PneumaticCylinderState.Fault, cylinder.CylinderState);
                double progress = Assert.IsType<double>(cylinder.MotionProgress);
                if (blockedMotionProgress.HasValue)
                {
                    Assert.Equal(blockedMotionProgress.Value, progress, precision: 10);
                }
                else
                {
                    Assert.InRange(progress, double.Epsilon, 1d - double.Epsilon);
                    blockedMotionProgress = progress;
                }

                sawExpectedFaultEffect = true;
            }
            else if (faultCase.Kind == SimulationFaultKind.CylinderTravelBlocked
                     && scenarioTick >= faultCase.ClearTick)
            {
                sawCylinderResumeToExtended |=
                    cylinder.CylinderState == PneumaticCylinderState.Extended;
            }
        }

        SimulationSnapshot completed = engine.CurrentSnapshot;
        Assert.True(sawExpectedFaultEffect);
        if (faultCase.Kind == SimulationFaultKind.CylinderTravelBlocked)
        {
            Assert.True(sawCylinderResumeToExtended);
        }
        Assert.Empty(completed.Faults);
        Assert.False(completed.ConditionScenario.IsActive);
        Assert.True(completed.AutomaticRun.IsActive);
        Assert.True(completed.AutomaticRun.CompletedCycleCount >= 1);
        Assert.NotEqual(
            SequenceExecutionStatus.Faulted,
            Assert.Single(completed.Sequences).Status);

        SimulationEvent injected = Assert.Single(events, item => item.Code == "FaultInjected");
        SimulationEvent sequenceFaulted = Assert.Single(events, item => item.Code == "SequenceFaulted");
        SimulationEvent automaticFaulted = Assert.Single(events, item => item.Code == "AutomaticRunFaulted");
        SimulationEvent cleared = Assert.Single(events, item => item.Code == "FaultCleared");
        SimulationEvent recovered = Assert.Single(events, item => item.Code == "AutomaticRunRecovered");
        SimulationEvent firstCycleCompleted = events.First(item => item.Code == "AutomaticRunCycleCompleted");
        Assert.True(injected.EventIndex < sequenceFaulted.EventIndex);
        Assert.True(sequenceFaulted.EventIndex < automaticFaulted.EventIndex);
        Assert.True(automaticFaulted.EventIndex < cleared.EventIndex);
        Assert.True(cleared.EventIndex < recovered.EventIndex);
        Assert.True(recovered.EventIndex < firstCycleCompleted.EventIndex);

        var package = DeterministicSimulationRunResultPackage.Create(
            project.Id,
            project.Name,
            samplePath,
            projectJson,
            FixedStep,
            profile,
            isSuccess: true,
            executedTicks: profile.DurationTicks,
            commandResults,
            conditionHistory,
            transitions,
            snapshots,
            events);

        await engine.StopAsync();
        return new AutomaticTransferFaultRun(
            package,
            profile,
            injected.TickIndex,
            sequenceFaulted.TickIndex,
            cleared.TickIndex,
            recovered.TickIndex,
            firstCycleCompleted.TickIndex,
            blockedMotionProgress);
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

    private static bool Signal(SimulationSnapshot snapshot, string signalId) =>
        Assert.Single(snapshot.Signals, signal => signal.Id == signalId).Value;

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(Environment.NewLine, compilation.Errors.Select(error =>
            $"{error.Code} [{error.TargetId}]: {error.Message}"));

    internal sealed record AutomaticTransferFaultRun(
        DeterministicSimulationRunResultPackage Package,
        DeterministicConditionScenarioProfile Profile,
        long FaultInjectedTick,
        long SequenceFaultedTick,
        long FaultClearedTick,
        long AutomaticRunRecoveredTick,
        long FirstCompletedCycleTick,
        double? BlockedMotionProgress);

    internal sealed record AutomaticTransferFaultCase(
        string ProfileId,
        string Name,
        string Description,
        SimulationFaultKind Kind,
        string TargetId,
        bool? ForcedValue,
        long InjectTick,
        int HoldTicks,
        string ArtifactDirectory,
        string ArtifactFileName,
        bool IsPersistedConfiguration)
    {
        public long ClearTick => InjectTick + HoldTicks;
    }
}
