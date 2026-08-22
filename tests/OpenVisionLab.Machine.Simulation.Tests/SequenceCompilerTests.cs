using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SequenceCompilerTests
{
    [Fact]
    public void Compile_InspectionCycle_ProducesFiveTypedStepKinds()
    {
        var compiler = new SequenceCompiler();
        var result = compiler.Compile(CreateInspectionCycle(), Targets());

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Sequence!.Steps,
            step => Assert.IsType<WaitSignalStep>(step),
            step => Assert.IsType<SetSignalStep>(step),
            step => Assert.IsType<MoveAxisStep>(step),
            step => Assert.IsType<WaitAxisDoneStep>(step),
            step => Assert.IsType<CompleteStep>(step));
        Assert.Equal(100, Assert.IsType<MoveAxisStep>(result.Sequence.Steps[2]).TargetPosition);
        Assert.Equal(
            TimeSpan.FromMilliseconds(2000),
            Assert.IsType<WaitAxisDoneStep>(result.Sequence.Steps[3]).Timeout);
    }

    [Fact]
    public void Compile_SetSignalToInputOrUnknownAxis_IsRejected()
    {
        var inputWrite = new SequenceDefinition
        {
            Id = "invalid-input-write",
            Steps =
            {
                Step("set-input", SequenceStepAction.SetSignal, "di.start", "true", "complete"),
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var unknownAxis = new SequenceDefinition
        {
            Id = "invalid-axis",
            Steps =
            {
                Step("move", SequenceStepAction.MoveAxis, "missing", "1", "complete"),
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };

        var compiler = new SequenceCompiler();
        var inputResult = compiler.Compile(inputWrite, Targets());
        var axisResult = compiler.Compile(unknownAxis, Targets());

        Assert.Contains(inputResult.Errors, error => error.Code == SequenceCompilationErrorCode.InvalidSignalKind);
        Assert.Contains(axisResult.Errors, error => error.Code == SequenceCompilationErrorCode.UnknownAxis);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Compile_LegacyWaitBoolean_RemainsCompatible(string parameter, bool expected)
    {
        var definition = new SequenceDefinition
        {
            Id = "legacy",
            Steps =
            {
                Step("wait", SequenceStepAction.Wait, "di.start", parameter, "complete"),
                Step("complete", SequenceStepAction.None, string.Empty, string.Empty)
            }
        };

        var result = new SequenceCompiler().Compile(definition, Targets());

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, Assert.IsType<WaitSignalStep>(result.Sequence!.Steps[0]).ExpectedValue);
    }

    [Fact]
    public void Compile_DuplicateStepAndNegativeTimeout_AreRejected()
    {
        var definition = new SequenceDefinition
        {
            Id = "invalid",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.start",
                    Parameter = "true",
                    TimeoutMs = -1,
                    NextStepId = "wait"
                },
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.Complete
                }
            }
        };

        var result = new SequenceCompiler().Compile(definition, Targets());

        Assert.Contains(result.Errors, error => error.Code == SequenceCompilationErrorCode.DuplicateStepId);
        Assert.Contains(result.Errors, error => error.Code == SequenceCompilationErrorCode.InvalidTimeout);
    }

    [Theory]
    [InlineData("x", null, SequenceCompilationErrorCode.ExpectedStateRequired)]
    [InlineData(null, "Idle", SequenceCompilationErrorCode.ExpectedTargetIdRequired)]
    public void Compile_IncompleteExpectedStateCheckpoint_IsRejected(
        string? expectedTargetId,
        string? expectedState,
        SequenceCompilationErrorCode expectedError)
    {
        var definition = CreateInspectionCycle();
        definition.Steps[0].ExpectedTargetId = expectedTargetId;
        definition.Steps[0].ExpectedState = expectedState;

        var result = new SequenceCompiler().Compile(definition, Targets());

        Assert.Contains(result.Errors, error => error.Code == expectedError);
    }

    [Fact]
    public void Compile_CameraVisionFlow_ProducesTypedRecipeTimeoutAndFailureRoute()
    {
        var result = new SequenceCompiler().Compile(CreateCameraVisionCycle(), CameraTargets());

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Sequence!.Steps,
            step =>
            {
                var trigger = Assert.IsType<TriggerCameraStep>(step);
                Assert.Equal("cam1", trigger.CameraId);
                Assert.Equal("presence-check", trigger.RecipeId);
                Assert.Equal("wait-vision", trigger.NextStepId);
                Assert.Equal("camera-error", trigger.ErrorStepId);
            },
            step =>
            {
                var wait = Assert.IsType<WaitVisionResultStep>(step);
                Assert.Equal("cam1", wait.CameraId);
                Assert.Equal("pass", wait.NextStepId);
                Assert.Equal("fail", wait.FailureStepId);
                Assert.Equal("camera-error", wait.ErrorStepId);
                Assert.Equal(TimeSpan.FromMilliseconds(50), wait.Timeout);
            },
            step => Assert.IsType<CompleteStep>(step),
            step => Assert.IsType<CompleteStep>(step),
            step => Assert.IsType<CompleteStep>(step));
    }

    [Fact]
    public void Compile_InvalidCameraVisionContracts_ReturnTypedErrors()
    {
        var invalidTrigger = new SequenceDefinition
        {
            Id = "invalid-trigger",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "trigger",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = "missing-camera",
                    Parameter = " ",
                    TimeoutMs = 1,
                    NextStepId = "complete"
                },
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var invalidWait = new SequenceDefinition
        {
            Id = "invalid-wait",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = "cam1",
                    Parameter = "Pass",
                    TimeoutMs = 0,
                    NextStepId = "complete",
                    FailureStepId = "missing-failure"
                },
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };
        var missingFailure = new SequenceDefinition
        {
            Id = "missing-failure",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = "cam1",
                    TimeoutMs = 10,
                    NextStepId = "complete"
                },
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };

        var compiler = new SequenceCompiler();
        var triggerResult = compiler.Compile(invalidTrigger, CameraTargets());
        var waitResult = compiler.Compile(invalidWait, CameraTargets());
        var missingFailureResult = compiler.Compile(missingFailure, CameraTargets());

        Assert.Contains(triggerResult.Errors, error => error.Code == SequenceCompilationErrorCode.UnknownCamera);
        Assert.Contains(triggerResult.Errors, error => error.Code == SequenceCompilationErrorCode.RecipeIdRequired);
        Assert.Contains(triggerResult.Errors, error => error.Code == SequenceCompilationErrorCode.InvalidTimeout);
        Assert.Contains(waitResult.Errors, error => error.Code == SequenceCompilationErrorCode.UnexpectedParameter);
        Assert.Contains(waitResult.Errors, error => error.Code == SequenceCompilationErrorCode.InvalidTimeout);
        Assert.Contains(waitResult.Errors, error => error.Code == SequenceCompilationErrorCode.FailureStepNotFound);
        Assert.Contains(missingFailureResult.Errors, error => error.Code == SequenceCompilationErrorCode.FailureStepRequired);
    }

    internal static SequenceDefinition CreateInspectionCycle() =>
        new()
        {
            Id = "inspection-cycle",
            Name = "Inspection Cycle",
            Steps =
            {
                Step("wait", SequenceStepAction.WaitSignal, "di.start", "true", "active"),
                Step("active", SequenceStepAction.SetSignal, "do.active", "true", "move"),
                Step("move", SequenceStepAction.MoveAxis, "x", "100", "wait-axis"),
                new SequenceStepDefinition
                {
                    Id = "wait-axis",
                    Name = "Wait Axis",
                    Action = SequenceStepAction.WaitAxisDone,
                    TargetId = "x",
                    TimeoutMs = 2000,
                    NextStepId = "complete"
                },
                Step("complete", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };

    internal static SequenceDefinition CreateCameraVisionCycle(int timeoutMs = 50) =>
        new()
        {
            Id = "camera-cycle",
            Name = "Camera Cycle",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "trigger",
                    Name = "Trigger Camera",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = "cam1",
                    Parameter = "presence-check",
                    NextStepId = "wait-vision",
                    ErrorStepId = "camera-error"
                },
                new SequenceStepDefinition
                {
                    Id = "wait-vision",
                    Name = "Wait Vision Result",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = "cam1",
                    TimeoutMs = timeoutMs,
                    NextStepId = "pass",
                    FailureStepId = "fail",
                    ErrorStepId = "camera-error"
                },
                Step("pass", SequenceStepAction.Complete, string.Empty, string.Empty),
                Step("fail", SequenceStepAction.Complete, string.Empty, string.Empty),
                Step("camera-error", SequenceStepAction.Complete, string.Empty, string.Empty)
            }
        };

    internal static SequenceCompilationTargets Targets() =>
        new(
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
            {
                ["di.start"] = ChannelKind.DigitalInput,
                ["do.active"] = ChannelKind.DigitalOutput
            },
            new[] { "x" });

    internal static SequenceCompilationTargets CameraTargets() =>
        new(
            new Dictionary<string, ChannelKind>(StringComparer.Ordinal),
            Array.Empty<string>(),
            new[] { "cam1" });

    internal static SequenceStepDefinition Step(
        string id,
        SequenceStepAction action,
        string target,
        string parameter,
        string? next = null) =>
        new()
        {
            Id = id,
            Name = id,
            Action = action,
            TargetId = target,
            Parameter = parameter,
            NextStepId = next
        };
}
