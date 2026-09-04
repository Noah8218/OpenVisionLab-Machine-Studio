using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationSequenceCommandHandlerTests
{
    [Fact]
    public void Apply_StartReturnsSequenceStateDeltaAndOperationEvent()
    {
        var executor = CreateReadyExecutor("sequence");
        var handler = new SimulationSequenceCommandHandler();
        var context = CreateContext(
            new Dictionary<string, DeterministicSequenceExecutor>
            {
                ["sequence"] = executor
            });

        var outcome = handler.Apply(new StartSequenceCommand("sequence"), context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SequenceExecutionStatus.Running, executor.CaptureSnapshot().Status);
        Assert.Equal("sequence", outcome.State!.ActiveSequenceId);
        Assert.Equal(SimulationControlOwner.EmbeddedSequence, outcome.State.ControlOwner);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("SequenceStarted", operationEvent.Code);
        Assert.Equal("sequence entered complete.", operationEvent.Message);
    }

    [Fact]
    public void Apply_AbortStopsAutomaticContinuationAndPreservesActiveSequenceId()
    {
        var executor = CreateReadyExecutor("sequence");
        Assert.True(executor.Start().IsSuccess);
        var handler = new SimulationSequenceCommandHandler();
        var context = CreateContext(
            new Dictionary<string, DeterministicSequenceExecutor>
            {
                ["sequence"] = executor
            },
            new SimulationSequenceCommandState(
                SimulationRunMode.RealTime,
                SimulationControlOwner.EmbeddedSequence,
                2,
                "sequence",
                AutomaticRunActive: true,
                AutomaticRunWaitingForRepeat: true,
                AutomaticRunRemainingDelayTicks: 4,
                ConditionScheduledFaultInterruptedAutomaticRun: true));

        var outcome = handler.Apply(new AbortSequenceCommand("sequence"), context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SequenceExecutionStatus.Aborted, executor.CaptureSnapshot().Status);
        Assert.Equal(SimulationRunMode.Paused, outcome.State!.RunMode);
        Assert.Equal(SimulationControlOwner.Definition, outcome.State.ControlOwner);
        Assert.Equal(0, outcome.State.PendingSteps);
        Assert.Equal("sequence", outcome.State.ActiveSequenceId);
        Assert.False(outcome.State.AutomaticRunActive);
        Assert.False(outcome.State.AutomaticRunWaitingForRepeat);
        Assert.Equal(0, outcome.State.AutomaticRunRemainingDelayTicks);
        Assert.True(outcome.State.ConditionScheduledFaultInterruptedAutomaticRun);
        Assert.Equal(
            SequenceDebugPauseReason.SequenceAborted,
            context.SequenceDebugState.CreateSnapshot().PauseReason);
        Assert.Equal(
            new[] { "SequenceAborted", "AutomaticRunAborted" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Apply_RetryRestartsFaultedSequenceAndClearsAutomaticRecoveryFlag()
    {
        var executor = CreateFaultedExecutor("sequence");
        var handler = new SimulationSequenceCommandHandler();
        var context = CreateContext(
            new Dictionary<string, DeterministicSequenceExecutor>
            {
                ["sequence"] = executor
            },
            new SimulationSequenceCommandState(
                SimulationRunMode.RealTime,
                SimulationControlOwner.Definition,
                1,
                "sequence",
                AutomaticRunActive: true,
                AutomaticRunWaitingForRepeat: true,
                AutomaticRunRemainingDelayTicks: 3,
                ConditionScheduledFaultInterruptedAutomaticRun: true));

        var outcome = handler.Apply(new RetrySequenceCommand("sequence"), context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SequenceExecutionStatus.Running, executor.CaptureSnapshot().Status);
        Assert.Equal("wait", executor.CaptureSnapshot().CurrentStepId);
        Assert.Equal(SimulationRunMode.Paused, outcome.State!.RunMode);
        Assert.Equal(SimulationControlOwner.EmbeddedSequence, outcome.State.ControlOwner);
        Assert.Equal(0, outcome.State.PendingSteps);
        Assert.False(outcome.State.AutomaticRunActive);
        Assert.False(outcome.State.AutomaticRunWaitingForRepeat);
        Assert.Equal(0, outcome.State.AutomaticRunRemainingDelayTicks);
        Assert.False(outcome.State.ConditionScheduledFaultInterruptedAutomaticRun);
        Assert.Equal("SequenceRetried", Assert.Single(outcome.Events!).Code);
    }

    private static SimulationSequenceCommandContext CreateContext(
        IReadOnlyDictionary<string, DeterministicSequenceExecutor> executors,
        SimulationSequenceCommandState? state = null) =>
        new(
            state ?? new SimulationSequenceCommandState(
                SimulationRunMode.Paused,
                SimulationControlOwner.Definition,
                0,
                null,
                AutomaticRunActive: false,
                AutomaticRunWaitingForRepeat: false,
                AutomaticRunRemainingDelayTicks: 0,
                ConditionScheduledFaultInterruptedAutomaticRun: false),
            executors,
            new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>(),
            new DeterministicSequenceDebugState(),
            13,
            TimeSpan.FromMilliseconds(65));

    private static DeterministicSequenceExecutor CreateReadyExecutor(string id)
    {
        var compilation = new SequenceCompiler().Compile(
            new SequenceDefinition
            {
                Id = id,
                Name = id,
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "complete",
                        Name = "Complete",
                        Action = SequenceStepAction.Complete
                    }
                }
            });
        return new DeterministicSequenceExecutor(compilation.Sequence!);
    }

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
