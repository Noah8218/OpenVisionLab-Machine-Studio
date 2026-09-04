using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SubsequenceCompositionTests
{
    [Fact]
    public void Compiler_ProducesTypedCallAndRejectsInvalidCallFields()
    {
        var definition = ParentDefinition();
        var result = new SequenceCompiler().Compile(definition, Targets("parent", "child"));

        Assert.True(result.IsSuccess);
        var call = Assert.IsType<CallSubsequenceStep>(result.Sequence!.Steps[0]);
        Assert.Equal("child", call.SequenceId);
        Assert.Equal("complete", call.NextStepId);

        definition.Steps[0].Parameter = "unexpected";
        definition.Steps[0].TimeoutMs = 5;
        var invalid = new SequenceCompiler().Compile(definition, Targets("parent", "child"));

        Assert.Contains(invalid.Errors, error => error.Code == SequenceCompilationErrorCode.UnexpectedParameter);
        Assert.Contains(invalid.Errors, error => error.Code == SequenceCompilationErrorCode.InvalidTimeout);
    }

    [Fact]
    public void Compiler_RejectsMissingAndCyclicSubsequences()
    {
        var missing = ParentDefinition("missing");
        var missingResult = new SequenceCompiler().Compile(missing, Targets("parent"));

        Assert.Contains(missingResult.Errors, error => error.Code == SequenceCompilationErrorCode.UnknownSubsequence);

        var first = Compile(ParentDefinition("second"), "first", "first", "second");
        var second = Compile(ParentDefinition("first"), "second", "first", "second");
        var compositionErrors = SequenceCompiler.ValidateComposition([first, second]);

        var cycle = Assert.Single(compositionErrors, error =>
            error.Code == SequenceCompilationErrorCode.SubsequenceCycle);
        Assert.Contains("first -> second -> first", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Executor_EntersChildAndReturnsToCallerAtOneTransitionPerTick()
    {
        var child = Compile(ChildDefinition(), "child", "parent", "child");
        var parent = Compile(ParentDefinition("child"), "parent", "parent", "child");
        var executor = new DeterministicSequenceExecutor(
            parent,
            new Dictionary<string, CompiledSequence>(StringComparer.Ordinal)
            {
                [parent.Id] = parent,
                [child.Id] = child
            });

        Assert.True(executor.Start().IsSuccess);
        var entered = executor.Tick(TimeSpan.FromMilliseconds(5), new TestContext());
        Assert.True(entered.IsSuccess);
        Assert.True(entered.Transitioned);
        Assert.Equal("child-entry", entered.Snapshot.CurrentStepId);
        Assert.Equal("child", entered.Snapshot.ActiveSequenceId);
        Assert.Equal(new[] { "parent", "child" }, entered.Snapshot.CallStack);
        Assert.Equal("parent", entered.PreviousSequenceId);
        Assert.Equal("child", entered.CurrentSequenceId);

        var returned = executor.Tick(TimeSpan.FromMilliseconds(5), new TestContext());
        Assert.True(returned.IsSuccess);
        Assert.Equal(SequenceExecutionStatus.Running, returned.Snapshot.Status);
        Assert.Equal("complete", returned.Snapshot.CurrentStepId);
        Assert.Null(returned.Snapshot.ActiveSequenceId);
        Assert.Null(returned.Snapshot.CallStack);
        Assert.Equal("child", returned.PreviousSequenceId);
        Assert.Equal("parent", returned.CurrentSequenceId);

        var completed = executor.Tick(TimeSpan.FromMilliseconds(5), new TestContext());
        Assert.Equal(SequenceExecutionStatus.Completed, completed.Snapshot.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(15), completed.Snapshot.TotalElapsed);
    }

    [Fact]
    public void Executor_RoutesUnhandledChildFailureThroughCallerErrorStep()
    {
        var child = Compile(ChildTimeoutDefinition(), "child", "parent", "child");
        var parent = Compile(ParentWithErrorDefinition(), "parent", "parent", "child");
        var executor = new DeterministicSequenceExecutor(
            parent,
            new Dictionary<string, CompiledSequence>(StringComparer.Ordinal)
            {
                [parent.Id] = parent,
                [child.Id] = child
            });
        var context = new TestContext();

        executor.Start();
        executor.Tick(TimeSpan.FromMilliseconds(5), context);
        var routed = executor.Tick(TimeSpan.FromMilliseconds(5), context);

        Assert.False(routed.IsSuccess);
        Assert.True(routed.Transitioned);
        Assert.Equal(SequenceExecutionStatus.Running, routed.Snapshot.Status);
        Assert.Equal("handled", routed.Snapshot.CurrentStepId);
        Assert.Equal(SequenceExecutionErrorCode.StepTimedOut, routed.Error!.Code);
        Assert.Equal("child", routed.Error.SequenceId);
        Assert.Null(routed.Snapshot.ActiveSequenceId);
    }

    [Fact]
    public void Executor_RetryClearsNestedFaultBoundaryAndStartsRootFresh()
    {
        var child = Compile(ChildTimeoutDefinition(), "child", "parent", "child");
        var parent = Compile(ParentDefinition("child"), "parent", "parent", "child");
        var executor = new DeterministicSequenceExecutor(
            parent,
            new Dictionary<string, CompiledSequence>(StringComparer.Ordinal)
            {
                [parent.Id] = parent,
                [child.Id] = child
            });

        executor.Start();
        executor.Tick(TimeSpan.FromMilliseconds(5), new TestContext());
        var faulted = executor.Tick(TimeSpan.FromMilliseconds(5), new TestContext());
        Assert.Equal(SequenceExecutionStatus.Faulted, faulted.Snapshot.Status);
        Assert.Equal("child", faulted.Error!.SequenceId);
        Assert.Equal(new[] { "parent", "child" }, faulted.Snapshot.CallStack);

        var retried = executor.Retry();
        Assert.True(retried.IsSuccess);
        Assert.Equal(SequenceExecutionStatus.Running, retried.Snapshot.Status);
        Assert.Equal("call", retried.Snapshot.CurrentStepId);
        Assert.Equal(TimeSpan.Zero, retried.Snapshot.TotalElapsed);
        Assert.Equal(0, retried.Snapshot.TickCount);
        Assert.Null(retried.Snapshot.CallStack);
        Assert.Null(retried.Snapshot.LastError);
    }

    [Fact]
    public async Task Engine_UsesOneRootExecutorCatalogAndPublishesNestedPath()
    {
        var child = Compile(ChildDefinition(), "child", "parent", "child");
        var parent = Compile(ParentDefinition("child"), "parent", "parent", "child");
        using var engine = new FixedStepSimulationEngine(new SimulationSettings());
        await engine.StartAsync();

        var configured = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            new SimulationRuntimeConfiguration(
                Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                Array.Empty<ChannelDefinition>(),
                new[] { parent, child })));
        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("parent"))).IsAccepted);

        await engine.EnqueueCommandAsync(new StepCommand());
        var nested = engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == "parent");
        Assert.Equal("child-entry", nested.CurrentStepId);
        Assert.Equal("child", nested.ActiveSequenceId);
        Assert.Equal(new[] { "parent", "child" }, nested.CallStack);

        await engine.EnqueueCommandAsync(new StepCommand());
        var resumed = engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == "parent");
        Assert.Equal("complete", resumed.CurrentStepId);
        Assert.Null(resumed.ActiveSequenceId);

        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal(
            SequenceExecutionStatus.Completed,
            engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == "parent").Status);

        await engine.StopAsync();
        var events = new List<SimulationEvent>();
        await foreach (var item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }

        Assert.Contains(events, item => item.Code == "SequenceCompleted");
    }

    [Fact]
    public async Task Engine_BreakpointCanPauseAtAnActiveChildStep()
    {
        var child = Compile(ChildDefinition(), "child", "parent", "child");
        var parent = Compile(ParentDefinition("child"), "parent", "parent", "child");
        using var engine = new FixedStepSimulationEngine(new SimulationSettings());
        await engine.StartAsync();

        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(
            new SimulationRuntimeConfiguration(
                Array.Empty<OpenVisionLab.Machine.Simulation.Axis.AxisConfiguration>(),
                Array.Empty<ChannelDefinition>(),
                new[] { parent, child })))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("parent"))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new SetSequenceBreakpointCommand(
            "child",
            "child-entry",
            true))).IsAccepted);

        Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);

        Assert.Equal(SimulationRunMode.Paused, engine.CurrentSnapshot.RunMode);
        Assert.Equal(SequenceDebugPauseReason.Breakpoint, engine.CurrentSnapshot.SequenceDebug.PauseReason);
        Assert.Equal("child-entry", engine.CurrentSnapshot.SequenceDebug.PausedStepId);
        Assert.Equal(
            "child",
            engine.CurrentSnapshot.Sequences.Single(sequence => sequence.SequenceId == "parent").ActiveSequenceId);

        await engine.StopAsync();
    }

    private static SequenceDefinition ParentDefinition(string childId = "child") =>
        new()
        {
            Id = "parent",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "call",
                    Action = SequenceStepAction.CallSubsequence,
                    TargetId = childId,
                    NextStepId = "complete"
                },
                Complete("complete")
            }
        };

    private static SequenceDefinition ParentWithErrorDefinition() =>
        new()
        {
            Id = "parent",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "call",
                    Action = SequenceStepAction.CallSubsequence,
                    TargetId = "child",
                    NextStepId = "complete",
                    ErrorStepId = "handled"
                },
                Complete("complete"),
                Complete("handled")
            }
        };

    private static SequenceDefinition ChildDefinition() =>
        new()
        {
            Id = "child",
            Steps =
            {
                Complete("child-entry")
            }
        };

    private static SequenceDefinition ChildTimeoutDefinition() =>
        new()
        {
            Id = "child",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.never",
                    Parameter = "true",
                    TimeoutMs = 5,
                    NextStepId = "complete"
                },
                Complete("complete")
            }
        };

    private static CompiledSequence Compile(
        SequenceDefinition definition,
        string expectedId,
        params string[] sequenceIds)
    {
        definition.Id = expectedId;
        var result = new SequenceCompiler().Compile(definition, Targets(sequenceIds));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => error.Message)));
        return result.Sequence!;
    }

    private static SequenceCompilationTargets Targets(params string[] sequenceIds) =>
        new(
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
            {
                ["di.never"] = ChannelKind.DigitalInput
            },
            Array.Empty<string>(),
            Array.Empty<string>(),
            sequenceIds);

    private static SequenceStepDefinition Complete(string id) =>
        new()
        {
            Id = id,
            Action = SequenceStepAction.Complete
        };

    private sealed class TestContext : ISequenceRuntimeContext
    {
        public SequenceContextError? ReadError { get; init; }

        public bool SignalValue { get; init; }

        public SequenceSignalReadResult ReadSignal(string signalId) => ReadError is null
            ? SequenceSignalReadResult.Success(SignalValue)
            : SequenceSignalReadResult.Failure(ReadError.Code, ReadError.Message);

        public SequenceContextOperationResult SetSignal(string signalId, bool value) =>
            SequenceContextOperationResult.Success();

        public SequenceContextOperationResult RequestAxisMove(string axisId, double targetPosition) =>
            SequenceContextOperationResult.Success();

        public SequenceAxisMotionReadResult ReadAxisMotionState(string axisId) =>
            SequenceAxisMotionReadResult.Success(SequenceAxisMotionState.Completed);
    }
}
