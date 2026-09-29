using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSequenceExecutorTests
{
    [Fact]
    public void Abort_PreservesBoundaryEvidence_AndResetIsRequiredBeforeRestart()
    {
        var definition = new SequenceDefinition
        {
            Id = "abortable",
            Steps =
            {
                SequenceCompilerTests.Step(
                    "wait",
                    SequenceStepAction.WaitSignal,
                    "di.start",
                    "true",
                    "complete"),
                SequenceCompilerTests.Step(
                    "complete",
                    SequenceStepAction.Complete,
                    string.Empty,
                    string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);

        Assert.True(executor.Start().IsSuccess);
        var running = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());
        var aborted = executor.Abort();

        Assert.Equal(SequenceExecutionStatus.Running, running.Snapshot.Status);
        Assert.Equal("wait", running.CurrentStepId);
        Assert.True(aborted.IsSuccess);
        Assert.Equal(SequenceExecutionStatus.Aborted, aborted.Snapshot.Status);
        Assert.Equal("wait", aborted.Snapshot.CurrentStepId);
        Assert.Equal(TimeSpan.FromMilliseconds(5), aborted.Snapshot.TotalElapsed);
        Assert.Null(aborted.Snapshot.LastError);

        var tickAfterAbort = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());
        Assert.False(tickAfterAbort.IsSuccess);
        Assert.Equal(SequenceExecutionErrorCode.InvalidState, tickAfterAbort.Error!.Code);
        Assert.Equal(SequenceExecutionStatus.Aborted, tickAfterAbort.Snapshot.Status);
        Assert.False(executor.Start().IsSuccess);

        executor.Reset();
        Assert.Equal(SequenceExecutionStatus.Ready, executor.CaptureSnapshot().Status);
        Assert.True(executor.Start().IsSuccess);
    }

    [Fact]
    public void Tick_TransitionsAtMostOneStepAndResetAllowsRestart()
    {
        var definition = new SequenceDefinition
        {
            Id = "signal-cycle",
            Steps =
            {
                SequenceCompilerTests.Step("wait", SequenceStepAction.WaitSignal, "di.start", "true", "set"),
                SequenceCompilerTests.Step("set", SequenceStepAction.SetSignal, "do.active", "true", "complete"),
                SequenceCompilerTests.Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new FakeContext { Input = true };

        Assert.True(executor.Start().IsSuccess);
        var waitTick = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        Assert.Equal("set", waitTick.CurrentStepId);
        Assert.False(context.Output);

        var setTick = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        Assert.Equal("complete", setTick.CurrentStepId);
        Assert.True(context.Output);
        Assert.Equal(SequenceExecutionStatus.Running, setTick.Snapshot.Status);

        var completeTick = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        Assert.Equal(SequenceExecutionStatus.Completed, completeTick.Snapshot.Status);
        Assert.False(executor.Start().IsSuccess);

        executor.Reset();
        Assert.Equal(SequenceExecutionStatus.Ready, executor.CaptureSnapshot().Status);
        Assert.True(executor.Start().IsSuccess);
    }

    [Fact]
    public void WaitSignal_TimesOutOnInclusiveBoundary()
    {
        var definition = new SequenceDefinition
        {
            Id = "timeout",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.start",
                    Parameter = "true",
                    TimeoutMs = 10,
                    NextStepId = "complete"
                },
                SequenceCompilerTests.Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        executor.Start();

        var first = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());
        var boundary = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());

        Assert.True(first.IsSuccess);
        Assert.Equal(SequenceExecutionStatus.Running, first.Snapshot.Status);
        Assert.False(boundary.IsSuccess);
        Assert.Equal(SequenceExecutionStatus.Faulted, boundary.Snapshot.Status);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, boundary.Error!.Code);
    }

    [Fact]
    public void Watchdog_FaultsAtInclusiveBoundaryAndResetStartsFreshBudget()
    {
        var definition = new SequenceDefinition
        {
            Id = "watchdog",
            WatchdogTimeoutMs = 10,
            Steps =
            {
                SequenceCompilerTests.Step(
                    "wait",
                    SequenceStepAction.WaitSignal,
                    "di.start",
                    "true",
                    "complete"),
                SequenceCompilerTests.Step(
                    "complete",
                    SequenceStepAction.Complete,
                    string.Empty,
                    string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        executor.Start();

        var beforeBoundary = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());
        var boundary = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());

        Assert.Equal(SequenceExecutionStatus.Running, beforeBoundary.Snapshot.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(10), beforeBoundary.Snapshot.WatchdogTimeout);
        Assert.Equal(SequenceExecutionStatus.Faulted, boundary.Snapshot.Status);
        Assert.Equal(SequenceExecutionErrorCode.SequenceWatchdogTimedOut, boundary.Error!.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(10), boundary.Snapshot.TotalElapsed);

        executor.Reset();
        Assert.True(executor.Start().IsSuccess);
        var restarted = executor.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());

        Assert.Equal(SequenceExecutionStatus.Running, restarted.Snapshot.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(5), restarted.Snapshot.TotalElapsed);
    }

    [Fact]
    public void Retry_FaultedSequenceStartsAtEntryWithFreshExecutionEvidence()
    {
        var definition = new SequenceDefinition
        {
            Id = "retry",
            WatchdogTimeoutMs = 10,
            Steps =
            {
                SequenceCompilerTests.Step(
                    "wait",
                    SequenceStepAction.WaitSignal,
                    "di.start",
                    "true",
                    "complete"),
                SequenceCompilerTests.Step(
                    "complete",
                    SequenceStepAction.Complete,
                    string.Empty,
                    string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        Assert.True(executor.Start().IsSuccess);

        var faulted = executor.Tick(TimeSpan.FromMilliseconds(10), new FakeContext());
        Assert.Equal(SequenceExecutionStatus.Faulted, faulted.Snapshot.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(10), faulted.Snapshot.TotalElapsed);
        Assert.NotNull(faulted.Snapshot.LastError);

        var retried = executor.Retry();

        Assert.True(retried.IsSuccess);
        Assert.Equal(SequenceExecutionStatus.Running, retried.Snapshot.Status);
        Assert.Equal("wait", retried.Snapshot.CurrentStepId);
        Assert.Equal(0, retried.Snapshot.CurrentStepIndex);
        Assert.Equal(TimeSpan.Zero, retried.Snapshot.ElapsedInStep);
        Assert.Equal(TimeSpan.Zero, retried.Snapshot.TotalElapsed);
        Assert.Equal(0, retried.Snapshot.TickCount);
        Assert.Null(retried.Snapshot.LastError);

        var aborted = new DeterministicSequenceExecutor(compiled);
        Assert.True(aborted.Start().IsSuccess);
        Assert.True(aborted.Abort().IsSuccess);
        var abortedRetry = aborted.Retry();
        Assert.False(abortedRetry.IsSuccess);
        Assert.Equal(SequenceExecutionErrorCode.InvalidState, abortedRetry.Error!.Code);
        Assert.Equal(SequenceExecutionStatus.Aborted, abortedRetry.Snapshot.Status);
    }

    [Fact]
    public void Watchdog_ZeroAllowsWaitAndCompletionWinsAtBoundary()
    {
        var unlimitedDefinition = new SequenceDefinition
        {
            Id = "unlimited",
            Steps =
            {
                SequenceCompilerTests.Step(
                    "wait",
                    SequenceStepAction.WaitSignal,
                    "di.start",
                    "true",
                    "complete"),
                SequenceCompilerTests.Step(
                    "complete",
                    SequenceStepAction.Complete,
                    string.Empty,
                    string.Empty)
            }
        };
        var unlimited = new DeterministicSequenceExecutor(
            new SequenceCompiler().Compile(unlimitedDefinition, SequenceCompilerTests.Targets()).Sequence!);
        unlimited.Start();
        for (var index = 0; index < 100; index++)
        {
            Assert.Equal(
                SequenceExecutionStatus.Running,
                unlimited.Tick(TimeSpan.FromMilliseconds(5), new FakeContext()).Snapshot.Status);
        }

        var completingDefinition = new SequenceDefinition
        {
            Id = "completing",
            WatchdogTimeoutMs = 5,
            Steps =
            {
                SequenceCompilerTests.Step(
                    "complete",
                    SequenceStepAction.Complete,
                    string.Empty,
                    string.Empty)
            }
        };
        var completing = new DeterministicSequenceExecutor(
            new SequenceCompiler().Compile(completingDefinition).Sequence!);
        completing.Start();

        var completed = completing.Tick(TimeSpan.FromMilliseconds(5), new FakeContext());

        Assert.Equal(SequenceExecutionStatus.Completed, completed.Snapshot.Status);
        Assert.Null(completed.Error);
    }

    [Fact]
    public void LoopBackEdge_ConsumesOneTickPerIterationAndExitsWhenSignalChanges()
    {
        var definition = new SequenceDefinition
        {
            Id = "bounded-loop",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "loop",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.start",
                    Parameter = "true",
                    TimeoutMs = 5,
                    NextStepId = "complete",
                    ErrorStepId = "loop"
                },
                SequenceCompilerTests.Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new LoopContext { TrueOnRead = 3 };

        Assert.True(executor.Start().IsSuccess);
        var first = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        var second = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        var third = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        var completed = executor.Tick(TimeSpan.Zero, context);

        Assert.True(first.Transitioned);
        Assert.Equal("loop", first.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, first.Error!.Code);
        Assert.True(second.Transitioned);
        Assert.Equal("loop", second.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, second.Error!.Code);
        Assert.True(third.Transitioned);
        Assert.Equal("complete", third.CurrentStepId);
        Assert.Null(third.Error);
        Assert.Equal(SequenceExecutionStatus.Completed, completed.Snapshot.Status);
        Assert.Equal(4, completed.Snapshot.TickCount);
        Assert.Equal(TimeSpan.FromMilliseconds(15), completed.Snapshot.TotalElapsed);
        Assert.Equal(3, context.ReadCount);
    }

    [Fact]
    public void InfiniteLoop_StopsAtInclusiveWatchdogBudget()
    {
        var definition = new SequenceDefinition
        {
            Id = "infinite-loop",
            WatchdogTimeoutMs = 15,
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "loop",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.start",
                    Parameter = "true",
                    NextStepId = "loop"
                }
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new LoopContext { TrueOnRead = int.MaxValue };

        Assert.True(executor.Start().IsSuccess);
        var first = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        var second = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        var boundary = executor.Tick(TimeSpan.FromMilliseconds(5), context);

        Assert.Equal(SequenceExecutionStatus.Running, first.Snapshot.Status);
        Assert.Equal(SequenceExecutionStatus.Running, second.Snapshot.Status);
        Assert.Equal("loop", second.CurrentStepId);
        Assert.Equal(SequenceExecutionStatus.Faulted, boundary.Snapshot.Status);
        Assert.Equal(SequenceExecutionErrorCode.SequenceWatchdogTimedOut, boundary.Error!.Code);
        Assert.Equal(3, boundary.Snapshot.TickCount);
        Assert.Equal(TimeSpan.FromMilliseconds(15), boundary.Snapshot.TotalElapsed);
        Assert.Equal(3, context.ReadCount);
    }

    [Fact]
    public void WaitTimeout_UsesIndependentBeforeEqualAndAfterBoundaryResults()
    {
        var before = CreateWaitTimeoutExecutor(10);
        var beforeResult = before.Tick(TimeSpan.FromMilliseconds(9), new LoopContext());

        var equal = CreateWaitTimeoutExecutor(10);
        var equalResult = equal.Tick(TimeSpan.FromMilliseconds(10), new LoopContext());

        var after = CreateWaitTimeoutExecutor(10);
        var afterResult = after.Tick(TimeSpan.FromMilliseconds(11), new LoopContext());

        Assert.Equal(SequenceExecutionStatus.Running, beforeResult.Snapshot.Status);
        Assert.Null(beforeResult.Error);
        Assert.Equal(TimeSpan.FromMilliseconds(9), beforeResult.Snapshot.ElapsedInStep);
        Assert.Equal(SequenceExecutionStatus.Faulted, equalResult.Snapshot.Status);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, equalResult.Error!.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(10), equalResult.Snapshot.ElapsedInStep);
        Assert.Equal(SequenceExecutionStatus.Faulted, afterResult.Snapshot.Status);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, afterResult.Error!.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(11), afterResult.Snapshot.ElapsedInStep);
    }

    [Fact]
    public void WaitSignalAtTimeoutBoundaryWinsWhenConditionIsSatisfied()
    {
        var executor = CreateWaitTimeoutExecutor(10);
        var context = new LoopContext { TrueOnRead = 1 };

        var boundary = executor.Tick(TimeSpan.FromMilliseconds(10), context);

        Assert.Equal(SequenceExecutionStatus.Running, boundary.Snapshot.Status);
        Assert.Equal("complete", boundary.CurrentStepId);
        Assert.Null(boundary.Error);
        Assert.Equal(TimeSpan.FromMilliseconds(10), boundary.Snapshot.TotalElapsed);
    }

    [Fact]
    public void ZeroElapsedImmediateStepCompletesWithoutAdvancingSimulationTime()
    {
        var definition = new SequenceDefinition
        {
            Id = "zero-delay",
            Steps =
            {
                SequenceCompilerTests.Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        Assert.True(executor.Start().IsSuccess);

        var result = executor.Tick(TimeSpan.Zero, new LoopContext());

        Assert.Equal(SequenceExecutionStatus.Completed, result.Snapshot.Status);
        Assert.Equal(TimeSpan.Zero, result.Snapshot.TotalElapsed);
        Assert.Equal(1, result.Snapshot.TickCount);
        Assert.Null(result.Error);
    }

    private static DeterministicSequenceExecutor CreateWaitTimeoutExecutor(int timeoutMs)
    {
        var definition = new SequenceDefinition
        {
            Id = "wait-boundary",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.start",
                    Parameter = "true",
                    TimeoutMs = timeoutMs,
                    NextStepId = "complete"
                },
                SequenceCompilerTests.Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        Assert.True(executor.Start().IsSuccess);
        return executor;
    }

    [Fact]
    public void FailedMove_UsesDeclaredErrorRouteWithoutAdditionalTransition()
    {
        var definition = new SequenceDefinition
        {
            Id = "error-route",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "move",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "x",
                    Parameter = "100",
                    NextStepId = "success",
                    ErrorStepId = "error"
                },
                SequenceCompilerTests.Step("success", SequenceStepAction.Complete, string.Empty, string.Empty),
                SequenceCompilerTests.Step("error", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var compiled = new SequenceCompiler().Compile(definition, SequenceCompilerTests.Targets()).Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new FakeContext { RejectMove = true };
        executor.Start();

        var failedMove = executor.Tick(TimeSpan.FromMilliseconds(5), context);

        Assert.True(failedMove.Transitioned);
        Assert.Equal("error", failedMove.CurrentStepId);
        Assert.Equal(SequenceExecutionStatus.Running, failedMove.Snapshot.Status);
        Assert.Equal(SequenceExecutionErrorCode.AxisMoveFailed, failedMove.Error!.Code);
    }

    [Fact]
    public void CameraVision_PreservesAcquisitionCorrelationAndTransitionsOneStepPerTick()
    {
        var compiled = new SequenceCompiler()
            .Compile(SequenceCompilerTests.CreateCameraVisionCycle(), SequenceCompilerTests.CameraTargets())
            .Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new FakeContext { AcquisitionId = "cam1:0001" };
        context.VisionResults["cam1:0001"] = SequenceVisionResultState.Pending;
        context.VisionResults["stale-latest"] = SequenceVisionResultState.Passed;
        executor.Start();

        var trigger = executor.Tick(TimeSpan.FromMilliseconds(5), context);

        Assert.Equal("wait-vision", trigger.CurrentStepId);
        Assert.Equal(1, context.CameraTriggerCalls);
        Assert.Empty(context.ReadAcquisitionIds);

        var pending = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        Assert.Equal("wait-vision", pending.CurrentStepId);
        Assert.False(pending.Transitioned);
        Assert.Equal(new[] { "cam1:0001" }, context.ReadAcquisitionIds);

        context.VisionResults["cam1:0001"] = SequenceVisionResultState.Passed;
        var passed = executor.Tick(TimeSpan.FromMilliseconds(5), context);
        Assert.Equal("pass", passed.CurrentStepId);
        Assert.True(passed.Transitioned);
        Assert.Null(passed.Error);
        Assert.Null(passed.Snapshot.LastError);

        executor.Reset();
        context.AcquisitionId = "cam1:0002";
        context.VisionResults["cam1:0002"] = SequenceVisionResultState.Pending;
        Assert.True(executor.Start().IsSuccess);
        executor.Tick(TimeSpan.FromMilliseconds(5), context);
        executor.Tick(TimeSpan.FromMilliseconds(5), context);
        Assert.Equal("cam1:0002", context.ReadAcquisitionIds[^1]);
    }

    [Fact]
    public void WaitVisionResult_FailedJudgmentUsesNormalFailureTransition()
    {
        var compiled = new SequenceCompiler()
            .Compile(SequenceCompilerTests.CreateCameraVisionCycle(), SequenceCompilerTests.CameraTargets())
            .Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new FakeContext { AcquisitionId = "cam1:fail" };
        context.VisionResults["cam1:fail"] = SequenceVisionResultState.Failed;
        executor.Start();
        executor.Tick(TimeSpan.FromMilliseconds(5), context);

        var failed = executor.Tick(TimeSpan.FromMilliseconds(5), context);

        Assert.True(failed.IsSuccess);
        Assert.True(failed.Transitioned);
        Assert.Equal("fail", failed.CurrentStepId);
        Assert.Equal(SequenceExecutionStatus.Running, failed.Snapshot.Status);
        Assert.Null(failed.Error);
        Assert.Null(failed.Snapshot.LastError);
    }

    [Fact]
    public void WaitVisionResult_FaultAndInclusiveTimeoutUseTypedErrorRoute()
    {
        var compiled = new SequenceCompiler()
            .Compile(SequenceCompilerTests.CreateCameraVisionCycle(10), SequenceCompilerTests.CameraTargets())
            .Sequence!;

        var timeoutExecutor = new DeterministicSequenceExecutor(compiled);
        var timeoutContext = new FakeContext { AcquisitionId = "cam1:timeout" };
        timeoutContext.VisionResults["cam1:timeout"] = SequenceVisionResultState.Pending;
        timeoutExecutor.Start();
        timeoutExecutor.Tick(TimeSpan.FromMilliseconds(5), timeoutContext);
        var beforeBoundary = timeoutExecutor.Tick(TimeSpan.FromMilliseconds(5), timeoutContext);
        var timeout = timeoutExecutor.Tick(TimeSpan.FromMilliseconds(5), timeoutContext);

        Assert.False(beforeBoundary.Transitioned);
        Assert.Equal("camera-error", timeout.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.VisionResultTimedOut, timeout.Error!.Code);

        var faultExecutor = new DeterministicSequenceExecutor(compiled);
        var faultContext = new FakeContext { AcquisitionId = "cam1:fault" };
        faultContext.VisionResults["cam1:fault"] = SequenceVisionResultState.Faulted;
        faultExecutor.Start();
        faultExecutor.Tick(TimeSpan.FromMilliseconds(5), faultContext);
        var fault = faultExecutor.Tick(TimeSpan.FromMilliseconds(5), faultContext);

        Assert.Equal("camera-error", fault.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.VisionResultFaulted, fault.Error!.Code);

        var notTriggeredExecutor = new DeterministicSequenceExecutor(compiled);
        var notTriggeredContext = new FakeContext { AcquisitionId = "cam1:unknown" };
        notTriggeredExecutor.Start();
        notTriggeredExecutor.Tick(TimeSpan.FromMilliseconds(5), notTriggeredContext);
        var notTriggered = notTriggeredExecutor.Tick(TimeSpan.FromMilliseconds(5), notTriggeredContext);

        Assert.Equal("camera-error", notTriggered.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.VisionResultNotTriggered, notTriggered.Error!.Code);

        var boundaryExecutor = new DeterministicSequenceExecutor(compiled);
        var boundaryContext = new FakeContext { AcquisitionId = "cam1:boundary" };
        boundaryContext.VisionResults["cam1:boundary"] = SequenceVisionResultState.Pending;
        boundaryExecutor.Start();
        boundaryExecutor.Tick(TimeSpan.FromMilliseconds(5), boundaryContext);
        boundaryExecutor.Tick(TimeSpan.FromMilliseconds(5), boundaryContext);
        boundaryContext.VisionResults["cam1:boundary"] = SequenceVisionResultState.Passed;
        var boundaryPass = boundaryExecutor.Tick(TimeSpan.FromMilliseconds(5), boundaryContext);

        Assert.Equal("pass", boundaryPass.CurrentStepId);
        Assert.Null(boundaryPass.Error);
    }

    [Fact]
    public void TriggerCamera_RejectionUsesTypedErrorRouteWithoutReadingVisionResult()
    {
        var compiled = new SequenceCompiler()
            .Compile(SequenceCompilerTests.CreateCameraVisionCycle(), SequenceCompilerTests.CameraTargets())
            .Sequence!;
        var executor = new DeterministicSequenceExecutor(compiled);
        var context = new FakeContext { RejectCameraTrigger = true };
        executor.Start();

        var trigger = executor.Tick(TimeSpan.FromMilliseconds(5), context);

        Assert.Equal("camera-error", trigger.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.CameraTriggerFailed, trigger.Error!.Code);
        Assert.Empty(context.ReadAcquisitionIds);
    }

    private sealed class LoopContext : ISequenceRuntimeContext
    {
        public int TrueOnRead { get; init; } = int.MaxValue;
        public int ReadCount { get; private set; }

        public SequenceSignalReadResult ReadSignal(string signalId)
        {
            ReadCount++;
            return SequenceSignalReadResult.Success(ReadCount >= TrueOnRead);
        }

        public SequenceContextOperationResult SetSignal(string signalId, bool value) =>
            SequenceContextOperationResult.Success();

        public SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition) =>
            SequenceContextOperationResult.Success();

        public SequenceAxisMotionReadResult ReadAxisMotionState(string axisId) =>
            SequenceAxisMotionReadResult.Success(SequenceAxisMotionState.Completed);
    }

    private sealed class FakeContext : ISequenceRuntimeContext
    {
        public bool Input { get; init; }
        public bool Output { get; private set; }
        public bool RejectMove { get; init; }
        public bool RejectCameraTrigger { get; init; }
        public string AcquisitionId { get; set; } = "cam1:0001";
        public int CameraTriggerCalls { get; private set; }
        public List<string> ReadAcquisitionIds { get; } = new();
        public Dictionary<string, SequenceVisionResultState> VisionResults { get; } =
            new(StringComparer.Ordinal);

        public SequenceSignalReadResult ReadSignal(string signalId) =>
            SequenceSignalReadResult.Success(Input);

        public SequenceContextOperationResult SetSignal(string signalId, bool value)
        {
            Output = value;
            return SequenceContextOperationResult.Success();
        }

        public SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition) =>
            RejectMove
                ? SequenceContextOperationResult.Failure(SequenceContextErrorCode.Rejected, "rejected")
                : SequenceContextOperationResult.Success();

        public SequenceAxisMotionReadResult ReadAxisMotionState(string axisId) =>
            SequenceAxisMotionReadResult.Success(SequenceAxisMotionState.Completed);

        public SequenceCameraTriggerResult TriggerCamera(string cameraId, string recipeId)
        {
            CameraTriggerCalls++;
            return RejectCameraTrigger
                ? SequenceCameraTriggerResult.Failure(SequenceContextErrorCode.Rejected, "rejected")
                : SequenceCameraTriggerResult.Success(AcquisitionId);
        }

        public SequenceVisionResultReadResult ReadVisionResult(string cameraId, string acquisitionId)
        {
            ReadAcquisitionIds.Add(acquisitionId);
            return SequenceVisionResultReadResult.Success(
                VisionResults.TryGetValue(acquisitionId, out var state)
                    ? state
                    : SequenceVisionResultState.NotTriggered);
        }
    }
}
