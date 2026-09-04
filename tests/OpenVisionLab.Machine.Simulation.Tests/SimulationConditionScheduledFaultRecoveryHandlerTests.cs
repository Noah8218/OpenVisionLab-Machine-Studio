using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationConditionScheduledFaultRecoveryHandlerTests
{
    [Fact]
    public void Apply_AcceptedClearReturnsClearedStateAndFaultEvent()
    {
        var signalHub = CreateSignalHub("di.sensor");
        Assert.True(signalHub.SetDigitalInputOverride("di.sensor", false).IsAccepted);
        var activeFaults = new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>
        {
            [new(SimulationFaultKind.StuckDigitalInput, "di.sensor")] = new(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                false,
                3,
                TimeSpan.FromMilliseconds(15))
        };
        var state = ActiveState(AutomaticRunActive: false, InterruptedAutomaticRun: false);
        var outcome = new SimulationConditionScheduledFaultRecoveryHandler().Apply(
            CreateContext(
                new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.sensor",
                    3,
                    2),
                restartSequence: false,
                state,
                signalHub,
                activeFaults));

        Assert.False(outcome.State!.ScheduledFaultActive);
        Assert.False(outcome.State.InterruptedAutomaticRun);
        Assert.Empty(activeFaults);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("FaultCleared", operationEvent.Code);
        Assert.NotNull(operationEvent.CommandId);
    }

    [Fact]
    public void Apply_FaultedSequenceRestartResumesInterruptedAutomaticRunInOrder()
    {
        var executor = CreateFaultedExecutor("sequence");
        var signalHub = CreateSignalHub("di.sensor");
        Assert.True(signalHub.SetDigitalInputOverride("di.sensor", false).IsAccepted);
        var activeFaults = ActiveFaults();
        var outcome = new SimulationConditionScheduledFaultRecoveryHandler().Apply(
            CreateContext(
                new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.sensor",
                    3,
                    2,
                    RestartSequenceId: "sequence"),
                restartSequence: true,
                ActiveState(
                    AutomaticRunActive: false,
                    InterruptedAutomaticRun: true,
                    activeSequenceId: "sequence",
                    automaticRunWaitingForRepeat: true,
                    automaticRunRemainingDelayTicks: 4),
                signalHub,
                activeFaults,
                new Dictionary<string, DeterministicSequenceExecutor>
                {
                    ["sequence"] = executor
                }));

        Assert.Equal(SequenceExecutionStatus.Running, executor.CaptureSnapshot().Status);
        Assert.Equal("sequence", outcome.State!.ActiveSequenceId);
        Assert.Equal(SimulationControlOwner.EmbeddedSequence, outcome.State.ControlOwner);
        Assert.True(outcome.State.AutomaticRunActive);
        Assert.False(outcome.State.AutomaticRunWaitingForRepeat);
        Assert.Equal(0, outcome.State.AutomaticRunRemainingDelayTicks);
        Assert.Equal(
            new[] { "FaultCleared", "SequenceStarted", "AutomaticRunRecovered" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Apply_RejectedClearReturnsOnlyConditionRejectionWithoutStateMutation()
    {
        var outcome = new SimulationConditionScheduledFaultRecoveryHandler().Apply(
            CreateContext(
                new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.sensor",
                    3,
                    2),
                restartSequence: false,
                ActiveState(AutomaticRunActive: false, InterruptedAutomaticRun: true),
                CreateSignalHub("di.sensor"),
                new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>()));

        Assert.Null(outcome.State);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("ConditionFaultClearRejected", operationEvent.Code);
        Assert.Contains("FaultNotActive", operationEvent.Message, StringComparison.Ordinal);
    }

    private static SimulationConditionScheduledFaultRecoveryContext CreateContext(
        DeterministicFaultRecoverySchedule schedule,
        bool restartSequence,
        SimulationConditionScheduledFaultRecoveryState state,
        DeterministicSignalHub signalHub,
        IDictionary<SimulationFaultKey, SimulationFaultSnapshot> activeFaults,
        IReadOnlyDictionary<string, DeterministicSequenceExecutor>? sequenceExecutors = null) =>
        new(
            schedule,
            restartSequence,
            null,
            state,
            new List<OpenVisionLab.Machine.Simulation.Axis.ServoAxisComponent>(),
            signalHub,
            null,
            activeFaults,
            sequenceExecutors ?? new Dictionary<string, DeterministicSequenceExecutor>(),
            new SimulationFaultCommandHandler(),
            7,
            TimeSpan.FromMilliseconds(35));

    private static SimulationConditionScheduledFaultRecoveryState ActiveState(
        bool AutomaticRunActive,
        bool InterruptedAutomaticRun,
        string? activeSequenceId = null,
        bool automaticRunWaitingForRepeat = false,
        int automaticRunRemainingDelayTicks = 0) =>
        new(
            true,
            InterruptedAutomaticRun,
            activeSequenceId,
            SimulationControlOwner.Definition,
            AutomaticRunActive,
            automaticRunWaitingForRepeat,
            automaticRunRemainingDelayTicks);

    private static Dictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults() =>
        new()
        {
            [new(SimulationFaultKind.StuckDigitalInput, "di.sensor")] = new(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                false,
                3,
                TimeSpan.FromMilliseconds(15))
        };

    private static DeterministicSignalHub CreateSignalHub(params string[] channelIds) =>
        DeterministicSignalHub.Create(channelIds.Select(Channel).ToArray()).Hub!;

    private static ChannelDefinition Channel(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = ChannelKind.DigitalInput
    };

    private static DeterministicSequenceExecutor CreateFaultedExecutor(string id)
    {
        var compilation = new SequenceCompiler().Compile(
            new SequenceDefinition
            {
                Id = id,
                Name = id,
                WatchdogTimeoutMs = 1,
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "wait",
                        Name = "Wait",
                        Action = SequenceStepAction.WaitSignal,
                        TargetId = "di.never",
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
            new SequenceCompilationTargets(
                new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
                {
                    ["di.never"] = ChannelKind.DigitalInput
                },
                Array.Empty<string>()));
        var executor = new DeterministicSequenceExecutor(compilation.Sequence!);
        Assert.True(executor.Start().IsSuccess);
        var faulted = executor.Tick(TimeSpan.FromMilliseconds(1), new NoSignalContext());
        Assert.Equal(SequenceExecutionStatus.Faulted, faulted.Snapshot.Status);
        return executor;
    }

    private sealed class NoSignalContext : ISequenceRuntimeContext
    {
        public SequenceSignalReadResult ReadSignal(string signalId) => SequenceSignalReadResult.Success(false);

        public SequenceContextOperationResult SetSignal(string signalId, bool value) =>
            SequenceContextOperationResult.Failure(SequenceContextErrorCode.Unavailable, "Not used.");

        public SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition) =>
            SequenceContextOperationResult.Failure(SequenceContextErrorCode.Unavailable, "Not used.");

        public SequenceAxisMotionReadResult ReadAxisMotionState(string axisId) =>
            SequenceAxisMotionReadResult.Failure(SequenceContextErrorCode.Unavailable, "Not used.");
    }
}
