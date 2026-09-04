using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SequenceStepNormalizationTests
{
    [Fact]
    public void NormalizeStep_PreservesActionSpecificDefaultsAndTargetFallback()
    {
        SequenceAuthoringTarget[] targets =
        [
            new("axis-1", "Axis", SequenceAuthoringTargetKind.Axis, "12.5"),
            new("camera-1", "Camera", SequenceAuthoringTargetKind.Camera),
            new("output-1", "Output", SequenceAuthoringTargetKind.DigitalOutput)
        ];
        var step = new SequenceStepDefinition
        {
            Action = SequenceStepAction.MoveAxis,
            TargetId = "missing",
            Parameter = "NaN",
            TimeoutMs = 250,
            FailureStepId = "failed"
        };

        SequenceDefinitionEditor.NormalizeStep(step, new SequenceStepTemplateCatalog(), targets);

        Assert.Equal("axis-1", step.TargetId);
        Assert.Equal("12.5", step.Parameter);
        Assert.Equal(0, step.TimeoutMs);
        Assert.Null(step.FailureStepId);

        step.Action = SequenceStepAction.WaitVisionResult;
        step.TargetId = "missing";
        step.Parameter = "stale";
        step.TimeoutMs = 0;
        step.FailureStepId = "failed";

        SequenceDefinitionEditor.NormalizeStep(step, new SequenceStepTemplateCatalog(), targets);

        Assert.Equal("camera-1", step.TargetId);
        Assert.Equal(string.Empty, step.Parameter);
        Assert.Equal(1000, step.TimeoutMs);
        Assert.Equal("failed", step.FailureStepId);

        step.Action = SequenceStepAction.Complete;
        step.TargetId = "camera-1";
        step.Parameter = "stale";
        step.TimeoutMs = 10;
        step.NextStepId = "next";
        step.ErrorStepId = "error";
        step.FailureStepId = "failed";

        SequenceDefinitionEditor.NormalizeStep(step, new SequenceStepTemplateCatalog(), targets);

        Assert.Equal(string.Empty, step.TargetId);
        Assert.Equal(string.Empty, step.Parameter);
        Assert.Equal(0, step.TimeoutMs);
        Assert.Null(step.NextStepId);
        Assert.Null(step.ErrorStepId);
        Assert.Null(step.FailureStepId);
    }

    [Fact]
    public void NormalizeStep_UsesParameterChoiceDefaultsAndClearsUnsupportedFields()
    {
        SequenceAuthoringTarget[] targets =
        [
            new("output-1", "Output", SequenceAuthoringTargetKind.DigitalOutput),
            new("child-1", "Child", SequenceAuthoringTargetKind.Subsequence)
        ];
        var step = new SequenceStepDefinition
        {
            Action = SequenceStepAction.SetSignal,
            TargetId = "missing",
            Parameter = "invalid",
            TimeoutMs = 50,
            FailureStepId = "failed"
        };

        SequenceDefinitionEditor.NormalizeStep(step, new SequenceStepTemplateCatalog(), targets);

        Assert.Equal("output-1", step.TargetId);
        Assert.Equal("true", step.Parameter);
        Assert.Equal(0, step.TimeoutMs);
        Assert.Null(step.FailureStepId);

        step.Action = SequenceStepAction.CallSubsequence;
        step.TargetId = "missing";
        step.Parameter = "stale";
        step.TimeoutMs = 50;
        step.FailureStepId = "failed";

        SequenceDefinitionEditor.NormalizeStep(step, new SequenceStepTemplateCatalog(), targets);

        Assert.Equal("child-1", step.TargetId);
        Assert.Equal(string.Empty, step.Parameter);
        Assert.Equal(0, step.TimeoutMs);
        Assert.Null(step.FailureStepId);
    }
}
